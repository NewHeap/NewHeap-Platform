import {
  AdminAgent,
  AgentLatestRun,
  AgentProposal,
  AgentObjectAgent,
  AgentObjectAgents,
  AgentRun,
  AgentRunHistory,
  AgentRunHistoryItem,
  AgentRunMcpSkip,
  AgentRunResult,
  AgentRunStatus,
  AgentThread,
  AgentThreadLookup,
  AgentTriggerReceipt
} from '@newheap/platform-ai-chat';
import { NhAssistantMockRun, NhAssistantMockRunNow, NhAssistantMockScenario } from './nh-assistant-mock.models';
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

/** A request's agent and subject, read as the server reads them, or why they cannot be. */
type SubjectRead =
  | { failure: NhAssistantMockResult }
  | { agent: AdminAgent; subject: NhAssistantMockDescribedSubject | null };

const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const maxMessage = 8_000;
const maxSubjectId = 200;
const maxAgentId = 128;
const resultPreview = 300;
const maxResult = 20_000;
const maxItemsPerPage = 50;
const dayMs = 24 * 60 * 60 * 1000;
const activeStatuses = ['queued', 'running', 'waiting-for-approval'];
const retryableStatuses = ['failed', 'cancelled'];
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
 * scope; its required policy; shadow for administrators only), then the agent's state: a stopped
 * agent or one disabled in code is refused with `409`, a paused one answers `202 waiting` with
 * `assistant-agent-paused`, and `runNow.dailyLimit` refuses more runs a day with `409
 * assistant-trigger-rate-limited`. Otherwise it records a run that ends at once as the scenario's
 * `runNow` says, and `GET subjects/{type}/{id}/agents`, `GET threads` and the thread's runs show it.
 * `POST threads/open` creates the thread without a run for whoever may start the agent;
 * `POST runs/{id}/cancel` and `POST runs/{id}/retry` cancel, resume or restart a run with the server's
 * roles. The thread's memory, artifacts and consultations are empty; its proposals are those the
 * scenario lists in it. The scenario's `runs` happened before; `GET runs`, `GET runs/{id}` and the
 * administration's `GET admin/agents/{id}/runs` list them and the runs by hand, with the server's
 * filters, field errors and visibility.
 */
export class NhAssistantMockAgentRuns {
  private readonly threads = new Map<string, StoredThread>();
  /** The answer of each run, by run id, at most 20,000 characters. */
  private readonly results = new Map<string, string | null>();
  /** The runs whose answer was longer and was cut. */
  private readonly truncated = new Set<string>();
  /** The personal MCP servers each run left out, by run id. */
  private readonly mcpSkips = new Map<string, AgentRunMcpSkip[]>();
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

  /**
   * `GET runs`, `GET runs/{id}`, `GET runs/{id}/artifacts/{id}/content`, `POST runs/{id}/cancel` and
   * `POST runs/{id}/retry`; `null` for other paths.
   */
  handleRuns(method: string, segments: string[], query: URLSearchParams, body: unknown, language: string | null): NhAssistantMockResult | null {
    if (segments[0] !== 'runs' || ![1, 2, 3, 5].includes(segments.length)) {
      return null;
    }
    if (segments.length === 3) {
      if (segments[2] !== 'cancel' && segments[2] !== 'retry') {
        return null;
      }
      if (method !== 'POST') {
        return { status: 405 };
      }
      return segments[2] === 'cancel' ? this.cancel(segments[1], language) : this.retry(segments[1], body, language);
    }
    if (method !== 'GET') {
      return segments.length === 1 || segments.length === 2 ? { status: 405 } : null;
    }
    if (segments.length === 1) {
      const read = readFilter(query, true);
      if ('errors' in read) {
        return failure('assistant-run-filter-invalid', read.errors);
      }
      return { status: 200, body: this.history(read.filter, language, false) };
    }

    const found = this.findRun(segments[1]);
    if (!found || !(this.context.administers() || this.mayView(found.thread, language))) {
      return failure('assistant-run-not-found');
    }
    if (segments.length === 5) {
      // The mock's runs save no files.
      return segments[2] === 'artifacts' && segments[4] === 'content' ? failure('assistant-artifact-not-found') : null;
    }

    const result: AgentRunResult = {
      run: this.toHistoryItem(found.thread, found.run, language),
      result: this.results.get(found.run.id) ?? null,
      resultTruncated: this.truncated.has(found.run.id),
      summary: found.run.summary,
      proposals: [],
      hiddenProposals: 0,
      artifacts: [],
      canOpenConversation: this.mayView(found.thread, language)
    };
    return { status: 200, body: result };
  }

