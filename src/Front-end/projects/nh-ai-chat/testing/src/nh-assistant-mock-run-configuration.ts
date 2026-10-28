import {
  AdminAgent,
  AgentRunConfiguration,
  AssistantDataClassification,
  AgentRunSettings,
  AgentRunSettingsRequest,
  CodeRunSettings,
  CodeTrigger,
  DataSkill,
  DataSkillRequest,
  DataSkillVersion,
  DataSkillVersionStatus,
  DataTrigger,
  DataTriggerRequest,
  DataTriggerVersion,
  FileLimits,
  LowerFileLimits,
  NhAssistantConsultationRoute,
  RunCatalog,
  SkillComparison,
  ToolCatalogEntry,
  TriggerComparison
} from '@newheap/platform-ai-chat';
import { NhAssistantMockAccounts } from './nh-assistant-mock-accounts';
import { NhAssistantMockConsultations } from './nh-assistant-mock-consultations';
import { NhAssistantMockAdminScenario } from './nh-assistant-mock.models';
import { mockSystemSubjectType, mockWorksOnCovers } from './nh-assistant-mock-works-on';
import { mockClassificationWarnings, mockFailure as failure, mockSnapshot } from './nh-assistant-mock-admin-shared';
import { NhAssistantMockInstructionAssets } from './nh-assistant-mock-instruction-assets';

/** A mock response: status plus optional JSON body. */
interface MockResult {
  status: number;
  body?: unknown;
}

/** What the run configuration reads of the mock's agents and changes in them. */
export interface NhAssistantMockRunAgents {
  find(agentId: string): AdminAgent | undefined;
  /** Every agent, to tell which of them select the tool of a consultation. */
  all(): AdminAgent[];
  /** The subject types a consultation can be about: the scenario's types and the whole application. */
  knownSubjectTypes(): string[];
  /**
   * Adds the state of a data skill to the agent, off with `startsOff`, or removes it with
   * `description` `null`.
   */
  setDataSkill(agentId: string, skillId: string, description: string | null, startsOff?: boolean): void;
  /** Whether the agent accepts a hand-off defined as data, which keeps its run settings in use. */
  acceptsDataHandOffs?(agentId: string): boolean;
}

interface StoredSkillVersion extends DataSkill {
  status: DataSkillVersionStatus;
}

/** One version of a data trigger in the mock's history: the live one, a candidate or history. */
interface StoredTriggerVersion extends DataTrigger {
  status: DataSkillVersionStatus;
  agentId: string;
}

const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const dataClassifications: string[] = ['public', 'internal', 'confidential', 'restricted'];
const accountabilities = ['agent', 'subject-responsible', 'service-account'];
const conflicts = ['coalesce', 'queue', 'supersede', 'ignore'];
const maxDescription = 300;
const maxInstructions = 20_000;
const maxMessage = 2_000;
const maxReason = 500;
/** A data trigger applies at most this many filters of its event, each named in lowercase dash-case. */
const maxTriggerFilters = 10;
const filterName = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/** The platform's default file limits (`NhAssistantLimits`). */
const defaultFileLimits: FileLimits = {
  maxFileBytes: 20 * 1024 * 1024,
  maxFilePages: 100,
  maxFilesPerTurn: 5,
  maxFileTokensPerTurn: 100_000
};

const fileLimitFields: readonly (keyof FileLimits)[] = ['maxFileBytes', 'maxFilePages', 'maxFilesPerTurn', 'maxFileTokensPerTurn'];

/**
 * In-memory run configuration as data of the mock API: run settings of agents created in the
 * administration, skills defined as data with versions and candidates, and data triggers. Saving
 * checks the names against the scenario's run catalog as the server checks them against the code;
 * new skills and triggers start in shadow. A schedule without a subject set runs for the whole
 * application and fits only an agent that works on it; a stored trigger that no longer fits what its
 * agent works on stays, out of effect, with the problem `not-worked-on`. Triggers have append-only
 * versions as skills do: a change applies at once or, with `apply` `shadow`, becomes the candidate next
 * to the live version, which the administrator promotes or discards. The mock runs no agents, so a
 * comparison counts nothing.
 */
export class NhAssistantMockRunConfiguration {
  private readonly settings = new Map<string, AgentRunSettings>();
  private readonly skills = new Map<string, StoredSkillVersion[]>();
  private readonly triggers = new Map<string, DataTrigger>();
  /** Every version of every data trigger by trigger id, live and candidate included, oldest first. */
  private readonly triggerVersions = new Map<string, StoredTriggerVersion[]>();
  private readonly consultations: NhAssistantMockConsultations;
  private readonly catalog: RunCatalog;
  private readonly codeTriggers: (CodeTrigger & { agentId: string })[];
  /** How the scenario's agents in code run, as their code would say. */
  private readonly codeRunSettings: Map<string, Partial<CodeRunSettings>>;
  /** What each profile and its fallbacks permit, to warn as the server does. */
  private readonly profileClassifications: NhAssistantMockAdminScenario['profileClassifications'];

