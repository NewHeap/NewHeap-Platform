import { ApprovalPresentation, NhAssistantConsultationRoute, StaleKind } from './assistant-api.models';

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
  | 'awaiting-application'
  | 'executed'
  | 'execution-failed'
  | 'execution-unknown'
  | 'resolved'
  | 'shadow';

export type AgentGrantSource = 'none' | 'delegated' | 'agent' | 'delegated-read' | 'accountable';

/**
 * How a run worked: `model` through the language model, `rule` through the agent's rules without a
 * model, `rule-then-model` when the rules handed the run over to the model.
 */
export type AgentRunKind = 'model' | 'rule' | 'rule-then-model';

/**
 * Who chose the skill a run worked in; `follow-up` continues in the skill of the run whose
 * proposal people decided.
 */
export type AgentSkillChooser = 'trigger' | 'rules' | 'triage' | 'model' | 'follow-up';

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
  /** How the run worked. Missing on older servers, which only ran the model. */
  kind?: AgentRunKind;
  /** The skill of the agent the run worked in; missing or null for the agent's own tools. */
  skill?: string | null;
  skillChosenBy?: AgentSkillChooser | null;
  /** The version of a skill defined as data the run worked in; absent for a skill in code. */
  skillVersion?: number | null;
  /** The run evaluated a candidate version of its skill in the shadow thread. */
  skillCandidate?: boolean;
  /** The agent that handed the run's work off; missing or null for a run no agent asked for. */
  requestedByAgentId?: string | null;
  /**
   * The thread of the run that handed the work off, only in a thread's run list and only when the
   * reader may open it.
   */
  requestedByConversationId?: string | null;
  /** How many hand-offs led to the run; 0 or missing for a run no agent asked for. */
  handOffDepth?: number;
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
  /**
   * The thread holds the agent's shadow work about the subject: only administrators see it,
   * nobody is notified and nothing proposed in it executes.
   */
  shadow?: boolean;
  /**
   * The name of the subject type in the caller's language, such as "Project", when the application
   * named the type; `null` or missing otherwise.
   */
  subjectTypeName?: string | null;
  /**
   * Where the application shows the subject, from the type's link template; `null` or missing
   * without one. `provideNhAssistant({ subjectLinks })` may replace it with a router link.
   */
  subjectLink?: string | null;
}

export interface AgentThreadLookup {
  thread: AgentThread | null;
  canStart: boolean;
  role: AgentSubjectRole;
}

/** The caller's live right to decide one open proposal; the server checks every decision again. */
export interface AgentProposalDecisionRight {
  canDecide: boolean;
  /**
   * Why the caller may not decide, for example `assistant-four-eyes-required`,
   * `assistant-approval-policy-denied` or `assistant-consult-asker-decides` for a consultation's
   * proposal that the person who asked decides in their own chat; `null` when they may.
   */
  reasonCode: string | null;
}

/** One agent on a subject: its thread, `null` before it worked on the subject, and whether the caller may start it. */
export interface AgentSubjectWorkAgent {
  agentId: string;
  /**
   * The agent's name: a translation key for an agent in code, the text itself for an agent created in
   * the administration. It names an agent kept out of the chat as well, which the status does not list;
   * missing from servers before it was added.
   */
  displayNameKey?: string | null;
  thread: AgentThread | null;
  canStart: boolean;
  /**
   * For administrators, the agent's shadow thread about the subject while the agent or one of its
   * skills works in shadow; missing or `null` otherwise.
   */
  shadowThread?: AgentThread | null;
}

/**
 * A proposal on a subject with the caller's live decision right. Only a pending proposal can be
 * decided; for every other status `canDecide` is `false` and `reasonCode` is `null`.
 */
export interface AgentSubjectWorkProposal extends AgentProposalDecisionRight {
  proposal: AgentProposal;
}

/** The most proposals one `POST approvals/lookup` reads. */
export const NH_ASSISTANT_MAX_PROPOSAL_LOOKUP = 100;

/**
 * Response of `POST approvals/lookup`: the requested proposals the caller may see, in the order of
 * the request. Ids the caller may not see, or that do not exist, are left out.
 */
export interface AgentProposalLookup {
  items: AgentSubjectWorkProposal[];
}

/**
 * All agent work on one subject as the caller may see it (`GET subjects/{type}/{id}/work`): every
 * agent with a thread on the subject plus the requested agent, and the pending proposals of all
 * those threads, the earliest expiry first.
 */
