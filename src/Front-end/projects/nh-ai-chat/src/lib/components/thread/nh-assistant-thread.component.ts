import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalDecision, ApprovalPart, ConversationMember, Message } from '../../models/assistant-api.models';
import { NhAssistantMarkdownPipe } from '../../internal/nh-assistant-markdown.pipe';
import { NhAssistantPartGroupsPipe } from '../../internal/nh-assistant-part-groups';
import { NhAssistantApprovalCardComponent } from '../approval-card/nh-assistant-approval-card.component';
import { NhAssistantToolCallGroupComponent } from '../tool-call-group/nh-assistant-tool-call-group.component';
import type { NhAssistantApprovalRight } from '../../services/nh-assistant.store';

/** A decision about one approval of the thread. */
export interface NhAssistantApprovalChoice {
  approvalId: string;
  decision: ApprovalDecision;
}

/** Above this number of messages the thread renders through CDK virtual scrolling. */
export const NH_ASSISTANT_VIRTUAL_SCROLL_THRESHOLD = 200;

/** Distance from the bottom, in pixels, within which new content keeps the thread pinned to the end. */
const stickToBottomDistance = 120;

/**
 * The messages of a conversation as a live log. Assistant text renders as sanitized
 * Markdown, consecutive tool calls as one collapsible group that counts along, and
 * approvals as cards. Messages a trigger wrote into an agent thread read as events, not as
 * a person. While a turn runs without visible progress, a working indicator shows the
 * elapsed time. The thread follows new content while the user is at the bottom.
 */
@Component({
  selector: 'nh-assistant-thread',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    ScrollingModule,
    TranslatePipe,
    NhAssistantMarkdownPipe,
    NhAssistantPartGroupsPipe,
    NhAssistantToolCallGroupComponent,
    NhAssistantApprovalCardComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-thread.component.html',
  styleUrl: './nh-assistant-thread.component.scss'
})
export class NhAssistantThreadComponent {
  private readonly injector = inject(Injector);

  readonly messages = input.required<readonly Message[]>();
  readonly streaming = input(false);
  readonly deciding = input(false);
  readonly markdown = input(true);
  /** The owner and participants of a shared conversation; empty while it is not shared. */
  readonly members = input<readonly ConversationMember[]>([]);
  /** The current user, to tell their own messages from those of others. */
  readonly currentActorId = input<string | null>(null);
  /** False when the pending approval belongs to another participant's turn. */
  readonly canDecide = input(true);
  /** Name of the participant who decides the pending approval, when it is someone else. */
  readonly decisionOwner = input<string | null>(null);
  /**
   * Who may decide each pending approval, by approval id, for example `NhAssistantStore.approvalRights`.
   * An agent thread can hold several open proposals that the subject's approvers decide. Approvals
   * without an entry use `canDecide` and `decisionOwner`. An entry with `canGiveFeedback` lets the
   * card of a rejected proposal ask what was wrong.
   */
  readonly approvalRights = input<ReadonlyMap<string, NhAssistantApprovalRight> | null>(null);
  /** The decision about an approval card, without the approval it belongs to. */
  readonly approvalDecision = output<ApprovalDecision>();
  /** The decision about an approval card together with its approval id. */
  readonly approvalChoice = output<NhAssistantApprovalChoice>();

  private readonly names = computed(() => new Map(this.members().map(member => [member.actorId, member.displayName])));

  readonly virtualThreshold = NH_ASSISTANT_VIRTUAL_SCROLL_THRESHOLD;
  readonly virtual = computed(() => this.messages().length > NH_ASSISTANT_VIRTUAL_SCROLL_THRESHOLD);
  /**
   * True while the turn runs and nothing else shows progress: before the first part and
   * between tool calls while the model decides what to do next. Streaming text and a
   * running tool group show their own progress; an approval waits for the user.
   */
  readonly working = computed(() => {
    if (!this.streaming()) {
      return false;
    }

    const last = this.messages()[this.messages().length - 1];
    if (!last || last.role === 'user' || last.parts.length === 0) {
      return true;
    }

    const lastPart = last.parts[last.parts.length - 1];
    return lastPart.type === 'tool-call'
      && lastPart.status !== 'running'
      && lastPart.status !== 'awaiting-approval';
  });
  /** Whole seconds since the running turn started. */
  readonly elapsedSeconds = computed(() => {
    const startedAt = this.turnStartedAt();
    return startedAt === null ? 0 : Math.max(0, Math.floor((this.now() - startedAt) / 1000));
  });

  private readonly turnStartedAt = signal<number | null>(null);
  private readonly now = signal(Date.now());

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly viewport = viewChild(CdkVirtualScrollViewport);

  constructor() {
    effect(onCleanup => {
      if (!this.streaming()) {
        this.turnStartedAt.set(null);
        return;
      }

      untracked(() => {
        const now = Date.now();
        this.now.set(now);
        if (this.turnStartedAt() === null) {
          this.turnStartedAt.set(now);
        }
      });
      const timer = setInterval(() => this.now.set(Date.now()), 1000);
      onCleanup(() => clearInterval(timer));
    });

    effect(() => {
      this.messages();
      this.working();
      const element = this.scrollElement();
      const pinned = !element || element.scrollHeight - element.scrollTop - element.clientHeight <= stickToBottomDistance;
      if (pinned) {
        afterNextRender(() => this.scrollToEnd(), { injector: this.injector });
      }
    });
  }

  trackMessage(_index: number, message: Message): string {
    return message.id;
  }

  /** True for a user message a trigger wrote into an agent thread. */
  isTrigger(message: Message): boolean {
    return message.role === 'user' && message.authorKind === 'trigger';
  }

  /** True for an assistant message the agent's rules wrote without a model. */
  isRule(message: Message): boolean {
    return message.role === 'assistant' && message.authorKind === 'rule';
  }

  /** True for a user message written by another person in a shared conversation. */
  isOthers(message: Message): boolean {
    return message.role === 'user'
      && !this.isTrigger(message)
      && !!message.authorActorId
      && !!this.currentActorId()
      && message.authorActorId !== this.currentActorId();
  }

  /** Whether and why the current user may decide an approval card. */
  rightFor(approval: ApprovalPart): NhAssistantApprovalRight {
    return this.approvalRights()?.get(approval.approvalId) ?? {
      canDecide: this.canDecide(),
      waitingFor: 'turn',
      decisionOwner: this.decisionOwner()
    };
  }

  choose(approval: ApprovalPart, decision: ApprovalDecision): void {
    this.approvalDecision.emit(decision);
    this.approvalChoice.emit({ approvalId: approval.approvalId, decision });
  }

  /** Display name of another person's message, or `null` when the application provides none. */
  authorName(message: Message): string | null {
    return message.authorActorId ? this.names().get(message.authorActorId) ?? null : null;
  }

  private scrollElement(): HTMLElement | null {
    return this.viewport()?.elementRef.nativeElement ?? this.scroller()?.nativeElement ?? null;
  }

  private scrollToEnd(): void {
    const viewport = this.viewport();
    if (viewport) {
      viewport.scrollToIndex(this.messages().length - 1);
      viewport.scrollTo({ bottom: 0 });
      return;
    }

    const element = this.scroller()?.nativeElement;
    if (element) {
      element.scrollTop = element.scrollHeight;
    }
  }
}
