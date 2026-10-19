import {
  AdminAgent,
  AgentRunConfiguration,
  AgentRunSettings,
  AgentRunSettingsRequest,
  CodeTrigger,
  DataSkill,
  DataSkillRequest,
  DataSkillVersion,
  DataSkillVersionStatus,
  DataTrigger,
  DataTriggerRequest,
  RunCatalog,
  SkillComparison
} from '@newheap/platform-ai-chat';
import { NhAssistantMockAdminScenario } from './nh-assistant-mock.models';

/** A mock response: status plus optional JSON body. */
interface MockResult {
  status: number;
  body?: unknown;
}

/** What the run configuration reads of the mock's agents and changes in them. */
export interface NhAssistantMockRunAgents {
  find(agentId: string): AdminAgent | undefined;
  /** Adds the state of a data skill to the agent, or removes it with `description` `null`. */
  setDataSkill(agentId: string, skillId: string, description: string | null): void;
}

interface StoredSkillVersion extends DataSkill {
  status: DataSkillVersionStatus;
}

const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const accountabilities = ['agent', 'subject-responsible', 'service-account'];
const conflicts = ['coalesce', 'queue', 'supersede', 'ignore'];
const maxDescription = 300;
const maxInstructions = 20_000;
const maxMessage = 2_000;
const maxReason = 500;

/**
 * In-memory run configuration as data of the mock API: run settings of agents created in the
 * administration, skills defined as data with versions and candidates, and data triggers. Saving
 * checks the names against the scenario's run catalog as the server checks them against the code;
 * new skills and triggers start in shadow. The mock runs no agents, so a comparison counts nothing.
 */
export class NhAssistantMockRunConfiguration {
  private readonly settings = new Map<string, AgentRunSettings>();
  private readonly skills = new Map<string, StoredSkillVersion[]>();
  private readonly triggers = new Map<string, DataTrigger>();
  private readonly catalog: RunCatalog;
  private readonly codeTriggers: (CodeTrigger & { agentId: string })[];

  constructor(scenario: NhAssistantMockAdminScenario, private readonly agents: NhAssistantMockRunAgents, private readonly now: () => string) {
    this.catalog = {
      triggerEvents: scenario.runCatalog?.triggerEvents ?? [],
      subjectSets: scenario.runCatalog?.subjectSets ?? [],
      ruleHandlers: scenario.runCatalog?.ruleHandlers ?? [],
      approvalPolicies: scenario.runCatalog?.approvalPolicies ?? scenario.policies ?? [],
      profiles: scenario.runCatalog?.profiles ?? []
    };
    this.codeTriggers = (scenario.codeTriggers ?? []).map(trigger => ({ ...trigger, skillId: trigger.skillId ?? null }));
  }

  /** `GET admin/run-catalog`. */
  runCatalog(method: string): MockResult {
    return method === 'GET' ? { status: 200, body: this.catalog } : { status: 405 };
  }

  /**
   * `admin/agents/{id}/configuration`, `run-settings`, `skills/{skillId}[/...]` and
   * `triggers/{triggerId}[/shadow]`; `segments` start after the agent id.
   */
  handle(method: string, agentId: string, segments: string[], body: unknown, query: URLSearchParams): MockResult | null {
    const agent = this.agents.find(agentId);
    const [area, id, action, child] = segments;
    if (area === 'configuration' && segments.length === 1) {
      return agent && method === 'GET' ? { status: 200, body: this.configuration(agent) } : failure(404, 'assistant-agent-not-found');
    }
    if (area === 'run-settings' && segments.length === 1) {
      return agent ? this.runSettings(method, agent, body, query) : failure(404, 'assistant-agent-not-found');
    }
    if (area === 'skills' && id !== undefined && (segments.length === 2 || action === 'versions' || action === 'candidate')) {
      if (!agent) {
        return failure(404, 'assistant-agent-not-found');
      }
      if (action === 'versions' && segments.length === 3) {
        return method === 'GET' ? { status: 200, body: this.versions(agent.id, id) } : { status: 405 };
      }
      if (action === 'candidate' && segments.length === 4) {
        return this.candidate(method, agent, id, child, body);
      }
      return segments.length === 2 ? this.skill(method, agent, id, body, query) : null;
    }
    if (area === 'triggers' && id !== undefined && (segments.length === 2 || (segments.length === 3 && action === 'shadow'))) {
      if (!agent) {
        return failure(404, 'assistant-agent-not-found');
      }
      return action === 'shadow' ? this.triggerShadow(method, agent, id, body) : this.trigger(method, agent, id, body, query);
    }
    return null;
  }

