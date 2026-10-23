/*
 * Wire models of the assistant preference and administration endpoints.
 *
 * Literal translation of the assistant API contract (preferences and `admin/*`). Keep the
 * field names identical to the JSON the server returns.
 */
import { AgentProposalFeedbackCategory, AgentProposalFeedbackGroup, AgentProposalStatus, AgentSubjectRole } from './assistant-agent-run.models';
import { ApprovalPresentation, NhAssistantConsultationRoute, StaleKind } from './assistant-api.models';

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

/**
 * An MCP server that agents may use in background runs with the caller's own account once the
 * caller allows it (`GET mcp-connections`). The consent holds for the server as it is configured
 * now; a changed address or authentication shows it as not allowed again.
 */
export interface McpConnection {
  serverId: string;
  displayName: string;
  /** The server's host name, for example `planning.example.org`. */
  host: string;
  allowBackgroundUse: boolean;
  /** When the caller allowed it; `null` while not allowed. */
  consentedAt: string | null;
  /** The agents the caller may use that have the server. */
  agentIds: string[];
  /**
   * For a server each person connects to with OAuth: the caller's connection; `null` for a server
   * that forwards the caller's sign-in.
   */
  connectionStatus: McpConnectionStatus | null;
  /** The scopes the caller's connection grants. */
  grantedScopes: string | null;
  /** Scopes the server asked for that the connection lacks; connecting again adds them. */
  requiredScopes: string | null;
  connectedAt: string | null;
  /** Whether background use is offered for the server at all. */
  offersBackgroundUse: boolean;
}

/** Body of `PUT mcp-connections/{serverId}`. */
export interface UpdateMcpConnectionRequest {
  allowBackgroundUse: boolean;
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
  /**
   * The object type a call of the tool names (`[NhAiSubject]`): a run places its proposal for another
   * object at that object. `null` when the tool declares none, as MCP tools never do; their proposals
   * stay in the run's own thread. Missing on older servers.
   */
  subjectType?: string | null;
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
  /**
   * The object types an agent created in the administration works on, from the run catalog's
   * `subjectTypes`, at most 20. What an agent works on is sent together: omitting all of
   * `worksOnSubjectTypes`, `worksOnAllSubjects`, `worksOnApplication`, `perDivision`, `runPolicy`
   * and `viewPolicy` keeps it on update and lets a new agent work on all object types. Agents in
   * code take it from their code.
   */
  worksOnSubjectTypes?: string[];
  /** The agent works on every object type; not next to `worksOnSubjectTypes`. */
  worksOnAllSubjects?: boolean;
  /** The agent works on the whole application. */
  worksOnApplication?: boolean;
  /**
   * The agent works per division; only when the run catalog reports `divisions`. Its work for the
   * whole application, such as a schedule without a subject set, then runs once per listed division.
   */
  perDivision?: boolean;
  /** Who may run the agent for the whole application, from the run catalog's `agentPolicies`; `null`: the administration policy. */
  runPolicy?: string | null;
  /** Who sees its results for the whole application, from the run catalog's `agentPolicies`; `null`: the agent's required policy. */
  viewPolicy?: string | null;
  /**
   * On create only: the new agent starts off, so its events, schedules and other triggers are
   * ignored until someone switches it on with `setAgentState`. Omitted starts it active; ignored on
   * update.
   */
  startsOff?: boolean;
  /**
   * For an agent created in the administration: values of the run catalog's `agentMetadata` entries
   * by name, saved with the definition's version; `null` removes one, entries left out keep theirs.
   * Field errors are `metadata.<name>`. An agent in code changes its editable entries one by one with
   * `saveAgentMetadata`.
   */
  metadata?: Record<string, unknown>;
  /**
   * For an agent created in the administration: the model profile of its model calls, one of the run
   * catalog's `profiles` (`profile: not-found` otherwise). Omitted keeps it on update; a new agent uses
   * the application's chat profile. Agents in code take it from their code.
   */
  profile?: string | null;
  /**
   * For an agent created in the administration: the classification of the data its model calls carry,
   * in people's chats with it and in its runs (`dataClassification: invalid` for another value). A
   * skill's own wins. Omitted keeps it on update; a new agent has none, so its calls are `internal`.
   * Agents in code take it from their code.
   */
  dataClassification?: AssistantDataClassification | null;
  /**
   * On create only: the live agent the new agent is a data shadow variant of, within the ceiling in
   * code (`UseShadowVariantsAsData`). The variant receives every trigger of the live agent once it has
   * run settings, always works in shadow, is never offered in the chat and never changes its live
   * agent. Outside the ceiling it fails with `assistant-shadow-variants-not-allowed`, beyond its number
   * of variants with `assistant-configuration-limit`, and with the field errors `shadowOf: not-found`
   * and `shadowOf: invalid` for a live agent without a run configuration or one that is no live agent.
   * Ignored on update.
   */
  shadowOf?: string | null;
  /**
   * For an agent created in the administration, when the run catalog reports `instructionAssets`:
   * the application's asset its instructions come from instead of `instructions`, which is then
   * empty. The save checks it with the application (`instructionsAsset: not-found`,
   * `instructionsAsset.hash: invalid`). Omitted or `null` keeps or makes the instructions text.
   */
  instructionsAsset?: InstructionAssetReference | null;
}

