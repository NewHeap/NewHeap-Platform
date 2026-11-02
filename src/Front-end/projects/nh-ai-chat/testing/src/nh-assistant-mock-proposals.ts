import {
  AgentBulkDecisionItem,
  AgentBulkDecisionKind,
  AgentBulkDecisionOverview,
  AgentBulkDecisionResult,
  AgentDecisionOutcome,
  AgentInbox,
  AgentInboxGroup,
  AgentInboxStatus,
  AgentInboxSummary,
  AgentInboxVisit,
  AgentProposal,
  AgentProposalLookup,
  AgentSubjectWorkProposal,
  NH_ASSISTANT_MAX_PROPOSAL_LOOKUP,
  NhAssistantConsultationRoute
} from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';
import { NhAssistantMockProposal } from './nh-assistant-mock.models';

interface StoredProposal {
  proposal: AgentProposal;
  canDecide: boolean;
  reasonCode: string | null;
  divisionId: string | null;
  /** The application executes the proposal's tool (`ExecuteInApplication`). */
  executedByApplication: boolean;
  /** The proposal's tool is decided one proposal at a time (`RequireIndividualDecision`). */
  individual: boolean;
}

/** The set of a bulk decision's overview, which its token decides once. */
interface StoredBulkDecision {
  actorId: string;
  items: { approvalId: string; proposalHash: string }[];
  expiresAt: string;
  result: AgentBulkDecisionResult | null;
}

/** How long the application may take to report after a hand-off in the mock. */
const reportWithinMs = 4 * 60 * 60 * 1000;

const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const maxReason = 500;
const maxItemsPerPage = 100;
const maxBulkDecisions = 50;
/** The most kinds of action one inbox request filters on. */
const maxToolFilters = 20;
/** How far ahead the inbox summary counts what expires soon. */
const expiringSoonMs = 24 * 60 * 60 * 1000;
/** The most proposals one set of a bulk decision holds. */
const maxBulkDecisionSet = 200;
/** How long the set of a bulk decision's overview may be decided. */
const bulkDecisionLifetimeMs = 15 * 60 * 1000;
const guid = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;
const lifetimeMs = 3 * 24 * 60 * 60 * 1000;
/** The platform's subject type of the whole application. */
const systemType = 'nh-system';
/** The person who asked decides a consultation's proposal in their own chat; nobody decides it in the thread. */
const askerDecidesCode = 'assistant-consult-asker-decides';
/** Someone other than the person who started the work decides it. */
const fourEyesCode = 'assistant-four-eyes-required';

/**
 * The proposals the scenario lists, which the mock's agents left, for example one a run for the whole
 * application placed at a project (`placedBy`). The mock runs no agents: it serves them as the server
 * does in the approval inbox (`GET approvals/inbox`, pending proposals the caller may decide, with its
 * filters on kind of action and expiry and its sorts), counted (`GET approvals/inbox/summary`), one by id
 * (`GET approvals/{id}`), several by id (`POST approvals/lookup`), in their thread
 * (`GET threads/{id}/proposals`) and on their subject's work, and decides them one by one or together
 * with the server's validation and statuses. An approved proposal executes at once. A proposal of the
 * whole application (`nh-system`) exists only for the approvers of the whole application who pass the
 * agent's view policy (`mayDecideApplication`): for anybody else the inbox leaves it out, reading it
 * answers `404` and deciding it `404 assistant-approval-not-found`. A consultation's proposal that the
 * person who asked decides in their own chat (`asker-decides`) waits inline and is refused here with
 * `assistant-consult-asker-decides`; one that runs with the agent's own rights refuses its asker with
 * `assistant-four-eyes-required`. A proposal of a tool the application executes (`executedByApplication`)
 * waits as `awaiting-application` once approved, and `POST approvals/{id}/resolve` resolves an
 * `execution-unknown` proposal as the server does: `hand-to-application` for one the application
 * executes, `re-execute` for any other, `mark-resolved` for both. A bulk decision of a filter
 * (`POST approvals/bulk-decisions/overview` and `POST approvals/bulk-decisions`) decides the set of its
 * overview once for the person who asked for it, leaves proposals decided one by one
 * (`requireIndividualDecision`) out, reports a repeat and refuses an expired set, as the server does;
 * deciding several by id checks the proposal hashes sent with them.
 */
export class NhAssistantMockProposals {
  private readonly proposals = new Map<string, StoredProposal>();
  /** The sets of bulk decisions by token. */
  private readonly bulkDecisions = new Map<string, StoredBulkDecision>();
  /** When the caller last looked at the inbox, by scope key. */
  private readonly visits = new Map<string, string>();

