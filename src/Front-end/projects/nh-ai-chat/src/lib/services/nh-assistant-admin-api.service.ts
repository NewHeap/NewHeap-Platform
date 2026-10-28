import { Injectable, inject } from '@angular/core';
import { Observable, forkJoin, map, of } from 'rxjs';
import {
  AdminAgent,
  AdminAgentGroups,
  AgentMetadata,
  AgentMetadataRequest,
  AdminAgentInput,
  AgentRunConfiguration,
  AgentRunSettings,
  AgentRunSettingsRequest,
  AgentEvaluation,
  AgentEvaluationFilter,
  AgentFeedbackExportFilter,
  AgentFeedbackFilter,
  AgentFeedbackList,
  AgentFeedbackListFilter,
  AgentFeedbackSummary,
  AgentReplay,
  AgentReplayList,
  AgentReplayRequest,
  AgentSkillState,
  AgentState,
  ApplicationContext,
  DataSkill,
  DataSkillRequest,
  DataSkillVersion,
  DataConsultation,
  DataConsultationRequest,
  DataConsultationStateRequest,
  DataTrigger,
  DataTriggerRequest,
  DataTriggerShadowRequest,
  DataTriggerVersion,
  ApplicationContextVersion,
  McpAuthorizationStart,
  McpAuthorizeRequest,
  McpCompleteRequest,
  McpCompletion,
  McpConnectionsSummary,
  McpDiscovery,
  McpOrganizationConnection,
  McpCustomAuthMode,
  McpServer,
  McpServerInput,
  McpServerTestResult,
  McpTool,
  AssistantAccount,
  InstructionAssetList,
  InstructionAssetText,
  RunCatalog,
  RunConfigurationRemoval,
  SetAgentConsultationRoutesRequest,
  SetAgentServiceAccountRequest,
  SetAgentShadowRequest,
  SetAgentStateRequest,
  SkillCandidateRequest,
  SkillComparison,
  SkillComparisonFilter,
  ToolCatalogEntry,
  TriggerCandidateRequest,
  TriggerComparison,
  UpdateAdminAgentRequest,
  UpdateApplicationContextRequest,
  UpdateMcpToolRequest
} from '../models/assistant-admin.models';
import {
  AgentPackage,
  AgentPackageExport,
  AgentPackageExportOptions,
  AgentPackageImport,
  AgentPackageImportOptions,
  AgentPackageParts,
  AgentPackagePreview,
  AgentPackageReadRequest,
  AgentPackageUnlink,
  AgentPackageUnlinkRequest,
  AgentPackageWarning,
  InstalledAgentPackage
} from '../models/assistant-agent-package.models';
import { AgentRunFilter, AgentRunHistory } from '../models/assistant-agent-run.models';
import {
  AgentHandOffs,
  AgentShadowVariants,
  DataHandOff,
  DataHandOffRequest,
  ShadowVariantPromotion,
  ShadowVariantPromotionRequest
} from '../models/assistant-hand-off.models';
import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import { NhAssistantApiService } from './nh-assistant-api.service';
import { NhAssistantApiError, NhAssistantStatusCodes, NhAssistantTransport, nhAssistantErrorMessageKey, nhAssistantIsSafeCode } from './nh-assistant-transport';

/** In the admin endpoints a 409 means a stale `expectedVersion` or a forbidden state change. */
const adminStatusCodes: NhAssistantStatusCodes = {
  409: NhAssistantClientErrorCodes.versionConflict
};

/** The most account ids one `GET admin/accounts?ids=` reads; `getAccounts` asks for more in parts. */
const maxAccountIds = 50;

/** The longest account id the server reads; a longer id names no account. */
const maxAccountIdLength = 256;

/** The header of the agent package export that lists the references to agents outside the selection. */
const packageWarningsHeader = 'X-NewHeap-Package-Warnings';

/**
 * Client for the assistant administration endpoints (`admin/*`). The server requires the
 * access policy and the admin policy; show the administration only when
 * `NhAssistantStore.canAdminister()` is true. Failures error with `NhAssistantApiError`.
 * Secrets are write-only: `McpServer` only reports `hasSecret`.
 */
@Injectable()
export class NhAssistantAdminApiService {
  private readonly transport = inject(NhAssistantTransport);
  private readonly api = inject(NhAssistantApiService);

  getContext(): Observable<ApplicationContext> {
    return this.request<ApplicationContext>('GET', 'admin/context');
  }

  /** Saves a new context version; fails with `assistant-version-conflict` when `expectedVersion` is stale. */
  updateContext(request: UpdateApplicationContextRequest): Observable<ApplicationContext> {
    return this.request<ApplicationContext>('PUT', 'admin/context', request);
  }

  getContextVersions(): Observable<ApplicationContextVersion[]> {
    return this.request<ApplicationContextVersion[]>('GET', 'admin/context/versions');
  }