  constructor(
    scenario: NhAssistantMockAdminScenario,
    private readonly agents: NhAssistantMockRunAgents,
    private readonly now: () => string,
    private readonly accounts: NhAssistantMockAccounts = new NhAssistantMockAccounts(scenario),
    private readonly instructionAssets: NhAssistantMockInstructionAssets = new NhAssistantMockInstructionAssets(scenario.instructionAssets)
  ) {
    const approvalPolicies = scenario.runCatalog?.approvalPolicies ?? scenario.policies ?? [];
    this.consultations = new NhAssistantMockConsultations(scenario.consultationsAsData, scenario.agents ?? [], {
      find: agentId => this.agents.find(agentId),
      all: () => this.agents.all(),
      knownSubjectTypes: () => this.agents.knownSubjectTypes(),
      isConfigured: agent => this.isConfigured(agent),
      accountabilityOf: agent => agent.source === 'admin' ? this.settings.get(agent.id)?.accountability ?? null : this.codeSettingsOf(agent).accountability,
      approvalPolicyOf: agent => agent.source === 'admin' ? this.settings.get(agent.id)?.approvalPolicy ?? null : this.codeSettingsOf(agent).approvalPolicy,
      approvalPolicies: () => this.catalog.approvalPolicies,
      approvalPolicySummaries: () => this.catalog.approvalPolicySummaries ?? [],
      agentPolicyNames: () => this.agentPolicyNames
    }, now);
    this.catalog = {
      // A trigger event lists its named filters, none unless the scenario's application registers some.
      triggerEvents: (scenario.runCatalog?.triggerEvents ?? []).map(event => ({ ...event, filters: event.filters ?? [] })),
      subjectSets: scenario.runCatalog?.subjectSets ?? [],
      ruleHandlers: scenario.runCatalog?.ruleHandlers ?? [],
      approvalPolicies,
      profiles: scenario.runCatalog?.profiles ?? [],
      // By default a policy lets the subject's approvers decide within a week, after the run.
      approvalPolicySummaries: scenario.runCatalog?.approvalPolicySummaries ?? approvalPolicies.map(name => ({
        name,
        approvers: [{ kind: 'subject-role', role: 'approver', namedRole: null, policy: null }],
        lifetimeMinutes: 7 * 24 * 60,
        fourEyes: false,
        mode: 'deferred'
      })),
      accountSearch: accounts.searchable,
      agentPolicies: scenario.runCatalog?.agentPolicies ?? [],
      divisions: scenario.runCatalog?.divisions ?? false,
      // The platform's default file limits, unless the scenario's application chose its own.
      fileLimits: scenario.runCatalog?.fileLimits ?? defaultFileLimits,
      consultationCeiling: this.consultations.applicationCeiling,
      instructionAssets: instructionAssets.supported,
      compositionCeiling: scenario.compositionsAsData ?? null
    };
    this.codeTriggers = (scenario.codeTriggers ?? []).map(trigger => ({
      ...trigger,
      skillId: trigger.skillId ?? null,
      subjectType: trigger.subjectType ?? null
    }));
    this.profileClassifications = scenario.profileClassifications;
    this.codeRunSettings = new Map((scenario.agents ?? [])
      .filter(agent => agent.source === 'code')
      .map(agent => [agent.id, agent.codeRunSettings ?? {}]));
  }

  /** The policies administrators may choose as run and view policies, by name. */
  get agentPolicyNames(): string[] {
    return (this.catalog.agentPolicies ?? []).map(policy => policy.name);
  }

  /** Whether the scenario lists divisions, so an agent may work per division. */
  get divisionsListed(): boolean {
    return this.catalog.divisions === true;
  }

  /** The profiles the run catalog offers to data skills and to agents created in the administration. */
  get profileNames(): string[] {
    return [...this.catalog.profiles];
  }

  /**
   * A copy of the run settings, skills, triggers and consultations; the returned function puts it
   * back, for an import of an agent package that fails as a whole and for its preview.
   */
  snapshot(): () => void {
    const restoreOwn = mockSnapshot(this.settings, this.skills, this.triggers, this.triggerVersions);
    const restoreConsultations = this.consultations.snapshot();
    return () => {
      restoreOwn();
      restoreConsultations();
    };
  }

  /**
   * The agent's consultations defined as data in effect with their routes in effect, which count for
   * its route switches.
   */
  consultationsInEffect(agentId: string): { subjectType: string; routes: NhAssistantConsultationRoute[] }[] {
    return this.consultations.inEffect(agentId);
  }

