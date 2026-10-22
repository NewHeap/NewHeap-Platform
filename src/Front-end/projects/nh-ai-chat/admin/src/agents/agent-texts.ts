/**
 * Translation keys for the texts the administration shows about agents, skills and policies. Each
 * list goes through `nhAssistantTranslate` with the text itself as fallback, so a missing
 * translation shows what the server sent.
 */

/**
 * The key of a text of an agent in code, which names and describes itself with translation keys;
 * none for an agent created in the administration, whose texts are what the administrator typed.
 */
export function codeTextKeys(source: string, text: string | null | undefined): string[] {
  return source === 'code' && text ? [text] : [];
}

/** The key an application may translate a skill's name with. */
export function skillNameKeys(agentId: string, skillId: string): string[] {
  return [`nh-assistant.agents.${agentId}.skills.${skillId}.name`];
}

/**
 * The key an application may translate a skill's description with. Skills in code describe
 * themselves in the language triage reads; the screen shows a translation when there is one.
 */
export function skillDescriptionKeys(agentId: string, skillId: string): string[] {
  return [`nh-assistant.agents.${agentId}.skills.${skillId}.description`];
}

/**
 * The key of a warning about an agent or a skill that is saved and in effect but may not work as
 * configured, such as `classification-not-permitted`; a code the screen does not know shows as sent.
 */
export function warningKeys(kind: 'agent' | 'skill', code: string): string[] {
  return kind === 'agent'
    ? [`nh-assistant.admin.agents.warnings.${code}`]
    : [`nh-assistant.admin.run-config.skills.warnings.${code}`];
}

/** The key an application may translate an authorization policy's name with. */
export function policyKeys(policy: string | null | undefined): string[] {
  return policy ? [`nh-assistant.policies.${policy}`] : [];
}

/** A readable name for a dash-case id: `task-follow-up` reads "Task follow up". */
export function readableId(id: string): string {
  const words = id.split('-').filter(word => word.length > 0).join(' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** What an agent works on, ready to show: object types by name, all objects and the application. */
export interface WorksOnSummary {
  /** The names of the object types it works on; empty with `all`. */
  types: string[];
  /** It works on every object type. */
  all: boolean;
  /** It works on the whole application. */
  application: boolean;
  perDivision: boolean;
  /** An agent in code that does not say what it works on. */
  undeclared: boolean;
  /** The names of the object types it is known to work on, as a hint next to "All objects". */
  known: string[];
  runPolicy: string | null;
  viewPolicy: string | null;
}

/**
 * What an agent works on, with object types named as the run catalog names several of them in the
 * reader's language, and their ids otherwise. An older server that does not say reads as all objects.
 * The platform's types (`nh-`, such as the whole application's `nh-system`) are not object types of
 * the application: the agent's threads on the whole application are no hint next to "All objects".
 */
export function worksOnSummary(
  agent: {
    worksOnSubjectTypes?: string[];
    worksOnAllSubjects?: boolean;
    worksOnApplication?: boolean;
    perDivision?: boolean;
    worksOnDeclared?: boolean;
    knownSubjectTypes?: string[];
    runPolicy?: string | null;
    viewPolicy?: string | null;
  },
  subjectTypes: readonly { id: string; name: string; pluralName?: string | null; isSystem?: boolean }[] | null | undefined
): WorksOnSummary {
  const names = new Map((subjectTypes ?? []).map(type => [type.id, type.pluralName ?? type.name]));
  const system = new Set((subjectTypes ?? []).filter(type => type.isSystem).map(type => type.id));
  const name = (id: string): string => names.get(id) ?? id;
  const objectType = (id: string): boolean => !id.startsWith('nh-') && !system.has(id);
  const types = (agent.worksOnSubjectTypes ?? []).filter(objectType);
  const all = agent.worksOnAllSubjects ?? (types.length === 0 && agent.worksOnApplication !== true);
  return {
    types: all ? [] : types.map(name),
    all,
    application: agent.worksOnApplication === true,
    perDivision: agent.perDivision === true,
    undeclared: agent.worksOnDeclared === false,
    known: (agent.knownSubjectTypes ?? []).filter(objectType).map(name),
    runPolicy: agent.runPolicy ?? null,
    viewPolicy: agent.viewPolicy ?? null
  };
}

/** What the run-now dialog needs of an agent: its id, its name as people read it and what it works on. */
export interface RunNowAgentInput {
  id: string;
  worksOnSubjectTypes?: string[];
  worksOnAllSubjects?: boolean;
  worksOnApplication?: boolean;
  perDivision?: boolean;
}

/**
 * The agent as the run-now dialog runs it, with its name in the reader's language. An older server that
 * does not say what the agent works on reads as all objects, not the whole application, app-wide.
 */
export function runNowAgentOf(agent: RunNowAgentInput, name: string): {
  id: string;
  name: string;
  worksOnSubjectTypes: string[];
  worksOnAllSubjects: boolean;
  worksOnApplication: boolean;
  perDivision: boolean;
} {
  return {
    id: agent.id,
    name,
    worksOnSubjectTypes: agent.worksOnSubjectTypes ?? [],
    worksOnAllSubjects: agent.worksOnAllSubjects ?? true,
    worksOnApplication: agent.worksOnApplication ?? false,
    perDivision: agent.perDivision ?? false
  };
}