  constructor(
    scenario: readonly NhAssistantMockProposal[] = [],
    private readonly actorId: () => string,
    private readonly now: () => string,
    private readonly mayDecideApplication: (agentId: string) => boolean = () => true,
    private readonly decided: (proposal: AgentProposal) => void = () => undefined,
    private readonly mayWorkIn: (divisionId: string) => boolean = () => true
  ) {
    const created = new Date(now()).getTime();
    for (const item of scenario) {
      if (this.proposals.has(item.approvalId)) {
        throw new Error(`Mock proposal '${item.approvalId}' is listed twice.`);
      }
      if (!dashCase.test(item.subjectType)) {
        throw new Error(`Mock proposal '${item.approvalId}' names subject type '${item.subjectType}', which is not dash-case.`);
      }
      if (item.consultationRoute && !item.consultationId) {
        throw new Error(`Mock proposal '${item.approvalId}' names a consultation route without its consultation.`);
      }
      const route = item.consultationRoute ?? null;
      const refusal = consultationRefusal(route, item.initiatorActorId ?? null, actorId());
      const canDecide = item.canDecide ?? refusal === null;
      const executedByApplication = item.executedByApplication ?? false;
      const handedOff = executedByApplication && (item.status === 'awaiting-application' || item.status === 'execution-unknown');
      const madeAt = item.createdAt ?? new Date(created).toISOString();
      this.proposals.set(item.approvalId, {
        proposal: {
          approvalId: item.approvalId,
          proposalId: `${item.approvalId}-proposal`,
          proposalHash: `sha256:${item.approvalId}`,
          conversationId: item.conversationId ?? `${item.approvalId}-thread`,
          runId: item.runId ?? null,
          agentId: item.agentId,
          subjectType: item.subjectType,
          subjectId: item.subjectId,
          subjectDisplayName: item.subjectDisplayName ?? null,
          subjectTypeName: item.subjectTypeName ?? null,
          subjectLink: item.subjectLink ?? null,
          toolId: item.toolId,
          summary: 'Approval is required before this tool can run.',
          presentation: item.presentation ?? null,
          argumentsPreview: '{}',
          targets: [],
          status: item.status ?? 'pending',
          mode: route === 'asker-decides' ? 'inline' : 'deferred',
          policyId: null,
          grantSource: item.grantSource ?? (route === 'subject-asker-rights' ? 'delegated' : 'agent'),
          createdAt: item.createdAt ?? new Date(created).toISOString(),
          expiresAt: item.expiresAt ?? new Date(Date.parse(item.createdAt ?? new Date(created).toISOString()) + lifetimeMs).toISOString(),
          initiatorActorId: item.initiatorActorId ?? null,
          decidedByActorId: null,
          decidedAt: null,
          executionResultCode: item.executionResultCode ?? null,
          executedAt: null,
          executionReportDueAt: handedOff
            ? item.executionReportDueAt ?? new Date(Date.parse(madeAt) + reportWithinMs).toISOString()
            : null,
          rationale: item.rationale ?? null,
          placedBy: item.placedBy ?? null,
          parent: item.parent ?? null,
          canGiveFeedback: false,
          consultationId: item.consultationId ?? null,
          consultationRoute: route,
          ...(item.supersededByApprovalId ? { supersededByApprovalId: item.supersededByApprovalId } : {})
        },
        divisionId: item.divisionId ?? null,
        canDecide,
        reasonCode: canDecide ? null : item.reasonCode ?? refusal ?? 'assistant-approval-policy-denied',
        executedByApplication,
        individual: item.requireIndividualDecision ?? false
      });
    }
  }

  /** `GET`/`POST approvals/…`; `null` for paths the mock does not serve. */
  handle(method: string, segments: string[], query: URLSearchParams, body: unknown): NhAssistantMockResult | null {
    if (segments.length === 2 && segments[1] === 'inbox') {
      return method === 'GET' ? this.inbox(query) : { status: 405 };
    }
    if (segments.length === 3 && segments[1] === 'inbox' && segments[2] === 'summary') {
      return method === 'GET' ? this.summary(query) : { status: 405 };
    }
    if (segments.length === 3 && segments[1] === 'inbox' && segments[2] === 'visit') {
      return method === 'POST' ? this.visit(query) : { status: 405 };
    }
    if (segments.length === 2 && segments[1] === 'lookup') {
      return method === 'POST' ? this.lookup(body) : { status: 405 };
    }
    if (segments.length === 2 && segments[1] === 'decisions') {
      return method === 'POST' ? this.decideMany(body) : { status: 405 };
    }
    if (segments.length === 3 && segments[1] === 'bulk-decisions' && segments[2] === 'overview') {
      return method === 'POST' ? this.bulkOverview(body) : { status: 405 };
    }
    if (segments.length === 2 && segments[1] === 'bulk-decisions') {
      return method === 'POST' ? this.decideBulk(body) : { status: 405 };
    }
    if (segments.length === 2) {
      return method === 'GET' ? this.read(segments[1]) : { status: 405 };
    }
    if (segments.length === 3 && segments[2] === 'decisions') {
      return method === 'POST' ? this.decideOne(segments[1], body) : { status: 405 };
    }
    if (segments.length === 3 && segments[2] === 'resolve') {
      return method === 'POST' ? this.resolve(segments[1], body) : { status: 405 };
    }
    return null;
  }