  /** Local, bridge and MCP tools, for choosing tool selectors. */
  getTools(): Observable<ToolCatalogEntry[]> {
    return this.request<ToolCatalogEntry[]>('GET', 'admin/tools');
  }

  getAgents(): Observable<AdminAgent[]> {
    return this.request<AdminAgent[]>('GET', 'admin/agents');
  }

  createAgent(input: AdminAgentInput): Observable<AdminAgent> {
    return this.request<AdminAgent>('POST', 'admin/agents', input);
  }

  /**
   * Saves an agent's definition when `expectedVersion` is current. Omit `mcpServerIds` to keep its MCP
   * assignments, which `assignMcpServer` and `unassignMcpServer` change one by one; `[]` removes them.
   */
  updateAgent(agentId: string, request: UpdateAdminAgentRequest): Observable<AdminAgent> {
    return this.request<AdminAgent>('PUT', `admin/agents/${encodeURIComponent(agentId)}`, request);
  }

  /** Restores a code agent to its definition in code. The run state stays as it is. */
  resetAgent(agentId: string): Observable<AdminAgent> {
    return this.request<AdminAgent>('POST', `admin/agents/${encodeURIComponent(agentId)}/reset`);
  }

  getAgentState(agentId: string): Observable<AgentState> {
    return this.request<AgentState>('GET', `admin/agents/${encodeURIComponent(agentId)}/state`);
  }

  /**
   * Pauses, stops or resumes an agent without changing its definition. A code-disabled agent
   * cannot be resumed (`assistant-agent-configured-off`).
   */
  setAgentState(agentId: string, request: SetAgentStateRequest): Observable<AgentState> {
    return this.request<AgentState>('POST', `admin/agents/${encodeURIComponent(agentId)}/state`, request);
  }

  /**
   * Pauses, stops or resumes one skill of an agent; its other skills keep working.
   */
  setSkillState(agentId: string, skillId: string, request: SetAgentStateRequest): Observable<AgentSkillState> {
    return this.request<AgentSkillState>(
      'POST',
      `admin/agents/${encodeURIComponent(agentId)}/skills/${encodeURIComponent(skillId)}/state`,
      request);
  }

  /**
   * Puts an agent in shadow, live, or back to the mode in code. A shadow variant cannot change
   * (`assistant-shadow-variant`).
   */
  setAgentShadow(agentId: string, request: SetAgentShadowRequest): Observable<AgentState> {
    return this.request<AgentState>('POST', `admin/agents/${encodeURIComponent(agentId)}/shadow`, request);
  }

  /** Puts one skill of an agent in shadow, live, or back to the mode in code. */
  setSkillShadow(agentId: string, skillId: string, request: SetAgentShadowRequest): Observable<AgentSkillState> {
    return this.request<AgentSkillState>(
      'POST',
      `admin/agents/${encodeURIComponent(agentId)}/skills/${encodeURIComponent(skillId)}/shadow`,
      request);
  }

  /**
   * Changes the technical account that answers for the agent's new runs, or with `actorId: null`
   * uses the account in code again. Fails with `assistant-service-account-not-found` for an
   * account that does not exist or may not sign in, and with `assistant-service-account-not-permitted`
   * for an account that would lend the agent rights the administrator does not hold.
   */
  setServiceAccount(agentId: string, request: SetAgentServiceAccountRequest): Observable<AgentState> {
    return this.request<AgentState>('POST', `admin/agents/${encodeURIComponent(agentId)}/service-account`, request);
  }

  /**
   * Switches the routes of the agent's consultations on, off or back to the setting in code; a route
   * left out keeps its switch. Code is the ceiling: switching on a route that no consultation in code
   * allows fails with `assistant-consult-route-not-allowed`. Returns the agent's state with its
   * `consultationRoutes`.
   */
  setConsultationRoutes(agentId: string, request: SetAgentConsultationRoutesRequest): Observable<AgentState> {
    return this.request<AgentState>('POST', `admin/agents/${encodeURIComponent(agentId)}/consultation-routes`, request);
  }

  /**
   * Puts an agent, in code or created here, in exactly `groups` of the run catalog's `agentGroups`
   * and out of the others. The choice wins over the agent's `InGroup` in code. Fails with a field error
   * on `groups` for an unknown group and with `assistant-shadow-variant` for a shadow variant.
   */
  setAgentGroups(agentId: string, groups: readonly string[]): Observable<AdminAgentGroups> {
    return this.request<AdminAgentGroups>('PUT', `admin/agents/${encodeURIComponent(agentId)}/groups`, { groups });
  }

  /**
   * Every entry of agent metadata the application registers, with the agent's value in effect, where
   * it comes from and whether it changes here (`GET admin/agents/{id}/metadata`).
   */
  getAgentMetadata(agentId: string): Observable<AgentMetadata[]> {
    return this.request<AgentMetadata[]>('GET', `${agentPath(agentId)}/metadata`);
  }

