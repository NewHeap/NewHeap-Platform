import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import {
  ApprovalPart,
  ClientContext,
  Conversation,
  ConversationMember,
  ConversationSummary,
  CreateConversationRequest,
  DecideApprovalRequest,
  LiveConversationChanged,
  LiveConversationEvent,
  LiveConversationRead,
  LiveConversationRemoved,
  Message,
  NH_ASSISTANT_CONFIG,
  NhAssistantSseEvent,
  SendMessageRequest,
  TurnUsage,
  applyNhAssistantApprovalDecision,
  applyNhAssistantEvent,
  normalizeNhAssistantClientContext
} from '@newheap/platform-ai-chat';
import { Observable, Subject } from 'rxjs';
import { NhAssistantMockAdmin } from './nh-assistant-mock-admin';
import {
  NhAssistantMockApprovalStep,
  NhAssistantMockPerson,
  NhAssistantMockRemoteTool,
  NhAssistantMockRequest,
  NhAssistantMockScenario,
  NhAssistantMockStep,
  NhAssistantMockTurn
} from './nh-assistant-mock.models';

export const NH_ASSISTANT_MOCK_SCENARIO = new InjectionToken<NhAssistantMockScenario>('NH_ASSISTANT_MOCK_SCENARIO');

/** Path the mock reports as hub; the mock live service answers it in memory. */
export const NH_ASSISTANT_MOCK_HUB_PATH = '/hub/assistant-mock';

/** One live update of the mock, as the assistant hub would send it. */
export type NhAssistantMockLiveMessage =
  | { kind: 'changed'; data: LiveConversationChanged }
  | { kind: 'read'; data: LiveConversationRead }
  | { kind: 'removed'; data: LiveConversationRemoved }
  | { kind: 'event'; data: LiveConversationEvent };

const defaultLimits = { maxMessageChars: 4_000, maxToolCallsPerTurn: 8 };
const defaultUser: NhAssistantMockPerson = { actorId: 'mock-user', displayName: 'You' };

interface PendingApproval {
  approval: ApprovalPart;
  invocationId: string;
  assistantMessageId: string;
  step: NhAssistantMockApprovalStep['approval'];
}

interface ActiveRun {
  cancelled: boolean;
  aborted: boolean;
}

class RunStopped extends Error {}

type Emit = (event: NhAssistantSseEvent) => Promise<void>;

/**
 * In-memory implementation of the assistant HTTP API and its event streams. It keeps
 * conversations, plays scripted turns as contract events through a real
 * `text/event-stream` response and handles approvals, cancellation and the feature flag.
 */
@Injectable()
export class NhAssistantMockBackend {
  private readonly scenario = inject(NH_ASSISTANT_MOCK_SCENARIO);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly conversations = new Map<string, Conversation>();
  private readonly pendingApprovals = new Map<string, PendingApproval>();
  private readonly runs = new Map<string, ActiveRun>();
  /** Page context of the latest message per conversation, as the server stores it. */
  private readonly pageContexts = new Map<string, ClientContext | null>();
  private readonly enabledState = signal(this.scenario.enabled ?? true);
  private readonly admin = new NhAssistantMockAdmin(this.scenario, () => new Date().toISOString());
  private readonly canAdministerState = signal(this.admin.administers);
  private readonly currentUser = this.scenario.collaboration?.currentUser ?? defaultUser;
  private readonly members = new Map<string, ConversationMember[]>();
  private readonly readPositions = new Map<string, number>();
  private readonly shareTokens = new Map<string, string>();
  private readonly activeActors = new Map<string, string>();
  private readonly liveSubject = new Subject<NhAssistantMockLiveMessage>();
  private pushEnabled = true;
  private sequence = 0;

  /** Requests received so far, oldest first. */
  readonly requests: NhAssistantMockRequest[] = [];
  readonly enabled = this.enabledState.asReadonly();
  readonly canAdminister = this.canAdministerState.asReadonly();
  /** Live updates for the mock live service, in the order the hub would send them. */
  readonly live$: Observable<NhAssistantMockLiveMessage> = this.liveSubject.asObservable();

  constructor() {
    for (const conversation of this.scenario.conversations ?? []) {
      const copy = withSequences(structuredClone(conversation));
      this.conversations.set(copy.id, copy);
      this.readPositions.set(copy.id, lastSequence(copy));
    }
  }

