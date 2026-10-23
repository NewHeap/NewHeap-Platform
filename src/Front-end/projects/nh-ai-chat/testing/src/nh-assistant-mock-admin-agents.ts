import {
  AdminAgent,
  AdminAgentInput,
  AgentConsultationRoutesState,
  AgentReplay,
  AgentShadowState,
  AgentSkillState,
  AgentState,
  AgentSummary,
  AssistantAutonomy,
  AssistantDataClassification,
  InstructionAssetProblem,
  InstructionAssetReference,
  NhAssistantConsultationRoute
} from '@newheap/platform-ai-chat';
import { NhAssistantMockAdminAgent, NhAssistantMockScenario } from './nh-assistant-mock.models';
import { NhAssistantMockInstructionAssets } from './nh-assistant-mock-instruction-assets';
import {
  NhAssistantMockResult,
  mockClassificationWarnings,
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
  /** The routes of the agent's consultations and the switches administrators set. */
  consultations: StoredConsultationRoutes;
}

/** The routes code allows for an agent's consultations and the switches administrators set. */
interface StoredConsultationRoutes {
  /**
   * The subject types whose consultation in code allows each route; `null` for an agent without
   * consultations in code.
   */
  allowed: Partial<Record<NhAssistantConsultationRoute, string[]>> | null;
  /** The switch per route: `true` on, `false` off; a missing route follows the code. */
  overrides: Partial<Record<NhAssistantConsultationRoute, boolean>>;
  changedAt: string | null;
  changedBy: string | null;
  reason: string | null;
}

/** The routes in the order the server lists them, with the field of the request that switches each. */
const consultationRoutes: readonly [NhAssistantConsultationRoute, 'askerDecides' | 'subjectAgentRights' | 'subjectAskerRights'][] = [
  ['asker-decides', 'askerDecides'],
  ['subject-agent-rights', 'subjectAgentRights'],
  ['subject-asker-rights', 'subjectAskerRights']
];

/** What the agents read of the other areas of the mock administration. */
export interface NhAssistantMockAgentRules {
  /** Whether an MCP server with this id exists, for `mcpServerIds`. */
  hasServer(serverId: string): boolean;
  /** The policies administrators may choose as run and view policies, by name. */
  agentPolicyNames(): string[];
  /** Whether the scenario lists divisions, so an agent may work per division. */
  divisionsListed(): boolean;
  /** The profiles the run catalog offers, which an agent created here may choose. */
  profileNames(): string[];
  /**
   * The agent's consultations defined as data that are in effect, each with the routes in effect: they
   * count for its route switches as consultations in code do.
   */
  dataConsultations(agentId: string): { subjectType: string; routes: NhAssistantConsultationRoute[] }[];
  /** The application's text assets, which an agent created here may take its instructions from. */
  instructionAssets?(): NhAssistantMockInstructionAssets;
}

/** The profile of an agent that did not choose one, as the application's chat profile. */
const mockChatProfile = 'chat';

