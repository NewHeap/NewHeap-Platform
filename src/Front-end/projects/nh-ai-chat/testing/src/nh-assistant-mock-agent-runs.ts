import {
  AdminAgent,
  AgentLatestRun,
  AgentObjectAgent,
  AgentObjectAgents,
  AgentRun,
  AgentThread,
  AgentThreadLookup,
  AgentTriggerReceipt
} from '@newheap/platform-ai-chat';
import { NhAssistantMockScenario } from './nh-assistant-mock.models';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';
import { NhAssistantMockDescribedSubject, NhAssistantMockSubjects } from './nh-assistant-mock-subjects';
import { mockSystemSubjectType, mockWorksOnCovers } from './nh-assistant-mock-works-on';

/** What the agent runs read of the mock's agents and caller. */
export interface NhAssistantMockAgentRunContext {
  /** Every agent of the administration. */
  agents(): AdminAgent[];
  /** Whether the caller passes the administration policy. */
  administers(): boolean;
}

interface StoredThread {
  conversationId: string;
  agentId: string;
  subject: NhAssistantMockDescribedSubject;
  runs: AgentRun[];
  openProposals: number;
}

const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const maxMessage = 8_000;
const maxSubjectId = 200;
const resultPreview = 300;
const activeStatuses = ['queued', 'running', 'waiting-for-approval'];

/**
 * Agents run by hand in the mock API, which runs no agents itself: `POST threads/start` checks who
 * may run the agent as the server does (on an object the participant role, on the whole application
 * the agent's run policy, by default the administration policy; what the agent works on, with its
 * scope; its required policy; shadow for administrators only), records a run that ends at once as the
 * scenario's `runNow` says, and `GET subjects/{type}/{id}/agents`, `GET threads` and the thread's runs
 * show it. The thread's proposals, memory, artifacts and consultations are empty.
 */
export class NhAssistantMockAgentRuns {
  private readonly threads = new Map<string, StoredThread>();
  private sequence = 0;

  constructor(
    private readonly scenario: NhAssistantMockScenario,
    private readonly subjects: NhAssistantMockSubjects,
    private readonly context: NhAssistantMockAgentRunContext,
    private readonly now: () => string
  ) {}

  /** `POST threads/start`, `GET threads` and `GET threads/{id}/…`; `null` for other paths. */
  handleThreads(method: string, segments: string[], query: URLSearchParams, body: unknown, language: string | null): NhAssistantMockResult | null {
    if (segments.length === 2 && segments[1] === 'start') {
      return method === 'POST' ? this.start(body, language) : { status: 405 };
    }
    if (segments.length === 1) {
      return method === 'GET' ? this.lookup(query, language) : { status: 405 };
    }
    if (segments.length === 3 && method === 'GET') {
      const thread = [...this.threads.values()].find(item => item.conversationId === segments[1]);
      if (!thread) {
        return failure(404, 'assistant-conversation-not-found');
      }
      switch (segments[2]) {
        case 'runs':
          return { status: 200, body: { items: [...thread.runs].reverse(), total: thread.runs.length } };
        case 'proposals':
        case 'artifacts':
          return { status: 200, body: [] };
        case 'memory':
          return { status: 200, body: { configured: false, schemaVersion: 1, memory: null, updatedAt: null } };
        case 'consultations':
          return { status: 200, body: { items: [], total: 0 } };
        default:
          return null;
      }
    }
    return null;
  }

  /** `GET subjects/{type}/{id}/agents`; `null` for other paths. */
  handleSubjects(method: string, segments: string[], language: string | null): NhAssistantMockResult | null {
    if (segments.length !== 4 || segments[3] !== 'agents') {
      return null;
    }
    if (method !== 'GET') {
      return { status: 405 };
    }

    const [, type, id] = segments;
    if (!validSubject(type, id)) {
      return failure(400, 'assistant-subject-not-found');
    }
    const subject = this.subjects.describe(type, id, language, this.context.administers());
    if (subject === undefined) {
      return failure(404, 'assistant-subject-type-unknown');
    }
    if (subject === null) {
      return failure(404, 'assistant-subject-not-found');
    }

    const agents: AgentObjectAgent[] = this.context.agents()
      .filter(agent => this.isWorking(agent) && this.mayUse(agent) && worksOn(agent, subject) && (!agent.shadowOf || this.context.administers()))
      .sort((left, right) => left.id.localeCompare(right.id))
      .map(agent => {
        const thread = this.threads.get(key(agent.id, subject));
        return {
          agentId: agent.id,
          displayNameKey: this.nameKeyOf(agent),
          descriptionKey: agent.source === 'code'
            ? this.scenario.agents.find(item => item.id === agent.id)?.descriptionKey ?? agent.description
            : agent.description,
          canStart: this.refusal(agent, subject) === null,
          conversationId: thread?.conversationId ?? null,
          latestRun: thread ? latestRun(thread) : null,
          openProposals: thread?.openProposals ?? 0
        };
      });
    const body: AgentObjectAgents = {
      subjectType: subject.subjectType,
      subjectId: subject.subjectId,
      subjectDisplayName: subject.displayName,
      subjectTypeName: subject.typeName,
      subjectLink: subject.link,
      role: subject.role,
      agents
    };
    return { status: 200, body };
  }