  /**
   * Plays a scripted turn as another person in the conversation, as if a colleague with an
   * invitation sent a message: the conversation becomes shared, and the message and the
   * answer arrive as live updates. Resolves when the turn ends.
   */
  async simulateParticipantTurn(conversationId: string, person: NhAssistantMockPerson, text: string): Promise<void> {
    const conversation = this.conversations.get(conversationId);
    if (!conversation || conversation.status !== 'idle') {
      return;
    }

    this.addMember(conversationId, person, 'participant');
    const userMessage: Message = {
      id: this.nextId(),
      role: 'user',
      createdAt: new Date().toISOString(),
      parts: [{ type: 'text', text }],
      authorActorId: person.actorId
    };
    this.activeActors.set(conversationId, person.actorId);
    this.update(conversationId, current => ({ ...current, status: 'running', messages: [...current.messages, userMessage] }));
    const stored = this.conversations.get(conversationId)!.messages.find(message => message.id === userMessage.id)!;
    this.emitLive({ kind: 'event', data: { conversationId, actorId: person.actorId, type: 'message.created', data: stored } });

    const timing = this.scenario.timing ?? {};
    const assistantMessageId = this.nextId();
    const emit: Emit = async event => {
      await delay(timing.eventDelayMs ?? 40);
      this.update(conversationId, current => applyNhAssistantEvent(current, event));
      if (event.type === 'turn.completed' && event.data.status !== 'waiting-for-approval') {
        this.activeActors.delete(conversationId);
        this.emitChanged(conversationId);
      }
      this.emitLive({ kind: 'event', data: { conversationId, actorId: person.actorId, type: event.type, data: event.data } });
    };
    await delay(timing.firstEventDelayMs ?? 200);
    await emit({ type: 'turn.started', data: { turnId: this.nextId(), userMessageId: userMessage.id, assistantMessageId } });
    const turn = this.findTurn(text, conversation.agentId, null);
    await this.playSteps(conversationId, assistantMessageId, turn?.steps ?? [], emit);
  }

  /** Switches the simulated `NewHeap:AI:Assistant:Enabled` flag. */
  setEnabled(enabled: boolean): void {
    this.enabledState.set(enabled);
  }

  /** Switches whether the caller passes the admin policy (`canAdminister`, `admin/*`). */
  setCanAdminister(canAdminister: boolean): void {
    this.admin.setCanAdminister(canAdminister);
    this.canAdministerState.set(canAdminister);
  }

  /** Replaces the tools a simulated MCP server lists; the next sync picks them up. */
  setRemoteTools(serverId: string, tools: NhAssistantMockRemoteTool[]): void {
    this.admin.setRemoteTools(serverId, tools);
  }

  /** A `fetch` implementation that answers the assistant endpoints below `apiBaseUrl`. */
  readonly fetch = async (input: string, init: RequestInit): Promise<Response> => {
    const method = (init.method ?? 'GET').toUpperCase();
    const url = new URL(input, 'http://mock.local');
    const base = new URL(this.config.apiBaseUrl.replace(/\/+$/, ''), 'http://mock.local').pathname;
    const path = url.pathname.startsWith(`${base}/`) ? url.pathname.slice(base.length + 1) : null;
    const body = typeof init.body === 'string' && init.body.length > 0 ? JSON.parse(init.body) : undefined;

    this.requests.push({ method, path: path ?? url.pathname, headers: { ...(init.headers as Record<string, string>) }, body });

    if (init.signal?.aborted) {
      throw new DOMException('The operation was aborted.', 'AbortError');
    }
    if (path === null) {
      return this.empty(404);
    }

    return this.route(method, path, url.searchParams, body);
  };

