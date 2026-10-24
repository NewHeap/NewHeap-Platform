import {
  AdminAgent,
  AgentAccountability,
  ApprovalPolicySummary,
  CodeConsultation,
  ConsultationCeiling,
  ConsultationSubjectRole,
  DataConsultation,
  DataConsultationRequest,
  NhAssistantConsultationRoute,
  RunConfigurationProblem,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { NhAssistantMockAdminAgent, NhAssistantMockConsultationCeiling } from './nh-assistant-mock.models';
import { NhAssistantMockResult, mockFailure as failure, mockHashText as hashText, mockMaxStateReason as maxReason, mockSnapshot } from './nh-assistant-mock-admin-shared';
import { mockSystemSubjectType, mockWorksOnCovers } from './nh-assistant-mock-works-on';

/** What the consultations read of the mock's agents, their run configuration and the run catalog. */
export interface NhAssistantMockConsultationRules {
  find(agentId: string): AdminAgent | undefined;
  /** Every agent, to tell which of them select a consultation's tool. */
  all(): AdminAgent[];
  /** The subject types with an access resolver: the scenario's types and the whole application. */
  knownSubjectTypes(): string[];
  /** An agent in code has a run configuration; one created in the administration once it has run settings. */
  isConfigured(agent: AdminAgent): boolean;
  /** Whom the agent answers to, from its run settings as data or its code; `null` while it has none. */
  accountabilityOf(agent: AdminAgent): AgentAccountability | null;
  /** The agent's own approval policy; `null` for the platform's default. */
  approvalPolicyOf(agent: AdminAgent): string | null;
  approvalPolicies(): string[];
  approvalPolicySummaries(): ApprovalPolicySummary[];
  /** The policies administrators may choose as the asker policy, by name. */
  agentPolicyNames(): string[];
}

/** A consultation defined as data as the mock stores it. */
interface StoredConsultation {
  agentId: string;
  subjectType: string;
  version: number;
  description: string;
  instructions: string | null;
  minimumRole: ConsultationSubjectRole;
  askerPolicy: string | null;
  routes: NhAssistantConsultationRoute[];
  proposalRole: ConsultationSubjectRole;
  proposalPolicy: string | null;
  /** The tool selection as saved; `null` offers every tool of the agent. */
  toolSelectors: string[] | null;
  maxToolCalls: number;
  timeoutSeconds: number;
  maxAnswerChars: number;
  maxHistoryMessages: number;
  allowRequests: boolean;
  exposeToMcp: boolean;
  dataClassification: DataConsultation['dataClassification'];
  /** Switched off: not offered until an administrator switches it on. */
  off: boolean;
  createdAt: string;
  createdBy: string;
  changedAt: string;
  changedBy: string;
  reason: string | null;
}

/** What is in effect of a stored consultation: nothing with the problem that keeps it out, or the parts that still fit. */
interface ConsultationCheck {
  inEffect: boolean;
  problems: string[];
  routes: NhAssistantConsultationRoute[];
  allowRequests: boolean;
  /** The selectors the agent's own still cover; `null` offers every tool, empty none. */
  toolSelectors: string[] | null;
}

/** The bounds of `AllowConsultation`, within which a ceiling may only lower the maxima. */
const bounds = {
  toolCalls: { min: 0, max: 32, initial: 4 },
  timeoutSeconds: { min: 1, max: 600, initial: 60 },
  answerChars: { min: 200, max: 32_000, initial: 4_000 },
  history: { min: 0, max: 200, initial: 20 }
} as const;

const maxDescription = 1_000;
const maxInstructions = 4_000;
const maxConsultations = 20;
const roles: readonly ConsultationSubjectRole[] = ['reader', 'participant', 'approver'];
const allRoutes: readonly NhAssistantConsultationRoute[] = ['asker-decides', 'subject-agent-rights', 'subject-asker-rights'];
const classifications = ['public', 'internal', 'confidential', 'restricted'];
const subjectTypePattern = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
/** A tool id or a prefix ending in `.*`, as an agent's tool selectors are. */
const selectorPattern = /^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)*(?:\.\*)?$/;