const autonomyLevels: AssistantAutonomy[] = ['observe', 'explain', 'propose', 'simulate', 'execute'];
const dataClassifications: AssistantDataClassification[] = ['public', 'internal', 'confidential', 'restricted'];

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
    // How an agent runs, in code or as data, belongs to its run configuration, not to the agent.
    for (const {
      source, skills, shadowOf, shadow, serviceAccount, worksOnDeclared, knownSubjectTypes, consultations, consultationDescriptions,
      codeRunSettings: _, runConfiguration: __, consultableAsData: ___, dataConsultations: ____, consultationTools: _____, ...input
    } of agents) {
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
            runState: skill.startsOff === true ? 'off' as const : 'active' as const,
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
        codeDefinition: source === 'code' ? structuredClone(input) : null,
        // Consultations in code belong to agents in code; every agent may get consultations as data.
        consultations: noSwitches(source === 'code' && (consultations !== undefined || consultationDescriptions !== undefined)
          ? structuredClone(consultations ?? {})
          : null)
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

  /**
   * Makes the next version of an agent, as a change of the metadata of an agent created here does on
   * the server; nothing else of the agent changes.
   */
  nextVersion(agentId: string): AdminAgent {
    const stored = this.agents.get(agentId);
    if (!stored) {
      throw new Error(`The mock has no agent '${agentId}'.`);
    }
    stored.agent = { ...stored.agent, version: stored.agent.version + 1, updatedAt: this.now() };
    return stored.agent;
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

  /**
   * `admin/agents`, `admin/agents/{id}` and their `reset`, `state`, `shadow`, `service-account`,
   * `consultation-routes` and `kill-switch`.
   */
  agentsEndpoint(method: string, id: string | undefined, action: string | undefined, body: unknown, length: number): NhAssistantMockResult {
    if (id === undefined) {
      if (method === 'GET') {
        return { status: 200, body: [...this.agents.values()].map(stored => this.withInstructions(stored.agent)) };
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
        return failure('assistant-agent-exists');
      }

      const agent = this.toAgent(input, 'admin', 1, false);
      this.agents.set(agent.id, { agent, codeDefinition: null, consultations: noSwitches(null) });
      return { status: 201, body: agent };
    }

    const stored = this.agents.get(id);
    if (!stored) {
      return failure('assistant-not-found');
    }

    if (length === 4 && action === 'reset' && method === 'POST') {
      if (!stored.codeDefinition) {
        return failure('assistant-agent-not-code');
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
    if (length === 4 && action === 'consultation-routes' && method === 'POST') {
      return this.consultationRoutesEndpoint(stored, body);
    }
    if (length === 4 && action === 'kill-switch' && method === 'POST') {
      const engaged = (body as { engaged?: unknown } | undefined)?.engaged;
      if (typeof engaged !== 'boolean') {
        return validationFailure();
      }
      // Releasing never switches on an agent that was off, as on the server: one that was off when
      // it stopped goes back off, and one that is off stays off.
      const released = stored.agent.runState === 'off' || stored.agent.offBeforeStop === true ? 'off' : 'active';
      const changed = this.agentStateEndpoint(stored, 'POST', { state: engaged ? 'stopped' : released });
      return changed.status === 200
        ? {
          status: 200,
          body: { agentId: id, enabled: stored.agent.runState !== 'stopped', cancelledRuns: 0, runState: stored.agent.runState }
        }
        : changed;
    }
    if (length !== 3) {
      return failure('assistant-not-found');
    }

    if (method === 'PUT') {
      const input = body as AdminAgentInput & { expectedVersion?: number };
      const invalid = input?.id !== id ? validationFailure() : this.agentFailure(input, stored.agent.source);
      if (invalid) {
        return invalid;
      }
      if (input.expectedVersion !== stored.agent.version) {
        return failure('assistant-version-conflict');
      }

      const { expectedVersion, ...definition } = input;
      stored.agent = this.toAgent(definition, stored.agent.source, stored.agent.version + 1, stored.agent.source === 'code', stored.agent);
      return { status: 200, body: stored.agent };
    }
    if (method === 'DELETE') {
      if (stored.agent.source === 'code') {
        return failure('assistant-code-agent-not-deletable');
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
      return failure('assistant-skill-not-found');
    }
    if (method !== 'POST') {
      return { status: 405 };
    }

    const request = body as { state?: unknown; reason?: unknown } | undefined;
    if (request?.state !== 'active' && request?.state !== 'paused' && request?.state !== 'stopped' && request?.state !== 'off') {
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
      return failure('assistant-skill-not-found');
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
      return failure('assistant-shadow-variant');
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
      return failure('assistant-agent-not-found');
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

    return failure('assistant-not-found');
  }

  /**
   * The comparison of a shadow variant with its live agent. The mock runs no agents on their own,
   * so it pairs no runs and the export is empty.
   */
  agentEvaluation(method: string, agentId: string, child: string | undefined, length: number, query: URLSearchParams): NhAssistantMockResult {
    if (!this.agents.has(agentId)) {
      return failure('assistant-agent-not-found');
    }
    if (method !== 'GET') {
      return { status: 405 };
    }

    const variantId = query.get('variant') ?? '';
    if (this.agents.get(variantId)?.agent.shadowOf !== agentId) {
      return failure('assistant-evaluation-not-variant');
    }
    if (length === 5 && child === 'export') {
      return { status: 200 };
    }
    if (length !== 4) {
      return failure('assistant-not-found');
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
      return failure('assistant-agent-not-found');
    }
    if (action === 'replays') {
      return method === 'GET' ? { status: 200, body: { items: [...(this.replays.get(agentId) ?? [])].reverse() } } : { status: 405 };
    }
    if (method !== 'POST') {
      return { status: 405 };
    }
    if (!stored.agent.shadowOf) {
      return failure('assistant-replay-not-shadow');
    }

    const request = (body ?? {}) as { sourceAgentId?: string | null; from?: string | null; to?: string | null; maxRuns?: number | null };
    const maxRuns = request.maxRuns ?? 100;
    if (maxRuns < 1 || maxRuns > 500) {
      return failure('assistant-message-invalid');
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
   * server shows it, and off when it was added with `startsOff`; removing it takes its state along.
   */
  setDataSkill(agentId: string, skillId: string, description: string | null, startsOff = false): void {
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
          runState: startsOff ? 'off' : 'active',
          changedAt: startsOff ? this.now() : null,
          changedBy: startsOff ? 'current-user' : null,
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
      return failure('assistant-shadow-variant');
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
    return { status: 200, body: this.toAgentState(stored) };
  }

  /**
   * `POST admin/agents/{id}/service-account`: the technical account of an agent that answers to
   * one in code. The mock accepts every account id except those starting with `unknown`.
   */
  private serviceAccountEndpoint(stored: StoredAgent, body: unknown): NhAssistantMockResult {
    const current = stored.agent.serviceAccount;
    if (!current) {
      return failure('assistant-service-account-not-used');
    }
    const request = body as { actorId?: unknown; reason?: unknown } | undefined;
    if (request === undefined || (request.actorId !== null && request.actorId !== undefined && typeof request.actorId !== 'string')) {
      return validationFailure();
    }
    const actorId = typeof request.actorId === 'string' && request.actorId.trim().length > 0 ? request.actorId.trim() : null;
    if (actorId !== null && (actorId.length > 256 || actorId.startsWith('unknown'))) {
      return actorId.length > 256 ? validationFailure() : failure('assistant-service-account-not-found');
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
    return { status: 200, body: this.toAgentState(stored) };
  }

  /**
   * `POST admin/agents/{id}/consultation-routes`: each route on, off or back to what the consultations
   * allow; a route left out keeps its switch. The consultations in code and as data in effect are the
   * ceiling, as on the server: switching on a route that none of them allows, or switching an agent
   * nobody can consult, answers `409 assistant-consult-route-not-allowed`.
   */
  private consultationRoutesEndpoint(stored: StoredAgent, body: unknown): NhAssistantMockResult {
    const request = body as Record<string, unknown> | undefined;
    if (!request || typeof request !== 'object') {
      return validationFailure();
    }
    for (const [, field] of consultationRoutes) {
      const value = request[field];
      if (value !== undefined && value !== null && value !== 'on' && value !== 'off' && value !== 'code') {
        return validationFailure(field);
      }
    }
    const reason = typeof request['reason'] === 'string' && request['reason'].trim().length > 0 ? request['reason'].trim() : null;
    if ((request['reason'] !== undefined && request['reason'] !== null && typeof request['reason'] !== 'string')
      || (reason !== null && reason.length > maxStateReason)) {
      return validationFailure('reason');
    }

    const current = stored.consultations;
    const allowed = this.allowedRoutes(stored);
    if (this.consultationRoutesState(stored) === null
      || !consultationRoutes.some(([route]) => (allowed[route]?.length ?? 0) > 0)
      || consultationRoutes.some(([route, field]) => request[field] === 'on' && (allowed[route]?.length ?? 0) === 0)) {
      return failure('assistant-consult-route-not-allowed');
    }

    const overrides = { ...current.overrides };
    for (const [route, field] of consultationRoutes) {
      const value = request[field];
      if (value === 'on' || value === 'off') {
        overrides[route] = value === 'on';
      } else if (value === 'code') {
        delete overrides[route];
      }
    }
    const changed = consultationRoutes.some(([route]) => overrides[route] !== current.overrides[route]);
    if (changed) {
      stored.consultations = { ...current, overrides, changedAt: this.now(), changedBy: 'current-user', reason };
    }
    return { status: 200, body: this.toAgentState(stored) };
  }

  /**
   * The subject types whose consultation allows each route: in code, and as data with the routes in
   * effect, as the server reads both from the agent's options in effect.
   */
  private allowedRoutes(stored: StoredAgent): Partial<Record<NhAssistantConsultationRoute, string[]>> {
    const allowed: Partial<Record<NhAssistantConsultationRoute, string[]>> = structuredClone(stored.consultations.allowed ?? {});
    for (const consultation of this.rules.dataConsultations(stored.agent.id)) {
      for (const route of consultation.routes) {
        allowed[route] = [...(allowed[route] ?? []), consultation.subjectType];
      }
    }
    return allowed;
  }

  /**
   * The routes of an agent's consultations as the server reports them; `null` for an agent nobody can
   * consult: it has no consultation in code and none as data in effect.
   */
  private consultationRoutesState(stored: StoredAgent): AgentConsultationRoutesState | null {
    const switches = stored.consultations;
    if (switches.allowed === null && this.rules.dataConsultations(stored.agent.id).length === 0) {
      return null;
    }

    const allowed = this.allowedRoutes(stored);
    return {
      routes: consultationRoutes.map(([route]) => {
        const types = allowed[route] ?? [];
        const override = switches.overrides[route] ?? null;
        return {
          route,
          allowed: types.length > 0,
          override,
          on: types.length > 0 && override !== false,
          subjectTypes: [...types]
        };
      }),
      changedAt: switches.changedAt,
      changedBy: switches.changedBy,
      reason: switches.reason
    };
  }

  /** The agent's state with the routes of its consultations. */
  private toAgentState(stored: StoredAgent): AgentState {
    return { ...toAgentState(stored), consultationRoutes: this.consultationRoutesState(stored) };
  }

  /**
   * `GET` and `POST admin/agents/{id}/state`. The mock runs no agents on their own, so a pause
   * collects no waiting events, a stop cancels no runs and switching an agent off or on starts
   * nothing; the version never changes.
   */
  private agentStateEndpoint(stored: StoredAgent, method: string, body: unknown): NhAssistantMockResult {
    if (method === 'GET') {
      return { status: 200, body: this.toAgentState(stored) };
    }
    if (method !== 'POST') {
      return { status: 405 };
    }

    const request = body as { state?: unknown; reason?: unknown } | undefined;
    if (request?.state === undefined || request.state === null) {
      return validationFailure();
    }
    if (request.state !== 'active' && request.state !== 'paused' && request.state !== 'stopped' && request.state !== 'off') {
      return validationFailure();
    }
    const reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    if (reason !== null && reason.length > maxStateReason) {
      return validationFailure();
    }
    if (stored.agent.runState === 'configured-off') {
      return request.state === 'active'
        ? failure('assistant-agent-configured-off')
        : { status: 200, body: this.toAgentState(stored) };
    }
    if (stored.agent.runState !== request.state) {
      stored.agent = {
        ...stored.agent,
        isEnabled: request.state !== 'stopped',
        runState: request.state,
        // A stop remembers whether the agent was off, for the kill switch; every other change forgets it.
        offBeforeStop: request.state === 'stopped' && stored.agent.runState === 'off',
        stateChangedAt: this.now(),
        stateChangedBy: 'current-user',
        stateReason: reason
      };
    }
    return { status: 200, body: this.toAgentState(stored) };
  }

  /**
   * The contract failure for an invalid agent, or null when the agent is valid. What an agent created
   * here works on is checked as the server checks it; an agent in code takes it from its code.
   */
  private agentFailure(input: AdminAgentInput | undefined, source: 'code' | 'admin'): NhAssistantMockResult | null {
    if (typeof input?.instructions === 'string' && input.instructions.length > maxInstructions) {
      return failure('assistant-instructions-too-long');
    }
    // Only an agent created here takes its instructions from an asset, never next to inline text.
    if (input?.instructionsAsset) {
      const errors: Record<string, string[]> = {};
      if (source !== 'admin' || !this.rules.instructionAssets) {
        errors['instructionsAsset'] = ['not-allowed'];
      } else {
        this.rules.instructionAssets().check(input.instructionsAsset, errors);
      }
      if (typeof input.instructions === 'string' && input.instructions.trim().length > 0) {
        errors['instructions'] = ['not-allowed'];
      }
      if (Object.keys(errors).length > 0) {
        return failure('assistant-validation', errors);
      }
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
        return failure('assistant-validation', errors);
      }
    }
    // An agent created here uses a profile the run catalog offers, or keeps the one it has or the
    // chat profile it gets by default; an agent in code takes its own.
    const profile = typeof input?.profile === 'string' ? input.profile.trim() : '';
    if (source === 'admin' && profile.length > 0 && profile !== mockChatProfile
      && profile !== this.agents.get(input!.id)?.agent.profile
      && !this.rules.profileNames().includes(profile)) {
      return failure('assistant-validation', { profile: ['not-found'] });
    }
    // An agent created here names the classification of its model calls, as a data skill does.
    const classification = chosenClassification(input!.dataClassification);
    if (source === 'admin' && classification !== null && !dataClassifications.includes(classification as AssistantDataClassification)) {
      return failure('assistant-validation', { dataClassification: ['invalid'] });
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
    // Only a new agent starts off, created that way here or with StartsOff() in code.
    // Metadata is not part of the definition; its values live with the mock's agent metadata.
    const { isEnabled, startsOff, startsInShadow, profile, dataClassification, metadata, instructionsAsset, ...definition } = structuredClone(input);
    const createdOff = previous === undefined && startsOff === true;
    // Only a new agent created here starts in shadow, as an override that applies once it has run settings.
    const createdInShadow = previous === undefined && source === 'admin' && startsInShadow === true;
    // The agent shows the asset's text read-only, or none while it does not resolve.
    const reference = source === 'admin' && instructionsAsset ? instructionsAsset : null;
    const assetProblem = reference ? this.instructionsProblemOf(reference) : null;
    const chosenProfile = source === 'admin' && typeof profile === 'string' && profile.trim().length > 0 ? profile.trim() : null;
    const classification = chosenClassification(dataClassification) as AssistantDataClassification | null;
    return this.withWarnings({
      ...definition,
      // An agent created here keeps its profile when the input omits it; code decides for the others.
      profile: chosenProfile ?? previous?.profile ?? mockChatProfile,
      // The same for its classification; an agent in code keeps the one of its scenario.
      dataClassification: source === 'admin' || previous === undefined
        ? classification ?? previous?.dataClassification ?? null
        : previous.dataClassification ?? null,
      isEnabled: previous?.isEnabled ?? true,
      version,
      source,
      isOverridden,
      instructions: reference ? this.rules.instructionAssets?.().textOf(reference) ?? '' : definition.instructions,
      instructionsHash: reference ? reference.hash : hashText(input.instructions),
      instructionsAsset: reference,
      instructionsProblem: assetProblem,
      updatedAt: this.now(),
      runState: previous?.runState ?? (createdOff ? 'off' : 'active'),
      stateChangedAt: previous?.stateChangedAt ?? (createdOff && source === 'admin' ? this.now() : null),
      stateChangedBy: previous?.stateChangedBy ?? (createdOff && source === 'admin' ? 'current-user' : null),
      stateReason: previous?.stateReason ?? null,
      waitingInputs: previous?.waitingInputs ?? 0,
      skills: previous?.skills ?? [],
      shadowOf: previous?.shadowOf ?? null,
      shadow: previous?.shadow ?? (createdInShadow
        ? { ...shadowState(true, false, true, false), changedAt: this.now(), changedBy: 'current-user' }
        : shadowState(false, false, null, false)),
      serviceAccount: previous?.serviceAccount ?? null,
      offBeforeStop: previous?.offBeforeStop ?? false,
      // Code decides for its agents; an agent created here keeps its choice when it is omitted.
      offeredInChat: source === 'code' && previous
        ? previous.offeredInChat ?? true
        : input.offeredInChat ?? previous?.offeredInChat ?? true,
      // The same for what it works on: omitted keeps it, and a new agent works on all objects.
      ...(source === 'code' && previous
        ? pickWorksOn(previous)
        : mockWorksOnFromInput(input) ?? (previous ? pickWorksOn(previous) : { ...mockUndeclaredWorksOn, worksOnDeclared: true })),
      knownSubjectTypes: previous?.knownSubjectTypes ?? []
    });
  }

  /** Why the asset an agent created here names does not resolve now, or `null` when it does. */
  private instructionsProblemOf(reference: InstructionAssetReference): InstructionAssetProblem | null {
    const assets = this.rules.instructionAssets?.();
    return assets ? assets.problemOf(reference) : 'no-asset-source';
  }

  /**
   * The agent as a read finds it now: the text of the asset it names and the problem when that no
   * longer resolves, as the server checks it at every load.
   */
  private withInstructions(agent: AdminAgent): AdminAgent {
    if (!agent.instructionsAsset) {
      return agent;
    }
    const problem = this.instructionsProblemOf(agent.instructionsAsset);
    return {
      ...agent,
      instructions: problem === null ? this.rules.instructionAssets?.().textOf(agent.instructionsAsset) ?? '' : '',
      instructionsProblem: problem
    };
  }

  /** The agent with the warnings the server reports for it, from its profile and classification. */
  private withWarnings(agent: AdminAgent): AdminAgent {
    return {
      ...agent,
      warnings: mockClassificationWarnings(this.scenario.admin?.profileClassifications, agent.profile, agent.dataClassification)
    };
  }
}

/** A classification name the input chose, or `null` when it chose none. */
function chosenClassification(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
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

function toAgentState(stored: StoredAgent): AgentState {
  const agent = stored.agent;
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
    serviceAccount: agent.serviceAccount ?? null,
    offBeforeStop: agent.runState === 'stopped' && agent.offBeforeStop === true,
    consultationRoutes: null
  };
}

/** The route switches of an agent nobody switched yet, with the routes its consultations in code allow. */
function noSwitches(allowed: Partial<Record<NhAssistantConsultationRoute, string[]>> | null): StoredConsultationRoutes {
  return { allowed, overrides: {}, changedAt: null, changedBy: null, reason: null };
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
