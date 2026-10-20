import {
  AdminAgentInput,
  AgentRunStatus,
  AgentSummary,
  AgentTriggerReceipt,
  AssistantPreferences,
  AssistantStatus,
  ClientContext,
  Conversation,
  McpDiscovery,
  McpServerInput,
  AssistantAccount,
  RunCatalog,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';

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
 * the `approved` or `rejected` steps.
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
  };
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

/** A preconfigured agent. Code agents default to the scenario's chat agents. */
export interface NhAssistantMockAdminAgent extends AdminAgentInput {
  source: 'code' | 'admin';
  /** The skills of a code agent, by id and description; they start active, and in shadow with `shadow`. */
  skills?: { skillId: string; description: string; shadow?: boolean }[];
  /** The live agent this code agent is a shadow variant of. */
  shadowOf?: string;
  /** The agent starts in shadow, as with `Shadow()` in code. */
  shadow?: boolean;
  /** The technical account in code, as with `ServiceAccount(actorId)`. */
  serviceAccount?: string;
  /**
   * Whether a code agent says what it works on, as with `WorksOn`; by default it does when the
   * scenario gives `worksOnSubjectTypes`, `worksOnAllSubjects` or `worksOnApplication`.
   */
  worksOnDeclared?: boolean;
  /** The object types the agent is known to work on, shown as a hint next to "All objects". */
  knownSubjectTypes?: string[];
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
  /** Triggers in code, per agent; they cannot change in the administration. */
  codeTriggers?: { agentId: string; triggerId: string; source: 'event' | 'schedule' | 'webhook'; skillId?: string | null }[];
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
 * this status and result, leaving this many open proposals.
 */
export interface NhAssistantMockRunNow {
  /** Default `completed`. */
  status?: AgentRunStatus;
  /** The run's result; default none. */
  result?: string | null;
  /** The open proposals the run leaves on its subject. Default 0. */
  proposals?: number;
  /** The receipt's outcome. Default `started`. */
  outcome?: AgentTriggerReceipt['outcome'];
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
}

/** One request the mock API received, for assertions in tests. */
export interface NhAssistantMockRequest {
  method: string;
  path: string;
  headers: Record<string, string>;
  body: unknown;
}