/**
 * Consultations of the mock API's agents: those in code from the scenario, read-only, and those
 * defined as data, saved and removed as on the server within the ceilings in code
 * (`consultationsAsData` for the application, `consultableAsData` for an agent in code). A save
 * checks a consultation as the server does and refuses with the same codes and field errors; every
 * read checks it again, so a consultation that no longer fits, for example after the run settings
 * changed whom the agent answers to or the agent's own tools no longer cover its tool selection,
 * stays with its problems. Data consultations in effect generate their consult and request tools in
 * the tool list and count for the agent's route switches. A new consultation starts off when its
 * request or the ceiling says so (`startsOff`); one that is off is checked as every read checks it but
 * offers nothing until `POST .../state` switches it on.
 */
export class NhAssistantMockConsultations {
  private readonly stored = new Map<string, StoredConsultation>();
  private readonly codeAllowed = new Map<string, Partial<Record<NhAssistantConsultationRoute, string[]>>>();
  private readonly codeDescriptions = new Map<string, Record<string, string>>();
  private readonly codeTools = new Map<string, Record<string, string[]>>();
  private readonly agentCeilings = new Map<string, NhAssistantMockConsultationCeiling>();

  /** The application's ceiling; `null` while consultations exist in code only. */
  readonly applicationCeiling: ConsultationCeiling | null;

  constructor(
    application: NhAssistantMockConsultationCeiling | undefined,
    agents: readonly NhAssistantMockAdminAgent[],
    private readonly rules: NhAssistantMockConsultationRules,
    private readonly now: () => string
  ) {
    this.applicationCeiling = application ? ceilingOf(application) : null;
    for (const agent of agents) {
      if (agent.source !== 'code') {
        continue;
      }
      if (agent.consultations !== undefined || agent.consultationDescriptions !== undefined) {
        this.codeAllowed.set(agent.id, agent.consultations ?? {});
        this.codeDescriptions.set(agent.id, agent.consultationDescriptions ?? {});
        this.codeTools.set(agent.id, agent.consultationTools ?? {});
      }
      if (agent.consultableAsData !== undefined) {
        this.agentCeilings.set(agent.id, agent.consultableAsData);
      }
    }
  }

  /**
   * The ceiling of the agent: the application's for an agent created in the administration, the
   * application's and its own together for a consultable agent in code, none otherwise.
   */
  ceilingOf(agent: AdminAgent): ConsultationCeiling | null {
    const application = this.applicationCeiling;
    if (!application) {
      return null;
    }
    if (agent.source === 'admin') {
      return application;
    }
    const own = this.agentCeilings.get(agent.id);
    if (!own) {
      return null;
    }
    const narrowed = ceilingOf(own);
    return {
      ...application,
      routes: application.routes.filter(route => narrowed.routes.includes(route)),
      allowRequests: application.allowRequests && narrowed.allowRequests,
      allowMcp: application.allowMcp && narrowed.allowMcp,
      maxToolCalls: Math.min(application.maxToolCalls, narrowed.maxToolCalls),
      maxTimeoutSeconds: Math.min(application.maxTimeoutSeconds, narrowed.maxTimeoutSeconds),
      maxAnswerChars: Math.min(application.maxAnswerChars, narrowed.maxAnswerChars),
      maxHistoryMessages: Math.min(application.maxHistoryMessages, narrowed.maxHistoryMessages),
      ...(application.startsOff === true || narrowed.startsOff === true ? { startsOff: true } : {})
    };
  }

  /** The agent's consultations in code, by subject type. */
  codeConsultations(agent: AdminAgent): CodeConsultation[] {
    const allowed = this.codeAllowed.get(agent.id);
    if (!allowed) {
      return [];
    }
    const descriptions = this.codeDescriptions.get(agent.id) ?? {};
    const tools = this.codeTools.get(agent.id) ?? {};
    const types = new Set([...Object.values(allowed).flat(), ...Object.keys(descriptions)]);
    return [...types].sort().map(subjectType => {
      const consultToolId = consultToolOf(agent.id, subjectType);
      return {
        subjectType,
        description: descriptions[subjectType] ?? null,
        allowedRoutes: allRoutes.filter(route => (allowed[route] ?? []).includes(subjectType)),
        allowRequests: false,
        exposure: ['local', 'agent'],
        consultToolId,
        requestToolId: null,
        askingAgents: this.askersOf(agent.id, consultToolId),
        toolSelectors: tools[subjectType] ? [...tools[subjectType]] : null
      };
    });
  }

