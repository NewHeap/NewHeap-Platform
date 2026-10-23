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
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { ApprovalDecision, ApprovalPart, ConversationMember, FilePart, Message } from '../../models/assistant-api.models';
import { nhAssistantFileTypeLabel, nhAssistantFormatFileSize } from '../../internal/nh-assistant-files';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantMarkdownPipe } from '../../internal/nh-assistant-markdown.pipe';
import { NhAssistantMessageLinkComponent } from '../../internal/nh-assistant-message-link.component';
import { NhAssistantPartGroupsPipe } from '../../internal/nh-assistant-part-groups';
import { NhAssistantApprovalCardComponent } from '../approval-card/nh-assistant-approval-card.component';
import { NhAssistantConnectionRequiredComponent } from '../connection-required/nh-assistant-connection-required.component';
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
 * approvals as cards. The files a person attached show by name, type and size. Messages a
 * trigger wrote into an agent thread read as events, not as a person, and a message the application
 * posted reads as the application's, with its sender, title, Markdown text and link, and a label when
 * the assistant follows it as an instruction. While a turn runs without visible progress, a working
 * indicator shows the elapsed time, directly under the answer the turn writes, also when the
 * application's or another person's message arrived after it. The thread follows new content while
 * the user is at the bottom.
 */
@Component({
  selector: 'nh-assistant-thread',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    ScrollingModule,
    TranslatePipe,
    NhAssistantIconComponent,
    NhAssistantMarkdownPipe,
    NhAssistantMessageLinkComponent,
    NhAssistantPartGroupsPipe,
    NhAssistantToolCallGroupComponent,
    NhAssistantApprovalCardComponent,
    NhAssistantConnectionRequiredComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-thread.component.html',
  styleUrl: './nh-assistant-thread.component.scss'
})
export class NhAssistantThreadComponent {
  private readonly injector = inject(Injector);
  private readonly translate = inject(TranslateService, { optional: true });

  /** The type and size of an attached file, in the current language. */
  fileMeta(file: FilePart): { type: string; size: string } {
    return {
      type: nhAssistantFileTypeLabel(file.mediaType),
      size: nhAssistantFormatFileSize(file.size, this.translate?.getCurrentLang())
    };
  }

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
  /**
   * The question to ask again after the viewer connected an MCP server from a connection card: the
   * text of the user message the card answered. The panel sends it.
   */
  readonly retryMessage = output<string>();

  private readonly names = computed(() => new Map(this.members().map(member => [member.actorId, member.displayName])));

  /** Asks the question again that the assistant message with a connection card answered. */
  retryAfterConnect(message: Message): void {
    const messages = this.messages();
    const index = messages.findIndex(item => item.id === message.id);
    for (let position = index - 1; position >= 0; position--) {
      const candidate = messages[position];
      if (candidate.role !== 'user') {
        continue;
      }
      const text = candidate.parts.map(part => part.type === 'text' ? part.text : '').join('').trim();
      if (text.length > 0) {
        this.retryMessage.emit(text);
      }
      return;
    }
  }

  readonly virtualThreshold = NH_ASSISTANT_VIRTUAL_SCROLL_THRESHOLD;
  readonly virtual = computed(() => this.messages().length > NH_ASSISTANT_VIRTUAL_SCROLL_THRESHOLD);
  /**
   * True while the turn runs and nothing else shows progress: before the first part and
   * between tool calls while the model decides what to do next. Streaming text and a
   * running tool group show their own progress; an approval waits for the user. When messages
   * arrived after the answer the turn still writes, the answer decides, as if they were not there.
   */
  readonly working = computed(() => {
    if (!this.streaming()) {
      return false;
    }

    const messages = this.messages();
    const last = this.answerBeingWritten() ?? messages[messages.length - 1];
    if (!last || last.role === 'user' || last.role === 'application' || last.parts.length === 0) {
      return true;
    }

    const lastPart = last.parts[last.parts.length - 1];
    return lastPart.type === 'tool-call'
      && lastPart.status !== 'running'
      && lastPart.status !== 'awaiting-approval';
  });
  /**
   * The message the working indicator follows when it is not the last one: the answer the turn
   * still writes, after which messages arrived during the turn. `null` puts the indicator at the end.
   */
  readonly workingAfterId = computed(() => this.answerBeingWritten()?.id ?? null);
  /**
   * While a turn runs: its answer, when messages that arrived during the turn (the application's or
   * other people's, which sit after the answer by their sequence number) follow it at the end of the
   * thread; `null` when nothing arrived after an answer.
   */
  private readonly answerBeingWritten = computed<Message | null>(() => {
    if (!this.streaming()) {
      return null;
    }

    const messages = this.messages();
    let index = messages.length - 1;
    while (index >= 0 && this.arrivedDuringTurn(messages[index])) {
      index--;
    }
    if (index < 0 || index === messages.length - 1) {
      return null;
    }

    return messages[index].role === 'assistant' ? messages[index] : null;
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

  /** True for a message the application posted into the conversation. */
  isApplication(message: Message): boolean {
    return message.role === 'application';
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

  /** True for a message that can arrive while a turn runs: the application's or another person's. */
  private arrivedDuringTurn(message: Message): boolean {
    return this.isApplication(message) || this.isOthers(message);
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