  /** The proposals waiting in a thread, newest first; `null` when the scenario lists none there. */
  ofThread(conversationId: string): AgentProposal[] | null {
    const items = this.visible()
      .filter(item => item.proposal.conversationId === conversationId)
      .map(item => item.proposal)
      .reverse();
    return items.length === 0 ? null : items;
  }

  /** The proposals a consultation left per route; routes without proposals are left out. */
  consultationCounts(consultationId: string): { total: number; byRoute: Partial<Record<NhAssistantConsultationRoute, number>> } {
    const byRoute: Partial<Record<NhAssistantConsultationRoute, number>> = {};
    let total = 0;
    for (const { proposal } of this.proposals.values()) {
      if (proposal.consultationId !== consultationId) {
        continue;
      }
      total++;
      if (proposal.consultationRoute) {
        byRoute[proposal.consultationRoute] = (byRoute[proposal.consultationRoute] ?? 0) + 1;
      }
    }
    return { total, byRoute };
  }

  /** The proposals of a run the caller may see, in the order the scenario lists them. */
  ofRun(runId: string): AgentProposal[] {
    return this.visible()
      .filter(item => item.proposal.runId === runId)
      .map(item => item.proposal);
  }

  /** The pending proposals on a subject with the caller's right to decide each, earliest expiry first. */
  openOfSubject(subjectType: string, subjectId: string): AgentSubjectWorkProposal[] {
    return this.visible()
      .filter(item => item.proposal.subjectType === subjectType && item.proposal.subjectId === subjectId && item.proposal.status === 'pending')
      .map(item => this.withRight(item));
  }

  /** The proposals the caller may see: those of the whole application only for its approvers. */
  private visible(): StoredProposal[] {
    return [...this.proposals.values()].filter(item => this.isVisible(item));
  }

  private isVisible(item: StoredProposal): boolean {
    return item.proposal.subjectType !== systemType || this.mayDecideApplication(item.proposal.agentId);
  }

  /** A proposal by id that the caller may see. */
  private find(approvalId: string): StoredProposal | undefined {
    const stored = this.proposals.get(approvalId);
    return stored && this.isVisible(stored) ? stored : undefined;
  }

  /**
   * The proposals the inbox lists with the request's filter, in its order, as the server filters and
   * sorts them; a failure for a filter the server refuses.
   */
  private listed(query: URLSearchParams): { items: AgentProposal[]; status: AgentInboxStatus; divisionId: string | null } | NhAssistantMockResult {
    const subjectType = query.get('subjectType');
    if (subjectType !== null && !dashCase.test(subjectType)) {
      return failure('assistant-message-invalid');
    }

    const agentId = query.get('agentId');
    const runId = query.get('runId');
    const divisionId = query.get('divisionId');
    if (divisionId !== null && !guid.test(divisionId)) {
      return { status: 400 };
    }

    // What waits for a decision by default; what waits for the application's report or for an
    // approver to resolve an uncertain execution on request, with the same visibility.
    const status = query.get('status') || 'pending';
    if (!isInboxStatus(status)) {
      return failure('assistant-message-invalid');
    }

    const toolIds = [...new Set(query.getAll('toolId'))];
    const sort = query.get('sort');
    const expiresBefore = query.get('expiresBefore');
    if (toolIds.length > maxToolFilters
      || toolIds.some(toolId => toolId.trim() === '' || toolId.length > 256)
      || (sort !== null && sort !== '' && sort !== 'expiresAt' && sort !== 'createdAt')
      || (expiresBefore !== null && Number.isNaN(Date.parse(expiresBefore)))) {
      return failure('assistant-message-invalid');
    }

    const before = expiresBefore === null ? null : Date.parse(expiresBefore);
    const sortAt = (proposal: AgentProposal) => sort === 'expiresAt'
      ? proposal.expiresAt
      : sort === 'createdAt' ? proposal.createdAt : inboxSortAt(proposal, status);
    const items = this.visible()
      .filter(item => item.proposal.status === status && item.canDecide)
      .filter(item => divisionId === null || sameGuid(item.divisionId, divisionId))
      .map(item => item.proposal)
      .filter(proposal => (subjectType === null || proposal.subjectType === subjectType)
        && (agentId === null || proposal.agentId === agentId)
        && (runId === null || proposal.runId === runId)
        && (toolIds.length === 0 || toolIds.includes(proposal.toolId))
        && (before === null || Date.parse(proposal.expiresAt) < before))
      .sort((left, right) => sortAt(left).localeCompare(sortAt(right))
        || left.approvalId.localeCompare(right.approvalId));
    return { items, status, divisionId };
  }