export interface AgentSubjectWork {
  subjectType: string;
  subjectId: string;
  subjectDisplayName: string | null;
  /**
   * The name of the subject type in the caller's language, such as "Project", when the application
   * named the type; `null` or missing otherwise.
   */
  subjectTypeName?: string | null;
  /**
   * Where the application shows the subject, from the type's link template; `null` or missing
   * without one. `provideNhAssistant({ subjectLinks })` may replace it with a router link.
   */
  subjectLink?: string | null;
  /** The caller's live role. Without a role the server answers `404 assistant-subject-not-found`. */
  role: Exclude<AgentSubjectRole, 'none'>;
  agents: AgentSubjectWorkAgent[];
  openProposals: AgentSubjectWorkProposal[];
}

/** The agent work on one subject for a list view (`POST subjects/{type}/summary`). */
export interface AgentSubjectSummary {
  subjectId: string;
  /** Proposals that wait for a decision on the subject. */
  openProposals: number;
  /** The open proposals the caller may decide, counted like the approval inbox. */
  proposalsToDecide: number;
  /** An agent works on the subject now. */
  runActive: boolean;
}

/** One summary per requested subject, in request order; subjects without visible work count zero. */
export interface AgentSubjectSummaryList {
  subjectType: string;
  items: AgentSubjectSummary[];
}

/** Maximum number of subjects one `POST subjects/{type}/summary` request accepts. */
export const NH_ASSISTANT_MAX_SUBJECT_SUMMARY = 200;

/** One subject a search found (`GET subjects/{type}/search`). */
export interface AgentSubjectSearchResult {
  type: string;
  id: string;
  /** The name the application gives the subject; show the id without one. */
  displayName: string | null;
  /** The subject's isolation key: `tenant:…`, `division:…` or `global`. */
  scopeKey: string;
  divisionId: string | null;
  /** Where the application shows the subject; `null` without a link template. */
  link?: string | null;
}

/** The subjects of one type a search found that the caller may read, in the application's order. */
export interface AgentSubjectSearch {
  subjectType: string;
  items: AgentSubjectSearchResult[];
}

/** The shortest and longest text `searchSubjects` accepts, and the most results it returns. */
export const NH_ASSISTANT_SUBJECT_SEARCH = { minQueryLength: 2, maxQueryLength: 100, maxLimit: 50 } as const;

/** The platform's subject type of the whole application, which the server registers itself. */
export const NH_ASSISTANT_SYSTEM_SUBJECT_TYPE = 'nh-system';

/** The whole application as one subject, for an agent that works app-wide. */
export const NH_ASSISTANT_SYSTEM_GLOBAL_ID = 'global';

/** The most characters of a run's result that `getObjectAgents` returns for its latest run. */
export const NH_ASSISTANT_RESULT_PREVIEW_LENGTH = 300;

/** The newest run of an agent on a subject, with the beginning of its result. */
export interface AgentLatestRun {
  runId: string;
  conversationId: string;
  triggerId: string;
  status: AgentRunStatus;
  failureCode: string | null;
  /** When the run ended, or started, or was queued. */
  at: string;
  /**
   * The first `NH_ASSISTANT_RESULT_PREVIEW_LENGTH` characters of the run's stored answer: model
   * output, to show as text. For a run without one, such as a run that ended before runs kept their
   * answer, the summary it ended with; `null` while the run has none. `getRunResult` returns all of it.
   */
  result: string | null;
  /** The result is longer than `result`. */
  resultTruncated: boolean;
  /** The run worked in the agent's shadow thread, which only administrators see. */
  shadow: boolean;
}

/** One agent that works on a subject, as `getObjectAgents` lists it. */
export interface AgentObjectAgent {
  agentId: string;
  /** A translation key for an agent in code, the text itself for one created in the administration. */
  displayNameKey: string;
  descriptionKey: string;
  /** Whether the caller may run the agent on the subject now. */
  canStart: boolean;
  /** The agent's thread about the subject that people open; `null` before it worked on it. */
  conversationId: string | null;
  /** The agent's newest run on the subject; `null` before its first. */
  latestRun: AgentLatestRun | null;
  /** The agent's proposals on the subject that wait for a decision. */
  openProposals: number;
}

/**
 * The agents that work on one subject and that the caller may use (`GET subjects/{type}/{id}/agents`),
 * ordered by id. The subject is an object or the whole application.
 */