  private configuration(agent: AdminAgent): AgentRunConfiguration {
    const dataSkills = this.liveSkills(agent.id);
    const dataSkillIds = new Set(dataSkills.map(skill => skill.skillId));
    return {
      agentId: agent.id,
      toolSelectors: [...agent.toolSelectors],
      codeSkills: agent.source === 'code'
        ? (agent.skills ?? []).filter(skill => !dataSkillIds.has(skill.skillId)).map(skill => ({ skillId: skill.skillId, description: skill.description }))
        : [],
      dataSkills,
      codeTriggers: this.codeTriggers.filter(trigger => trigger.agentId === agent.id).map(({ agentId, ...trigger }) => trigger),
      dataTriggers: [...this.triggers.values()].filter(trigger => this.ownerOf(trigger) === agent.id).map(trigger => ({ ...trigger })),
      problems: [],
      source: agent.source,
      runSettings: agent.source === 'admin' ? this.settings.get(agent.id) ?? null : null
    };
  }

  private runSettings(method: string, agent: AdminAgent, body: unknown, query: URLSearchParams): MockResult {
    if (agent.source === 'code') {
      return failure(409, 'assistant-run-settings-in-code');
    }
    const current = this.settings.get(agent.id);
    if (method === 'DELETE') {
      if (!current) {
        return failure(409, 'assistant-agent-not-configured');
      }
      if (!versionMatches(query, current.version)) {
        return failure(409, 'assistant-version-conflict');
      }
      if (this.liveSkills(agent.id).length > 0 || [...this.triggers.values()].some(trigger => this.ownerOf(trigger) === agent.id)) {
        return failure(409, 'assistant-run-settings-in-use');
      }
      this.settings.delete(agent.id);
      return { status: 204 };
    }
    if (method !== 'PUT') {
      return { status: 405 };
    }

    const request = (body ?? {}) as AgentRunSettingsRequest;
    const serviceAccount = text(request.serviceAccountActorId);
    const errors: Record<string, string[]> = {};
    if (!accountabilities.includes(request.accountability)) {
      errors['accountability'] = [request.accountability ? 'invalid' : 'required'];
    }
    if ((request.accountability === 'service-account') !== (serviceAccount !== null)) {
      errors['serviceAccountActorId'] = [serviceAccount === null ? 'required' : 'invalid'];
    }
    if (request.allowGatewayReads && request.accountability === 'agent') {
      errors['allowGatewayReads'] = ['invalid'];
    }
    if (!inRange(request.retryCount, 0, 10)) {
      errors['retryCount'] = ['invalid'];
    }
    if (!inRange(request.maxConcurrentRuns, 1, 100)) {
      errors['maxConcurrentRuns'] = ['invalid'];
    }
    if (!inRange(request.maxRunsPerSubjectPerDay, 1, 1_000)) {
      errors['maxRunsPerSubjectPerDay'] = ['invalid'];
    }
    if (!budgetValid(request.dailyToolCalls, request.dailyTokens)) {
      errors['dailyBudget'] = ['invalid'];
    }
    this.requireKnown(errors, 'approvalPolicy', request.approvalPolicy, this.catalog.approvalPolicies);
    this.requireKnown(errors, 'triageProfile', request.triageProfile, this.catalog.profiles);
    this.requireKnown(errors, 'ruleHandler', request.ruleHandler, this.catalog.ruleHandlers.map(handler => handler.name));
    if (Object.keys(errors).length > 0) {
      return failure(400, 'assistant-validation', errors);
    }
    if (serviceAccount?.startsWith('unknown')) {
      return failure(400, 'assistant-service-account-not-found');
    }
    if ((request.expectedVersion ?? null) !== (current?.version ?? null)) {
      return failure(409, 'assistant-version-conflict');
    }

    const saved: AgentRunSettings = {
      version: (current?.version ?? 0) + 1,
      accountability: request.accountability,
      serviceAccountActorId: serviceAccount,
      delegateFromInitiator: request.delegateFromInitiator ?? false,
      allowGatewayReads: request.allowGatewayReads ?? false,
      elevatedReads: request.elevatedReads ?? 'restricted',
      approvalPolicy: text(request.approvalPolicy),
      retryCount: request.retryCount ?? 3,
      dailyToolCalls: request.dailyToolCalls ?? null,
      dailyTokens: request.dailyTokens ?? null,
      maxConcurrentRuns: request.maxConcurrentRuns ?? 4,
      maxRunsPerSubjectPerDay: request.maxRunsPerSubjectPerDay ?? 10,
      maxTokensPerRun: request.maxTokensPerRun ?? null,
      triageProfile: text(request.triageProfile),
      ruleHandler: text(request.ruleHandler),
      changedAt: this.now(),
      changedBy: 'current-user',
      reason: text(request.reason),
      inEffect: true,
      problem: null
    };
    this.settings.set(agent.id, saved);
    return { status: 200, body: saved };
  }