  private inbox(query: URLSearchParams): NhAssistantMockResult {
    const page = Number(query.get('page') ?? '1');
    const itemsPerPage = Number(query.get('itemsPerPage') ?? '20');
    if (!Number.isInteger(page) || page < 1
      || !Number.isInteger(itemsPerPage) || itemsPerPage < 1 || itemsPerPage > maxItemsPerPage) {
      return failure('assistant-message-invalid');
    }

    const listed = this.listed(query);
    if (!('items' in listed)) {
      return listed;
    }

    const { items, status, divisionId } = listed;
    const grouped = query.get('grouped') === 'true';
    const ordered = grouped ? groupedOrder(items, proposal => inboxSortKey(proposal, status, query.get('sort'))) : items;
    const offset = (page - 1) * itemsPerPage;
    const pageItems = ordered.slice(offset, offset + itemsPerPage);
    const scopeKey = scopeKeyOf(divisionId);
    const lastVisitedAt = this.visits.get(scopeKey) ?? null;
    const body: AgentInbox = {
      items: pageItems,
      total: items.length,
      status,
      scopeKey,
      lastVisitedAt,
      newApprovalIds: pageItems
        .filter(proposal => lastVisitedAt === null || Date.parse(proposal.createdAt) > Date.parse(lastVisitedAt))
        .map(proposal => proposal.approvalId),
      groups: grouped ? groupsOf(ordered, offset, pageItems.length) : null
    };
    return { status: 200, body };
  }

  /**
   * `GET approvals/inbox/summary`: what the inbox lists with the same filter, counted per agent, kind of
   * action (named by the newest card's presentation, otherwise the tool id) and subject type with its
   * subjects, and how many expire within 24 hours.
   */
  private summary(query: URLSearchParams): NhAssistantMockResult {
    const listed = this.listed(query);
    if (!('items' in listed)) {
      return listed;
    }

    const { items, status } = listed;
    const expiringBefore = new Date(Date.parse(this.now()) + expiringSoonMs).toISOString();
    const body: AgentInboxSummary = {
      total: items.length,
      expiringSoon: items.filter(proposal => Date.parse(proposal.expiresAt) < Date.parse(expiringBefore)).length,
      expiringBefore,
      status,
      agents: counted(items, proposal => proposal.agentId).map(([agentId, group]) => ({ agentId, count: group.length })),
      tools: toolCounts(items),
      subjectTypes: subjectTypeCounts(items)
    };
    return { status: 200, body };
  }

  /** `POST approvals/inbox/visit`: the whole inbox, or that of a division the caller works in. */
  private visit(query: URLSearchParams): NhAssistantMockResult {
    const divisionId = query.get('divisionId');
    if (divisionId !== null && !guid.test(divisionId)) {
      return { status: 400 };
    }
    if (divisionId !== null && !this.mayWorkIn(divisionId)) {
      return failure('assistant-subject-not-found');
    }

    const body: AgentInboxVisit = { scopeKey: scopeKeyOf(divisionId), visitedAt: this.now() };
    this.visits.set(body.scopeKey, body.visitedAt);
    return { status: 200, body };
  }

  private read(approvalId: string): NhAssistantMockResult {
    const stored = this.find(approvalId);
    return stored ? { status: 200, body: this.withRight(stored) } : failure('assistant-approval-not-found');
  }

  private lookup(body: unknown): NhAssistantMockResult {
    const ids = (body as { approvalIds?: unknown } | undefined)?.approvalIds;
    if (!Array.isArray(ids) || ids.length === 0 || ids.some(id => typeof id !== 'string')) {
      return failure('assistant-message-invalid');
    }
    const distinct = [...new Set(ids as string[])];
    if (distinct.length > NH_ASSISTANT_MAX_PROPOSAL_LOOKUP) {
      return failure('assistant-message-invalid');
    }

    const response: AgentProposalLookup = {
      items: distinct
        .map(id => this.find(id))
        .filter((item): item is StoredProposal => item !== undefined)
        .map(item => this.withRight(item))
    };
    return { status: 200, body: response };
  }

