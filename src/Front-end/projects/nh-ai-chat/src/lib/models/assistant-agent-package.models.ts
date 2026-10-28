/**
 * Agent packages (S7-2): several agents with their configuration as data in one versioned package,
 * exported with `POST admin/agent-packages/export`, previewed and imported on another host with
 * `POST admin/agent-packages/preview` and `.../import` through the same validators as saving, and
 * parts the package locks against local changes until an administrator unlinks them with
 * `POST admin/agents/{id}/package/unlink`. `POST admin/agent-packages/read` and
 * `GET admin/agent-packages/{packageId}` read agents' parts and an installed package without an export.
 * The names are those of the server's JSON (camelCase).
 */
import {
  AgentAccountability,
  AgentElevatedReads,
  AssistantAutonomy,
  AssistantDataClassification,
  ConsultationSubjectRole,
  InstructionAssetReference,
  SkillApplyMode,
  TriggerConflict
} from './assistant-admin.models';
import { NhAssistantConsultationRoute } from './assistant-api.models';

/** The `format` of every agent package. */
export const NH_ASSISTANT_AGENT_PACKAGE_FORMAT = 'newheap.assistant.agent-package';

/** The format version this library reads and writes; a newer one is refused. */
export const NH_ASSISTANT_AGENT_PACKAGE_FORMAT_VERSION = 1;

/** The largest package in bytes of its UTF-8 JSON: 1 MB (`assistant-package-too-large` above). */
export const NH_ASSISTANT_AGENT_PACKAGE_MAX_BYTES = 1_048_576;

/** The most agents of one package. */
export const NH_ASSISTANT_AGENT_PACKAGE_MAX_AGENTS = 20;

/**
 * The kinds of part of an agent in a package. A part key is `agent`, `runSettings`, or
 * `{kind}:{id}` for a skill or trigger id, the subject type of a consultation or hand-off and the
 * name of an entry of agent metadata, such as `skill:triage`.
 */
export type AgentPackagePartKind = 'agent' | 'runSettings' | 'skill' | 'trigger' | 'consultation' | 'handOff' | 'metadata';

/** The lock of a whole part, instead of a list of its fields. */
export const NH_ASSISTANT_PACKAGE_LOCK_ALL = '*';

/**
 * The fields a package may lock per part kind (`NhAssistantPackageLockFields`). A save that changes a
 * locked field is refused with the field error `{field}: locked`, named as here; `worksOn`,
 * `dailyBudget`, `files` and `limits` each stand for the fields the forms show together. An agent in
 * code carries only the first six fields of `agent`.
 */
export const NH_ASSISTANT_PACKAGE_LOCK_FIELDS: Readonly<Record<AgentPackagePartKind, readonly string[]>> = {
  agent: ['displayName', 'description', 'instructions', 'toolSelectors', 'requiredPolicy', 'autonomy', 'offeredInChat', 'worksOn', 'profile', 'dataClassification'],
  runSettings: [
    'accountability', 'delegateFromInitiator', 'allowGatewayReads', 'elevatedReads', 'approvalPolicy', 'retryCount', 'dailyBudget',
    'maxConcurrentRuns', 'maxRunsPerSubjectPerDay', 'maxTokensPerRun', 'triageProfile', 'ruleHandler', 'files'
  ],
  skill: ['description', 'instructions', 'tools', 'profile', 'ruleHandler', 'maxTokensPerRun', 'dailyBudget', 'approvalPolicy', 'dataClassification', 'files'],
  trigger: ['skill', 'source', 'event', 'filters', 'cron', 'timeZone', 'subjectSet', 'message', 'conflict', 'debounceSeconds'],
  consultation: [
    'description', 'instructions', 'minimumRole', 'askerPolicy', 'routes', 'proposalRole', 'proposalPolicy', 'toolSelectors', 'limits',
    'allowRequests', 'exposeToMcp', 'dataClassification'
  ],
  handOff: ['sources', 'description', 'notifySource', 'maxPerSubjectPerDay'],
  metadata: ['value']
};

/**
 * A versioned agent package: several agents with what belongs to each of them as data. It never holds
 * history, candidates, runs, feedback, run state, shadow overrides, MCP server definitions, the
 * technical account or secrets; references travel as names. The server reads it strictly: an unknown
 * property, a missing required one, another `format` or a newer `formatVersion` is refused with
 * `assistant-package-format-unsupported`.
 */