  private skill(method: string, agent: AdminAgent, skillId: string, body: unknown, query: URLSearchParams): MockResult {
    if (agent.source === 'code' && (agent.skills ?? []).some(skill => skill.skillId === skillId) && !this.skills.has(key(agent.id, skillId))) {
      return failure(409, 'assistant-skill-in-code');
    }
    if (!this.isConfigured(agent)) {
      return failure(409, 'assistant-agent-not-configured');
    }
    const versions = this.skills.get(key(agent.id, skillId)) ?? [];
    const live = versions.find(version => version.status === 'live');
    if (method === 'DELETE') {
      if (!live) {
        return failure(404, 'assistant-skill-not-found');
      }
      if (!versionMatches(query, live.version)) {
        return failure(409, 'assistant-version-conflict');
      }
      if ([...this.triggers.values()].some(trigger => this.ownerOf(trigger) === agent.id && trigger.skillId === skillId)) {
        return failure(409, 'assistant-skill-in-use');
      }
      for (const version of versions) {
        version.status = version.status === 'candidate' ? 'discarded' : version.status === 'live' ? 'superseded' : version.status;
      }
      this.agents.setDataSkill(agent.id, skillId, null);
      return { status: 204 };
    }
    if (method !== 'PUT') {
      return { status: 405 };
    }

    const request = (body ?? {}) as DataSkillRequest;
    const description = text(request.description);
    const errors: Record<string, string[]> = {};
    if (!dashCase.test(skillId)) {
      errors['skillId'] = ['invalid'];
    }
    if (description === null) {
      errors['description'] = ['required'];
    } else if (description.length > maxDescription) {
      errors['description'] = ['too-long'];
    }
    if ((request.instructions ?? '').length > maxInstructions) {
      errors['instructions'] = ['too-long'];
    }
    if ((request.tools ?? []).some(tool => !agent.toolSelectors.some(selector => covers(selector, tool)))) {
      errors['tools'] = ['invalid'];
    }
    if (!inRange(request.maxTokensPerRun, 1, Number.MAX_SAFE_INTEGER)) {
      errors['maxTokensPerRun'] = ['invalid'];
    }
    if (!budgetValid(request.dailyToolCalls, request.dailyTokens)) {
      errors['dailyBudget'] = ['invalid'];
    }
    if (request.apply === 'shadow' && (request.expectedVersion ?? null) === null) {
      errors['apply'] = ['invalid'];
    }
    this.requireKnown(errors, 'profile', request.profile, this.catalog.profiles);
    this.requireKnown(errors, 'ruleHandler', request.ruleHandler, this.catalog.ruleHandlers.map(handler => handler.name));
    this.requireKnown(errors, 'approvalPolicy', request.approvalPolicy, this.catalog.approvalPolicies);
    if (Object.keys(errors).length > 0) {
      return failure(400, 'assistant-validation', errors);
    }
    if ((request.expectedVersion ?? null) !== (live?.version ?? null)) {
      return failure(409, 'assistant-version-conflict');
    }

    const candidate = request.apply === 'shadow';
    for (const version of versions) {
      if (version.status === 'candidate') {
        version.status = 'discarded';
      } else if (version.status === 'live' && !candidate) {
        version.status = 'superseded';
      }
    }
    const instructions = text(request.instructions);
    const saved: StoredSkillVersion = {
      skillId,
      version: versions.reduce((latest, version) => Math.max(latest, version.version), 0) + 1,
      status: candidate ? 'candidate' : 'live',
      description: description!,
      instructions,
      instructionsHash: instructions === null ? null : hashText(instructions),
      tools: [...new Set(request.tools ?? [])],
      profile: text(request.profile),
      ruleHandler: text(request.ruleHandler),
      maxTokensPerRun: request.maxTokensPerRun ?? null,
      dailyToolCalls: request.dailyToolCalls ?? null,
      dailyTokens: request.dailyTokens ?? null,
      approvalPolicy: text(request.approvalPolicy),
      changedAt: this.now(),
      changedBy: 'current-user',
      reason: text(request.reason),
      inEffect: true,
      problem: null
    };
    this.skills.set(key(agent.id, skillId), [...versions, saved]);
    if (!candidate) {
      this.agents.setDataSkill(agent.id, skillId, saved.description);
    }
    return { status: 200, body: toSkill(saved) };
  }