  /** The generated consult and request tools of the data consultations in effect. */
  consultationTools(): ToolCatalogEntry[] {
    return this.consultations.tools();
  }

  /** `GET admin/run-catalog`. */
  runCatalog(method: string): MockResult {
    return method === 'GET' ? { status: 200, body: this.catalog } : { status: 405 };
  }

  /**
   * `admin/agents/{id}/configuration`, `run-settings`, `skills/{skillId}[/...]`,
   * `triggers/{triggerId}[/shadow|/versions|/candidate/...]` and `consultations/{subjectType}[/state]`;
   * `segments` start after the agent id.
   */
  handle(method: string, agentId: string, segments: string[], body: unknown, query: URLSearchParams): MockResult | null {
    const agent = this.agents.find(agentId);
    const [area, id, action, child] = segments;
    if (area === 'configuration' && segments.length === 1) {
      return agent && method === 'GET' ? { status: 200, body: this.configuration(agent) } : failure('assistant-agent-not-found');
    }
    if (area === 'run-settings' && segments.length === 1) {
      return agent ? this.runSettings(method, agent, body, query) : failure('assistant-agent-not-found');
    }
    if (area === 'skills' && id !== undefined && (segments.length === 2 || action === 'versions' || action === 'candidate')) {
      if (!agent) {
        return failure('assistant-agent-not-found');
      }
      if (action === 'versions' && segments.length === 3) {
        return method === 'GET' ? { status: 200, body: this.versions(agent.id, id) } : { status: 405 };
      }
      if (action === 'candidate' && segments.length === 4) {
        return this.candidate(method, agent, id, child, body);
      }
      return segments.length === 2 ? this.skill(method, agent, id, body, query) : null;
    }
    if (area === 'triggers' && id !== undefined && (segments.length === 3 && action === 'versions' || segments.length === 4 && action === 'candidate')) {
      if (!agent) {
        return failure('assistant-agent-not-found');
      }
      return action === 'versions'
        ? method === 'GET' ? { status: 200, body: this.triggerHistory(agent.id, id) } : { status: 405 }
        : this.triggerCandidate(method, agent, id, child, body);
    }
    if (area === 'triggers' && id !== undefined && (segments.length === 2 || (segments.length === 3 && action === 'shadow'))) {
      if (!agent) {
        return failure('assistant-agent-not-found');
      }
      return action === 'shadow' ? this.triggerShadow(method, agent, id, body) : this.trigger(method, agent, id, body, query);
    }
    if (area === 'consultations' && id !== undefined && (segments.length === 2 || (segments.length === 3 && action === 'state'))) {
      return agent ? this.consultations.handle(method, agent, id, body, query, action) : failure('assistant-agent-not-found');
    }
    return null;
  }

  private configuration(agent: AdminAgent): AgentRunConfiguration {
    const dataSkills = this.liveSkills(agent.id);
    const dataSkillIds = new Set(dataSkills.map(skill => skill.skillId));
    const dataTriggers = [...this.triggers.values()]
      .filter(trigger => this.ownerOf(trigger) === agent.id)
      .map(trigger => {
        const problem = this.triggerProblem(agent, trigger);
        const live = problem ? { ...withoutOwner(trigger), inEffect: false, problem } : withoutOwner(trigger);
        const candidate = this.candidateOf(trigger.triggerId);
        return { ...live, candidate: candidate ? this.candidateDto(agent, candidate) : null };
      });
    return {
      agentId: agent.id,
      toolSelectors: [...agent.toolSelectors],
      codeSkills: agent.source === 'code'
        ? (agent.skills ?? []).filter(skill => !dataSkillIds.has(skill.skillId)).map(skill => ({ skillId: skill.skillId, description: skill.description }))
        : [],
      dataSkills,
      codeTriggers: this.codeTriggers.filter(trigger => trigger.agentId === agent.id).map(({ agentId, ...trigger }) => trigger),
      dataTriggers,
      problems: [
        ...dataSkills
          .flatMap(skill => [{ kind: 'skill' as const, skill }, ...(skill.candidate ? [{ kind: 'candidate' as const, skill: skill.candidate }] : [])])
          .filter(entry => entry.skill.instructionsAsset && entry.skill.problem !== null)
          .map(entry => ({ agentId: agent.id, kind: entry.kind, id: entry.skill.skillId, code: entry.skill.problem! })),
        ...dataTriggers
          .filter(trigger => trigger.problem !== null)
          .map(trigger => ({ agentId: agent.id, kind: 'trigger' as const, id: trigger.triggerId, code: trigger.problem! })),
        ...dataTriggers
          .filter(trigger => trigger.candidate && trigger.candidate.problem !== null)
          .map(trigger => ({ agentId: agent.id, kind: 'trigger-candidate' as const, id: trigger.triggerId, code: trigger.candidate!.problem! })),
        ...this.consultations.problems(agent)
      ],
      source: agent.source,
      runSettings: agent.source === 'admin' ? this.settings.get(agent.id) ?? null : null,
      codeRunSettings: agent.source === 'code' ? this.codeSettingsOf(agent) : null,
      codeConsultations: this.consultations.codeConsultations(agent),
      dataConsultations: this.consultations.dataConsultations(agent),
      consultationCeiling: this.consultations.ceilingOf(agent)
    };
  }

