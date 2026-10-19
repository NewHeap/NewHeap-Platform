/*
 * Wire models of the assistant preference and administration endpoints.
 *
 * Literal translation of the assistant API contract (preferences and `admin/*`). Keep the
 * field names identical to the JSON the server returns.
 */
import { AgentProposalFeedbackCategory, AgentProposalFeedbackGroup, AgentProposalStatus } from './assistant-agent-run.models';
import { ApprovalPresentation, StaleKind } from './assistant-api.models';

export type AssistantStyle = 'default' | 'direct' | 'personal' | 'detailed';

export type AssistantAddressForm = 'informal' | 'formal';

export type AssistantResponseLength = 'short' | 'normal' | 'long';

export interface AssistantPreferences {
  style: AssistantStyle;
  addressForm: AssistantAddressForm;
  responseLength: AssistantResponseLength;
  /** At most 1,000 characters. Steers style only; it never overrides the fixed rules. */
  customInstructions: string | null;
}

export interface ApplicationContext {
  text: string;
  version: number;
  hash: string;
  updatedAt: string;
  updatedBy: string | null;
}

export interface ApplicationContextVersion {
  version: number;
  hash: string;
  updatedAt: string;
  updatedBy: string | null;
}

/** Body of `PUT admin/context`. */
export interface UpdateApplicationContextRequest {
  text: string;
  expectedVersion: number;
}

export type ToolCatalogEffect = 'read-only' | 'idempotent-mutation' | 'mutation' | 'destructive';

export interface ToolCatalogEntry {
  id: string;
  source: 'local' | 'bridge' | 'mcp';
  effect: ToolCatalogEffect;
  description: string;
}

/** Equals `NhAiAutonomyLevel`. */
export type AssistantAutonomy = 'observe' | 'explain' | 'propose' | 'simulate' | 'execute';

export interface AdminAgentInput {
  id: string;
  displayName: string;
  description: string;
  instructions: string;
  toolSelectors: string[];
  mcpServerIds: string[];
  requiredPolicy: string | null;
  autonomy: AssistantAutonomy;
  /**
   * @deprecated Accepted and ignored by the server until the next major version: pause, stop and
   * resume an agent with `setAgentState`. A new agent starts active.
   */
  isEnabled?: boolean;
  /**
   * Whether people can choose an agent created in the administration in the chat. Omitted keeps it on
   * update; a new agent is offered. An agent that is not offered keeps working on its triggers.
   */
  offeredInChat?: boolean;
}

/** Body of `PUT admin/agents/{id}`. */
export type UpdateAdminAgentRequest = AdminAgentInput & { expectedVersion: number };

/**
 * The run state of an agent. `paused`: no new runs; events wait and run after the resume.
 * `stopped`: active runs were cancelled and events are refused. `configured-off`: disabled in the
 * application's code until the next deployment; it cannot be resumed here.
 */
export type AgentRunState = 'active' | 'paused' | 'stopped' | 'configured-off';

export interface AdminAgent extends AdminAgentInput {
  /** `false` while the agent is stopped; read `runState` for the full state. */
  isEnabled: boolean;
  version: number;
  source: 'code' | 'admin';
  isOverridden: boolean;
  instructionsHash: string;
  updatedAt: string;
  runState: AgentRunState;
  stateChangedAt: string | null;
  stateChangedBy: string | null;
  stateReason: string | null;
  /** Events that wait while the agent is paused. */
  waitingInputs: number;
  /** The agent's skills with their states and today's usage; missing or empty without skills. */
  skills?: AgentSkillState[];
  /** The live agent this agent is a shadow variant of; missing or null for an agent that is no variant. */
  shadowOf?: string | null;
  /** Whether the agent works in shadow, and how the code and an administrator decided it. */
  shadow?: AgentShadowState | null;
  /** The technical account that answers for the agent's new runs; null when it answers to none. */
  serviceAccount?: AgentServiceAccountState | null;
  /**
   * Whether people can choose the agent in the chat; missing means offered. Agents in code decide
   * it with `HideFromChat()`.
   */
  offeredInChat?: boolean;
}