  /** The agent's consultations defined as data, by subject type, each with what is in effect of it. */
  dataConsultations(agent: AdminAgent): DataConsultation[] {
    return this.storedOf(agent.id).map(stored => this.toConsultation(agent, stored));
  }

  /** The problems of kind `consultation` of the agent's data consultations. */
  problems(agent: AdminAgent): RunConfigurationProblem[] {
    return this.storedOf(agent.id).flatMap(stored => this.check(agent, stored).problems.map(code => ({
      agentId: agent.id,
      kind: 'consultation' as const,
      id: stored.subjectType,
      code
    })));
  }

  /** The agent's data consultations in effect with their routes in effect, for its route switches. */
  inEffect(agentId: string): { subjectType: string; routes: NhAssistantConsultationRoute[] }[] {
    const agent = this.rules.find(agentId);
    if (!agent) {
      return [];
    }
    return this.storedOf(agentId)
      .map(stored => ({ subjectType: stored.subjectType, check: this.check(agent, stored) }))
      .filter(item => item.check.inEffect)
      .map(item => ({ subjectType: item.subjectType, routes: item.check.routes }));
  }

  /** Whether data consultations belong to the agent, which keeps its run settings. */
  has(agentId: string): boolean {
    return this.storedOf(agentId).length > 0;
  }

  /** The generated tools of the data consultations in effect, as `GET admin/tools` lists them. */
  tools(): ToolCatalogEntry[] {
    const tools: ToolCatalogEntry[] = [];
    for (const stored of [...this.stored.values()].sort((left, right) => key(left.agentId, left.subjectType).localeCompare(key(right.agentId, right.subjectType)))) {
      const agent = this.rules.find(stored.agentId);
      const check = agent ? this.check(agent, stored) : null;
      if (!check?.inEffect) {
        continue;
      }
      tools.push({ id: consultToolOf(stored.agentId, stored.subjectType), source: 'local', effect: 'read-only', description: stored.description, subjectType: null });
      if (check.allowRequests) {
        tools.push({ id: requestToolOf(stored.agentId, stored.subjectType), source: 'local', effect: 'idempotent-mutation', description: stored.description, subjectType: null });
      }
    }
    return tools;
  }

  /** `PUT` and `DELETE admin/agents/{id}/consultations/{subjectType}`, and `POST .../state`. */
  /**
   * A copy of the consultations defined as data; the returned function puts it back, for an agent package import that fails
   * as a whole and for its preview.
   */
  snapshot(): () => void {
    return mockSnapshot(this.stored);
  }

  handle(method: string, agent: AdminAgent, subjectType: string, body: unknown, query: URLSearchParams, action?: string): NhAssistantMockResult {
    if (action === 'state') {
      return method === 'POST' ? this.setState(agent, subjectType, body) : { status: 405 };
    }
    if (method === 'DELETE') {
      return this.remove(agent, subjectType, query);
    }
    return method === 'PUT' ? this.save(agent, subjectType, body) : { status: 405 };
  }