/**
 * The asset of the application a skill version or an agent created in the administration takes its
 * instructions from: its id, exact version and the SHA-256 of its content. A new version of the asset
 * takes effect only when an administrator saves it as the next version.
 */
export interface InstructionAssetReference {
  assetId: string;
  version: number;
  /** The SHA-256 of the asset's content, lowercase hex. */
  hash: string;
}

/**
 * Why instructions from an asset are not available: the application no longer has a source, the asset
 * is missing, its text changed since it was saved, it cannot be instructions, or the source failed.
 */
export type InstructionAssetProblem =
  | 'no-asset-source'
  | 'asset-missing'
  | 'asset-changed'
  | 'asset-invalid'
  | 'asset-unavailable';

/** One of the application's assets, without its content (`GET admin/instruction-assets`). */
export interface InstructionAssetSummary {
  assetId: string;
  version: number;
  /** The hash its manifest states; a save checks it against the content. */
  hash: string;
  /** The asset that replaces it, when the application deprecated it. */
  replacedByAssetId?: string | null;
  deprecatedAt?: string | null;
}

/** The application's assets an administrator may choose as instructions. */
export interface InstructionAssetList {
  /** Whether the application registered a text asset source. */
  supported: boolean;
  /** The assets it lists, in its order; empty when it lists none, so the id and version are typed. */
  assets: InstructionAssetSummary[];
}

/** The text of one asset, read-only (`GET admin/instruction-assets/{id}/versions/{version}`). */
export interface InstructionAssetText {
  assetId: string;
  version: number;
  /** The SHA-256 of `content`: the hash a reference stores. */
  hash: string;
  content: string;
  /** `asset-invalid` when the asset cannot be instructions, so a save refuses it. */
  problem?: InstructionAssetProblem | null;
}

/** Body of `PUT admin/agents/{id}`. */
export type UpdateAdminAgentRequest = AdminAgentInput & { expectedVersion: number };

/**
 * The run state of an agent. `paused`: no new runs; events wait and run after the resume.
 * `stopped`: active runs were cancelled and events are refused. `off`: events, schedules and other
 * triggers are ignored and nothing waits, while running runs finish, proposals stay decidable and
 * people still chat with it and start it by hand; switching it on replays nothing.
 * `configured-off`: disabled in the application's code until the next deployment; it cannot be
 * resumed here.
 */
export type AgentRunState = 'active' | 'paused' | 'stopped' | 'off' | 'configured-off';

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
  /**
   * `true` while the agent is stopped and was off when it stopped: releasing the kill switch switches
   * it back off, while resuming switches it on. `false` otherwise; missing on older servers.
   */
  offBeforeStop?: boolean;
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
  /**
   * Whether the agent says what it works on: always for an agent created here, for an agent in code
   * only with `WorksOn`, `WorksOnAllObjects` or `WorksOnApplication`. An agent that does not say
   * works on all object types; missing on older servers.
   */
  worksOnDeclared?: boolean;
  /**
   * The object types the agent is known to work on, as a hint next to "All objects": those it
   * accepts hand-offs and consultations about, those its triggers name and those of its threads.
   */
  knownSubjectTypes?: string[];
  /**
   * The groups of agents the agent is in now, by id: an administrator's choice per group, otherwise
   * its `InGroup` in code; missing on older servers. Change them with `setAgentGroups`.
   */
  groups?: string[];
  /** The groups the agent joins in code, by id; empty for an agent created here. */
  codeGroups?: string[];
  /**
   * The model profile of the agent's model calls: from its code, or chosen here for an agent created
   * here; missing on older servers.
   */
  profile?: string;
  /**
   * The classification of the data the agent's model calls carry: from its code, or chosen here for
   * an agent created here; `null` when it has none, so its calls are `internal`. Missing on older
   * servers.
   */
  dataClassification?: AssistantDataClassification | null;
  /**
   * Why the agent, saved and in effect, may still not work as configured (`AssistantConfigurationWarning`):
   * `classification-not-permitted` when neither its model profile nor the profile's fallbacks permit
   * its classification. A warning never refuses a save; missing on older servers.
   */
  warnings?: AssistantConfigurationWarning[];
  /**
   * Why the instructions of `instructionsAsset` are not available: the agent then starts no model
   * turn and `instructions` is empty. `null` when they resolve; missing on older servers. With an
   * asset that resolves, `instructions` is its text, read-only.
   */
  instructionsProblem?: InstructionAssetProblem | null;
}

/** The groups one agent is in, as `setAgentGroups` answers. */
export interface AdminAgentGroups {
  agentId: string;
  /** The groups the agent is in now, by id. */
  groups: string[];
  /** The groups it joins in code, by id. */
  codeGroups: string[];
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
  runState: 'active' | 'paused' | 'stopped' | 'off';
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
  /**
   * `true` while the agent is stopped and was off when it stopped: releasing the kill switch switches
   * it back off, while resuming switches it on. `false` otherwise; missing on older servers.
   */
  offBeforeStop?: boolean;
  skills?: AgentSkillState[];
  shadowOf?: string | null;
  /** `null` for an agent without runs, which only chats. */
  shadow?: AgentShadowState | null;
  serviceAccount?: AgentServiceAccountState | null;
  /**
   * The routes of the agent's consultations, as its consultations in code and as data allow them and
   * an administrator switched them; `null` when nobody can consult the agent: it has no consultation
   * in code and none defined as data in effect. Missing on older servers.
   */
  consultationRoutes?: AgentConsultationRoutesState | null;
}