/** The shadow modes an administrator chooses; `code` lets the mode in code decide again. */
export type AgentShadowMode = 'shadow' | 'live' | 'code';

/**
 * The shadow mode of an agent or a skill. In shadow the work goes to the agent's shadow threads,
 * which only administrators see; nobody is notified and nothing proposed there executes.
 */
export interface AgentShadowState {
  /** Whether the work goes to shadow threads now; for a skill this includes the agent's mode. */
  shadow: boolean;
  /** The mode in code: `Shadow()` on the agent or the skill. */
  configured: boolean;
  /** The mode an administrator set at runtime; `null` follows the code. */
  override: boolean | null;
  /** A shadow variant always works in shadow; its mode cannot change. */
  locked: boolean;
  changedAt: string | null;
  changedBy: string | null;
  reason: string | null;
}

/** The technical account of an agent that answers to one. */
export interface AgentServiceAccountState {
  /** The account that answers for the agent's new runs. */
  actorId: string;
  /** The account in code. */
  configuredActorId: string;
  /** Whether an administrator set `actorId` at runtime. */
  overridden: boolean;
  changedAt: string | null;
  changedBy: string | null;
  reason: string | null;
}

/** One skill of an agent with its own state and today's usage across scopes. */
export interface AgentSkillState {
  skillId: string;
  description: string;
  runState: 'active' | 'paused' | 'stopped';
  changedAt: string | null;
  changedBy: string | null;
  reason: string | null;
  /** Events that wait for the skill while it is paused. */
  waitingInputs: number;
  toolCallsToday: number;
  tokensToday: number;
  /** The skill's daily budget per scope, when it has one. */
  dailyToolCalls: number | null;
  dailyTokens: number | null;
  cancelledRuns: number;
  startedRuns: number;
  /** Whether the skill's work goes to the agent's shadow threads. */
  shadow?: AgentShadowState;
}

/** Response of `GET` and `POST admin/agents/{id}/state`. */
export interface AgentState {
  agentId: string;
  runState: AgentRunState;
  /** Whether the agent works now: active, enabled in code and the assistant enabled. */
  active: boolean;
  changedAt: string | null;
  changedBy: string | null;
  reason: string | null;
  waitingInputs: number;
  /** The runs this change cancelled by stopping the agent. */
  cancelledRuns: number;
  /** The runs this change started for waiting events by resuming the agent. */
  startedRuns: number;
  skills?: AgentSkillState[];
  shadowOf?: string | null;
  /** `null` for an agent without runs, which only chats. */
  shadow?: AgentShadowState | null;
  serviceAccount?: AgentServiceAccountState | null;
}

/** Body of `POST admin/agents/{id}/shadow` and `POST admin/agents/{id}/skills/{skillId}/shadow`. */
export interface SetAgentShadowRequest {
  mode: AgentShadowMode;
  /** Optional, at most 500 characters. */
  reason?: string | null;
}

/** Body of `POST admin/agents/{id}/service-account`. `null` uses the account in code again. */
export interface SetAgentServiceAccountRequest {
  actorId: string | null;
  /** Optional, at most 500 characters. */
  reason?: string | null;
}

/** Body of `POST admin/agents/{id}/state`. `active` resumes the agent. */
export interface SetAgentStateRequest {
  state: 'active' | 'paused' | 'stopped';
  /** Optional, at most 500 characters. */
  reason?: string | null;
}

export type McpAuthMode = 'none' | 'bearer' | 'api-key' | 'forward-user-token';

export interface McpServerInput {
  id: string;
  displayName: string;
  url: string;
  authMode: McpAuthMode;
  headerName: string | null;
  /** Write-only. Omit to keep the stored secret, `""` to clear it. */
  secret?: string | null;
  requiredPolicy: string | null;
  isEnabled: boolean;
}

export interface McpServer {
  id: string;
  displayName: string;
  url: string;
  authMode: McpAuthMode;
  headerName: string | null;
  hasSecret: boolean;
  requiredPolicy: string | null;
  isEnabled: boolean;
  lastSyncAt: string | null;
  lastSyncStatus: 'ok' | 'failed' | null;
  assignedAgentIds: string[];
}

