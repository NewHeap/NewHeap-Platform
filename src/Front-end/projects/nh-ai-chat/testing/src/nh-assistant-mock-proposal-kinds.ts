import { AdminAgent, ProposalKind, ProposalKindNumbers, ProposalKinds, RoutineCandidate } from '@newheap/platform-ai-chat';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';

const dayMs = 24 * 60 * 60 * 1000;
const maxPeriodMs = 366 * dayMs;

/**
 * `GET admin/proposal-kinds` of the mock API: the view per kind of action over the mock's first agents.
 * The mock records no decisions of its own, so it shows a fixed example: a status change that people
 * approved 64 times within minutes, a candidate for running without approval, and a task the agent
 * proposes with three rejections, the newest two days ago, which misses three thresholds. The default
 * thresholds apply; `agentId` and `toolId` narrow the view as on the server.
 */
export function mockProposalKinds(method: string, agents: AdminAgent[], query: URLSearchParams, now: string): NhAssistantMockResult {
  if (method !== 'GET') {
    return { status: 405 };
  }

  const to = query.get('to') ?? now;
  const from = query.get('from') ?? new Date(Date.parse(to) - 30 * dayMs).toISOString();
  const span = Date.parse(to) - Date.parse(from);
  if (Number.isNaN(span) || span <= 0 || span > maxPeriodMs) {
    return failure('assistant-message-invalid');
  }

  const [first, second] = agents;
  const rejectedAt = new Date(Date.parse(to) - 2 * dayMs).toISOString();
  const kinds: ProposalKind[] = [];
  if (first) {
    const routine = candidate(first, 'projects.change-status', numbers(64, 0, 95, 240, null), []);
    const others = second ? [candidate(second, 'projects.change-status', numbers(6, 0, 300, 1800, null), ['too-few-decisions'])] : [];
    kinds.push(kind('projects.change-status', 'Change project status', [routine, ...others]));
    kinds.push(kind('projects.add-task', 'Add task', [
      candidate(first, 'projects.add-task', numbers(9, 3, 1260, 7200, rejectedAt), ['too-few-decisions', 'approval-rate-too-low', 'recent-rejection'])
    ]));
  }

  const agentId = query.get('agentId');
  const toolId = query.get('toolId');
  const body: ProposalKinds = {
    from,
    to,
    thresholds: { minimumDecisions: 50, minimumApprovalRate: 0.98, quietPeriodDays: 7, maximumStaleOrFailedAfterApproval: 0 },
    kinds: kinds
      .filter(item => !toolId || item.toolId === toolId)
      .map(item => agentId ? kind(item.toolId, item.displayName, item.agents.filter(agent => agent.agentId === agentId)) : item)
      .filter(item => item.agents.length > 0)
  };
  return { status: 200, body };
}

function numbers(approved: number, rejected: number, medianSeconds: number, percentile90Seconds: number, lastRejectedAt: string | null): ProposalKindNumbers {
  const decisions = approved + rejected;
  return {
    pending: 2,
    approved,
    rejected,
    decisions,
    expired: 0,
    withdrawn: 0,
    invalidated: 0,
    staleAfterApproval: 0,
    executionFailed: 0,
    approvalRate: decisions === 0 ? null : approved / decisions,
    rejectionRate: decisions === 0 ? null : rejected / decisions,
    decisionTime: decisions === 0 ? null : { sampleSize: decisions, medianSeconds, percentile90Seconds, sampled: false },
    lastRejectedAt
  };
}

function candidate(agent: AdminAgent, toolId: string, kindNumbers: ProposalKindNumbers, unmet: RoutineCandidate['unmetThresholds']): RoutineCandidate {
  return {
    agentId: agent.id,
    agentDisplayName: agent.displayName,
    agentSource: agent.source,
    toolId,
    numbers: kindNumbers,
    candidate: unmet.length === 0,
    unmetThresholds: unmet
  };
}

/** A kind with the numbers of its agents added up; the slowest agent's times stand for the kind. */
function kind(toolId: string, displayName: string, agents: RoutineCandidate[]): ProposalKind {
  const approved = agents.reduce((sum, agent) => sum + agent.numbers.approved, 0);
  const rejected = agents.reduce((sum, agent) => sum + agent.numbers.rejected, 0);
  const slowest = agents
    .map(agent => agent.numbers.decisionTime)
    .reduce((left, right) => !left || (right && right.percentile90Seconds > left.percentile90Seconds) ? right : left, null);
  const lastRejectedAt = agents
    .map(agent => agent.numbers.lastRejectedAt)
    .filter((value): value is string => value !== null)
    .sort()
    .at(-1) ?? null;
  const total = numbers(approved, rejected, slowest?.medianSeconds ?? 0, slowest?.percentile90Seconds ?? 0, lastRejectedAt);
  return {
    toolId,
    displayName,
    numbers: {
      ...total,
      pending: agents.reduce((sum, agent) => sum + agent.numbers.pending, 0),
      decisionTime: total.decisionTime ? { ...total.decisionTime, sampleSize: approved + rejected } : null
    },
    agents
  };
}
