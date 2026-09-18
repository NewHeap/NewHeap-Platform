import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { Observable, Subscription, firstValueFrom, isObservable } from 'rxjs';
import {
  AgentSummary,
  ApprovalDecision,
  AssistantStatus,
  Conversation,
  ConversationSummary
} from '../models/assistant-api.models';
import { NhAssistantClientErrorCodes, NhAssistantSseEvent, TurnUsage } from '../models/assistant-sse.models';
import { NH_ASSISTANT_ACCESS_POLICY, NH_ASSISTANT_CONFIG } from '../nh-assistant.config';
import { NhAssistantApiError, NhAssistantApiService, nhAssistantErrorMessageKey } from './nh-assistant-api.service';
import { applyNhAssistantApprovalDecision, applyNhAssistantEvent } from './nh-assistant-reducer';

/** A user-facing assistant failure: a stable code and a translation key, never raw server text. */
export interface NhAssistantError {
  code: string;
  messageKey: string;
}

const conversationPageSize = 50;
const cancelGracePeriodMs = 5_000;

/**
 * Signal-based state of one assistant scope: availability, agents, the conversation list
 * and the active conversation with its streamed turn.
 */
@Injectable()
export class NhAssistantStore {
  private readonly api = inject(NhAssistantApiService);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly accessPolicy = inject(NH_ASSISTANT_ACCESS_POLICY);

  private readonly accessGrantedState = signal<boolean | null>(null);
  private readonly statusState = signal<AssistantStatus | null>(null);
  private readonly statusLoadingState = signal(false);
  private readonly selectedAgentIdState = signal<string | null>(null);
  private readonly conversationsState = signal<ConversationSummary[]>([]);
  private readonly conversationsTotalState = signal(0);
  private readonly conversationsLoadingState = signal(false);
  private readonly activeConversationState = signal<Conversation | null>(null);
  private readonly conversationLoadingState = signal(false);
  private readonly streamingState = signal(false);
  private readonly decidingState = signal(false);
  private readonly errorState = signal<NhAssistantError | null>(null);
  private readonly lastUsageState = signal<TurnUsage | null>(null);
  private readonly restoredDraftState = signal<string | null>(null);

  private accessSubscription?: Subscription;
  private streamSubscription?: Subscription;
  private cancelTimer?: ReturnType<typeof setTimeout>;
  private initialization?: Promise<void>;