export interface McpServerTestResult {
  ok: boolean;
  code: string | null;
  toolCount: number | null;
}

export type McpToolEffect = 'read-only' | 'mutation';

export type McpToolStatus = 'available' | 'schema-changed' | 'missing';

export interface McpTool {
  remoteName: string;
  localId: string;
  description: string;
  descriptionOverride: string | null;
  isEnabled: boolean;
  effect: McpToolEffect;
  status: McpToolStatus;
  /** Remote annotation. Untrusted: shown as a hint only, never used as a default. */
  readOnlyHint: boolean | null;
}

/** Body of `PUT admin/mcp-servers/{id}/tools/{remoteName}`. */
export interface UpdateMcpToolRequest {
  isEnabled: boolean;
  effect: McpToolEffect;
  descriptionOverride: string | null;
}

/**
 * Filter of the feedback report of an agent. The server selects proposals by when they were made,
 * defaults to the 30 days before `to` (now) and allows at most 366 days; it only reports scopes the
 * caller administers.
 */
export interface AgentFeedbackFilter {
  /** Start of the period as an ISO 8601 date-time. */
  from?: string;
  /** End of the period, exclusive, as an ISO 8601 date-time. */
  to?: string;
  scopeKey?: string;
  toolId?: string;
  agentVersion?: number;
}

/** Filter and page of `GET admin/agents/{id}/feedback`. */
export interface AgentFeedbackListFilter extends AgentFeedbackFilter {
  category?: AgentProposalFeedbackCategory;
  page?: number;
  /** At most 100. */
  itemsPerPage?: number;
}

/** Filter of `GET admin/agents/{id}/feedback/export`. */
/** Query of `GET admin/agents/{id}/evaluation` and its export. */
export interface AgentEvaluationFilter {
  /** The shadow variant to compare with the agent. */
  variant: string;
  /** Start of the period as an ISO 8601 date-time; seven days before `to` by default. */
  from?: string;
  /** End of the period, exclusive; now by default. The period spans at most 92 days. */
  to?: string;
}

/**
 * Response of `GET admin/agents/{id}/evaluation`: how a shadow variant compared with its live
 * agent. Counts, identifiers and links only; no messages or arguments.
 */
export interface AgentEvaluation {
  agentId: string;
  variantId: string;
  from: string;
  to: string;
  runs: AgentEvaluationRuns;
  tools: AgentEvaluationTool[];
  liveCost: AgentEvaluationCost;
  variantCost: AgentEvaluationCost;
  routing: AgentEvaluationRouting[];
  /** Proposals only one of the two made, with the runs and threads to read; at most 50. */
  differences: AgentEvaluationDifference[];
}

export interface AgentEvaluationRuns {
  live: number;
  variant: number;
  /** Pairs of runs that handled the same event or that a replay repeated. */
  paired: number;
  /** Pairs whose variant run replayed the live run; it saw today's state of the subject. */
  replayed: number;
  liveUnpaired: number;
  variantUnpaired: number;
  /** The period held more runs than one evaluation reads; it covers the oldest. */
  truncated: boolean;
}

export interface AgentEvaluationTool {
  toolId: string;
  agreed: number;
  liveOnly: number;
  variantOnly: number;
  liveDecisions: AgentEvaluationDecisions;
  /** Live proposals people rejected that the variant did not make. */
  rejectedLiveNotProposed: number;
  /** Live proposals people rejected that the variant made too. */
  rejectedLiveAlsoProposed: number;
  /** Live proposals people approved that the variant did not make. */
  approvedLiveMissed: number;
}

export interface AgentEvaluationDecisions {
  pending: number;
  approved: number;
  /** Executed, including failed and uncertain executions. */
  executed: number;
  rejected: number;
  stale: number;
  /** Expired and withdrawn proposals. */
  other: number;
  /** Feedback categories of rejected proposals and how often people chose them. */
  rejectionCategories: Record<string, number>;
}

