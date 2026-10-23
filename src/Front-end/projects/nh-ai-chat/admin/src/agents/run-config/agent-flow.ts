import {
  AdminAgent,
  AgentAccountability,
  AgentRunConfiguration,
  DataTrigger,
  McpHeadlessUse,
  McpServer,
  RunCatalog,
  RunConfigurationProblem,
  ToolCatalogEffect,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { nhAssistantSelectorMatches } from '../nh-assistant-admin-agent-editor.component';
import { systemSubjectType } from './run-config-shared';

/**
 * The flow view of an agent: what it works on, when it runs, how it works, who it answers to, who
 * approves its proposals and its results. Each step summarizes what the run configuration, the agent
 * editor and the runs view hold and opens the form that changes it; the flow itself edits nothing.
 */
export type AgentFlowStepId = 'works-on' | 'when' | 'skills' | 'account' | 'approval' | 'results';

export const agentFlowSteps: readonly AgentFlowStepId[] = ['works-on', 'when', 'skills', 'account', 'approval', 'results'];

/**
 * What an entry point of the flow opens: the run configuration opens its own forms; the agent editor,
 * the technical account, the runs view and the MCP servers belong to the page that hosts it.
 */
export type AgentFlowEntry =
  | { kind: 'setup' }
  | { kind: 'settings' }
  | { kind: 'add-skill' }
  | { kind: 'edit-skill'; skillId: string }
  | { kind: 'add-trigger' }
  | { kind: 'edit-trigger'; triggerId: string }
  | { kind: 'add-consultation' }
  | { kind: 'edit-consultation'; subjectType: string }
  | { kind: 'edit-agent' }
  | { kind: 'account' }
  | { kind: 'runs' }
  | { kind: 'mcp-servers' };

/** Something in a step that no longer fits or keeps the agent from working, with the form that fixes it. */
export interface AgentFlowWarning {
  /** Translation key of the warning and its parameters. */
  key: string;
  params?: Record<string, unknown>;
  /** A second translation key that explains it, such as a problem's code. */
  detailKey?: string;
  /** The entry point that fixes it and the key of its label. */
  entry?: AgentFlowEntry;
  entryKey?: string;
  /** Tells several warnings with the same entry apart, for tracking. */
  id: string;
}

/**
 * What a trigger runs for each time it starts the agent: the whole application (once per division for
 * an agent that works per division), objects of a type (`value` names the type), every object of a
 * subject set (`value` names the set) or of a set in code, or the object the event names when it fires.
 */
export interface AgentFlowTarget {
  kind: 'application' | 'every-division' | 'type' | 'set' | 'code-set' | 'when-fired';
  value: string | null;
}

/** A trigger in code or as data, as the flow lists it. */
export interface AgentFlowTrigger {
  triggerId: string;
  origin: 'code' | 'data';
  source: 'event' | 'schedule' | 'webhook';
  /** The event, or the schedule with its time zone; `null` for a trigger in code. */
  detail: string | null;
  target: AgentFlowTarget;
  skillId: string | null;
  /** A trigger as data in shadow; `false` for a trigger in code, which follows the agent. */
  shadow: boolean;
  inEffect: boolean;
}

/** One tool of the agent's own tools, and where a run's proposals of it wait. */
export interface AgentFlowTool {
  id: string;
  description: string;
  effect: ToolCatalogEffect;
  /** The name of the object type a run places the tool's proposals at; `null` when it declares none. */
  placesAt: string | null;
}

/** The tools the agent's selectors choose from the catalog, without its MCP servers. */
export interface AgentFlowTools {
  tools: AgentFlowTool[];
  /** Tools the selectors choose beyond those listed. */
  more: number;
  /** Selectors that choose no tool of the catalog: those tools are no longer offered. */
  unmatched: string[];
  /** Whether the catalog was known; without it, the selectors are listed as they are. */
  known: boolean;
}

/**
 * Whether an MCP server's tools are offered in runs that start without a person, given the server's
 * background use and whom the agent answers to.
 */
export type AgentFlowServerUse =
  | 'unknown'
  | 'missing'
  | 'disabled'
  | 'never'
  | 'organization'
  | 'organization-grant'
  | 'personal'
  | 'personal-responsible'
  | 'personal-initiator'
  | 'personal-never-account'
  | 'personal-never-agent';

export interface AgentFlowServer {
  id: string;
  name: string;
  headlessUse: McpHeadlessUse | null;
  use: AgentFlowServerUse;
}

/** Whom an agent answers to, as its run settings (data) or its code say; `null` while unknown. */
export interface AgentFlowAccountability {
  accountability: AgentAccountability;
  delegateFromInitiator: boolean;
}

/**
 * Whether people can run the agent now, by the rule of runs by hand: it runs on its own, is neither
 * stopped nor disabled in code, has run settings and the run catalog names what it runs on.
 */
export type AgentFlowRunNow =
  | { available: true; paused: boolean }
  | { available: false; reason: 'stopped' | 'configured-off' | 'no-settings' | 'no-catalog' };

/** The most tools the flow lists before it counts the rest. */
export const agentFlowToolLimit = 8;

/** The run setting problems that concern the way of working or the approval instead of the account. */
const skillProblems = new Set(['unknown-rule-handler', 'unknown-profile']);

/** Whom the agent answers to, from its run settings as data or from its code; `null` while unknown. */
export function flowAccountability(configuration: AgentRunConfiguration): AgentFlowAccountability | null {
  const settings = configuration.source === 'admin' ? configuration.runSettings : configuration.codeRunSettings;
  return settings ? { accountability: settings.accountability, delegateFromInitiator: settings.delegateFromInitiator } : null;
}

/** The step a problem of the run configuration concerns. */
export function problemStep(problem: RunConfigurationProblem): AgentFlowStepId {
  switch (problem.kind) {
    case 'trigger':
    case 'consultation':
    case 'hand-off':
      return 'when';
    case 'skill':
    case 'candidate':
      return 'skills';
    default:
      if (problem.code === 'unknown-policy') {
        return 'approval';
      }
      return skillProblems.has(problem.code) ? 'skills' : 'account';
  }
}

/**
 * A problem as a warning on its step, with the form that fixes it. A consultation's problem names its
 * subject type as the run catalog names it.
 */
export function problemWarning(problem: RunConfigurationProblem, configuration: AgentRunConfiguration, catalog: RunCatalog | null = null): AgentFlowWarning {
  const base = {
    id: `problem:${problem.kind}:${problem.id}:${problem.code}`,
    key: `nh-assistant.admin.run-config.problems.kind.${problem.kind}`,
    params: { id: problem.kind === 'consultation' || problem.kind === 'hand-off' ? typeName(catalog, problem.id) : problem.id },
    // A hand-off out of effect is never used with less, so it has texts of its own.
    detailKey: problem.kind === 'hand-off'
      ? `nh-assistant.admin.hand-offs.problems.${problem.code}`
      : `nh-assistant.admin.run-config.problems.code.${problem.code}`
  };
  switch (problem.kind) {
    case 'consultation':
      // A consultation the agent may no longer get can only be removed, below in the section.
      return (configuration.consultationCeiling ?? null) !== null
        && (configuration.dataConsultations ?? []).some(consultation => consultation.subjectType === problem.id)
        ? {
          ...base,
          entry: { kind: 'edit-consultation', subjectType: problem.id },
          entryKey: 'nh-assistant.admin.run-config.flow.entry.edit-consultation'
        }
        : base;
    case 'hand-off':
      // A hand-off is changed or removed in the hand-offs below the run configuration.
      return base;
    case 'trigger':
      return configuration.dataTriggers.some(trigger => trigger.triggerId === problem.id)
        ? { ...base, entry: { kind: 'edit-trigger', triggerId: problem.id }, entryKey: 'nh-assistant.admin.run-config.flow.entry.edit-trigger' }
        : base;
    case 'skill':
    case 'candidate':
      return configuration.dataSkills.some(skill => skill.skillId === problem.id)
        ? { ...base, entry: { kind: 'edit-skill', skillId: problem.id }, entryKey: 'nh-assistant.admin.run-config.flow.entry.edit-skill' }
        : base;
    default:
      return configuration.source === 'admin' && configuration.runSettings
        ? { ...base, entry: { kind: 'settings' }, entryKey: 'nh-assistant.admin.run-config.flow.entry.settings' }
        : base;
  }
}

/** The name of an object type as the run catalog gives it in the reader's language; the id otherwise. */
export function typeName(catalog: RunCatalog | null, subjectType: string, plural = false): string {
  const type = catalog?.subjectTypes?.find(item => item.id === subjectType);
  if (!type) {
    return subjectType;
  }
  return plural ? type.pluralName ?? type.name : type.name;
}

/** The triggers in code and as data with what each runs for. */
export function flowTriggers(configuration: AgentRunConfiguration, catalog: RunCatalog | null, agent: AdminAgent): AgentFlowTrigger[] {
  const application: AgentFlowTarget = { kind: agent.perDivision === true ? 'every-division' : 'application', value: null };
  const ofType = (subjectType: string | null | undefined, schedule: boolean): AgentFlowTarget => {
    if (subjectType === systemSubjectType) {
      return application;
    }
    if (subjectType) {
      return { kind: 'type', value: typeName(catalog, subjectType) };
    }
    return { kind: schedule ? 'code-set' : 'when-fired', value: null };
  };

  const code = configuration.codeTriggers.map((trigger): AgentFlowTrigger => ({
    triggerId: trigger.triggerId,
    origin: 'code',
    source: trigger.source,
    detail: null,
    target: ofType(trigger.subjectType, trigger.source === 'schedule'),
    skillId: trigger.skillId,
    shadow: false,
    inEffect: true
  }));
  const data = configuration.dataTriggers.map((trigger): AgentFlowTrigger => ({
    triggerId: trigger.triggerId,
    origin: 'data',
    source: trigger.source,
    detail: triggerDetail(trigger),
    target: trigger.source === 'event'
      ? ofType(catalog?.triggerEvents.find(block => block.name === trigger.event)?.subjectType, false)
      : trigger.subjectSet ? { kind: 'set', value: trigger.subjectSet } : application,
    skillId: trigger.skillId,
    shadow: trigger.shadow,
    inEffect: trigger.inEffect
  }));
  return [...code, ...data];
}

function triggerDetail(trigger: DataTrigger): string | null {
  return trigger.source === 'event' ? trigger.event : `${trigger.cron ?? ''} ${trigger.timeZone ?? 'UTC'}`.trim();
}

/**
 * The tools the agent's selectors choose from the catalog. MCP tools come from the agent's servers,
 * so the flow lists those per server; a selector that chooses only MCP tools still counts as matched.
 * Without a catalog the selectors stay as they are and nothing reads as missing.
 */
export function flowTools(
  selectors: readonly string[],
  catalog: readonly ToolCatalogEntry[],
  runCatalog: RunCatalog | null,
  limit = agentFlowToolLimit
): AgentFlowTools {
  if (catalog.length === 0) {
    return { tools: [], more: 0, unmatched: [], known: false };
  }

  const chosen = new Map<string, ToolCatalogEntry>();
  const unmatched: string[] = [];
  for (const selector of selectors) {
    const matches = catalog.filter(tool => nhAssistantSelectorMatches(tool.id, selector));
    if (matches.length === 0) {
      unmatched.push(selector);
    }
    for (const tool of matches) {
      if (tool.source !== 'mcp') {
        chosen.set(tool.id, tool);
      }
    }
  }

  const tools = [...chosen.values()]
    .sort((left, right) => left.id.localeCompare(right.id))
    .map((tool): AgentFlowTool => ({
      id: tool.id,
      description: tool.description,
      effect: tool.effect,
      placesAt: tool.subjectType ? typeName(runCatalog, tool.subjectType) : null
    }));
  return { tools: tools.slice(0, limit), more: Math.max(0, tools.length - limit), unmatched, known: true };
}

/**
 * The agent's MCP servers with whether their tools are offered in runs that start without a person:
 * a server's background use (`never`, `organization`, `personal`) decides, and for `personal` also
 * whom the agent answers to. A run of an agent that answers to itself uses an organization's
 * credential only with a grant in code; a personal connection serves the person the run acts for,
 * with that person's consent, and never a technical account. Without the servers nothing is known.
 */
export function flowServers(
  serverIds: readonly string[],
  servers: readonly McpServer[] | null,
  accountability: AgentFlowAccountability | null
): AgentFlowServer[] {
  return serverIds.map(id => {
    if (servers === null) {
      return { id, name: id, headlessUse: null, use: 'unknown' };
    }
    const server = servers.find(item => item.id === id);
    if (!server) {
      return { id, name: id, headlessUse: null, use: 'missing' };
    }
    return { id, name: server.displayName, headlessUse: server.headlessUse, use: serverUse(server, accountability) };
  });
}

function serverUse(server: McpServer, accountability: AgentFlowAccountability | null): AgentFlowServerUse {
  if (!server.isEnabled) {
    return 'disabled';
  }
  switch (server.headlessUse) {
    case 'organization':
      return accountability?.accountability === 'agent' ? 'organization-grant' : 'organization';
    case 'personal':
      return personalUse(accountability);
    default:
      return 'never';
  }
}

function personalUse(accountability: AgentFlowAccountability | null): AgentFlowServerUse {
  if (!accountability) {
    return 'personal';
  }
  switch (accountability.accountability) {
    case 'subject-responsible':
      return 'personal-responsible';
    case 'service-account':
      return accountability.delegateFromInitiator ? 'personal-initiator' : 'personal-never-account';
    default:
      return accountability.delegateFromInitiator ? 'personal-initiator' : 'personal-never-agent';
  }
}

/** Server uses that keep its tools out of runs that start without a person. */
export const unavailableServerUses: ReadonlySet<AgentFlowServerUse> = new Set([
  'missing',
  'disabled',
  'never',
  'personal-never-account',
  'personal-never-agent'
]);

/**
 * Whether the agent can be run now, by the rule the server applies to runs by hand: it is neither
 * stopped nor disabled in code, an agent created here has run settings, and the run catalog names
 * what it runs on. A paused agent accepts the run, which waits until it is resumed; an agent that is
 * off runs it at once, because it only ignores the work it would take on by itself.
 */
export function flowRunNow(agent: AdminAgent, configuration: AgentRunConfiguration, catalog: RunCatalog | null): AgentFlowRunNow {
  if (agent.runState === 'configured-off') {
    return { available: false, reason: 'configured-off' };
  }
  if (agent.runState === 'stopped') {
    return { available: false, reason: 'stopped' };
  }
  if (configuration.source === 'admin' && configuration.runSettings === null) {
    return { available: false, reason: 'no-settings' };
  }
  if (catalog === null) {
    return { available: false, reason: 'no-catalog' };
  }
  return { available: true, paused: agent.runState === 'paused' };
}
