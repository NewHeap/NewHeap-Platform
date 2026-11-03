/**
 * Rule scripts (RB-1 to RB-6) as the administration reads and changes them: the rules of an agent, or of
 * one of its skills, written as the body of an async JavaScript function and stored as data, within the
 * ceiling the application's code sets (`UseRuleScriptsAsData`). The endpoints are
 * `admin/agents/{id}/rule-script` and `admin/agents/{id}/skills/{skillId}/rule-script`.
 */

/** The most subjects one dry run names (`NhAssistantRuleScriptLimits.MaxDryRunSubjects`). */
export const NH_ASSISTANT_RULE_SCRIPT_MAX_DRY_RUN_SUBJECTS = 20;

/** The longest script a version stores, in characters (`NhAssistantRuleScriptLimits.MaxScriptLength`). */
export const NH_ASSISTANT_RULE_SCRIPT_MAX_LENGTH = 10_000;

/** The longest reason of a version or a change, in characters. */
export const NH_ASSISTANT_RULE_SCRIPT_MAX_REASON = 500;

/**
 * Where a version stands: `live` is in effect and takes the place of the code rule, `candidate` waits to
 * go live and may run in shadow, `superseded` was live before another version, `discarded` was a
 * candidate that will not go live. Every version stays as history.
 */
export type RuleScriptStatus = 'live' | 'candidate' | 'superseded' | 'discarded';

/** What the code's ceiling allows the agent's rule scripts. */
export interface RuleScriptCeiling {
  /** The writing tools a version may opt in to, by tool id. */
  writes: string[];
  /** The most writes one run makes. */
  maxWritesPerRun: number;
  /** The destructive tools among `writes` that may run in a script at all. */
  destructive: string[];
  /** Another administrator than a version's author sets it live; nobody saves straight live. */
  requireFourEyes: boolean;
}

/** One version of a rule script. */
export interface RuleScriptVersion {
  version: number;
  status: RuleScriptStatus;
  script: string;
  /** The SHA-256 of the script, lowercase hex. */
  scriptHash: string;
  /** The writing tools the version may call, by tool id. */
  writes: string[];
  /** The person who wrote it. */
  authorActorId: string;
  /** The agent that wrote it for the person, such as the agent builder; `null` when the person did. */
  authorAgentId: string | null;
  reason: string | null;
  createdAt: string;
  liveAt: string | null;
  liveBy: string | null;
  /** When it was superseded or discarded. */
  endedAt: string | null;
  endedBy: string | null;
  /** Whether it is the candidate and runs in shadow next to the live rules. */
  shadow: boolean;
  /** The rule script's revision now, which the next change expects. */
  revision: number;
}

/** The rule script of an agent's own rules or of one skill (`GET .../rule-script`). */
export interface RuleScript {
  agentId: string;
  /** The skill; `null` for the agent's own rules. */
  skillId: string | null;
  /** Whether the code's ceiling lets the agent have rule scripts; without it nothing can be saved. */
  allowed: boolean;
  /** The revision every change expects, 0 before the first version. */
  revision: number;
  liveVersion: number | null;
  candidateVersion: number | null;
  candidateShadow: boolean;
  /** The code rule, by its handler's name, that a live version takes the place of; `null` without one. */
  codeRule: string | null;
  ceiling: RuleScriptCeiling;
  /** Every version, newest first. */
  versions: RuleScriptVersion[];
}

/** Body of `POST .../rule-script/versions`: the next version. */
export interface RuleScriptSaveRequest {
  /** The body of an async JavaScript function, at most `NH_ASSISTANT_RULE_SCRIPT_MAX_LENGTH` characters. */
  script: string;
  /** The writing tools it may call, by tool id, within the ceiling; empty for none. */
  writes: string[];
  /** The rule script's revision, 0 before the first version. */
  expectedRevision: number;
  /** Why, at most 500 characters; kept with the version and left out of the audit. */
  reason?: string | null;
  /** Saves it straight live (RB-3), which supersedes the live version; refused under four eyes. */
  live?: boolean;
  /** Lets the candidate run in shadow at once. */
  shadow?: boolean;
}

/** Body of `POST .../versions/{version}/live` and `.../discard`. */
export interface RuleScriptChangeRequest {
  expectedRevision: number;
  reason?: string | null;
}

/** Body of `POST .../versions/{version}/shadow`: lets a candidate run in shadow, or stop. */
export interface RuleScriptShadowRequest {
  expectedRevision: number;
  shadow: boolean;
  reason?: string | null;
}

/** A subject by type and id; `nh-system`/`global` is the whole application. */
export interface RuleScriptSubject {
  type: string;
  id: string;
}

/** Body of `POST .../versions/{version}/dry-run`: one to twenty subjects. */
export interface RuleScriptDryRunRequest {
  subjects: RuleScriptSubject[];
}

/** Whether a script parses and fits the length (`POST .../rule-script/check`). */
export interface RuleScriptCheck {
  valid: boolean;
  /** `ai-script-syntax-error` or `ai-script-too-long` when it is not valid. */
  code: string | null;
  /** The parser's description of the problem, for the script's author. */
  message: string | null;
  /** The line of the problem, from 1. */
  line: number | null;
  /** The column of the problem, from 1. */
  column: number | null;
}