/**
 * The routes of an agent's consultations: one entry per route, and the last change at runtime. The
 * consultations are the ceiling: a route none of them allows can never be switched on.
 */
export interface AgentConsultationRoutesState {
  routes: AgentConsultationRouteState[];
  /** When the routes last changed at runtime; `null` while they follow the code. */
  changedAt: string | null;
  changedBy: string | null;
  reason: string | null;
}

/** One route of an agent's consultations. */
export interface AgentConsultationRouteState {
  route: NhAssistantConsultationRoute;
  /** Whether a consultation of the agent, in code or as data in effect, allows the route: the ceiling. */
  allowed: boolean;
  /** The switch an administrator set: `true` on, `false` off, `null` following the code. */
  override: boolean | null;
  /** Whether consultations use the route now: allowed and not switched off. */
  on: boolean;
  /** The subject types whose consultation allows the route. */
  subjectTypes: string[];
}

/** A switch of a consultation route: on, off, or back to the setting in code. */
export type AgentConsultationRouteSwitch = 'on' | 'off' | 'code';

/**
 * Body of `POST admin/agents/{id}/consultation-routes`. A route left out keeps its switch; switching
 * on a route that code does not allow fails with `409 assistant-consult-route-not-allowed`.
 */
export interface SetAgentConsultationRoutesRequest {
  askerDecides?: AgentConsultationRouteSwitch;
  subjectAgentRights?: AgentConsultationRouteSwitch;
  subjectAskerRights?: AgentConsultationRouteSwitch;
  /** Optional, at most 500 characters. */
  reason?: string | null;
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

/** Body of `POST admin/agents/{id}/state`. `active` resumes the agent or switches it on. */
export interface SetAgentStateRequest {
  state: 'active' | 'paused' | 'stopped' | 'off';
  /** Optional, at most 500 characters. */
  reason?: string | null;
}

/**
 * The built-in authentication modes. `oauth` connects with the authorization code flow: once for the
 * organization or once per person (`McpConnectionScope`). `oauth-client-credentials` connects the
 * organization with the host's own client credentials.
 */
export type McpBuiltInAuthMode = 'none' | 'bearer' | 'api-key' | 'basic' | 'forward-user-token' | 'oauth' | 'oauth-client-credentials';

/**
 * The authentication mode of a server: a built-in mode, or a mode the application registered in code
 * (`AddMcpCredentialProvider`), which `NhAssistantAdminApiService.getMcpAuthModes` lists.
 */
export type McpAuthMode = McpBuiltInAuthMode | (string & {});

/**
 * An authentication mode the application registered in code. Its provider supplies the credential, so
 * a server of the mode takes no secret, user name or OAuth settings in the editor. `organization`: one
 * credential every caller shares, which may serve background runs (`headlessUse: organization`);
 * `person`: each person's own credential, which serves only that person's background runs with their
 * consent (`headlessUse: personal`).
 */
export interface McpCustomAuthMode {
  mode: string;
  ownership: 'organization' | 'person';
}

/** For `oauth`: one connection for everyone (`organization`), or each person connects (`user`). */
export type McpConnectionScope = 'organization' | 'user';

/** The state of an OAuth connection; it never carries tokens. */
export type McpConnectionStatus = 'not-connected' | 'active' | 'reauthorization-required' | 'issuer-changed';

/**
 * OAuth settings of a server. Omitted or `null` values keep the stored value, `""` clears it.
 * `clientSecret` is write-only. `confirmAuthorization` runs discovery again and pins the
 * authorization server and resource it finds; a new OAuth server and a changed address need it.
 */
export interface McpOAuthInput {
  clientId?: string | null;
  clientSecret?: string | null;
  clientSecretReference?: string | null;
  /** Space-separated scopes the host may request; empty leaves the choice to the authorization server. */
  scopes?: string | null;
  /** Which account the organization connects, shown to everyone using it; required for an organization `oauth` server. */
  organizationAccountLabel?: string | null;
  confirmAuthorization?: boolean;
}

/** The OAuth settings of a server as returned: never the client secret. */
export interface McpOAuthSettings {
  clientId: string | null;
  hasClientSecret: boolean;
  clientSecretReference: string | null;
  scopes: string | null;
  organizationAccountLabel: string | null;
}

/** The authorization server and resource an administrator confirmed. */
export interface McpAuthorization {
  issuer: string;
  resource: string;
  confirmedAt: string;
  confirmedBy: string | null;
}

/** The organization's OAuth connection of a server, without tokens. */
export interface McpOrganizationConnection {
  status: McpConnectionStatus;
  connectedAt: string | null;
  connectedBy: string | null;
  grantedScopes: string | null;
  requiredScopes: string | null;
  accessTokenExpiresAt: string | null;
  lastFailureCode: string | null;
}

/** Body of `POST admin/mcp-servers/discover`. */
export interface McpDiscoverRequest {
  url: string;
}

/** What discovery found for a server address; nothing is stored until the server is saved with `confirmAuthorization`. */
export interface McpDiscovery {
  resource: string;
  issuer: string;
  authorizationServers: string[];
  challengedScopes: string[];
  scopesSupported: string[];
  grantTypesSupported: string[];
  pkceSupported: boolean;
  clientIdMetadataDocumentSupported: boolean;
  dynamicRegistrationSupported: boolean;
  tokenEndpointAuthMethods: string[];
}

/**
 * Body of an authorize request. `returnUrl` chooses redirect mode: the callback returns there with
 * the flow and completion code in the fragment; its origin must be allowed by the host.
 */
export interface McpAuthorizeRequest {
  returnUrl?: string | null;
  /** For a personal connection: the consent to background use, recorded with the connection. */
  allowBackgroundUse?: boolean | null;
}

/** The flow and the URL the owner's browser opens; it expires at `expiresAt`. */
export interface McpAuthorizationStart {
  flowId: string;
  authorizationUrl: string;
  expiresAt: string;
  /** The origin of the assistant's callback page: the only origin whose message is accepted. */
  callbackOrigin: string;
}

/** Body of `POST mcp-connections/complete`. */
export interface McpCompleteRequest {
  flowId: string;
  completionCode: string;
}

/** The connection a completed authorization activated. */
export interface McpCompletion {
  serverId: string;
  ownerKind: 'organization' | 'user';
  status: McpConnectionStatus;
  grantedScopes: string | null;
}

/** The personal connections of a server per status. */
export interface McpConnectionsSummary {
  active: number;
  reauthorizationRequired: number;
  issuerChanged: number;
}

/**
 * Whether a server's tools are offered in background runs. `organization` is only valid for
 * `none`, `bearer`, `api-key` and `basic`; the agent then also needs a grant in code
 * (`mcp.<server>.*`). `personal` is only valid for `forward-user-token`: the run then sends a
 * short-lived token of the person it acts for, once that person allowed it (`McpConnection`).
 */
export type McpHeadlessUse = 'never' | 'organization' | 'personal';

/**
 * `streamable-http`, or `auto` to fall back to the deprecated HTTP+SSE transport for servers
 * that do not offer Streamable HTTP yet.
 */
export type McpTransport = 'streamable-http' | 'auto';

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
  /** Omit to keep the stored value; a new server starts at `never`. */
  headlessUse?: McpHeadlessUse;
  /** The user name of `basic`; the password is `secret`. Omit to keep the stored value. */
  userName?: string | null;
  /**
   * Where the secret is instead of storing it: `config:<name>` or a prefix the application
   * registered. Omit to keep, `""` to clear; a new stored secret replaces it and vice versa.
   */
  secretReference?: string | null;
  /** The name of a client certificate the host configured for mutual TLS. Omit to keep, `""` to clear. */
  clientCertificateReference?: string | null;
  /** Omit to keep the stored value; a new server starts at `streamable-http`. */
  transport?: McpTransport;
  /** For `oauth`; omit to keep the stored value. A new server starts at `organization`. */
  connectionScope?: McpConnectionScope;
  /** The OAuth settings of `oauth` and `oauth-client-credentials`; omit to keep them. */
  oauth?: McpOAuthInput | null;
  /**
   * The readers of the server, at most 16; omit to keep them, `[]` removes them all. A refused save
   * names the field of a reader with its index, such as `readers[0].toolName`.
   */
  readers?: McpReaderInput[] | null;
}

