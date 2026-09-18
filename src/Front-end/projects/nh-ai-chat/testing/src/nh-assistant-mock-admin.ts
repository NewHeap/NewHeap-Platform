import {
  AdminAgent,
  AdminAgentInput,
  AgentSummary,
  ApplicationContext,
  ApplicationContextVersion,
  AssistantAutonomy,
  AssistantPreferences,
  McpServer,
  McpServerInput,
  McpServerTestResult,
  McpTool,
  McpToolEffect,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { NhAssistantMockRemoteTool, NhAssistantMockScenario } from './nh-assistant-mock.models';

/** A mock response: status plus optional JSON body. */
export interface NhAssistantMockResult {
  status: number;
  body?: unknown;
}

interface StoredServer {
  server: Omit<McpServer, 'hasSecret' | 'assignedAgentIds'>;
  secret: string | null;
  remoteTools: NhAssistantMockRemoteTool[];
  tools: Map<string, McpTool & { inputSchemaHash: string }>;
}

interface StoredAgent {
  agent: AdminAgent;
  /** The definition in code, for `reset`; null for admin agents. */
  codeDefinition: AdminAgentInput | null;
}

const dashCase = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const styles = ['default', 'direct', 'personal', 'detailed'];
const addressForms = ['informal', 'formal'];
const responseLengths = ['short', 'normal', 'long'];
const autonomyLevels: AssistantAutonomy[] = ['observe', 'explain', 'propose', 'simulate', 'execute'];
const authModes = ['none', 'bearer', 'api-key', 'forward-user-token'];

const maxInstructions = 20_000;
const maxCustomInstructions = 1_000;

const defaultPreferences: AssistantPreferences = {
  style: 'default',
  addressForm: 'informal',
  responseLength: 'normal',
  customInstructions: null
};

/**
 * In-memory administration and preference endpoints of the mock API. Follows the
 * contract rules: secrets are write-only, new MCP tools start disabled as mutations, a
 * changed remote schema disables a tool, and stale versions answer `409`.
 */
export class NhAssistantMockAdmin {
  private readonly agents = new Map<string, StoredAgent>();
  private readonly servers = new Map<string, StoredServer>();
  private readonly contextVersions: (ApplicationContextVersion & { text: string })[] = [];
  private preferences: AssistantPreferences;
  private canAdminister: boolean;

  constructor(private readonly scenario: NhAssistantMockScenario, private readonly now: () => string) {
    const admin = scenario.admin ?? {};
    this.canAdminister = admin.canAdminister ?? true;
    this.preferences = { ...defaultPreferences, ...scenario.preferences };

    this.contextVersions.push({ version: 1, hash: hashText(admin.context ?? ''), updatedAt: now(), updatedBy: null, text: admin.context ?? '' });

    const agents = admin.agents ?? scenario.agents.map(agent => ({ ...codeAgentFromSummary(agent), source: 'code' as const }));
    for (const { source, ...input } of agents) {
      this.agents.set(input.id, {
        agent: this.toAgent(input, source, 1, false),
        codeDefinition: source === 'code' ? structuredClone(input) : null
      });
    }

    for (const { remoteTools, secret, ...input } of admin.mcpServers ?? []) {
      this.servers.set(input.id, {
        server: { ...toServerFields(input), lastSyncAt: null, lastSyncStatus: null },
        secret: secret ?? null,
        remoteTools: remoteTools ?? [],
        tools: new Map()
      });
    }
  }

  get administers(): boolean {
    return this.canAdminister;
  }

  setCanAdminister(value: boolean): void {
    this.canAdminister = value;
  }

  /** Replaces the tool list of a simulated remote server; the next sync picks it up. */
  setRemoteTools(serverId: string, tools: NhAssistantMockRemoteTool[]): void {
    const stored = this.servers.get(serverId);
    if (stored) {
      stored.remoteTools = structuredClone(tools);
    }
  }

  /** Chat agents: enabled code agents keep their keys, admin agents carry literal text. */
  chatAgents(): AgentSummary[] {
    const result: AgentSummary[] = [];
    for (const { agent } of this.agents.values()) {
      if (!agent.isEnabled) {
        continue;
      }

      const summary = this.scenario.agents.find(item => item.id === agent.id);
      result.push(summary && agent.source === 'code'
        ? { ...summary, version: agent.version }
        : {
          id: agent.id,
          version: agent.version,
          displayNameKey: agent.displayName,
          descriptionKey: agent.description,
          canMutate: agent.autonomy === 'execute'
        });
    }
    return result;
  }

  handlePreferences(method: string, body: unknown): NhAssistantMockResult {
    if (method === 'GET') {
      return { status: 200, body: this.preferences };
    }
    if (method !== 'PUT') {
      return { status: 405 };
    }

    const input = body as Partial<AssistantPreferences> | undefined;
    const instructions = input?.customInstructions ?? null;
    if (!input || !styles.includes(input.style ?? '') || !addressForms.includes(input.addressForm ?? '') ||
      !responseLengths.includes(input.responseLength ?? '') ||
      (instructions !== null && (typeof instructions !== 'string' || instructions.length > maxCustomInstructions))) {
      return validationFailure();
    }

    this.preferences = {
      style: input.style!,
      addressForm: input.addressForm!,
      responseLength: input.responseLength!,
      customInstructions: instructions && instructions.trim().length > 0 ? instructions : null
    };
    return { status: 200, body: this.preferences };
  }

  handleAdmin(method: string, segments: string[], body: unknown): NhAssistantMockResult {
    if (!this.canAdminister) {
      return failure(403, 'assistant-forbidden');
    }

    const [, area, id, action, child] = segments;
    switch (area) {
      case 'context':
        return this.context(method, id, body);
      case 'tools':
        return method === 'GET' && segments.length === 2 ? { status: 200, body: this.toolCatalog() } : failure(404, 'assistant-not-found');
      case 'agents':
        return this.agentsEndpoint(method, id, action, body, segments.length);
      case 'mcp-servers':
        return this.serversEndpoint(method, id, action, child, body, segments.length);
      default:
        return failure(404, 'assistant-not-found');
    }
  }

  private context(method: string, sub: string | undefined, body: unknown): NhAssistantMockResult {
    const current = this.contextVersions[this.contextVersions.length - 1];
    if (sub === 'versions' && method === 'GET') {
      return { status: 200, body: [...this.contextVersions].reverse().map(({ text, ...version }) => version) };
    }
    if (sub !== undefined) {
      return failure(404, 'assistant-not-found');
    }
    if (method === 'GET') {
      return { status: 200, body: toContext(current) };
    }
    if (method !== 'PUT') {
      return { status: 405 };
    }

    const input = body as { text?: unknown; expectedVersion?: unknown } | undefined;
    if (typeof input?.text !== 'string' || typeof input.expectedVersion !== 'number') {
      return validationFailure();
    }
    if (input.text.length > maxInstructions) {
      return failure(400, 'assistant-instructions-too-long');
    }
    if (input.expectedVersion !== current.version) {
      return failure(409, 'assistant-version-conflict');
    }

    const next = { version: current.version + 1, hash: hashText(input.text), updatedAt: this.now(), updatedBy: 'current-user', text: input.text };
    this.contextVersions.push(next);
    return { status: 200, body: toContext(next) };
  }

  private toolCatalog(): ToolCatalogEntry[] {
    const catalog = [...(this.scenario.admin?.tools ?? [])];
    for (const stored of this.servers.values()) {
      for (const tool of stored.tools.values()) {
        if (tool.isEnabled) {
          catalog.push({ id: tool.localId, source: 'mcp', effect: tool.effect, description: tool.descriptionOverride ?? tool.description });
        }
      }
    }
    return catalog;
  }

  private agentsEndpoint(method: string, id: string | undefined, action: string | undefined, body: unknown, length: number): NhAssistantMockResult {
    if (id === undefined) {
      if (method === 'GET') {
        return { status: 200, body: [...this.agents.values()].map(stored => stored.agent) };
      }
      if (method !== 'POST') {
        return { status: 405 };
      }

      const input = body as AdminAgentInput;
      const invalid = this.agentFailure(input);
      if (invalid) {
        return invalid;
      }
      if (this.agents.has(input.id)) {
        return failure(409, 'assistant-agent-exists');
      }

      const agent = this.toAgent(input, 'admin', 1, false);
      this.agents.set(agent.id, { agent, codeDefinition: null });
      return { status: 201, body: agent };
    }

    const stored = this.agents.get(id);
    if (!stored) {
      return failure(404, 'assistant-not-found');
    }

    if (length === 4 && action === 'reset' && method === 'POST') {
      if (!stored.codeDefinition) {
        return failure(409, 'assistant-agent-not-code');
      }
      stored.agent = this.toAgent(stored.codeDefinition, 'code', stored.agent.version + 1, false);
      return { status: 200, body: stored.agent };
    }
    if (length !== 3) {
      return failure(404, 'assistant-not-found');
    }

    if (method === 'PUT') {
      const input = body as AdminAgentInput & { expectedVersion?: number };
      const invalid = input?.id !== id ? validationFailure() : this.agentFailure(input);
      if (invalid) {
        return invalid;
      }
      if (input.expectedVersion !== stored.agent.version) {
        return failure(409, 'assistant-version-conflict');
      }

      const { expectedVersion, ...definition } = input;
      stored.agent = this.toAgent(definition, stored.agent.source, stored.agent.version + 1, stored.agent.source === 'code');
      return { status: 200, body: stored.agent };
    }
    if (method === 'DELETE') {
      if (stored.agent.source === 'code') {
        return failure(409, 'assistant-code-agent-not-deletable');
      }
      this.agents.delete(id);
      return { status: 204 };
    }

    return { status: 405 };
  }

  private serversEndpoint(
    method: string,
    id: string | undefined,
    action: string | undefined,
    child: string | undefined,
    body: unknown,
    length: number
  ): NhAssistantMockResult {
    if (id === undefined) {
      if (method === 'GET') {
        return { status: 200, body: [...this.servers.values()].map(stored => this.toServer(stored)) };
      }
      if (method !== 'POST') {
        return { status: 405 };
      }

      const input = body as McpServerInput;
      if (!validServer(input)) {
        return validationFailure();
      }
      if (this.servers.has(input.id)) {
        return failure(409, 'assistant-mcp-server-exists');
      }

      const stored: StoredServer = {
        server: { ...toServerFields(input), lastSyncAt: null, lastSyncStatus: null },
        secret: input.secret ? input.secret : null,
        remoteTools: [],
        tools: new Map()
      };
      this.servers.set(input.id, stored);
      return { status: 201, body: this.toServer(stored) };
    }

    const stored = this.servers.get(id);
    if (!stored) {
      return failure(404, 'assistant-mcp-server-not-found');
    }

    if (length === 3) {
      if (method === 'PUT') {
        const input = body as McpServerInput;
        if (!validServer(input) || input.id !== id) {
          return validationFailure();
        }
        stored.server = { ...stored.server, ...toServerFields(input) };
        if (input.secret !== undefined) {
          stored.secret = input.secret ? input.secret : null;
        }
        return { status: 200, body: this.toServer(stored) };
      }
      if (method === 'DELETE') {
        this.servers.delete(id);
        for (const agent of this.agents.values()) {
          agent.agent = { ...agent.agent, mcpServerIds: agent.agent.mcpServerIds.filter(serverId => serverId !== id) };
        }
        return { status: 204 };
      }
      return { status: 405 };
    }

    if (length === 4 && action === 'test' && method === 'POST') {
      const code = this.connectionFailure(stored);
      const result: McpServerTestResult = code
        ? { ok: false, code, toolCount: null }
        : { ok: true, code: null, toolCount: stored.remoteTools.length };
      return { status: 200, body: result };
    }
    if (length === 4 && action === 'sync' && method === 'POST') {
      const code = this.connectionFailure(stored);
      stored.server = { ...stored.server, lastSyncAt: this.now(), lastSyncStatus: code ? 'failed' : 'ok' };
      if (code) {
        // A blocked host is a configuration error; connection failures are upstream errors.
        return failure(code === 'assistant-mcp-host-blocked' ? 400 : 502, code);
      }
      this.sync(stored);
      return { status: 200, body: toTools(stored) };
    }
    if (length === 4 && action === 'tools' && method === 'GET') {
      return { status: 200, body: toTools(stored) };
    }
    if (length === 5 && action === 'tools' && method === 'PUT' && child !== undefined) {
      const tool = stored.tools.get(child);
      const input = body as { isEnabled?: unknown; effect?: unknown; descriptionOverride?: unknown } | undefined;
      if (!tool) {
        return failure(404, 'assistant-mcp-tool-not-found');
      }
      if (typeof input?.isEnabled !== 'boolean' || (input.effect !== 'read-only' && input.effect !== 'mutation') ||
        (input.descriptionOverride !== null && typeof input.descriptionOverride !== 'string')) {
        return validationFailure();
      }
      if (input.isEnabled && tool.status === 'missing') {
        return validationFailure('isEnabled');
      }

      const override = typeof input.descriptionOverride === 'string' && input.descriptionOverride.trim().length > 0
        ? input.descriptionOverride.slice(0, 1_000)
        : null;
      const updated = {
        ...tool,
        isEnabled: input.isEnabled,
        effect: input.effect as McpToolEffect,
        descriptionOverride: override,
        status: input.isEnabled && tool.status === 'schema-changed' ? 'available' as const : tool.status
      };
      stored.tools.set(child, updated);
      return { status: 200, body: toTool(updated) };
    }

    return failure(404, 'assistant-not-found');
  }

  private sync(stored: StoredServer): void {
    const seen = new Set<string>();
    for (const remote of stored.remoteTools) {
      seen.add(remote.remoteName);
      const existing = stored.tools.get(remote.remoteName);
      const description = remote.description.slice(0, 1_000);
      if (!existing) {
        stored.tools.set(remote.remoteName, {
          remoteName: remote.remoteName,
          localId: `mcp.${stored.server.id}.${kebab(remote.remoteName)}`,
          description,
          descriptionOverride: null,
          isEnabled: false,
          effect: 'mutation',
          status: 'available',
          readOnlyHint: remote.readOnlyHint ?? null,
          inputSchemaHash: remote.inputSchemaHash
        });
        continue;
      }

      const changed = existing.inputSchemaHash !== remote.inputSchemaHash;
      stored.tools.set(remote.remoteName, {
        ...existing,
        description,
        readOnlyHint: remote.readOnlyHint ?? null,
        inputSchemaHash: remote.inputSchemaHash,
        isEnabled: changed ? false : existing.isEnabled,
        status: changed ? 'schema-changed' : existing.status === 'missing' ? 'available' : existing.status
      });
    }

    for (const [name, tool] of stored.tools) {
      if (!seen.has(name)) {
        stored.tools.set(name, { ...tool, isEnabled: false, status: 'missing' });
      }
    }
  }

  private connectionFailure(stored: StoredServer): string | null {
    let url: URL;
    try {
      url = new URL(stored.server.url);
    } catch {
      return 'assistant-mcp-unreachable';
    }

    const host = url.hostname.toLowerCase();
    if (host.startsWith('169.254.') || host.startsWith('[fe80') || host.includes('metadata')) {
      return 'assistant-mcp-host-blocked';
    }
    if (stored.server.authMode === 'forward-user-token' && !(this.scenario.admin?.forwardUserTokenHosts ?? []).includes(host)) {
      return 'assistant-mcp-host-blocked';
    }
    if (host.includes('unreachable')) {
      return 'assistant-mcp-unreachable';
    }
    if ((stored.server.authMode === 'bearer' || stored.server.authMode === 'api-key') && !stored.secret) {
      return 'assistant-mcp-unauthorized';
    }
    return null;
  }

  /** The contract failure for an invalid agent, or null when the agent is valid. */
  private agentFailure(input: AdminAgentInput | undefined): NhAssistantMockResult | null {
    if (typeof input?.instructions === 'string' && input.instructions.length > maxInstructions) {
      return failure(400, 'assistant-instructions-too-long');
    }

    return this.validAgent(input) ? null : validationFailure();
  }

  private validAgent(input: AdminAgentInput | undefined): boolean {
    const policies = this.scenario.admin?.policies;
    return !!input &&
      typeof input.id === 'string' && input.id.length <= 60 && dashCase.test(input.id) &&
      typeof input.displayName === 'string' && input.displayName.trim().length > 0 &&
      typeof input.description === 'string' &&
      typeof input.instructions === 'string' && input.instructions.length <= maxInstructions &&
      Array.isArray(input.toolSelectors) && input.toolSelectors.every(selector => /^[a-z0-9*][a-z0-9.*-]*$/.test(selector)) &&
      Array.isArray(input.mcpServerIds) && input.mcpServerIds.every(serverId => this.servers.has(serverId)) &&
      (input.requiredPolicy === null || (typeof input.requiredPolicy === 'string' && (!policies || policies.includes(input.requiredPolicy)))) &&
      autonomyLevels.includes(input.autonomy) &&
      typeof input.isEnabled === 'boolean';
  }

  private toAgent(input: AdminAgentInput, source: 'code' | 'admin', version: number, isOverridden: boolean): AdminAgent {
    return {
      ...structuredClone(input),
      version,
      source,
      isOverridden,
      instructionsHash: hashText(input.instructions),
      updatedAt: this.now()
    };
  }

  private toServer(stored: StoredServer): McpServer {
    return {
      ...stored.server,
      hasSecret: stored.secret !== null,
      assignedAgentIds: [...this.agents.values()]
        .filter(agent => agent.agent.mcpServerIds.includes(stored.server.id))
        .map(agent => agent.agent.id)
    };
  }
}

function codeAgentFromSummary(agent: AgentSummary): AdminAgentInput {
  return {
    id: agent.id,
    displayName: agent.id,
    description: '',
    instructions: '',
    toolSelectors: [],
    mcpServerIds: [],
    requiredPolicy: null,
    autonomy: agent.canMutate ? 'execute' : 'explain',
    isEnabled: true
  };
}

function toServerFields(input: McpServerInput): Pick<McpServer, 'id' | 'displayName' | 'url' | 'authMode' | 'headerName' | 'requiredPolicy' | 'isEnabled'> {
  return {
    id: input.id,
    displayName: input.displayName,
    url: input.url,
    authMode: input.authMode,
    headerName: input.authMode === 'api-key' ? (input.headerName || 'X-Api-Key') : null,
    requiredPolicy: input.requiredPolicy,
    isEnabled: input.isEnabled
  };
}

function validServer(input: McpServerInput | undefined): boolean {
  if (!input || typeof input.id !== 'string' || input.id.length > 40 || !dashCase.test(input.id) ||
    typeof input.displayName !== 'string' || input.displayName.trim().length === 0 ||
    !authModes.includes(input.authMode) || typeof input.isEnabled !== 'boolean') {
    return false;
  }

  try {
    const url = new URL(input.url);
    return url.protocol === 'https:' || (url.protocol === 'http:' && url.hostname === 'localhost');
  } catch {
    return false;
  }
}

function toTool({ inputSchemaHash, ...tool }: McpTool & { inputSchemaHash: string }): McpTool {
  return tool;
}

function toTools(stored: StoredServer): McpTool[] {
  return [...stored.tools.values()].map(toTool);
}

function toContext(version: ApplicationContextVersion & { text: string }): ApplicationContext {
  return { text: version.text, version: version.version, hash: version.hash, updatedAt: version.updatedAt, updatedBy: version.updatedBy };
}

/** A contract error response: `{ code, messageKey, errors? }`, never free text. */
function failure(status: number, code: string, errors?: Record<string, string[]>): NhAssistantMockResult {
  return { status, body: { code, messageKey: `nh-assistant.errors.${code}`, ...(errors ? { errors } : {}) } };
}

function validationFailure(field?: string): NhAssistantMockResult {
  return failure(400, 'assistant-validation', field ? { [field]: ['invalid'] } : undefined);
}

function kebab(value: string): string {
  return value
    .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
    .replace(/[^A-Za-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .toLowerCase();
}

/** A stable, non-cryptographic fingerprint; the mock only needs change detection. */
function hashText(text: string): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index++) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `fnv1a:${hash.toString(16).padStart(8, '0')}`;
}
