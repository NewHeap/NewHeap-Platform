import { TemplatePortal } from '@angular/cdk/portal';
import { NgTemplateOutlet } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ErrorHandler,
  OnDestroy,
  TemplateRef,
  ViewContainerRef,
  WritableSignal,
  computed,
  effect,
  inject,
  input,
  model,
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
 * Whether the panel lists the conversations: `panel`, its own list, or `none` for a host that lists them
 * itself and opens them through `NhAssistantPanelService.open` or the store.
 */
export type NhAssistantPanelConversationList = 'panel' | 'none';

/** A message the person sends from the panel's composer, as `beforeSubmit` receives it before it goes. */
export interface NhAssistantOutgoingMessage {
  /** The text, trimmed. */
  readonly text: string;
  /** The uploaded files that go with it, such as `NhAssistantStore.attachedFiles()`. */
  readonly files: readonly FileReference[];
  /** The conversation it goes to; `null` for a new conversation, which the message creates. */
  readonly conversationId: string | null;
  /**
   * True for the first message of its conversation: a new one, or one that was created for the files
   * attached to it and has no messages yet.
   */
  readonly firstMessage: boolean;
  /** The agent that answers it: the conversation's agent, or the agent chosen for a new conversation. */
  readonly agentId: string | null;
}

/**
 * The panel's `beforeSubmit` input: resolves `true` to send the message and `false` to hold it for the
 * host's decision.
 */
export type NhAssistantBeforeSubmit = (message: NhAssistantOutgoingMessage) => Promise<boolean>;