export interface AgentPackage {
  /** Always `newheap.assistant.agent-package`. */
  format: string;
  formatVersion: number;
  package: AgentPackageInfo;
  /** 1 to 20 agents, each id once. */
  agents: PackageAgent[];
}

/** The identity of an agent package. */
export interface AgentPackageInfo {
  /** A dash-case id, at most 128 characters; by default the first agent's id. */
  id: string;
  /** 1 or higher; an upgrade of a linked package has a higher version. */
  version: number;
  /** What the package is for, at most 500 characters. */
  description?: string | null;
}

/**
 * One agent in a package: an agent created in the administration travels whole, an agent in code as
 * an overlay of what data may change on it, which the import applies to the same agent in code.
 */
export interface PackageAgent {
  /** The agent's id, kept on import. */
  id: string;
  /** `admin` for an agent created in the administration, `code` for an overlay of an agent in code. */
  source: 'admin' | 'code';
  /** The definition of an agent created here, or the overrides of an agent in code (part `agent`). */
  definition?: PackageAgentDefinition | null;
  /** The run settings of an agent created here, without the technical account (part `runSettings`). */
  runSettings?: PackageRunSettings | null;
  skills?: PackageSkill[] | null;
  triggers?: PackageTrigger[] | null;
  consultations?: PackageConsultation[] | null;
  /** The hand-offs defined as data that the agent accepts. */
  handOffs?: PackageHandOff[] | null;
  /** Its values of entries of agent metadata by name; for an agent in code only overrides of editable entries. */
  metadata?: Record<string, unknown> | null;
  /** The groups of agents it is in, by id. */
  groups?: string[] | null;
  /** The locked fields per part key, each `['*']` or fields of `NH_ASSISTANT_PACKAGE_LOCK_FIELDS`. */
  locks?: Record<string, string[]> | null;
  /** The SHA-256 of each part's canonical JSON by part key, lowercase hex; optional for a package a tool writes. */
  hashes?: Record<string, string> | null;
}

/**
 * The definition of an agent. For an agent in code only `displayName`, `description`, `instructions`,
 * `toolSelectors`, `requiredPolicy` and `autonomy` apply; a missing value keeps the code's.
 */
export interface PackageAgentDefinition {
  displayName?: string | null;
  description?: string | null;
  instructions?: string | null;
  instructionsAsset?: InstructionAssetReference | null;
  toolSelectors?: string[] | null;
  requiredPolicy?: string | null;
  autonomy?: AssistantAutonomy | null;
  offeredInChat?: boolean | null;
  worksOnSubjectTypes?: string[] | null;
  worksOnAllSubjects?: boolean | null;
  worksOnApplication?: boolean | null;
  perDivision?: boolean | null;
  runPolicy?: string | null;
  viewPolicy?: string | null;
  profile?: string | null;
  dataClassification?: AssistantDataClassification | null;
  /** For a new agent: it starts off. */
  startsOff?: boolean | null;
  /** For a new agent: it starts in shadow. */
  startsInShadow?: boolean | null;
  /** The live agent a data shadow variant belongs to: another agent of the package or one the host has. */
  shadowOf?: string | null;
}

/** The run settings of an agent created in the administration; the technical account never travels. */
export interface PackageRunSettings {
  accountability: AgentAccountability;
  delegateFromInitiator?: boolean;
  allowGatewayReads?: boolean;
  elevatedReads?: AgentElevatedReads | null;
  approvalPolicy?: string | null;
  retryCount?: number | null;
  dailyToolCalls?: number | null;
  dailyTokens?: number | null;
  maxConcurrentRuns?: number | null;
  maxRunsPerSubjectPerDay?: number | null;
  maxTokensPerRun?: number | null;
  triageProfile?: string | null;
  ruleHandler?: string | null;
  acceptsFiles?: boolean;
  maxFileBytes?: number | null;
  maxFilePages?: number | null;
  maxFilesPerTurn?: number | null;
  maxFileTokensPerTurn?: number | null;
}

/** The live version of a data skill. */
export interface PackageSkill {
  id: string;
  description: string;
  instructions?: string | null;
  instructionsAsset?: InstructionAssetReference | null;
  tools?: string[] | null;
  profile?: string | null;
  ruleHandler?: string | null;
  maxTokensPerRun?: number | null;
  dailyToolCalls?: number | null;
  dailyTokens?: number | null;
  approvalPolicy?: string | null;
  dataClassification?: AssistantDataClassification | null;
  acceptsFiles?: boolean;
  maxFileBytes?: number | null;
  maxFilePages?: number | null;
  maxFilesPerTurn?: number | null;
  maxFileTokensPerTurn?: number | null;
  /** For a new skill: it starts off. */
  startsOff?: boolean | null;
}