  /**
   * Saves the agent's value of one metadata entry after the checks in code: field errors `value` or
   * `value.<field>`. For an agent created here it makes the agent's next version (`expectedVersion` is
   * the agent's version); for an agent in code only an editable entry changes, as an override with a
   * version of its own (`assistant-metadata-read-only` otherwise).
   */
  saveAgentMetadata(agentId: string, name: string, request: AgentMetadataRequest): Observable<AgentMetadata> {
    return this.request<AgentMetadata>('PUT', metadataPath(agentId, name), request);
  }

  /**
   * Removes the agent's stored value of one metadata entry, so an agent in code uses its value in code
   * again; answers the entry as it is now.
   */
  removeAgentMetadata(agentId: string, name: string, expectedVersion: number | null): Observable<AgentMetadata> {
    return this.request<AgentMetadata>('DELETE', withQuery(metadataPath(agentId, name), { expectedVersion }));
  }

  /** Deletes an admin agent. Code agents cannot be deleted; stop them instead. */
  deleteAgent(agentId: string): Observable<void> {
    return this.request<void>('DELETE', `admin/agents/${encodeURIComponent(agentId)}`);
  }

  getMcpServers(): Observable<McpServer[]> {
    return this.request<McpServer[]>('GET', 'admin/mcp-servers');
  }

  /**
   * The authentication modes the application registered in code (`GET admin/mcp-auth-modes`), which
   * administrators choose next to the built-in modes.
   */
  getMcpAuthModes(): Observable<McpCustomAuthMode[]> {
    return this.request<McpCustomAuthMode[]>('GET', 'admin/mcp-auth-modes');
  }

  createMcpServer(input: McpServerInput): Observable<McpServer> {
    return this.request<McpServer>('POST', 'admin/mcp-servers', input);
  }

  /** Omit `secret` to keep the stored secret; send `""` to clear it. */
  updateMcpServer(serverId: string, input: McpServerInput): Observable<McpServer> {
    return this.request<McpServer>('PUT', `admin/mcp-servers/${encodeURIComponent(serverId)}`, input);
  }

  deleteMcpServer(serverId: string): Observable<void> {
    return this.request<void>('DELETE', `admin/mcp-servers/${encodeURIComponent(serverId)}`);
  }

  testMcpServer(serverId: string): Observable<McpServerTestResult> {
    return this.request<McpServerTestResult>('POST', `admin/mcp-servers/${encodeURIComponent(serverId)}/test`);
  }

  /** Reads the remote tool list. New tools start disabled; a changed input schema disables a tool. */
  syncMcpServer(serverId: string): Observable<McpTool[]> {
    return this.request<McpTool[]>('POST', `admin/mcp-servers/${encodeURIComponent(serverId)}/sync`);
  }

  getMcpTools(serverId: string): Observable<McpTool[]> {
    return this.request<McpTool[]>('GET', `admin/mcp-servers/${encodeURIComponent(serverId)}/tools`);
  }

  /**
   * Reads the protected resource metadata of a server address and its authorization server without
   * storing anything. Save the server with `oauth.confirmAuthorization` to pin what was found.
   */
  discoverMcpServer(url: string): Observable<McpDiscovery> {
    return this.request<McpDiscovery>('POST', 'admin/mcp-servers/discover', { url });
  }

  /** The organization's OAuth connection of a server, without tokens. */
  getMcpOrganizationConnection(serverId: string): Observable<McpOrganizationConnection> {
    return this.request<McpOrganizationConnection>('GET', `${mcpServerPath(serverId)}/connection`);
  }

  /**
   * Starts the administrator's authorization of the organization connection. Open
   * `authorizationUrl` in the administrator's browser, then complete with `completeMcpAuthorization`.
   */
  authorizeMcpOrganizationConnection(serverId: string, request: McpAuthorizeRequest = {}): Observable<McpAuthorizationStart> {
    return this.request<McpAuthorizationStart>('POST', `${mcpServerPath(serverId)}/connection/authorize`, request);
  }

  /** Removes the organization connection and revokes its tokens. */
  disconnectMcpOrganizationConnection(serverId: string): Observable<void> {
    return this.request<void>('DELETE', `${mcpServerPath(serverId)}/connection`);
  }

  /** The personal connections of a server per status. */
  getMcpConnectionsSummary(serverId: string): Observable<McpConnectionsSummary> {
    return this.request<McpConnectionsSummary>('GET', `${mcpServerPath(serverId)}/connections/summary`);
  }

  /** Revokes every person's connection of a server; each person connects again. */
  revokeMcpConnections(serverId: string): Observable<void> {
    return this.request<void>('DELETE', `${mcpServerPath(serverId)}/connections`);
  }

  /**
   * Activates the connection of an authorization the caller started with the completion code the
   * callback handed to the browser. An organization connection also needs the admin policy. The same
   * call as `NhAssistantApiService.completeMcpAuthorization`, with the same failure codes.
   */
  completeMcpAuthorization(request: McpCompleteRequest): Observable<McpCompletion> {
    return this.api.completeMcpAuthorization(request);
  }

