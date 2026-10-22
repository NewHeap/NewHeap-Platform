import {
  AgentDecisionOutcome,
  AgentInbox,
  AgentInboxGroup,
  AgentInboxVisit,
  AgentProposal,
  AgentProposalLookup,
  AgentSubjectWorkProposal,
  NH_ASSISTANT_MAX_PROPOSAL_LOOKUP
} from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';
import { NhAssistantMockProposal } from './nh-assistant-mock.models';

interface StoredProposal {
  proposal: AgentProposal;
  canDecide: boolean;
  reasonCode: string | null;
  divisionId: string | null;
}

const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const maxReason = 500;
const maxItemsPerPage = 100;
const maxBulkDecisions = 50;
const guid = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;
const lifetimeMs = 3 * 24 * 60 * 60 * 1000;
/** The platform's subject type of the whole application. */
const systemType = 'nh-system';

/**
 * The proposals the scenario lists, which the mock's agents left, for example one a run for the whole
 * application placed at a project (`placedBy`). The mock runs no agents: it serves them as the server
 * does in the approval inbox (`GET approvals/inbox`, pending proposals the caller may decide), one by id
 * (`GET approvals/{id}`), several by id (`POST approvals/lookup`), in their thread
 * (`GET threads/{id}/proposals`) and on their subject's work, and decides them one by one or together
 * with the server's validation and statuses. An approved proposal executes at once. A proposal of the
 * whole application (`nh-system`) exists only for the approvers of the whole application who pass the
 * agent's view policy (`mayDecideApplication`): for anybody else the inbox leaves it out, reading it
 * answers `404` and deciding it `404 assistant-approval-not-found`.
 */