export interface AgentEvaluationCost {
  runs: number;
  modelCalls: number;
  tokens: number;
  toolCalls: number;
  averageModelCalls: number;
  averageTokens: number;
  averageToolCalls: number;
  p95ModelCalls: number;
  p95Tokens: number;
  p95ToolCalls: number;
}

export interface AgentEvaluationRouting {
  /** `null` is the agent's own tools. */
  liveSkill: string | null;
  variantSkill: string | null;
  runs: number;
}

export interface AgentEvaluationDifference {
  toolId: string;
  kind: 'live-only' | 'variant-only';
  /** What people decided about a live-only proposal. */
  liveStatus: string | null;
  liveRunId: string;
  liveConversationId: string;
  variantRunId: string;
  variantConversationId: string;
  replayed: boolean;
}

/** Body of `POST admin/agents/{id}/replay`. */
export interface AgentReplayRequest {
  /** The agent whose runs to replay; the variant's live agent by default. */
  sourceAgentId?: string | null;
  from?: string | null;
  to?: string | null;
  /** 1 to 500; 100 by default. */
  maxRuns?: number | null;
}

/** A replay of recorded runs into a shadow variant. */
export interface AgentReplay {
  id: string;
  variantAgentId: string;
  sourceAgentId: string;
  from: string;
  to: string;
  maxRuns: number;
  status: 'running' | 'completed' | 'stopped';
  /** Why a stopped replay stopped, such as `assistant-budget-exhausted`. */
  stopReason: string | null;
  runsReplayed: number;
  runsSkipped: number;
  createdAt: string;
  endedAt: string | null;
}

export interface AgentReplayList {
  items: AgentReplay[];
}

export interface AgentFeedbackExportFilter extends AgentFeedbackFilter {
  category?: AgentProposalFeedbackCategory;
  /** Maximum number of records; the server defaults to 10,000 and allows at most 50,000. */
  limit?: number;
}

export interface AgentFeedbackCategoryCount {
  category: AgentProposalFeedbackCategory;
  group: AgentProposalFeedbackGroup;
  count: number;
}

/** The proposals of one tool and agent version in the period and what people said about them. */
export interface AgentFeedbackSummaryRow {
  toolId: string;
  /** The agent row version of the runs that proposed them; `null` for proposals of consultations. */
  agentVersion: number | null;
  pending: number;
  /** Approved proposals, including those that executed, failed, closed as stale or were resolved. */
  approved: number;
  rejected: number;
  expired: number;
  withdrawn: number;
  /** Approved proposals closed right before execution because they no longer fit; part of `approved`. */
  stale: number;
  /** Approved proposals whose execution failed; part of `approved`. */
  executionFailed: number;
  /** Rejected divided by approved plus rejected (0..1); `null` before anyone decided. */
  rejectionRate: number | null;
  /** Rejected proposals with at least one feedback. */
  rejectedWithFeedback: number;
  /** Proposals of any status with at least one feedback. */
  feedbackProposals: number;
  /** Different people who gave feedback, so one critical approver counts as one. */
  feedbackAuthors: number;
  categories: AgentFeedbackCategoryCount[];
  /**
   * Open proposals closed before anyone decided because they no longer fit, such as work a person
   * already did. Neither approved nor rejected, and not part of `rejectionRate`. Missing on older servers.
   */
  invalidated?: number;
  /** Why proposals closed as stale, per kind and reason code. Missing on older servers. */
  invalidations?: AgentInvalidationCount[];
}

/** Proposals of one tool and agent version that closed as stale for one kind and reason. */
export interface AgentInvalidationCount {
  kind: StaleKind;
  /** The application guard's reason code. */
  reasonCode: string;
  /** True when the proposal closed right before execution, after its approval. */
  afterApproval: boolean;
  count: number;
}

/** Response of `GET admin/agents/{id}/feedback/summary`. */
export interface AgentFeedbackSummary {
  agentId: string;
  from: string;
  /** Exclusive end of the period. */
  to: string;
  rows: AgentFeedbackSummaryRow[];
}