  private candidate(method: string, agent: AdminAgent, skillId: string, action: string | undefined, body: unknown): MockResult {
    const versions = this.skills.get(key(agent.id, skillId)) ?? [];
    const candidate = versions.find(version => version.status === 'candidate');
    const live = versions.find(version => version.status === 'live');
    if (action === 'comparison' && method === 'GET') {
      if (!candidate || !live) {
        return failure(404, 'assistant-skill-candidate-not-found');
      }
      const comparison: SkillComparison = {
        agentId: agent.id,
        skillId,
        from: candidate.changedAt,
        to: this.now(),
        live: emptyStats(live.version),
        candidate: emptyStats(candidate.version),
        pairedEvents: 0,
        tools: []
      };
      return { status: 200, body: comparison };
    }
    if ((action !== 'promote' && action !== 'discard') || method !== 'POST') {
      return { status: 405 };
    }

    const request = body as { version?: unknown; reason?: unknown } | undefined;
    if (typeof request?.version !== 'number') {
      return failure(400, 'assistant-validation', { version: ['required'] });
    }
    if (!candidate) {
      return failure(404, 'assistant-skill-candidate-not-found');
    }
    if (candidate.version !== request.version) {
      return failure(409, 'assistant-version-conflict');
    }
    if (action === 'discard') {
      candidate.status = 'discarded';
      return { status: 204 };
    }

    if (live) {
      live.status = 'superseded';
    }
    candidate.status = 'live';
    this.agents.setDataSkill(agent.id, skillId, candidate.description);
    return { status: 200, body: toSkill(candidate) };
  }

  private versions(agentId: string, skillId: string): DataSkillVersion[] {
    return [...(this.skills.get(key(agentId, skillId)) ?? [])]
      .sort((left, right) => right.version - left.version)
      .map(version => ({
        version: version.version,
        status: version.status,
        description: version.description,
        instructionsHash: version.instructionsHash,
        createdAt: version.changedAt,
        createdBy: version.changedBy,
        reason: version.reason
      }));
  }