  /**
   * Enables or disables a tool, sets its effect and its description. A remote name that no sync has
   * seen yet stores these reviewed settings as a tool that awaits its sync (`awaitingSync`).
   */
  updateMcpTool(serverId: string, remoteName: string, request: UpdateMcpToolRequest): Observable<McpTool> {
    const path = `admin/mcp-servers/${encodeURIComponent(serverId)}/tools/${encodeURIComponent(remoteName)}`;
    return this.request<McpTool>('PUT', path, request);
  }

  /**
   * Assigns a server to an agent (`PUT admin/mcp-servers/{id}/agents/{agentId}`). Only the assignment
   * changes, never the agent's definition or version; assigning an assigned server changes nothing.
   * Answers the server with its `assignedAgentIds`.
   */
  assignMcpServer(serverId: string, agentId: string): Observable<McpServer> {
    return this.request<McpServer>('PUT', `${mcpServerPath(serverId)}/agents/${encodeURIComponent(agentId)}`);
  }

  /**
   * Removes a server's assignment to an agent (`DELETE admin/mcp-servers/{id}/agents/{agentId}`); the
   * agent's definition and version stay. Answers the server with its `assignedAgentIds`.
   */
  unassignMcpServer(serverId: string, agentId: string): Observable<McpServer> {
    return this.request<McpServer>('DELETE', `${mcpServerPath(serverId)}/agents/${encodeURIComponent(agentId)}`);
  }

  /**
   * The agent's proposals per tool and agent version in the period: decisions, the rejection rate,
   * feedback per category and the number of different people who gave it.
   */
  getAgentFeedbackSummary(agentId: string, filter: AgentFeedbackFilter = {}): Observable<AgentFeedbackSummary> {
    return this.request<AgentFeedbackSummary>('GET', withQuery(`${feedbackPath(agentId)}/summary`, filter));
  }

  /** The feedback on the agent's proposals with the proposal as approvers read it, newest change first. */
  getAgentFeedback(agentId: string, filter: AgentFeedbackListFilter = {}): Observable<AgentFeedbackList> {
    return this.request<AgentFeedbackList>('GET', withQuery(feedbackPath(agentId), filter));
  }

  /**
   * Downloads the feedback as JSON Lines with the tool input of each proposal. The file holds
   * subject data and the server audits the export. Rejects with `NhAssistantApiError`.
   */
  exportAgentFeedback(agentId: string, filter: AgentFeedbackExportFilter = {}): Promise<Blob> {
    return this.download(withQuery(`${feedbackPath(agentId)}/export`, filter));
  }

  /**
   * Compares a shadow variant with its live agent over a period: the runs that handled the same
   * events, the proposals per tool with what people decided about the live ones, cost and routing.
   */
  getAgentEvaluation(agentId: string, filter: AgentEvaluationFilter): Observable<AgentEvaluation> {
    return this.request<AgentEvaluation>('GET', withQuery(`${agentPath(agentId)}/evaluation`, filter));
  }

  /**
   * Downloads the paired runs of the comparison as JSON Lines: identifiers, statuses, usage and
   * proposal identities, no content. The server audits the export. Rejects with `NhAssistantApiError`.
   */
  exportAgentEvaluation(agentId: string, filter: AgentEvaluationFilter): Promise<Blob> {
    return this.download(withQuery(`${agentPath(agentId)}/evaluation/export`, filter));
  }

  /**
   * Replays the recorded runs of another agent in a period into a shadow variant, against today's
   * state of each subject. One replay runs per variant (`assistant-replay-active`).
   */
  startAgentReplay(variantId: string, request: AgentReplayRequest): Observable<AgentReplay> {
    return this.request<AgentReplay>('POST', `${agentPath(variantId)}/replay`, request);
  }

  /** The latest replays into a shadow variant, newest first. */
  getAgentReplays(variantId: string): Observable<AgentReplayList> {
    return this.request<AgentReplayList>('GET', `${agentPath(variantId)}/replays`);
  }

  /** The building blocks in code that run settings, skills and triggers defined as data can name. */
  getRunCatalog(): Observable<RunCatalog> {
    return this.request<RunCatalog>('GET', 'admin/run-catalog');
  }

  /**
   * The application's assets an administrator may choose as instructions, without their content
   * (`GET admin/instruction-assets`). Fails with `assistant-instructions-asset-unavailable` when the
   * application's source fails.
   */
  listInstructionAssets(): Observable<InstructionAssetList> {
    return this.request<InstructionAssetList>('GET', 'admin/instruction-assets');
  }

  /**
   * The text of one asset, read-only, with the hash a reference stores. Fails with
   * `assistant-instructions-asset-not-found` for an asset or version the application does not have.
   */
  getInstructionAsset(assetId: string, version: number): Observable<InstructionAssetText> {
    return this.request<InstructionAssetText>(
      'GET',
      `admin/instruction-assets/${encodeURIComponent(assetId)}/versions/${encodeURIComponent(String(version))}`
    );
  }