export class NhAssistantMockProposals {
  private readonly proposals = new Map<string, StoredProposal>();
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
          mode: 'deferred',
          policyId: null,
          grantSource: 'agent',
          createdAt: item.createdAt ?? new Date(created).toISOString(),
          expiresAt: item.expiresAt ?? new Date(Date.parse(item.createdAt ?? new Date(created).toISOString()) + lifetimeMs).toISOString(),
          initiatorActorId: null,
          decidedByActorId: null,
          decidedAt: null,
          executionResultCode: null,
          executedAt: null,
          rationale: item.rationale ?? null,
          placedBy: item.placedBy ?? null,
          parent: item.parent ?? null,
          canGiveFeedback: false
        },
        divisionId: item.divisionId ?? null,
        canDecide: item.canDecide ?? true,
        reasonCode: item.canDecide === false ? item.reasonCode ?? 'assistant-approval-policy-denied' : null
      });
    }
  }

  /** `GET`/`POST approvals/…`; `null` for paths the mock does not serve. */
  handle(method: string, segments: string[], query: URLSearchParams, body: unknown): NhAssistantMockResult | null {
    if (segments.length === 2 && segments[1] === 'inbox') {
      return method === 'GET' ? this.inbox(query) : { status: 405 };
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
    if (segments.length === 2) {
      return method === 'GET' ? this.read(segments[1]) : { status: 405 };
    }
    if (segments.length === 3 && segments[2] === 'decisions') {
      return method === 'POST' ? this.decideOne(segments[1], body) : { status: 405 };
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

  private inbox(query: URLSearchParams): NhAssistantMockResult {
    const page = Number(query.get('page') ?? '1');
    const itemsPerPage = Number(query.get('itemsPerPage') ?? '20');
    const subjectType = query.get('subjectType');
    if (!Number.isInteger(page) || page < 1
      || !Number.isInteger(itemsPerPage) || itemsPerPage < 1 || itemsPerPage > maxItemsPerPage
      || (subjectType !== null && !dashCase.test(subjectType))) {
      return failure('assistant-message-invalid');
    }

    const agentId = query.get('agentId');
    const runId = query.get('runId');
    const divisionId = query.get('divisionId');
    if (divisionId !== null && !guid.test(divisionId)) {
      return { status: 400 };
    }

    const grouped = query.get('grouped') === 'true';
    const items = this.visible()
      .filter(item => item.proposal.status === 'pending' && item.canDecide)
      .filter(item => divisionId === null || sameGuid(item.divisionId, divisionId))
      .map(item => item.proposal)
      .filter(proposal => (subjectType === null || proposal.subjectType === subjectType)
        && (agentId === null || proposal.agentId === agentId)
        && (runId === null || proposal.runId === runId))
      .sort((left, right) => left.expiresAt.localeCompare(right.expiresAt) || left.approvalId.localeCompare(right.approvalId));
    const ordered = grouped ? groupedOrder(items) : items;
    const offset = (page - 1) * itemsPerPage;
    const pageItems = ordered.slice(offset, offset + itemsPerPage);
    const scopeKey = scopeKeyOf(divisionId);
    const lastVisitedAt = this.visits.get(scopeKey) ?? null;
    const body: AgentInbox = {
      items: pageItems,
      total: items.length,
      scopeKey,
      lastVisitedAt,
      newApprovalIds: pageItems
        .filter(proposal => lastVisitedAt === null || Date.parse(proposal.createdAt) > Date.parse(lastVisitedAt))
        .map(proposal => proposal.approvalId),
      groups: grouped ? groupsOf(ordered, offset, pageItems.length) : null
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
    const request = body as { approvalIds?: unknown; decision?: unknown; reason?: unknown } | undefined;
    const ids = request?.approvalIds;
    if (!validDecision(request)
      || !Array.isArray(ids)
      || ids.length === 0
      || ids.length > maxBulkDecisions
      || ids.some(id => typeof id !== 'string')) {
      return failure('assistant-approval-decision-invalid');
    }

    const results: AgentDecisionOutcome[] = [...new Set(ids as string[])].map(id => {
      const outcome = this.decide(id, request!.decision === 'approve', null);
      return {
        approvalId: id,
        succeeded: outcome.code === null,
        code: outcome.code,
        status: outcome.code === null ? this.proposals.get(id)!.proposal.status : null
      };
    });
    return { status: 200, body: { results } };
  }

  /** Decides one proposal as the server does; an approved proposal executes at once. */
  private decide(approvalId: string, approve: boolean, expectedHash: unknown): { code: string | null } {
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
    if (!stored.canDecide) {
      return { code: stored.reasonCode ?? 'assistant-approval-policy-denied' };
    }

    const now = this.now();
    stored.proposal = {
      ...stored.proposal,
      status: approve ? 'executed' : 'rejected',
      decidedByActorId: this.actorId(),
      decidedAt: now,
      executedAt: approve ? now : null
    };
    this.decided(stored.proposal);
    return { code: null };
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

function firstExpiries(items: readonly AgentProposal[], key: (proposal: AgentProposal) => string): Map<string, string> {
  const first = new Map<string, string>();
  for (const proposal of items) {
    const current = first.get(key(proposal));
    if (current === undefined || proposal.expiresAt < current) {
      first.set(key(proposal), proposal.expiresAt);
    }
  }
  return first;
}

/**
 * The order of the grouped inbox, as the server sorts: groups by their first expiry, a group's own
 * subject first, then its other subjects by their first expiry, one run's proposals together, then by
 * expiry.
 */
function groupedOrder(items: readonly AgentProposal[]): AgentProposal[] {
  const groups = firstExpiries(items, groupKeyOf);
  const subjects = firstExpiries(items, subjectKeyOf);
  const compare = (left: string, right: string) => (left < right ? -1 : left > right ? 1 : 0);
  return [...items].sort((left, right) =>
    compare(groups.get(groupKeyOf(left))!, groups.get(groupKeyOf(right))!)
    || compare(groupKeyOf(left), groupKeyOf(right))
    || (left.parent ? 1 : 0) - (right.parent ? 1 : 0)
    || compare(subjects.get(subjectKeyOf(left))!, subjects.get(subjectKeyOf(right))!)
    || compare(subjectKeyOf(left), subjectKeyOf(right))
    || (left.runId === null ? 1 : 0) - (right.runId === null ? 1 : 0)
    || compare(left.runId ?? '', right.runId ?? '')
    || compare(left.expiresAt, right.expiresAt)
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