export interface AgentObjectAgents {
  subjectType: string;
  subjectId: string;
  subjectDisplayName: string | null;
  subjectTypeName?: string | null;
  subjectLink?: string | null;
  /** The caller's live role. Without a role the server answers `404 assistant-subject-not-found`. */
  role: Exclude<AgentSubjectRole, 'none'>;
  agents: AgentObjectAgent[];
}

/** The most characters of a run's answer that `getRunResult` returns; a longer answer is cut. */
export const NH_ASSISTANT_RUN_RESULT_MAX_LENGTH = 20_000;

/** The most runs one page of a run history holds. */
export const NH_ASSISTANT_RUN_HISTORY_MAX_PAGE = 50;

/**
 * Which runs a run history lists. Every part is optional: an agent, a subject type (`nh-system` for
 * the whole application) or one subject of it, a status, and a period by when runs were created
 * (`from` inclusive, `to` exclusive, ISO 8601). `page` starts at 1; `itemsPerPage` is 1 to
 * `NH_ASSISTANT_RUN_HISTORY_MAX_PAGE` (default 20).
 */
export interface AgentRunFilter {
  agentId?: string;
  subjectType?: string;
  subjectId?: string;
  status?: AgentRunStatus;
  from?: string;
  to?: string;
  page?: number;
  itemsPerPage?: number;
}

/**
 * A personal MCP server a background run left out, and why: `assistant-mcp-consent-required` (the
 * person has not allowed background use), `assistant-mcp-connection-required` or
 * `assistant-mcp-reauthorization-required` (the person's connection is missing or ended), or
 * `assistant-mcp-initiator-only` (the run acted for someone other than the person who asked). The
 * person whose consent or connection is missing is told once a day.
 */
export interface AgentRunMcpSkip {
  serverId: string;
  /** The server's name when the run left it out. */
  serverDisplayName: string;
  code: string;
}

/** One run in a run history: its agent, subject, trigger, status, usage, times and result preview. */
export interface AgentRunHistoryItem {
  id: string;
  conversationId: string;
  agentId: string;
  /**
   * The agent's name: a translation key for an agent in code, the text itself for one created in the
   * administration; `null` for an agent that no longer exists.
   */
  agentDisplayNameKey?: string | null;
  subjectType: string;
  subjectId: string;
  subjectDisplayName: string | null;
  subjectTypeName?: string | null;
  subjectLink?: string | null;
  triggerId: string;
  status: AgentRunStatus;
  failureClass: string | null;
  failureCode: string | null;
  kind: AgentRunKind;
  skill: string | null;
  initiatorActorId: string | null;
  toolCalls: number;
  modelCalls: number;
  inputTokens: number;
  outputTokens: number;
  proposalCount: number;
  /**
   * The first `NH_ASSISTANT_RESULT_PREVIEW_LENGTH` characters of the run's answer, model output to
   * show as text; for a run without a stored answer, the summary it ended with.
   */
  result: string | null;
  /** The answer is longer than `result`. */
  resultTruncated: boolean;
  /** The run worked in the agent's shadow thread: nothing it proposed executes. */
  shadow: boolean;
  createdAt: string;
  startedAt: string | null;
  endedAt: string | null;
  /** The personal MCP servers the run left out; missing on older servers. */
  mcpConnectionSkips?: AgentRunMcpSkip[];
}

/**
 * A page of a run history, newest first (`getRunHistory` for people, the administration's
 * `getAgentRuns` for every run of an agent).
 */
export interface AgentRunHistory {
  items: AgentRunHistoryItem[];
  /** The runs that match the filter and that the caller may see. */
  total: number;
  /**
   * People's history considered only the agent threads with the most recent matching runs; a
   * narrower filter, such as one object or a period, reaches older ones.
   */
  limited: boolean;
}

/** One run with its whole result, its proposals and its files (`getRunResult`). */
export interface AgentRunResult {
  run: AgentRunHistoryItem;
  /**
   * The run's whole answer as Markdown, at most `NH_ASSISTANT_RUN_RESULT_MAX_LENGTH` characters:
   * model output, untrusted. Render it only with the assistant's safe Markdown rendering
   * (`nh-assistant-run-result` does), never as HTML. `null` while the run has none.
   */
  result: string | null;
  /** The answer was longer and `result` is its beginning. */
  resultTruncated: boolean;
  /** The bounded summary the run ended with, which the agent's next runs start from. */
  summary: string | null;
  /** The proposals the caller may see: in the run's own thread and placed at objects the caller reads. */
  proposals: AgentProposal[];
  /** The proposals the run placed at objects the caller may not read. */
  hiddenProposals: number;
  /** The files the run saved; `restricted` ones cannot be downloaded by the caller. */
  artifacts: AgentArtifact[];
  /** Whether the caller may open the run's thread conversation. */
  canOpenConversation: boolean;
}

