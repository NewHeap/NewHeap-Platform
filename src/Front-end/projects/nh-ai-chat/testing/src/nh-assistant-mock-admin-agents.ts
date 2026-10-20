import {
  AdminAgent,
  AdminAgentInput,
  AgentReplay,
  AgentShadowState,
  AgentSkillState,
  AgentState,
  AgentSummary,
  AssistantAutonomy
} from '@newheap/platform-ai-chat';
import { NhAssistantMockAdminAgent, NhAssistantMockScenario } from './nh-assistant-mock.models';
import {
  NhAssistantMockResult,
  mockDashCase,
  mockFailure as failure,
  mockHashText as hashText,
  mockMaxInstructions as maxInstructions,
  mockMaxStateReason as maxStateReason,
  mockValidationFailure as validationFailure
} from './nh-assistant-mock-admin-shared';
import { mockUndeclaredWorksOn, mockWorksOnErrors, mockWorksOnFromInput } from './nh-assistant-mock-works-on';

interface StoredAgent {
  agent: AdminAgent;
  /** The definition in code, for `reset`; null for admin agents. */
  codeDefinition: AdminAgentInput | null;
}

/** What the agents read of the other areas of the mock administration. */
export interface NhAssistantMockAgentRules {
  /** Whether an MCP server with this id exists, for `mcpServerIds`. */
  hasServer(serverId: string): boolean;
  /** The policies administrators may choose as run and view policies, by name. */
  agentPolicyNames(): string[];
  /** Whether the scenario lists divisions, so an agent may work per division. */
  divisionsListed(): boolean;
}

const autonomyLevels: AssistantAutonomy[] = ['observe', 'explain', 'propose', 'simulate', 'execute'];

/**
 * The agents of the mock administration: agents in code and agents created as data, their state,
 * shadow modes, technical accounts, feedback, evaluation and replays. The mock runs no agents on
 * their own, so reports and replays are empty.
 */
export class NhAssistantMockAdminAgents {
  private readonly agents = new Map<string, StoredAgent>();
  private readonly replays = new Map<string, AgentReplay[]>();