  /**
   * How an agent in code runs: the scenario's `codeRunSettings`, answering to its technical account
   * when it has one and to itself otherwise, as an agent in code does by default.
   */
  private codeSettingsOf(agent: AdminAgent): CodeRunSettings {
    const given = this.codeRunSettings.get(agent.id) ?? {};
    return {
      accountability: given.accountability ?? (agent.serviceAccount ? 'service-account' : 'agent'),
      delegateFromInitiator: given.delegateFromInitiator ?? false,
      approvalPolicy: given.approvalPolicy ?? null,
      usesRules: given.usesRules ?? false,
      startsOff: given.startsOff ?? false,
      acceptsFiles: given.acceptsFiles ?? false,
      chatOnly: given.chatOnly ?? agent.chatOnly === true,
      skillsInChat: given.skillsInChat ?? false,
      allowQuestions: given.allowQuestions ?? false,
      usesMemory: given.usesMemory ?? false,
      triageProfile: given.triageProfile ?? null,
      // The platform's defaults, as an agent in code that sets none has them.
      retryCount: given.retryCount ?? 3,
      maxConcurrentRuns: given.maxConcurrentRuns ?? 4,
      maxRunsPerSubjectPerDay: given.maxRunsPerSubjectPerDay ?? 10,
      dailyToolCalls: given.dailyToolCalls ?? null,
      dailyTokens: given.dailyTokens ?? null,
      maxTokensPerRun: given.maxTokensPerRun ?? null,
      maxConsultationsPerTurn: given.maxConsultationsPerTurn ?? null,
      elevatedReads: given.elevatedReads ?? 'restricted',
      allowGatewayReads: given.allowGatewayReads ?? false
    };
  }

  /**
   * Why a stored trigger no longer fits what its agent works on, as the server reads it: an event
   * that names a type the agent does not work on, or a schedule for the whole application of an
   * agent that does not work on it.
   */
  private triggerProblem(agent: AdminAgent, trigger: DataTrigger): string | null {
    if (trigger.source === 'event') {
      const event = this.catalog.triggerEvents.find(item => item.name === trigger.event);
      if (event?.subjectType && !mockWorksOnCovers(agent, event.subjectType)) {
        return 'not-worked-on';
      }
      // A filter the event no longer offers keeps the trigger from firing.
      const offered = (event?.filters ?? []).map(filter => filter.name);
      return (trigger.filters ?? []).some(name => !offered.includes(name)) ? 'unknown-filter' : null;
    }
    return trigger.subjectSet === null && !mockWorksOnCovers(agent, mockSystemSubjectType) ? 'not-worked-on' : null;
  }