  private decideOne(approvalId: string, body: unknown): NhAssistantMockResult {
    const request = body as { decision?: unknown; expectedProposalHash?: unknown; reason?: unknown } | undefined;
    if (!validDecision(request)) {
      return failure('assistant-approval-decision-invalid');
    }

    const outcome = this.decide(approvalId, request!.decision === 'approve', request!.expectedProposalHash);
    return outcome.code === null
      ? { status: 200, body: this.proposals.get(approvalId)!.proposal }
      : failure(outcome.code);
  }

  private decideMany(body: unknown): NhAssistantMockResult {
    const request = body as { approvalIds?: unknown; decision?: unknown; reason?: unknown; expectedProposalHashes?: unknown } | undefined;
    const ids = request?.approvalIds;
    if (!validDecision(request)
      || !Array.isArray(ids)
      || ids.length === 0
      || ids.length > maxBulkDecisions
      || ids.some(id => typeof id !== 'string')) {
      return failure('assistant-approval-decision-invalid');
    }

    const hashes = (request?.expectedProposalHashes ?? {}) as Record<string, unknown>;
    const results: AgentDecisionOutcome[] = [...new Set(ids as string[])].map(id => {
      const outcome = this.decide(id, request!.decision === 'approve', hashes[id] ?? null, true);
      return {
        approvalId: id,
        succeeded: outcome.code === null,
        code: outcome.code,
        status: outcome.code === null ? this.proposals.get(id)!.proposal.status : null
      };
    });
    return { status: 200, body: { results } };
  }

  /**
   * `POST approvals/bulk-decisions/overview`: the pending proposals the inbox lists with the filter, at
   * most a set's bound in the inbox's order, without those decided one by one, counted per kind of action
   * and object type, with the token of the set.
   */
  private bulkOverview(body: unknown): NhAssistantMockResult {
    const request = (body ?? {}) as Record<string, unknown>;
    const query = new URLSearchParams();
    for (const key of ['subjectType', 'agentId', 'divisionId', 'runId', 'expiresBefore', 'sort']) {
      if (typeof request[key] === 'string') {
        query.set(key, request[key] as string);
      }
    }
    if (Array.isArray(request['toolIds'])) {
      for (const toolId of request['toolIds'] as unknown[]) {
        query.append('toolId', String(toolId));
      }
    }

    const listed = this.listed(query);
    if (!('items' in listed)) {
      return listed;
    }

    const individual = listed.items.filter(proposal => this.proposals.get(proposal.approvalId)?.individual);
    const decidable = listed.items.filter(proposal => !this.proposals.get(proposal.approvalId)?.individual);
    const set = decidable.slice(0, maxBulkDecisionSet);
    let token: string | null = null;
    let expiresAt: string | null = null;
    if (set.length > 0) {
      token = `mock-bulk-${this.bulkDecisions.size + 1}`;
      expiresAt = new Date(Date.parse(this.now()) + bulkDecisionLifetimeMs).toISOString();
      this.bulkDecisions.set(token, {
        actorId: this.actorId(),
        items: set.map(proposal => ({ approvalId: proposal.approvalId, proposalHash: proposal.proposalHash })),
        expiresAt,
        result: null
      });
    }

    const response: AgentBulkDecisionOverview = {
      token,
      expiresAt,
      count: set.length,
      subjects: new Set(set.map(proposal => `${proposal.subjectType}|${proposal.subjectId}`)).size,
      listed: listed.items.length,
      remaining: decidable.length - set.length,
      limit: maxBulkDecisionSet,
      kinds: kindsOf(set),
      tools: toolCounts(set),
      subjectTypes: subjectTypeCounts(set),
      individual: { count: individual.length, tools: toolCounts(individual) }
    };
    return { status: 200, body: response };
  }