  /**
   * Accounts whose name or e-mail address contains `query` (2 to 100 characters), at most `limit`.
   * Fails with `assistant-account-search-unavailable` when the application offers no search; the
   * run catalog's `accountSearch` tells beforehand.
   */
  searchAccounts(query: string, limit?: number): Observable<AssistantAccount[]> {
    return this.request<AssistantAccount[]>('GET', withQuery('admin/accounts', { query, limit }));
  }

  /**
   * The names the application knows for these account ids; unknown ids are left out. The server reads
   * at most 50 ids per request, so more ids are asked for in parts of 50 and the answers merged in
   * the order of the ids. Fails with `NhAssistantApiError` when any part fails.
   */
  getAccounts(actorIds: readonly string[]): Observable<AssistantAccount[]> {
    // An empty id or one longer than the server reads names no account.
    const ids = [...new Set(actorIds.filter(id => id.trim().length > 0 && id.length <= maxAccountIdLength))];
    if (ids.length === 0) {
      return of([]);
    }

    const parts: Observable<AssistantAccount[]>[] = [];
    for (let start = 0; start < ids.length; start += maxAccountIds) {
      const part = ids.slice(start, start + maxAccountIds);
      parts.push(this.request<AssistantAccount[]>('GET', withQuery('admin/accounts', { ids: part.join(',') })));
    }

    return forkJoin(parts).pipe(map(answers => answers.flat()));
  }

  /**
   * The agent's run settings, skills and triggers, in code and as data, and the data that no longer
   * fits the code. An agent in code without a run configuration answers `assistant-agent-not-found`.
   */
  getAgentConfiguration(agentId: string): Observable<AgentRunConfiguration> {
    return this.request<AgentRunConfiguration>('GET', `${agentPath(agentId)}/configuration`);
  }

  /**
   * Adds or changes the run settings of an agent created in the administration. An agent in code
   * fails with `assistant-run-settings-in-code`; an unknown technical account with
   * `assistant-service-account-not-found`, and one that would lend the agent rights the
   * administrator does not hold with `assistant-service-account-not-permitted`.
   */
  saveRunSettings(agentId: string, request: AgentRunSettingsRequest): Observable<AgentRunSettings> {
    return this.request<AgentRunSettings>('PUT', `${agentPath(agentId)}/run-settings`, request);
  }

  /**
   * Every run of the agent, live and shadow, newest first, for its runs view. Read one run's whole
   * result with `NhAssistantAgentRunApiService.getRunResult`. The server checks the filter first:
   * `assistant-run-filter-invalid` with the refused parts in `fieldErrors`, then
   * `assistant-agent-not-found` for an unknown agent.
   */
  getAgentRuns(agentId: string, filter: Omit<AgentRunFilter, 'agentId'> = {}): Observable<AgentRunHistory> {
    return this.request<AgentRunHistory>('GET', withQuery(`${agentPath(agentId)}/runs`, filter));
  }

  /**
   * Removes the run settings; while data skills, triggers, consultations or hand-offs defined as data
   * belong to the agent, `assistant-run-settings-in-use`.
   */
  removeRunSettings(agentId: string, removal: RunConfigurationRemoval = {}): Observable<void> {
    return this.request<void>('DELETE', withQuery(`${agentPath(agentId)}/run-settings`, removal));
  }

  /**
   * Adds a skill defined as data, which starts in shadow, or stores its next version: with `apply`
   * `live` at once, with `shadow` as the candidate that runs in shadow next to the live version.
   */
  saveDataSkill(agentId: string, skillId: string, request: DataSkillRequest): Observable<DataSkill> {
    return this.request<DataSkill>('PUT', skillPath(agentId, skillId), request);
  }

  /** Takes a skill defined as data out of effect; while a data trigger names it, `assistant-skill-in-use`. */
  removeDataSkill(agentId: string, skillId: string, removal: RunConfigurationRemoval = {}): Observable<void> {
    return this.request<void>('DELETE', withQuery(skillPath(agentId, skillId), removal));
  }

  /** Every version of a skill defined as data, newest first. */
  getDataSkillVersions(agentId: string, skillId: string): Observable<DataSkillVersion[]> {
    return this.request<DataSkillVersion[]>('GET', `${skillPath(agentId, skillId)}/versions`);
  }

  /** Makes the candidate the live version of its skill. */
  promoteSkillCandidate(agentId: string, skillId: string, request: SkillCandidateRequest): Observable<DataSkill> {
    return this.request<DataSkill>('POST', `${skillPath(agentId, skillId)}/candidate/promote`, request);
  }

  /** Discards the candidate; it stays as history. */
  discardSkillCandidate(agentId: string, skillId: string, request: SkillCandidateRequest): Observable<void> {
    return this.request<void>('POST', `${skillPath(agentId, skillId)}/candidate/discard`, request);
  }

