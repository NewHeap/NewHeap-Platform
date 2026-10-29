import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import {
  ConversationSummary,
  NhAssistantBeforeSubmit,
  NhAssistantOutgoingMessage,
  NhAssistantPanelComponent,
  NhAssistantPanelConversationList,
  NhAssistantStore
} from '@newheap/platform-ai-chat';
import { TranslateModule } from '@ngx-translate/core';

/**
 * SPM-253: the assistant's chat on a page of its own, for long sessions. The same panel as the
 * layout's drawer renders in place with `mode="page"`: on wide screens the conversations stay listed
 * beside the thread, and it shares them with the drawer of the portal's assistant scope.
 *
 * SPM-376: while this page shows, the assistant opens here. The launcher, a clicked notification and
 * every `NhAssistantPanelService.open(...)` act on this panel and the drawer stays closed; the open
 * conversation counts as read while this page shows it. The topic bar's list button switches the
 * panel's own conversation list off and on (`conversationList`).
 *
 * SPM-377: the host's own content in the panel. A topic bar above the thread names the open
 * conversation (`nhAssistantAboveThread`), and `beforeSubmit` holds the first message of a new
 * conversation while earlier conversations exist: a choice above the composer
 * (`nhAssistantAboveComposer`) continues the latest conversation with it, starts the new conversation
 * or lets the person change the question, through the store.
 */