  /**
   * `POST approvals/bulk-decisions`: decides the set of a token once, each proposal with its own rules and
   * the version the overview counted; a repeat reports the stored result.
   */
  private decideBulk(body: unknown): NhAssistantMockResult {
    const request = body as { token?: unknown; decision?: unknown; reason?: unknown } | undefined;
    if (!validDecision(request) || typeof request?.token !== 'string' || request.token.trim() === '') {
      return failure('assistant-approval-decision-invalid');
    }

    const stored = this.bulkDecisions.get(request.token);
    if (!stored || stored.actorId !== this.actorId()) {
      return failure('assistant-bulk-decision-not-found');
    }
    if (stored.result) {
      return { status: 200, body: { ...stored.result, repeated: true } };
    }
    if (Date.parse(stored.expiresAt) <= Date.parse(this.now())) {
      return failure('assistant-bulk-decision-expired');
    }

    const approve = request.decision === 'approve';
    const items: AgentBulkDecisionItem[] = stored.items.map(item => {
      const outcome = this.decide(item.approvalId, approve, item.proposalHash, true);
      const code = outcome.code;
      return {
        approvalId: item.approvalId,
        outcome: code === null ? 'succeeded' : staleCodes.includes(code) ? 'stale' : 'refused',
        code,
        status: code === null ? this.proposals.get(item.approvalId)!.proposal.status : null
      };
    });
    const result: AgentBulkDecisionResult = {
      token: request.token,
      decision: approve ? 'approve' : 'reject',
      succeeded: items.filter(item => item.outcome === 'succeeded').length,
      refused: items.filter(item => item.outcome === 'refused').length,
      stale: items.filter(item => item.outcome === 'stale').length,
      repeated: false,
      items
    };
    stored.result = result;
    return { status: 200, body: result };
  }

  /**
   * Decides one proposal as the server does; an approved proposal executes at once. A decision of several
   * at once (`bulk`) leaves a proposal that is decided one by one.
   */
  private decide(approvalId: string, approve: boolean, expectedHash: unknown, bulk = false): { code: string | null } {
    const stored = this.find(approvalId);
    if (!stored) {
      return { code: 'assistant-approval-not-found' };
    }
    if (typeof expectedHash === 'string' && expectedHash !== stored.proposal.proposalHash) {
      return { code: 'assistant-proposal-hash-mismatch' };
    }
    if (stored.proposal.status !== 'pending') {
      return { code: 'assistant-approval-not-pending' };
    }
    if (bulk && stored.individual) {
      return { code: 'assistant-individual-decision-required' };
    }
    if (!stored.canDecide) {
      return { code: stored.reasonCode ?? 'assistant-approval-policy-denied' };
    }

    const now = this.now();
    // A proposal the application executes is handed to it and waits for its report.
    const handedOff = approve && stored.executedByApplication;
    stored.proposal = {
      ...stored.proposal,
      status: approve ? (handedOff ? 'awaiting-application' : 'executed') : 'rejected',
      decidedByActorId: this.actorId(),
      decidedAt: now,
      executedAt: approve && !handedOff ? now : null,
      executionReportDueAt: handedOff ? new Date(Date.parse(now) + reportWithinMs).toISOString() : null
    };
    this.decided(stored.proposal);
    return { code: null };
  }

  /**
   * `POST approvals/{id}/resolve` as the server answers it: only an `execution-unknown` proposal, which
   * a proposal the application executes resolves with `hand-to-application` or `mark-resolved` and
   * every other one with `re-execute` or `mark-resolved`. Handed to the application again, it waits
   * for its report once more; run again, it executes at once.
   */
  private resolve(approvalId: string, body: unknown): NhAssistantMockResult {
    const request = body as { action?: unknown; reason?: unknown } | undefined;
    const action = request?.action;
    if (action !== 're-execute' && action !== 'hand-to-application' && action !== 'mark-resolved') {
      return failure('assistant-approval-decision-invalid');
    }
    const stored = this.find(approvalId);
    if (!stored) {
      return failure('assistant-approval-not-found');
    }
    if (stored.proposal.status !== 'execution-unknown') {
      return failure('assistant-execution-not-uncertain');
    }
    if (!stored.canDecide) {
      return failure(stored.reasonCode ?? 'assistant-approval-policy-denied');
    }
    const handedOff = !!stored.proposal.executionReportDueAt;
    if ((action === 'hand-to-application' && !handedOff) || (action === 're-execute' && handedOff)) {
      return failure('assistant-approval-decision-invalid');
    }

    const now = this.now();
    stored.proposal = {
      ...stored.proposal,
      status: action === 'mark-resolved' ? 'resolved' : action === 'hand-to-application' ? 'awaiting-application' : 'executed',
      decidedByActorId: this.actorId(),
      executedAt: action === 're-execute' ? now : stored.proposal.executedAt,
      executionResultCode: action === 'mark-resolved' ? 'resolved-by-approver' : null,
      executionReportDueAt: action === 'hand-to-application'
        ? new Date(Date.parse(now) + reportWithinMs).toISOString()
        : stored.proposal.executionReportDueAt
    };
    this.decided(stored.proposal);
    return { status: 200, body: stored.proposal };
  }

  private withRight(item: StoredProposal): AgentSubjectWorkProposal {
    const pending = item.proposal.status === 'pending';
    return {
      proposal: item.proposal,
      canDecide: pending && item.canDecide,
      reasonCode: pending && !item.canDecide ? item.reasonCode : null
    };
  }
}