  private save(agent: AdminAgent, subjectType: string, body: unknown): NhAssistantMockResult {
    const request = (body ?? {}) as DataConsultationRequest;
    const reason = text(request.reason);
    if (reason !== null && reason.length > maxReason) {
      return failure('assistant-validation', { reason: ['too-long'] });
    }
    const ceiling = this.ceilingOf(agent);
    if (!ceiling) {
      return failure('assistant-consultations-not-allowed');
    }
    if (!this.rules.isConfigured(agent)) {
      return failure('assistant-agent-not-configured');
    }
    if (this.inCode(agent.id, subjectType)) {
      return failure('assistant-consultation-in-code');
    }

    // A new consultation starts with the defaults of code, capped by the ceiling.
    const description = text(request.description);
    const instructions = text(request.instructions);
    const minimumRole = (text(request.minimumRole) ?? 'reader') as ConsultationSubjectRole;
    const proposalRole = (text(request.proposalRole) ?? 'participant') as ConsultationSubjectRole;
    const routes = (request.routes ?? []).map(route => (typeof route === 'string' ? route.trim() : '')) as NhAssistantConsultationRoute[];
    // No selection, or an empty one, offers every tool of the agent.
    const toolSelectors = Array.isArray(request.toolSelectors) && request.toolSelectors.length > 0
      ? request.toolSelectors.map(selector => (typeof selector === 'string' ? selector.trim() : ''))
      : null;
    const classification = text(request.dataClassification);
    const limits = {
      maxToolCalls: request.maxToolCalls ?? Math.min(bounds.toolCalls.initial, ceiling.maxToolCalls),
      timeoutSeconds: request.timeoutSeconds ?? Math.min(bounds.timeoutSeconds.initial, ceiling.maxTimeoutSeconds),
      maxAnswerChars: request.maxAnswerChars ?? Math.min(bounds.answerChars.initial, ceiling.maxAnswerChars),
      maxHistoryMessages: request.maxHistoryMessages ?? Math.min(bounds.history.initial, ceiling.maxHistoryMessages)
    };
    const errors: Record<string, string[]> = {};
    const add = (field: string, code: string): void => {
      errors[field] = [...(errors[field] ?? []), code];
    };
    if (!subjectTypePattern.test(subjectType)) {
      add('subjectType', 'invalid');
    }
    if (description === null) {
      add('description', 'required');
    } else if (description.length > maxDescription) {
      add('description', 'too-long');
    }
    if ((instructions ?? '').length > maxInstructions) {
      add('instructions', 'too-long');
    }
    if (!roles.includes(minimumRole)) {
      add('minimumRole', 'invalid');
    }
    if (!roles.includes(proposalRole)) {
      add('proposalRole', 'invalid');
    }
    if (!validRoutes(routes)) {
      add('routes', 'invalid');
    }
    if (toolSelectors !== null && !validSelectors(toolSelectors)) {
      add('toolSelectors', 'invalid');
    }
    if (!within(limits.maxToolCalls, bounds.toolCalls)
      || !within(limits.timeoutSeconds, bounds.timeoutSeconds)
      || !within(limits.maxAnswerChars, bounds.answerChars)
      || !within(limits.maxHistoryMessages, bounds.history)) {
      add('limits', 'invalid');
    }
    if (classification !== null && !classifications.includes(classification)) {
      add('dataClassification', 'invalid');
    }
    // Only a new consultation starts off; one that exists keeps whether it is on.
    if (request.startsOff === true && request.expectedVersion != null) {
      add('startsOff', 'invalid');
    }
    if (Object.keys(errors).length > 0) {
      return failure('assistant-validation', errors);
    }

    const existing = this.stored.get(key(agent.id, subjectType));
    const now = this.now();
    const row: StoredConsultation = {
      agentId: agent.id,
      subjectType,
      version: (existing?.version ?? 0) + 1,
      description: description!,
      instructions,
      minimumRole,
      askerPolicy: text(request.askerPolicy),
      routes,
      proposalRole,
      proposalPolicy: text(request.proposalPolicy),
      toolSelectors,
      ...limits,
      allowRequests: request.allowRequests === true,
      exposeToMcp: request.exposeToMcp === true,
      dataClassification: classification as DataConsultation['dataClassification'],
      off: existing ? existing.off : request.startsOff === true || ceiling.startsOff === true,
      createdAt: existing?.createdAt ?? now,
      createdBy: existing?.createdBy ?? 'current-user',
      changedAt: now,
      changedBy: 'current-user',
      reason
    };

    // The same check as every read, refused here instead of degraded.
    const check = this.check(agent, row);
    if (check.problems.length > 0) {
      return fieldFailure(check.problems);
    }
    if (request.expectedVersion == null && !existing && this.storedOf(agent.id).length >= maxConsultations) {
      return failure('assistant-configuration-limit');
    }
    if ((request.expectedVersion ?? null) !== (existing?.version ?? null)) {
      return failure('assistant-version-conflict');
    }

    this.stored.set(key(agent.id, subjectType), row);
    return { status: 200, body: this.toConsultation(agent, row) };
  }

