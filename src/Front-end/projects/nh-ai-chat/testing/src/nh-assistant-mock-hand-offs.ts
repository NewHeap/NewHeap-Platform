import {
  AdminAgent,
  AdminAgentInput,
  AgentHandOffs,
  AgentShadowVariants,
  CodeHandOff,
  DataHandOff,
  DataHandOffRequest,
  HandOffCeiling
} from '@newheap/platform-ai-chat';
import { NhAssistantMockAdminScenario, NhAssistantMockHandOffRule } from './nh-assistant-mock.models';
import { NhAssistantMockResult, mockDashCase, mockFailure as failure, mockMaxStateReason as maxReason, mockSnapshot } from './nh-assistant-mock-admin-shared';
import { mockWorksOnCovers } from './nh-assistant-mock-works-on';

/** What the hand-offs and variants read of the mock's agents and their run configuration. */
export interface NhAssistantMockHandOffAgents {
  find(agentId: string): AdminAgent | undefined;
  all(): AdminAgent[];
  /** An agent in code has a run configuration; one created in the administration once it has run settings. */
  isConfigured(agent: AdminAgent): boolean;
}

const maxHandOffs = 20;
const maxSources = 20;
const maxDescription = 500;
const maxPerDay = 100;
const defaultPerDay = 5;
const defaultVariants = 3;

/**
 * In-memory hand-offs defined as data and shadow variants of the mock API (S9), within the scenario's
 * `handOffsAsData` and `shadowVariantsAsData` as the ceilings in code: saving checks a hand-off as the
 * server checks it on every save and load, and `GET` reports a stored hand-off that no longer fits,
 * for example after its source lost its run settings, out of effect with its problem. A data variant
 * is created with `POST admin/agents` and `shadowOf`, which `shadowVariantRefusal` checks.
 */
export class NhAssistantMockHandOffs {
  private readonly stored = new Map<string, DataHandOff & { agentId: string }>();

  constructor(
    private readonly scenario: NhAssistantMockAdminScenario,
    private readonly agents: NhAssistantMockHandOffAgents,
    private readonly now: () => string
  ) {
  }

  /**
   * A copy of the hand-offs defined as data; the returned function puts it back, for an import of
   * an agent package that fails as a whole and for its preview.
   */
  snapshot(): () => void {
    return mockSnapshot(this.stored);
  }

  /**
   * `GET admin/agents/{id}/hand-offs`, `PUT` and `DELETE admin/agents/{id}/hand-offs/{subjectType}`
   * and `GET admin/agents/{id}/shadow-variants`; `segments` start after the agent id. `null` for
   * another route.
   */
  handle(method: string, agentId: string, segments: string[], body: unknown, query: URLSearchParams): NhAssistantMockResult | null {
    const [area, subjectType] = segments;
    if (area === 'shadow-variants' && segments.length === 1) {
      const agent = this.agents.find(agentId);
      if (!agent) {
        return failure('assistant-agent-not-found');
      }
      return method === 'GET' ? { status: 200, body: this.variantsOf(agent) } : { status: 405 };
    }
    if (area !== 'hand-offs' || segments.length > 2) {
      return null;
    }

    const agent = this.agents.find(agentId);
    if (!agent) {
      return failure('assistant-agent-not-found');
    }
    if (subjectType === undefined) {
      return method === 'GET' ? { status: 200, body: this.handOffsOf(agent) } : { status: 405 };
    }
    if (method === 'PUT') {
      return this.save(agent, subjectType, body as DataHandOffRequest | undefined);
    }
    if (method === 'DELETE') {
      return this.remove(agent, subjectType, query);
    }
    return { status: 405 };
  }

  /** Whether the agent accepts a hand-off defined as data, which keeps its run settings in use. */
  acceptsDataHandOffs(agentId: string): boolean {
    return [...this.stored.values()].some(handOff => handOff.agentId === agentId);
  }

  /**
   * Why `input` may not create a data shadow variant, as `POST admin/agents` with `shadowOf` answers,
   * or `null`.
   */
  shadowVariantRefusal(input: AdminAgentInput): NhAssistantMockResult | null {
    const liveId = input.shadowOf ?? null;
    if (liveId === null) {
      return null;
    }
    if (liveId === input.id) {
      return failure('assistant-validation', { shadowOf: ['invalid'] });
    }
    const live = this.agents.find(liveId);
    if (!this.allowsVariantsOf(liveId, live)) {
      return failure('assistant-shadow-variants-not-allowed');
    }
    if (!live || !this.agents.isConfigured(live)) {
      return failure('assistant-validation', { shadowOf: ['not-found'] });
    }
    if (!isLive(live)) {
      return failure('assistant-validation', { shadowOf: ['invalid'] });
    }
    return this.dataVariantsOf(liveId).length >= this.maxVariants()
      ? failure('assistant-configuration-limit')
      : null;
  }