/**
 * Why nobody, or not the caller, decides a consultation's proposal here: the person who asked decides
 * an `asker-decides` proposal in their own chat, and four-eyes keeps the asker out of a proposal that
 * runs with the agent's own rights; `null` when the route does not keep the caller out.
 */
function consultationRefusal(route: NhAssistantConsultationRoute | null, initiatorActorId: string | null, actorId: string): string | null {
  if (route === 'asker-decides') {
    return askerDecidesCode;
  }
  return route === 'subject-agent-rights' && initiatorActorId === actorId ? fourEyesCode : null;
}

/** The codes of a proposal that is no longer what a bulk decision's overview showed. */
const staleCodes = [
  'assistant-proposal-hash-mismatch',
  'assistant-approval-not-pending',
  'assistant-approval-expired',
  'assistant-proposal-stale'
];

/** Per kind of action and object type how many and on how many objects, the most first. */
function kindsOf(items: readonly AgentProposal[]): AgentBulkDecisionKind[] {
  return counted(items, proposal => `${proposal.toolId}|${proposal.subjectType}`).map(([, group]) => ({
    toolId: group[0].toolId,
    toolDisplayName: toolCounts(group)[0].displayName,
    subjectType: group[0].subjectType,
    subjectTypeName: group.find(proposal => proposal.subjectTypeName)?.subjectTypeName ?? null,
    count: group.length,
    subjects: new Set(group.map(proposal => proposal.subjectId)).size
  }));
}

function isInboxStatus(status: string): status is AgentInboxStatus {
  return status === 'pending' || status === 'awaiting-application' || status === 'execution-unknown';
}

/**
 * The time the inbox lists a proposal by, as the server: a pending one by its expiry, one handed to the
 * application by its report deadline, an uncertain execution by its decision.
 */
function inboxSortAt(proposal: AgentProposal, status: AgentInboxStatus): string {
  switch (status) {
    case 'awaiting-application':
      return proposal.executionReportDueAt ?? proposal.expiresAt;
    case 'execution-unknown':
      return proposal.decidedAt ?? proposal.createdAt;
    default:
      return proposal.expiresAt;
  }
}

/** The time a sort orders by, as the server: the decision deadline, when it was made, or the status's own. */
function inboxSortKey(proposal: AgentProposal, status: AgentInboxStatus, sort: string | null): string {
  return sort === 'expiresAt' ? proposal.expiresAt : sort === 'createdAt' ? proposal.createdAt : inboxSortAt(proposal, status);
}

/** The proposals per key, the largest group first, then by key. */
function counted(items: readonly AgentProposal[], key: (proposal: AgentProposal) => string): [string, AgentProposal[]][] {
  const groups = new Map<string, AgentProposal[]>();
  for (const proposal of items) {
    groups.set(key(proposal), [...(groups.get(key(proposal)) ?? []), proposal]);
  }
  return [...groups.entries()].sort(([leftKey, left], [rightKey, right]) =>
    right.length - left.length || (leftKey < rightKey ? -1 : leftKey > rightKey ? 1 : 0));
}

/** Per kind of action how many, named by the newest card that has a presentation, otherwise by its tool id. */
function toolCounts(items: readonly AgentProposal[]): AgentInboxSummary['tools'] {
  return counted(items, proposal => proposal.toolId).map(([toolId, group]) => {
    const named = [...group]
      .filter(proposal => proposal.presentation)
      .sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0];
    return { toolId, displayName: named?.presentation?.toolDisplayName ?? toolId, count: group.length };
  });
}

/** Per subject type how many and on how many distinct subjects. */
function subjectTypeCounts(items: readonly AgentProposal[]): AgentInboxSummary['subjectTypes'] {
  return counted(items, proposal => proposal.subjectType).map(([subjectType, group]) => ({
    subjectType,
    subjectTypeName: group.find(proposal => proposal.subjectTypeName)?.subjectTypeName ?? null,
    count: group.length,
    subjects: new Set(group.map(proposal => proposal.subjectId)).size
  }));
}

/** The scope of the inbox's visits: `global`, or the scope key of a division. */
function scopeKeyOf(divisionId: string | null): string {
  return divisionId === null ? 'global' : `division:${divisionId.replaceAll('-', '').toLowerCase()}`;
}

function sameGuid(left: string | null, right: string): boolean {
  return left !== null && left.replaceAll('-', '').toLowerCase() === right.replaceAll('-', '').toLowerCase();
}

/** The group a proposal belongs to: its parent, or its own subject without one. */
function groupKeyOf(proposal: AgentProposal): string {
  return proposal.parent
    ? `${proposal.parent.subjectType}|${proposal.parent.subjectId}`
    : `${proposal.subjectType}|${proposal.subjectId}`;
}