/** The live version of a data trigger; its id stays on import. */
export interface PackageTrigger {
  id: string;
  source: 'event' | 'schedule';
  skill?: string | null;
  event?: string | null;
  filters?: string[] | null;
  cron?: string | null;
  timeZone?: string | null;
  subjectSet?: string | null;
  message?: string | null;
  conflict?: TriggerConflict | null;
  debounceSeconds?: number | null;
}

/** A consultation defined as data. */
export interface PackageConsultation {
  subjectType: string;
  description: string;
  instructions?: string | null;
  minimumRole?: ConsultationSubjectRole | null;
  askerPolicy?: string | null;
  routes?: NhAssistantConsultationRoute[] | null;
  proposalRole?: ConsultationSubjectRole | null;
  proposalPolicy?: string | null;
  toolSelectors?: string[] | null;
  maxToolCalls?: number | null;
  timeoutSeconds?: number | null;
  maxAnswerChars?: number | null;
  maxHistoryMessages?: number | null;
  allowRequests?: boolean;
  exposeToMcp?: boolean;
  dataClassification?: AssistantDataClassification | null;
  /** For a new consultation: it starts off. */
  startsOff?: boolean | null;
}

/** A hand-off defined as data that the agent accepts; a source may be another agent of the package. */
export interface PackageHandOff {
  subjectType: string;
  sources: string[];
  description?: string | null;
  notifySource?: boolean;
  maxPerSubjectPerDay?: number | null;
}

/** How an export builds its package. */
export interface AgentPackageExportOptions {
  /** By default the id of the package the first agent is linked to, otherwise the first agent's id. */
  packageId?: string | null;
  /** By default the next version after the linked package's, otherwise 1. */
  version?: number | null;
  /** At most 500 characters. */
  description?: string | null;
  /** Per agent id, the locked fields per part key, each `['*']` or lockable fields of the part's kind. */
  locks?: Record<string, Record<string, string[]>> | null;
}

/** Body of `POST admin/agent-packages/export`. */
export interface AgentPackageExportRequest {
  /** The agents to export, 1 to 20; exactly these. */
  agentIds: string[];
  options?: AgentPackageExportOptions | null;
}

/** What a reference to an agent outside the exported selection is. */
export type AgentPackageWarningCode = 'hand-off-source' | 'shadow-of' | 'consultation-asker' | 'shadow-variant' | (string & {});

/** A reference of an exported agent to an agent outside the selection, which must exist where the package is imported. */
export interface AgentPackageWarning {
  /** The exported agent. */
  agentId: string;
  /** The part that makes the reference, such as `handOff:project`. */
  part: string;
  code: AgentPackageWarningCode;
  /** The agent outside the selection. */
  referencedAgentId: string;
}

/**
 * An exported package as `exportAgentPackage` answers it: the package, its JSON exactly as the server
 * wrote it for the download, and the references to agents outside the selection.
 */
export interface AgentPackageExport {
  package: AgentPackage;
  /** The JSON of the download, `agent-package-{packageId}.json`. */
  json: string;
  /** Every reference to an agent outside the selection (header `X-NewHeap-Package-Warnings`). */
  warnings: AgentPackageWarning[];
}

/** A part of an agent; without `part` every part of the agent. */
export interface PackagePartReference {
  agentId: string;
  part?: string | null;
}

/** How an import applies a package. */
export interface AgentPackageImportOptions {
  /**
   * Per agent id, the technical account on this host for run settings that answer to one; without it
   * such run settings are refused with `serviceAccountActorId: required`.
   */
  serviceAccounts?: Record<string, string> | null;
  /**
   * `live` (default): a changed skill or trigger becomes its new live version; `shadow`: its candidate
   * next to the live version. New skills and triggers start in shadow either way.
   */
  apply?: SkillApplyMode | null;
  /** Unlinked parts that take the package's version again instead of staying `kept-local`. */
  relink?: PackagePartReference[] | null;
}

/** Body of `POST admin/agent-packages/preview` and `POST admin/agent-packages/import`. */
export interface AgentPackageImportRequest {
  /** The package as JSON; the server reads it strictly. */
  package: AgentPackage;
  options?: AgentPackageImportOptions | null;
}

/**
 * What an import does or did with one part. `kept-linked` is reported only by a promotion of a shadow
 * variant as its package's version with `uncarriedParts: 'relink'`: a part of the live agent the
 * variant does not carry stays as it is and stays linked to the package version with its locks.
 */