  private handOffsOf(agent: AdminAgent): AgentHandOffs {
    return {
      agentId: agent.id,
      codeHandOffs: this.codeHandOffsOf(agent.id),
      dataHandOffs: [...this.stored.values()]
        .filter(item => item.agentId === agent.id)
        .sort((first, second) => first.subjectType.localeCompare(second.subjectType))
        .map(({ agentId, ...item }) => {
          const problem = this.problemOf(agent, item.subjectType, item.sources, item.maxPerSubjectPerDay);
          return { ...item, inEffect: problem === null, problem };
        }),
      ceiling: this.ceilingOf(agent),
      configured: this.agents.isConfigured(agent)
    };
  }

  private save(agent: AdminAgent, subjectType: string, request: DataHandOffRequest | undefined): NhAssistantMockResult {
    const ceiling = this.ceilingOf(agent);
    if (!ceiling) {
      return failure('assistant-handoffs-not-allowed');
    }
    if (this.codeHandOffsOf(agent.id).some(item => item.subjectType === subjectType)) {
      return failure('assistant-handoff-in-code');
    }
    if (!this.agents.isConfigured(agent)) {
      return failure('assistant-agent-not-configured');
    }

    const sources = Array.isArray(request?.sources) ? request!.sources.map(source => String(source).trim()) : [];
    const description = typeof request?.description === 'string' && request.description.trim().length > 0 ? request.description.trim() : null;
    const limit = request?.maxPerSubjectPerDay ?? Math.min(defaultPerDay, ceiling.maxPerSubjectPerDay);
    const reason = typeof request?.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    const errors: Record<string, string[]> = {};
    if (sources.length === 0) {
      errors['sources'] = ['required'];
    } else if (sources.length > maxSources) {
      errors['sources'] = ['too-long'];
    } else if (sources.some(source => !mockDashCase.test(source)) || new Set(sources).size !== sources.length || sources.includes(agent.id)) {
      errors['sources'] = ['invalid'];
    }
    if (description !== null && description.length > maxDescription) {
      errors['description'] = ['too-long'];
    }
    if (!Number.isInteger(limit) || limit < 1 || limit > maxPerDay) {
      errors['maxPerSubjectPerDay'] = ['invalid'];
    }
    if (reason !== null && reason.length > maxReason) {
      errors['reason'] = ['too-long'];
    }
    if (Object.keys(errors).length > 0) {
      return failure('assistant-validation', errors);
    }

    // The same check as every load, refused here instead of left out of effect.
    const problem = this.problemOf(agent, subjectType, sources, limit);
    switch (problem) {
      case null:
        break;
      case 'outside-ceiling':
        return failure('assistant-validation', ceiling.subjectTypes.some(item => item.subjectType === subjectType)
          ? { sources: ['not-allowed'] }
          : { subjectType: ['not-allowed'] });
      case 'unknown-source':
        return failure('assistant-validation', { sources: ['not-found'] });
      case 'not-worked-on':
        return failure('assistant-validation', { subjectType: ['invalid'] });
      case 'limit-above-ceiling':
        return failure('assistant-validation', { maxPerSubjectPerDay: ['not-allowed'] });
      default:
        return failure('assistant-validation', { sources: ['invalid'] });
    }

    const key = `${agent.id}/${subjectType}`;
    const existing = this.stored.get(key);
    if ((existing?.version ?? null) !== (request?.expectedVersion ?? null)) {
      return failure('assistant-version-conflict');
    }
    if (!existing && [...this.stored.values()].filter(item => item.agentId === agent.id).length >= maxHandOffs) {
      return failure('assistant-configuration-limit');
    }

    const now = this.now();
    const saved: DataHandOff & { agentId: string } = {
      agentId: agent.id,
      subjectType,
      version: (existing?.version ?? 0) + 1,
      sources,
      description,
      notifySource: request?.notifySource === true,
      maxPerSubjectPerDay: limit,
      toolId: `assistant.handoff.${agent.id}.${subjectType}`,
      changedAt: now,
      changedBy: 'current-user',
      reason,
      inEffect: true,
      problem: null,
      createdAt: existing?.createdAt ?? now,
      createdBy: existing?.createdBy ?? 'current-user'
    };
    this.stored.set(key, saved);
    const { agentId, ...body } = saved;
    return { status: 200, body };
  }

  private remove(agent: AdminAgent, subjectType: string, query: URLSearchParams): NhAssistantMockResult {
    if (this.codeHandOffsOf(agent.id).some(item => item.subjectType === subjectType)) {
      return failure('assistant-handoff-in-code');
    }
    const key = `${agent.id}/${subjectType}`;
    const existing = this.stored.get(key);
    if (!existing) {
      return failure('assistant-handoff-not-found');
    }
    const expected = query.get('expectedVersion');
    if (expected !== null && Number(expected) !== existing.version) {
      return failure('assistant-version-conflict');
    }
    this.stored.delete(key);
    return { status: 204 };
  }