/**
 * One reader of an MCP server: its synced tool `toolName` turns files of `mediaTypes` into text for
 * models that do not read those types themselves. A file above `maxClassification` is never sent to
 * it; sending a file to an MCP server is data leaving the application. The assistant asks the
 * application's own readers first, then the MCP readers by `priority`, highest first, then by server
 * id and tool name.
 */
export interface McpReader {
  mediaTypes: string[];
  toolName: string;
  maxClassification: AssistantDataClassification;
  priority: number;
}

/**
 * A reader as saved: one to 16 media types; `toolName` must name a tool synced from the server, one
 * reader per tool; the tool receives `name`, `mediaType` and the base64 `content` of a file and returns
 * its text. `priority` is a whole number from -1000 to 1000; omitted it is 0.
 */
export interface McpReaderInput {
  mediaTypes: string[];
  toolName: string | null;
  maxClassification: AssistantDataClassification | null;
  priority?: number | null;
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
  headlessUse: McpHeadlessUse;
  /** When a test, a sync or a turn with the shared credential last connected. */
  lastConnectAt: string | null;
  /** The failure code of that connection; `null` when it succeeded or never ran. */
  lastConnectCode: string | null;
  /** The user name of `basic`; the password is never returned. */
  userName: string | null;
  /** The secret reference; it names where the secret is, it is not the secret. */
  secretReference: string | null;
  clientCertificateReference: string | null;
  transport: McpTransport;
  connectionScope: McpConnectionScope;
  /** The OAuth settings; `null` for other modes. */
  oauth: McpOAuthSettings | null;
  /** The confirmed authorization server; `null` until an administrator confirmed it. */
  authorization: McpAuthorization | null;
  /** The organization's OAuth connection; `null` for servers without one. */
  organizationConnection: McpOrganizationConnection | null;
  /** The readers of the server, by priority, highest first; empty when it reads no files. */
  readers: McpReader[];
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
  /**
   * For a trigger event that names its object type up front, that type: data triggers on it fit only
   * agents that work on it. Missing or `null` when the type is known only when the event fires.
   */
  subjectType?: string | null;
  /**
   * For a trigger event: the named filters a data trigger on it may select, by name with what each
   * skips, in the order the application registered them. `null` for other building blocks; missing
   * on older servers.
   */
  filters?: RunBlock[] | null;
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
  /**
   * The subject types the application registered, by id, with the platform's `nh-system` (the whole
   * application, `isSystem`); missing on older servers. Choices of object types leave out `isSystem` types.
   */
  subjectTypes?: AgentSubjectType[];
  /**
   * The policies the application lists for the run and view policy of agents created here, by name;
   * missing on older servers.
   */
  agentPolicies?: RunBlock[];
  /** Whether the application lists its divisions, so an agent may work per division. */
  divisions?: boolean;
  /**
   * The groups of agents the application registers in code, by id, with their names in the request's
   * language: what an administrator may put an agent in; missing on older servers.
   */
  agentGroups?: AgentGroup[];
  /**
   * The application's file limits: the run settings of an agent created here and a skill defined here
   * can only lower them; missing on older servers.
   */
  fileLimits?: FileLimits | null;
  /**
   * The application's ceiling of consultations defined as data (`UseConsultationsAsData`): what agents
   * created here may be consulted with. `null` while consultations exist in code only; missing on
   * older servers.
   */
  consultationCeiling?: ConsultationCeiling | null;
  /**
   * The entries of agent metadata the application registers with `AddAgentMetadata`, ordered by name:
   * the slots of the agent editor; missing on older servers.
   */
  agentMetadata?: AgentMetadataEntry[];
  /**
   * Whether the application registered a text asset source, so skills and agents created here may take
   * their instructions from its assets; missing on older servers.
   */
  instructionAssets?: boolean;
}

