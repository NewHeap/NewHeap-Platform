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
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalDecision, Message } from '../../models/assistant-api.models';
import { NhAssistantMarkdownPipe } from '../../internal/nh-assistant-markdown.pipe';
import { NhAssistantApprovalCardComponent } from '../approval-card/nh-assistant-approval-card.component';
import { NhAssistantToolCallCardComponent } from '../tool-call-card/nh-assistant-tool-call-card.component';

/** Above this number of messages the thread renders through CDK virtual scrolling. */
export const NH_ASSISTANT_VIRTUAL_SCROLL_THRESHOLD = 200;

/** Distance from the bottom, in pixels, within which new content keeps the thread pinned to the end. */
const stickToBottomDistance = 120;

/**
 * The messages of a conversation as a live log. Assistant text renders as sanitized
 * Markdown, tool calls and approvals as cards. The thread follows new content while the
 * user is at the bottom.
 */
@Component({
  selector: 'nh-assistant-thread',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    ScrollingModule,
    TranslatePipe,
    NhAssistantMarkdownPipe,
    NhAssistantToolCallCardComponent,
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
  readonly approvalDecision = output<ApprovalDecision>();

  readonly virtualThreshold = NH_ASSISTANT_VIRTUAL_SCROLL_THRESHOLD;
  readonly virtual = computed(() => this.messages().length > NH_ASSISTANT_VIRTUAL_SCROLL_THRESHOLD);
  readonly working = computed(() => {
    if (!this.streaming()) {
      return false;
    }

    const last = this.messages()[this.messages().length - 1];
    return !last || last.role === 'user' || last.parts.length === 0;
  });

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly viewport = viewChild(CdkVirtualScrollViewport);

  constructor() {
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
