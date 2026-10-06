import { DOCUMENT } from '@angular/common';
import {
  DestroyRef,
  EnvironmentInjector,
  Injectable,
  PLATFORM_ID,
  computed,
  effect,
  inject,
  runInInjectionContext,
  signal,
  untracked
} from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { Observable, Subscription, firstValueFrom, isObservable } from 'rxjs';
import {
  AgentSummary,
  ApprovalDecision,
  AssistantStatus,
  ClientContext,
  Conversation,
  ConversationMember,
  ConversationStatus,
  ConversationSummary,
  DirectoryEntry
} from '../models/assistant-api.models';
import {
  LiveConversationChanged,
  LiveConversationEvent,
  LiveConversationRead,
  LiveConversationRemoved
} from '../models/assistant-live.models';
import { NhAssistantClientErrorCodes, NhAssistantSseEvent, TurnUsage } from '../models/assistant-sse.models';
import { NH_ASSISTANT_ACCESS_POLICY, NH_ASSISTANT_CONFIG } from '../nh-assistant.config';
import { NhAssistantApiError, NhAssistantApiService, nhAssistantErrorMessageKey } from './nh-assistant-api.service';
import { NhAssistantLiveService } from './nh-assistant-live.service';
import { NhAssistantPushService } from './nh-assistant-push.service';
import {
  applyNhAssistantApprovalDecision,
  applyNhAssistantConversationChange,
  applyNhAssistantEvent,
  applyNhAssistantLiveEvent,
  nhAssistantLatestTurnHasText
} from './nh-assistant-reducer';
import { normalizeNhAssistantClientContext } from './nh-assistant-page-context';
import { NhAssistantUiState } from './nh-assistant-ui-state';

/** A user-facing assistant failure: a stable code and a translation key, never raw server text. */
export interface NhAssistantError {
  code: string;
  messageKey: string;
}

/**
 * A non-blocking remark about a turn that completed normally, for example a limit that cut the
 * answer short. The conversation stays usable; the notice only explains the answer.
 */
export type NhAssistantNotice = NhAssistantError;

/** An unsent message held in this tab, scoped to its original conversation. */
export interface NhAssistantQueuedMessage {
  id: string;
  conversationId: string;
  text: string;
  state: 'preparing' | 'queued' | 'sending' | 'failed';
}

interface QueuedMessage extends NhAssistantQueuedMessage {
  clientContext?: ClientContext | null;
}

/** Notice code for a completed turn that produced no answer text and no server code. */
export const NH_ASSISTANT_NO_ANSWER_NOTICE_CODE = 'assistant-no-answer';


/**
 * A conversation that needs attention outside the open thread: a turn runs, an approval waits or
 * there is something unread.
 */
export interface NhAssistantActivity {
  id: string;
  title: string | null;
  status: ConversationStatus;
  unread: boolean;
  shared: boolean;
  /** True for the conversation shown in the panel. */
  active: boolean;
  updatedAt: string;
}

/** True when a conversation has messages the caller has not read. A running turn is not unread yet. */
export function nhAssistantIsUnread(summary: Pick<ConversationSummary, 'status' | 'lastMessageSequence' | 'lastReadSequence'>): boolean {
  if (summary.status === 'running' || summary.lastReadSequence === undefined) {
    return false;
  }
  return (summary.lastMessageSequence ?? 0) > summary.lastReadSequence;
}

/** The state of one conversation this tab has opened. */
interface NhAssistantSession {
  conversation: Conversation;
  /** A request of this tab streams a turn of the conversation. */
  streaming: boolean;
  deciding: boolean;
  error: NhAssistantError | null;
  notice: NhAssistantNotice | null;
  lastUsage: TurnUsage | null;
}

const conversationPageSize = 50;
const cancelGracePeriodMs = 5_000;
const maxSessions = 20;
const refreshDelayMs = 300;
const newConversationKey = '';

/**
 * Signal-based state of one assistant scope: availability, agents, the conversation list and
 * every conversation this tab has opened. Several conversations can run turns at the same time;
 * the public signals describe the conversation that is open in the panel. With live updates the
 * store follows turns of other people and of the user's other tabs.
 */
@Injectable()
export class NhAssistantStore {
  private readonly api = inject(NhAssistantApiService);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly injector = inject(EnvironmentInjector);
  private readonly accessPolicy = inject(NH_ASSISTANT_ACCESS_POLICY);
  private readonly uiState = inject(NhAssistantUiState);
  private readonly live = inject(NhAssistantLiveService);
  private readonly push = inject(NhAssistantPushService);
  private readonly document = inject(DOCUMENT);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly accessGrantedState = signal<boolean | null>(null);
  private readonly statusState = signal<AssistantStatus | null>(null);
  private readonly statusLoadingState = signal(false);
  private readonly selectedAgentIdState = signal<string | null>(null);
  private readonly conversationsState = signal<ConversationSummary[]>([]);
  private readonly conversationsTotalState = signal(0);
  private readonly conversationsLoadingState = signal(false);
  private readonly sessionsState = signal<ReadonlyMap<string, NhAssistantSession>>(new Map());
  private readonly activeIdState = signal<string | null>(null);
  private readonly conversationLoadingState = signal(false);
  /** The conversation a message is being prepared for; `''` while a new conversation is created. */
  private readonly sendingState = signal<string | null>(null);
  private readonly draftErrorState = signal<NhAssistantError | null>(null);
  private readonly restoredDraftState = signal<string | null>(null);
  private readonly pageContextState = signal<ClientContext | null>(null);
  private readonly pageContextExcludedState = signal(false);
  private readonly restorePanelOpenState = signal(false);
  private readonly viewingState = signal(false);
  private readonly pageVisibleState = signal(true);
  private readonly queuedState = signal<QueuedMessage[]>([]);
  private readonly pausedQueuesState = signal<ReadonlySet<string>>(new Set());
  private readonly queueDispatchState = signal<string | null>(null);
  private readonly steeringState = signal<string | null>(null);
  private readonly submittingState = signal(false);
  private destroyed = false;

  private readonly streams = new Map<string, Subscription>();
  private readonly cancelTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private readonly pendingSendResolves = new Map<string, (accepted: boolean) => void>();
  private readonly markingRead = new Set<string>();
  private readonly liveSubscription = new Subscription();
  private accessSubscription?: Subscription;
  private refreshTimer?: ReturnType<typeof setTimeout>;
  private initialization?: Promise<void>;
  private statusRevision = 0;
  private accountRevision = 0;
  private collaborationKey: string | null = null;

  private readonly activeSession = computed(() => {
    const id = this.activeIdState();
    return id === null ? null : this.sessionsState().get(id) ?? null;
  });