/** An entry of agent metadata registered in code with `AddAgentMetadata`. */
export interface AgentMetadataEntry {
  /** The entry's dash-case name. */
  name: string;
  /** Administrators may change it on agents in code, as an override. */
  editable: boolean;
  /** The largest value in bytes of its JSON. */
  maxBytes: number;
  /** It declares a searchable value with `IndexBy`. */
  indexed: boolean;
}

/** Where the value of an agent's metadata entry comes from. */
export type AgentMetadataSource = 'code' | 'override' | 'data';

/**
 * One entry of agent metadata of one agent (`GET admin/agents/{id}/metadata`): the application's own
 * data about the agent, which NewHeap stores and checks but never interprets.
 */
export interface AgentMetadata {
  name: string;
  /**
   * Whether it changes here: always for an agent created here, for an agent in code only when the
   * entry is editable.
   */
  editable: boolean;
  maxBytes: number;
  /** Where the value in effect comes from; `null` without a value. */
  source: AgentMetadataSource | null;
  /** The value in effect, in the shape of the entry's type; `null` without one. */
  value: unknown;
  /**
   * The version a change sends as `expectedVersion`: the agent's version for an agent created here,
   * the override's version for an agent in code, `null` while it has no override.
   */
  version: number | null;
  /** The agent's version: a change of an agent created here makes its next version. */
  agentVersion: number;
  /** The value in code of an agent in code, next to an override. */
  codeValue?: unknown;
  indexed?: boolean;
  /** The searchable value of the value in effect, trimmed and in lowercase. */
  indexValue?: string | null;
  changedAt?: string | null;
  changedBy?: string | null;
}

/** Body of `PUT admin/agents/{id}/metadata/{name}`. */
export interface AgentMetadataRequest {
  /** The value in the shape of the entry's type (camelCase); required. */
  value: unknown;
  /** The entry's `version` the change was made on; `null` for a new override of an agent in code. */
  expectedVersion: number | null;
}

/**
 * A ceiling in code of consultations defined as data: the routes their proposals may use (empty allows
 * answers only), whether they may generate the request tool and be seen by MCP clients, and their
 * highest limits.
 */
export interface ConsultationCeiling {
  routes: NhAssistantConsultationRoute[];
  allowRequests: boolean;
  allowMcp: boolean;
  /** The most tool calls of one consultation. */
  maxToolCalls: number;
  /** The longest consultation, in seconds. */
  maxTimeoutSeconds: number;
  /** The longest answer, in characters. */
  maxAnswerChars: number;
  /** The most thread messages a consulted agent reads. */
  maxHistoryMessages: number;
  /** The most consultations defined as data of one agent. */
  maxConsultations: number;
  /** The longest instructions of a consultation defined as data. */
  maxInstructionsLength: number;
}

/**
 * File limits: the largest file in bytes, the most pages of one file, the most files of one turn and
 * the most estimated file tokens of one turn.
 */
