import {
  AdminAgent,
  AdminAgentInput,
  AgentReplay,
  AgentShadowState,
  AgentSkillState,
  AgentState,
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
import { NhAssistantMockAdminAgent, NhAssistantMockRemoteTool, NhAssistantMockScenario } from './nh-assistant-mock.models';

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
const maxStateReason = 500;
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
  private readonly replays = new Map<string, AgentReplay[]>();
  private readonly contextVersions: (ApplicationContextVersion & { text: string })[] = [];
  private preferences: AssistantPreferences;
  private canAdminister: boolean;

  constructor(private readonly scenario: NhAssistantMockScenario, private readonly now: () => string) {
    const admin = scenario.admin ?? {};
    this.canAdminister = admin.canAdminister ?? true;
    this.preferences = { ...defaultPreferences, ...scenario.preferences };

    this.contextVersions.push({ version: 1, hash: hashText(admin.context ?? ''), updatedAt: now(), updatedBy: null, text: admin.context ?? '' });

    const agents: NhAssistantMockAdminAgent[] = admin.agents
      ?? scenario.agents.map(agent => ({ ...codeAgentFromSummary(agent), source: 'code' as const }));
    for (const { source, skills, shadowOf, shadow, serviceAccount, ...input } of agents) {
      const agentShadow = shadowOf !== undefined || shadow === true;
      this.agents.set(input.id, {
        agent: {
          ...this.toAgent(input, source, 1, false),
          shadowOf: shadowOf ?? null,
          shadow: shadowState(agentShadow, shadowOf !== undefined || shadow === true, null, shadowOf !== undefined),
          serviceAccount: serviceAccount === undefined
            ? null
            : { actorId: serviceAccount, configuredActorId: serviceAccount, overridden: false, changedAt: null, changedBy: null, reason: null },
          skills: (skills ?? []).map((skill): AgentSkillState => ({
            skillId: skill.skillId,
            description: skill.description,
            runState: 'active' as const,
            changedAt: null,
            changedBy: null,
            reason: null,
            waitingInputs: 0,
            toolCallsToday: 0,
            tokensToday: 0,
            dailyToolCalls: null,
            dailyTokens: null,
            cancelledRuns: 0,
            startedRuns: 0,
            shadow: shadowState(agentShadow || skill.shadow === true, skill.shadow === true, null, shadowOf !== undefined)
          }))
        },
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

  /** Chat agents: code agents keep their keys, admin agents carry literal text; stopped agents are left out. */
  chatAgents(): AgentSummary[] {
    const result: AgentSummary[] = [];
    for (const { agent } of this.agents.values()) {
      if (agent.runState === 'stopped' || agent.runState === 'configured-off') {
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

  handleAdmin(method: string, segments: string[], body: unknown, query = new URLSearchParams()): NhAssistantMockResult {
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
        if (id !== undefined && action === 'feedback') {
          return this.agentFeedback(method, id, child, segments.length);
        }
        if (id !== undefined && action === 'evaluation') {
          return this.agentEvaluation(method, id, child, segments.length, query);
        }
        if (id !== undefined && (action === 'replay' || action === 'replays') && segments.length === 4) {
          return this.agentReplay(method, id, action, body);
        }
        if (id !== undefined && action === 'skills' && child !== undefined && segments[5] === 'state' && segments.length === 6) {
          return this.skillStateEndpoint(method, id, child, body);
        }
        if (id !== undefined && action === 'skills' && child !== undefined && segments[5] === 'shadow' && segments.length === 6) {
          return this.skillShadowEndpoint(method, id, child, body);
        }
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
      stored.agent = this.toAgent(stored.codeDefinition, 'code', stored.agent.version + 1, false, stored.agent);
      return { status: 200, body: stored.agent };
    }
    if (length === 4 && action === 'state') {
      return this.agentStateEndpoint(stored, method, body);
    }
    if (length === 4 && action === 'shadow' && method === 'POST') {
      return this.agentShadowEndpoint(stored, body);
    }
    if (length === 4 && action === 'service-account' && method === 'POST') {
      return this.serviceAccountEndpoint(stored, body);
    }
    if (length === 4 && action === 'kill-switch' && method === 'POST') {
      const engaged = (body as { engaged?: unknown } | undefined)?.engaged;
      if (typeof engaged !== 'boolean') {
        return validationFailure();
      }
      const changed = this.agentStateEndpoint(stored, 'POST', { state: engaged ? 'stopped' : 'active' });
      return changed.status === 200
        ? { status: 200, body: { agentId: id, enabled: stored.agent.runState !== 'stopped', cancelledRuns: 0 } }
        : changed;
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
      stored.agent = this.toAgent(definition, stored.agent.source, stored.agent.version + 1, stored.agent.source === 'code', stored.agent);
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

  /**
   * `POST admin/agents/{id}/skills/{skillId}/state`: the skill's state, next to the agent's.
   */
  private skillStateEndpoint(method: string, agentId: string, skillId: string, body: unknown): NhAssistantMockResult {
    const stored = this.agents.get(agentId);
    const skill = stored?.agent.skills?.find(item => item.skillId === skillId);
    if (!stored || !skill) {
      return failure(404, 'assistant-skill-not-found');
    }
    if (method !== 'POST') {
      return { status: 405 };
    }

    const request = body as { state?: unknown; reason?: unknown } | undefined;
    if (request?.state !== 'active' && request?.state !== 'paused' && request?.state !== 'stopped') {
      return validationFailure();
    }
    const reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    const changed: AgentSkillState = skill.runState === request.state
      ? skill
      : { ...skill, runState: request.state, changedAt: this.now(), changedBy: 'current-user', reason };
    stored.agent = { ...stored.agent, skills: stored.agent.skills!.map(item => item.skillId === skillId ? changed : item) };
    return { status: 200, body: changed };
  }

  /**
   * `POST admin/agents/{id}/shadow`: the agent's shadow mode as an override of the code. A
   * variant always works in shadow; the skills' effective modes follow the agent's.
   */
  private agentShadowEndpoint(stored: StoredAgent, body: unknown): NhAssistantMockResult {
    const request = shadowRequest(body);
    if (request === null) {
      return validationFailure();
    }
    const current = stored.agent.shadow ?? shadowState(false, false, null, false);
    if (current.locked) {
      return failure(409, 'assistant-shadow-variant');
    }

    const override = request.mode === 'code' ? null : request.mode === 'shadow';
    if (current.override !== override) {
      const shadow = override ?? current.configured;
      stored.agent = {
        ...stored.agent,
        shadow: { ...current, shadow, override, changedAt: this.now(), changedBy: 'current-user', reason: request.reason },
        skills: (stored.agent.skills ?? []).map(skill => ({ ...skill, shadow: withAgentShadow(skill.shadow, shadow) }))
      };
    }
    return { status: 200, body: toAgentState(stored.agent) };
  }

  /**
   * `POST admin/agents/{id}/skills/{skillId}/shadow`: one skill's shadow mode; a skill of an agent
   * in shadow works in shadow whatever its own mode.
   */
  private skillShadowEndpoint(method: string, agentId: string, skillId: string, body: unknown): NhAssistantMockResult {
    const stored = this.agents.get(agentId);
    const skill = stored?.agent.skills?.find(item => item.skillId === skillId);
    if (!stored || !skill) {
      return failure(404, 'assistant-skill-not-found');
    }
    if (method !== 'POST') {
      return { status: 405 };
    }
    const request = shadowRequest(body);
    if (request === null) {
      return validationFailure();
    }
    const agentShadow = stored.agent.shadow ?? shadowState(false, false, null, false);
    if (agentShadow.locked) {
      return failure(409, 'assistant-shadow-variant');
    }

    const current = skill.shadow ?? shadowState(agentShadow.shadow, false, null, false);
    const override = request.mode === 'code' ? null : request.mode === 'shadow';
    const changed: AgentSkillState = current.override === override
      ? skill
      : {
        ...skill,
        shadow: {
          ...current,
          shadow: agentShadow.shadow || (override ?? current.configured),
          override,
          changedAt: this.now(),
          changedBy: 'current-user',
          reason: request.reason
        }
      };
    stored.agent = { ...stored.agent, skills: stored.agent.skills!.map(item => item.skillId === skillId ? changed : item) };
    return { status: 200, body: changed };
  }

  /**
   * `POST admin/agents/{id}/service-account`: the technical account of an agent that answers to
   * one in code. The mock accepts every account id except those starting with `unknown`.
   */
  private serviceAccountEndpoint(stored: StoredAgent, body: unknown): NhAssistantMockResult {
    const current = stored.agent.serviceAccount;
    if (!current) {
      return failure(409, 'assistant-service-account-not-used');
    }
    const request = body as { actorId?: unknown; reason?: unknown } | undefined;
    if (request === undefined || (request.actorId !== null && request.actorId !== undefined && typeof request.actorId !== 'string')) {
      return validationFailure();
    }
    const actorId = typeof request.actorId === 'string' && request.actorId.trim().length > 0 ? request.actorId.trim() : null;
    if (actorId !== null && (actorId.length > 256 || actorId.startsWith('unknown'))) {
      return actorId.length > 256 ? validationFailure() : failure(400, 'assistant-service-account-not-found');
    }
    const reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    if (reason !== null && reason.length > maxStateReason) {
      return validationFailure();
    }

    const overridden = actorId !== null;
    if ((current.overridden ? current.actorId : null) !== actorId) {
      stored.agent = {
        ...stored.agent,
        serviceAccount: {
          ...current,
          actorId: actorId ?? current.configuredActorId,
          overridden,
          changedAt: this.now(),
          changedBy: 'current-user',
          reason
        }
      };
    }
    return { status: 200, body: toAgentState(stored.agent) };
  }

  /**
   * `GET` and `POST admin/agents/{id}/state`. The mock runs no agents on their own, so a pause
   * collects no waiting events and a stop cancels no runs; the version never changes.
   */
  private agentStateEndpoint(stored: StoredAgent, method: string, body: unknown): NhAssistantMockResult {
    if (method === 'GET') {
      return { status: 200, body: toAgentState(stored.agent) };
    }
    if (method !== 'POST') {
      return { status: 405 };
    }

    const request = body as { state?: unknown; reason?: unknown } | undefined;
    if (request?.state === undefined || request.state === null) {
      return validationFailure();
    }
    if (request.state !== 'active' && request.state !== 'paused' && request.state !== 'stopped') {
      return validationFailure();
    }
    const reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    if (reason !== null && reason.length > maxStateReason) {
      return validationFailure();
    }
    if (stored.agent.runState === 'configured-off') {
      return request.state === 'active'
        ? failure(409, 'assistant-agent-configured-off')
        : { status: 200, body: toAgentState(stored.agent) };
    }
    if (stored.agent.runState !== request.state) {
      stored.agent = {
        ...stored.agent,
        isEnabled: request.state !== 'stopped',
        runState: request.state,
        stateChangedAt: this.now(),
        stateChangedBy: 'current-user',
        stateReason: reason
      };
    }
    return { status: 200, body: toAgentState(stored.agent) };
  }

  /**
   * The feedback report of an agent. The mock runs no agents on their own and records no proposals,
   * so the summary, the list and the JSON Lines export are empty.
   */
  private agentFeedback(method: string, agentId: string, child: string | undefined, length: number): NhAssistantMockResult {
    if (!this.agents.has(agentId)) {
      return failure(404, 'assistant-agent-not-found');
    }
    if (method !== 'GET') {
      return { status: 405 };
    }

    if (length === 4) {
      return { status: 200, body: { items: [], total: 0 } };
    }
    if (length === 5 && child === 'summary') {
      const to = Date.parse(this.now());
      const from = to - 30 * 24 * 60 * 60 * 1000;
      return { status: 200, body: { agentId, from: new Date(from).toISOString(), to: new Date(to).toISOString(), rows: [] } };
    }
    if (length === 5 && child === 'export') {
      return { status: 200 };
    }

    return failure(404, 'assistant-not-found');
  }

  /**
   * The comparison of a shadow variant with its live agent. The mock runs no agents on their own,
   * so it pairs no runs and the export is empty.
   */
  private agentEvaluation(method: string, agentId: string, child: string | undefined, length: number, query: URLSearchParams): NhAssistantMockResult {
    if (!this.agents.has(agentId)) {
      return failure(404, 'assistant-agent-not-found');
    }
    if (method !== 'GET') {
      return { status: 405 };
    }

    const variantId = query.get('variant') ?? '';
    if (this.agents.get(variantId)?.agent.shadowOf !== agentId) {
      return failure(400, 'assistant-evaluation-not-variant');
    }
    if (length === 5 && child === 'export') {
      return { status: 200 };
    }
    if (length !== 4) {
      return failure(404, 'assistant-not-found');
    }

    const to = query.get('to') ?? this.now();
    const from = query.get('from') ?? new Date(Date.parse(to) - 7 * 24 * 60 * 60 * 1000).toISOString();
    const noCost = {
      runs: 0, modelCalls: 0, tokens: 0, toolCalls: 0,
      averageModelCalls: 0, averageTokens: 0, averageToolCalls: 0,
      p95ModelCalls: 0, p95Tokens: 0, p95ToolCalls: 0
    };
    return {
      status: 200,
      body: {
        agentId,
        variantId,
        from,
        to,
        runs: { live: 0, variant: 0, paired: 0, replayed: 0, liveUnpaired: 0, variantUnpaired: 0, truncated: false },
        tools: [],
        liveCost: noCost,
        variantCost: noCost,
        routing: [],
        differences: []
      }
    };
  }

  /**
   * Replays into a shadow variant. The mock records no runs, so a replay completes at once
   * without replaying any.
   */
  private agentReplay(method: string, agentId: string, action: string, body: unknown): NhAssistantMockResult {
    const stored = this.agents.get(agentId);
    if (!stored) {
      return failure(404, 'assistant-agent-not-found');
    }
    if (action === 'replays') {
      return method === 'GET' ? { status: 200, body: { items: [...(this.replays.get(agentId) ?? [])].reverse() } } : { status: 405 };
    }
    if (method !== 'POST') {
      return { status: 405 };
    }
    if (!stored.agent.shadowOf) {
      return failure(400, 'assistant-replay-not-shadow');
    }

    const request = (body ?? {}) as { sourceAgentId?: string | null; from?: string | null; to?: string | null; maxRuns?: number | null };
    const maxRuns = request.maxRuns ?? 100;
    if (maxRuns < 1 || maxRuns > 500) {
      return failure(400, 'assistant-message-invalid');
    }

    const now = this.now();
    const to = request.to ?? now;
    const replay = {
      id: crypto.randomUUID(),
      variantAgentId: agentId,
      sourceAgentId: request.sourceAgentId ?? stored.agent.shadowOf,
      from: request.from ?? new Date(Date.parse(to) - 7 * 24 * 60 * 60 * 1000).toISOString(),
      to,
      maxRuns,
      status: 'completed' as const,
      stopReason: null,
      runsReplayed: 0,
      runsSkipped: 0,
      createdAt: now,
      endedAt: now
    };
    this.replays.set(agentId, [...(this.replays.get(agentId) ?? []), replay]);
    return { status: 202, body: replay };
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
      (input.isEnabled === undefined || typeof input.isEnabled === 'boolean');
  }

  /**
   * The stored agent. `isEnabled` in the input is ignored, as by the server: the state comes from
   * `previous`, and a new agent starts active.
   */
  private toAgent(
    input: AdminAgentInput,
    source: 'code' | 'admin',
    version: number,
    isOverridden: boolean,
    previous?: AdminAgent
  ): AdminAgent {
    const { isEnabled, ...definition } = structuredClone(input);
    return {
      ...definition,
      isEnabled: previous?.isEnabled ?? true,
      version,
      source,
      isOverridden,
      instructionsHash: hashText(input.instructions),
      updatedAt: this.now(),
      runState: previous?.runState ?? 'active',
      stateChangedAt: previous?.stateChangedAt ?? null,
      stateChangedBy: previous?.stateChangedBy ?? null,
      stateReason: previous?.stateReason ?? null,
      waitingInputs: previous?.waitingInputs ?? 0,
      skills: previous?.skills ?? [],
      shadowOf: previous?.shadowOf ?? null,
      shadow: previous?.shadow ?? shadowState(false, false, null, false),
      serviceAccount: previous?.serviceAccount ?? null
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
    autonomy: agent.canMutate ? 'execute' : 'explain'
  };
}

function toAgentState(agent: AdminAgent): AgentState {
  return {
    agentId: agent.id,
    runState: agent.runState,
    active: agent.runState === 'active',
    changedAt: agent.stateChangedAt,
    changedBy: agent.stateChangedBy,
    reason: agent.stateReason,
    waitingInputs: agent.waitingInputs,
    cancelledRuns: 0,
    startedRuns: 0,
    skills: agent.skills ?? [],
    shadowOf: agent.shadowOf ?? null,
    shadow: agent.shadow ?? shadowState(false, false, null, false),
    serviceAccount: agent.serviceAccount ?? null
  };
}

function shadowState(shadow: boolean, configured: boolean, override: boolean | null, locked: boolean): AgentShadowState {
  return { shadow, configured, override, locked, changedAt: null, changedBy: null, reason: null };
}

/** A skill's mode after its agent's mode changed: the agent's shadow comes on top of its own. */
function withAgentShadow(skill: AgentShadowState | undefined, agentShadow: boolean): AgentShadowState {
  const own = skill?.override ?? skill?.configured ?? false;
  return { ...(skill ?? shadowState(false, false, null, false)), shadow: agentShadow || own };
}

function shadowRequest(body: unknown): { mode: 'shadow' | 'live' | 'code'; reason: string | null } | null {
  const request = body as { mode?: unknown; reason?: unknown } | undefined;
  if (request?.mode !== 'shadow' && request?.mode !== 'live' && request?.mode !== 'code') {
    return null;
  }
  const reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
  return reason !== null && reason.length > maxStateReason ? null : { mode: request.mode, reason };
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
