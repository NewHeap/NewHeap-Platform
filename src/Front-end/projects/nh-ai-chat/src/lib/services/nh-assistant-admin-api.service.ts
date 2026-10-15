import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AdminAgent,
  AdminAgentInput,
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
  ApplicationContextVersion,
  McpServer,
  McpServerInput,
  McpServerTestResult,
  McpTool,
  SetAgentStateRequest,
  ToolCatalogEntry,
  UpdateAdminAgentRequest,
  UpdateApplicationContextRequest,
  UpdateMcpToolRequest
} from '../models/assistant-admin.models';
import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import {
  NhAssistantApiError,
  NhAssistantStatusCodes,
  NhAssistantTransport,
  nhAssistantCodeForStatus,
  nhAssistantErrorMessageKey
} from './nh-assistant-transport';

/** In the admin endpoints a 409 means a stale `expectedVersion` or a forbidden state change. */
const adminStatusCodes: NhAssistantStatusCodes = {
  409: NhAssistantClientErrorCodes.versionConflict
};

/**
 * Client for the assistant administration endpoints (`admin/*`). The server requires the
 * access policy and the admin policy; show the administration only when
 * `NhAssistantStore.canAdminister()` is true. Failures error with `NhAssistantApiError`.
 * Secrets are write-only: `McpServer` only reports `hasSecret`.
 */
@Injectable()
export class NhAssistantAdminApiService {
  private readonly transport = inject(NhAssistantTransport);

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

  /** Deletes an admin agent. Code agents cannot be deleted; stop them instead. */
  deleteAgent(agentId: string): Observable<void> {
    return this.request<void>('DELETE', `admin/agents/${encodeURIComponent(agentId)}`);
  }

  getMcpServers(): Observable<McpServer[]> {
    return this.request<McpServer[]>('GET', 'admin/mcp-servers');
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

  private async download(path: string): Promise<Blob> {
    let response: Response;
    try {
      response = await this.transport.fetch('GET', path, 'application/x-ndjson', undefined, new AbortController().signal);
    } catch {
      const code = nhAssistantCodeForStatus(0);
      throw new NhAssistantApiError(0, code, nhAssistantErrorMessageKey(code));
    }

    if (!response.ok) {
      const code = await this.transport.failureCode(response, adminStatusCodes);
      throw new NhAssistantApiError(response.status, code, nhAssistantErrorMessageKey(code));
    }
    return response.blob();
  }

  private request<T>(method: string, path: string, body?: unknown): Observable<T> {
    return this.transport.json<T>(method, path, body, adminStatusCodes);
  }
}

function feedbackPath(agentId: string): string {
  return `${agentPath(agentId)}/feedback`;
}

function agentPath(agentId: string): string {
  return `admin/agents/${encodeURIComponent(agentId)}`;
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