  private route(method: string, path: string, query: URLSearchParams, body: unknown): Response {
    const segments = path.split('/').map(segment => decodeURIComponent(segment));

    if (method === 'GET' && path === 'status') {
      const limits = { ...defaultLimits, ...this.scenario.limits };
      const collaboration = {
        directory: (this.scenario.collaboration?.directory?.length ?? 0) > 0,
        hubPath: NH_ASSISTANT_MOCK_HUB_PATH,
        push: false,
        maxParticipants: this.maxParticipants()
      };
      return this.json(200, this.enabledState()
        ? { enabled: true, agents: this.admin.chatAgents(), limits, canAdminister: this.admin.administers, collaboration }
        : { enabled: false, agents: [], limits, canAdminister: false });
    }
    if (!this.enabledState()) {
      return this.empty(404);
    }

    if (method === 'GET' && path === 'agents') {
      return this.json(200, this.admin.chatAgents());
    }
    if (path === 'preferences') {
      return this.result(this.admin.handlePreferences(method, body));
    }
    if (path === 'notifications') {
      if (method === 'PUT') {
        this.pushEnabled = (body as { pushEnabled?: boolean } | undefined)?.pushEnabled === true;
      }
      return this.json(200, { pushEnabled: this.pushEnabled, pushAvailable: false, publicKey: null });
    }
    if (path === 'notifications/push-subscription') {
      return this.error(404, 'assistant-push-unavailable');
    }
    if (segments[0] === 'admin') {
      return this.result(this.admin.handleAdmin(method, segments, body));
    }
    if (segments[0] !== 'conversations') {
      return this.empty(404);
    }

    if (segments.length === 1) {
      if (method === 'GET') {
        return this.json(200, this.list(query));
      }
      if (method === 'POST') {
        return this.create(body as CreateConversationRequest);
      }
      return this.empty(405);
    }

    const conversation = this.conversations.get(segments[1]);
    if (!conversation) {
      return this.empty(404);
    }

    if (segments.length === 2) {
      if (method === 'GET') {
        return this.json(200, this.view(conversation));
      }
      if (method === 'DELETE') {
        this.conversations.delete(conversation.id);
        this.pendingApprovals.delete(conversation.id);
        this.emitLive({ kind: 'removed', data: { conversationId: conversation.id } });
        return this.empty(204);
      }
      return this.empty(405);
    }

    if (method === 'POST' && segments.length === 3 && segments[2] === 'read') {
      return this.markRead(conversation, (body as { sequence?: number } | undefined)?.sequence);
    }
    if (segments.length === 3 && segments[2] === 'share-link') {
      if (method === 'POST') {
        const token = this.nextId().replace(/-/g, '');
        this.shareTokens.set(conversation.id, token);
        return this.json(200, { token });
      }
      if (method === 'DELETE') {
        this.shareTokens.delete(conversation.id);
        return this.empty(204);
      }
    }
    if (method === 'POST' && segments.length === 3 && segments[2] === 'join') {
      const token = (body as { token?: string } | undefined)?.token;
      return token && this.shareTokens.get(conversation.id) === token
        ? this.json(200, this.view(conversation))
        : this.error(404, 'assistant-share-link-invalid');
    }
    if (method === 'GET' && segments.length === 3 && segments[2] === 'participant-candidates') {
      return this.candidates(conversation, query.get('query') ?? '');
    }
    if (method === 'POST' && segments.length === 3 && segments[2] === 'participants') {
      return this.invite(conversation, (body as { actorId?: string } | undefined)?.actorId);
    }
    if (method === 'DELETE' && segments.length === 4 && segments[2] === 'participants') {
      const removed = this.removeMember(conversation.id, segments[3]);
      return removed ? this.empty(204) : this.error(404, 'assistant-participant-not-found');
    }

    if (method === 'POST' && segments.length === 3 && segments[2] === 'messages') {
      return this.sendMessage(conversation, body as SendMessageRequest);
    }
    if (method === 'POST' && segments.length === 3 && segments[2] === 'cancel') {
      return this.cancel(conversation);
    }
    if (method === 'POST' && segments.length === 5 && segments[2] === 'approvals' && segments[4] === 'decide') {
      return this.decide(conversation, segments[3], body as DecideApprovalRequest);
    }

    return this.empty(404);
  }

  private list(query: URLSearchParams): { items: ConversationSummary[]; total: number } {
    const page = Math.max(1, Number(query.get('page') ?? 1));
    const itemsPerPage = Math.max(1, Number(query.get('itemsPerPage') ?? 20));
    const all = [...this.conversations.values()]
      .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
      .map(conversation => this.summary(conversation));

    return { items: all.slice((page - 1) * itemsPerPage, page * itemsPerPage), total: all.length };
  }