@Component({
  selector: 'app-assistant-chat-page',
  standalone: true,
  imports: [TranslateModule, NhAssistantPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="assistant-chat-page" [attr.aria-label]="'project.assistant-chat-nav' | translate">
      <nh-assistant-panel mode="page" [conversationList]="conversationList()" [beforeSubmit]="beforeSubmit">
        <div nhAssistantAboveThread class="topic-bar">
          <div class="topic">
            <span class="topic-label">{{ 'project.assistant-chat-topic' | translate }}</span>
            <strong class="topic-title" [attr.title]="topic()">{{ topic() ?? ('project.assistant-chat-topic-new' | translate) }}</strong>
          </div>
          <button
            type="button"
            class="topic-action"
            [attr.aria-pressed]="listShown()"
            [attr.title]="'project.assistant-chat-list-toggle-hint' | translate"
            (click)="toggleList()">
            <i class="ph ph-sidebar-simple" aria-hidden="true"></i>
            <span>{{ 'project.assistant-chat-list-toggle' | translate }}</span>
          </button>
        </div>
        @if (choice(); as message) {
          <div nhAssistantAboveComposer class="choice-bar" role="group" aria-labelledby="assistant-chat-choice-question">
            <p id="assistant-chat-choice-question" class="choice-question">{{ 'project.assistant-chat-choice-question' | translate }}</p>
            <div class="choice-actions">
              @if (latest(); as conversation) {
                <button #firstChoice type="button" class="choice primary" [disabled]="deciding()" (click)="continueIn(message, conversation.id)">
                  <i class="ph ph-arrow-bend-down-right" aria-hidden="true"></i>
                  <span>{{ 'project.assistant-chat-choice-continue' | translate: { title: conversation.title || ('project.assistant-chat-topic-untitled' | translate) } }}</span>
                </button>
              }
              <button type="button" class="choice" [disabled]="deciding()" (click)="startApart(message)">
                <i class="ph ph-plus" aria-hidden="true"></i>
                <span>{{ 'project.assistant-chat-choice-new' | translate }}</span>
              </button>
              <button type="button" class="choice quiet" [disabled]="deciding()" (click)="change()">
                <i class="ph ph-pencil-simple" aria-hidden="true"></i>
                <span>{{ 'project.assistant-chat-choice-change' | translate }}</span>
              </button>
            </div>
          </div>
        }
      </nh-assistant-panel>
    </section>
  `,
  styles: [`
    :host { display: block; }
    .assistant-chat-page {
      height: max(440px, calc(100dvh - 72px - 88px));
      margin-top: 24px;
    }
    .topic-bar {
      display: flex;
      align-items: center;
      gap: 12px;
      min-width: 0;
      border-bottom: 1px solid var(--line);
      padding: 8px 12px 8px 16px;
    }
    .topic {
      display: flex;
      flex: 1;
      align-items: baseline;
      gap: 8px;
      min-width: 0;
    }
    .topic-label {
      flex: none;
      color: var(--muted);
      font-size: 12px;
      font-weight: 600;
      letter-spacing: .04em;
      text-transform: uppercase;
    }
    .topic-title {
      min-width: 0;
      overflow: hidden;
      font-size: 14px;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .topic-action,
    .choice {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      min-height: 32px;
      border: 1px solid var(--line);
      border-radius: var(--radius-control);
      padding: 4px 10px;
      color: var(--ink);
      background: transparent;
      font-size: 13px;
    }
    .topic-action { flex: none; }
    .topic-action[aria-pressed='true'] {
      border-color: var(--line-strong);
      background: var(--surface-subtle);
    }
    .topic-action:hover:not(:disabled),
    .choice:hover:not(:disabled) { background: var(--surface-subtle); }
    .choice-bar {
      border: 1px solid var(--line-strong);
      border-radius: var(--radius-control);
      padding: 10px 12px;
      background: var(--surface-subtle);
    }
    .choice-question {
      margin: 0 0 8px;
      color: var(--ink-subtle);
      font-size: 13px;
    }
    .choice-actions {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .choice span {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .choice { max-width: 100%; }
    .choice.primary {
      border-color: var(--brand);
      color: var(--accent-ink);
      background: var(--brand);
    }
    .choice.primary:hover:not(:disabled) { background: var(--brand-strong); }
    .choice.quiet { border-color: transparent; color: var(--muted); }
    .choice:disabled { opacity: .6; }
    @media (max-width: 760px) {
      .assistant-chat-page { height: max(420px, calc(100dvh - 64px - 56px)); margin-top: 16px; }
      .topic-action span { display: none; }
    }
  `]
})
export class AssistantChatPageComponent {
  private readonly store = inject(NhAssistantStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly firstChoice = viewChild<ElementRef<HTMLButtonElement>>('firstChoice');

  /** SPM-376: the panel's own conversation list, which the topic bar switches off for a wide thread. */
  readonly conversationList = signal<NhAssistantPanelConversationList>('panel');
  readonly listShown = computed(() => this.conversationList() === 'panel');
  /** The open conversation's title; `null` for a new conversation. */
  readonly topic = computed(() => this.store.activeConversation()?.title?.trim() || null);
  /** The first message of a new conversation that `beforeSubmit` held. */
  readonly held = signal<NhAssistantOutgoingMessage | null>(null);
  readonly deciding = signal(false);
  /**
   * The choice about the held message, while the new conversation is open and its composer still holds
   * the message; with the text as the person may have changed it since.
   */
  readonly choice = computed<NhAssistantOutgoingMessage | null>(() => {
    const held = this.held();
    const text = this.store.unsentMessage()?.text.trim() ?? '';
    if (!held || this.store.activeConversation() !== null || text.length === 0) {
      return null;
    }

    return { ...held, text };
  });
  /** The person's latest conversation with the same agent, which the held question may continue. */
  readonly latest = computed<ConversationSummary | null>(() => {
    const agentId = this.held()?.agentId;
    const candidates = this.store.conversations()
      .filter(conversation => conversation.agentId === agentId && conversation.status !== 'archived');
    return [...candidates].sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))[0] ?? null;
  });

  /**
   * SPM-377: holds the first message of a new conversation, one that does not exist yet, while the
   * person has earlier conversations with the agent; every other message goes at once.
   */
  readonly beforeSubmit: NhAssistantBeforeSubmit = message => {
    const earlier = this.store.conversations().some(conversation => conversation.agentId === message.agentId);
    if (message.conversationId !== null || !earlier) {
      this.held.set(null);
      return Promise.resolve(true);
    }

    this.held.set(message);
    return Promise.resolve(false);
  };

  constructor() {
    // The choice takes the keyboard when it shows, so the person answers it first.
    effect(() => {
      if (this.choice() !== null) {
        untracked(() => afterNextRender(() => this.firstChoice()?.nativeElement.focus(), { injector: this.injector }));
      }
    });
  }

  toggleList(): void {
    this.conversationList.update(list => list === 'panel' ? 'none' : 'panel');
  }

  /** Asks the held question in the latest conversation instead of a new one. */
  async continueIn(message: NhAssistantOutgoingMessage, conversationId: string): Promise<void> {
    this.deciding.set(true);
    try {
      this.held.set(null);
      // The question leaves the new conversation's composer and goes to the earlier conversation.
      this.store.keepUnsentMessage(null);
      await this.store.openConversation(conversationId);
      if (this.store.activeConversation()?.id !== conversationId) {
        // The conversation did not open; the store shows why and the question waits where it was.
        this.store.keepUnsentMessage({ text: message.text, prepared: false });
        return;
      }

      await this.store.submit(message.text);
    } finally {
      this.deciding.set(false);
    }
  }

  /** Sends the held question as the first message of the new conversation, as the person wrote it. */
  async startApart(message: NhAssistantOutgoingMessage): Promise<void> {
    this.deciding.set(true);
    try {
      this.held.set(null);
      this.store.keepUnsentMessage(null);
      await this.store.submit(message.text, message.files);
    } finally {
      this.deciding.set(false);
    }
  }

  /** Leaves the question in the composer for the person to change; sending it asks again. */
  change(): void {
    this.held.set(null);
    this.host.nativeElement.querySelector<HTMLElement>('[data-nh-assistant-autofocus]')?.focus();
  }
}