  private trigger(method: string, agent: AdminAgent, triggerId: string, body: unknown, query: URLSearchParams): MockResult {
    if (this.codeTriggers.some(trigger => trigger.triggerId === triggerId)) {
      return failure(409, 'assistant-trigger-in-code');
    }
    if (!this.isConfigured(agent)) {
      return failure(409, 'assistant-agent-not-configured');
    }
    const existing = this.triggers.get(triggerId);
    if (existing && this.ownerOf(existing) !== agent.id) {
      return failure(400, 'assistant-validation', { triggerId: ['invalid'] });
    }
    if (method === 'DELETE') {
      if (!existing) {
        return failure(404, 'assistant-trigger-not-found');
      }
      if (!versionMatches(query, existing.version)) {
        return failure(409, 'assistant-version-conflict');
      }
      this.triggers.delete(triggerId);
      return { status: 204 };
    }
    if (method !== 'PUT') {
      return { status: 405 };
    }

    const request = (body ?? {}) as DataTriggerRequest;
    const schedule = request.source === 'schedule';
    const errors: Record<string, string[]> = {};
    if (!dashCase.test(triggerId)) {
      errors['triggerId'] = ['invalid'];
    }
    if (request.source !== 'event' && request.source !== 'schedule') {
      errors['source'] = [request.source ? 'invalid' : 'required'];
    }
    if (request.conflict && !conflicts.includes(request.conflict)) {
      errors['conflict'] = ['invalid'];
    }
    if (!inRange(request.debounceSeconds, 0, 3_600)) {
      errors['debounceSeconds'] = ['invalid'];
    }
    if (request.source === 'event') {
      this.requireKnown(errors, 'event', request.event, this.catalog.triggerEvents.map(item => item.name), true);
    }
    if (schedule) {
      if ((text(request.cron) ?? '').split(' ').filter(field => field.length > 0).length < 5) {
        errors['cron'] = [text(request.cron) === null ? 'required' : 'invalid'];
      }
      this.requireKnown(errors, 'subjectSet', request.subjectSet, this.catalog.subjectSets.map(item => item.name), true);
      const message = text(request.message);
      if (message === null) {
        errors['message'] = ['required'];
      } else if (message.length > maxMessage) {
        errors['message'] = ['too-long'];
      }
    }
    const skill = text(request.skill);
    if (skill !== null && !this.skillIds(agent).includes(skill)) {
      errors['skill'] = ['not-found'];
    }
    if (Object.keys(errors).length > 0) {
      return failure(400, 'assistant-validation', errors);
    }
    if ((request.expectedVersion ?? null) !== (existing?.version ?? null)) {
      return failure(409, 'assistant-version-conflict');
    }

    const saved: DataTrigger & { agentId?: string } = {
      triggerId,
      skillId: skill,
      source: request.source,
      event: schedule ? null : text(request.event),
      cron: schedule ? text(request.cron) : null,
      timeZone: schedule ? text(request.timeZone) : null,
      subjectSet: schedule ? text(request.subjectSet) : null,
      message: schedule ? text(request.message) : null,
      conflict: request.conflict ?? (schedule ? 'ignore' : 'coalesce'),
      debounceSeconds: schedule ? 0 : request.debounceSeconds ?? 120,
      shadow: existing?.shadow ?? true,
      version: (existing?.version ?? 0) + 1,
      changedAt: this.now(),
      changedBy: 'current-user',
      reason: text(request.reason),
      inEffect: true,
      problem: null
    };
    this.triggers.set(triggerId, Object.assign(saved, { [ownerKey]: agent.id }));
    return { status: 200, body: withoutOwner(saved) };
  }