  constructor(
    private readonly scenario: NhAssistantMockScenario,
    private readonly now: () => string,
    private readonly rules: NhAssistantMockAgentRules
  ) {
    const admin = scenario.admin ?? {};
    const agents: NhAssistantMockAdminAgent[] = admin.agents
      ?? scenario.agents.map(agent => ({ ...codeAgentFromSummary(agent), source: 'code' as const }));
    for (const { source, skills, shadowOf, shadow, serviceAccount, worksOnDeclared, knownSubjectTypes, ...input } of agents) {
      const agentShadow = shadowOf !== undefined || shadow === true;
      const initial = this.toAgent(input, source, 1, false);
      this.agents.set(input.id, {
        agent: {
          ...initial,
          // Code says what its agents work on; a scenario agent that says nothing works on all objects.
          ...(source === 'code' ? { ...(mockWorksOnFromInput(input) ?? mockUndeclaredWorksOn), worksOnDeclared: worksOnDeclared ?? mockWorksOnFromInput(input) !== null } : {}),
          knownSubjectTypes: knownSubjectTypes ?? [],
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
  }

  find(agentId: string): AdminAgent | undefined {
    return this.agents.get(agentId)?.agent;
  }

  has(agentId: string): boolean {
    return this.agents.has(agentId);
  }

  /** Every agent, in the order they were added. */
  all(): AdminAgent[] {
    return [...this.agents.values()].map(stored => stored.agent);
  }

  /** Removes a deleted MCP server from the agents it was assigned to. */
  removeServer(serverId: string): void {
    for (const agent of this.agents.values()) {
      agent.agent = { ...agent.agent, mcpServerIds: agent.agent.mcpServerIds.filter(id => id !== serverId) };
    }
  }

  /** Chat agents: code agents keep their keys, admin agents carry literal text; stopped agents are left out. */
  chatAgents(): AgentSummary[] {
    const result: AgentSummary[] = [];
    for (const { agent } of this.agents.values()) {
      // An agent kept out of the chat still works on its own.
      if (agent.runState === 'stopped' || agent.runState === 'configured-off' || agent.offeredInChat === false) {
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

  /** `admin/agents`, `admin/agents/{id}` and their `reset`, `state`, `shadow`, `service-account` and `kill-switch`. */
  agentsEndpoint(method: string, id: string | undefined, action: string | undefined, body: unknown, length: number): NhAssistantMockResult {
    if (id === undefined) {
      if (method === 'GET') {
        return { status: 200, body: [...this.agents.values()].map(stored => stored.agent) };
      }
      if (method !== 'POST') {
        return { status: 405 };
      }

      const input = body as AdminAgentInput;
      const invalid = this.agentFailure(input, 'admin');
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
      const invalid = input?.id !== id ? validationFailure() : this.agentFailure(input, stored.agent.source);
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
  skillStateEndpoint(method: string, agentId: string, skillId: string, body: unknown): NhAssistantMockResult {
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
   * `POST admin/agents/{id}/skills/{skillId}/shadow`: one skill's shadow mode; a skill of an agent
   * in shadow works in shadow whatever its own mode.
   */
  skillShadowEndpoint(method: string, agentId: string, skillId: string, body: unknown): NhAssistantMockResult {
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
   * The feedback report of an agent. The mock runs no agents on their own and records no proposals,
   * so the summary, the list and the JSON Lines export are empty.
   */
  agentFeedback(method: string, agentId: string, child: string | undefined, length: number): NhAssistantMockResult {
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
  agentEvaluation(method: string, agentId: string, child: string | undefined, length: number, query: URLSearchParams): NhAssistantMockResult {
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
  agentReplay(method: string, agentId: string, action: string, body: unknown): NhAssistantMockResult {
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

  /**
   * A skill defined as data shows among the agent's skills as a skill in shadow by default, as the
   * server shows it; removing it takes its state along.
   */
  setDataSkill(agentId: string, skillId: string, description: string | null): void {
    const stored = this.agents.get(agentId);
    if (!stored) {
      return;
    }
    const others = (stored.agent.skills ?? []).filter(skill => skill.skillId !== skillId);
    const existing = (stored.agent.skills ?? []).find(skill => skill.skillId === skillId);
    stored.agent = {
      ...stored.agent,
      skills: description === null
        ? others
        : [...others, existing ? { ...existing, description } : {
          skillId,
          description,
          runState: 'active',
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
          shadow: shadowState(true, true, null, stored.agent.shadow?.locked ?? false)
        }]
    };
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
   * The contract failure for an invalid agent, or null when the agent is valid. What an agent created
   * here works on is checked as the server checks it; an agent in code takes it from its code.
   */
  private agentFailure(input: AdminAgentInput | undefined, source: 'code' | 'admin'): NhAssistantMockResult | null {
    if (typeof input?.instructions === 'string' && input.instructions.length > maxInstructions) {
      return failure(400, 'assistant-instructions-too-long');
    }
    if (!this.validAgent(input)) {
      return validationFailure();
    }

    const worksOn = source === 'admin' ? mockWorksOnFromInput(input!) : null;
    if (worksOn) {
      const errors = mockWorksOnErrors(
        worksOn,
        (this.scenario.subjectTypes ?? []).map(type => type.id),
        this.rules.agentPolicyNames(),
        this.rules.divisionsListed());
      if (Object.keys(errors).length > 0) {
        return failure(400, 'assistant-validation', errors);
      }
    }
    return null;
  }

  private validAgent(input: AdminAgentInput | undefined): boolean {
    const policies = this.scenario.admin?.policies;
    return !!input &&
      typeof input.id === 'string' && input.id.length <= 60 && mockDashCase.test(input.id) &&
      typeof input.displayName === 'string' && input.displayName.trim().length > 0 &&
      typeof input.description === 'string' &&
      typeof input.instructions === 'string' && input.instructions.length <= maxInstructions &&
      Array.isArray(input.toolSelectors) && input.toolSelectors.every(selector => /^[a-z0-9*][a-z0-9.*-]*$/.test(selector)) &&
      Array.isArray(input.mcpServerIds) && input.mcpServerIds.every(serverId => this.rules.hasServer(serverId)) &&
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
      serviceAccount: previous?.serviceAccount ?? null,
      // Code decides for its agents; an agent created here keeps its choice when it is omitted.
      offeredInChat: source === 'code' && previous
        ? previous.offeredInChat ?? true
        : input.offeredInChat ?? previous?.offeredInChat ?? true,
      // The same for what it works on: omitted keeps it, and a new agent works on all objects.
      ...(source === 'code' && previous
        ? pickWorksOn(previous)
        : mockWorksOnFromInput(input) ?? (previous ? pickWorksOn(previous) : { ...mockUndeclaredWorksOn, worksOnDeclared: true })),
      knownSubjectTypes: previous?.knownSubjectTypes ?? []
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

/** What a stored agent works on, carried over to its next version. */
function pickWorksOn(agent: AdminAgent): Partial<AdminAgent> {
  return {
    worksOnSubjectTypes: agent.worksOnSubjectTypes ?? [],
    worksOnAllSubjects: agent.worksOnAllSubjects ?? true,
    worksOnApplication: agent.worksOnApplication ?? false,
    perDivision: agent.perDivision ?? false,
    runPolicy: agent.runPolicy ?? null,
    viewPolicy: agent.viewPolicy ?? null,
    worksOnDeclared: agent.worksOnDeclared ?? true
  };
}