export type AgentPackagePartOutcome = 'created' | 'changed' | 'unchanged' | 'kept-local' | 'left-behind' | 'refused' | 'kept-linked';

/** The outcome of one part in a preview or an import. */
export interface AgentPackagePart {
  agentId: string;
  /** The part key, such as `agent` or `skill:triage`. */
  part: string;
  outcome: AgentPackagePartOutcome;
  /** The host's part changed after the last import: the import overwrites its unlocked fields. */
  changedLocally: boolean;
  /** For a refused part without field errors: its failure code, such as `assistant-agent-exists`. */
  code: string | null;
  /** For a refused part: the field errors of the save validators, by field. */
  errors: Record<string, string[]> | null;
  /** The fields the package locks on the part, `['*']` for all. */
  lockedFields: string[];
  /** For a skill or trigger an import makes a candidate of (`apply: shadow`): the candidate's version. */
  candidateVersion?: number | null;
}

/** What an import would do: the parts per agent in dependency order, left-behind agents and parts last. */
export interface AgentPackagePreview {
  packageId: string;
  packageVersion: number;
  /** The version of the package the host has; `null` for a package it does not have. */
  installedVersion: number | null;
  /** No part is refused. */
  importable: boolean;
  parts: AgentPackagePart[];
}

/** What an import did: every part's outcome, as the preview showed it. */
export interface AgentPackageImport {
  packageId: string;
  packageVersion: number;
  /** The SHA-256 of the package's canonical JSON, lowercase hex, as audited. */
  hash: string;
  parts: AgentPackagePart[];
}

/** Body of `POST admin/agents/{id}/package/unlink`. */
export interface AgentPackageUnlinkRequest {
  /** The part key to unlink; `null` or missing unlinks the whole agent. */
  part?: string | null;
  /** At most 500 characters. */
  reason?: string | null;
}

/** The parts an unlink took out of their package. */
export interface AgentPackageUnlink {
  agentId: string;
  packageId: string;
  /** The part keys that are now local. */
  parts: string[];
}

/**
 * The package link of one part, as the administration shows it next to the part: the package it came
 * from and the fields that stay read-only.
 */
export interface PackagePartLink {
  part: string;
  packageId: string;
  /** The version of the package the part was imported from. */
  packageVersion: number;
  /** The locked fields, `['*']` for all; empty when the package locks none. */
  lockedFields: string[];
  /** The SHA-256 of the part's canonical JSON at import. */
  hash: string;
  /** The part changed after its import; the next upgrade overwrites its unlocked fields. */
  changedLocally: boolean;
  linkedAt: string;
  linkedBy: string;
}

/** The package an agent belongs to, with the link of every part that is still linked. */
export interface AgentPackageLink {
  packageId: string;
  /** The version of the package the host has. */
  packageVersion: number;
  /** The linked parts, ordered by part key. */
  parts: PackagePartLink[];
}

/** Body of `POST admin/agent-packages/read`. */
export interface AgentPackageReadRequest {
  /** The agents to read, 1 to 20, each once; agents of different packages may be read together. */
  agentIds: string[];
}

/**
 * `POST admin/agent-packages/read`: agents as an export would put them in a package, without exporting
 * or auditing.
 */
export interface AgentPackageParts {
  /**
   * Exactly the agents asked for, in the same order, each with every part and its SHA-256 per part key
   * in `hashes`, built as an export builds them, without `locks`, which only an export's options set.
   */
  agents: PackageAgent[];
}

/** The parts of one agent that are linked to an installed package. */
export interface PackageAgentLinks {
  agentId: string;
  /** Its linked parts, ordered by part key, each with whether it changed locally. */
  parts: PackagePartLink[];
}

/**
 * `GET admin/agent-packages/{packageId}`: a package this host imported, as it stores it, with the
 * current link of every part still linked to it. It stays known after its agents were unlinked.
 */
export interface InstalledAgentPackage {
  packageId: string;
  /** The version of the package the host has. */
  version: number;
  /** The version of the package format it was read with. */
  formatVersion: number;
  /** The SHA-256 of the package's canonical JSON at import, lowercase hex. */
  hash: string;
  /** The agents that belong to the package on this host, ordered by id. */
  agentIds: string[];
  importedAt: string;
  importedBy: string;
  /** The parts still linked to the package, per agent ordered by id; empty once everything was unlinked. */
  agents: PackageAgentLinks[];
}
