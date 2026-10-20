import {
  AdminAgent,
  AgentLatestRun,
  AgentProposal,
  AgentObjectAgent,
  AgentObjectAgents,
  AgentRun,
  AgentRunHistory,
  AgentRunHistoryItem,
  AgentRunResult,
  AgentRunStatus,
  AgentThread,
  AgentThreadLookup,
  AgentTriggerReceipt
} from '@newheap/platform-ai-chat';
import { NhAssistantMockRun, NhAssistantMockScenario } from './nh-assistant-mock.models';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';
import { NhAssistantMockDescribedSubject, NhAssistantMockSubjects } from './nh-assistant-mock-subjects';
import { mockSystemSubjectType, mockWorksOnCovers } from './nh-assistant-mock-works-on';

/** What the agent runs read of the mock's agents and caller. */
export interface NhAssistantMockAgentRunContext {
  /** Every agent of the administration. */
  agents(): AdminAgent[];
  /** Whether the caller passes the administration policy. */
  administers(): boolean;
  /** The scenario's proposals in a thread; `null` when it lists none there. */
  threadProposals?(conversationId: string): AgentProposal[] | null;
}

interface StoredThread {
  conversationId: string;
  agentId: string;
  subject: NhAssistantMockDescribedSubject;
  runs: AgentRun[];
  openProposals: number;
  /** The agent's shadow thread, which only administrators see. */
  shadow?: boolean;
}

const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const maxMessage = 8_000;
const maxSubjectId = 200;
const resultPreview = 300;
const maxResult = 20_000;
const maxItemsPerPage = 50;
const activeStatuses = ['queued', 'running', 'waiting-for-approval'];
const runStatuses: readonly AgentRunStatus[] = ['queued', 'running', 'waiting-for-approval', 'completed', 'failed', 'cancelled'];

/** A run history's filter as the server reads it. */
interface RunFilter {
  agentId: string | null;
  subjectType: string | null;
  subjectId: string | null;
  status: string | null;
  from: number | null;
  to: number | null;
  page: number;
  itemsPerPage: number;
}

/**
 * Agents run by hand in the mock API, which runs no agents itself: `POST threads/start` checks who
 * may run the agent as the server does (on an object the participant role, on the whole application
 * the agent's run policy, by default the administration policy; what the agent works on, with its
 * scope; its required policy; shadow for administrators only), records a run that ends at once as the
 * scenario's `runNow` says, and `GET subjects/{type}/{id}/agents`, `GET threads` and the thread's runs
 * show it. The thread's memory, artifacts and consultations are empty; its proposals are those the
 * scenario lists in it. The scenario's `runs` happened before; `GET runs`, `GET runs/{id}` and the
 * administration's `GET admin/agents/{id}/runs` list them and the runs by hand, with the server's
 * filters, validation and visibility.
 */
export class NhAssistantMockAgentRuns {
  private readonly threads = new Map<string, StoredThread>();
  /** The answer of each run, by run id, at most 20,000 characters. */
  private readonly results = new Map<string, string | null>();
  /** The runs whose answer was longer and was cut. */
  private readonly truncated = new Set<string>();
  private sequence = 0;

  constructor(
    private readonly scenario: NhAssistantMockScenario,
    private readonly subjects: NhAssistantMockSubjects,
    private readonly context: NhAssistantMockAgentRunContext,
    private readonly now: () => string
  ) {
    for (const seeded of scenario.runs ?? []) {
      this.seed(seeded);
    }
  }

