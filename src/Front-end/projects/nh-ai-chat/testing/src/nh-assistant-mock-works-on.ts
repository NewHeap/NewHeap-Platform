import { AdminAgent, AdminAgentInput } from '@newheap/platform-ai-chat';

/** What an agent works on, as the administration DTO carries it. */
export type MockWorksOn = Required<Pick<AdminAgent,
  'worksOnSubjectTypes' | 'worksOnAllSubjects' | 'worksOnApplication' | 'perDivision' | 'runPolicy' | 'viewPolicy' | 'worksOnDeclared'>>;

/** The platform's type of the whole application. */
export const mockSystemSubjectType = 'nh-system';

const subjectTypePattern = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/;
const maxSubjectTypes = 20;
const worksOnFields = ['worksOnSubjectTypes', 'worksOnAllSubjects', 'worksOnApplication', 'perDivision', 'runPolicy', 'viewPolicy'] as const;

/** An agent that does not say what it works on: every object type, not the application, app-wide. */
export const mockUndeclaredWorksOn: MockWorksOn = {
  worksOnSubjectTypes: [],
  worksOnAllSubjects: true,
  worksOnApplication: false,
  perDivision: false,
  runPolicy: null,
  viewPolicy: null,
  worksOnDeclared: false
};

/**
 * What an administrator says an agent works on, as the server reads it, or `null` when the input
 * says nothing: the six fields travel together, the omitted ones read as not chosen, and without
 * types, all objects or the application the agent works on all object types.
 */
export function mockWorksOnFromInput(input: AdminAgentInput): MockWorksOn | null {
  if (worksOnFields.every(field => input[field] === undefined || input[field] === null)) {
    return null;
  }

  const nothingChosen = input.worksOnSubjectTypes == null && input.worksOnAllSubjects == null && input.worksOnApplication == null;
  return {
    worksOnSubjectTypes: [...new Set((input.worksOnSubjectTypes ?? []).map(type => type.trim()))].sort(),
    worksOnAllSubjects: nothingChosen || input.worksOnAllSubjects === true,
    worksOnApplication: input.worksOnApplication === true,
    perDivision: input.perDivision === true,
    runPolicy: input.runPolicy?.trim() || null,
    viewPolicy: input.viewPolicy?.trim() || null,
    worksOnDeclared: true
  };
}

/**
 * The field errors of what an agent works on, with the server's rules: something chosen, not types
 * next to all objects, at most 20 registered application types, per division only when the
 * application lists divisions, and policies from those the application lists.
 */
export function mockWorksOnErrors(
  worksOn: MockWorksOn,
  knownTypes: readonly string[],
  agentPolicies: readonly string[],
  divisions: boolean
): Record<string, string[]> {
  const errors: Record<string, string[]> = {};
  const add = (field: string, code: string): void => {
    errors[field] = [...new Set([...(errors[field] ?? []), code])];
  };
  const types = worksOn.worksOnSubjectTypes;
  if (types.length === 0 && !worksOn.worksOnAllSubjects && !worksOn.worksOnApplication) {
    add('worksOn', 'required');
  }
  if (worksOn.worksOnAllSubjects && types.length > 0) {
    add('worksOn', 'invalid');
  }
  if (types.length > maxSubjectTypes) {
    add('worksOnSubjectTypes', 'too-long');
  }
  if (types.some(type => !subjectTypePattern.test(type))) {
    add('worksOnSubjectTypes', 'invalid');
  }
  if (types.some(type => subjectTypePattern.test(type) && (type.startsWith('nh-') || !knownTypes.includes(type)))) {
    add('worksOnSubjectTypes', 'not-found');
  }
  if (worksOn.perDivision && !divisions) {
    add('perDivision', 'invalid');
  }
  if (worksOn.runPolicy !== null && !agentPolicies.includes(worksOn.runPolicy)) {
    add('runPolicy', 'not-found');
  }
  if (worksOn.viewPolicy !== null && !agentPolicies.includes(worksOn.viewPolicy)) {
    add('viewPolicy', 'not-found');
  }
  return errors;
}

/** Whether an agent works on subjects of the type, as the server's rule decides it. */
export function mockWorksOnCovers(agent: Partial<MockWorksOn>, subjectType: string): boolean {
  if (subjectType === mockSystemSubjectType) {
    return agent.worksOnApplication === true;
  }
  if (subjectType.startsWith('nh-')) {
    return false;
  }
  return agent.worksOnAllSubjects !== false || (agent.worksOnSubjectTypes ?? []).includes(subjectType);
}