  /**
   * `GET admin/agents/{id}/runs`: every run of the agent, for administrators. The filter is checked
   * before the agent, as on the server.
   */
  handleAgentRuns(method: string, agentId: string, query: URLSearchParams, language: string | null): NhAssistantMockResult {
    if (method !== 'GET') {
      return { status: 405 };
    }
    if (!this.context.administers()) {
      return failure('assistant-forbidden');
    }
    const read = readFilter(query, false);
    if ('errors' in read) {
      return failure('assistant-run-filter-invalid', read.errors);
    }
    if (!this.context.agents().some(agent => agent.id === agentId)) {
      return failure('assistant-agent-not-found');
    }
    return { status: 200, body: this.history({ ...read.filter, agentId }, language, true) };
  }

  /** `POST threads/start`, `POST threads/open`, `GET threads` and `GET threads/{id}/…`; `null` for other paths. */
  handleThreads(method: string, segments: string[], query: URLSearchParams, body: unknown, language: string | null): NhAssistantMockResult | null {
    if (segments.length === 2 && segments[1] === 'start') {
      return method === 'POST' ? this.start(body, language) : { status: 405 };
    }
    if (segments.length === 2 && segments[1] === 'open') {
      return method === 'POST' ? this.open(body, language) : { status: 405 };
    }
    if (segments.length === 1) {
      return method === 'GET' ? this.lookup(query, language) : { status: 405 };
    }
    if (segments.length === 3 && method === 'GET') {
      const listed = segments[2] === 'proposals' ? this.context.threadProposals?.(segments[1]) ?? null : null;
      if (listed) {
        return { status: 200, body: listed };
      }
      const thread = this.threadOf(segments[1]);
      if (!thread) {
        return failure('assistant-conversation-not-found');
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
      return failure('assistant-subject-invalid');
    }
    const subject = this.subjects.describe(type, id, language, this.context.administers());
    if (subject === undefined) {
      return failure('assistant-subject-type-unknown');
    }
    if (subject === null) {
      return failure('assistant-subject-not-found');
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
          canStart: this.canStart(agent, subject),
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

  /**
   * An agent's work on one subject of the whole application, as a board tile shows it: its thread,
   * latest run, open proposals (those the runs left and those the scenario lists in the thread) and
   * whether the caller may run it there now.
   */
  boardWork(agent: AdminAgent, subject: NhAssistantMockDescribedSubject): NhAssistantMockBoardWork {
    const thread = this.threads.get(key(agent.id, subject));
    const listed = thread ? (this.context.threadProposals?.(thread.conversationId) ?? []).filter(proposal => proposal.status === 'pending').length : 0;
    return {
      conversationId: thread?.conversationId ?? null,
      latestRun: thread ? latestRun(thread, this.results) : null,
      openProposals: (thread?.openProposals ?? 0) + listed,
      canStart: this.canStart(agent, subject)
    };
  }

  /**
   * Whether the agent works for people now and the caller sees its results for the whole application:
   * its view policy, by default its required policy.
   */
  maySeeResults(agent: AdminAgent): boolean {
    return this.isWorking(agent) && this.passesViewPolicy(agent);
  }

  /** Whether the agent works for people now and the caller may use it. */
  mayUseNow(agent: AdminAgent): boolean {
    return this.isWorking(agent) && this.mayUse(agent);
  }

  /** The agent's name as people read it: a translation key in code, the text of an agent created as data. */
  nameOf(agent: AdminAgent): { displayNameKey: string; descriptionKey: string } {
    return {
      displayNameKey: this.nameKeyOf(agent),
      descriptionKey: agent.source === 'code'
        ? this.scenario.agents.find(item => item.id === agent.id)?.descriptionKey ?? agent.description
        : agent.description
    };
  }

  private start(body: unknown, language: string | null): NhAssistantMockResult {
    const request = body as { agentId?: unknown; subjectType?: unknown; subjectId?: unknown; message?: unknown } | undefined;
    if (!request || (request.message !== undefined && request.message !== null && (typeof request.message !== 'string' || request.message.length > maxMessage))) {
      return failure('assistant-message-invalid');
    }
    const read = this.readSubject(request.agentId, request.subjectType, request.subjectId, language);
    if ('failure' in read) {
      return read.failure;
    }
    // A subject the caller may not read gives no role on it.
    if (read.subject === null) {
      return failure('assistant-subject-access-denied');
    }
    const refusal = this.refusal(read.agent, read.subject);
    if (refusal) {
      return failure(refusal);
    }

    return this.startNow(read.agent, read.subject);
  }

  /**
   * Starts a run by hand after the start checks, as the server's triggers do: the agent's state, the
   * daily limit, then the run with the scenario's outcome. A paused agent answers `waiting`.
   */
  private startNow(agent: AdminAgent, subject: NhAssistantMockDescribedSubject): NhAssistantMockResult {
    const stateRefusal = refusalOfState(agent);
    if (stateRefusal) {
      return failure(stateRefusal);
    }

    const runNow: NhAssistantMockRunNow = this.scenario.runNow?.[agent.id] ?? {};
    const threadKey = key(agent.id, subject);
    const existing = this.threads.get(threadKey);
    if (runNow.dailyLimit !== undefined && existing && this.runsByHandToday(existing) >= runNow.dailyLimit) {
      return failure('assistant-trigger-rate-limited');
    }

    const outcome = runNow.outcome ?? 'started';
    if (outcome === 'ignored' || outcome === 'deferred') {
      // Nothing started yet: a deferred start names no run or thread until the change is saved.
      return accepted({ triggerId: 'manual', outcome, runId: null, conversationId: null, reasonCode: null });
    }

    const thread = existing ?? { conversationId: this.nextId(), agentId: agent.id, subject, runs: [], openProposals: 0 };
    this.threads.set(threadKey, thread);
    if (agent.runState === 'paused' || outcome === 'waiting') {
      // The start waits in the agent's thread and runs when the agent, or its skill, resumes.
      const reasonCode = agent.runState === 'paused' ? 'assistant-agent-paused' : 'assistant-skill-paused';
      return accepted({ triggerId: 'manual', outcome: 'waiting', runId: null, conversationId: thread.conversationId, reasonCode });
    }

    const run = this.recordRun(thread, agent, runNow);
    return accepted({ triggerId: 'manual', outcome, runId: run.id, conversationId: thread.conversationId, reasonCode: null });
  }

  /** A run by hand that ends at once with the scenario's status, result and proposals. */
  private recordRun(thread: StoredThread, agent: AdminAgent, runNow: NhAssistantMockRunNow): AgentRun {
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
    return run;
  }

  /** The runs by hand of a thread in the last day, which the daily limit counts. */
  private runsByHandToday(thread: StoredThread): number {
    const now = Date.parse(this.now());
    return thread.runs.filter(run => run.triggerId === 'manual' && now - Date.parse(run.createdAt) < dayMs).length;
  }

  /**
   * `POST threads/open`: the agent's thread about the subject, created without a run when it is
   * missing. Readers open an existing thread; creating one needs what a start by hand needs, and a
   * refusal answers `409` for what the agent does not work on and `403` otherwise.
   */
  private open(body: unknown, language: string | null): NhAssistantMockResult {
    const request = body as { agentId?: unknown; subjectType?: unknown; subjectId?: unknown } | undefined;
    if (!request || typeof request !== 'object') {
      return failure('assistant-subject-invalid');
    }
    const read = this.readSubject(request.agentId, request.subjectType, request.subjectId, language);
    if ('failure' in read) {
      return read.failure;
    }
    const { agent, subject } = read;
    if (subject === null || !this.mayReadThreadOf(agent, subject)) {
      return failure('assistant-subject-not-found');
    }
    if (agent.shadowOf && !this.context.administers()) {
      return failure('assistant-agent-not-found');
    }

    const threadKey = key(agent.id, subject);
    let thread = this.threads.get(threadKey);
    if (!thread) {
      const refusal = this.refusal(agent, subject);
      if (refusal) {
        return failure(refusal === 'assistant-agent-does-not-work-on' ? refusal : 'assistant-subject-access-denied');
      }
      thread = { conversationId: this.nextId(), agentId: agent.id, subject, runs: [], openProposals: 0 };
      this.threads.set(threadKey, thread);
    }

    const lookup: AgentThreadLookup = { thread: toThread(thread), canStart: this.canStart(agent, subject), role: subject.role };
    return { status: 200, body: lookup };
  }

  private lookup(query: URLSearchParams, language: string | null): NhAssistantMockResult {
    const read = this.readSubject(query.get('agentId'), query.get('subjectType') ?? '', query.get('subjectId') ?? '', language);
    if ('failure' in read) {
      return read.failure;
    }
    const { agent, subject } = read;
    if (subject === null || !this.mayReadThreadOf(agent, subject)) {
      return failure('assistant-subject-not-found');
    }
    if (agent.shadowOf && !this.context.administers()) {
      return failure('assistant-agent-not-found');
    }

    const thread = this.threads.get(key(agent.id, subject));
    const body: AgentThreadLookup = {
      thread: thread ? toThread(thread) : null,
      canStart: this.canStart(agent, subject),
      role: subject.role
    };
    return { status: 200, body };
  }

  /**
   * `POST runs/{id}/cancel`: cancels an open run and withdraws its open proposals; needs the
   * participant role on the thread. An ended run stays as it is.
   */
  private cancel(runId: string, language: string | null): NhAssistantMockResult {
    const found = this.findRun(runId);
    if (!found) {
      return failure('assistant-run-not-found');
    }
    const role = this.roleOn(found.thread, language);
    if (role === null) {
      return failure('assistant-conversation-not-found');
    }
    if (role === 'reader') {
      return failure('assistant-subject-access-denied');
    }
    if (activeStatuses.includes(found.run.status)) {
      found.run.status = 'cancelled';
      found.run.endedAt = this.now();
      found.thread.openProposals = Math.max(0, found.thread.openProposals - found.run.proposalCount);
    }
    return { status: 202 };
  }

  /**
   * `POST runs/{id}/retry`: resumes a failed or cancelled run (`queued`), or restarts the agent in the
   * same thread like a start by hand; needs the approver role on the thread. The mock's resumed
   * attempt ends at once as the scenario's `runNow` says.
   */
  private retry(runId: string, body: unknown, language: string | null): NhAssistantMockResult {
    const mode = (body as { mode?: unknown } | undefined)?.mode ?? 'resume';
    if (mode !== 'resume' && mode !== 'restart') {
      return failure('assistant-run-not-retryable');
    }
    const found = this.findRun(runId);
    if (!found) {
      return failure('assistant-run-not-found');
    }
    const role = this.roleOn(found.thread, language);
    if (role === null) {
      return failure('assistant-conversation-not-found');
    }
    if (role !== 'approver') {
      return failure('assistant-subject-access-denied');
    }
    if (!retryableStatuses.includes(found.run.status)) {
      return failure('assistant-run-not-retryable');
    }

    const agent = this.context.agents().find(item => item.id === found.thread.agentId);
    if (!agent) {
      return failure('assistant-agent-not-found');
    }
    if (mode === 'restart') {
      return this.startNow(agent, found.thread.subject);
    }

    const runNow = this.scenario.runNow?.[agent.id] ?? {};
    const status = runNow.status ?? 'completed';
    const now = this.now();
    found.run.status = status;
    found.run.attempt += 1;
    found.run.failureClass = status === 'failed' ? 'permanent' : null;
    found.run.failureCode = status === 'failed' ? 'assistant-turn-failed' : null;
    found.run.startedAt = now;
    found.run.endedAt = activeStatuses.includes(status) ? null : now;
    return accepted({ triggerId: found.run.triggerId, outcome: 'queued', runId: found.run.id, conversationId: found.run.conversationId, reasonCode: null });
  }

  /**
   * The request's agent and subject as the server reads them: an unknown agent is not found, a
   * malformed subject reference invalid and an unknown type unknown. `subject` is `null` for a subject
   * the caller may not read.
   */
  private readSubject(agentId: unknown, subjectType: unknown, subjectId: unknown, language: string | null): SubjectRead {
    const agent = typeof agentId === 'string' ? this.context.agents().find(item => item.id === agentId) : undefined;
    if (!agent) {
      return { failure: failure('assistant-agent-not-found') };
    }
    const type = typeof subjectType === 'string' ? subjectType : '';
    const id = typeof subjectId === 'string' ? subjectId : '';
    if (!validSubject(type, id)) {
      return { failure: failure('assistant-subject-invalid') };
    }
    const subject = this.subjects.describe(type, id, language, this.context.administers());
    if (subject === undefined) {
      return { failure: failure('assistant-subject-type-unknown') };
    }
    return { agent, subject };
  }

  /**
   * Why the caller may not run the agent on the subject, in the server's order: the role, the agent,
   * what it works on, then the run policy and shadow. The agent's state is checked when the run
   * starts.
   */
  private refusal(agent: AdminAgent, subject: NhAssistantMockDescribedSubject): string | null {
    const system = subject.subjectType === mockSystemSubjectType;
    if (!system && subject.role === 'reader') {
      return 'assistant-subject-access-denied';
    }
    if (!this.mayUse(agent)) {
      return 'assistant-agent-forbidden';
    }
    if (!worksOn(agent, subject)) {
      return 'assistant-agent-does-not-work-on';
    }
    if (system && !(agent.runPolicy ? this.passes(agent.runPolicy) : this.context.administers())) {
      return 'assistant-agent-forbidden';
    }
    if (agent.shadow?.shadow && !this.context.administers()) {
      return 'assistant-agent-forbidden';
    }
    return null;
  }

  /** Whether the caller may start the agent now, as `canStart` says: it works and nothing refuses it. */
  private canStart(agent: AdminAgent, subject: NhAssistantMockDescribedSubject): boolean {
    return this.isWorking(agent) && this.refusal(agent, subject) === null;
  }

  /** The caller's live role on a thread, or `null` when the caller may not see it. */
  private roleOn(thread: StoredThread, language: string | null): 'reader' | 'participant' | 'approver' | null {
    if (!this.mayView(thread, language)) {
      return null;
    }
    const subject = this.subjects.describe(thread.subject.subjectType, thread.subject.subjectId, language, this.context.administers());
    return subject?.role ?? null;
  }

  /** The agent's thread about the whole application is only for people who pass its view policy. */
  private mayReadThreadOf(agent: AdminAgent, subject: NhAssistantMockDescribedSubject): boolean {
    return subject.scope === 'object' || this.passesViewPolicy(agent);
  }

  /** A run history's page of the runs the caller may see, or with `everything` every run, newest first. */
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
    return agent !== undefined && this.passesViewPolicy(agent);
  }

  private threadOf(conversationId: string): StoredThread | undefined {
    return [...this.threads.values()].find(item => item.conversationId === conversationId);
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
      endedAt: run.endedAt,
      mcpConnectionSkips: this.mcpSkips.get(run.id) ?? []
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
      failureCode: status === 'failed' ? seeded.failureCode ?? 'assistant-turn-failed' : null,
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
    if (seeded.mcpConnectionSkips?.length) {
      this.mcpSkips.set(run.id, seeded.mcpConnectionSkips.map(skip => ({ ...skip })));
    }
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

  /** The agent's view policy, by default its required policy, for its work on the whole application. */
  private passesViewPolicy(agent: AdminAgent): boolean {
    const policy = agent.viewPolicy ?? agent.requiredPolicy ?? null;
    return policy === null || this.passes(policy);
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

/** An agent's work on one subject of the whole application for a board tile. */
export interface NhAssistantMockBoardWork {
  conversationId: string | null;
  latestRun: AgentLatestRun | null;
  openProposals: number;
  canStart: boolean;
}

/** The receipt of a start the server accepted. */
function accepted(receipt: AgentTriggerReceipt): NhAssistantMockResult {
  return { status: 202, body: receipt };
}

/** Why the agent's state refuses a start: stopped, or disabled in the application's code. */
function refusalOfState(agent: AdminAgent): string | null {
  switch (agent.runState) {
    case 'stopped':
      return 'assistant-agent-stopped';
    case 'configured-off':
      return 'assistant-agent-configured-off';
    default:
      return null;
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
 * Reads a run history's filter and page as the server does: the filter, or the refused parts as field
 * errors (`page`, `itemsPerPage`, `agentId`, `subjectType`, `subjectId`, `status`, `from`, `to`).
 * People's history takes an agent; the administration's runs view names its agent in the path.
 */
function readFilter(query: URLSearchParams, withAgent: boolean): { filter: RunFilter } | { errors: Record<string, string[]> } {
  const errors: Record<string, string[]> = {};
  const page = query.has('page') ? Number(query.get('page')) : 1;
  if (!Number.isInteger(page) || page < 1) {
    errors['page'] = ['invalid'];
  }
  const itemsPerPage = query.has('itemsPerPage') ? Number(query.get('itemsPerPage')) : 20;
  if (!Number.isInteger(itemsPerPage) || itemsPerPage < 1 || itemsPerPage > maxItemsPerPage) {
    errors['itemsPerPage'] = ['invalid'];
  }
  const agentId = withAgent ? query.get('agentId') : null;
  if (agentId !== null && (agentId.trim().length === 0 || agentId.length > maxAgentId)) {
    errors['agentId'] = ['invalid'];
  }
  const subjectType = query.get('subjectType');
  if (subjectType !== null && (!dashCase.test(subjectType) || subjectType.length > 100)) {
    errors['subjectType'] = ['invalid'];
  }
  const subjectId = query.get('subjectId');
  if (subjectId !== null) {
    if (subjectType === null) {
      errors['subjectType'] = ['required'];
    }
    if (subjectId.trim().length === 0 || subjectId.length > maxSubjectId || subjectId.trim() !== subjectId) {
      errors['subjectId'] = ['invalid'];
    }
  }
  const status = query.get('status');
  if (status !== null && !runStatuses.includes(status as AgentRunStatus)) {
    errors['status'] = ['invalid'];
  }
  const from = query.has('from') ? Date.parse(query.get('from')!) : null;
  const to = query.has('to') ? Date.parse(query.get('to')!) : null;
  if (from !== null && Number.isNaN(from)) {
    errors['from'] = ['invalid'];
  }
  if ((to !== null && Number.isNaN(to)) || (from !== null && to !== null && !Number.isNaN(from) && to <= from)) {
    errors['to'] = ['invalid'];
  }

  if (Object.keys(errors).length > 0) {
    return { errors };
  }
  return { filter: { agentId, subjectType, subjectId, status, from, to, page, itemsPerPage } };
}