  /** Compares the candidate with the live version on the runs they made in the period. */
  compareSkillCandidate(agentId: string, skillId: string, filter: SkillComparisonFilter = {}): Observable<SkillComparison> {
    return this.request<SkillComparison>('GET', withQuery(`${skillPath(agentId, skillId)}/candidate/comparison`, filter));
  }

  /**
   * Adds a trigger defined as data, which starts in shadow, or stores its next version: with `apply`
   * `live` (default) it takes effect at once in the trigger's mode; with `shadow` it becomes the
   * candidate that fires next to the live version until it is promoted or discarded.
   */
  saveDataTrigger(agentId: string, triggerId: string, request: DataTriggerRequest): Observable<DataTrigger> {
    return this.request<DataTrigger>('PUT', triggerPath(agentId, triggerId), request);
  }

  /** Every version of a trigger defined as data, newest first, with its definition. */
  getDataTriggerVersions(agentId: string, triggerId: string): Observable<DataTriggerVersion[]> {
    return this.request<DataTriggerVersion[]>('GET', `${triggerPath(agentId, triggerId)}/versions`);
  }

  /** Makes the candidate the live version of its trigger, in the trigger's mode. */
  promoteTriggerCandidate(agentId: string, triggerId: string, request: TriggerCandidateRequest): Observable<DataTrigger> {
    return this.request<DataTrigger>('POST', `${triggerPath(agentId, triggerId)}/candidate/promote`, request);
  }

  /** Discards the candidate of a trigger; it stays as history and fires no more. */
  discardTriggerCandidate(agentId: string, triggerId: string, request: TriggerCandidateRequest): Observable<void> {
    return this.request<void>('POST', `${triggerPath(agentId, triggerId)}/candidate/discard`, request);
  }

  /** Compares the trigger's candidate with its live version on the runs they started in the period. */
  compareTriggerCandidate(agentId: string, triggerId: string, filter: SkillComparisonFilter = {}): Observable<TriggerComparison> {
    return this.request<TriggerComparison>('GET', withQuery(`${triggerPath(agentId, triggerId)}/candidate/comparison`, filter));
  }

  removeDataTrigger(agentId: string, triggerId: string, removal: RunConfigurationRemoval = {}): Observable<void> {
    return this.request<void>('DELETE', withQuery(triggerPath(agentId, triggerId), removal));
  }

  /** Puts a trigger defined as data in shadow or live. */
  setDataTriggerShadow(agentId: string, triggerId: string, request: DataTriggerShadowRequest): Observable<DataTrigger> {
    return this.request<DataTrigger>('POST', `${triggerPath(agentId, triggerId)}/shadow`, request);
  }

  /**
   * Adds or changes the consultation defined as data of an agent about one subject type, within the
   * agent's `consultationCeiling`. It is in effect once saved and answers when an asking agent selects
   * its consult tool. An agent that may get none fails with `assistant-consultations-not-allowed`, an
   * agent created here without run settings with `assistant-agent-not-configured`, a type the agent
   * allows consultation about in code with `assistant-consultation-in-code`, more than the ceiling's
   * `maxConsultations` with `assistant-configuration-limit`, a stale `expectedVersion` with
   * `assistant-version-conflict` and invalid input with field errors, such as `routes: not-allowed` for
   * a route outside the ceiling.
   */
  saveDataConsultation(agentId: string, subjectType: string, request: DataConsultationRequest): Observable<DataConsultation> {
    return this.request<DataConsultation>('PUT', consultationPath(agentId, subjectType), request);
  }

  /**
   * Switches a consultation defined as data on or off, without a new version. Off, it is not offered,
   * while its recorded proposals can still be decided. Fails with `assistant-consultation-not-found`
   * without one and with `assistant-consultation-in-code` for a consultation in code.
   */
  setDataConsultationState(agentId: string, subjectType: string, request: DataConsultationStateRequest): Observable<DataConsultation> {
    return this.request<DataConsultation>('POST', `${consultationPath(agentId, subjectType)}/state`, request);
  }

  /**
   * Removes a consultation defined as data: its tools disappear, while its recorded proposals can still
   * be decided. Fails with `assistant-consultation-not-found` without one and with
   * `assistant-consultation-in-code` for a consultation in code.
   */
  removeDataConsultation(agentId: string, subjectType: string, removal: RunConfigurationRemoval = {}): Observable<void> {
    return this.request<void>('DELETE', withQuery(consultationPath(agentId, subjectType), removal));
  }

  /**
   * The hand-offs the agent accepts, in code and as data with their problems, and what the ceiling in
   * code (`UseHandOffsAsData`) lets administrators add; `ceiling` is `null` when it may accept none.
   */
  getAgentHandOffs(agentId: string): Observable<AgentHandOffs> {
    return this.request<AgentHandOffs>('GET', `${agentPath(agentId)}/hand-offs`);
  }