/** One feedback with the proposal it is about, as approvers read it. */
export interface AgentFeedbackItem {
  id: string;
  approvalId: string;
  proposalId: string;
  conversationId: string;
  runId: string | null;
  scopeKey: string;
  subjectType: string;
  subjectId: string;
  subjectDisplayName: string | null;
  toolId: string;
  agentVersion: number | null;
  /** The proposal's status when the feedback was given. */
  approvalStatus: AgentProposalStatus;
  /** The proposal's summary as the agent recorded it. */
  summary: string;
  presentation: ApprovalPresentation | null;
  category: AgentProposalFeedbackCategory;
  group: AgentProposalFeedbackGroup;
  text: string | null;
  authorActorId: string;
  /**
   * The name of `authorActorId` from the member snapshots; `null` when no snapshot knows the
   * person. Show `authorActorId` then. Missing on older servers.
   */
  authorDisplayName?: string | null;
  bulk: boolean;
  createdAt: string;
  updatedAt: string;
}

/** Response of `GET admin/agents/{id}/feedback`, newest change first. */
export interface AgentFeedbackList {
  items: AgentFeedbackItem[];
  total: number;
}

/** A building block registered in code that run configuration as data names. */
export interface RunBlock {
  name: string;
  description: string;
}

/** Response of `GET admin/run-catalog`: what skills, triggers and run settings defined as data can name. */
export interface RunCatalog {
  triggerEvents: RunBlock[];
  subjectSets: RunBlock[];
  ruleHandlers: RunBlock[];
  approvalPolicies: string[];
  /** Chat profiles with function calling and streaming. */
  profiles: string[];
  /** Per approval policy who decides, how long a proposal waits and how; missing on older servers. */
  approvalPolicySummaries?: ApprovalPolicySummary[];
  /** Whether `searchAccounts` can find accounts by name; otherwise an account is chosen by its id. */
  accountSearch?: boolean;
}

/** Who may decide a proposal under an approval policy. */
export interface ApproverSummary {
  /** `initiator`: the person who caused the run; `subject-role`: a role on the subject; `policy`: an authorization policy. */
  kind: 'initiator' | 'subject-role' | 'policy' | string;
  /** For `subject-role`: at least `reader`, `participant` or `approver`. */
  role?: string | null;
  /** For `subject-role`: the application's role name, when one is required. */
  namedRole?: string | null;
  /** For `policy`: the authorization policy. */
  policy?: string | null;
}

/** What an approval policy means, so an administration can explain the choice. */
export interface ApprovalPolicySummary {
  name: string;
  approvers: ApproverSummary[];
  /** How long a proposal waits for a decision. */
  lifetimeMinutes: number;
  /** The person who caused a proposal may not decide it. */
  fourEyes: boolean;
  /** `inline`: the run waits for the decision; `deferred`: people decide later. */
  mode: 'inline' | 'deferred' | string;
}

/** An account an agent can answer to, from `GET admin/accounts`. */
export interface AssistantAccount {
  actorId: string;
  displayName: string;
  /** The e-mail address, when the application knows it. */
  detail: string | null;
}

/** Which part of the run configuration a problem concerns. */
export type RunConfigurationProblemKind = 'agent' | 'skill' | 'candidate' | 'trigger';

/**
 * Data that is out of effect because it no longer fits the code, for example after a deploy removed
 * a profile, policy, rule handler, event or subject set it names.
 */
export interface RunConfigurationProblem {
  agentId: string;
  kind: RunConfigurationProblemKind;
  /** The agent, skill or trigger id. */
  id: string;
  /** Such as `unknown-event` or `tool-not-covered`. */
  code: string;
}

/** Whom an agent answers to. */
export type AgentAccountability = 'agent' | 'subject-responsible' | 'service-account';

/** Reads with rights beyond the thread's readers: shielded from them, or refused. */
export type AgentElevatedReads = 'restricted' | 'deny';

/**
 * The run settings of an agent created in the administration. Grants are never data: in headless
 * runs the agent acts with the rights of the account it answers to, within its tool selectors.
 */
