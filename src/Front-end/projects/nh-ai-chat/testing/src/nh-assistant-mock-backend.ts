import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import {
  ApprovalPart,
  Conversation,
  ConversationSummary,
  CreateConversationRequest,
  DecideApprovalRequest,
  NH_ASSISTANT_CONFIG,
  NhAssistantSseEvent,
  SendMessageRequest,
  TurnUsage,
  applyNhAssistantApprovalDecision,
  applyNhAssistantEvent
} from '@newheap/platform-ai-chat';
import {
  NhAssistantMockApprovalStep,
  NhAssistantMockRequest,
  NhAssistantMockScenario,
  NhAssistantMockStep,
  NhAssistantMockTurn
} from './nh-assistant-mock.models';

export const NH_ASSISTANT_MOCK_SCENARIO = new InjectionToken<NhAssistantMockScenario>('NH_ASSISTANT_MOCK_SCENARIO');

const defaultLimits = { maxMessageChars: 4_000, maxToolCallsPerTurn: 8 };

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
  private readonly enabledState = signal(this.scenario.enabled ?? true);
  private sequence = 0;

  /** Requests received so far, oldest first. */
  readonly requests: NhAssistantMockRequest[] = [];
  readonly enabled = this.enabledState.asReadonly();

  constructor() {
    for (const conversation of this.scenario.conversations ?? []) {
      this.conversations.set(conversation.id, structuredClone(conversation));
    }
  }

  /** Switches the simulated `NewHeap:AI:Assistant:Enabled` flag. */
  setEnabled(enabled: boolean): void {
    this.enabledState.set(enabled);
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
      return this.json(200, this.enabledState()
        ? { enabled: true, agents: this.scenario.agents, limits: { ...defaultLimits, ...this.scenario.limits } }
        : { enabled: false, agents: [], limits: { ...defaultLimits, ...this.scenario.limits } });
    }
    if (!this.enabledState()) {
      return this.empty(404);
    }

    if (method === 'GET' && path === 'agents') {
      return this.json(200, this.scenario.agents);
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
        return this.json(200, conversation);
      }
      if (method === 'DELETE') {
        this.conversations.delete(conversation.id);
        this.pendingApprovals.delete(conversation.id);
        return this.empty(204);
      }
      return this.empty(405);
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
      .map(({ id, agentId, title, status, createdAt, updatedAt }) => ({ id, agentId, title, status, createdAt, updatedAt }));

    return { items: all.slice((page - 1) * itemsPerPage, page * itemsPerPage), total: all.length };
  }

  private create(request: CreateConversationRequest): Response {
    const agent = this.scenario.agents.find(item => item.id === request?.agentId);
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

    return this.json(201, conversation);
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

    const turn = this.findTurn(text, conversation.agentId);
    const userMessageId = this.nextId();
    const assistantMessageId = this.nextId();
    this.update(conversation.id, current => ({
      ...current,
      title: current.title ?? text.slice(0, 60),
      status: 'running',
      messages: [...current.messages, { id: userMessageId, role: 'user', createdAt: new Date().toISOString(), parts: [{ type: 'text', text }] }]
    }));

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
        for (const piece of step.text.match(/\S+\s*|\s+/g) ?? []) {
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

  private findTurn(text: string, agentId: string): NhAssistantMockTurn | undefined {
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
      return match(text, agentId);
    });
  }

  private update(conversationId: string, change: (conversation: Conversation) => Conversation): void {
    const conversation = this.conversations.get(conversationId);
    if (conversation) {
      this.conversations.set(conversationId, { ...change(conversation), updatedAt: new Date().toISOString() });
    }
  }

  private nextId(): string {
    this.sequence++;
    const suffix = this.sequence.toString(16).padStart(12, '0');
    return `00000000-0000-4000-8000-${suffix}`;
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