  /** `POST admin/agents/{id}/consultations/{subjectType}/state`: on or off, without a new version. */
  private setState(agent: AdminAgent, subjectType: string, body: unknown): NhAssistantMockResult {
    const request = body as { state?: unknown; reason?: unknown } | undefined;
    if (request?.state !== 'on' && request?.state !== 'off') {
      return failure('assistant-validation', { state: [request?.state === undefined ? 'required' : 'invalid'] });
    }
    if (typeof request.reason === 'string' && request.reason.trim().length > maxReason) {
      return failure('assistant-validation', { reason: ['too-long'] });
    }
    if (this.inCode(agent.id, subjectType)) {
      return failure('assistant-consultation-in-code');
    }
    const existing = this.stored.get(key(agent.id, subjectType));
    if (!existing) {
      return failure('assistant-consultation-not-found');
    }

    const off = request.state === 'off';
    if (existing.off !== off) {
      existing.off = off;
      existing.changedAt = this.now();
      existing.changedBy = 'current-user';
      existing.reason = typeof request.reason === 'string' && request.reason.trim().length > 0 ? request.reason.trim() : null;
    }
    return { status: 200, body: this.toConsultation(agent, existing) };
  }

  private remove(agent: AdminAgent, subjectType: string, query: URLSearchParams): NhAssistantMockResult {
    const reason = query.get('reason');
    if (reason !== null && reason.trim().length > maxReason) {
      return failure('assistant-validation', { reason: ['too-long'] });
    }
    if (this.inCode(agent.id, subjectType)) {
      return failure('assistant-consultation-in-code');
    }
    const existing = this.stored.get(key(agent.id, subjectType));
    if (!existing) {
      return failure('assistant-consultation-not-found');
    }
    const expected = query.get('expectedVersion');
    if (expected !== null && Number(expected) !== existing.version) {
      return failure('assistant-version-conflict');
    }

    this.stored.delete(key(agent.id, subjectType));
    return { status: 204 };
  }