  private create(request: CreateConversationRequest): Response {
    const agent = this.admin.chatAgents().find(item => item.id === request?.agentId);
    if (!agent) {
      return this.empty(400);
    }

    const now = new Date().toISOString();
    const conversation: Conversation = {
      id: this.nextId(),
      agentId: agent.id,
      agentVersion: agent.version,
      title: request.title ?? null,
      status: 'idle',
      createdAt: now,
      updatedAt: now,
      messages: [],
      pendingApproval: null
    };
    this.conversations.set(conversation.id, conversation);
    this.readPositions.set(conversation.id, 0);

    return this.json(201, this.view(conversation));
  }

  private sendMessage(conversation: Conversation, request: SendMessageRequest): Response {
    const text = request?.text?.trim() ?? '';
    const limit = this.scenario.limits?.maxMessageChars ?? defaultLimits.maxMessageChars;
    if (text.length === 0 || text.length > limit || !request.clientMessageId) {
      return this.empty(400);
    }
    if (conversation.status !== 'idle') {
      return this.empty(409);
    }

    // Like the server: an invalid page context is dropped, never a 400.
    const pageContext = normalizeNhAssistantClientContext(request.clientContext);
    this.pageContexts.set(conversation.id, pageContext);
    const turn = this.findTurn(text, conversation.agentId, pageContext);
    const userMessageId = this.nextId();
    const assistantMessageId = this.nextId();
    this.activeActors.set(conversation.id, this.currentUser.actorId);
    this.update(conversation.id, current => ({
      ...current,
      title: current.title ?? text.slice(0, 60),
      status: 'running',
      messages: [
        ...current.messages,
        { id: userMessageId, role: 'user', createdAt: new Date().toISOString(), parts: [{ type: 'text', text }], authorActorId: this.currentUser.actorId }
      ]
    }));
    // The sender has read their own message.
    this.readPositions.set(conversation.id, lastSequence(this.conversations.get(conversation.id)!));

    return this.stream(conversation.id, async emit => {
      await emit({ type: 'turn.started', data: { turnId: this.nextId(), userMessageId, assistantMessageId } });
      await this.playSteps(conversation.id, assistantMessageId, turn?.steps ?? [], emit);
    });
  }

  private decide(conversation: Conversation, approvalId: string, request: DecideApprovalRequest): Response {
    const pending = this.pendingApprovals.get(conversation.id);
    if (!pending || pending.approval.approvalId !== approvalId) {
      return this.empty(404);
    }
    const decider = this.activeActors.get(conversation.id);
    if (decider && decider !== this.currentUser.actorId) {
      return this.error(403, 'assistant-approval-forbidden');
    }
    if (request?.expectedProposalHash !== pending.approval.proposalHash) {
      return this.empty(409);
    }
    if (Date.parse(pending.approval.expiresAt) <= Date.now()) {
      return this.empty(409);
    }

    this.pendingApprovals.delete(conversation.id);
    const decision = request.decision;
    const lastUser = [...conversation.messages].reverse().find(message => message.role === 'user');
    this.update(conversation.id, current => applyNhAssistantApprovalDecision(current, approvalId, decision));

    return this.stream(conversation.id, async emit => {
      await emit({
        type: 'turn.started',
        data: { turnId: this.nextId(), userMessageId: lastUser?.id ?? pending.assistantMessageId, assistantMessageId: pending.assistantMessageId }
      });

      if (decision === 'approve') {
        await emit({
          type: 'tool.completed',
          data: { invocationId: pending.invocationId, status: 'succeeded', resultCode: null, resultPreview: pending.step.resultPreview ?? null }
        });
        await this.playSteps(conversation.id, pending.assistantMessageId, pending.step.approved, emit);
      } else {
        await this.playSteps(conversation.id, pending.assistantMessageId, pending.step.rejected, emit);
      }
    });
  }

  private cancel(conversation: Conversation): Response {
    const run = this.runs.get(conversation.id);
    if (run) {
      run.cancelled = true;
      return this.empty(202);
    }

    const pending = this.pendingApprovals.get(conversation.id);
    if (pending) {
      this.pendingApprovals.delete(conversation.id);
      this.update(conversation.id, current => ({
        ...applyNhAssistantApprovalDecision(current, pending.approval.approvalId, 'reject'),
        status: 'idle'
      }));
    }

    return this.empty(202);
  }