  /** True when the access policy allows the user and the server reports the assistant as enabled. */
  readonly enabled = computed(() => this.accessGrantedState() === true && this.statusState()?.enabled === true);
  readonly accessGranted = this.accessGrantedState.asReadonly();
  /** True when the assistant is enabled and the server reports that the caller passes the admin policy. */
  readonly canAdminister = computed(() => this.enabled() && this.statusState()?.canAdminister === true);
  readonly statusLoading = this.statusLoadingState.asReadonly();
  readonly limits = computed(() => this.statusState()?.limits ?? null);
  readonly agents = computed<AgentSummary[]>(() => this.statusState()?.agents ?? []);
  /** Sharing, live update and notification features the server offers. */
  readonly collaboration = computed(() => this.statusState()?.collaboration ?? null);
  readonly selectedAgentId = this.selectedAgentIdState.asReadonly();
  readonly selectedAgent = computed(() =>
    this.agents().find(agent => agent.id === this.selectedAgentIdState()) ?? null
  );
  readonly conversations = this.conversationsState.asReadonly();
  readonly conversationsTotal = this.conversationsTotalState.asReadonly();
  readonly conversationsLoading = this.conversationsLoadingState.asReadonly();
  /** The conversation open in the panel, or `null` for a new one. */
  readonly activeConversation = computed(() => this.activeSession()?.conversation ?? null);
  readonly conversationLoading = this.conversationLoadingState.asReadonly();
  /** True while this tab sends a message or streams a turn of the open conversation. */
  readonly streaming = computed(() => {
    const sending = this.sendingState();
    const activeId = this.activeIdState();
    return (sending !== null && sending === (activeId ?? newConversationKey)) || this.activeSession()?.streaming === true;
  });
  readonly pendingApproval = computed(() => this.activeConversation()?.pendingApproval ?? null);
  readonly deciding = computed(() => this.activeSession()?.deciding === true);
  readonly error = computed(() => this.activeIdState() === null ? this.draftErrorState() : this.activeSession()?.error ?? null);
  /** Non-blocking remark about the latest completed turn; shown only while no error is shown. */
  readonly notice = computed(() => this.activeSession()?.notice ?? null);
  readonly lastUsage = computed(() => this.activeSession()?.lastUsage ?? null);
  /** Text of a message the server did not accept, so the composer can offer it again. */
  readonly restoredDraft = this.restoredDraftState.asReadonly();
  /** Page context that the next message would send, for the transparency chip. */
  readonly pageContext = this.pageContextState.asReadonly();
  /** True when the user left the page context out of the next message. */
  readonly pageContextExcluded = this.pageContextExcludedState.asReadonly();
  /** Whether the drawer was open when this account last left the application. */
  readonly restorePanelOpen = this.restorePanelOpenState.asReadonly();
  /** True while the user can send: enabled, an agent is chosen and no turn runs or waits. */
  readonly canSend = computed(() => {
    const conversation = this.activeConversation();
    const blocked = conversation !== null && conversation.status !== 'idle';
    return this.enabled() && this.selectedAgentIdState() !== null && !this.streaming() && !this.conversationLoadingState() && !blocked;
  });
  /** Messages not yet accepted by the server, for the open conversation. Never persisted. */
  readonly queuedMessages = computed<readonly NhAssistantQueuedMessage[]>(() =>
    this.queuedState().filter(item => item.conversationId === this.activeIdState()));
  /** Sending or queueing is available during a turn and while an approval waits. */
  readonly canSubmit = computed(() => (this.canSend() && !this.submittingState()) || (this.enabled()
    && !this.conversationLoading() && this.activeConversation() !== null
    && ['idle', 'running', 'waiting-for-approval'].includes(this.activeConversation()!.status)));
  readonly queuePaused = computed(() => this.pausedQueuesState().has(this.activeIdState() ?? '') || this.error() !== null);
  /** Steering cancels the active turn before sending the selected correction. */
  readonly canSteer = computed(() => {
    const conversation = this.activeConversation();
    const ownsTurn = conversation?.activeActorId && conversation.currentActorId
      ? conversation.activeActorId === conversation.currentActorId : this.activeSession()?.streaming === true;
    return this.enabled() && this.steeringState() === null && !this.submittingState()
      && this.queueDispatchState() === null
      && ['running', 'waiting-for-approval'].includes(conversation?.status ?? '')
      && (this.isOwner() || ownsTurn);
  });
  /** The owner and participants of the open conversation; empty while it is not shared. */
  readonly members = computed<ConversationMember[]>(() => this.activeConversation()?.members ?? []);
  /** True when the current user owns the open conversation (or it is new). */
  readonly isOwner = computed(() => (this.activeConversation()?.role ?? 'owner') === 'owner');
  /** True when the current user may decide the pending approval: it belongs to their own turn. */
  readonly canDecide = computed(() => {
    const conversation = this.activeConversation();
    if (!conversation?.pendingApproval) {
      return false;
    }
    return !conversation.activeActorId || !conversation.currentActorId || conversation.activeActorId === conversation.currentActorId;
  });
  /** The member whose turn runs or waits in the open conversation, when it is someone else. */
  readonly activeMember = computed<ConversationMember | null>(() => {
    const conversation = this.activeConversation();
    if (!conversation?.activeActorId || conversation.activeActorId === conversation.currentActorId) {
      return null;
    }
    return conversation.members?.find(member => member.actorId === conversation.activeActorId) ?? null;
  });
  /** Conversations that need attention: running, waiting for approval or unread. Most recent first. */
  readonly activity = computed<NhAssistantActivity[]>(() => {
    const sessions = this.sessionsState();
    const activeId = this.activeIdState();
    return this.conversationsState()
      .map(summary => {
        const session = sessions.get(summary.id);
        const status = session?.streaming ? session.conversation.status : summary.status;
        return {
          id: summary.id,
          title: summary.title,
          status,
          unread: nhAssistantIsUnread({ ...summary, status }),
          shared: (summary.participantCount ?? 0) > 0,
          active: summary.id === activeId,
          updatedAt: summary.updatedAt
        };
      })
      .filter(item => item.status === 'running' || item.status === 'waiting-for-approval' || item.unread);
  });
  readonly runningCount = computed(() => this.activity().filter(item => item.status === 'running').length);
  readonly waitingCount = computed(() => this.activity().filter(item => item.status === 'waiting-for-approval').length);
  readonly unreadCount = computed(() => this.activity().filter(item => item.unread).length);
  /** Live update connection state. */
  readonly liveState = this.live.state;

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.accessSubscription?.unsubscribe();
      this.liveSubscription.unsubscribe();
      this.stopAllTurns();
      clearTimeout(this.refreshTimer);
    });

    effect(() => {
      const queued = this.queuedState();
      const sessions = this.sessionsState();
      const paused = this.pausedQueuesState();
      const steering = this.steeringState();
      const steeringItem = queued.find(item => item.id === steering);
      const steeringSession = steeringItem ? sessions.get(steeringItem.conversationId) : null;
      if (steering && (!steeringItem || steeringSession?.error
        || (steeringSession?.conversation.status === 'idle' && !steeringSession.streaming))) {
        untracked(() => this.steeringState.set(null));
      }
      if (!this.enabled() || this.queueDispatchState() !== null || this.sendingState() !== null || this.submittingState()) {
        return;
      }
      const next = queued.find((item, index) => {
        const session = sessions.get(item.conversationId);
        return item.state === 'queued' && !paused.has(item.conversationId)
          && !queued.slice(0, index).some(previous => previous.conversationId === item.conversationId)
          && session?.conversation.status === 'idle' && !session.streaming && !session.deciding && !session.error;
      });
      if (next) {
        untracked(() => void this.dispatchQueued(next));
      }
    });

    this.liveSubscription.add(this.live.changed$.subscribe(change => this.onLiveChanged(change)));
    this.liveSubscription.add(this.live.read$.subscribe(read => this.onLiveRead(read)));
    this.liveSubscription.add(this.live.removed$.subscribe(removed => this.onLiveRemoved(removed)));
    this.liveSubscription.add(this.live.events$.subscribe(event => this.onLiveEvent(event)));
    this.liveSubscription.add(this.live.resync$.subscribe(() => this.resync()));

    if (this.browser) {
      const update = () => this.pageVisibleState.set(this.document.visibilityState === 'visible' && this.document.hasFocus());
      const visible = () => {
        update();
        if (this.document.visibilityState === 'visible' && this.enabled()) {
          // Without live updates, or on another instance, coming back reloads the list.
          void this.refreshConversations();
        }
      };
      this.document.addEventListener('visibilitychange', visible);
      globalThis.addEventListener?.('focus', update);
      globalThis.addEventListener?.('blur', update);
      destroyRef.onDestroy(() => {
        this.document.removeEventListener('visibilitychange', visible);
        globalThis.removeEventListener?.('focus', update);
        globalThis.removeEventListener?.('blur', update);
      });
      update();
    }

    // Marks the open conversation as read while the user looks at it.
    effect(() => {
      this.activeIdState();
      this.viewingState();
      this.pageVisibleState();
      this.conversationsState();
      this.sessionsState();
      untracked(() => this.markActiveReadWhenSeen());
    });
  }

  /** Evaluates the access policy and loads the server status once. Safe to call repeatedly. */
  initialize(): Promise<void> {
    this.initialization ??= this.startInitialization();
    return this.initialization;
  }

  /** Reloads the server status, for example after the host changed the signed-in user. */
  async reloadStatus(): Promise<void> {
    const revision = ++this.statusRevision;
    if (this.accessGrantedState() !== true) {
      this.resetAccount();
      this.statusState.set(null);
      this.selectedAgentIdState.set(null);
      this.restorePanelOpenState.set(false);
      this.uiState.deactivate();
      return;
    }

    this.statusLoadingState.set(true);
    try {
      const restored = await this.uiState.activate();
      if (revision !== this.statusRevision) {
        return;
      }
      if (restored.changed) {
        this.resetAccount();
        this.statusState.set(null);
        this.selectedAgentIdState.set(restored.state.agentId);
        this.restorePanelOpenState.set(restored.state.panelOpen);
      }

      const status = await firstValueFrom(this.api.status());
      if (revision !== this.statusRevision) {
        return;
      }
      this.statusState.set(status);
      if (status.enabled) {
        this.selectInitialAgent(status.agents);
        this.startCollaboration(status);
        if (this.activeIdState() === null && restored.state.conversationId) {
          await this.loadConversation(restored.state.conversationId, true);
        }
      }
    } catch {
      // An unavailable assistant endpoint hides the assistant instead of showing an error.
      if (revision === this.statusRevision) {
        this.statusState.set(null);
      }
    } finally {
      if (revision === this.statusRevision) {
        this.statusLoadingState.set(false);
      }
    }
  }

  async refreshConversations(): Promise<void> {
    if (!this.enabled()) {
      return;
    }

    const revision = this.accountRevision;
    this.conversationsLoadingState.set(true);
    try {
      const page = await firstValueFrom(this.api.listConversations(1, conversationPageSize));
      if (revision === this.accountRevision) {
        this.conversationsState.set(page.items.map(item => this.withLocalState(item)));
        this.conversationsTotalState.set(page.total);
      }
    } catch (error) {
      if (revision === this.accountRevision) {
        this.setError(this.activeIdState(), error);
      }
    } finally {
      if (revision === this.accountRevision) {
        this.conversationsLoadingState.set(false);
      }
    }
  }

  /** Chooses the agent for the next new conversation. Switching away from the open conversation's agent starts a new one. */
  selectAgent(agentId: string): void {
    if (!this.agents().some(agent => agent.id === agentId)) {
      return;
    }

    this.selectedAgentIdState.set(agentId);
    this.uiState.update({ agentId });
    const active = this.activeConversation();
    if (active && active.agentId !== agentId) {
      this.startNewConversation();
    }
  }

  /**
   * Leaves the open conversation; the next message creates a new one. Turns of other
   * conversations keep running in the background.
   */
  startNewConversation(): void {
    this.activeIdState.set(null);
    this.uiState.update({ conversationId: null });
    this.draftErrorState.set(null);
  }

  /** Opens a conversation; one that this tab already follows opens immediately. */
  async openConversation(conversationId: string): Promise<void> {
    await this.loadConversation(conversationId, false);
  }

  /** Tells the store whether the user can see the open thread, so it can mark it as read. */
  setViewing(viewing: boolean): void {
    this.viewingState.set(viewing);
  }

  async deleteConversation(conversationId: string): Promise<void> {
    if (this.sessionsState().get(conversationId)?.streaming) {
      return;
    }

    const revision = this.accountRevision;
    try {
      await firstValueFrom(this.api.deleteConversation(conversationId), { defaultValue: undefined });
      if (revision !== this.accountRevision) {
        return;
      }
      this.forget(conversationId);
    } catch (error) {
      if (revision === this.accountRevision) {
        this.setError(this.activeIdState(), error);
      }
    }
  }

  /**
   * Sends a user message. Creates a conversation with the selected agent when none is open,
   * shows the message optimistically and applies the streamed events. Other conversations keep
   * their own turns. Resolves `false` when the message was not accepted.
   */
  async send(text: string): Promise<boolean> {
    // Browsers only ask for notification permission during a user action such as this one.
    void this.push.onUserGesture().catch(() => undefined);
    return this.sendMessage(text);
  }

  /** Sends immediately when idle, otherwise appends to the conversation's FIFO queue. */
  async submit(text: string): Promise<boolean> {
    if (this.canSend() && this.queuedMessages().length === 0 && !this.queuePaused() && !this.submittingState()) {
      const revision = this.accountRevision;
      this.submittingState.set(true);
      try {
        return await this.send(text);
      } finally {
        if (revision === this.accountRevision && !this.destroyed) {
          this.submittingState.set(false);
        }
      }
    }
    const conversationId = this.activeIdState();
    const trimmed = text.trim();
    if (!conversationId || !this.canSubmit() || !trimmed || trimmed.length > (this.limits()?.maxMessageChars ?? Infinity)) {
      return false;
    }
    void this.push.onUserGesture().catch(() => undefined);
    const revision = this.accountRevision;
    const item: QueuedMessage = { id: createClientMessageId(), conversationId, text: trimmed, state: 'preparing' };
    this.queuedState.update(items => [...items, item]);
    const clientContext = await this.contextForMessage();
    if (revision !== this.accountRevision || this.destroyed) {
      return false;
    }
    this.queuedState.update(items => items.map(current => current.id === item.id
      ? { ...current, state: 'queued', clientContext } : current));
    return true;
  }

  /** Edits an unsent queued message without replacing the composer's current draft. */
  updateQueuedMessage(id: string, text: string): boolean {
    const trimmed = text.trim();
    if (!trimmed || trimmed.length > (this.limits()?.maxMessageChars ?? Infinity)
      || !this.queuedMessages().some(item => item.id === id && item.state !== 'sending')) {
      return false;
    }
    this.queuedState.update(items => items.map(item => item.id === id ? { ...item, text: trimmed } : item));
    return true;
  }

  removeQueuedMessage(id: string): void {
    this.queuedState.update(items => items.filter(item => item.id !== id
      || item.conversationId !== this.activeIdState() || item.state === 'sending'));
  }

  /** Pauses automatic dispatch while keeping all unsent messages available. */
  pauseQueue(): void {
    const conversationId = this.activeIdState();
    if (conversationId) {
      this.pausedQueuesState.update(paused => new Set([...paused, conversationId]));
    }
  }

  /** Explicitly resumes after Stop or a failure; approval decisions remain separate. */
  resumeQueue(): void {
    const conversationId = this.activeIdState();
    if (!conversationId) {
      return;
    }
    this.pausedQueuesState.update(paused => new Set([...paused].filter(id => id !== conversationId)));
    this.patchSession(conversationId, session => ({ ...session, error: null }));
    this.queuedState.update(items => items.map(item => item.conversationId === conversationId && item.state === 'failed'
      ? { ...item, state: 'queued' } : item));
  }

  /** Prioritizes a queued correction and waits for confirmed cancellation before dispatch. */
  async steerQueuedMessage(id: string): Promise<void> {
    const item = this.queuedState().find(message => message.id === id && message.conversationId === this.activeIdState());
    if (!item || item.state !== 'queued' || !this.canSteer()) {
      return;
    }
    this.steeringState.set(id);
    this.queuedState.update(items => [item, ...items.filter(message => message.id !== id)]);
    this.resumeQueue();
    try {
      await this.cancelConversation(item.conversationId);
    } finally {
      if (this.steeringState() === id && this.sessionsState().get(item.conversationId)?.error) {
        this.steeringState.set(null);
      }
    }
  }

  private async dispatchQueued(item: QueuedMessage): Promise<void> {
    const revision = this.accountRevision;
    this.queueDispatchState.set(item.id);
    this.queuedState.update(items => items.map(current => current.id === item.id ? { ...current, state: 'sending' } : current));
    const accepted = await this.sendMessage(item.text, item);
    if (revision !== this.accountRevision || this.destroyed) {
      return;
    }
    this.queuedState.update(items => accepted ? items.filter(current => current.id !== item.id)
      : items.map(current => current.id === item.id ? { ...current, state: 'failed' } : current));
    if (!accepted) {
      this.pausedQueuesState.update(paused => new Set([...paused, item.conversationId]));
    }
    this.queueDispatchState.set(null);
  }

  private async sendMessage(text: string, queued?: QueuedMessage): Promise<boolean> {
    const trimmed = text.trim();
    const accountRevision = this.accountRevision;
    await this.initialize();
    await this.reloadStatus();
    if (accountRevision !== this.accountRevision || this.destroyed) {
      return false;
    }
    let conversation = queued ? this.sessionsState().get(queued.conversationId)?.conversation ?? null : this.activeConversation();
    const session = queued ? this.sessionsState().get(queued.conversationId) : null;
    const available = queued ? this.enabled() && !this.pausedQueuesState().has(queued.conversationId)
      && conversation?.status === 'idle' && !session?.streaming
      && !session?.deciding && this.agents().some(agent => agent.id === conversation?.agentId) : this.canSend();
    const maxChars = this.limits()?.maxMessageChars ?? Number.MAX_SAFE_INTEGER;
    if (trimmed.length === 0 || trimmed.length > maxChars || !available) {
      return false;
    }

    this.sendingState.set(conversation?.id ?? newConversationKey);
    if (!queued) {
      this.restoredDraftState.set(null);
    }
    this.draftErrorState.set(null);

    if (!conversation) {
      conversation = await this.createConversation();
      if (!conversation) {
        if (accountRevision === this.accountRevision) {
          this.sendingState.set(null);
          this.restoredDraftState.set(text);
        }
        return false;
      }
      this.sendingState.set(conversation.id);
    }

    const clientContext = queued ? queued.clientContext : await this.contextForMessage();
    if (accountRevision !== this.accountRevision) {
      return false;
    }
    const conversationId = conversation.id;
    const clientMessageId = createClientMessageId();
    const previousStatus = conversation.status;
    this.patchSession(conversationId, session => ({
      ...session,
      error: null,
      notice: null,
      conversation: {
        ...session.conversation,
        status: 'running',
        messages: [
          ...session.conversation.messages,
          {
            id: clientMessageId,
            role: 'user',
            createdAt: new Date().toISOString(),
            parts: [{ type: 'text', text: trimmed }],
            authorActorId: session.conversation.currentActorId ?? null
          }
        ]
      }
    }));
    this.patchSummary(conversationId, { status: 'running', updatedAt: new Date().toISOString() });

    return new Promise<boolean>(resolve => {
      this.pendingSendResolves.set(conversationId, resolve);
      let started = false;
      let failedAfterStart = false;

      this.runStream(conversationId, this.api.sendMessage(conversationId, {
        text: trimmed,
        clientMessageId,
        ...(clientContext === undefined ? {} : { clientContext })
      }), {
        next: event => {
          if (event.type === 'error' && !started) {
            this.removeMessage(conversationId, clientMessageId, previousStatus);
            if (!queued && this.activeIdState() === conversationId) {
              this.restoredDraftState.set(text);
            }
            this.patchSession(conversationId, session => ({ ...session, error: event.data }));
            resolve(false);
            return;
          }

          if (event.type === 'turn.started') {
            started = true;
            resolve(true);
          }
          if (event.type === 'error') {
            failedAfterStart = true;
          }

          this.applyEvent(conversationId, event, clientMessageId);
        },
        complete: () => {
          this.pendingSendResolves.delete(conversationId);
          resolve(started);
          this.afterTurn(conversationId, failedAfterStart);
        }
      });
      this.sendingState.set(null);
    });
  }

  /** Approves or rejects the pending approval. Ignores repeated calls while a decision is in flight. */
  decide(decision: ApprovalDecision, reason?: string): void {
    const session = this.activeSession();
    const conversation = session?.conversation;
    const approval = conversation?.pendingApproval ?? null;
    if (!session || !conversation || !approval || session.deciding || session.streaming || !this.canDecide()) {
      return;
    }

    const conversationId = conversation.id;
    this.patchSession(conversationId, current => ({ ...current, deciding: true, error: null, notice: null }));

    let accepted = false;
    let failed = false;
    const request = { decision, expectedProposalHash: approval.proposalHash, ...(reason ? { reason } : {}) };

    this.runStream(conversationId, this.api.decideApproval(conversationId, approval.approvalId, request), {
      next: event => {
        if (!accepted && event.type !== 'error') {
          accepted = true;
          this.patchSession(conversationId, current => ({
            ...current,
            conversation: applyNhAssistantApprovalDecision(current.conversation, approval.approvalId, decision)
          }));
        }
        if (event.type === 'error') {
          failed = true;
          if (!accepted) {
            this.patchSession(conversationId, current => ({ ...current, error: event.data }));
            return;
          }
        }

        this.applyEvent(conversationId, event);
      },
      complete: () => {
        this.patchSession(conversationId, current => ({ ...current, deciding: false }));
        this.afterTurn(conversationId, failed);
      }
    });
  }

  /** Asks the server to stop the running turn or to abandon a waiting approval of the open conversation. */
  async cancel(): Promise<void> {
    const conversationId = this.activeIdState();
    if (conversationId) {
      this.pauseQueue();
      await this.cancelConversation(conversationId);
    }
  }

  private async cancelConversation(conversationId: string): Promise<void> {
    const session = this.sessionsState().get(conversationId);
    const conversation = session?.conversation;
    if (!session || !conversation || (conversation.status === 'idle' && !session.streaming)) {
      return;
    }

    const revision = this.accountRevision;
    try {
      await firstValueFrom(this.api.cancel(conversationId), { defaultValue: undefined });
    } catch (error) {
      if (revision === this.accountRevision) {
        this.setError(conversationId, error);
      }
      return;
    }

    if (revision !== this.accountRevision) {
      return;
    }

    if (!this.sessionsState().get(conversationId)?.streaming) {
      await this.reloadSession(conversationId);
      return;
    }

    // The server ends the stream with turn.completed(cancelled); stop waiting if it does not.
    clearTimeout(this.cancelTimers.get(conversationId));
    this.cancelTimers.set(conversationId, setTimeout(() => {
      this.cancelTimers.delete(conversationId);
      if (this.sessionsState().get(conversationId)?.streaming) {
        this.streams.get(conversationId)?.unsubscribe();
        this.streams.delete(conversationId);
        this.patchSession(conversationId, current => ({ ...current, streaming: false, deciding: false }));
        void this.reloadSession(conversationId);
      }
    }, cancelGracePeriodMs));
  }

  /** Joins a shared conversation with an invitation link and opens it. Resolves `false` on failure. */
  async joinConversation(conversationId: string, token: string): Promise<boolean> {
    await this.initialize();
    const revision = this.accountRevision;
    try {
      const conversation = await firstValueFrom(this.api.joinConversation(conversationId, token));
      if (revision !== this.accountRevision) {
        return false;
      }
      this.upsertSession(conversation);
      this.setActive(conversation.id, conversation.agentId);
      void this.refreshConversations();
      return true;
    } catch (error) {
      if (revision === this.accountRevision) {
        this.startNewConversation();
        this.setError(null, error);
      }
      return false;
    }
  }

  /** Owner only: creates a new invitation link for the open conversation and returns its token. */
  async createShareLink(): Promise<string | null> {
    const conversationId = this.activeIdState();
    if (!conversationId) {
      return null;
    }
    try {
      const link = await firstValueFrom(this.api.createShareLink(conversationId));
      this.patchSession(conversationId, session => ({
        ...session,
        conversation: { ...session.conversation, shareToken: link.token }
      }));
      return link.token;
    } catch (error) {
      this.setError(conversationId, error);
      return null;
    }
  }

  /** Owner only: the invitation link of the open conversation stops working. */
  async revokeShareLink(): Promise<boolean> {
    const conversationId = this.activeIdState();
    if (!conversationId) {
      return false;
    }
    try {
      await firstValueFrom(this.api.revokeShareLink(conversationId), { defaultValue: undefined });
      this.patchSession(conversationId, session => ({
        ...session,
        conversation: { ...session.conversation, shareToken: null }
      }));
      return true;
    } catch (error) {
      this.setError(conversationId, error);
      return false;
    }
  }

  /** Owner only: searches the application's directory for people to invite to the open conversation. */
  async searchParticipantCandidates(query: string): Promise<DirectoryEntry[]> {
    const conversationId = this.activeIdState();
    if (!conversationId || query.trim().length < 2) {
      return [];
    }
    try {
      return await firstValueFrom(this.api.searchParticipantCandidates(conversationId, query.trim()));
    } catch (error) {
      this.setError(conversationId, error);
      return [];
    }
  }

  /** Owner only: invites a person from the directory to the open conversation. */
  async inviteParticipant(actorId: string): Promise<boolean> {
    const conversationId = this.activeIdState();
    if (!conversationId) {
      return false;
    }
    try {
      await firstValueFrom(this.api.inviteParticipant(conversationId, actorId), { defaultValue: undefined });
      await this.reloadSession(conversationId);
      return true;
    } catch (error) {
      this.setError(conversationId, error);
      return false;
    }
  }

  /**
   * The owner removes a participant of the open conversation; with the current user's own id a
   * participant leaves it.
   */
  async removeParticipant(actorId: string): Promise<boolean> {
    const conversation = this.activeConversation();
    if (!conversation) {
      return false;
    }
    try {
      await firstValueFrom(this.api.removeParticipant(conversation.id, actorId), { defaultValue: undefined });
      if (actorId === conversation.currentActorId) {
        this.forget(conversation.id);
      } else {
        await this.reloadSession(conversation.id);
      }
      return true;
    } catch (error) {
      this.setError(conversation.id, error);
      return false;
    }
  }

  clearError(): void {
    const activeId = this.activeIdState();
    if (activeId === null) {
      this.draftErrorState.set(null);
      return;
    }
    this.patchSession(activeId, session => ({ ...session, error: null }));
  }

  clearNotice(): void {
    const activeId = this.activeIdState();
    if (activeId !== null) {
      this.patchSession(activeId, session => ({ ...session, notice: null }));
    }
  }

  /** Persists only drawer visibility, never the page context or a message draft. */
  setPanelOpen(open: boolean): void {
    this.restorePanelOpenState.set(open);
    this.uiState.update({ panelOpen: open });
  }

  /** Reads the host's page context again so the chip shows what the next message sends. */
  async refreshPageContext(): Promise<void> {
    const revision = this.accountRevision;
    const context = await this.readPageContext();
    if (revision === this.accountRevision) {
      this.pageContextState.set(context ?? null);
    }
  }

  /** Leaves the page context out of the next message only, or includes it again. */
  setPageContextExcluded(excluded: boolean): void {
    this.pageContextExcludedState.set(excluded);
  }

  /** Returns and clears the draft of a message that was not accepted. */
  consumeRestoredDraft(): string | null {
    const draft = this.restoredDraftState();
    this.restoredDraftState.set(null);
    return draft;
  }

  private async loadConversation(conversationId: string, restoring: boolean): Promise<void> {
    if (this.sessionsState().has(conversationId)) {
      const session = this.sessionsState().get(conversationId)!;
      this.setActive(conversationId, session.conversation.agentId);
      return;
    }
    if (this.activeIdState() === conversationId) {
      return;
    }

    const revision = this.accountRevision;
    this.conversationLoadingState.set(true);
    this.draftErrorState.set(null);
    try {
      const conversation = await firstValueFrom(this.api.getConversation(conversationId));
      if (revision !== this.accountRevision) {
        return;
      }
      if (!this.agents().some(agent => agent.id === conversation.agentId)) {
        this.uiState.update({ conversationId: null });
        return;
      }
      this.upsertSession(conversation);
      this.setActive(conversationId, conversation.agentId);
    } catch (error) {
      if (revision !== this.accountRevision) {
        return;
      }
      if (error instanceof NhAssistantApiError && (error.status === 403 || error.status === 404)) {
        this.uiState.update({ conversationId: null });
      }
      if (!restoring) {
        this.setError(null, error);
      }
    } finally {
      if (revision === this.accountRevision) {
        this.conversationLoadingState.set(false);
      }
    }
  }

  private setActive(conversationId: string | null, agentId?: string): void {
    this.activeIdState.set(conversationId);
    if (agentId) {
      this.selectedAgentIdState.set(agentId);
    }
    this.uiState.update(agentId ? { conversationId, agentId } : { conversationId });
  }

  /**
   * Page context for one message: `undefined` omits the field (no getter, getter failed or
   * invalid shape), `null` sends an explicit "none" (no context or left out by the user).
   */
  private async contextForMessage(): Promise<ClientContext | null | undefined> {
    if (!this.config.getPageContext) {
      return undefined;
    }

    if (this.pageContextExcludedState()) {
      this.pageContextExcludedState.set(false);
      return null;
    }

    const revision = this.accountRevision;
    const context = await this.readPageContext();
    if (revision === this.accountRevision) {
      this.pageContextState.set(context ?? null);
    }
    return context;
  }

  private async readPageContext(): Promise<ClientContext | null | undefined> {
    const getter = this.config.getPageContext;
    if (!getter) {
      return undefined;
    }

    try {
      const value = await runInInjectionContext(this.injector, () => getter());
      if (value === null || value === undefined) {
        return null;
      }
      return normalizeNhAssistantClientContext(value) ?? undefined;
    } catch {
      // A failing host getter never blocks sending; the message goes without page context.
      return undefined;
    }
  }

  private async startInitialization(): Promise<void> {
    const decision = this.accessPolicy.canUse();
    if (!isObservable(decision)) {
      this.accessGrantedState.set(decision);
      await this.reloadStatus();
      return;
    }

    await new Promise<void>(resolve => {
      this.accessSubscription = (decision as Observable<boolean>).subscribe({
        next: granted => {
          this.accessGrantedState.set(granted);
          void this.reloadStatus().finally(() => resolve());
        },
        error: () => {
          this.accessGrantedState.set(false);
          resolve();
        },
        complete: () => resolve()
      });
    });
  }

  /** Connects live updates and notifications once per account and server configuration. */
  private startCollaboration(status: AssistantStatus): void {
    const collaboration = status.collaboration ?? null;
    const key = `${this.accountRevision}|${collaboration?.hubPath ?? ''}|${collaboration?.push === true}`;
    if (key === this.collaborationKey) {
      return;
    }
    const accountChanged = this.collaborationKey?.split('|')[0] !== String(this.accountRevision);
    this.collaborationKey = key;
    void this.live.connect(collaboration?.hubPath ?? null, accountChanged);
    void this.push.activate(collaboration?.push === true);
  }

  /** Forgets everything about the previous account: turns, conversations, live updates and push. */
  private resetAccount(): void {
    this.accountRevision++;
    this.collaborationKey = null;
    this.stopAllTurns();
    this.sessionsState.set(new Map());
    this.activeIdState.set(null);
    this.conversationsState.set([]);
    this.conversationsTotalState.set(0);
    this.conversationLoadingState.set(false);
    this.conversationsLoadingState.set(false);
    this.pageContextState.set(null);
    this.pageContextExcludedState.set(false);
    this.restoredDraftState.set(null);
    this.draftErrorState.set(null);
    this.sendingState.set(null);
    this.queuedState.set([]);
    this.pausedQueuesState.set(new Set());
    this.queueDispatchState.set(null);
    this.steeringState.set(null);
    this.submittingState.set(false);
    void this.live.stop();
    void this.push.deactivate().catch(() => undefined);
  }

  private async createConversation(): Promise<Conversation | null> {
    const agentId = this.selectedAgentIdState();
    if (!agentId) {
      return null;
    }

    const revision = this.accountRevision;
    try {
      const conversation = await firstValueFrom(this.api.createConversation({ agentId }));
      if (revision !== this.accountRevision) {
        return null;
      }
      this.upsertSession(conversation);
      this.setActive(conversation.id);
      this.conversationsState.update(items => [toSummary(conversation), ...items.filter(item => item.id !== conversation.id)]);
      this.conversationsTotalState.update(total => total + 1);
      return conversation;
    } catch (error) {
      if (revision === this.accountRevision) {
        this.setError(null, error);
      }
      return null;
    }
  }

  private runStream(
    conversationId: string,
    stream: Observable<NhAssistantSseEvent>,
    handlers: { next: (event: NhAssistantSseEvent) => void; complete: () => void }
  ): void {
    this.streams.get(conversationId)?.unsubscribe();
    this.patchSession(conversationId, session => ({ ...session, streaming: true }));

    const finish = () => {
      clearTimeout(this.cancelTimers.get(conversationId));
      this.cancelTimers.delete(conversationId);
      this.streams.delete(conversationId);
      this.patchSession(conversationId, session => ({ ...session, streaming: false }));
      handlers.complete();
    };

    const subscription = stream.subscribe({
      next: event => handlers.next(event),
      error: () => {
        this.patchSession(conversationId, session => ({ ...session, error: clientError(NhAssistantClientErrorCodes.network) }));
        finish();
      },
      complete: finish
    });
    if (!subscription.closed) {
      this.streams.set(conversationId, subscription);
    }
  }

  private stopAllTurns(): void {
    for (const subscription of this.streams.values()) {
      subscription.unsubscribe();
    }
    this.streams.clear();
    for (const resolve of this.pendingSendResolves.values()) {
      resolve(false);
    }
    this.pendingSendResolves.clear();
    for (const timer of this.cancelTimers.values()) {
      clearTimeout(timer);
    }
    this.cancelTimers.clear();
  }

  private applyEvent(conversationId: string, event: NhAssistantSseEvent, clientMessageId?: string): void {
    this.patchSession(conversationId, session => {
      const conversation = applyNhAssistantEvent(session.conversation, event, clientMessageId);
      let { error, notice, lastUsage } = session;
      if (event.type === 'error') {
        error = event.data;
      }
      if (event.type === 'turn.completed') {
        lastUsage = event.data.usage;
        if (event.data.status === 'failed') {
          error = clientError(event.data.errorCode ?? NhAssistantClientErrorCodes.server);
        } else if (event.data.status === 'completed') {
          notice = completionNotice(conversation, event.data.errorCode);
        }
      }
      return { ...session, conversation, error, notice, lastUsage };
    });
  }

  private afterTurn(conversationId: string, reload: boolean): void {
    const session = this.sessionsState().get(conversationId);
    if (session) {
      const status = session.conversation.status;
      this.patchSummary(conversationId, summary => ({
        ...summary,
        status,
        updatedAt: new Date().toISOString(),
        activeActorId: status === 'waiting-for-approval' ? summary.activeActorId ?? session.conversation.currentActorId ?? null : null
      }));
    }

    if (reload) {
      void this.reloadSession(conversationId);
    }
    // The list carries the exact title, latest message and read position of the finished turn.
    void this.refreshConversations();
  }

  private async reloadSession(conversationId: string): Promise<void> {
    const revision = this.accountRevision;
    try {
      const conversation = await firstValueFrom(this.api.getConversation(conversationId));
      const session = this.sessionsState().get(conversationId);
      if (revision === this.accountRevision && session && !session.streaming) {
        this.upsertSession(conversation);
      }
    } catch (error) {
      if (error instanceof NhAssistantApiError && (error.status === 403 || error.status === 404)) {
        this.forget(conversationId);
      }
      // Otherwise keep the local state; the error of the turn is already shown.
    }
  }

  private markActiveReadWhenSeen(): void {
    const conversationId = this.activeIdState();
    if (!conversationId || !this.viewingState() || !this.pageVisibleState() || this.markingRead.has(conversationId)) {
      return;
    }
    const session = this.sessionsState().get(conversationId);
    const summary = this.conversationsState().find(item => item.id === conversationId);
    if (!session || session.streaming || !summary || !nhAssistantIsUnread(summary)) {
      return;
    }

    this.markingRead.add(conversationId);
    const revision = this.accountRevision;
    let request: Observable<void>;
    try {
      request = this.api.markRead(conversationId);
    } catch {
      this.markingRead.delete(conversationId);
      return;
    }
    firstValueFrom(request, { defaultValue: undefined })
      .then(() => {
        if (revision === this.accountRevision) {
          this.patchSummary(conversationId, item => ({ ...item, lastReadSequence: item.lastMessageSequence ?? item.lastReadSequence }));
        }
      })
      .catch(() => undefined)
      .finally(() => this.markingRead.delete(conversationId));
  }

  private onLiveChanged(change: LiveConversationChanged): void {
    const known = this.conversationsState().some(item => item.id === change.conversationId);
    if (!known) {
      this.scheduleRefresh();
    } else {
      this.patchSummary(change.conversationId, summary => ({
        ...summary,
        status: change.status,
        title: change.title,
        updatedAt: change.updatedAt,
        activeActorId: change.activeActorId,
        lastMessageSequence: Math.max(summary.lastMessageSequence ?? 0, change.lastMessageSequence),
        participantCount: change.participantCount
      }));
    }

    const session = this.sessionsState().get(change.conversationId);
    if (!session || session.streaming) {
      return;
    }
    const wasRunning = session.conversation.status === 'running';
    this.patchSession(change.conversationId, current => ({
      ...current,
      conversation: applyNhAssistantConversationChange(current.conversation, change)
    }));
    if (wasRunning && change.status !== 'running') {
      // Another session ended its turn; the snapshot holds the exact answer and approvals.
      void this.reloadSession(change.conversationId);
    } else if (session.conversation.participantCount !== change.participantCount) {
      void this.reloadSession(change.conversationId);
    }
  }

  private onLiveRead(read: LiveConversationRead): void {
    this.patchSummary(read.conversationId, summary => ({
      ...summary,
      lastReadSequence: Math.max(summary.lastReadSequence ?? 0, read.lastReadSequence)
    }));
  }

  private onLiveRemoved(removed: LiveConversationRemoved): void {
    const wasActive = this.activeIdState() === removed.conversationId;
    this.forget(removed.conversationId);
    if (wasActive) {
      this.draftErrorState.set(clientError(NhAssistantClientErrorCodes.conversationRemoved));
    }
  }

  private onLiveEvent(event: LiveConversationEvent): void {
    const session = this.sessionsState().get(event.conversationId);
    if (event.type === 'turn.started') {
      this.patchSummary(event.conversationId, summary => ({ ...summary, status: 'running', activeActorId: event.actorId }));
    }
    if (!session || session.streaming) {
      // This tab streams the turn itself, or does not show the conversation.
      return;
    }
    this.patchSession(event.conversationId, current => ({
      ...current,
      conversation: applyNhAssistantLiveEvent(current.conversation, event)
    }));
  }

  /** After a reconnect: updates sent while disconnected are lost, so reload what is shown. */
  private resync(): void {
    if (!this.enabled()) {
      return;
    }
    void this.refreshConversations();
    for (const [conversationId, session] of this.sessionsState()) {
      if (!session.streaming) {
        void this.reloadSession(conversationId);
      }
    }
  }

  private scheduleRefresh(): void {
    clearTimeout(this.refreshTimer);
    this.refreshTimer = setTimeout(() => void this.refreshConversations(), refreshDelayMs);
  }

  /** Stores a snapshot, keeping at most `maxSessions` idle conversations in memory. */
  private upsertSession(conversation: Conversation): void {
    this.sessionsState.update(sessions => {
      const next = new Map(sessions);
      const existing = next.get(conversation.id);
      next.set(conversation.id, existing
        ? { ...existing, conversation }
        : { conversation, streaming: false, deciding: false, error: null, notice: null, lastUsage: null });
      if (next.size > maxSessions) {
        const activeId = this.activeIdState();
        for (const [id, session] of next) {
          if (next.size <= maxSessions) {
            break;
          }
          if (id !== activeId && id !== conversation.id && !session.streaming
            && !this.queuedState().some(item => item.conversationId === id)) {
            next.delete(id);
          }
        }
      }
      return next;
    });
    this.patchSummary(conversation.id, summary => ({
      ...summary,
      title: conversation.title,
      status: conversation.status,
      updatedAt: conversation.updatedAt,
      ...(conversation.lastMessageSequence === undefined ? {} : { lastMessageSequence: conversation.lastMessageSequence }),
      ...(conversation.lastReadSequence === undefined ? {} : { lastReadSequence: conversation.lastReadSequence }),
      ...(conversation.activeActorId === undefined ? {} : { activeActorId: conversation.activeActorId }),
      ...(conversation.members === undefined ? {} : { participantCount: Math.max(0, conversation.members.length - 1) })
    }));
  }

  private patchSession(conversationId: string, update: (session: NhAssistantSession) => NhAssistantSession): void {
    const session = this.sessionsState().get(conversationId);
    if (!session) {
      return;
    }
    this.sessionsState.update(sessions => new Map(sessions).set(conversationId, update(session)));
  }

  private patchSummary(
    conversationId: string,
    update: Partial<ConversationSummary> | ((summary: ConversationSummary) => ConversationSummary)
  ): void {
    this.conversationsState.update(items => items.map(item => {
      if (item.id !== conversationId) {
        return item;
      }
      return typeof update === 'function' ? update(item) : { ...item, ...update };
    }));
  }

  /**
   * Keeps local knowledge when the list is reloaded: the status of turns this tab streams, and a
   * read position the server already confirmed, which a list response sent earlier may not show.
   */
  private withLocalState(summary: ConversationSummary): ConversationSummary {
    const session = this.sessionsState().get(summary.id);
    const known = this.conversationsState().find(item => item.id === summary.id)?.lastReadSequence;
    const lastReadSequence = known !== undefined && summary.lastReadSequence !== undefined
      ? Math.max(known, summary.lastReadSequence)
      : summary.lastReadSequence;
    return {
      ...summary,
      lastReadSequence,
      status: session?.streaming ? session.conversation.status : summary.status
    };
  }

  /** Removes a deleted, left or inaccessible conversation everywhere. */
  private forget(conversationId: string): void {
    this.queuedState.update(items => items.filter(item => item.conversationId !== conversationId));
    this.pausedQueuesState.update(paused => new Set([...paused].filter(id => id !== conversationId)));
    this.streams.get(conversationId)?.unsubscribe();
    this.streams.delete(conversationId);
    this.sessionsState.update(sessions => {
      const next = new Map(sessions);
      next.delete(conversationId);
      return next;
    });
    const before = this.conversationsState().length;
    this.conversationsState.update(items => items.filter(item => item.id !== conversationId));
    if (this.conversationsState().length < before) {
      this.conversationsTotalState.update(total => Math.max(0, total - 1));
    }
    if (this.activeIdState() === conversationId) {
      this.activeIdState.set(null);
      this.uiState.update({ conversationId: null });
    }
  }

  private removeMessage(conversationId: string, messageId: string, status: Conversation['status']): void {
    this.patchSession(conversationId, session => ({
      ...session,
      conversation: {
        ...session.conversation,
        status,
        messages: session.conversation.messages.filter(message => message.id !== messageId)
      }
    }));
    this.patchSummary(conversationId, { status });
  }

  private selectInitialAgent(agents: AgentSummary[]): void {
    const current = this.selectedAgentIdState();
    if (current && agents.some(agent => agent.id === current)) {
      return;
    }

    const preferred = agents.find(agent => agent.id === this.config.defaultAgentId) ?? agents[0] ?? null;
    this.selectedAgentIdState.set(preferred?.id ?? null);
    this.uiState.update({ agentId: preferred?.id ?? null });
  }

  /** Shows an error on a conversation, or on the new-conversation view for `null`. */
  private setError(conversationId: string | null, error: unknown): void {
    const value = error instanceof NhAssistantApiError
      ? { code: error.code, messageKey: error.messageKey }
      : clientError(NhAssistantClientErrorCodes.server);
    if (conversationId === null || !this.sessionsState().has(conversationId)) {
      this.draftErrorState.set(value);
      return;
    }
    this.patchSession(conversationId, session => ({ ...session, error: value }));
  }
}