  /** True when the access policy allows the user and the server reports the assistant as enabled. */
  readonly enabled = computed(() => this.accessGrantedState() === true && this.statusState()?.enabled === true);
  readonly accessGranted = this.accessGrantedState.asReadonly();
  /** True when the assistant is enabled and the server reports that the caller passes the admin policy. */
  readonly canAdminister = computed(() => this.enabled() && this.statusState()?.canAdminister === true);
  readonly statusLoading = this.statusLoadingState.asReadonly();
  readonly limits = computed(() => this.statusState()?.limits ?? null);
  readonly agents = computed<AgentSummary[]>(() => this.statusState()?.agents ?? []);
  readonly selectedAgentId = this.selectedAgentIdState.asReadonly();
  readonly selectedAgent = computed(() =>
    this.agents().find(agent => agent.id === this.selectedAgentIdState()) ?? null
  );
  readonly conversations = this.conversationsState.asReadonly();
  readonly conversationsTotal = this.conversationsTotalState.asReadonly();
  readonly conversationsLoading = this.conversationsLoadingState.asReadonly();
  readonly activeConversation = this.activeConversationState.asReadonly();
  readonly conversationLoading = this.conversationLoadingState.asReadonly();
  readonly streaming = this.streamingState.asReadonly();
  readonly pendingApproval = computed(() => this.activeConversationState()?.pendingApproval ?? null);
  readonly deciding = this.decidingState.asReadonly();
  readonly error = this.errorState.asReadonly();
  readonly lastUsage = this.lastUsageState.asReadonly();
  /** Text of a message the server did not accept, so the composer can offer it again. */
  readonly restoredDraft = this.restoredDraftState.asReadonly();
  /** True while the user can send: enabled, an agent is chosen and no turn runs or waits. */
  readonly canSend = computed(() => {
    const conversation = this.activeConversationState();
    const blocked = conversation !== null && conversation.status !== 'idle';
    return this.enabled() && this.selectedAgentIdState() !== null && !this.streamingState() && !blocked;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.accessSubscription?.unsubscribe();
      this.streamSubscription?.unsubscribe();
      clearTimeout(this.cancelTimer);
    });
  }

  /** Evaluates the access policy and loads the server status once. Safe to call repeatedly. */
  initialize(): Promise<void> {
    this.initialization ??= this.startInitialization();
    return this.initialization;
  }

  /** Reloads the server status, for example after the host changed the signed-in user. */
  async reloadStatus(): Promise<void> {
    if (this.accessGrantedState() !== true) {
      this.statusState.set(null);
      return;
    }

    this.statusLoadingState.set(true);
    try {
      const status = await firstValueFrom(this.api.status());
      this.statusState.set(status);
      this.selectInitialAgent(status.agents);
    } catch {
      // An unavailable assistant endpoint hides the assistant instead of showing an error.
      this.statusState.set(null);
    } finally {
      this.statusLoadingState.set(false);
    }
  }

  async refreshConversations(): Promise<void> {
    if (!this.enabled()) {
      return;
    }

    this.conversationsLoadingState.set(true);
    try {
      const page = await firstValueFrom(this.api.listConversations(1, conversationPageSize));
      this.conversationsState.set(page.items);
      this.conversationsTotalState.set(page.total);
    } catch (error) {
      this.setError(error);
    } finally {
      this.conversationsLoadingState.set(false);
    }
  }

  /** Chooses the agent for the next new conversation. Switching away from the active conversation's agent starts a new one. */
  selectAgent(agentId: string): void {
    if (!this.agents().some(agent => agent.id === agentId)) {
      return;
    }

    this.selectedAgentIdState.set(agentId);
    const active = this.activeConversationState();
    if (active && active.agentId !== agentId && !this.streamingState()) {
      this.startNewConversation();
    }
  }

  /** Leaves the active conversation; the next message creates a new one. */
  startNewConversation(): void {
    if (this.streamingState()) {
      return;
    }

    this.activeConversationState.set(null);
    this.errorState.set(null);
    this.lastUsageState.set(null);
  }

  async openConversation(conversationId: string): Promise<void> {
    if (this.streamingState() || this.activeConversationState()?.id === conversationId) {
      return;
    }

    this.conversationLoadingState.set(true);
    this.errorState.set(null);
    try {
      const conversation = await firstValueFrom(this.api.getConversation(conversationId));
      this.activeConversationState.set(conversation);
      if (this.agents().some(agent => agent.id === conversation.agentId)) {
        this.selectedAgentIdState.set(conversation.agentId);
      }
    } catch (error) {
      this.setError(error);
    } finally {
      this.conversationLoadingState.set(false);
    }
  }

  async deleteConversation(conversationId: string): Promise<void> {
    if (this.streamingState() && this.activeConversationState()?.id === conversationId) {
      return;
    }

    try {
      await firstValueFrom(this.api.deleteConversation(conversationId), { defaultValue: undefined });
      this.conversationsState.update(items => items.filter(item => item.id !== conversationId));
      this.conversationsTotalState.update(total => Math.max(0, total - 1));
      if (this.activeConversationState()?.id === conversationId) {
        this.activeConversationState.set(null);
      }
    } catch (error) {
      this.setError(error);
    }
  }

  /**
   * Sends a user message. Creates a conversation with the selected agent when none is
   * active, shows the message optimistically and applies the streamed events.
   * Resolves `false` when the message was not accepted.
   */
  async send(text: string): Promise<boolean> {
    const trimmed = text.trim();
    const maxChars = this.limits()?.maxMessageChars ?? Number.MAX_SAFE_INTEGER;
    if (trimmed.length === 0 || trimmed.length > maxChars || !this.canSend()) {
      return false;
    }

    this.errorState.set(null);
    this.restoredDraftState.set(null);
    this.streamingState.set(true);

    let conversation = this.activeConversationState();
    if (!conversation) {
      conversation = await this.createConversation();
      if (!conversation) {
        this.streamingState.set(false);
        this.restoredDraftState.set(text);
        return false;
      }
    }

    const clientMessageId = createClientMessageId();
    const previousStatus = conversation.status;
    this.activeConversationState.set({
      ...conversation,
      status: 'running',
      messages: [
        ...conversation.messages,
        { id: clientMessageId, role: 'user', createdAt: new Date().toISOString(), parts: [{ type: 'text', text: trimmed }] }
      ]
    });

    const conversationId = conversation.id;
    return new Promise<boolean>(resolve => {
      let started = false;
      let failedAfterStart = false;

      this.runStream(this.api.sendMessage(conversationId, { text: trimmed, clientMessageId }), {
        next: event => {
          if (event.type === 'error' && !started) {
            this.removeMessage(clientMessageId, previousStatus);
            this.restoredDraftState.set(text);
            this.errorState.set(event.data);
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

          this.applyEvent(event, clientMessageId);
        },
        complete: () => {
          resolve(started);
          this.afterTurn(conversationId, failedAfterStart);
        }
      });
    });
  }

  /** Approves or rejects the pending approval. Ignores repeated calls while a decision is in flight. */
  decide(decision: ApprovalDecision, reason?: string): void {
    const conversation = this.activeConversationState();
    const approval = conversation?.pendingApproval ?? null;
    if (!conversation || !approval || this.decidingState() || this.streamingState()) {
      return;
    }

    this.decidingState.set(true);
    this.streamingState.set(true);
    this.errorState.set(null);

    let accepted = false;
    let failed = false;
    const request = { decision, expectedProposalHash: approval.proposalHash, ...(reason ? { reason } : {}) };

    this.runStream(this.api.decideApproval(conversation.id, approval.approvalId, request), {
      next: event => {
        if (!accepted && event.type !== 'error') {
          accepted = true;
          this.updateActive(current => applyNhAssistantApprovalDecision(current, approval.approvalId, decision));
        }
        if (event.type === 'error') {
          failed = true;
          if (!accepted) {
            this.errorState.set(event.data);
            return;
          }
        }

        this.applyEvent(event);
      },
      complete: () => {
        this.decidingState.set(false);
        this.afterTurn(conversation.id, failed);
      }
    });
  }

  /** Asks the server to stop the running turn or to abandon a waiting approval. */
  async cancel(): Promise<void> {
    const conversation = this.activeConversationState();
    if (!conversation || (conversation.status === 'idle' && !this.streamingState())) {
      return;
    }

    try {
      await firstValueFrom(this.api.cancel(conversation.id), { defaultValue: undefined });
    } catch (error) {
      this.setError(error);
      return;
    }

    if (!this.streamingState()) {
      await this.reloadActiveConversation(conversation.id);
      return;
    }

    // The server ends the stream with turn.completed(cancelled); stop waiting if it does not.
    clearTimeout(this.cancelTimer);
    this.cancelTimer = setTimeout(() => {
      if (this.streamingState()) {
        this.streamSubscription?.unsubscribe();
        this.streamingState.set(false);
        this.decidingState.set(false);
        void this.reloadActiveConversation(conversation.id);
      }
    }, cancelGracePeriodMs);
  }

  clearError(): void {
    this.errorState.set(null);
  }

  /** Returns and clears the draft of a message that was not accepted. */
  consumeRestoredDraft(): string | null {
    const draft = this.restoredDraftState();
    this.restoredDraftState.set(null);
    return draft;
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
          const changed = this.accessGrantedState() !== granted;
          this.accessGrantedState.set(granted);
          if (changed) {
            void this.reloadStatus().finally(() => resolve());
          } else {
            resolve();
          }
        },
        error: () => {
          this.accessGrantedState.set(false);
          resolve();
        },
        complete: () => resolve()
      });
    });
  }

  private async createConversation(): Promise<Conversation | null> {
    const agentId = this.selectedAgentIdState();
    if (!agentId) {
      return null;
    }

    try {
      const conversation = await firstValueFrom(this.api.createConversation({ agentId }));
      this.activeConversationState.set(conversation);
      this.conversationsState.update(items => [toSummary(conversation), ...items.filter(item => item.id !== conversation.id)]);
      this.conversationsTotalState.update(total => total + 1);
      return conversation;
    } catch (error) {
      this.setError(error);
      return null;
    }
  }

  private runStream(
    stream: Observable<NhAssistantSseEvent>,
    handlers: { next: (event: NhAssistantSseEvent) => void; complete: () => void }
  ): void {
    this.streamSubscription?.unsubscribe();
    this.streamingState.set(true);

    const finish = () => {
      clearTimeout(this.cancelTimer);
      this.streamingState.set(false);
      handlers.complete();
    };

    this.streamSubscription = stream.subscribe({
      next: event => handlers.next(event),
      error: () => {
        this.errorState.set(clientError(NhAssistantClientErrorCodes.network));
        finish();
      },
      complete: finish
    });
  }

  private applyEvent(event: NhAssistantSseEvent, clientMessageId?: string): void {
    this.updateActive(conversation => applyNhAssistantEvent(conversation, event, clientMessageId));

    if (event.type === 'error') {
      this.errorState.set(event.data);
    }
    if (event.type === 'turn.completed') {
      this.lastUsageState.set(event.data.usage);
      if (event.data.status === 'failed') {
        const code = event.data.errorCode ?? NhAssistantClientErrorCodes.server;
        this.errorState.set(clientError(code));
      }
    }
  }

  private afterTurn(conversationId: string, reload: boolean): void {
    const active = this.activeConversationState();
    if (active?.id === conversationId) {
      this.conversationsState.update(items => items.map(item =>
        item.id === conversationId ? { ...item, status: active.status, updatedAt: new Date().toISOString() } : item
      ));
    }

    if (reload) {
      void this.reloadActiveConversation(conversationId);
    }
    if (!active?.title) {
      void this.refreshConversations();
    }
  }

  private async reloadActiveConversation(conversationId: string): Promise<void> {
    try {
      const conversation = await firstValueFrom(this.api.getConversation(conversationId));
      if (this.activeConversationState()?.id === conversationId && !this.streamingState()) {
        this.activeConversationState.set(conversation);
      }
    } catch {
      // Keep the local state; the error of the turn is already shown.
    }
  }

  private updateActive(update: (conversation: Conversation) => Conversation): void {
    const conversation = this.activeConversationState();
    if (conversation) {
      this.activeConversationState.set(update(conversation));
    }
  }

  private removeMessage(messageId: string, status: Conversation['status']): void {
    this.updateActive(conversation => ({
      ...conversation,
      status,
      messages: conversation.messages.filter(message => message.id !== messageId)
    }));
  }

  private selectInitialAgent(agents: AgentSummary[]): void {
    const current = this.selectedAgentIdState();
    if (current && agents.some(agent => agent.id === current)) {
      return;
    }

    const preferred = agents.find(agent => agent.id === this.config.defaultAgentId) ?? agents[0] ?? null;
    this.selectedAgentIdState.set(preferred?.id ?? null);
  }

  private setError(error: unknown): void {
    if (error instanceof NhAssistantApiError) {
      this.errorState.set({ code: error.code, messageKey: error.messageKey });
      return;
    }

    this.errorState.set(clientError(NhAssistantClientErrorCodes.server));
  }
}

function clientError(code: string): NhAssistantError {
  return { code, messageKey: nhAssistantErrorMessageKey(code) };
}

function toSummary(conversation: Conversation): ConversationSummary {
  return {
    id: conversation.id,
    agentId: conversation.agentId,
    title: conversation.title,
    status: conversation.status,
    createdAt: conversation.createdAt,
    updatedAt: conversation.updatedAt
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