  private async playSteps(conversationId: string, assistantMessageId: string, steps: NhAssistantMockStep[], emit: Emit): Promise<void> {
    const usage: TurnUsage = { inputTokens: 180, outputTokens: 0, toolCalls: 0 };
    const complete = (status: 'completed' | 'failed', errorCode: string | null = null) =>
      emit({ type: 'turn.completed', data: { turnId: this.nextId(), status, usage, errorCode } });

    for (const step of steps) {
      if ('text' in step) {
        const text = typeof step.text === 'function' ? step.text(this.pageContexts.get(conversationId) ?? null) : step.text;
        for (const piece of text.match(/\S+\s*|\s+/g) ?? []) {
          usage.outputTokens++;
          await emit({ type: 'message.delta', data: { messageId: assistantMessageId, text: piece } });
        }
      } else if ('tool' in step) {
        const invocationId = this.nextId();
        usage.toolCalls++;
        await emit({
          type: 'tool.started',
          data: {
            invocationId,
            toolId: step.tool.toolId,
            toolVersion: step.tool.toolVersion ?? 1,
            displayName: step.tool.displayName,
            argumentsPreview: step.tool.argumentsPreview ?? null
          }
        });
        await emit({
          type: 'tool.completed',
          data: {
            invocationId,
            status: step.tool.status ?? 'succeeded',
            resultCode: step.tool.resultCode ?? null,
            resultPreview: step.tool.resultPreview ?? null
          }
        });
      } else if ('approval' in step) {
        await this.pauseForApproval(conversationId, assistantMessageId, step.approval, emit);
        usage.toolCalls++;
        await emit({ type: 'turn.completed', data: { turnId: this.nextId(), status: 'waiting-for-approval', usage, errorCode: null } });
        return;
      } else if ('error' in step) {
        await emit({ type: 'error', data: step.error });
        // The mock keeps the conversation usable after a failed turn.
        this.update(conversationId, current => ({ ...current, status: 'idle' }));
        return;
      } else if ('fail' in step) {
        await complete('failed', step.fail.errorCode);
        return;
      }
    }

    await complete('completed');
  }

  private async pauseForApproval(
    conversationId: string,
    assistantMessageId: string,
    step: NhAssistantMockApprovalStep['approval'],
    emit: Emit
  ): Promise<void> {
    const invocationId = this.nextId();
    await emit({
      type: 'tool.started',
      data: {
        invocationId,
        toolId: step.toolId,
        toolVersion: step.toolVersion ?? 1,
        displayName: step.displayName,
        argumentsPreview: step.argumentsPreview
      }
    });

    const approval: ApprovalPart = {
      type: 'approval',
      approvalId: this.nextId(),
      proposalId: this.nextId(),
      proposalHash: `sha256:${this.nextId().replace(/-/g, '')}`,
      toolId: step.toolId,
      summary: step.summary,
      presentation: step.presentation,
      argumentsPreview: step.argumentsPreview,
      targets: step.targets,
      expiresAt: new Date(Date.now() + (step.expiresInSeconds ?? 300) * 1000).toISOString(),
      status: 'pending'
    };
    this.pendingApprovals.set(conversationId, { approval, invocationId, assistantMessageId, step });
    await emit({ type: 'approval.required', data: approval });
  }