  /** `GET runs`, `GET runs/{id}` and `GET runs/{id}/artifacts/{id}/content`; `null` for other paths. */
  handleRuns(method: string, segments: string[], query: URLSearchParams, language: string | null): NhAssistantMockResult | null {
    if (segments[0] !== 'runs' || (segments.length !== 1 && segments.length !== 2 && segments.length !== 5)) {
      return null;
    }
    if (method !== 'GET') {
      return segments.length === 1 || segments.length === 2 ? { status: 405 } : null;
    }
    if (segments.length === 1) {
      const filter = readFilter(query, true);
      if (!filter) {
        return failure(400, 'assistant-run-filter-invalid');
      }
      return { status: 200, body: this.history(filter, language, false) };
    }

    const found = this.findRun(segments[1]);
    if (!found || !(this.context.administers() || this.mayView(found.thread, language))) {
      return failure(404, 'assistant-run-not-found');
    }
    if (segments.length === 5) {
      // The mock's runs save no files.
      return segments[2] === 'artifacts' && segments[4] === 'content' ? failure(404, 'assistant-artifact-not-found') : null;
    }

    const body: AgentRunResult = {
      run: this.toHistoryItem(found.thread, found.run, language),
      result: this.results.get(found.run.id) ?? null,
      resultTruncated: this.truncated.has(found.run.id),
      summary: found.run.summary,
      proposals: [],
      hiddenProposals: 0,
      artifacts: [],
      canOpenConversation: this.mayView(found.thread, language)
    };
    return { status: 200, body };
  }

  /** `GET admin/agents/{id}/runs`: every run of the agent, for administrators. */
  handleAgentRuns(method: string, agentId: string, query: URLSearchParams, language: string | null): NhAssistantMockResult {
    if (method !== 'GET') {
      return { status: 405 };
    }
    if (!this.context.administers()) {
      return failure(403, 'assistant-forbidden');
    }
    if (!this.context.agents().some(agent => agent.id === agentId)) {
      return failure(404, 'assistant-agent-not-found');
    }
    const filter = readFilter(query, false);
    if (!filter) {
      return failure(400, 'assistant-run-filter-invalid');
    }
    return { status: 200, body: this.history({ ...filter, agentId }, language, true) };
  }

