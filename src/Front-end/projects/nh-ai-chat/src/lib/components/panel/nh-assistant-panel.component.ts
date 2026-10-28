import { TemplatePortal } from '@angular/cdk/portal';
import { NgTemplateOutlet } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  TemplateRef,
  ViewContainerRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalDecision, ClientContext, FileReference } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NH_ASSISTANT_CONFIG } from '../../nh-assistant.config';
import { NhAssistantPanelService } from '../../services/nh-assistant-panel.service';
import { NhAssistantError, NhAssistantStore } from '../../services/nh-assistant.store';
import { describeNhAssistantClientContext } from '../../services/nh-assistant-page-context';
import { NhAssistantPushService } from '../../services/nh-assistant-push.service';
import { NhAssistantActivityComponent } from '../activity/nh-assistant-activity.component';
import { NhAssistantAgentPickerComponent } from '../agent-picker/nh-assistant-agent-picker.component';
import { NhAssistantComposerComponent } from '../composer/nh-assistant-composer.component';
import { NhAssistantConversationListComponent } from '../conversation-list/nh-assistant-conversation-list.component';
import { NhAssistantPreferencesComponent } from '../preferences/nh-assistant-preferences.component';
import { NhAssistantShareComponent } from '../share/nh-assistant-share.component';
import {
  NH_ASSISTANT_CONVERSATION_FRAGMENT,
  NH_ASSISTANT_JOIN_FRAGMENT,
  NH_ASSISTANT_RUN_FRAGMENT
} from '../../services/nh-assistant-share-link';
import {
  NhAssistantApprovalChoice,
  NhAssistantApprovalResolutionChoice,
  NhAssistantQuestionAnswer,
  NhAssistantThreadComponent
} from '../thread/nh-assistant-thread.component';
import { nhAssistantErrorKeys, toNhAssistantError } from '../../internal/nh-assistant-errors';

let nextId = 0;

/** Where the panel shows: as the drawer of the application, or in a page of its own. */
export type NhAssistantPanelMode = 'drawer' | 'page';

/**
 * The assistant drawer. Place it once in the application layout; it renders nothing in
 * place and appears as an overlay when `NhAssistantPanelService.open()` is called. It opens
 * by itself for an invitation link (`#nh-assistant-join=...`) and for a clicked notification.
 *
 * With `mode="page"` the same chat renders in place instead, filling the element the host gives it,
 * for example on a "Chat" page of the application's menu for long sessions: the conversations stay
 * listed beside the thread on wide screens, there is nothing to close, and invitation links and
 * clicked notifications stay with the drawer of the layout. Both share the conversations of the
 * assistant's scope.
 */