  /**
   * Adds or changes the hand-off defined as data that the agent accepts about one subject type, within
   * the ceiling: in effect once saved, as one in code. An agent the ceiling connects to no agent fails
   * with `assistant-handoffs-not-allowed`, a type it accepts in code with `assistant-handoff-in-code`,
   * an agent without run configuration with `assistant-agent-not-configured`, more than the ceiling's
   * `maxHandOffs` with `assistant-configuration-limit`, a stale `expectedVersion` with
   * `assistant-version-conflict` and invalid input with field errors, such as `sources: not-allowed`
   * for a source outside the ceiling.
   */
  saveDataHandOff(agentId: string, subjectType: string, request: DataHandOffRequest): Observable<DataHandOff> {
    return this.request<DataHandOff>('PUT', handOffPath(agentId, subjectType), request);
  }

  /**
   * Removes a hand-off defined as data; its tool disappears from the sources' next turns. Fails with
   * `assistant-handoff-not-found` without one and with `assistant-handoff-in-code` for one in code.
   */
  removeDataHandOff(agentId: string, subjectType: string, removal: RunConfigurationRemoval = {}): Observable<void> {
    return this.request<void>('DELETE', withQuery(handOffPath(agentId, subjectType), removal));
  }

  /**
   * The agent's shadow variants, in code and created as data, and whether administrators may create
   * more (`UseShadowVariantsAsData`). Create a data variant with `createAgent` and `shadowOf`.
   */
  getShadowVariants(agentId: string): Observable<AgentShadowVariants> {
    return this.request<AgentShadowVariants>('GET', `${agentPath(agentId)}/shadow-variants`);
  }

  /**
   * Promotes the data shadow variant `variantId` onto its live agent (`POST admin/agents/{id}/promote`):
   * its definition becomes the live agent's next version through the update path, with its checks,
   * locks and audit, its data skills move along when asked, and the variant is deleted. The live agent
   * keeps its name, MCP servers, metadata, run settings, triggers, state and shadow mode; an agent in
   * code takes only the fields it can override. Fails with `assistant-not-shadow-variant`,
   * `assistant-shadow-variants-not-allowed`, `assistant-version-conflict` and `assistant-skill-in-code`.
   * With `asPackageVersion` and `uncarriedParts: 'relink'` the live agent's linked parts the variant
   * does not carry stay linked to the package version with their locks, reported as `kept-linked`.
   */
  promoteShadowVariant(variantId: string, request: ShadowVariantPromotionRequest): Observable<ShadowVariantPromotion> {
    return this.request<ShadowVariantPromotion>('POST', `${agentPath(variantId)}/promote`, request);
  }

  /**
   * Exports exactly `agentIds` (1 to 20) as one agent package (`POST admin/agent-packages/export`):
   * agents created here whole, agents in code as an overlay of what data changes on them; never
   * history, candidates, runs, run state, shadow overrides, MCP servers, the technical account or
   * secrets. The options set the package id, version and locks; left out, the id of the package the
   * first agent is linked to and its next version, otherwise the first agent's id and 1. Answers the
   * package, its JSON exactly as the server wrote it for the download, and every reference to an agent
   * outside the selection, which must exist where the package is imported. Fails with
   * `assistant-agent-not-found` for an unknown agent and with field errors for invalid options; the
   * server audits the export.
   */
  exportAgentPackage(agentIds: readonly string[], options: AgentPackageExportOptions = {}): Observable<AgentPackageExport> {
    return this.transport.file('POST', 'admin/agent-packages/export', { agentIds: [...agentIds], options }, 'application/json', adminStatusCodes).pipe(
      map(file => {
        let agentPackage: AgentPackage;
        try {
          agentPackage = JSON.parse(file.text) as AgentPackage;
        } catch {
          throw new NhAssistantApiError(200, NhAssistantClientErrorCodes.invalidResponse, nhAssistantErrorMessageKey(NhAssistantClientErrorCodes.invalidResponse));
        }

        return { package: agentPackage, json: file.text, warnings: packageWarnings(file.headers.get(packageWarningsHeader)) };
      }));
  }

  /**
   * What importing the package would do, per agent and part (`POST admin/agent-packages/preview`):
   * `created`, `changed`, `unchanged`, `kept-local`, `left-behind` or `refused` with the field
   * errors of the save validators, after one check of the whole package against this host as it would
   * be after the import. Changes nothing. The server reads the package strictly: a package it does not
   * read fails with `assistant-package-format-unsupported`, one above 1 MB or 20 agents with
   * `assistant-package-too-large`.
   */
  previewAgentPackageImport(agentPackage: AgentPackage, options: AgentPackageImportOptions = {}): Observable<AgentPackagePreview> {
    return this.request<AgentPackagePreview>('POST', 'admin/agent-packages/preview', { package: agentPackage, options });
  }

  /**
   * Imports the package in one transaction (`POST admin/agent-packages/import`): every agent and part
   * of it, or nothing. Fails as `previewAgentPackageImport` does, and with `assistant-package-refused`
   * and the field error `parts: invalid` when the check refuses a part; preview again to see which part
   * and why. Every part is audited as its save.
   */
  importAgentPackage(agentPackage: AgentPackage, options: AgentPackageImportOptions = {}): Observable<AgentPackageImport> {
    return this.request<AgentPackageImport>('POST', 'admin/agent-packages/import', { package: agentPackage, options });
  }