  /**
   * The problem a hand-off would have, as the server checks it on save and on every load: a ceiling
   * that connects the agent about the type, a type it works on, sources with a run configuration
   * that the ceiling allows, and a limit within the ceiling's.
   */
  private problemOf(agent: AdminAgent, subjectType: string, sources: readonly string[], limit: number): string | null {
    const rules = this.rulesFor(agent, subjectType);
    if (!this.scenario.handOffsAsData) {
      return 'outside-ceiling';
    }
    if (!this.agents.isConfigured(agent)) {
      return 'unknown-agent';
    }
    if (this.codeHandOffsOf(agent.id).some(item => item.subjectType === subjectType)) {
      return 'conflicts-with-code';
    }
    if (!mockWorksOnCovers(agent, subjectType)) {
      return 'not-worked-on';
    }
    const known = sources.map(source => this.agents.find(source));
    if (known.some(source => !source || !this.agents.isConfigured(source))) {
      return 'unknown-source';
    }
    if (known.some(source => !rules.some(rule => this.allowsSource(rule, source!)))) {
      return 'outside-ceiling';
    }
    return limit > this.maxPerDay() ? 'limit-above-ceiling' : null;
  }

  private ceilingOf(agent: AdminAgent): HandOffCeiling | null {
    const rules = (this.scenario.handOffsAsData?.rules ?? []).filter(rule => this.allowsTarget(rule, agent));
    if (rules.length === 0) {
      return null;
    }
    const configured = this.agents.all().filter(candidate => candidate.id !== agent.id && this.agents.isConfigured(candidate));
    const types = [...new Set(rules.map(rule => rule.subjectType))].sort();
    return {
      subjectTypes: types.map(subjectType => {
        const forType = rules.filter(rule => rule.subjectType === subjectType);
        return {
          subjectType,
          sources: configured.filter(candidate => forType.some(rule => this.allowsSource(rule, candidate))).map(candidate => candidate.id).sort(),
          sourcesCreatedInAdministration: forType.some(rule => rule.fromAgentsCreatedInAdministration === true)
        };
      }),
      maxPerSubjectPerDay: this.maxPerDay(),
      maxHandOffs,
      maxSources,
      maxDescriptionLength: maxDescription
    };
  }

  private rulesFor(agent: AdminAgent, subjectType: string): NhAssistantMockHandOffRule[] {
    return (this.scenario.handOffsAsData?.rules ?? []).filter(rule => rule.subjectType === subjectType && this.allowsTarget(rule, agent));
  }

  private allowsTarget(rule: NhAssistantMockHandOffRule, agent: AdminAgent): boolean {
    return (rule.to ?? []).includes(agent.id) || (rule.toAgentsCreatedInAdministration === true && agent.source === 'admin');
  }

  private allowsSource(rule: NhAssistantMockHandOffRule, agent: AdminAgent): boolean {
    return (rule.from ?? []).includes(agent.id) || (rule.fromAgentsCreatedInAdministration === true && agent.source === 'admin');
  }

  private maxPerDay(): number {
    return this.scenario.handOffsAsData?.maxPerSubjectPerDay ?? maxPerDay;
  }

  private codeHandOffsOf(agentId: string): CodeHandOff[] {
    const agent = (this.scenario.agents ?? []).find(item => item.id === agentId && item.source === 'code');
    return (agent?.codeHandOffs ?? []).map(item => ({
      subjectType: item.subjectType,
      sources: [...item.sources],
      description: item.description ?? null,
      notifySource: item.notifySource ?? false,
      maxPerSubjectPerDay: item.maxPerSubjectPerDay ?? defaultPerDay,
      toolId: `assistant.handoff.${agentId}.${item.subjectType}`
    }));
  }

  private variantsOf(live: AdminAgent): AgentShadowVariants {
    const all = this.agents.all();
    const code = all.filter(agent => agent.source === 'code' && agent.shadowOf === live.id);
    const data = this.dataVariantsOf(live.id);
    const allowed = this.allowsVariantsOf(live.id, live) && this.agents.isConfigured(live) && isLive(live);
    return {
      agentId: live.id,
      allowsDataVariants: allowed,
      maxDataVariants: allowed ? this.maxVariants() : 0,
      variants: [
        ...code.map(agent => ({ agentId: agent.id, displayName: agent.displayName, source: 'code' as const, hasRunSettings: true, inEffect: true, problem: null })),
        ...data.map(agent => {
          const hasRunSettings = this.agents.isConfigured(agent);
          return { agentId: agent.id, displayName: agent.displayName, source: 'data' as const, hasRunSettings, inEffect: hasRunSettings, problem: null };
        })
      ]
    };
  }

  private dataVariantsOf(liveId: string): AdminAgent[] {
    return this.agents.all()
      .filter(agent => agent.source === 'admin' && agent.shadowOf === liveId)
      .sort((first, second) => first.id.localeCompare(second.id));
  }

  private allowsVariantsOf(liveId: string, live: AdminAgent | undefined): boolean {
    const ceiling = this.scenario.shadowVariantsAsData;
    return ceiling !== undefined
      && ((ceiling.of ?? []).includes(liveId) || (ceiling.ofAgentsCreatedInAdministration === true && (live?.source ?? 'admin') === 'admin'));
  }

  private maxVariants(): number {
    return this.scenario.shadowVariantsAsData?.maxPerAgent ?? defaultVariants;
  }
}

/** A live agent: no shadow variant and not in shadow by its code. */
function isLive(agent: AdminAgent): boolean {
  return !agent.shadowOf && !(agent.source === 'code' && agent.shadow?.configured === true);
}