  private triggerShadow(method: string, agent: AdminAgent, triggerId: string, body: unknown): MockResult {
    if (method !== 'POST') {
      return { status: 405 };
    }
    const existing = this.triggers.get(triggerId);
    if (!existing || this.ownerOf(existing) !== agent.id) {
      return failure(404, 'assistant-trigger-not-found');
    }
    const request = body as { mode?: unknown; reason?: unknown } | undefined;
    if (request?.mode !== 'shadow' && request?.mode !== 'live') {
      return failure(400, 'assistant-validation', { mode: [request?.mode === undefined ? 'required' : 'invalid'] });
    }
    if (typeof request.reason === 'string' && request.reason.length > maxReason) {
      return failure(400, 'assistant-validation', { reason: ['too-long'] });
    }

    existing.shadow = request.mode === 'shadow';
    existing.changedAt = this.now();
    existing.changedBy = 'current-user';
    existing.reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    return { status: 200, body: withoutOwner(existing) };
  }

  /** An agent in code has a run configuration; one created in the administration once it has run settings. */
  private isConfigured(agent: AdminAgent): boolean {
    return agent.source === 'code' || this.settings.has(agent.id);
  }

  private skillIds(agent: AdminAgent): string[] {
    return [...(agent.skills ?? []).map(skill => skill.skillId), ...this.liveSkills(agent.id).map(skill => skill.skillId)];
  }

  private liveSkills(agentId: string): DataSkill[] {
    const result: DataSkill[] = [];
    for (const [stored, versions] of this.skills) {
      if (!stored.startsWith(`${agentId}/`)) {
        continue;
      }
      const live = versions.find(version => version.status === 'live');
      if (live) {
        const candidate = versions.find(version => version.status === 'candidate');
        result.push({ ...toSkill(live), candidate: candidate ? toSkill(candidate) : null });
      }
    }
    return result.sort((left, right) => left.skillId.localeCompare(right.skillId));
  }

  private ownerOf(trigger: DataTrigger): string | undefined {
    return (trigger as unknown as Record<string, string | undefined>)[ownerKey];
  }

  private requireKnown(errors: Record<string, string[]>, field: string, value: string | null | undefined, known: string[], required = false): void {
    const name = text(value);
    if (name === null) {
      if (required) {
        errors[field] = ['required'];
      }
      return;
    }
    if (!known.includes(name)) {
      errors[field] = ['not-found'];
    }
  }
}

const ownerKey = '__agentId';

function key(agentId: string, skillId: string): string {
  return `${agentId}/${skillId}`;
}

function toSkill({ status, ...skill }: StoredSkillVersion): DataSkill {
  return { ...skill, tools: [...skill.tools] };
}

function withoutOwner(trigger: DataTrigger): DataTrigger {
  const { [ownerKey]: owner, ...rest } = trigger as DataTrigger & Record<string, unknown>;
  return { ...rest } as DataTrigger;
}

function emptyStats(version: number): SkillComparison['live'] {
  return { version, runs: 0, completed: 0, failed: 0, proposals: 0, toolCalls: 0, tokens: 0 };
}

function text(value: string | null | undefined): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
}

function inRange(value: number | null | undefined, min: number, max: number): boolean {
  return value === null || value === undefined || (Number.isInteger(value) && value >= min && value <= max);
}

function budgetValid(calls: number | null | undefined, tokens: number | null | undefined): boolean {
  const hasCalls = calls !== null && calls !== undefined;
  const hasTokens = tokens !== null && tokens !== undefined;
  return hasCalls === hasTokens && (!hasCalls || (calls! >= 1 && tokens! >= 1));
}

function versionMatches(query: URLSearchParams, version: number): boolean {
  const expected = query.get('expectedVersion');
  return expected === null || Number(expected) === version;
}

/** The agent's selector covers the skill's: the same tool, or a prefix selector that includes it. */
function covers(agentSelector: string, skillSelector: string): boolean {
  return agentSelector === skillSelector
    || (agentSelector.endsWith('.*') && skillSelector.startsWith(agentSelector.slice(0, -1)));
}

function hashText(text: string): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index++) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `fnv1a:${hash.toString(16).padStart(8, '0')}`;
}

function failure(status: number, code: string, errors?: Record<string, string[]>): MockResult {
  return { status, body: { code, messageKey: `nh-assistant.errors.${code}`, ...(errors ? { errors } : {}) } };
}