function clientError(code: string): NhAssistantError {
  return { code, messageKey: nhAssistantErrorMessageKey(code) };
}

/** A completed turn with a server code, or without any answer text, explains itself with a notice. */
function completionNotice(conversation: Conversation, errorCode: string | null): NhAssistantNotice | null {
  if (errorCode) {
    return clientError(errorCode);
  }
  if (!nhAssistantLatestTurnHasText(conversation)) {
    return { code: NH_ASSISTANT_NO_ANSWER_NOTICE_CODE, messageKey: 'nh-assistant.notices.no-answer' };
  }

  return null;
}

function toSummary(conversation: Conversation): ConversationSummary {
  return {
    id: conversation.id,
    agentId: conversation.agentId,
    title: conversation.title,
    status: conversation.status,
    createdAt: conversation.createdAt,
    updatedAt: conversation.updatedAt,
    role: conversation.role,
    participantCount: conversation.members ? Math.max(0, conversation.members.length - 1) : conversation.participantCount,
    lastMessageSequence: conversation.lastMessageSequence,
    lastReadSequence: conversation.lastReadSequence,
    activeActorId: conversation.activeActorId
  };
}

function createClientMessageId(): string {
  const cryptoApi = globalThis.crypto;
  if (cryptoApi?.randomUUID) {
    return cryptoApi.randomUUID();
  }

  const bytes = new Uint8Array(16);
  if (cryptoApi?.getRandomValues) {
    cryptoApi.getRandomValues(bytes);
  } else {
    for (let index = 0; index < bytes.length; index++) {
      bytes[index] = Math.floor(Math.random() * 256);
    }
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