/** One tool a script of the agent or skill sees, as the property of `tools`. */
export interface RuleScriptTool {
  /** The function name, such as `projects_request_review_v1`. */
  name: string;
  toolId: string;
  /** Whether it changes something. */
  write: boolean;
  /** For a write, whether the ceiling lets a version opt in to it. */
  inCeiling: boolean;
  /** Whether a version with the asked writes may call it. */
  callable: boolean;
  destructive: boolean;
  /** Its TypeScript-style declaration. */
  signature: string;
  /** Why such a version may not call it. */
  refusal: string | null;
}

/** What a rule script of the agent or skill sees (`GET .../rule-script/catalog`). */
export interface RuleScriptCatalog {
  agentId: string;
  skillId: string | null;
  tools: RuleScriptTool[];
  /** The alert kinds the agent lists, which `alerts.raise` and `alerts.resolve` accept. */
  alertKinds: string[];
  /**
   * The TypeScript-style declarations of `subject`, `inputs`, `run`, `alerts`, `propose`, `done`,
   * `handOver` and `useSkill`.
   */
  platformFunctions: string;
  maxToolCalls: number;
  timeoutSeconds: number;
  maxScriptLength: number;
  maxWritesPerRun: number;
}

/** A dry run started by a person, or a shadow run next to a live run. */
export type RuleScriptRunKind = 'dry-run' | 'shadow';

/**
 * How a version ended on one subject: `completed`, `stopped` with the script's code, or `failed` when the
 * subject could not be read, with the assistant's code.
 */
export type RuleScriptSubjectRunStatus = 'completed' | 'stopped' | 'failed';

/** The end a script chose. */
export interface RuleScriptEnd {
  kind: 'done' | 'hand-over' | 'use-skill';
  text: string;
  skillId: string | null;
}

/** One tool call of a script, in order, with its outcome. */
export interface RuleScriptCall {
  call: number;
  tool: string;
  toolId: string;
  /** Such as `done`, `failed`, `proposed`, `not-needed` or `refused`. */
  outcome: string;
  code: string | null;
  write: boolean;
}

/** What a write in shadow or in a dry run would have become (`NhAssistantRuleScriptWouldOutcomes`). */
export type RuleScriptWouldOutcome = 'would-run' | 'would-propose' | 'would-decide';

/** A write a script would make in shadow or in a dry run. */
export interface RuleScriptWrite {
  call: number;
  tool: string;
  toolId: string;
  outcome: RuleScriptWouldOutcome;
  /** The input as JSON, bounded. */
  input: string;
}

/** A proposal a script would record in shadow or in a dry run. */
export interface RuleScriptProposal {
  tool: string;
  toolId: string | null;
  input: string;
  rationale: string | null;
}

/** An alert a script would raise. */
export interface RuleScriptAlert {
  kind: string;
  subjectType: string;
  subjectId: string;
  key: string;
  urgency: string | null;
  parameters: Record<string, string>;
  recipients: string[];
  expiresAt: string | null;
}

/** An alert a script would resolve; `key` `null` for every key. */
export interface RuleScriptAlertKey {
  kind: string;
  subjectType: string;
  subjectId: string;
  key: string | null;
}

/** What a version would do on one subject. */
export interface RuleScriptSubjectRun {
  subjectType: string;
  subjectId: string;
  displayName: string | null;
  status: RuleScriptSubjectRunStatus;
  code: string | null;
  /** The script's or the assistant's description of where it stopped, for the script's author. */
  message: string | null;
  line: number | null;
  column: number | null;
  end: RuleScriptEnd | null;
  calls: RuleScriptCall[];
  writes: RuleScriptWrite[];
  proposals: RuleScriptProposal[];
  alerts: RuleScriptAlert[];
  resolves: RuleScriptAlertKey[];
  logs: string[];
}

/** A dry run or a shadow run of a version: what it would do per subject. */
export interface RuleScriptRunRecord {
  id: string;
  agentId: string;
  skillId: string | null;
  version: number;
  kind: RuleScriptRunKind;
  /** The run a shadow run went along with. */
  runId: string | null;
  /** Who started a dry run; the agent's actor for a shadow run. */
  ranBy: string;
  ranAt: string;
  subjectCount: number;
  completedCount: number;
  subjects: RuleScriptSubjectRun[];
}

/** A version that went live, with its latest dry run, or `null` when it has none (RB-5). */
export interface RuleScriptLive {
  version: RuleScriptVersion;
  latestDryRun: RuleScriptRunRecord | null;
}

/** One line of a comparison, in the order of the compared script. */
export interface RuleScriptLine {
  change: 'same' | 'added' | 'removed';
  /** Its number in the compared version, from 1; `null` for a removed line. */
  line: number | null;
  /** Its number in the other version; `null` for an added line. */
  otherLine: number | null;
  text: string;
}

/** Two versions compared (`GET .../rule-script/compare`). */
export interface RuleScriptComparison {
  version: number;
  /** The version it is compared with; 0 when there is none before it. */
  otherVersion: number;
  /** The script of `otherVersion` turned into the script of `version`, line by line. */
  lines: RuleScriptLine[];
  writesAdded: string[];
  writesRemoved: string[];
  dryRun: RuleScriptRunRecord | null;
  otherDryRun: RuleScriptRunRecord | null;
}