  private stream(conversationId: string, play: (emit: Emit) => Promise<void>): Response {
    const timing = this.scenario.timing ?? {};
    const firstDelay = timing.firstEventDelayMs ?? 200;
    const eventDelay = timing.eventDelayMs ?? 40;
    const chunkSize = Math.max(1, timing.chunkSize ?? 23);
    const encoder = new TextEncoder();
    const run: ActiveRun = { cancelled: false, aborted: false };
    this.runs.set(conversationId, run);

    let first = true;
    let eventCount = 0;

    return new Response(new ReadableStream<Uint8Array>({
      start: controller => {
        const write = (text: string) => {
          const bytes = encoder.encode(text);
          for (let offset = 0; offset < bytes.length; offset += chunkSize) {
            controller.enqueue(bytes.slice(offset, offset + chunkSize));
          }
        };

        const emit: Emit = async event => {
          await delay(first ? firstDelay : eventDelay);
          first = false;
          if (run.aborted) {
            throw new RunStopped();
          }
          if (run.cancelled && event.type !== 'turn.started') {
            throw new RunStopped();
          }

          this.update(conversationId, current => applyNhAssistantEvent(current, event));
          if (eventCount++ % 5 === 2) {
            write(': keep-alive\n\n');
          }
          write(`event: ${event.type}\ndata: ${JSON.stringify(event.data)}\n\n`);
        };

        void play(emit)
          .catch(async error => {
            if (!(error instanceof RunStopped) || run.aborted) {
              return;
            }

            const usage = { inputTokens: 180, outputTokens: 0, toolCalls: 0 };
            const cancelled: NhAssistantSseEvent = { type: 'turn.completed', data: { turnId: this.nextId(), status: 'cancelled', usage, errorCode: null } };
            this.update(conversationId, current => applyNhAssistantEvent(current, cancelled));
            write(`event: ${cancelled.type}\ndata: ${JSON.stringify(cancelled.data)}\n\n`);
          })
          .finally(() => {
            if (this.runs.get(conversationId) === run) {
              this.runs.delete(conversationId);
            }
            if (!run.aborted) {
              controller.close();
            }
          });
      },
      cancel: () => {
        run.aborted = true;
      }
    }), { status: 200, headers: { 'content-type': 'text/event-stream; charset=utf-8', 'cache-control': 'no-cache' } });
  }

  private findTurn(text: string, agentId: string, pageContext: ClientContext | null): NhAssistantMockTurn | undefined {
    return this.scenario.turns.find(turn => {
      const match = turn.match;
      if (match === undefined) {
        return true;
      }
      if (typeof match === 'string') {
        return text.toLowerCase().includes(match.toLowerCase());
      }
      if (match instanceof RegExp) {
        return match.test(text);
      }
      return match(text, agentId, pageContext);
    });
  }

  private update(conversationId: string, change: (conversation: Conversation) => Conversation): void {
    const conversation = this.conversations.get(conversationId);
    if (!conversation) {
      return;
    }
    const updated = withSequences({ ...change(conversation), updatedAt: new Date().toISOString() });
    if (updated.status !== 'running' && updated.status !== 'waiting-for-approval') {
      this.activeActors.delete(conversationId);
    }
    this.conversations.set(conversationId, updated);
    if (updated.status !== conversation.status || updated.title !== conversation.title) {
      this.emitChanged(conversationId);
    }
  }

  /** The conversation as the signed-in user sees it. */
  private view(conversation: Conversation): Conversation {
    const members = this.members.get(conversation.id) ?? [];
    return {
      ...conversation,
      ...this.summary(conversation),
      members,
      shareToken: this.shareTokens.get(conversation.id) ?? null,
      currentActorId: this.currentUser.actorId
    };
  }

  private summary(conversation: Conversation): ConversationSummary {
    const latest = lastSequence(conversation);
    const members = this.members.get(conversation.id) ?? [];
    return {
      id: conversation.id,
      agentId: conversation.agentId,
      title: conversation.title,
      status: conversation.status,
      createdAt: conversation.createdAt,
      updatedAt: conversation.updatedAt,
      role: 'owner',
      participantCount: Math.max(0, members.length - 1),
      lastMessageSequence: latest,
      lastReadSequence: Math.min(this.readPositions.get(conversation.id) ?? latest, latest),
      activeActorId: this.activeActors.get(conversation.id) ?? null
    };
  }

  private markRead(conversation: Conversation, sequence: number | undefined): Response {
    const latest = lastSequence(conversation);
    const target = Math.min(sequence ?? latest, latest);
    if (target > (this.readPositions.get(conversation.id) ?? 0)) {
      this.readPositions.set(conversation.id, target);
      this.emitLive({ kind: 'read', data: { conversationId: conversation.id, lastReadSequence: target } });
    }
    return this.empty(204);
  }