export interface StartAgentRequest {
  agentId: string;
  subjectType: string;
  subjectId: string;
  message?: string | null;
}

/**
 * What became of a start by hand (`POST threads/start`, `POST runs/{id}/retry` with `restart`):
 * `started`, `queued`, `coalesced` with a run about to start, `superseded` the current run,
 * `ignored` or `deduplicated`; `waiting` while the agent or its skill is paused (`reasonCode`
 * `assistant-agent-paused` or `assistant-skill-paused`), so the run starts when it resumes; and
 * `deferred` until the application's transaction commits, without a run or thread yet. A refused
 * start is an HTTP error with its code, never a receipt.
 */
export type AgentTriggerOutcome =
  | 'started'
  | 'queued'
  | 'coalesced'
  | 'superseded'
  | 'ignored'
  | 'deduplicated'
  | 'waiting'
  | 'deferred';

export interface AgentTriggerReceipt {
  triggerId: string;
  outcome: AgentTriggerOutcome;
  runId: string | null;
  conversationId: string | null;
  /** Why the run did not start yet, such as `assistant-agent-paused` with `waiting`; `null` otherwise. */
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
  /**
   * The name of the subject type in the caller's language, such as "Project", when the application
   * named the type; `null` or missing otherwise.
   */
  subjectTypeName?: string | null;
  /**
   * Where the application shows the subject, from the type's link template; `null` or missing
   * without one. `provideNhAssistant({ subjectLinks })` may replace it with a router link.
   */
  subjectLink?: string | null;
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
  /**
   * The name of `decidedByActorId` from the subject's member snapshot; `null` when the snapshot
   * does not know the person. Show `decidedByActorId` then. Missing on older servers.
   */
  decidedByDisplayName?: string | null;
  decidedAt: string | null;
  executionResultCode: string | null;
  executedAt: string | null;
  /**
   * For a proposal the application executes (`ExecuteInApplication`), until when its report of the
   * latest hand-off is due: while it is `awaiting-application` the moment it becomes
   * `execution-unknown`, afterwards the sign that an approver resolves it with `hand-to-application`
   * instead of `re-execute`. `null` for a proposal never handed to the application; missing on older
   * servers.
   */
  executionReportDueAt?: string | null;
  /**
   * The consultation that proposed it, when another assistant consulted the agent for
   * `initiatorActorId`; `consultationRoute` says who decides it and whose rights execute it.
   */
  consultationId?: string | null;
  /**
   * How a consultation's proposal is decided and whose rights execute it: `asker-decides`, the person
   * who asked (`initiatorActorId`) decides it in their own chat, nobody decides it here;
   * `subject-asker-rights`, the subject's approvers decide it and it runs with the asker's rights;
   * `subject-agent-rights`, the subject's approvers other than the asker decide it and it runs with the
   * agent's own rights (`grantSource`). `null` for a run's proposal; missing on older servers.
   */
  consultationRoute?: NhAssistantConsultationRoute | null;
  /** The caller's own feedback on the proposal; `null` when they gave none. Missing on older servers. */
  feedback?: AgentProposalFeedback | null;
  /**
   * Whether the caller may give or change feedback: the proposal was rejected, expired, withdrawn or
   * closed as stale, and the caller decided it or is an approver of the subject. Missing on older servers.
   */
  canGiveFeedback?: boolean;
  /**
   * Why a stale proposal no longer fits; `null` for other statuses. Its reason code is
   * `executionResultCode`. Missing on older servers.
   */
  staleKind?: StaleKind | null;
  /**
   * The application's explanation of a stale proposal in the caller's language, such as who already
   * did the work; `null` when it gave none. Missing on older servers.
   */
  staleExplanation?: string | null;
  /**
   * Who made the proposal stale, when the application's guard named them; `null` otherwise.
   * Missing on older servers.
   */
  staleByActorId?: string | null;
  /** The name of `staleByActorId` from the subject's member snapshot; `null` when unknown. */
  staleByDisplayName?: string | null;
  /**
   * The application objects the proposal changes, as its guards named them, so lists can group
   * proposals by object. `targets` holds execution scopes instead. Missing on older servers.
   */
  objects?: AgentProposalObject[];
  /**
   * The newer proposal for the same objects that replaced this withdrawn one; `null` otherwise.
   * Missing on older servers.
   */
  supersededByApprovalId?: string | null;
  /**
   * Why the agent's rules proposed it, in the caller's language; `null` for proposals of the model.
   * Missing on older servers.
   */
  rationale?: string | null;
  /**
   * The run that placed this proposal here from another subject, such as a run for the whole
   * application; `null` for a proposal made in this thread. Missing on older servers.
   */
  placedBy?: AgentProposalOrigin | null;
  /**
   * The subject the proposal's subject belonged to when the proposal was made, such as the project of
   * a task, as the application described it; `null` without one. The grouped inbox groups by it.
   * Missing on older servers.
   */
  parent?: AgentProposalParent | null;
}

