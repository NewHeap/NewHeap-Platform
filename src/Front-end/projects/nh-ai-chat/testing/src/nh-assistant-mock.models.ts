import {
  AdminAgentInput,
  AgentConsultationStatus,
  AgentGrantSource,
  AgentProposalOrigin,
  AgentProposalParent,
  AgentProposalStatus,
  AgentRunMcpSkip,
  McpCustomAuthMode,
  AgentRunSettingsRequest,
  AgentRunStatus,
  AgentSummary,
  AgentTriggerReceipt,
  AssistantDataClassification,
  AssistantPreferences,
  AssistantStatus,
  ClientContext,
  CodeRunSettings,
  Conversation,
  DataSkillRequest,
  DataTriggerRequest,
  McpDiscovery,
  McpServerInput,
  AssistantAccount,
  ApprovalPresentation,
  NhAssistantConsultationLeaveRoute,
  NhAssistantConsultationRoute,
  RunCatalog,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import type { NhAssistantMockUpload } from './nh-assistant-mock-files';

/** Streams text as `message.delta` events, split into word-sized pieces. */
export interface NhAssistantMockTextStep {
  /** Fixed text, or text built from the page context the client sent with the message. */
  text: string | ((pageContext: ClientContext | null) => string);
}

/** Runs one tool: `tool.started` followed by `tool.completed`. */
export interface NhAssistantMockToolStep {
  tool: {
    toolId: string;
    displayName: string;
    toolVersion?: number;
    argumentsPreview?: string | null;
    resultPreview?: string | null;
    /** Default `succeeded`. */
    status?: 'succeeded' | 'failed';
    resultCode?: string | null;
  };
}

/**
 * Pauses the turn for approval: `tool.started`, `approval.required` and
 * `turn.completed` with status `waiting-for-approval`. The decision resumes the turn with
 * the `approved` or `rejected` steps. With `consultation` the approval is a consulted agent's
 * proposal that the person who asked decides in their own chat (`asker-decides`); with its
 * `leaveRoute` the decision `leave` leaves it for the subject's approvers and resumes the turn with
 * the `left` steps.
 */
export interface NhAssistantMockApprovalStep {
  approval: {
    toolId: string;
    displayName: string;
    toolVersion?: number;
    summary: string;
    presentation?: {
      toolDisplayName: string;
      summary: string;
      fields: { label: string; value: string }[];
      notice: string | null;
    } | null;
    argumentsPreview: string;
    targets: string[];
    /** Default 300 seconds. */
    expiresInSeconds?: number;
    /** Result preview of the tool after approval. */
    resultPreview?: string | null;
    approved: NhAssistantMockStep[];
    rejected: NhAssistantMockStep[];
    /** The consultation that proposed it; without it the approval is the conversation's own. */
    consultation?: NhAssistantMockApprovalConsultation;
    /** The steps after the person left the proposal for the subject's approvers. Default none. */
    left?: NhAssistantMockStep[];
  };
}

/** Where a consulted agent's proposal of a mock turn comes from. */
export interface NhAssistantMockApprovalConsultation {
  /** Default a generated id. */
  consultationId?: string;
  /** The consulted agent that proposed it. */
  agentId: string;
  subjectType: string;
  subjectId: string;
  subjectDisplayName?: string | null;
  /** The route at the subject the person may leave it for; default none, so they only approve or reject it. */
  leaveRoute?: NhAssistantConsultationLeaveRoute | null;
  /** The consulted agent's answer. Default none. */
  answer?: string | null;
}

/** Ends the stream with an `error` event. */
export interface NhAssistantMockErrorStep {
  error: {
    code: string;
    messageKey: string;
  };
}

/** Ends the turn with `turn.completed` status `failed`. */
export interface NhAssistantMockFailStep {
  fail: {
    errorCode: string;
  };
}

/**
 * Needs the caller's own connection to an MCP server each person connects to (`connectionScope:
 * 'user'`). Without an active connection the mock streams `connection.required`, adds `text` and
 * ends the turn; with one the step is skipped and the next steps play.
 */
export interface NhAssistantMockConnectionStep {
  connectionRequired: {
    serverId: string;
    text?: string;
  };
}

export type NhAssistantMockStep =
  | NhAssistantMockTextStep
  | NhAssistantMockToolStep
  | NhAssistantMockApprovalStep
  | NhAssistantMockErrorStep
  | NhAssistantMockFailStep
  | NhAssistantMockConnectionStep;

/** A scripted assistant turn, chosen by matching the user's message. */
export interface NhAssistantMockTurn {
  /** Case-insensitive substring, regular expression or predicate. Omit to match every message. */
  match?: string | RegExp | ((text: string, agentId: string, pageContext: ClientContext | null) => boolean);
  steps: NhAssistantMockStep[];
}

export interface NhAssistantMockTiming {
  /** Delay before the first event of a stream. Default 200 ms. */
  firstEventDelayMs?: number;
  /** Delay between events. Default 40 ms. */
  eventDelayMs?: number;
  /** Size of the byte chunks the stream is cut into, to exercise the client parser. Default 23. */
  chunkSize?: number;
}

/** A tool that a simulated remote MCP server lists. */
export interface NhAssistantMockRemoteTool {
  remoteName: string;
  description: string;
  /** Changing the hash between two syncs simulates a changed remote input schema. */
  inputSchemaHash: string;
  /** Remote annotation; the mock passes it on as an untrusted hint. */
  readOnlyHint?: boolean | null;
}

/** A simulated MCP server. The mock stores `secret` and `oauth.clientSecret` but never returns them. */
export interface NhAssistantMockMcpServer extends McpServerInput {
  remoteTools?: NhAssistantMockRemoteTool[];
  /** An `oauth` organization server starts connected. */
  organizationConnected?: boolean;
  /** A server each person connects to starts with the caller's own account connected. */
  personalConnected?: boolean;
}

/**
 * A preconfigured agent. Code agents default to the scenario's chat agents. `startsOff` starts it
 * off, as `StartsOff()` in code does for an agent in code.
 */
export interface NhAssistantMockAdminAgent extends AdminAgentInput {
  source: 'code' | 'admin';
  /**
   * The skills of a code agent, by id and description; they start active, in shadow with `shadow`
   * and off with `startsOff`, as `StartsOff()` in code.
   */
  skills?: { skillId: string; description: string; shadow?: boolean; startsOff?: boolean }[];
  /** The live agent this code agent is a shadow variant of. */
  shadowOf?: string;
  /** The agent starts in shadow, as with `Shadow()` in code. */
  shadow?: boolean;
  /** The technical account in code, as with `ServiceAccount(actorId)`. */
  serviceAccount?: string;
  /**
   * The routes that the consultations of a code agent allow, with the subject types whose
   * consultation allows each, as `AllowConsultation` in code, such as
   * `{ 'asker-decides': ['project'], 'subject-asker-rights': ['project'] }`; a route without types is
   * not allowed. Without it nobody can consult the agent and its state has no `consultationRoutes`.
   */
  consultations?: Partial<Record<NhAssistantConsultationRoute, string[]>>;
  /**
   * Whether a code agent says what it works on, as with `WorksOn`; by default it does when the
   * scenario gives `worksOnSubjectTypes`, `worksOnAllSubjects` or `worksOnApplication`.
   */
  worksOnDeclared?: boolean;
  /** The object types the agent is known to work on, shown as a hint next to "All objects". */
  knownSubjectTypes?: string[];
  /**
   * How a code agent runs, as its code says (`codeRunSettings` of its run configuration). By default
   * it answers to its `serviceAccount` when it has one and to itself otherwise, with the platform's
   * approval policy and the model.
   */
  codeRunSettings?: Partial<CodeRunSettings>;
  /**
   * The run configuration an agent created in the administration starts with, as administrators saved
   * it: its run settings, skills and triggers defined as data. Without it such an agent has none and
   * the administration offers the guided setup.
   */
  runConfiguration?: NhAssistantMockRunConfigurationPreset;
}

/**
 * The run configuration as data an agent created in the administration starts with. The mock saves it
 * through its own endpoints in order (the run settings, then each skill, then each trigger), so every
 * part is checked as an administrator's save is, against the scenario's run catalog and accounts; a
 * part the mock refuses fails the scenario at once, like a startup check. Skills and triggers start in
 * shadow, as saved ones do, unless `shadow` is `false`.
 */
export interface NhAssistantMockRunConfigurationPreset {
  runSettings: AgentRunSettingsRequest;
  skills?: NhAssistantMockDataSkill[];
  triggers?: NhAssistantMockDataTrigger[];
}

/** A skill defined as data of a preset run configuration. */
export interface NhAssistantMockDataSkill extends DataSkillRequest {
  skillId: string;
  /** Default `true`, as for a saved skill; `false` puts it live. */
  shadow?: boolean;
}

/** A trigger defined as data of a preset run configuration. */
export interface NhAssistantMockDataTrigger extends DataTriggerRequest {
  triggerId: string;
  /** Default `true`, as for a saved trigger; `false` puts it live. */
  shadow?: boolean;
}

/**
 * Administration data of the mock. Hosts without an admin scenario still get working
 * `admin/*` endpoints with code agents derived from `agents` and an empty context.
 */
export interface NhAssistantMockAdminScenario {
  /** Default `true`. Without it `admin/*` answers `403`. */
  canAdminister?: boolean;
  context?: string;
  /** Tool catalog for the selector picker. Enabled MCP tools are added automatically. */
  tools?: ToolCatalogEntry[];
  agents?: NhAssistantMockAdminAgent[];
  mcpServers?: NhAssistantMockMcpServer[];
  /**
   * Authentication modes the application registered in code (`AddMcpCredentialProvider`), which
   * `GET admin/mcp-auth-modes` lists and servers may use. Default none.
   */
  mcpAuthModes?: McpCustomAuthMode[];
  /** Policies the host knows; `requiredPolicy` must be one of them. Default: any value. */
  policies?: string[];
  /** Hosts that may receive the user's token (`forward-user-token`). Default: none. */
  forwardUserTokenHosts?: string[];
  /** Secret references that name an available secret. Default: every `config:` reference. */
  secretReferences?: string[];
  /** Client certificate names the host configured. Default: none. */
  clientCertificates?: string[];
  /** The caller's consent to background use of personal servers (`mcp-connections`). Default: none. */
  mcpConnections?: { serverId: string; allowBackgroundUse: boolean }[];
  /**
   * What the mock authorization window answers to an MCP authorization: the person approves
   * (default), denies, or closes the window. `NhAssistantMockBackend.setMcpAuthorizationOutcome`
   * changes it.
   */
  mcpAuthorization?: 'approve' | 'deny' | 'cancel';
  /** Discovery results per server host, over the mock's defaults (`https://auth.<host>`, PKCE, dynamic registration). */
  mcpDiscovery?: Record<string, Partial<McpDiscovery>>;
  /** Origins an authorization may return to in redirect mode. Default: any. */
  mcpReturnOrigins?: string[];
  /**
   * The building blocks in code that run configuration as data names. Default: none, with
   * `policies` as the approval policies. `divisions: true` lets agents created in the
   * administration work per division, as an application that lists its divisions does.
   */
  runCatalog?: Partial<RunCatalog>;
  /**
   * The data classifications each model profile and its fallbacks permit together, by profile name.
   * An agent or skill whose classification its profile does not permit is saved with the warning
   * `classification-not-permitted`, as on the server. A profile that is not listed permits every
   * classification. Default: none.
   */
  profileClassifications?: Record<string, AssistantDataClassification[]>;
  /**
   * Triggers in code, per agent; they cannot change in the administration. `subjectType` is the type
   * a trigger names up front, `nh-system` for a schedule for the whole application.
   */
  codeTriggers?: {
    agentId: string;
    triggerId: string;
    source: 'event' | 'schedule' | 'webhook';
    skillId?: string | null;
    subjectType?: string | null;
  }[];
  /**
   * The accounts `GET admin/accounts` finds, as the application's `INhActorSearch` would. With
   * accounts, run settings accept only these technical accounts; without, the account search is
   * unavailable and any id that does not start with `unknown` is accepted.
   */
  accounts?: AssistantAccount[];
}

/** A person in the simulated organization. */
export interface NhAssistantMockPerson {
  actorId: string;
  displayName: string;
  /** Short distinguishing text the directory shows, such as a department. */
  detail?: string;
}

/**
 * Sharing, live updates and notifications of the mock. The mock always offers invitation links
 * and live updates; with `directory` it also offers direct invitations.
 */
export interface NhAssistantMockCollaboration {
  /** The signed-in user. Default `{ actorId: 'mock-user', displayName: 'You' }`. */
  currentUser?: NhAssistantMockPerson;
  /** Colleagues the directory finds; enables direct invitations. */
  directory?: NhAssistantMockPerson[];
  /** Default 20. */
  maxParticipants?: number;
}

/** A subject of a mock subject type, as the application's resolver describes it. */
export interface NhAssistantMockSubject {
  id: string;
  displayName: string;
  /** Default `global`. */
  scopeKey?: string;
  divisionId?: string | null;
  /** Whether the signed-in user may read the subject; others never find it. Default `true`. */
  readable?: boolean;
  /** The signed-in user's role on a readable subject. Default `participant`, who may run its agents. */
  role?: 'reader' | 'participant' | 'approver';
}

/**
 * A division the application lists, for agents that work on the whole application per division:
 * the run catalog then reports divisions only when `admin.runCatalog.divisions` says so.
 */
export interface NhAssistantMockDivision {
  /** The division's id, a GUID; its subject of the whole application is `nh-system/division-{id:N}`. */
  id: string;
  name: string;
  /** Whether the signed-in user works in the division. Default `true`. */
  readable?: boolean;
}

/**
 * What a run of an agent started by hand does in the mock, which runs no agents: it ends at once with
 * this status and result, leaving this many open proposals. The agent's state decides first, as on
 * the server: a stopped agent or one disabled in code is refused (`409`), and a paused agent answers
 * `waiting` with `assistant-agent-paused`.
 */
export interface NhAssistantMockRunNow {
  /** Default `completed`. */
  status?: AgentRunStatus;
  /** The run's result; default none. */
  result?: string | null;
  /**
   * The run's result per culture (`en`, `nl-NL`), as a run whose rules wrote it per culture keeps it:
   * a reader gets the one of the request's language (exact, then the language), `result` otherwise.
   */
  results?: Readonly<Record<string, string>>;
  /** The open proposals the run leaves on its subject. Default 0. */
  proposals?: number;
  /**
   * The receipt's outcome. Default `started`. `ignored` and `deferred` record no run; `waiting` records
   * none either and names `assistant-skill-paused`, as for a paused skill.
   */
  outcome?: AgentTriggerReceipt['outcome'];
  /**
   * The runs by hand the agent may make per object in a day; a start beyond it answers
   * `409 assistant-trigger-rate-limited`. Default no limit.
   */
  dailyLimit?: number;
}

/**
 * A run that happened before the first request, for the run histories (`GET runs`, the
 * administration's runs view) and `GET runs/{id}`. The mock shows it to the caller as the server
 * would: on an object the caller reads, for the whole application when the caller passes the agent's
 * view policy, by default its required policy; administrators see every run of an agent.
 */
export interface NhAssistantMockRun {
  /** Default a generated id. */
  id?: string;
  agentId: string;
  /** An object type of `subjectTypes`, or `nh-system` for the whole application. */
  subjectType: string;
  /** The object's id, or `global` (or `division-…`) for the whole application. */
  subjectId: string;
  /** The object's name; default the name `subjectTypes` gives it, or the id. */
  subjectDisplayName?: string;
  /** Default `completed`. */
  status?: AgentRunStatus;
  /** Why a `failed` run failed, such as `assistant-trigger-rolled-back`. Default `assistant-turn-failed`. */
  failureCode?: string;
  /** Default `manual`. */
  triggerId?: string;
  /** The run's answer as Markdown, model output; default none. */
  result?: string | null;
  /**
   * The run's result per culture (`en`, `nl-NL`), as a run whose rules wrote it per culture keeps it:
   * a reader gets the one of the request's language (exact, then the language), `result` otherwise.
   */
  results?: Readonly<Record<string, string>>;
  /** When the run was queued, ISO 8601; default now. */
  createdAt?: string;
  /** How long the run took. Default 30. */
  durationSeconds?: number;
  /** Default 0. */
  proposalCount?: number;
  /** Default 0. */
  toolCalls?: number;
  /** Default 0. */
  tokens?: number;
  /** The run worked in the agent's shadow thread. Default `false`. */
  shadow?: boolean;
  /** The personal MCP servers the run left out, as the server records them. Default none. */
  mcpConnectionSkips?: AgentRunMcpSkip[];
}

/**
 * A subject type the application registered with `AddSubjectType`, with its names per language, the
 * entity it stands for, its link template and the subjects people can find. The mock checks it as
 * the server checks the registration at startup: a dash-case id outside the reserved `nh-` prefix,
 * names of 1 to 100 characters with one in the fallback language, and one `{id}` in the link.
 */
export interface NhAssistantMockSubjectType {
  id: string;
  /** Names per culture name, such as `{ en: 'Project', nl: 'Project' }`; without, people see the id. */
  names?: Record<string, string>;
  pluralNames?: Record<string, string>;
  /** Default `en`. */
  fallbackLanguage?: string;
  entityType?: string | null;
  entityTypeFull?: string | null;
  /** Link template with exactly one `{id}`, such as `/projects/{id}`. */
  link?: string | null;
  subjects?: NhAssistantMockSubject[];
  /** Whether `GET subjects/{type}/search` finds the subjects by name. Default: when the type has subjects. */
  searchable?: boolean;
}

/**
 * A group of agents the application registers in code, checked as the server checks it at startup:
 * a dash-case id of at most 64 characters, a name of 1 to 100 characters per language with one in the
 * fallback language, and optional descriptions of at most 300 characters the same way.
 */
export interface NhAssistantMockAgentGroup {
  id: string;
  /** Names per culture name, such as `{ en: 'Project management', nl: 'Projectbeheer' }`. */
  names: Record<string, string>;
  descriptions?: Record<string, string>;
  /** Default `en`. */
  fallbackLanguage?: string;
  /**
   * The agents in the group when the mock starts: agents in code join it in code (`InGroup`), agents
   * created as data as an administrator put them there. Administrators' later choices win.
   */
  agentIds?: string[];
}

/** The scenario the mock API plays without a back-end. */
export interface NhAssistantMockScenario {
  /** Default `true`. */
  enabled?: boolean;
  limits?: Partial<AssistantStatus['limits']>;
  agents: AgentSummary[];
  /** Conversations that exist before the first request. */
  conversations?: Conversation[];
  /** Tried in order; the first matching turn answers. */
  turns: NhAssistantMockTurn[];
  timing?: NhAssistantMockTiming;
  /** Stored preferences of the caller. Default: default style, informal, normal length. */
  preferences?: AssistantPreferences;
  admin?: NhAssistantMockAdminScenario;
  collaboration?: NhAssistantMockCollaboration;
  /**
   * The application's subject types: the run catalog lists them, next to the platform's `nh-system`
   * (the whole application, `isSystem`) that the mock always adds as the server does,
   * `GET subjects/{type}/search` finds their readable subjects and `GET subjects/{type}/{id}/work`
   * names a subject chosen by id, `nh-system/global` included.
   */
  subjectTypes?: NhAssistantMockSubjectType[];
  /**
   * The application's divisions: `GET subjects/nh-system/search` finds those the user works in by
   * name, and the whole application in a division is named after it.
   */
  divisions?: NhAssistantMockDivision[];
  /**
   * The authorization policies the signed-in user passes: agents' required, run and view policies.
   * Default: every policy. Without a run policy an agent runs for the whole application for
   * administrators (`admin.canAdminister`), as the server's administration policy decides.
   */
  callerPolicies?: string[];
  /** What a run by hand does, per agent id; `POST threads/start` and the agents of a subject show it. */
  runNow?: Record<string, NhAssistantMockRunNow>;
  /** Runs that happened before the first request, for the run histories and their results. */
  runs?: NhAssistantMockRun[];
  /**
   * The application's groups of agents (`AddAgentGroup`) with the agents that join them in code: the
   * run catalog lists them, administrators put agents in them and take them out, and `GET boards`
   * shows their boards.
   */
  agentGroups?: NhAssistantMockAgentGroup[];
  /**
   * Proposals the agents left, for example ones a run for the whole application placed at projects:
   * the approval inbox, their thread and their subject's work list them, and people decide them.
   */
  proposals?: NhAssistantMockProposal[];
  /**
   * Consultations of agents by other assistants, as the consulted agent's thread lists them
   * (`GET threads/{id}/consultations`), newest first. Their proposals are those of `proposals` that
   * name them (`consultationId`), counted per route.
   */
  consultations?: NhAssistantMockConsultation[];
  /**
   * The application's own answer to an upload that passed the assistant's checks: `null` stores it,
   * a code refuses it as the server passes the file source's refusal on, for example
   * `'application-virus-found'` as `assistant-file-upload-refused`. People attach files to messages for
   * the agents whose summary in `agents` lists `uploads`; without such an agent the application keeps
   * no files for the assistant. Default: every upload is stored.
   */
  refuseUpload?: (upload: NhAssistantMockUpload) => string | null;
  /**
   * The application's upload scanner (`INhAssistantUploadScanner`), which the server runs after the
   * checks of type, size and signature and before the file source: `'refused'` refuses the upload with
   * `assistant-file-scan-refused`, `'failed'` with `assistant-file-upload-failed`, `null` lets it pass.
   * Default: no scanner, as on a server without one.
   */
  scanUpload?: (upload: NhAssistantMockUpload) => 'refused' | 'failed' | null;
}

/** A consultation of an agent by another assistant, as the consulted agent's thread lists it. */
export interface NhAssistantMockConsultation {
  id: string;
  /** The consulted agent's thread. */
  conversationId: string;
  askerActorId: string;
  askerDisplayName?: string | null;
  askingAgentId?: string | null;
  /** Default `completed`. */
  status?: AgentConsultationStatus;
  /** When it started, an ISO date. Default when the mock started. */
  createdAt?: string;
}

/**
 * A proposal an agent left, as the mock serves it; the mock fills what it leaves out. With
 * `placedBy` a run placed it here from another subject, such as a run for the whole application.
 * With `consultationId` another assistant consulted the agent for `initiatorActorId`, and
 * `consultationRoute` says who decides it: an `asker-decides` proposal waits inline for the person who
 * asked and nobody decides it here (`assistant-consult-asker-decides`); a `subject-agent-rights`
 * proposal runs with the agent's own rights and its asker may not decide it
 * (`assistant-four-eyes-required`).
 */
export interface NhAssistantMockProposal {
  approvalId: string;
  agentId: string;
  subjectType: string;
  subjectId: string;
  subjectDisplayName?: string | null;
  subjectTypeName?: string | null;
  subjectLink?: string | null;
  /** The thread the proposal waits in. Default `{approvalId}-thread`. */
  conversationId?: string;
  runId?: string | null;
  toolId: string;
  presentation?: ApprovalPresentation | null;
  /** Default `pending`. */
  status?: AgentProposalStatus;
  rationale?: string | null;
  placedBy?: AgentProposalOrigin | null;
  /**
   * The subject the proposal's subject belonged to when it was made, such as the project of a task;
   * the grouped inbox groups by it. Default none.
   */
  parent?: AgentProposalParent | null;
  /** The division of the proposal's subject, which the inbox's `divisionId` filter matches. Default none. */
  divisionId?: string | null;
  /** When the proposal was made, an ISO date. Default when the mock started. */
  createdAt?: string;
  /** When the proposal expires, an ISO date. Default three days after it was made. */
  expiresAt?: string;
  /**
   * Whether the signed-in user may decide it. Default `true`. A proposal of the whole application
   * (`nh-system`) exists only for its approvers who pass the agent's view policy, by default its
   * required policy: the administrators in the mock; for anybody else it is not found at all.
   */
  canDecide?: boolean;
  /** Why not, when `canDecide` is `false`. Default `assistant-approval-policy-denied`. */
  reasonCode?: string | null;
  /** The consultation that proposed it. Default none. */
  consultationId?: string | null;
  /** Who decides a consultation's proposal and whose rights run it. Default none. */
  consultationRoute?: NhAssistantConsultationRoute | null;
  /** The person who asked through the consultation, or who started the run. Default none. */
  initiatorActorId?: string | null;
  /**
   * Whose rights execute it. Default `agent`, and `delegated` for a consultation's proposal that runs
   * with the asker's rights.
   */
  grantSource?: AgentGrantSource;
}

/** One request the mock API received, for assertions in tests. */
export interface NhAssistantMockRequest {
  method: string;
  path: string;
  headers: Record<string, string>;
  body: unknown;
}