export interface AgentRunSettings {
  version: number;
  accountability: AgentAccountability;
  serviceAccountActorId: string | null;
  delegateFromInitiator: boolean;
  allowGatewayReads: boolean;
  elevatedReads: AgentElevatedReads;
  approvalPolicy: string | null;
  retryCount: number;
  dailyToolCalls: number | null;
  dailyTokens: number | null;
  maxConcurrentRuns: number;
  maxRunsPerSubjectPerDay: number;
  maxTokensPerRun: number | null;
  triageProfile: string | null;
  ruleHandler: string | null;
  changedAt: string;
  changedBy: string;
  reason: string | null;
  inEffect: boolean;
  problem: string | null;
}

/** Body of `PUT admin/agents/{id}/run-settings`. */
export interface AgentRunSettingsRequest {
  accountability: AgentAccountability;
  /** For `service-account` only: an account that exists and may sign in. */
  serviceAccountActorId?: string | null;
  delegateFromInitiator?: boolean;
  /** Not for an agent that answers to itself. */
  allowGatewayReads?: boolean;
  elevatedReads?: AgentElevatedReads;
  approvalPolicy?: string | null;
  /** 0 to 10; 3 by default. */
  retryCount?: number | null;
  /** With `dailyTokens`, the daily budget per scope. */
  dailyToolCalls?: number | null;
  dailyTokens?: number | null;
  /** 1 to 100; 4 by default. */
  maxConcurrentRuns?: number | null;
  /** 1 to 1,000; 10 by default. */
  maxRunsPerSubjectPerDay?: number | null;
  maxTokensPerRun?: number | null;
  triageProfile?: string | null;
  ruleHandler?: string | null;
  /** The settings' current version; `null` for new settings. */
  expectedVersion?: number | null;
  /** Optional, at most 500 characters. */
  reason?: string | null;
}

/** A skill the agent declares in code; it cannot change here. */
export interface CodeSkill {
  skillId: string;
  description: string;
}

/** A trigger registered in code; it cannot change here. */
export interface CodeTrigger {
  triggerId: string;
  source: 'event' | 'schedule' | 'webhook';
  skillId: string | null;
}

/** One version of a skill defined as data. */
export interface DataSkill {
  skillId: string;
  version: number;
  description: string;
  instructions: string | null;
  instructionsHash: string | null;
  tools: string[];
  profile: string | null;
  ruleHandler: string | null;
  maxTokensPerRun: number | null;
  dailyToolCalls: number | null;
  dailyTokens: number | null;
  approvalPolicy: string | null;
  changedAt: string;
  changedBy: string;
  reason: string | null;
  /** `false` with `problem` when the version no longer fits the code. */
  inEffect: boolean;
  problem: string | null;
  /** The candidate that runs in shadow next to this live version; missing or null without one. */
  candidate?: DataSkill | null;
}

/** How a change of a skill takes effect: at once, or as a candidate in shadow next to the live version. */
export type SkillApplyMode = 'live' | 'shadow';

/** Body of `PUT admin/agents/{id}/skills/{skillId}`. */
export interface DataSkillRequest {
  /** At most 300 characters; triage and `use_skill` choose by it. */
  description: string;
  /** At most 20,000 characters. */
  instructions?: string | null;
  /** Within the agent's own tool selectors; empty uses the agent's. */
  tools?: string[];
  profile?: string | null;
  ruleHandler?: string | null;
  maxTokensPerRun?: number | null;
  dailyToolCalls?: number | null;
  dailyTokens?: number | null;
  approvalPolicy?: string | null;
  /** The live version the change was made on; `null` for a new skill. */
  expectedVersion?: number | null;
  reason?: string | null;
  /** For a change of a skill; `live` by default. */
  apply?: SkillApplyMode;
}

export type DataSkillVersionStatus = 'live' | 'candidate' | 'superseded' | 'discarded';

/** One version in the history of a skill defined as data. */
export interface DataSkillVersion {
  version: number;
  status: DataSkillVersionStatus;
  description: string;
  instructionsHash: string | null;
  createdAt: string;
  createdBy: string;
  reason: string | null;
}