/**
 * The subject a proposal's subject belonged to when the proposal was made. The name is the one recorded
 * then; the type's name and the link follow the application's registration of the type.
 */
export interface AgentProposalParent {
  subjectType: string;
  subjectId: string;
  displayName: string | null;
  /** The name of the parent's type in the caller's language, when the application named it. */
  subjectTypeName?: string | null;
  /** Where the application shows the parent; `null` without a link template. */
  subjectLink?: string | null;
}

/**
 * The run that placed a proposal at another object than its own, and the thread it ran in. Its subject
 * is named only for the whole application (`subjectType` `nh-system`); for another object only its kind
 * is known, because a reader of this object may not read that one.
 */
export interface AgentProposalOrigin {
  /** The thread of the run that placed the proposal; opening it checks the reader's access. */
  conversationId: string;
  runId: string | null;
  /** The run's subject type; `null` when its thread no longer exists. */
  subjectType: string | null;
  /** The run's subject id for the whole application (`global` or `division-…`); `null` otherwise. */
  subjectId?: string | null;
  /** The name of the whole application or its division in the caller's language; `null` otherwise. */
  subjectDisplayName?: string | null;
  /** The name of the run's subject type in the caller's language, when the application named it. */
  subjectTypeName?: string | null;
}

/** An application object a proposal changes, such as `{ type: 'project-task', id: '4711' }`. */
export interface AgentProposalObject {
  type: string;
  id: string;
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
  /**
   * The proposals the consultation left per route, such as `{ 'asker-decides': 1 }`; routes without
   * proposals are left out. Missing on older servers.
   */
  proposalsByRoute?: Partial<Record<NhAssistantConsultationRoute, number>>;
}

export interface AgentConsultationList {
  items: AgentConsultation[];
  total: number;
}

export interface AgentInbox {
  items: AgentProposal[];
  total: number;
  /**
   * The scope whose visits mark what is new: `global`, or `division:{id}` for an inbox filtered on a
   * division. Missing on older servers.
   */
  scopeKey?: string;
  /**
   * When the caller last looked at the inbox in this scope (`visitInbox`), before this read; `null`
   * before the first visit. Missing on older servers.
   */
  lastVisitedAt?: string | null;
  /**
   * The proposals on this page made after `lastVisitedAt`; before the first visit all of them.
   * Missing on older servers, which mark nothing as new.
   */
  newApprovalIds?: string[];
  /**
   * With `grouped: true`, the groups on this page in order. The items are contiguous per group and
   * subject: the first `count` items belong to the first group, and so on. `null` or missing for the
   * flat inbox.
   */
  groups?: AgentInboxGroup[] | null;
}

/**
 * One group on a page of the grouped inbox: the parent its proposals recorded, or the subject of
 * proposals without a parent, which holds that subject's own proposals and those of its children.
 */
export interface AgentInboxGroup {
  subjectType: string;
  subjectId: string;
  /** The group's name: its subject's own name, or the name its proposals recorded for the parent. */
  displayName: string | null;
  /** The name of the group's type in the caller's language, when the application named it. */
  subjectTypeName?: string | null;
  /** Where the application shows the group's subject; `null` without a link template. */
  subjectLink?: string | null;
  /** How many of the page's items belong to the group. */
  count: number;
  /** How many proposals of the inbox, with its filters, belong to the group. */
  total: number;
  /** The group started on the page before. */
  continuesFromPreviousPage: boolean;
  /** The group goes on on the page after. */
  continuesOnNextPage: boolean;
  /** The group's subjects on this page in order, the group's own subject first. */
  subjects: AgentInboxSubjectGroup[];
}