  /**
   * Checks a consultation against the ceilings and the agent as the server does on save and on every
   * load: out of effect when the agent may get none, the type is unknown or not worked on or the
   * asker policy is no longer listed; otherwise without the routes, requests and MCP exposure that
   * no longer fit and with limits capped at the ceiling, each with a problem.
   */
  private check(agent: AdminAgent, row: StoredConsultation): ConsultationCheck {
    const out = (problem: string): ConsultationCheck => ({ inEffect: false, problems: [problem], routes: [], allowRequests: false, toolSelectors: [] });
    const ceiling = this.ceilingOf(agent);
    if (!ceiling) {
      return out('not-consultable');
    }
    if (!this.rules.knownSubjectTypes().includes(row.subjectType)) {
      return out('unknown-subject-type');
    }
    if (!mockWorksOnCovers(agent, row.subjectType)) {
      return out('not-worked-on');
    }
    if (this.inCode(agent.id, row.subjectType)) {
      return out('conflicts-with-code');
    }
    if (row.askerPolicy !== null && !this.rules.agentPolicyNames().includes(row.askerPolicy)) {
      return out('unknown-agent-policy');
    }

    const problems: string[] = [];
    const addOnce = (problem: string): void => {
      if (!problems.includes(problem)) {
        problems.push(problem);
      }
    };
    const accountability = this.rules.accountabilityOf(agent);
    let kept: NhAssistantConsultationRoute[] = [];
    for (const route of row.routes) {
      const agentRights = route === 'subject-agent-rights';
      if (!ceiling.routes.includes(route) || (agentRights && row.subjectType === mockSystemSubjectType)) {
        addOnce('route-not-allowed');
        continue;
      }
      if (agentRights && accountability === 'subject-responsible') {
        addOnce('agent-answers-to-person');
        continue;
      }
      // An agent created here that answers for itself has no grants; one in code is assumed to have them.
      if (agentRights && agent.source === 'admin' && accountability === 'agent') {
        addOnce('agent-has-no-rights');
        continue;
      }
      kept.push(route);
    }

    if (row.proposalPolicy !== null && !this.rules.approvalPolicies().includes(row.proposalPolicy)) {
      // Without its policy nobody knows who decides a proposal: the consultation only answers.
      addOnce('unknown-policy');
      kept = [];
    }
    const policies = [row.proposalPolicy, this.rules.approvalPolicyOf(agent)].filter((policy): policy is string => policy !== null);
    if (kept.includes('subject-agent-rights') && policies.some(policy => this.namesOnlyTheInitiator(policy))) {
      addOnce('no-approver');
      kept = kept.filter(route => route !== 'subject-agent-rights');
    }
    let allowRequests = row.allowRequests;
    if (allowRequests && !ceiling.allowRequests) {
      problems.push('requests-not-allowed');
      allowRequests = false;
    }
    if (row.exposeToMcp && !ceiling.allowMcp) {
      problems.push('mcp-not-allowed');
    }
    if (row.maxToolCalls > ceiling.maxToolCalls
      || row.timeoutSeconds > ceiling.maxTimeoutSeconds
      || row.maxAnswerChars > ceiling.maxAnswerChars
      || row.maxHistoryMessages > ceiling.maxHistoryMessages) {
      problems.push('limit-above-ceiling');
    }
    // The selection only narrows the agent's own tools; one they no longer cover drops, and a
    // selection with none left offers no tools, never every tool of the agent.
    let toolSelectors: string[] | null = null;
    if (row.toolSelectors !== null) {
      toolSelectors = row.toolSelectors.filter(selector => agent.toolSelectors.some(own => selectorCovers(own, selector)));
      if (toolSelectors.length < row.toolSelectors.length) {
        problems.push('tools-not-allowed');
      }
    }
    // Off, it is checked as every read checks it, and offers nothing until it is switched on.
    return row.off
      ? { inEffect: false, problems, routes: [], allowRequests: false, toolSelectors: [] }
      : { inEffect: true, problems, routes: kept, allowRequests, toolSelectors };
  }

  private namesOnlyTheInitiator(policy: string): boolean {
    const summary = this.rules.approvalPolicySummaries().find(item => item.name === policy);
    return summary !== undefined && summary.approvers.length > 0 && summary.approvers.every(approver => approver.kind === 'initiator');
  }

  private toConsultation(agent: AdminAgent, row: StoredConsultation): DataConsultation {
    const check = this.check(agent, row);
    const consultToolId = consultToolOf(agent.id, row.subjectType);
    const { agentId, ...stored } = row;
    return {
      ...stored,
      routes: [...row.routes],
      toolSelectors: row.toolSelectors ? [...row.toolSelectors] : null,
      toolSelectorsInEffect: check.toolSelectors ? [...check.toolSelectors] : null,
      instructionsHash: row.instructions === null ? null : hashText(row.instructions),
      inEffect: check.inEffect,
      problem: check.problems[0] ?? null,
      problems: check.problems,
      routesInEffect: check.routes,
      consultToolId,
      requestToolId: check.allowRequests ? requestToolOf(agent.id, row.subjectType) : null,
      askingAgents: this.askersOf(agent.id, consultToolId)
    };
  }

  /** The other agents whose tool selectors select the tool now, by id: the consultation's second key. */
  private askersOf(agentId: string, toolId: string): string[] {
    return this.rules.all()
      .filter(agent => agent.id !== agentId && agent.toolSelectors.some(selector => covers(selector, toolId)))
      .map(agent => agent.id)
      .sort();
  }

  private inCode(agentId: string, subjectType: string): boolean {
    const allowed = this.codeAllowed.get(agentId);
    return allowed !== undefined
      && (Object.values(allowed).some(types => types?.includes(subjectType)) || subjectType in (this.codeDescriptions.get(agentId) ?? {}));
  }

  private storedOf(agentId: string): StoredConsultation[] {
    return [...this.stored.values()]
      .filter(stored => stored.agentId === agentId)
      .sort((left, right) => left.subjectType.localeCompare(right.subjectType));
  }
}