  private candidates(conversation: Conversation, query: string): Response {
    const directory = this.scenario.collaboration?.directory ?? [];
    if (directory.length === 0) {
      return this.error(404, 'assistant-directory-unavailable');
    }
    const text = query.trim().toLowerCase();
    if (text.length < 2) {
      return this.error(400, 'assistant-validation');
    }
    const members = new Set((this.members.get(conversation.id) ?? []).map(member => member.actorId));
    return this.json(200, directory
      .filter(person => person.actorId !== this.currentUser.actorId && !members.has(person.actorId))
      .filter(person => person.displayName.toLowerCase().includes(text))
      .map(person => ({ actorId: person.actorId, displayName: person.displayName, detail: person.detail ?? null })));
  }

  private invite(conversation: Conversation, actorId: string | undefined): Response {
    const person = (this.scenario.collaboration?.directory ?? []).find(item => item.actorId === actorId);
    if (!person) {
      return this.error(404, 'assistant-participant-not-found');
    }
    const participants = (this.members.get(conversation.id) ?? []).filter(member => member.role === 'participant');
    if (participants.length >= this.maxParticipants()) {
      return this.error(409, 'assistant-participant-limit-reached');
    }
    this.addMember(conversation.id, person, 'participant');
    return this.empty(204);
  }

  private addMember(conversationId: string, person: NhAssistantMockPerson, role: ConversationMember['role']): void {
    const members = this.members.get(conversationId) ?? [];
    if (members.some(member => member.actorId === person.actorId)) {
      return;
    }
    const joinedAt = new Date().toISOString();
    const owner: ConversationMember[] = members.length === 0
      ? [{ actorId: this.currentUser.actorId, displayName: this.currentUser.displayName, role: 'owner', joinedAt }]
      : [];
    this.members.set(conversationId, [...members, ...owner, { actorId: person.actorId, displayName: person.displayName, role, joinedAt }]);
    this.emitChanged(conversationId);
  }

  private removeMember(conversationId: string, actorId: string): boolean {
    const members = this.members.get(conversationId) ?? [];
    if (!members.some(member => member.actorId === actorId && member.role === 'participant')) {
      return false;
    }
    const remaining = members.filter(member => member.actorId !== actorId);
    this.members.set(conversationId, remaining.length > 1 ? remaining : []);
    this.emitChanged(conversationId);
    return true;
  }

  private emitChanged(conversationId: string): void {
    const conversation = this.conversations.get(conversationId);
    if (!conversation) {
      return;
    }
    const summary = this.summary(conversation);
    this.emitLive({
      kind: 'changed',
      data: {
        conversationId,
        status: summary.status,
        title: summary.title,
        updatedAt: summary.updatedAt,
        activeActorId: summary.activeActorId ?? null,
        lastMessageSequence: summary.lastMessageSequence ?? 0,
        participantCount: summary.participantCount ?? 0
      }
    });
  }

  private emitLive(message: NhAssistantMockLiveMessage): void {
    // Delivered after the current request, as a hub message would arrive.
    queueMicrotask(() => this.liveSubject.next(message));
  }

  private maxParticipants(): number {
    return this.scenario.collaboration?.maxParticipants ?? 20;
  }

  private error(status: number, code: string): Response {
    return this.json(status, { code, messageKey: `nh-assistant.errors.${code}` });
  }

  private nextId(): string {
    this.sequence++;
    const suffix = this.sequence.toString(16).padStart(12, '0');
    return `00000000-0000-4000-8000-${suffix}`;
  }

  private result(result: { status: number; body?: unknown }): Response {
    return result.body === undefined ? this.empty(result.status) : this.json(result.status, result.body);
  }

  private json(status: number, value: unknown): Response {
    return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });
  }

  private empty(status: number): Response {
    return new Response(null, { status });
  }
}

function delay(milliseconds: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

/** Numbers messages in order, as the server does, keeping numbers that are already set. */
function withSequences(conversation: Conversation): Conversation {
  let next = lastSequence(conversation);
  if (conversation.messages.every(message => message.sequence !== undefined)) {
    return conversation;
  }
  return {
    ...conversation,
    messages: conversation.messages.map(message => message.sequence === undefined ? { ...message, sequence: ++next } : message)
  };
}

function lastSequence(conversation: Conversation): number {
  return conversation.messages.reduce((maximum, message) => Math.max(maximum, message.sequence ?? 0), 0);
}
