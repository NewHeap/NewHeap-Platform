import { ApprovalPresentation } from './assistant-api.models';

/**
 * Contracts of the agent-run endpoints of `NewHeap.Platform.AI.Chat.AspNet`: threads about a
 * subject, runs, proposals, the approval inbox, working memory and artifacts.
 */

/** The caller's role on the subject of a thread. */
export type AgentSubjectRole = 'none' | 'reader' | 'participant' | 'approver';

export type AgentRunStatus = 'queued' | 'running' | 'waiting-for-approval' | 'completed' | 'failed' | 'cancelled';

export type AgentProposalStatus =
  | 'pending'
  | 'approved'
  | 'rejected'
  | 'expired'
  | 'withdrawn'
  | 'stale'
  | 'executing'
  | 'executed'
  | 'execution-failed'
  | 'execution-unknown'
  | 'resolved'
  | 'shadow';

export type AgentGrantSource = 'none' | 'delegated' | 'agent';

export interface AgentRun {
  id: string;
  conversationId: string;
  triggerId: string;
  status: AgentRunStatus;
  failureClass: string | null;
  failureCode: string | null;
  initiatorActorId: string | null;
  accountableOwnerId: string;
  attempt: number;
  agentVersion: number;
  toolCalls: number;
  inputTokens: number;
  outputTokens: number;
  proposalCount: number;
  summary: string | null;
  shadow: boolean;
  createdAt: string;
  startedAt: string | null;
  endedAt: string | null;
  backgroundOperationId: string | null;
}

export interface AgentRunList {
  items: AgentRun[];
  total: number;
}

export interface AgentThread {
  conversationId: string;
  agentId: string;
  subjectType: string;
  subjectId: string;
  subjectDisplayName: string | null;
  accountability: string;
  accountableOwnerId: string;
  role: AgentSubjectRole;
  activeRun: AgentRun | null;
  waitingInputs: number;
}

export interface AgentThreadLookup {
  thread: AgentThread | null;
  canStart: boolean;
  role: AgentSubjectRole;
}

export interface StartAgentRequest {
  agentId: string;
  subjectType: string;
  subjectId: string;
  message?: string | null;
}

export interface AgentTriggerReceipt {
  triggerId: string;
  outcome: 'started' | 'queued' | 'coalesced' | 'superseded' | 'ignored' | 'deduplicated' | 'rejected';
  runId: string | null;
  conversationId: string | null;
  reasonCode: string | null;
}

export interface AgentProposal {
  approvalId: string;
  proposalId: string;
  proposalHash: string;
  conversationId: string;
  runId: string | null;
  agentId: string;
  subjectType: string;
  subjectId: string;
  subjectDisplayName: string | null;
  toolId: string;
  summary: string;
  /**
   * The application's localized explanation, written when the proposal was recorded. Show it
   * instead of `argumentsPreview`; `null` when no presenter handled the tool.
   */
  presentation: ApprovalPresentation | null;
  argumentsPreview: string;
  targets: string[];
  status: AgentProposalStatus;
  mode: 'inline' | 'deferred';
  policyId: string | null;
  grantSource: AgentGrantSource;
  createdAt: string;
  expiresAt: string;
  initiatorActorId: string | null;
  decidedByActorId: string | null;
  decidedAt: string | null;
  executionResultCode: string | null;
  executedAt: string | null;
  /**
   * The consultation that proposed it, when another assistant consulted the agent for
   * `initiatorActorId`; it executes with that person's rights only.
   */
  consultationId?: string | null;
  /** The caller's own feedback on the proposal; `null` when they gave none. Missing on older servers. */
  feedback?: AgentProposalFeedback | null;
  /**
   * Whether the caller may give or change feedback: the proposal was rejected, expired, withdrawn or
   * closed as stale, and the caller decided it or is an approver of the subject. Missing on older servers.
   */
  canGiveFeedback?: boolean;
}

/**
 * Why a person rejected a proposal. `incorrect`, `wrong-value` and `not-needed` say the agent was
 * wrong; `not-now` and `already-done` say it was right but not at this moment; `not-allowed` says
 * it went beyond what the agent should do; `other` leaves the explanation to the text.
 */