  private runSettings(method: string, agent: AdminAgent, body: unknown, query: URLSearchParams): MockResult {
    if (agent.source === 'code') {
      return failure('assistant-run-settings-in-code');
    }
    const current = this.settings.get(agent.id);
    if (method === 'DELETE') {
      if (!current) {
        return failure('assistant-agent-not-configured');
      }
      if (!versionMatches(query, current.version)) {
        return failure('assistant-version-conflict');
      }
      if (this.liveSkills(agent.id).length > 0
        || [...this.triggers.values()].some(trigger => this.ownerOf(trigger) === agent.id)
        || this.consultations.has(agent.id)
        || this.agents.acceptsDataHandOffs?.(agent.id)) {
        return failure('assistant-run-settings-in-use');
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
    this.requireFileLimits(errors, request.acceptsFiles ?? false, request);
    if (Object.keys(errors).length > 0) {
      return failure('assistant-validation', errors);
    }
    if (serviceAccount && this.accounts.rejects(serviceAccount)) {
      return failure('assistant-service-account-not-found');
    }
    if ((request.expectedVersion ?? null) !== (current?.version ?? null)) {
      return failure('assistant-version-conflict');
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
      problem: null,
      acceptsFiles: request.acceptsFiles ?? false,
      ...storedFileLimits(request)
    };
    this.settings.set(agent.id, saved);
    return { status: 200, body: saved };
  }

  private skill(method: string, agent: AdminAgent, skillId: string, body: unknown, query: URLSearchParams): MockResult {
    if (agent.source === 'code' && (agent.skills ?? []).some(skill => skill.skillId === skillId) && !this.skills.has(key(agent.id, skillId))) {
      return failure('assistant-skill-in-code');
    }
    if (!this.isConfigured(agent)) {
      return failure('assistant-agent-not-configured');
    }
    const versions = this.skills.get(key(agent.id, skillId)) ?? [];
    const live = versions.find(version => version.status === 'live');
    if (method === 'DELETE') {
      if (!live) {
        return failure('assistant-skill-not-found');
      }
      if (!versionMatches(query, live.version)) {
        return failure('assistant-version-conflict');
      }
      if ([...this.triggers.values()].some(trigger => this.ownerOf(trigger) === agent.id && trigger.skillId === skillId)) {
        return failure('assistant-skill-in-use');
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
    // A version takes its instructions from an asset of the application or has text of its own.
    const instructionsAsset = request.instructionsAsset ?? null;
    if (instructionsAsset !== null) {
      if (text(request.instructions) !== null) {
        errors['instructions'] = ['not-allowed'];
      }
      this.instructionAssets.check(instructionsAsset, errors);
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
    // Only a new skill starts off; a skill that exists keeps its state.
    if (request.startsOff === true && (request.expectedVersion ?? null) !== null) {
      errors['startsOff'] = ['invalid'];
    }
    const classification = text(request.dataClassification);
    if (classification !== null && !dataClassifications.includes(classification)) {
      errors['dataClassification'] = ['invalid'];
    }
    this.requireKnown(errors, 'profile', request.profile, this.catalog.profiles);
    this.requireKnown(errors, 'ruleHandler', request.ruleHandler, this.catalog.ruleHandlers.map(handler => handler.name));
    this.requireKnown(errors, 'approvalPolicy', request.approvalPolicy, this.catalog.approvalPolicies);
    this.requireFileLimits(errors, request.acceptsFiles ?? false, request);
    if (Object.keys(errors).length > 0) {
      return failure('assistant-validation', errors);
    }
    if ((request.expectedVersion ?? null) !== (live?.version ?? null)) {
      return failure('assistant-version-conflict');
    }

    const candidate = request.apply === 'shadow';
    for (const version of versions) {
      if (version.status === 'candidate') {
        version.status = 'discarded';
      } else if (version.status === 'live' && !candidate) {
        version.status = 'superseded';
      }
    }
    const instructions = instructionsAsset === null ? text(request.instructions) : null;
    const saved: StoredSkillVersion = {
      skillId,
      version: versions.reduce((latest, version) => Math.max(latest, version.version), 0) + 1,
      status: candidate ? 'candidate' : 'live',
      description: description!,
      instructions,
      instructionsHash: instructionsAsset?.hash ?? (instructions === null ? null : hashText(instructions)),
      instructionsAsset: instructionsAsset === null
        ? null
        : { assetId: instructionsAsset.assetId, version: instructionsAsset.version, hash: instructionsAsset.hash },
      tools: [...new Set(request.tools ?? [])],
      profile: text(request.profile),
      dataClassification: classification as AssistantDataClassification | null,
      acceptsFiles: request.acceptsFiles ?? false,
      ...storedFileLimits(request),
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
      this.agents.setDataSkill(agent.id, skillId, saved.description, live === undefined && request.startsOff === true);
    }
    // A classification that no profile permits never refuses the save; the skill reports it.
    return { status: 200, body: this.withWarnings(agent, toSkill(saved)) };
  }

  private candidate(method: string, agent: AdminAgent, skillId: string, action: string | undefined, body: unknown): MockResult {
    const versions = this.skills.get(key(agent.id, skillId)) ?? [];
    const candidate = versions.find(version => version.status === 'candidate');
    const live = versions.find(version => version.status === 'live');
    if (action === 'comparison' && method === 'GET') {
      if (!candidate || !live) {
        return failure('assistant-skill-candidate-not-found');
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
      return failure('assistant-validation', { version: ['required'] });
    }
    if (!candidate) {
      return failure('assistant-skill-candidate-not-found');
    }
    if (candidate.version !== request.version) {
      return failure('assistant-version-conflict');
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
    return { status: 200, body: this.withWarnings(agent, toSkill(candidate)) };
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
        reason: version.reason,
        instructionsAsset: version.instructionsAsset ?? null
      }));
  }

  private trigger(method: string, agent: AdminAgent, triggerId: string, body: unknown, query: URLSearchParams): MockResult {
    if (this.codeTriggers.some(trigger => trigger.triggerId === triggerId)) {
      return failure('assistant-trigger-in-code');
    }
    if (!this.isConfigured(agent)) {
      return failure('assistant-agent-not-configured');
    }
    const existing = this.triggers.get(triggerId);
    if (existing && this.ownerOf(existing) !== agent.id) {
      return failure('assistant-validation', { triggerId: ['invalid'] });
    }
    if (method === 'DELETE') {
      if (!existing) {
        return failure('assistant-trigger-not-found');
      }
      if (!versionMatches(query, existing.version)) {
        return failure('assistant-version-conflict');
      }
      // Out of effect; its versions stay as history.
      for (const version of this.triggerVersions.get(triggerId) ?? []) {
        version.status = version.status === 'live' ? 'superseded' : version.status === 'candidate' ? 'discarded' : version.status;
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
      // An event that names its object type up front fits only an agent that works on it.
      const subjectType = this.catalog.triggerEvents.find(item => item.name === text(request.event))?.subjectType;
      if (!errors['event'] && subjectType && !mockWorksOnCovers(agent, subjectType)) {
        errors['event'] = ['invalid'];
      }
    }
    if (schedule) {
      if ((text(request.cron) ?? '').split(' ').filter(field => field.length > 0).length < 5) {
        errors['cron'] = [text(request.cron) === null ? 'required' : 'invalid'];
      }
      if (text(request.subjectSet) !== null) {
        this.requireKnown(errors, 'subjectSet', request.subjectSet, this.catalog.subjectSets.map(item => item.name));
      } else if (!mockWorksOnCovers(agent, mockSystemSubjectType)) {
        // Without a subject set the schedule runs for the whole application, which only an agent
        // that works on it may do.
        errors['subjectSet'] = ['required'];
      }
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
    // The filters in the order chosen, each once; only an event trigger takes them.
    const filters = [...new Set((request.filters ?? []).map(name => (typeof name === 'string' ? name.trim() : '')))];
    if (filters.length > maxTriggerFilters || filters.some(name => name.length > 64 || !filterName.test(name)) || (schedule && filters.length > 0)) {
      errors['filters'] = ['invalid'];
    }
    // A new trigger has no live version to evaluate a candidate next to.
    if ((request.apply !== undefined && request.apply !== 'live' && request.apply !== 'shadow')
      || (request.apply === 'shadow' && (request.expectedVersion ?? null) === null)) {
      errors['apply'] = ['invalid'];
    }
    if (Object.keys(errors).length > 0) {
      return failure('assistant-validation', errors);
    }
    if (request.source === 'event') {
      const offered = (this.catalog.triggerEvents.find(item => item.name === text(request.event))?.filters ?? []).map(filter => filter.name);
      if (filters.some(name => !offered.includes(name))) {
        return failure('assistant-trigger-filter-not-found', { filters: ['not-found'] });
      }
    }
    if ((request.expectedVersion ?? null) !== (existing?.version ?? null)) {
      return failure('assistant-version-conflict');
    }

    const candidate = request.apply === 'shadow';
    const history = this.triggerVersions.get(triggerId) ?? [];
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
      // A candidate always fires in shadow; a new version keeps the trigger's mode.
      shadow: candidate || (existing?.shadow ?? true),
      version: history.reduce((latest, version) => Math.max(latest, version.version), existing?.version ?? 0) + 1,
      changedAt: this.now(),
      changedBy: 'current-user',
      reason: text(request.reason),
      inEffect: true,
      problem: null,
      filters: schedule ? [] : filters,
      createdAt: this.now(),
      createdBy: 'current-user'
    };
    for (const version of history) {
      if (version.status === 'candidate') {
        version.status = 'discarded';
      } else if (version.status === 'live' && !candidate) {
        version.status = 'superseded';
      }
    }
    if (existing && history.length === 0) {
      // A trigger of the scenario's preset starts its history with its live version.
      history.push({ ...withoutOwner(existing), status: candidate ? 'live' : 'superseded', agentId: agent.id });
    }
    this.triggerVersions.set(triggerId, [...history, { ...withoutOwner(saved), status: candidate ? 'candidate' : 'live', agentId: agent.id }]);
    if (candidate) {
      return { status: 200, body: this.candidateDto(agent, this.candidateOf(triggerId)!) };
    }
    this.triggers.set(triggerId, Object.assign(saved, { [ownerKey]: agent.id }));
    return { status: 200, body: withoutOwner(saved) };
  }

  /** `admin/agents/{id}/triggers/{triggerId}/candidate/{promote|discard|comparison}`. */
  private triggerCandidate(method: string, agent: AdminAgent, triggerId: string, action: string | undefined, body: unknown): MockResult {
    const live = this.triggers.get(triggerId);
    const candidate = live && this.ownerOf(live) === agent.id ? this.candidateOf(triggerId) : undefined;
    if (action === 'comparison' && method === 'GET') {
      if (!candidate || !live) {
        return failure('assistant-trigger-candidate-not-found');
      }
      const comparison: TriggerComparison = {
        agentId: agent.id,
        triggerId,
        from: candidate.createdAt ?? candidate.changedAt,
        to: this.now(),
        live: emptyStats(live.version),
        candidate: emptyStats(candidate.version),
        pairedEvents: 0,
        liveOnlyEvents: 0,
        candidateOnlyEvents: 0,
        tools: []
      };
      return { status: 200, body: comparison };
    }
    if ((action !== 'promote' && action !== 'discard') || method !== 'POST') {
      return { status: 405 };
    }

    const request = body as { version?: unknown; reason?: unknown } | undefined;
    if (typeof request?.version !== 'number') {
      return failure('assistant-validation', { version: ['required'] });
    }
    if (!candidate || !live) {
      return failure('assistant-trigger-candidate-not-found');
    }
    if (candidate.version !== request.version) {
      return failure('assistant-version-conflict');
    }
    if (action === 'discard') {
      candidate.status = 'discarded';
      return { status: 204 };
    }

    for (const version of this.triggerVersions.get(triggerId) ?? []) {
      if (version.status === 'live') {
        version.status = 'superseded';
      }
    }
    candidate.status = 'live';
    // The promoted version keeps working in the trigger's mode.
    const { status, agentId, ...definition } = candidate;
    const promoted: DataTrigger = { ...definition, shadow: live.shadow, changedAt: this.now(), changedBy: 'current-user' };
    candidate.shadow = live.shadow;
    this.triggers.set(triggerId, Object.assign(promoted, { [ownerKey]: agent.id }));
    return { status: 200, body: withoutOwner(promoted) };
  }

  /** Every version of a data trigger of the agent, newest first, with its definition. */
  private triggerHistory(agentId: string, triggerId: string): DataTriggerVersion[] {
    return [...(this.triggerVersions.get(triggerId) ?? [])]
      .filter(version => version.agentId === agentId)
      .sort((left, right) => right.version - left.version)
      .map(version => ({
        version: version.version,
        status: version.status,
        skillId: version.skillId,
        source: version.source,
        event: version.event,
        filters: [...(version.filters ?? [])],
        cron: version.cron,
        timeZone: version.timeZone,
        subjectSet: version.subjectSet,
        message: version.message,
        conflict: version.conflict,
        debounceSeconds: version.debounceSeconds,
        createdAt: version.createdAt ?? version.changedAt,
        createdBy: version.createdBy ?? version.changedBy,
        reason: version.reason
      }));
  }

  private candidateOf(triggerId: string): StoredTriggerVersion | undefined {
    return (this.triggerVersions.get(triggerId) ?? []).find(version => version.status === 'candidate');
  }

  /** A candidate as the server shows it: always in shadow, with its own problem. */
  private candidateDto(agent: AdminAgent, candidate: StoredTriggerVersion): DataTrigger {
    const { status, agentId, ...definition } = candidate;
    const problem = this.triggerProblem(agent, definition);
    return { ...definition, shadow: true, inEffect: problem === null, problem };
  }

  private triggerShadow(method: string, agent: AdminAgent, triggerId: string, body: unknown): MockResult {
    if (method !== 'POST') {
      return { status: 405 };
    }
    const existing = this.triggers.get(triggerId);
    if (!existing || this.ownerOf(existing) !== agent.id) {
      return failure('assistant-trigger-not-found');
    }
    const request = body as { mode?: unknown; reason?: unknown } | undefined;
    if (request?.mode !== 'shadow' && request?.mode !== 'live') {
      return failure('assistant-validation', { mode: [request?.mode === undefined ? 'required' : 'invalid'] });
    }
    if (typeof request.reason === 'string' && request.reason.length > maxReason) {
      return failure('assistant-validation', { reason: ['too-long'] });
    }

    existing.shadow = request.mode === 'shadow';
    existing.changedAt = this.now();
    existing.changedBy = 'current-user';
    existing.reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    return { status: 200, body: withoutOwner(existing) };
  }

  /**
   * The ids of the variant's data skills that would move to `live` and the failure of one that cannot,
   * as a promotion with `moveSkills` checks them before anything changes: a skill the live agent has
   * in code stays (`assistant-skill-in-code`).
   */
  skillsToMove(variantId: string, live: AdminAgent): { skillIds: string[]; failure: MockResult | null } {
    const skillIds = this.liveSkills(variantId).map(skill => skill.skillId);
    const inCode = (live.skills ?? []).some(skill => skillIds.includes(skill.skillId) && !this.skills.has(key(live.id, skill.skillId)));
    return { skillIds, failure: inCode ? failure('assistant-skill-in-code') : null };
  }

  /**
   * Moves the variant's data skills to `live` as a promotion does: each becomes the next live version
   * of the live agent's skill with the same id, or a new skill, which starts in shadow. Answers the ids
   * that moved, in order, or the failure of the first save.
   */
  moveSkills(variantId: string, live: AdminAgent, reason: string | null): { moved: string[]; failure: MockResult | null } {
    const moved: string[] = [];
    for (const skill of this.liveSkills(variantId)) {
      const current = (this.skills.get(key(live.id, skill.skillId)) ?? []).find(version => version.status === 'live');
      const request: DataSkillRequest = {
        description: skill.description,
        instructions: skill.instructionsAsset ? null : skill.instructions,
        tools: [...skill.tools],
        profile: skill.profile,
        ruleHandler: skill.ruleHandler,
        maxTokensPerRun: skill.maxTokensPerRun,
        dailyToolCalls: skill.dailyToolCalls,
        dailyTokens: skill.dailyTokens,
        approvalPolicy: skill.approvalPolicy,
        expectedVersion: current?.version ?? null,
        reason,
        ...(current ? { apply: 'live' as const } : {}),
        dataClassification: skill.dataClassification ?? null,
        acceptsFiles: skill.acceptsFiles ?? false,
        maxFileBytes: skill.maxFileBytes ?? null,
        maxFilePages: skill.maxFilePages ?? null,
        maxFilesPerTurn: skill.maxFilesPerTurn ?? null,
        maxFileTokensPerTurn: skill.maxFileTokensPerTurn ?? null,
        instructionsAsset: skill.instructionsAsset ?? null
      };
      const saved = this.skill('PUT', live, skill.skillId, request, new URLSearchParams());
      if (saved.status !== 200) {
        return { moved, failure: saved };
      }
      moved.push(skill.skillId);
    }
    return { moved, failure: null };
  }

  /** An agent in code has a run configuration; one created in the administration once it has run settings. */
  isConfigured(agent: AdminAgent): boolean {
    return agent.source === 'code' || this.settings.has(agent.id);
  }

  private skillIds(agent: AdminAgent): string[] {
    return [...(agent.skills ?? []).map(skill => skill.skillId), ...this.liveSkills(agent.id).map(skill => skill.skillId)];
  }

  private liveSkills(agentId: string): DataSkill[] {
    const result: DataSkill[] = [];
    const agent = this.agents.find(agentId);
    for (const [stored, versions] of this.skills) {
      if (!stored.startsWith(`${agentId}/`)) {
        continue;
      }
      const live = versions.find(version => version.status === 'live');
      if (live) {
        const candidate = versions.find(version => version.status === 'candidate');
        result.push({
          ...this.withWarnings(agent, toSkill(live)),
          candidate: candidate ? this.withWarnings(agent, toSkill(candidate)) : null
        });
      }
    }
    return result.sort((left, right) => left.skillId.localeCompare(right.skillId));
  }

  /**
   * The skill with the warnings the server reports for it: its classification, or else its agent's,
   * that its profile, or else its agent's, does not permit.
   */
  private withWarnings(agent: AdminAgent | undefined, skill: DataSkill): DataSkill {
    // A version whose asset no longer resolves to its hash is out of effect, as after the server's load.
    const assetProblem = skill.instructionsAsset ? this.instructionAssets.problemOf(skill.instructionsAsset) : null;
    return {
      ...skill,
      ...(assetProblem !== null ? { inEffect: false, problem: assetProblem } : {}),
      warnings: mockClassificationWarnings(
        this.profileClassifications,
        skill.profile ?? agent?.profile,
        skill.dataClassification ?? agent?.dataClassification)
    };
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

  /**
   * Lower file limits as the server checks them: each a whole number from 1 to the application's
   * limit, and only next to `acceptsFiles`.
   */
  private requireFileLimits(errors: Record<string, string[]>, acceptsFiles: boolean, request: LowerFileLimits): void {
    const application = this.catalog.fileLimits ?? defaultFileLimits;
    for (const field of fileLimitFields) {
      const value = request[field];
      if (value !== null && value !== undefined && (!acceptsFiles || !inRange(value, 1, application[field]))) {
        errors[field] = ['invalid'];
      }
    }
  }
}

/** The lower file limits a save stores, `null` where the request keeps the application's. */
function storedFileLimits(request: LowerFileLimits): Required<LowerFileLimits> {
  return {
    maxFileBytes: request.maxFileBytes ?? null,
    maxFilePages: request.maxFilePages ?? null,
    maxFilesPerTurn: request.maxFilesPerTurn ?? null,
    maxFileTokensPerTurn: request.maxFileTokensPerTurn ?? null
  };
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