@Component({
  selector: 'nh-assistant-panel',
  standalone: true,
  host: { '[class.nh-assistant-page]': 'page()' },
  imports: [
    NgTemplateOutlet,
    RouterLink,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantActivityComponent,
    NhAssistantAgentPickerComponent,
    NhAssistantComposerComponent,
    NhAssistantConversationListComponent,
    NhAssistantPreferencesComponent,
    NhAssistantShareComponent,
    NhAssistantThreadComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-panel.component.html',
  styleUrl: './nh-assistant-panel.component.scss'
})
export class NhAssistantPanelComponent implements AfterViewInit, OnDestroy {
  readonly store = inject(NhAssistantStore);
  readonly panel = inject(NhAssistantPanelService);
  readonly push = inject(NhAssistantPushService);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly router = inject(Router, { optional: true });
  private readonly drawer = viewChild.required<TemplateRef<unknown>>('drawer');
  private portal?: TemplatePortal;

  /** `drawer` by default; `page` renders the chat in place. */
  readonly mode = input<NhAssistantPanelMode>('drawer');
  /** The chat renders in a page of its own instead of the drawer. */
  readonly page = computed(() => this.mode() === 'page');

  readonly titleId = `nh-assistant-panel-title-${nextId++}`;
  readonly markdown = this.config.markdown?.enabled ?? true;
  readonly showConversations = signal(false);
  readonly showPreferences = signal(false);
  readonly showShare = signal(false);
  readonly shareSection = signal<'all' | 'link' | 'members'>('all');
  readonly notificationError = signal<NhAssistantError | null>(null);
  readonly notificationsLabel = computed(() => this.push.settings()?.pushEnabled
    ? 'nh-assistant.panel.notifications-off' : 'nh-assistant.panel.notifications-on');
  readonly adminRoute = this.config.adminRoute ?? null;
  readonly conversation = this.store.activeConversation;
  private readonly conversationId = computed(() => this.conversation()?.id ?? null);
  readonly messages = computed(() => this.conversation()?.messages ?? []);
  /** An archived conversation takes no new messages. A failed one does: the person asks again. */
  readonly closed = computed(() => this.conversation()?.status === 'archived');
  readonly busy = computed(() => this.store.streaming() || this.conversation()?.status === 'running');
  /**
   * The last turn failed and no error bar explains it, for example after the conversation was
   * reopened. The panel then says so, while the composer stays available.
   */
  readonly lastTurnFailed = computed(() => this.conversation()?.status === 'failed'
    && !this.busy() && this.store.error() === null);
  readonly composerDisabled = computed(() => !this.store.canSubmit());
  readonly editingQueuedId = signal<string | null>(null);
  readonly queuedDraft = signal('');
  private resumeAfterEditing = false;
  readonly validQueuedDraft = computed(() => this.queuedDraft().trim().length > 0
    && this.queuedDraft().trim().length <= (this.store.limits()?.maxMessageChars ?? Infinity));
  readonly composerStatus = computed<'running' | 'waiting-for-approval' | 'waiting-for-answer' | null>(() => {
    if (this.store.streaming() || this.conversation()?.status === 'running') {
      return 'running';
    }
    if (this.conversation()?.status !== 'waiting-for-approval' || this.store.waitExpired()) {
      // A wait whose lifetime passed holds nothing back: the next message is sent and closes it.
      return null;
    }
    // A turn that asked a question waits for its answer, not for an approval.
    return this.conversation()?.pendingQuestion ? 'waiting-for-answer' : 'waiting-for-approval';
  });
  /**
   * Who the composer says the conversation waits for, when it is someone else: the addressee of a
   * pending question, otherwise the person whose turn waits.
   */
  readonly composerStatusActor = computed(() => {
    if (this.composerStatus() === 'waiting-for-answer') {
      const addressee = this.store.questionAddressee();
      return addressee ? addressee.displayName ?? '' : null;
    }
    return this.activeMemberName();
  });
  /** Name of another participant whose turn runs or waits, or `null`. */
  readonly activeMemberName = computed(() => {
    const member = this.store.activeMember();
    return member ? member.displayName ?? '' : null;
  });
  /** The owner and the person who started the turn may stop it. */
  readonly canStop = computed(() => this.store.isOwner() || this.store.activeMember() === null);
  readonly shared = computed(() => this.store.members().some(member => member.role === 'participant'));
  readonly canShare = computed(() => this.conversation() !== null && !!this.store.collaboration());
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.notificationError() ?? this.store.error()));
  readonly noticeKeys = computed(() => nhAssistantErrorKeys(this.store.notice()));

  constructor() {
    // Opening, creating or leaving a conversation returns from the list to the thread.
    effect(() => {
      if (this.conversationId() !== undefined) {
        untracked(() => {
          this.showConversations.set(false);
          this.showPreferences.set(false);
          this.showShare.set(false);
        });
      }
    });

    // The open thread counts as seen only while the drawer, or the page, shows it.
    effect(() => {
      const viewing = (this.page() || this.panel.isOpen()) && !this.showConversations() && !this.showPreferences() && !this.showShare();
      untracked(() => this.store.setViewing(viewing));
    });

    // A clicked notification opens its conversation in this window, in the drawer of the layout.
    const notifications = this.push.openConversation$.subscribe(conversationId => {
      if (!this.page()) {
        this.panel.open(conversationId);
      }
    });
    inject(DestroyRef).onDestroy(() => notifications.unsubscribe());

    effect(() => {
      if (this.page() || this.panel.isOpen()) {
        untracked(() => {
          void this.store.initialize().then(() => this.store.refreshConversations());
          this.refreshPageContext();
        });
      }
    });

    // The drawer stays open while the user navigates; keep the page-context control current.
    const navigation = this.router?.events.subscribe(event => {
      if (event instanceof NavigationEnd && (this.page() || this.panel.isOpen())) {
        this.refreshPageContext();
      }
    });
    inject(DestroyRef).onDestroy(() => navigation?.unsubscribe());
  }

  ngAfterViewInit(): void {
    // A page renders the chat in place; only the drawer registers with the panel service.
    if (this.page()) {
      return;
    }

    this.portal = new TemplatePortal(this.drawer(), this.viewContainerRef);
    this.panel.registerPanel(this.portal);
    this.openFromLocation();
  }

  ngOnDestroy(): void {
    if (this.portal) {
      this.panel.unregisterPanel(this.portal);
    }
  }

  refreshPageContext(): void {
    void this.store.refreshPageContext();
  }

  describeContext(context: ClientContext): string {
    return describeNhAssistantClientContext(context);
  }

  toggleConversations(): void {
    this.showPreferences.set(false);
    this.showShare.set(false);
    this.showConversations.update(show => !show);
  }

  togglePreferences(): void {
    this.showConversations.set(false);
    this.showShare.set(false);
    this.showPreferences.update(show => !show);
  }

  toggleShare(section: 'all' | 'link' | 'members' = 'all'): void {
    this.showConversations.set(false);
    this.showPreferences.set(false);
    const close = this.showShare() && this.shareSection() === section;
    this.shareSection.set(section);
    this.showShare.set(!close);
  }

  /** Applies the notification choice immediately, including browser permission handling. */
  async toggleNotifications(): Promise<void> {
    if (this.push.busy() || this.push.state() === 'unavailable') {
      return;
    }
    this.notificationError.set(null);
    try {
      await this.push.setEnabled(!this.push.settings()?.pushEnabled);
    } catch (error) {
      this.notificationError.set(toNhAssistantError(error));
    }
  }

  newConversation(): void {
    this.store.startNewConversation();
    this.showConversations.set(false);
  }

  openConversation(conversationId: string): void {
    void this.store.openConversation(conversationId);
    this.showConversations.set(false);
  }

  deleteConversation(conversationId: string): void {
    void this.store.deleteConversation(conversationId);
  }

  send(text: string): void {
    this.showConversations.set(false);
    this.showPreferences.set(false);
    this.showShare.set(false);
    void this.store.submit(text, this.store.attachedFiles());
  }

  /** Uploads the files the person attached to the next message. */
  attach(files: File[]): void {
    void this.store.attach(files);
  }

  /** The names of the files a queued message carries, as the queue lists them. */
  fileNames(files: readonly FileReference[]): string {
    return files.map(file => file.name).join(', ');
  }

  editQueuedMessage(id: string, text: string): void {
    this.resumeAfterEditing = !this.store.queuePaused();
    this.store.pauseQueue();
    this.editingQueuedId.set(id);
    this.queuedDraft.set(text);
  }

  changeQueuedDraft(event: Event): void {
    this.queuedDraft.set((event.target as HTMLTextAreaElement).value);
  }

  saveQueuedMessage(id: string): void {
    if (this.store.updateQueuedMessage(id, this.queuedDraft())) {
      this.cancelQueueEdit();
    }
  }

  cancelQueueEdit(): void {
    this.editingQueuedId.set(null);
    if (this.resumeAfterEditing && !this.store.error()) {
      this.store.resumeQueue();
    }
  }

  /**
   * Opens the panel for an invitation link or a clicked notification in the URL fragment and
   * removes the fragment, so a reload or a shared screenshot does not reuse the token. A clicked
   * notification about the proposals of a run goes to `NhAssistantPushService.openRun$` while the
   * application subscribes, and otherwise opens its conversation.
   */
  private openFromLocation(): void {
    const location = globalThis.location;
    const hash = location?.hash?.replace(/^#/, '') ?? '';
    const separatorIndex = hash.indexOf('=');
    const name = separatorIndex < 0 ? hash : hash.slice(0, separatorIndex);
    const value = separatorIndex < 0 ? '' : hash.slice(separatorIndex + 1);
    if (!value || (name !== NH_ASSISTANT_JOIN_FRAGMENT && name !== NH_ASSISTANT_CONVERSATION_FRAGMENT && name !== NH_ASSISTANT_RUN_FRAGMENT)) {
      return;
    }

    globalThis.history?.replaceState(globalThis.history.state, '', location.pathname + location.search);
    if (name === NH_ASSISTANT_CONVERSATION_FRAGMENT) {
      this.panel.open(decodeURIComponent(value));
      return;
    }

    const tokenIndex = value.indexOf('.');
    if (tokenIndex <= 0) {
      return;
    }
    if (name === NH_ASSISTANT_RUN_FRAGMENT) {
      this.push.openNotification(decodeURIComponent(value.slice(tokenIndex + 1)), decodeURIComponent(value.slice(0, tokenIndex)));
      return;
    }
    const conversationId = decodeURIComponent(value.slice(0, tokenIndex));
    const token = decodeURIComponent(value.slice(tokenIndex + 1));
    this.panel.open();
    void this.store.initialize().then(() => this.store.joinConversation(conversationId, token));
  }

  decide(decision: ApprovalDecision): void {
    this.store.decide(decision);
  }

  /** Decides the approval of a card; in an agent thread several proposals can be open. */
  decideApproval(choice: NhAssistantApprovalChoice): void {
    this.store.decide(choice.decision, undefined, choice.approvalId);
  }

  resolveApproval(choice: NhAssistantApprovalResolutionChoice): void {
    void this.store.resolveApproval(choice.approvalId, choice.resolution);
  }

  /** Answers the question the conversation waits for; only its addressee gets the choices. */
  answerQuestion(answer: NhAssistantQuestionAnswer): void {
    this.store.answerQuestion(answer.questionId, answer.choice);
  }

  cancel(): void {
    void this.store.cancel();
  }
}