function subjectKeyOf(proposal: AgentProposal): string {
  return `${groupKeyOf(proposal)}|${proposal.subjectType}|${proposal.subjectId}`;
}

function firstExpiries(
  items: readonly AgentProposal[],
  key: (proposal: AgentProposal) => string,
  sortAt: (proposal: AgentProposal) => string
): Map<string, string> {
  const first = new Map<string, string>();
  for (const proposal of items) {
    const current = first.get(key(proposal));
    if (current === undefined || sortAt(proposal) < current) {
      first.set(key(proposal), sortAt(proposal));
    }
  }
  return first;
}

/**
 * The order of the grouped inbox, as the server sorts: groups by their first expiry, a group's own
 * subject first, then its other subjects by their first expiry, one run's proposals together, then by
 * expiry.
 */
function groupedOrder(items: readonly AgentProposal[], sortAt: (proposal: AgentProposal) => string): AgentProposal[] {
  const groups = firstExpiries(items, groupKeyOf, sortAt);
  const subjects = firstExpiries(items, subjectKeyOf, sortAt);
  const compare = (left: string, right: string) => (left < right ? -1 : left > right ? 1 : 0);
  return [...items].sort((left, right) =>
    compare(groups.get(groupKeyOf(left))!, groups.get(groupKeyOf(right))!)
    || compare(groupKeyOf(left), groupKeyOf(right))
    || (left.parent ? 1 : 0) - (right.parent ? 1 : 0)
    || compare(subjects.get(subjectKeyOf(left))!, subjects.get(subjectKeyOf(right))!)
    || compare(subjectKeyOf(left), subjectKeyOf(right))
    || (left.runId === null ? 1 : 0) - (right.runId === null ? 1 : 0)
    || compare(left.runId ?? '', right.runId ?? '')
    || compare(sortAt(left), sortAt(right))
    || compare(left.approvalId, right.approvalId));
}

/** The groups of the page `ordered[offset, offset + count)`, with their totals and continuation. */
function groupsOf(ordered: readonly AgentProposal[], offset: number, count: number): AgentInboxGroup[] {
  const page = ordered.slice(offset, offset + count);
  const previous = offset > 0 ? ordered[offset - 1] : undefined;
  const next = ordered[offset + count];
  const groups: AgentInboxGroup[] = [];
  for (let index = 0; index < page.length;) {
    const key = groupKeyOf(page[index]);
    let end = index;
    while (end < page.length && groupKeyOf(page[end]) === key) {
      end++;
    }

    const members = page.slice(index, end);
    const own = members.find(proposal => !proposal.parent);
    const parent = members.find(proposal => proposal.parent)?.parent ?? null;
    const subjects: AgentInboxGroup['subjects'] = [];
    for (let at = 0; at < members.length;) {
      const subjectKey = subjectKeyOf(members[at]);
      let stop = at;
      while (stop < members.length && subjectKeyOf(members[stop]) === subjectKey) {
        stop++;
      }
      subjects.push({
        subjectType: members[at].subjectType,
        subjectId: members[at].subjectId,
        count: stop - at,
        total: ordered.filter(proposal => subjectKeyOf(proposal) === subjectKey).length,
        continuesFromPreviousPage: index === 0 && at === 0 && previous !== undefined && subjectKeyOf(previous) === subjectKey,
        continuesOnNextPage: end === page.length && stop === members.length && next !== undefined && subjectKeyOf(next) === subjectKey
      });
      at = stop;
    }

    groups.push({
      subjectType: own?.subjectType ?? parent!.subjectType,
      subjectId: own?.subjectId ?? parent!.subjectId,
      displayName: own ? own.subjectDisplayName : parent!.displayName,
      subjectTypeName: own ? own.subjectTypeName ?? null : parent!.subjectTypeName ?? null,
      subjectLink: own ? own.subjectLink ?? null : parent!.subjectLink ?? null,
      count: members.length,
      total: ordered.filter(proposal => groupKeyOf(proposal) === key).length,
      continuesFromPreviousPage: index === 0 && previous !== undefined && groupKeyOf(previous) === key,
      continuesOnNextPage: end === page.length && next !== undefined && groupKeyOf(next) === key,
      subjects
    });
    index = end;
  }
  return groups;
}

function validDecision(request: { decision?: unknown; reason?: unknown } | undefined): boolean {
  return !!request
    && (request.decision === 'approve' || request.decision === 'reject')
    && (request.reason === undefined || request.reason === null || (typeof request.reason === 'string' && request.reason.length <= maxReason));
}