/** One subject within a group on a page of the grouped inbox; its name and link are those of its items. */
export interface AgentInboxSubjectGroup {
  subjectType: string;
  subjectId: string;
  count: number;
  total: number;
  continuesFromPreviousPage: boolean;
  continuesOnNextPage: boolean;
}

export interface AgentInboxFilter {
  subjectType?: string;
  agentId?: string;
  /** Only proposals on subjects of this division; the inbox's visits are then those of the division. */
  divisionId?: string;
  runId?: string;
  /** Groups the proposals by parent and subject (`AgentInbox.groups`) instead of by expiry alone. */
  grouped?: boolean;
  page?: number;
  itemsPerPage?: number;
}

/** A recorded visit of the inbox in one scope (`visitInbox`). */
export interface AgentInboxVisit {
  /** `global`, or `division:{id}` for the inbox of one division. */
  scopeKey: string;
  visitedAt: string;
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

/**
 * `re-execute` runs the proposal again; `hand-to-application` hands a proposal the application
 * executes (one with `executionReportDueAt`) to the application again; `mark-resolved` confirms its
 * outcome without running it.
 */
export type AgentExecutionResolution = 're-execute' | 'hand-to-application' | 'mark-resolved';

/** A board of a group of agents the caller may open (`getBoards`). */
export interface AgentBoardSummary {
  /** The group id, such as `project-management`. */
  id: string;
  /** The group's name in the request's language. */
  name: string;
  /** What the group's agents do, when the application described it. */
  description: string | null;
  /** How many of the group's agents the caller sees on its board. */
  agentCount: number;
}

/** The boards the caller may open, ordered by name. */
export interface AgentBoardList {
  items: AgentBoardSummary[];
}

/** A kind of object as a board names it. */
export interface AgentBoardSubjectType {
  id: string;
  /** The name of one object in the request's language; the id without a name. */
  name: string;
  /** The name of several objects, when the application gave one. */
  pluralName: string | null;
}

/** What an agent on a board works on. */
export interface AgentBoardWorksOn {
  /** Every kind of object. */
  allObjects: boolean;
  /** The whole application. */
  application: boolean;
  /** Per division instead of app-wide. */
  perDivision: boolean;
  /** The kinds of objects it names, by id. */
  subjectTypes: AgentBoardSubjectType[];
}

/** One agent on a board. */
export interface AgentBoardAgent {
  agentId: string;
  /** A translation key for an agent in code, the text itself for one created in the administration. */
  displayNameKey: string;
  descriptionKey: string;
  worksOn: AgentBoardWorksOn;
  /**
   * `nh-system` when the agent works on the whole application in the board's scope; `null` for an
   * agent without such work there, which the board shows without a result or a start.
   */
  subjectType: string | null;
  /** `global` for an app-wide agent, `division-…` for a per-division agent on a division's board. */
  subjectId: string | null;
  /** The name of that subject in the request's language. */
  subjectDisplayName: string | null;
  /** Whether the caller may run the agent for that subject now. */
  canStart: boolean;
  /** The agent's thread of that subject, when the caller may open it. */
  conversationId: string | null;
  /** The agent's newest run for that subject; `null` before its first. */
  latestRun: AgentLatestRun | null;
  /** The open proposals of that thread. */
  openProposals: number;
  /**
   * Whether the latest run ended, or a proposal was made, after the caller's last visit of the board
   * in this scope; before the first visit everything shown is new.
   */
  isNew: boolean;
}

/** The board of one group in one scope (`getBoard`). */
export interface AgentBoard {
  id: string;
  name: string;
  description: string | null;
  /** `global`, or `division:{id}` for the board of one division. */
  scopeKey: string;
  divisionId: string | null;
  /** When the caller last looked at this board in this scope, before this read; `null` before the first visit. */
  lastVisitedAt: string | null;
  /** The group's agents the caller sees, ordered by id. */
  agents: AgentBoardAgent[];
}

/** A recorded visit of a board (`visitBoard`). */
export interface AgentBoardVisit {
  groupId: string;
  scopeKey: string;
  visitedAt: string;
}
