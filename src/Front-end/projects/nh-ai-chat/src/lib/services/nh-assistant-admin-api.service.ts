import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AdminAgent,
  AdminAgentInput,
  ApplicationContext,
  ApplicationContextVersion,
  McpServer,
  McpServerInput,
  McpServerTestResult,
  McpTool,
  ToolCatalogEntry,
  UpdateAdminAgentRequest,
  UpdateApplicationContextRequest,
  UpdateMcpToolRequest
} from '../models/assistant-admin.models';
import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import { NhAssistantStatusCodes, NhAssistantTransport } from './nh-assistant-transport';

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

  /** Restores a code agent to its definition in code. */
  resetAgent(agentId: string): Observable<AdminAgent> {
    return this.request<AdminAgent>('POST', `admin/agents/${encodeURIComponent(agentId)}/reset`);
  }

  /** Deletes an admin agent. Code agents cannot be deleted; disable them instead. */
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

  private request<T>(method: string, path: string, body?: unknown): Observable<T> {
    return this.transport.json<T>(method, path, body, adminStatusCodes);
  }
}
