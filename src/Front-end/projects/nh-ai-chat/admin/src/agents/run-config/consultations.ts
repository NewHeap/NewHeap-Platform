import {
  AgentAccountability,
  AgentRunConfiguration,
  AgentSubjectType,
  ApprovalPolicySummary,
  CodeConsultation,
  ConsultationCeiling,
  ConsultationSubjectRole,
  DataConsultation,
  NhAssistantConsultationRoute,
  RunCatalog,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { ToolChoice, WorksOnStatement, systemSubjectType, toolChoices, worksOnCovers } from './run-config-shared';

/**
 * Consultations in the run configuration (AC20): other assistants consult an agent about a subject
 * type, through a consultation in code (read only) or one defined as data within the agent's ceiling
 * in code. These helpers decide what the Consultations section, its form and the flow view show.
 */

/** The routes of a consultation's proposals, in the order the form offers them. */
export const consultationRoutes: readonly NhAssistantConsultationRoute[] = ['asker-decides', 'subject-asker-rights', 'subject-agent-rights'];

/** The two routes at the subject, of which a consultation uses one at most. */
export const subjectRoutes: readonly NhAssistantConsultationRoute[] = ['subject-asker-rights', 'subject-agent-rights'];

/** The roles a person may need on the subject, from the lowest. */
export const consultationRoles: readonly ConsultationSubjectRole[] = ['reader', 'participant', 'approver'];

/** A limit of a consultation: its field, the bounds of `AllowConsultation` and the default of code. */
export interface ConsultationLimit {
  field: 'maxToolCalls' | 'timeoutSeconds' | 'maxAnswerChars' | 'maxHistoryMessages';
  /** The key of its name under `nh-assistant.admin.run-config.consultations.limits`. */
  key: string;
  min: number;
  max: number;
  initial: number;
  /** The ceiling's maximum of this limit. */
  ceiling: (ceiling: ConsultationCeiling) => number;
}

/** The limits a consultation defined as data sets, within the bounds of code and the ceiling. */
export const consultationLimits: readonly ConsultationLimit[] = [
  { field: 'maxToolCalls', key: 'tool-calls', min: 0, max: 32, initial: 4, ceiling: ceiling => ceiling.maxToolCalls },
  { field: 'timeoutSeconds', key: 'timeout-seconds', min: 1, max: 600, initial: 60, ceiling: ceiling => ceiling.maxTimeoutSeconds },
  { field: 'maxAnswerChars', key: 'answer-chars', min: 200, max: 32_000, initial: 4_000, ceiling: ceiling => ceiling.maxAnswerChars },
  { field: 'maxHistoryMessages', key: 'history-messages', min: 0, max: 200, initial: 20, ceiling: ceiling => ceiling.maxHistoryMessages }
];

/** The longest description of a consultation defined as data. */
export const maxConsultationDescription = 1_000;

/** One consultation as the section and the flow list it: in code or defined as data. */
export interface ConsultationRow {
  subjectType: string;
  origin: 'code' | 'data';
  description: string | null;
  /** The routes in effect: those code allows, or those of the data that still fit. */
  routes: NhAssistantConsultationRoute[];
  /** It generates the request tool now. */
  requests: boolean;
  /** MCP clients see its tools. */
  mcp: boolean;
  consultToolId: string;
  /** The agents whose tool selectors select its consult tool now. */
  askingAgents: string[];
  inEffect: boolean;
  problems: string[];
  /**
   * The tools the consulted agent is offered: the selection in code, or the one of the data that the
   * agent's own selectors still cover. `null` offers every tool of the agent; empty offers none.
   */
  toolSelectors: string[] | null;
  /** The consultation defined as data; `null` for one in code. */
  data: DataConsultation | null;
  code: CodeConsultation | null;
}

/** The agent's consultations in code and as data, by subject type. */
export function consultationRows(configuration: AgentRunConfiguration): ConsultationRow[] {
  const code = (configuration.codeConsultations ?? []).map((consultation): ConsultationRow => ({
    subjectType: consultation.subjectType,
    origin: 'code',
    description: consultation.description,
    routes: [...consultation.allowedRoutes],
    requests: consultation.allowRequests,
    mcp: consultation.exposure.includes('mcp'),
    consultToolId: consultation.consultToolId,
    askingAgents: consultation.askingAgents,
    inEffect: true,
    problems: [],
    toolSelectors: consultation.toolSelectors ? [...consultation.toolSelectors] : null,
    data: null,
    code: consultation
  }));
  const data = (configuration.dataConsultations ?? []).map((consultation): ConsultationRow => ({
    subjectType: consultation.subjectType,
    origin: 'data',
    description: consultation.description,
    routes: [...consultation.routesInEffect],
    requests: consultation.requestToolId !== null,
    mcp: consultation.inEffect && consultation.exposeToMcp && !consultation.problems.includes('mcp-not-allowed'),
    consultToolId: consultation.consultToolId,
    askingAgents: consultation.askingAgents,
    inEffect: consultation.inEffect,
    problems: consultation.problems.length > 0 ? consultation.problems : consultation.problem ? [consultation.problem] : [],
    toolSelectors: toolSelectionInEffect(consultation),
    data: consultation,
    code: null
  }));
  return [...code, ...data].sort((left, right) => left.subjectType.localeCompare(right.subjectType));
}

/** The tool selection of a consultation defined as data in effect; servers before it offer every tool. */
function toolSelectionInEffect(consultation: DataConsultation): string[] | null {
  const selection = consultation.toolSelectorsInEffect === undefined ? consultation.toolSelectors : consultation.toolSelectorsInEffect;
  return selection ? [...selection] : null;
}

/** Whether one of the agent's own tool selectors covers a selector: the same tool, or a prefix that includes it. */
export function selectorCovers(agentSelector: string, selector: string): boolean {
  return agentSelector === selector || (agentSelector.endsWith('.*') && selector.startsWith(agentSelector.slice(0, -1)));
}

/** A tool selector the consultation form offers, with what it selects and whether the agent still has it. */
export interface ConsultationToolChoice extends ToolChoice {
  /** The agent's own selectors cover it; a saved selector they no longer cover can only be cleared. */
  covered: boolean;
}

/**
 * The tools the consultation form offers as check boxes: the agent's own selectors, as a skill's form
 * offers them, followed by the saved selectors that are none of them, such as a single tool under a
 * prefix the agent selects or a selector the agent no longer covers.
 */
export function consultationToolChoices(
  agentSelectors: readonly string[],
  catalog: readonly ToolCatalogEntry[],
  saved: readonly string[]
): ConsultationToolChoice[] {
  const own = toolChoices(agentSelectors, catalog).map(choice => ({ ...choice, covered: true }));
  const extra = toolChoices(saved.filter(selector => !agentSelectors.includes(selector)), catalog)
    .map(choice => ({ ...choice, covered: agentSelectors.some(agentSelector => selectorCovers(agentSelector, choice.selector)) }));
  return [...own, ...extra];
}

/**
 * Why administrators cannot add a consultation to the agent now, or `null` when they can:
 * `application` when the application does not let them define consultations, `agent` when the agent
 * is in code and not consultable as data, `needs-settings` while an agent created here has no run
 * settings, `limit` when it has as many as the ceiling allows, and `no-types` when every kind of
 * object it works on already has a consultation.
 */
export type ConsultationsClosed = 'application' | 'agent' | 'needs-settings' | 'limit' | 'no-types';

export function consultationsClosed(
  configuration: AgentRunConfiguration,
  catalog: RunCatalog | null,
  worksOn: WorksOnStatement | null
): ConsultationsClosed | null {
  const ceiling = configuration.consultationCeiling ?? null;
  if (ceiling === null) {
    return configuration.source === 'code' && (catalog?.consultationCeiling ?? null) !== null ? 'agent' : 'application';
  }
  if (configuration.source === 'admin' && configuration.runSettings === null) {
    return 'needs-settings';
  }
  if ((configuration.dataConsultations ?? []).length >= ceiling.maxConsultations) {
    return 'limit';
  }
  return catalog?.subjectTypes && consultationTypeOptions(catalog, worksOn, consultationRows(configuration).map(row => row.subjectType)).length === 0
    ? 'no-types'
    : null;
}

/**
 * The subject types a new consultation can be about: those the agent works on, the whole application
 * included, that it has no consultation about yet.
 */
export function consultationTypeOptions(
  catalog: RunCatalog | null,
  worksOn: WorksOnStatement | null,
  taken: readonly string[]
): AgentSubjectType[] {
  return (catalog?.subjectTypes ?? []).filter(type => worksOnCovers(worksOn, type.id) && !taken.includes(type.id));
}

/**
 * Whether the run configuration has a Consultations section: the agent has consultations, in code or
 * as data, or the application lets administrators define consultations, so the section either offers
 * them or says why this agent cannot get any. An application without consultations as data shows no
 * section for an agent nobody can consult, as before.
 */
export function showsConsultations(configuration: AgentRunConfiguration, catalog: RunCatalog | null): boolean {
  return (configuration.codeConsultations ?? []).length > 0
    || (configuration.dataConsultations ?? []).length > 0
    || (configuration.consultationCeiling ?? null) !== null
    || (catalog?.consultationCeiling ?? null) !== null;
}

/**
 * Why a route cannot be chosen for a consultation of this agent, or `null`: the ceiling does not name
 * it, the route with the agent's own rights is never for the whole application, and it needs an agent
 * with rights of its own to lend: not one that answers to the subject's responsible person, and not
 * one created here that answers for itself, which has no grants.
 */
export type ConsultationRouteBlock = 'not-in-ceiling' | 'system' | 'agent-answers-to-person' | 'agent-has-no-rights';

export function consultationRouteBlock(
  route: NhAssistantConsultationRoute,
  ceiling: ConsultationCeiling,
  subjectType: string,
  agent: { source: 'code' | 'admin'; accountability: AgentAccountability | null }
): ConsultationRouteBlock | null {
  if (!ceiling.routes.includes(route)) {
    return 'not-in-ceiling';
  }
  if (route !== 'subject-agent-rights') {
    return null;
  }
  if (subjectType === systemSubjectType) {
    return 'system';
  }
  if (agent.accountability === 'subject-responsible') {
    return 'agent-answers-to-person';
  }
  return agent.source === 'admin' && agent.accountability === 'agent' ? 'agent-has-no-rights' : null;
}

/** An approval policy lets only the person who caused a proposal decide it. */
export function initiatorOnly(summary: ApprovalPolicySummary | null | undefined): boolean {
  return !!summary && summary.approvers.length > 0 && summary.approvers.every(approver => approver.kind === 'initiator');
}

/** The approval policy the agent's own proposals use: its run settings as data, or its code. */
export function agentApprovalPolicy(configuration: AgentRunConfiguration): string | null {
  const settings = configuration.source === 'admin' ? configuration.runSettings : configuration.codeRunSettings;
  return settings?.approvalPolicy ?? null;
}