/**
 * The assistant drawer. Place it once in the application layout; it renders nothing in
 * place and appears as an overlay when `NhAssistantPanelService.open()` is called. It opens
 * by itself for an invitation link (`#nh-assistant-join=...`) and for a clicked notification.
 *
 * With `mode="page"` the same chat renders in place instead, filling the element the host gives it,
 * for example on a "Chat" page of the application's menu for long sessions: the conversations stay
 * listed beside the thread on wide screens and there is nothing to close. While it shows, the panel
 * service opens conversations, new chats and drafts there and keeps the drawer closed: an invitation
 * link or a clicked notification that the layout's drawer handles opens its conversation in the page.
 * Both share the conversations of the assistant's scope, and the composer of either shows a draft from
 * `NhAssistantStore.setDraft` or `NhAssistantPanelService.open({ draft })` without sending it. What the
 * person typed and did not send stays with its conversation in the store
 * (`NhAssistantStore.unsentMessage()`): closing the drawer, leaving the page or opening another
 * conversation keeps it, and the drawer and the page show the same text. Each panel tells the store
 * whether it shows the open thread (`NhAssistantStore.registerThreadSurface`), so the conversation is
 * marked as read while any of them shows it.
 *
 * In both modes the host can place its own content above the thread (`nhAssistantAboveThread`) and
 * above the composer (`nhAssistantAboveComposer`), hold a message before it is sent (`beforeSubmit`),
 * open the conversation list (`[(conversationsOpen)]`) or leave the list out (`conversationList="none"`).
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
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly errorHandler = inject(ErrorHandler);
  private readonly router = inject(Router, { optional: true });
  private readonly drawer = viewChild.required<TemplateRef<unknown>>('drawer');
  private readonly threadSurface = this.store.registerThreadSurface();
  private portal?: TemplatePortal;
  private pageRegistered = false;
  /** The conversation the panel showed last, so only a change of it returns from the list. */
  private shownConversationId: string | null | undefined = undefined;

  /** `drawer` by default; `page` renders the chat in place. */
  readonly mode = input<NhAssistantPanelMode>('drawer');
  /** The chat renders in a page of its own instead of the drawer. */
  readonly page = computed(() => this.mode() === 'page');
  /**
   * `panel` (the default) lists the conversations: beside the thread in a page from 900 px wide, and
   * behind the list button otherwise. `none` leaves the list and its button out, in a page and in the
   * drawer alike, for a host that lists the conversations itself and opens them with
   * `NhAssistantPanelService.open(conversationId)` or `NhAssistantStore.openConversation`.
   */
  readonly conversationList = input<NhAssistantPanelConversationList>('panel');
  /**
   * Whether the conversation list shows over the thread, two-way: `[(conversationsOpen)]="open"`, for
   * example from a button of the host on a narrow screen. The list button, choosing or starting a
   * conversation, and opening the preferences or the share view change it too, and the host hears each
   * change. In a page from 900 px wide the conversations are listed beside the thread as well. With
   * `conversationList="none"` no list shows, whatever it says.
   */
  readonly conversationsOpen = model(false);
  /** @deprecated Bind `conversationsOpen` instead; this is the same signal. */
  readonly showConversations: WritableSignal<boolean> = this.conversationsOpen;
  /**
   * Lets the host decide on each message the person sends from this panel's composer before it goes,
   * for example to hold the first message of a new conversation (`firstMessage`) and offer to continue
   * an existing conversation instead. The panel passes the message (`NhAssistantOutgoingMessage`) and
   * waits; meanwhile the composer keeps showing the message and cannot send. `true` sends it. `false`
   * holds it: it stays in the composer as the person's unsent text, with its files, and the host decides
   * through the store, for example `keepUnsentMessage(null)` and then `submit(text, files)` to send it as
   * it is, `setDraft(text, { replace: true })` to change it, or `keepUnsentMessage(null)`,
   * `openConversation` and `submit(text)` to ask it in another conversation. A message also stays unsent
   * when the person changed it or another conversation opened while the host decided, and when the
   * function fails, which goes to Angular's `ErrorHandler`. Messages that the host sends through the
   * store do not pass it. Left unset, every message goes at once.
   */
  readonly beforeSubmit = input<NhAssistantBeforeSubmit | null>(null);
  /** True while the conversation list shows over the thread. */
  readonly listOpen = computed(() => this.conversationsOpen() && this.conversationList() === 'panel');
  /** True while the conversations are listed beside the thread: in a page with its own list. */
  readonly sidebar = computed(() => this.page() && this.conversationList() === 'panel' && this.store.enabled());
  /** True while `beforeSubmit` decides on a message; the composer keeps showing it and cannot send. */
  readonly holding = signal(false);

  readonly titleId = `nh-assistant-panel-title-${nextId++}`;
  readonly markdown = this.config.markdown?.enabled ?? true;
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
  /**
   * What the open or next chat is about (FB25): the subject's type name and name as the server or the
   * host gave them, or its id; `null` for a chat without a subject.
   */
  readonly chatSubjectLabel = computed(() => {
    const subject = this.store.chatSubject();
    if (!subject) {
      return null;
    }

    const name = subject.subjectDisplayName?.trim() || subject.subjectId;
    const typeName = 'subjectTypeName' in subject ? subject.subjectTypeName?.trim() : null;
    return typeName ? `${typeName}: ${name}` : name;
  });

  constructor() {
    // Opening, creating or leaving a conversation returns from the list to the thread. The first
    // conversation the panel shows changes nothing, so a host's `conversationsOpen` stays as bound.
    effect(() => {
      const conversationId = this.conversationId();
      untracked(() => {
        const changed = this.shownConversationId !== undefined && this.shownConversationId !== conversationId;
        this.shownConversationId = conversationId;
        if (changed) {
          this.conversationsOpen.set(false);
          this.showPreferences.set(false);
          this.showShare.set(false);
        }
      });
    });

    // This panel shows the open thread while the drawer is open, or in a page, and nothing covers it.
    // The store counts every panel, so the thread is seen while any of them shows it.
    effect(() => {
      const showing = (this.page() || this.panel.isOpen()) && !this.listOpen() && !this.showPreferences() && !this.showShare();
      untracked(() => this.threadSurface.setShowing(showing));
    });
    inject(DestroyRef).onDestroy(() => this.threadSurface.unregister());

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
    // A page renders the chat in place and registers as the place `open` acts on; the drawer
    // registers its content for the overlay.
    if (this.page()) {
      this.pageRegistered = true;
      this.panel.registerPagePanel(this.host.nativeElement);
      return;
    }

    this.portal = new TemplatePortal(this.drawer(), this.viewContainerRef);
    this.panel.registerPanel(this.portal);
    this.openFromLocation();
  }

  ngOnDestroy(): void {
    if (this.pageRegistered) {
      this.panel.unregisterPagePanel(this.host.nativeElement);
    }
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
    this.conversationsOpen.update(show => !show);
  }

  togglePreferences(): void {
    this.conversationsOpen.set(false);
    this.showShare.set(false);
    this.showPreferences.update(show => !show);
  }

  toggleShare(section: 'all' | 'link' | 'members' = 'all'): void {
    this.conversationsOpen.set(false);
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
    this.conversationsOpen.set(false);
  }

  openConversation(conversationId: string): void {
    void this.store.openConversation(conversationId);
    this.conversationsOpen.set(false);
  }

  deleteConversation(conversationId: string): void {
    void this.store.deleteConversation(conversationId);
  }

  /** Sends what the composer sent, after the host's `beforeSubmit` when it has one. */
  send(text: string): void {
    this.conversationsOpen.set(false);
    this.showPreferences.set(false);
    this.showShare.set(false);

    const files = this.store.attachedFiles();
    const beforeSubmit = this.beforeSubmit();
    if (!beforeSubmit) {
      void this.store.submit(text, files);
      return;
    }

    void this.submitAfterHost(beforeSubmit, text, files);
  }

  /**
   * Asks the host's `beforeSubmit` about a message and sends it on `true`. Until the host decides, and
   * after it holds the message, the message stays in the composer as the person's unsent text of its
   * conversation, with its files.
   */
  private async submitAfterHost(beforeSubmit: NhAssistantBeforeSubmit, text: string, files: FileReference[]): Promise<void> {
    const conversation = this.conversation();
    const message: NhAssistantOutgoingMessage = {
      text,
      files: [...files],
      conversationId: conversation?.id ?? null,
      firstMessage: (conversation?.messages.length ?? 0) === 0,
      agentId: conversation?.agentId ?? this.store.selectedAgentId()
    };

    this.store.keepUnsentMessage({ text, prepared: false });
    this.holding.set(true);
    let send = false;
    try {
      send = await beforeSubmit(message) === true;
    } catch (error) {
      // The host's function failed: the message stays unsent and the application learns why.
      this.errorHandler.handleError(error);
    } finally {
      this.holding.set(false);
    }

    if (!send) {
      return;
    }
    if ((this.conversation()?.id ?? null) !== message.conversationId || this.store.unsentMessage()?.text !== text) {
      // Another conversation opened, or the person changed the message, while the host decided: it
      // stays unsent, as the composer shows it.
      return;
    }

    this.store.keepUnsentMessage(null);
    await this.store.submit(text, files);
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