  private start(body: unknown, language: string | null): NhAssistantMockResult {
    const request = body as { agentId?: unknown; subjectType?: unknown; subjectId?: unknown; message?: unknown } | undefined;
    if (!request || (request.message !== undefined && request.message !== null && (typeof request.message !== 'string' || request.message.length > maxMessage))) {
      return failure(400, 'assistant-message-invalid');
    }
    const agent = typeof request.agentId === 'string' ? this.context.agents().find(item => item.id === request.agentId) : undefined;
    if (!agent) {
      return failure(404, 'assistant-agent-not-found');
    }
    const type = typeof request.subjectType === 'string' ? request.subjectType : '';
    const id = typeof request.subjectId === 'string' ? request.subjectId : '';
    if (!validSubject(type, id)) {
      return failure(400, 'assistant-subject-not-found');
    }
    const subject = this.subjects.describe(type, id, language, this.context.administers());
    if (subject === undefined) {
      return failure(404, 'assistant-subject-type-unknown');
    }
    if (subject === null) {
      return failure(403, 'assistant-subject-access-denied');
    }
    const refusal = this.refusal(agent, subject);
    if (refusal) {
      return failure(refusal.status, refusal.code);
    }

    const runNow = this.scenario.runNow?.[agent.id] ?? {};
    const outcome = runNow.outcome ?? 'started';
    if (outcome === 'rejected' || outcome === 'ignored') {
      const receipt: AgentTriggerReceipt = { triggerId: 'manual', outcome, runId: null, conversationId: null, reasonCode: null };
      return { status: 202, body: receipt };
    }

    const threadKey = key(agent.id, subject);
    const thread = this.threads.get(threadKey) ?? { conversationId: this.nextId(), agentId: agent.id, subject, runs: [], openProposals: 0 };
    this.threads.set(threadKey, thread);
    const now = this.now();
    const status = runNow.status ?? 'completed';
    const ended = !activeStatuses.includes(status);
    const run: AgentRun = {
      id: this.nextId(),
      conversationId: thread.conversationId,
      triggerId: 'manual',
      status,
      failureClass: status === 'failed' ? 'permanent' : null,
      failureCode: status === 'failed' ? 'assistant-turn-failed' : null,
      initiatorActorId: 'current-user',
      accountableOwnerId: `assistant-agent:${agent.id}`,
      attempt: 1,
      agentVersion: agent.version,
      toolCalls: 0,
      inputTokens: 0,
      outputTokens: 0,
      proposalCount: runNow.proposals ?? 0,
      summary: runNow.result ?? null,
      shadow: false,
      createdAt: now,
      startedAt: status === 'queued' ? null : now,
      endedAt: ended ? now : null,
      backgroundOperationId: null,
      kind: 'model'
    };
    thread.runs.push(run);
    thread.openProposals += runNow.proposals ?? 0;
    const receipt: AgentTriggerReceipt = { triggerId: 'manual', outcome, runId: run.id, conversationId: thread.conversationId, reasonCode: null };
    return { status: 202, body: receipt };
  }

  private lookup(query: URLSearchParams, language: string | null): NhAssistantMockResult {
    const agent = this.context.agents().find(item => item.id === query.get('agentId'));
    if (!agent) {
      return failure(404, 'assistant-agent-not-found');
    }
    const type = query.get('subjectType') ?? '';
    const id = query.get('subjectId') ?? '';
    if (!validSubject(type, id)) {
      return failure(400, 'assistant-subject-not-found');
    }
    const subject = this.subjects.describe(type, id, language, this.context.administers());
    if (subject === undefined) {
      return failure(404, 'assistant-subject-type-unknown');
    }
    if (subject === null) {
      return failure(404, 'assistant-subject-not-found');
    }

    const thread = this.threads.get(key(agent.id, subject));
    const body: AgentThreadLookup = {
      thread: thread ? toThread(thread) : null,
      canStart: this.refusal(agent, subject) === null,
      role: subject.role
    };
    return { status: 200, body };
  }