  /**
   * Unlinks one part of the agent, or without `part` the whole agent, from its package
   * (`POST admin/agents/{id}/package/unlink`): its locks no longer hold and the next upgrade keeps it as
   * `kept-local` unless the import relinks it. Fails with `assistant-package-not-linked` for an agent
   * or part without a link and with the field error `part: invalid` for an unknown part key.
   */
  unlinkAgentPackage(agentId: string, request: AgentPackageUnlinkRequest = {}): Observable<AgentPackageUnlink> {
    return this.request<AgentPackageUnlink>('POST', `${agentPath(agentId)}/package/unlink`, request);
  }

  /**
   * Exactly `agentIds` (1 to 20), in the same order, as an export would put them in a package
   * (`POST admin/agent-packages/read`): every part and its SHA-256 per part key in `hashes`, without
   * locks. Compare them with a shipped package's hashes, or with the hashes of the parts' links of
   * `getInstalledAgentPackage`, to see which parts this host has otherwise. Nothing is exported, audited
   * or changed. Fails with the field error `agentIds` (`required`, `too-long`, `invalid` for a malformed
   * id or one named twice) and with `assistant-agent-not-found`.
   */
  readAgentPackageParts(agentIds: readonly string[]): Observable<AgentPackageParts> {
    const request: AgentPackageReadRequest = { agentIds: [...agentIds] };
    return this.request<AgentPackageParts>('POST', 'admin/agent-packages/read', request);
  }

  /**
   * The package this host imported as `packageId` (`GET admin/agent-packages/{packageId}`): its
   * version, format version and hash, its agents, when and by whom it was imported, and the current link
   * of every part still linked to it, each with whether it changed locally. It stays known after its
   * agents were unlinked. Reads only, without an audit. Fails with `assistant-package-not-found` for a
   * package this host never imported.
   */
  getInstalledAgentPackage(packageId: string): Observable<InstalledAgentPackage> {
    return this.request<InstalledAgentPackage>('GET', `admin/agent-packages/${encodeURIComponent(packageId)}`);
  }

  private download(path: string): Promise<Blob> {
    return this.transport.download(path, 'application/x-ndjson', adminStatusCodes);
  }

  private request<T>(method: string, path: string, body?: unknown): Observable<T> {
    return this.transport.json<T>(method, path, body, adminStatusCodes);
  }
}

function mcpServerPath(serverId: string): string {
  return `admin/mcp-servers/${encodeURIComponent(serverId)}`;
}

function feedbackPath(agentId: string): string {
  return `${agentPath(agentId)}/feedback`;
}

function agentPath(agentId: string): string {
  return `admin/agents/${encodeURIComponent(agentId)}`;
}

function skillPath(agentId: string, skillId: string): string {
  return `${agentPath(agentId)}/skills/${encodeURIComponent(skillId)}`;
}

function triggerPath(agentId: string, triggerId: string): string {
  return `${agentPath(agentId)}/triggers/${encodeURIComponent(triggerId)}`;
}

function consultationPath(agentId: string, subjectType: string): string {
  return `${agentPath(agentId)}/consultations/${encodeURIComponent(subjectType)}`;
}

function handOffPath(agentId: string, subjectType: string): string {
  return `${agentPath(agentId)}/hand-offs/${encodeURIComponent(subjectType)}`;
}

function metadataPath(agentId: string, name: string): string {
  return `${agentPath(agentId)}/metadata/${encodeURIComponent(name)}`;
}

/**
 * The references to agents outside an exported selection, from the export's header: a JSON array,
 * of which only entries with a safe code and string ids are kept. Without the header, none.
 */
function packageWarnings(header: string | null): AgentPackageWarning[] {
  if (!header) {
    return [];
  }

  let items: unknown;
  try {
    items = JSON.parse(header);
  } catch {
    return [];
  }
  if (!Array.isArray(items)) {
    return [];
  }

  const warnings: AgentPackageWarning[] = [];
  for (const item of items) {
    const record = item && typeof item === 'object' ? item as Record<string, unknown> : null;
    if (record
      && typeof record['agentId'] === 'string'
      && typeof record['part'] === 'string'
      && typeof record['referencedAgentId'] === 'string'
      && nhAssistantIsSafeCode(record['code'])) {
      warnings.push({ agentId: record['agentId'], part: record['part'], code: record['code'], referencedAgentId: record['referencedAgentId'] });
    }
  }
  return warnings;
}

/** Appends the filter values that are set as query parameters. */
function withQuery(path: string, filter: object): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(filter)) {
    if (value !== undefined && value !== null && value !== '') {
      query.set(key, String(value));
    }
  }

  const suffix = query.toString();
  return suffix ? `${path}?${suffix}` : path;
}