  /** `POST threads/start`, `GET threads` and `GET threads/{id}/…`; `null` for other paths. */
  handleThreads(method: string, segments: string[], query: URLSearchParams, body: unknown, language: string | null): NhAssistantMockResult | null {
    if (segments.length === 2 && segments[1] === 'start') {
      return method === 'POST' ? this.start(body, language) : { status: 405 };
    }
    if (segments.length === 1) {
      return method === 'GET' ? this.lookup(query, language) : { status: 405 };
    }
    if (segments.length === 3 && method === 'GET') {
      const listed = segments[2] === 'proposals' ? this.context.threadProposals?.(segments[1]) ?? null : null;
      if (listed) {
        return { status: 200, body: listed };
      }
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
          latestRun: thread ? latestRun(thread, this.results) : null,
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
      summary: runNow.result === undefined || runNow.result === null ? null : runNow.result.slice(0, 600),
      shadow: false,
      createdAt: now,
      startedAt: status === 'queued' ? null : now,
      endedAt: ended ? now : null,
      backgroundOperationId: null,
      kind: 'model'
    };
    thread.runs.push(run);
    this.keep(run.id, runNow.result);
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

  /** A page of the runs the caller may see, or with `everything` every run, newest first. */
  private history(filter: RunFilter, language: string | null, everything: boolean): AgentRunHistory {
    const matching = [...this.threads.values()]
      .filter(thread => (filter.agentId === null || thread.agentId === filter.agentId)
        && (filter.subjectType === null || thread.subject.subjectType === filter.subjectType)
        && (filter.subjectId === null || thread.subject.subjectId === filter.subjectId)
        && (everything || this.mayView(thread, language)))
      .flatMap(thread => thread.runs
        .filter(run => (filter.status === null || run.status === filter.status)
          && (filter.from === null || Date.parse(run.createdAt) >= filter.from)
          && (filter.to === null || Date.parse(run.createdAt) < filter.to))
        .map(run => ({ thread, run })))
      .sort((left, right) => Date.parse(right.run.createdAt) - Date.parse(left.run.createdAt) || right.run.id.localeCompare(left.run.id));
    const start = (filter.page - 1) * filter.itemsPerPage;
    return {
      items: matching.slice(start, start + filter.itemsPerPage).map(item => this.toHistoryItem(item.thread, item.run, language)),
      total: matching.length,
      limited: false
    };
  }

  /**
   * Whether the caller may see the runs of the thread: shadow work for administrators only, an object
   * the caller reads, the whole application by the agent's view policy, by default its required
   * policy.
   */
  private mayView(thread: StoredThread, language: string | null): boolean {
    if (thread.shadow && !this.context.administers()) {
      return false;
    }
    const subject = this.subjects.describe(thread.subject.subjectType, thread.subject.subjectId, language, this.context.administers());
    if (!subject) {
      return false;
    }
    if (subject.scope === 'object') {
      return true;
    }
    const agent = this.context.agents().find(item => item.id === thread.agentId);
    const policy = agent?.viewPolicy ?? agent?.requiredPolicy ?? null;
    return agent !== undefined && (policy === null || this.passes(policy));
  }

  private findRun(runId: string): { thread: StoredThread; run: AgentRun } | null {
    for (const thread of this.threads.values()) {
      const run = thread.runs.find(item => item.id === runId);
      if (run) {
        return { thread, run };
      }
    }
    return null;
  }

  private toHistoryItem(thread: StoredThread, run: AgentRun, language: string | null): AgentRunHistoryItem {
    const agent = this.context.agents().find(item => item.id === thread.agentId);
    const subject = this.subjects.describe(thread.subject.subjectType, thread.subject.subjectId, language, this.context.administers())
      ?? thread.subject;
    const answer = this.results.get(run.id) ?? null;
    const preview = answer ?? run.summary;
    return {
      id: run.id,
      conversationId: run.conversationId,
      agentId: thread.agentId,
      agentDisplayNameKey: agent ? this.nameKeyOf(agent) : null,
      subjectType: thread.subject.subjectType,
      subjectId: thread.subject.subjectId,
      subjectDisplayName: subject.displayName,
      subjectTypeName: subject.typeName,
      subjectLink: subject.link,
      triggerId: run.triggerId,
      status: run.status,
      failureClass: run.failureClass,
      failureCode: run.failureCode,
      kind: run.kind ?? 'model',
      skill: run.skill ?? null,
      initiatorActorId: run.initiatorActorId,
      toolCalls: run.toolCalls,
      modelCalls: 0,
      inputTokens: run.inputTokens,
      outputTokens: run.outputTokens,
      proposalCount: run.proposalCount,
      result: preview === null ? null : preview.slice(0, resultPreview),
      resultTruncated: preview !== null && (preview.length > resultPreview || this.truncated.has(run.id)),
      shadow: run.shadow,
      createdAt: run.createdAt,
      startedAt: run.startedAt,
      endedAt: run.endedAt
    };
  }

  /** Stores a run of the scenario in its agent's thread on its subject. */
  private seed(seeded: NhAssistantMockRun): void {
    const described = this.subjects.describe(seeded.subjectType, seeded.subjectId, null, true);
    const system = seeded.subjectType === mockSystemSubjectType;
    const subject: NhAssistantMockDescribedSubject = {
      subjectType: seeded.subjectType,
      subjectId: seeded.subjectId,
      displayName: seeded.subjectDisplayName ?? described?.displayName ?? seeded.subjectId,
      typeName: described?.typeName ?? null,
      link: described?.link ?? null,
      role: described?.role ?? 'reader',
      scope: described?.scope ?? (system ? 'global' : 'object')
    };
    const shadow = seeded.shadow === true;
    const threadKey = key(seeded.agentId, subject) + (shadow ? '|shadow' : '');
    const thread = this.threads.get(threadKey)
      ?? { conversationId: this.nextId(), agentId: seeded.agentId, subject, runs: [], openProposals: 0, shadow };
    this.threads.set(threadKey, thread);
    const createdAt = seeded.createdAt ?? this.now();
    const status = seeded.status ?? 'completed';
    const ended = !activeStatuses.includes(status);
    const endedAt = new Date(Date.parse(createdAt) + (seeded.durationSeconds ?? 30) * 1000).toISOString();
    const tokens = seeded.tokens ?? 0;
    const run: AgentRun = {
      id: seeded.id ?? this.nextId(),
      conversationId: thread.conversationId,
      triggerId: seeded.triggerId ?? 'manual',
      status,
      failureClass: status === 'failed' ? 'internal' : null,
      failureCode: status === 'failed' ? 'assistant-turn-failed' : null,
      initiatorActorId: null,
      accountableOwnerId: `assistant-agent:${seeded.agentId}`,
      attempt: 1,
      agentVersion: 1,
      toolCalls: seeded.toolCalls ?? 0,
      inputTokens: Math.ceil(tokens / 2),
      outputTokens: Math.floor(tokens / 2),
      proposalCount: seeded.proposalCount ?? 0,
      summary: seeded.result ? seeded.result.slice(0, 600) : null,
      shadow,
      createdAt,
      startedAt: status === 'queued' ? null : createdAt,
      endedAt: ended ? endedAt : null,
      backgroundOperationId: null,
      kind: 'model'
    };
    thread.runs.push(run);
    thread.runs.sort((left, right) => Date.parse(left.createdAt) - Date.parse(right.createdAt));
    this.keep(run.id, seeded.result);
  }

  /** Keeps a run's answer as the server bounds it: its first 20,000 characters, marked when cut. */
  private keep(runId: string, answer: string | null | undefined): void {
    this.results.set(runId, answer === undefined || answer === null ? null : answer.slice(0, maxResult));
    if (answer && answer.length > maxResult) {
      this.truncated.add(runId);
    }
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

function latestRun(thread: StoredThread, results: ReadonlyMap<string, string | null>): AgentLatestRun | null {
  const run = thread.runs.at(-1);
  if (!run) {
    return null;
  }
  // The stored answer first, as the server reads it; without one, the summary the run ended with.
  const result = results.get(run.id) ?? run.summary;
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

/**
 * Reads a run history's filter and page as the server does; `null` when a part is invalid. People's
 * history takes an agent, the administration's runs view names its agent in the path.
 */
function readFilter(query: URLSearchParams, withAgent: boolean): RunFilter | null {
  const page = query.has('page') ? Number(query.get('page')) : 1;
  const itemsPerPage = query.has('itemsPerPage') ? Number(query.get('itemsPerPage')) : 20;
  if (!Number.isInteger(page) || page < 1 || !Number.isInteger(itemsPerPage) || itemsPerPage < 1 || itemsPerPage > maxItemsPerPage) {
    return null;
  }
  const agentId = withAgent ? query.get('agentId') : null;
  if (agentId !== null && (agentId.trim().length === 0 || agentId.length > 128)) {
    return null;
  }
  const subjectType = query.get('subjectType');
  if (subjectType !== null && (!dashCase.test(subjectType) || subjectType.length > 100)) {
    return null;
  }
  const subjectId = query.get('subjectId');
  if (subjectId !== null && (subjectType === null || subjectId.trim().length === 0 || subjectId.length > maxSubjectId || subjectId.trim() !== subjectId)) {
    return null;
  }
  const status = query.get('status');
  if (status !== null && !runStatuses.includes(status as AgentRunStatus)) {
    return null;
  }
  const from = query.has('from') ? Date.parse(query.get('from')!) : null;
  const to = query.has('to') ? Date.parse(query.get('to')!) : null;
  if ((from !== null && Number.isNaN(from)) || (to !== null && Number.isNaN(to)) || (from !== null && to !== null && to <= from)) {
    return null;
  }
  return { agentId, subjectType, subjectId, status, from, to, page, itemsPerPage };
}