/** Body of `POST admin/agents/{id}/skills/{skillId}/candidate/promote` and `.../discard`. */
export interface SkillCandidateRequest {
  /** The candidate version the change is for. */
  version: number;
  reason?: string | null;
}

/** The runs of one version in a comparison. */
export interface SkillVersionStats {
  version: number;
  runs: number;
  completed: number;
  failed: number;
  proposals: number;
  toolCalls: number;
  tokens: number;
}

/** The proposals of both versions for one tool. */
export interface SkillComparisonTool {
  toolId: string;
  liveProposals: number;
  candidateProposals: number;
  liveApproved: number;
  liveRejected: number;
}

/** Response of `GET admin/agents/{id}/skills/{skillId}/candidate/comparison`. Counts only. */
export interface SkillComparison {
  agentId: string;
  skillId: string;
  from: string;
  to: string;
  live: SkillVersionStats;
  candidate: SkillVersionStats;
  /** The events both versions handled. */
  pairedEvents: number;
  tools: SkillComparisonTool[];
}

/** Optional period of a comparison; it starts when the candidate was saved by default. */
export interface SkillComparisonFilter {
  from?: string;
  to?: string;
}

/** What happens to an event while a run on the subject is active. */
export type TriggerConflict = 'coalesce' | 'queue' | 'supersede' | 'ignore';

/** A trigger defined as data: a named event or a schedule that starts the agent. */
export interface DataTrigger {
  triggerId: string;
  skillId: string | null;
  source: 'event' | 'schedule';
  event: string | null;
  cron: string | null;
  timeZone: string | null;
  subjectSet: string | null;
  message: string | null;
  conflict: TriggerConflict;
  debounceSeconds: number;
  /** In shadow its runs work in the agent's shadow threads. A new trigger starts in shadow. */
  shadow: boolean;
  version: number;
  changedAt: string;
  changedBy: string;
  reason: string | null;
  inEffect: boolean;
  problem: string | null;
}

/** Body of `PUT admin/agents/{id}/triggers/{triggerId}`. */
export interface DataTriggerRequest {
  source: 'event' | 'schedule';
  /** The skill the runs work in; empty lets the agent's rules or triage choose. */
  skill?: string | null;
  /** For an event trigger: a registered trigger event. */
  event?: string | null;
  /** For a schedule: a five- or six-field cron expression. */
  cron?: string | null;
  /** For a schedule: an IANA or Windows time zone; UTC by default. */
  timeZone?: string | null;
  /** For a schedule: a registered subject set. */
  subjectSet?: string | null;
  /** For a schedule: what every run receives, at most 2,000 characters. */
  message?: string | null;
  conflict?: TriggerConflict | null;
  /** For an event trigger: at most 3,600; 120 by default. */
  debounceSeconds?: number | null;
  /** The trigger's current version; `null` for a new trigger. */
  expectedVersion?: number | null;
  reason?: string | null;
}

/** Body of `POST admin/agents/{id}/triggers/{triggerId}/shadow`. */
export interface DataTriggerShadowRequest {
  mode: 'shadow' | 'live';
  reason?: string | null;
}

/** Query of the removals of run settings, data skills and data triggers. */
export interface RunConfigurationRemoval {
  expectedVersion?: number | null;
  reason?: string | null;
}

/**
 * Response of `GET admin/agents/{id}/configuration`: the agent's run settings, skills and triggers,
 * in code and as data, and the data that no longer fits the code.
 */
export interface AgentRunConfiguration {
  agentId: string;
  /** The agent's own tool selectors; a data skill's tools stay within them. */
  toolSelectors: string[];
  codeSkills: CodeSkill[];
  dataSkills: DataSkill[];
  codeTriggers: CodeTrigger[];
  dataTriggers: DataTrigger[];
  problems: RunConfigurationProblem[];
  source: 'code' | 'admin';
  /** The run settings of an agent created in the administration; `null` for an agent in code and one that only chats. */
  runSettings: AgentRunSettings | null;
}