  /**
   * Why the caller may not run the agent on the subject, in the server's order: the role, the agent,
   * what it works on, then the run policy and shadow.
   */
  private refusal(agent: AdminAgent, subject: NhAssistantMockDescribedSubject): { status: number; code: string } | null {
    const system = subject.subjectType === mockSystemSubjectType;
    if (!system && subject.role === 'reader') {
      return { status: 403, code: 'assistant-subject-access-denied' };
    }
    if (!this.isWorking(agent) || !this.mayUse(agent)) {
      return { status: 403, code: 'assistant-agent-forbidden' };
    }
    if (!worksOn(agent, subject)) {
      return { status: 409, code: 'assistant-agent-does-not-work-on' };
    }
    if (system && !(agent.runPolicy ? this.passes(agent.runPolicy) : this.context.administers())) {
      return { status: 403, code: 'assistant-agent-forbidden' };
    }
    if (agent.shadow?.shadow && !this.context.administers()) {
      return { status: 403, code: 'assistant-agent-forbidden' };
    }
    return null;
  }

  /** Neither stopped nor disabled in code; a paused agent still works for people. */
  private isWorking(agent: AdminAgent): boolean {
    return agent.runState === 'active' || agent.runState === 'paused';
  }

  private mayUse(agent: AdminAgent): boolean {
    return agent.requiredPolicy === null || agent.requiredPolicy === undefined || this.passes(agent.requiredPolicy);
  }

  private passes(policy: string): boolean {
    return this.scenario.callerPolicies?.includes(policy) ?? true;
  }

  /** Agents in code keep their translation keys, agents created as data their text. */
  private nameKeyOf(agent: AdminAgent): string {
    return agent.source === 'code'
      ? this.scenario.agents.find(item => item.id === agent.id)?.displayNameKey ?? agent.displayName
      : agent.displayName;
  }

  private nextId(): string {
    this.sequence++;
    return `00000000-0000-4000-9000-${this.sequence.toString(16).padStart(12, '0')}`;
  }
}

/** One thread per agent and subject, as on the server. */
function key(agentId: string, subject: NhAssistantMockDescribedSubject): string {
  return `${agentId}|${subject.subjectType}|${subject.subjectId}`;
}

function validSubject(type: string, id: string): boolean {
  return dashCase.test(type) && type.length <= 100 && id.trim().length > 0 && id.length <= maxSubjectId && id.trim() === id;
}

/** The type the agent works on, and for the whole application its scope: app-wide or per division. */
function worksOn(agent: AdminAgent, subject: NhAssistantMockDescribedSubject): boolean {
  if (!mockWorksOnCovers(agent, subject.subjectType)) {
    return false;
  }
  if (subject.scope === 'object') {
    return true;
  }
  return agent.perDivision === true ? subject.scope === 'division' : subject.scope === 'global';
}

function latestRun(thread: StoredThread): AgentLatestRun | null {
  const run = thread.runs.at(-1);
  if (!run) {
    return null;
  }
  const result = run.summary;
  return {
    runId: run.id,
    conversationId: run.conversationId,
    triggerId: run.triggerId,
    status: run.status,
    failureCode: run.failureCode,
    at: run.endedAt ?? run.startedAt ?? run.createdAt,
    result: result === null ? null : result.slice(0, resultPreview),
    resultTruncated: result !== null && result.length > resultPreview,
    shadow: run.shadow
  };
}

function toThread(thread: StoredThread): AgentThread {
  const active = thread.runs.find(run => activeStatuses.includes(run.status)) ?? null;
  return {
    conversationId: thread.conversationId,
    agentId: thread.agentId,
    subjectType: thread.subject.subjectType,
    subjectId: thread.subject.subjectId,
    subjectDisplayName: thread.subject.displayName,
    accountability: 'agent',
    accountableOwnerId: `assistant-agent:${thread.agentId}`,
    role: thread.subject.role,
    activeRun: active,
    waitingInputs: 0,
    shadow: false,
    subjectTypeName: thread.subject.typeName,
    subjectLink: thread.subject.link
  };
}