export type AgentProposalFeedbackCategory =
  | 'incorrect'
  | 'wrong-value'
  | 'not-needed'
  | 'not-now'
  | 'already-done'
  | 'not-allowed'
  | 'other';

/** The group of a feedback category. `right-but-not-now` never counts as "the agent was wrong". */
export type AgentProposalFeedbackGroup = 'wrong' | 'right-but-not-now' | 'not-allowed' | 'other';

/** The feedback categories in the order people choose them. */
export const NH_ASSISTANT_FEEDBACK_CATEGORIES: readonly AgentProposalFeedbackCategory[] = [
  'incorrect',
  'wrong-value',
  'not-needed',
  'not-now',
  'already-done',
  'not-allowed',
  'other'
];

/** The group of each feedback category, as the server reports it. */
export const NH_ASSISTANT_FEEDBACK_GROUPS: Readonly<Record<AgentProposalFeedbackCategory, AgentProposalFeedbackGroup>> = {
  incorrect: 'wrong',
  'wrong-value': 'wrong',
  'not-needed': 'wrong',
  'not-now': 'right-but-not-now',
  'already-done': 'right-but-not-now',
  'not-allowed': 'not-allowed',
  other: 'other'
};

/** Maximum length of the text of a feedback. */
export const NH_ASSISTANT_MAX_FEEDBACK_TEXT = 1_000;

/** Maximum number of proposals one `POST approvals/feedback` request accepts. */
export const NH_ASSISTANT_MAX_BULK_FEEDBACK = 50;

/**
 * One person's feedback on a rejected or otherwise closed proposal. It is separate from the
 * decision's reason, arrives after the decision and can be changed by its author.
 */
export interface AgentProposalFeedback {
  approvalId: string;
  category: AgentProposalFeedbackCategory;
  group: AgentProposalFeedbackGroup;
  text: string | null;
  /** Given once for several proposals that were rejected together. */
  bulk: boolean;
  createdAt: string;
  updatedAt: string;
}

/** The outcome of the feedback on one proposal of `POST approvals/feedback`. */
export interface AgentFeedbackOutcome {
  approvalId: string;
  succeeded: boolean;
  /** For example `assistant-feedback-not-accepted`; `null` when it succeeded. */
  code: string | null;
}

export type AgentConsultationStatus = 'running' | 'completed' | 'failed';

/**
 * One consultation of a thread's agent by another assistant. It names who asked and through which
 * assistant; the question and the answer stay in the asking conversation.
 */
export interface AgentConsultation {
  id: string;
  askerActorId: string;
  /** The asker's name from the subject's members, when known. */
  askerDisplayName: string | null;
  askingAgentId: string | null;
  depth: number;
  status: AgentConsultationStatus;
  failureCode: string | null;
  toolCalls: number;
  inputTokens: number;
  outputTokens: number;
  proposalCount: number;
  createdAt: string;
  endedAt: string | null;
}

export interface AgentConsultationList {
  items: AgentConsultation[];
  total: number;
}

export interface AgentInbox {
  items: AgentProposal[];
  total: number;
}

export interface AgentInboxFilter {
  subjectType?: string;
  agentId?: string;
  divisionId?: string;
  runId?: string;
  page?: number;
  itemsPerPage?: number;
}

export type AgentProposalDecision = 'approve' | 'reject';

export interface AgentDecisionOutcome {
  approvalId: string;
  succeeded: boolean;
  code: string | null;
  status: AgentProposalStatus | null;
}

export interface AgentThreadMemory {
  configured: boolean;
  schemaVersion: number;
  memory: unknown;
  updatedAt: string | null;
}

export interface AgentArtifact {
  id: string;
  name: string;
  version: number;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
  runId: string | null;
  restricted: boolean;
}

/** `resume` continues the run from its last checkpoint; `restart` starts a new run in the thread. */
export type AgentRunRetryMode = 'resume' | 'restart';

/** `re-execute` runs the proposal again; `mark-resolved` confirms its outcome without running it. */
export type AgentExecutionResolution = 're-execute' | 'mark-resolved';