/** A ceiling of the scenario with the defaults of the ceiling builder. */
function ceilingOf(ceiling: NhAssistantMockConsultationCeiling): ConsultationCeiling {
  return {
    routes: [...(ceiling.routes ?? [])],
    allowRequests: ceiling.allowRequests ?? false,
    allowMcp: ceiling.allowMcp ?? false,
    maxToolCalls: ceiling.maxToolCalls ?? bounds.toolCalls.max,
    maxTimeoutSeconds: ceiling.maxTimeoutSeconds ?? bounds.timeoutSeconds.max,
    maxAnswerChars: ceiling.maxAnswerChars ?? bounds.answerChars.max,
    maxHistoryMessages: ceiling.maxHistoryMessages ?? bounds.history.max,
    maxConsultations,
    maxInstructionsLength: maxInstructions,
    // Reported only when the application lets new consultations start off, as older servers omit it.
    ...(ceiling.startsOff === true ? { startsOff: true } : {})
  };
}

/** The field error of each problem a consultation would have, as the server maps them on save. */
function fieldFailure(problems: readonly string[]): NhAssistantMockResult {
  if (problems.includes('not-consultable')) {
    return failure('assistant-consultations-not-allowed');
  }
  if (problems.includes('conflicts-with-code')) {
    return failure('assistant-consultation-in-code');
  }
  const fields: Record<string, [string, string]> = {
    'unknown-subject-type': ['subjectType', 'not-found'],
    'not-worked-on': ['subjectType', 'invalid'],
    'unknown-agent-policy': ['askerPolicy', 'not-found'],
    'route-not-allowed': ['routes', 'not-allowed'],
    'agent-answers-to-person': ['routes', 'invalid'],
    'agent-has-no-rights': ['routes', 'invalid'],
    'unknown-policy': ['proposalPolicy', 'not-found'],
    'no-approver': ['proposalPolicy', 'invalid'],
    'limit-above-ceiling': ['limits', 'not-allowed'],
    'requests-not-allowed': ['allowRequests', 'not-allowed'],
    'mcp-not-allowed': ['exposeToMcp', 'not-allowed'],
    'tools-not-allowed': ['toolSelectors', 'not-allowed']
  };
  const errors: Record<string, string[]> = {};
  for (const problem of problems) {
    const [field, code] = fields[problem] ?? ['subjectType', 'invalid'];
    errors[field] = [...(errors[field] ?? []), code];
  }
  return failure('assistant-validation', errors);
}

/** Known routes, each once, and at most one of the two routes at the subject. */
function validRoutes(routes: readonly string[]): boolean {
  return routes.every(route => (allRoutes as readonly string[]).includes(route))
    && new Set(routes).size === routes.length
    && !(routes.includes('subject-agent-rights') && routes.includes('subject-asker-rights'));
}

/** Valid selectors, each once. */
function validSelectors(selectors: readonly string[]): boolean {
  return selectors.every(selector => selector.length <= 128 && selectorPattern.test(selector))
    && new Set(selectors).size === selectors.length;
}

/** The agent's selector covers a consultation's selector: the same tool, or a prefix that includes it. */
function selectorCovers(agentSelector: string, selector: string): boolean {
  return agentSelector === selector || (agentSelector.endsWith('.*') && selector.startsWith(agentSelector.slice(0, -1)));
}

function within(value: number, range: { min: number; max: number }): boolean {
  return Number.isInteger(value) && value >= range.min && value <= range.max;
}

function consultToolOf(agentId: string, subjectType: string): string {
  return `assistant.consult.${agentId}.${subjectType}`;
}

function requestToolOf(agentId: string, subjectType: string): string {
  return `assistant.request.${agentId}.${subjectType}`;
}

/** The agent's selector covers the tool: the same tool, or a pattern with `*` that matches it. */
function covers(selector: string, toolId: string): boolean {
  const pattern = selector.split('*').map(part => part.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*');
  return new RegExp(`^${pattern}$`).test(toolId);
}

function key(agentId: string, subjectType: string): string {
  return `${agentId}/${subjectType}`;
}

function text(value: string | null | undefined): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
}
