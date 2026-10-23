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
  DataTrigger,
  DataTriggerRequest,
  DataTriggerShadowRequest,
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
  UpdateAdminAgentRequest,
  UpdateApplicationContextRequest,
  UpdateMcpToolRequest
} from '../models/assistant-admin.models';
import { AgentRunFilter, AgentRunHistory } from '../models/assistant-agent-run.models';
import { AgentHandOffs, AgentShadowVariants, DataHandOff, DataHandOffRequest } from '../models/assistant-hand-off.models';
import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import { NhAssistantApiService } from './nh-assistant-api.service';
import { NhAssistantStatusCodes, NhAssistantTransport } from './nh-assistant-transport';

/** In the admin endpoints a 409 means a stale `expectedVersion` or a forbidden state change. */
const adminStatusCodes: NhAssistantStatusCodes = {
  409: NhAssistantClientErrorCodes.versionConflict
};

/** The most account ids one `GET admin/accounts?ids=` reads; `getAccounts` asks for more in parts. */
const maxAccountIds = 50;

/** The longest account id the server reads; a longer id names no account. */
const maxAccountIdLength = 256;

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
   * account that does not exist or may not sign in.
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

  updateMcpTool(serverId: string, remoteName: string, request: UpdateMcpToolRequest): Observable<McpTool> {
    const path = `admin/mcp-servers/${encodeURIComponent(serverId)}/tools/${encodeURIComponent(remoteName)}`;
    return this.request<McpTool>('PUT', path, request);
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
   * `assistant-service-account-not-found`.
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

  /** Removes the run settings; while data skills or triggers belong to the agent, `assistant-run-settings-in-use`. */
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

  /** Adds a trigger defined as data, which starts in shadow, or changes one in the mode it has. */
  saveDataTrigger(agentId: string, triggerId: string, request: DataTriggerRequest): Observable<DataTrigger> {
    return this.request<DataTrigger>('PUT', triggerPath(agentId, triggerId), request);
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