export interface FileLimits {
  maxFileBytes: number;
  maxFilePages: number;
  maxFilesPerTurn: number;
  maxFileTokensPerTurn: number;
}

/**
 * Lower file limits of an agent or a skill that accepts files; `null` or missing keeps the
 * application's (for a skill: the agent's). Each is at most the application's limit.
 */
export interface LowerFileLimits {
  maxFileBytes?: number | null;
  maxFilePages?: number | null;
  maxFilesPerTurn?: number | null;
  maxFileTokensPerTurn?: number | null;
}

/** A group of agents the application registers in code, as people see it. */
export interface AgentGroup {
  /** The dash-case group id, such as `project-management`. */
  id: string;
  /** The group's name in the request's language. */
  name: string;
  /** What the group's agents do, when the application described it. */
  description: string | null;
}

/** A subject type as people see it, from the run catalog. */
export interface AgentSubjectType {
  /** The dash-case type id, such as `project`. */
  id: string;
  /** The name of one subject in the request's language; the id when the application did not name the type. */
  name: string;
  /** The name of several subjects, when the application gave one. */
  pluralName: string | null;
  /** The short type name of the entity the type stands for, as the NewHeap log records `ObjectType`. */
  entityType: string | null;
  /** The full type name of that entity, as `ObjectTypeFull`. */
  entityTypeFull: string | null;
  /** The link template with `{id}`; `null` without one. */
  link: string | null;
  /** Whether `searchSubjects` finds subjects of this type by name; otherwise people give an id. */
  searchable: boolean;
  /**
   * Whether the type belongs to the platform instead of the application, such as `nh-system`, the
   * whole application; it is not offered as a kind of object.
   */
  isSystem: boolean;
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
export type RunConfigurationProblemKind = 'agent' | 'skill' | 'candidate' | 'trigger' | 'consultation' | 'hand-off';

/**
 * Data that is out of effect because it no longer fits the code, for example after a deploy removed
 * a profile, policy, rule handler, event or subject set it names. A consultation defined as data may
 * also stay in effect without the parts that no longer fit, each with a problem.
 */
export interface RunConfigurationProblem {
  agentId: string;
  kind: RunConfigurationProblemKind;
  /** The agent, skill or trigger id; the subject type of a consultation. */
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
export interface AgentRunSettings extends LowerFileLimits {
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
  /**
   * People's messages and the agent's tools may give it files; missing on older servers. Off for a new
   * agent.
   */
  acceptsFiles?: boolean;
}

/** Body of `PUT admin/agents/{id}/run-settings`. */
export interface AgentRunSettingsRequest extends LowerFileLimits {
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
  /** People's messages and the agent's tools may give it files; `false` by default. */
  acceptsFiles?: boolean;
}

/** A skill the agent declares in code; it cannot change here. */
export interface CodeSkill {
  skillId: string;
  description: string;
  /**
   * The skill starts off in code (`StartsOff()`): it was off when the application first saw it. Its
   * state now is in the skill's state; missing on older servers.
   */
  startsOff?: boolean;
  /** The skill accepts files in code (`AcceptsFiles()`); missing on older servers. */
  acceptsFiles?: boolean;
}

/** A trigger registered in code; it cannot change here. */
export interface CodeTrigger {
  triggerId: string;
  source: 'event' | 'schedule' | 'webhook';
  skillId: string | null;
  /**
   * The subject type the trigger names up front: an object type, or `nh-system` for a schedule that
   * runs for the whole application (once per division for an agent that works per division). `null`
   * when the type is known only when it fires; missing on older servers.
   */
  subjectType?: string | null;
}

/**
 * How an agent in code runs, as its code says; it changes only in code. The technical account, which
 * an administrator may replace, is the agent's `serviceAccount`.
 */
export interface CodeRunSettings {
  /** Whose rights its runs use. */
  accountability: AgentAccountability;
  /** Its runs also use what the person who started them may use. */
  delegateFromInitiator: boolean;
  /** The agent's approval policy; `null` for the platform's default. */
  approvalPolicy: string | null;
  /** It works by a rule handler in code instead of the model. */
  usesRules: boolean;
  /**
   * The agent starts off in code (`StartsOff()`): it was off when the application first saw it. Its
   * state now is the agent's `runState`; missing on older servers.
   */
  startsOff?: boolean;
  /** The agent accepts files in code (`AcceptsFiles()`); missing on older servers. */
  acceptsFiles?: boolean;
}

/**
 * How sensitive the data is that a model call carries (`NhAiDataClassification`); the model profile of
 * the call must permit it.
 */
export type AssistantDataClassification = 'public' | 'internal' | 'confidential' | 'restricted';

/**
 * Why an agent or a skill that is saved and in effect may still not work as configured
 * (`NhAssistantConfigurationWarnings`). `classification-not-permitted`: neither its model profile nor
 * the profile's fallbacks permit its data classification, so its model calls are refused. A newer
 * server may report other codes.
 */
export type AssistantConfigurationWarning = 'classification-not-permitted' | (string & {});

/** One version of a skill defined as data. */
export interface DataSkill extends LowerFileLimits {
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
  /**
   * The classification of the data the skill's model calls carry; `null` uses the agent's, missing on
   * older servers.
   */
  dataClassification?: AssistantDataClassification | null;
  /**
   * A run in the skill reads files when its agent accepts them too; missing on older servers. Off for a
   * new skill.
   */
  acceptsFiles?: boolean;
  /**
   * Why the skill, saved and in effect, may still not work as configured: `classification-not-permitted`
   * when neither its model profile, or else its agent's, nor the profile's fallbacks permit its
   * classification, or else its agent's. A warning never refuses a save; missing on older servers.
   */
  warnings?: AssistantConfigurationWarning[];
  /**
   * The application's asset the version takes its instructions from; `null` for inline instructions
   * or none, missing on older servers. `problem` says when it no longer resolves to
   * `instructionsHash`; read its text with `getInstructionAsset`.
   */
  instructionsAsset?: InstructionAssetReference | null;
}

/** How a change of a skill takes effect: at once, or as a candidate in shadow next to the live version. */
export type SkillApplyMode = 'live' | 'shadow';

/** Body of `PUT admin/agents/{id}/skills/{skillId}`. */
export interface DataSkillRequest extends LowerFileLimits {
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
  /**
   * For a new skill only: it starts off, so triggers that name it are ignored and rules and triage do
   * not route to it until someone switches it on. A change of a skill fails with `startsOff: invalid`.
   */
  startsOff?: boolean;
  /**
   * The classification of the data the skill's model calls carry; `null` uses the agent's. The model
   * profile of its runs must permit it, or its model calls are refused.
   */
  dataClassification?: AssistantDataClassification | null;
  /** A run in the skill reads files when its agent accepts them too; `false` by default. */
  acceptsFiles?: boolean;
  /**
   * The application's asset the version takes its instructions from, instead of `instructions`
   * (`instructions: not-allowed` next to it), when the run catalog reports `instructionAssets`. The
   * save checks it with the application: `instructionsAsset: not-found`, `instructionsAsset.hash:
   * invalid` and `instructionsAsset: invalid`.
   */
  instructionsAsset?: InstructionAssetReference | null;
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
  /** The application's asset the version takes its instructions from; missing on older servers. */
  instructionsAsset?: InstructionAssetReference | null;
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
  /**
   * The subject set a schedule runs for; `null` for an event, and for a schedule that runs for the
   * whole application (once per division for an agent that works per division).
   */
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
  /**
   * For an event trigger: the names of the event's filters it applies, which must all accept an event.
   * A name the event no longer registers keeps the trigger from firing (problem `unknown-filter`).
   * Missing on older servers.
   */
  filters?: string[];
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
  /**
   * For a schedule: a registered subject set. Without one the schedule runs for the whole
   * application, which only an agent that works on it may do (`subjectSet: required` otherwise).
   */
  subjectSet?: string | null;
  /** For a schedule: what every run receives, at most 2,000 characters. */
  message?: string | null;
  conflict?: TriggerConflict | null;
  /** For an event trigger: at most 3,600; 120 by default. */
  debounceSeconds?: number | null;
  /** The trigger's current version; `null` for a new trigger. */
  expectedVersion?: number | null;
  reason?: string | null;
  /**
   * For an event trigger: names of the event's filters (`RunBlock.filters`) that must all accept an
   * event, at most 10; empty applies none. A name the event does not register fails with
   * `assistant-trigger-filter-not-found`; a schedule takes none.
   */
  filters?: string[] | null;
}

/** Body of `POST admin/agents/{id}/triggers/{triggerId}/shadow`. */
export interface DataTriggerShadowRequest {
  mode: 'shadow' | 'live';
  reason?: string | null;
}

/** Query of the removals of run settings, data skills, data triggers and data consultations. */
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
  /** How an agent in code runs, as its code says; `null` for an agent created here, missing on older servers. */
  codeRunSettings?: CodeRunSettings | null;
  /** The consultations the agent allows in code, read-only, by subject type; missing on older servers. */
  codeConsultations?: CodeConsultation[];
  /** The consultations of the agent defined as data, by subject type; missing on older servers. */
  dataConsultations?: DataConsultation[];
  /**
   * The ceiling within which the agent may get consultations as data: the application's for an agent
   * created here, the application's and its own together for an agent in code that is consultable as
   * data. `null` when it may get none; missing on older servers.
   */
  consultationCeiling?: ConsultationCeiling | null;
}

/** The role a person needs on the subject: to ask a consulted agent, or before it may propose. */
export type ConsultationSubjectRole = Exclude<AgentSubjectRole, 'none'>;

/** Where the generated tools of a consultation are offered. */
export type ConsultationExposure = 'local' | 'agent' | 'mcp';

/**
 * A consultation the agent allows in code with `AllowConsultation`; it cannot change here, apart from
 * the agent's route switches.
 */
export interface CodeConsultation {
  subjectType: string;
  description: string | null;
  /** The routes its registration allows, before the agent's route switches. */
  allowedRoutes: NhAssistantConsultationRoute[];
  allowRequests: boolean;
  exposure: ConsultationExposure[];
  /** The id of the generated consult tool, which asking agents select. */
  consultToolId: string;
  /** The id of the generated request tool; `null` without requests. */
  requestToolId: string | null;
  /** The agents whose tool selectors select the consult tool now, by id. */
  askingAgents: string[];
  /**
   * The tools the consulted agent is offered, from `Tools(...)` in code; `null` offers every tool of the
   * agent. Missing on older servers.
   */
  toolSelectors?: string[] | null;
}

/**
 * A consultation defined as data: other assistants may consult the agent about subjects of one type.
 * It is in effect once saved and answers only when an asking agent selects its consult tool.
 */
export interface DataConsultation {
  subjectType: string;
  version: number;
  /** What the asking model reads about when to consult the agent. */
  description: string;
  /** Extra instructions of the consulted agent. */
  instructions: string | null;
  instructionsHash: string | null;
  /** The role the asking person needs on the subject. */
  minimumRole: ConsultationSubjectRole;
  /**
   * The policy the asking person must pass, one the application lists with `AddAgentPolicy`; `null`
   * uses the agent's required policy, else the assistant's access policy.
   */
  askerPolicy: string | null;
  /** The routes as saved; empty for a consultation that only answers. */
  routes: NhAssistantConsultationRoute[];
  /** The role the asking person needs before the agent may propose. */
  proposalRole: ConsultationSubjectRole;
  /** The approval policy of its proposals; `null` uses the agent's. */
  proposalPolicy: string | null;
  maxToolCalls: number;
  timeoutSeconds: number;
  maxAnswerChars: number;
  maxHistoryMessages: number;
  allowRequests: boolean;
  /** MCP clients may see its tools, while the ceiling allows MCP. */
  exposeToMcp: boolean;
  /** The classification of its answers; `null` uses the agent's. */
  dataClassification: AssistantDataClassification | null;
  changedAt: string;
  changedBy: string;
  reason: string | null;
  /** `false` with `problem` when the consultation no longer fits the code. */
  inEffect: boolean;
  /** The first problem: the reason it is out of effect when it is. */
  problem: string | null;
  /**
   * Every problem: the reason it is out of effect, or each part that no longer fits and was dropped or
   * capped, such as `route-not-allowed` or `limit-above-ceiling`.
   */
  problems: string[];
  /** The routes in effect: those saved that still fit the ceiling and the agent, before its route switches. */
  routesInEffect: NhAssistantConsultationRoute[];
  /** The tool selection as saved; `null` offers every tool of the agent. Missing on older servers. */
  toolSelectors?: string[] | null;
  /**
   * The tool selection in effect: the saved selectors the agent's own selectors still cover. `null`
   * offers every tool of the agent; empty offers none, when every selector dropped with the problem
   * `tools-not-allowed`, or while the consultation is out of effect. Missing on older servers.
   */
  toolSelectorsInEffect?: string[] | null;
  /** The id of the generated consult tool, which asking agents select. */
  consultToolId: string;
  /** The id of the generated request tool while requests are in effect. */
  requestToolId: string | null;
  /** The agents whose tool selectors select the consult tool now, by id: the consultation's second key. */
  askingAgents: string[];
  createdAt: string;
  createdBy: string;
}

/**
 * Body of `PUT admin/agents/{id}/consultations/{subjectType}`. Limits left out start at the code's
 * defaults within the ceiling.
 */
export interface DataConsultationRequest {
  /** Required, at most 1,000 characters. */
  description: string;
  /** At most the ceiling's `maxInstructionsLength`, 4,000 characters. */
  instructions?: string | null;
  /** `reader` by default. */
  minimumRole?: ConsultationSubjectRole | null;
  /** A policy of the run catalog's `agentPolicies`; `null` uses the agent's required policy. */
  askerPolicy?: string | null;
  /**
   * `asker-decides` and at most one of `subject-asker-rights` and `subject-agent-rights`, within the
   * ceiling; empty keeps the consultation read-only. A route outside the ceiling fails with
   * `routes: not-allowed`.
   */
  routes?: NhAssistantConsultationRoute[];
  /** `participant` by default. */
  proposalRole?: ConsultationSubjectRole | null;
  /** A registered approval policy; `null` uses the agent's. */
  proposalPolicy?: string | null;
  /**
   * The tools the consulted agent is offered, by id or `prefix.*` selector, each covered by the
   * agent's own selectors; one outside them fails with `toolSelectors: not-allowed`. `null` or empty
   * offers every tool of the agent.
   */
  toolSelectors?: string[] | null;
  /** 0 to 32, at most the ceiling's. */
  maxToolCalls?: number | null;
  /** 1 to 600, at most the ceiling's. */
  timeoutSeconds?: number | null;
  /** 200 to 32,000, at most the ceiling's. */
  maxAnswerChars?: number | null;
  /** 0 to 200, at most the ceiling's. */
  maxHistoryMessages?: number | null;
  /** Also generates the request tool; only when the ceiling allows requests. */
  allowRequests?: boolean;
  /** MCP clients may see its tools; only when the ceiling allows MCP. */
  exposeToMcp?: boolean;
  /** `null` uses the agent's. */
  dataClassification?: AssistantDataClassification | null;
  /** The consultation's current version; `null` for a new consultation. */
  expectedVersion?: number | null;
  /** Optional, at most 500 characters. */
  reason?: string | null;
}
