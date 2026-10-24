import {
  AdminAgent,
  AdminAgentGroups,
  AgentBoard,
  AgentBoardAgent,
  AgentBoardList,
  AgentBoardVisit,
  AgentGroup,
  AgentSubjectType
} from '@newheap/platform-ai-chat';
import { NhAssistantMockAgentGroup, NhAssistantMockScenario } from './nh-assistant-mock.models';
import { NhAssistantMockResult, mockFailure as failure, mockSnapshot } from './nh-assistant-mock-admin-shared';
import { NhAssistantMockAgentRuns } from './nh-assistant-mock-agent-runs';
import { NhAssistantMockDescribedSubject, NhAssistantMockSubjects, mockPickText } from './nh-assistant-mock-subjects';
import { mockSystemSubjectType } from './nh-assistant-mock-works-on';

/** What the boards read of the mock's agents and caller. */
export interface NhAssistantMockBoardContext {
  /** Every agent of the administration. */
  agents(): AdminAgent[];
  /** Whether the caller passes the administration policy. */
  administers(): boolean;
}

const dashCase = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const language = /^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$/;
const maxGroupId = 64;
const maxGroups = 100;
const maxName = 100;
const maxDescription = 300;
const guid = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;

/**
 * Groups and boards of the mock API (K15, K16), as the server serves them: the scenario's
 * `agentGroups` are the groups in code with their names per language, checked as the server checks
 * the registration at startup; administrators put agents in groups and out of them with
 * `PUT admin/agents/{id}/groups`, which wins over the scenario's code memberships; `GET boards`,
 * `GET boards/{group}` and `POST boards/{group}/visit` show people the agents they may see (an agent
 * that works on the whole application by its view policy, by default its required policy, another by
 * its required policy), with their latest result and what is new since the visit of the board in its
 * scope. Visits live as long as the mock.
 */
export class NhAssistantMockBoards {
  private readonly groups: readonly NhAssistantMockAgentGroup[];
  /** The administrators' choices per agent: group id to in or out. */
  private readonly choices = new Map<string, Map<string, boolean>>();
  /** When the caller last visited a board, by `group|scope`. */
  private readonly visits = new Map<string, string>();

  constructor(
    scenario: NhAssistantMockScenario,
    private readonly subjects: NhAssistantMockSubjects,
    private readonly agentRuns: NhAssistantMockAgentRuns,
    private readonly context: NhAssistantMockBoardContext,
    private readonly now: () => string
  ) {
    this.groups = [...(scenario.agentGroups ?? [])].sort((left, right) => left.id.localeCompare(right.id));
    validate(this.groups);
    // An agent created as data that the scenario lists in a group was put there by an administrator.
    for (const group of this.groups) {
      for (const agentId of group.agentIds ?? []) {
        if (this.context.agents().find(agent => agent.id === agentId)?.source === 'admin') {
          const choices = this.choices.get(agentId) ?? new Map<string, boolean>();
          choices.set(group.id, true);
          this.choices.set(agentId, choices);
        }
      }
    }
  }

  /** The run catalog's groups in the request's language, by id. */
  catalog(requested: string | null): AgentGroup[] {
    return this.groups.map(group => ({
      id: group.id,
      name: mockPickText(group.names, group.fallbackLanguage ?? 'en', requested) ?? group.id,
      description: mockPickText(group.descriptions, group.fallbackLanguage ?? 'en', requested)
    }));
  }

  /** The groups an agent is in now and those it joins in code. */
  membership(agentId: string): AdminAgentGroups {
    const codeGroups = this.codeGroupsOf(agentId);
    const groups = new Set(codeGroups);
    for (const [groupId, member] of this.choices.get(agentId) ?? []) {
      if (member) {
        groups.add(groupId);
      } else {
        groups.delete(groupId);
      }
    }
    return { agentId, groups: [...groups].sort(), codeGroups };
  }

  /**
   * A copy of the administrators' group choices; the returned function puts it back, for an import
   * of an agent package that fails as a whole and for its preview.
   */
  snapshot(): () => void {
    return mockSnapshot(this.choices);
  }

  /** Forgets the choices about an agent that was deleted. */
  forget(agentId: string): void {
    this.choices.delete(agentId);
  }

  /** `PUT admin/agents/{id}/groups`, with the server's validation. */
  handleAdminGroups(method: string, agentId: string, body: unknown): NhAssistantMockResult {
    if (method !== 'PUT') {
      return { status: 405 };
    }
    if (!this.context.administers()) {
      return failure('assistant-forbidden');
    }
    const request = body as { groups?: unknown } | undefined;
    if (!request || typeof request !== 'object') {
      return failure('assistant-validation');
    }
    if (!Array.isArray(request.groups)) {
      return failure('assistant-validation', { groups: ['required'] });
    }
    const wanted = request.groups as unknown[];
    if (wanted.length > maxGroups) {
      return failure('assistant-validation', { groups: ['too-long'] });
    }
    if (!wanted.every(groupId => typeof groupId === 'string' && this.groups.some(group => group.id === groupId))) {
      return failure('assistant-validation', { groups: ['not-found'] });
    }
    const agent = this.context.agents().find(item => item.id === agentId);
    if (!agent) {
      return failure('assistant-agent-not-found');
    }
    if (wanted.length > 0 && agent.shadowOf) {
      return failure('assistant-shadow-variant');
    }

    // Only a choice that differs from code is kept; one that equals code lets code decide again.
    const codeGroups = this.codeGroupsOf(agentId);
    const choices = new Map<string, boolean>();
    for (const group of this.groups) {
      const member = wanted.includes(group.id);
      if (member !== codeGroups.includes(group.id)) {
        choices.set(group.id, member);
      }
    }
    this.choices.set(agentId, choices);
    return { status: 200, body: this.membership(agentId) };
  }

  /** `GET boards`, `GET boards/{group}` and `POST boards/{group}/visit`; `null` for other paths. */
  handle(method: string, segments: string[], query: URLSearchParams, requested: string | null): NhAssistantMockResult | null {
    if (segments[0] !== 'boards' || segments.length > 3 || (segments.length === 3 && segments[2] !== 'visit')) {
      return null;
    }
    if (segments.length === 1) {
      if (method !== 'GET') {
        return { status: 405 };
      }
      const items = this.groups
        .map(group => ({ group, count: this.visibleAgents(group.id, null, requested).length }))
        .filter(item => item.count > 0)
        .map(item => {
          const named = this.catalog(requested).find(group => group.id === item.group.id)!;
          return { id: named.id, name: named.name, description: named.description, agentCount: item.count };
        })
        .sort((left, right) => left.name.localeCompare(right.name) || left.id.localeCompare(right.id));
      const body: AgentBoardList = { items };
      return { status: 200, body };
    }

    const visit = segments.length === 3;
    if (method !== (visit ? 'POST' : 'GET')) {
      return { status: 405 };
    }
    const groupId = segments[1];
    const divisionId = query.get('divisionId');
    if (divisionId !== null && divisionId !== '' && !guid.test(divisionId)) {
      return failure('assistant-board-scope-invalid');
    }
    const division = divisionId ? this.division(divisionId, requested) : null;
    if (!this.groups.some(group => group.id === groupId) || division === undefined) {
      return failure('assistant-board-not-found');
    }
    const agents = this.visibleAgents(groupId, division, requested);
    if (agents.length === 0) {
      return failure('assistant-board-not-found');
    }

    const scopeKey = division ? `division:${guidN(divisionId!)}` : 'global';
    const visitKey = `${groupId}|${scopeKey}`;
    if (visit) {
      const visitedAt = this.now();
      this.visits.set(visitKey, visitedAt);
      const body: AgentBoardVisit = { groupId, scopeKey, visitedAt };
      return { status: 200, body };
    }

    const lastVisitedAt = this.visits.get(visitKey) ?? null;
    const named = this.catalog(requested).find(group => group.id === groupId)!;
    const types = this.subjects.catalog(requested);
    const body: AgentBoard = {
      id: groupId,
      name: named.name,
      description: named.description,
      scopeKey,
      divisionId: division ? divisionId!.toLowerCase() : null,
      lastVisitedAt,
      agents: agents.map(agent => this.tile(agent, division, types, lastVisitedAt, requested))
    };
    return { status: 200, body };
  }

  /** The tile of one agent on a board. */
  private tile(
    agent: AdminAgent,
    division: NhAssistantMockDescribedSubject | null,
    types: readonly AgentSubjectType[],
    lastVisitedAt: string | null,
    requested: string | null
  ): AgentBoardAgent {
    const subject = this.subjectOf(agent, division, requested);
    const work = subject ? this.agentRuns.boardWork(agent, subject, requested) : null;
    const ended = work?.latestRun && work.latestRun.status !== 'queued' && work.latestRun.status !== 'running'
      ? Date.parse(work.latestRun.at)
      : null;
    const visited = lastVisitedAt === null ? null : Date.parse(lastVisitedAt);
    const isNew = visited === null
      ? ended !== null || (work?.openProposals ?? 0) > 0
      : ended !== null && ended > visited;
    return {
      agentId: agent.id,
      ...this.agentRuns.nameOf(agent),
      worksOn: {
        allObjects: agent.worksOnAllSubjects ?? true,
        application: agent.worksOnApplication === true,
        perDivision: agent.perDivision === true,
        subjectTypes: [...(agent.worksOnSubjectTypes ?? [])].sort().map(id => {
          const type = types.find(item => item.id === id);
          return { id, name: type?.name ?? id, pluralName: type?.pluralName ?? null };
        })
      },
      subjectType: subject?.subjectType ?? null,
      subjectId: subject?.subjectId ?? null,
      subjectDisplayName: subject?.displayName ?? null,
      canStart: work?.canStart ?? false,
      conversationId: work?.conversationId ?? null,
      latestRun: work?.latestRun ?? null,
      openProposals: work?.openProposals ?? 0,
      isNew
    };
  }

  /**
   * The group's agents the caller sees on its board in the scope, by id: working live agents, by their
   * view policy where they work on the whole application in the scope, otherwise by their required
   * policy.
   */
  private visibleAgents(groupId: string, division: NhAssistantMockDescribedSubject | null, requested: string | null): AdminAgent[] {
    return this.context.agents()
      .filter(agent => !agent.shadowOf && this.membership(agent.id).groups.includes(groupId))
      .filter(agent => this.subjectOf(agent, division, requested)
        ? this.agentRuns.maySeeResults(agent)
        : this.agentRuns.mayUseNow(agent))
      .sort((left, right) => left.id.localeCompare(right.id));
  }

  /** The whole application an agent works on in the board's scope, as the server decides it. */
  private subjectOf(agent: AdminAgent, division: NhAssistantMockDescribedSubject | null, requested: string | null): NhAssistantMockDescribedSubject | null {
    if (agent.worksOnApplication !== true) {
      return null;
    }
    if (agent.perDivision !== true) {
      return this.subjects.describe(mockSystemSubjectType, 'global', requested, this.context.administers()) ?? null;
    }
    return division;
  }

  /** A listed division the caller works in; `undefined` for one the board may not show. */
  private division(divisionId: string, requested: string | null): NhAssistantMockDescribedSubject | undefined {
    return this.subjects.describe(mockSystemSubjectType, `division-${guidN(divisionId)}`, requested, this.context.administers()) ?? undefined;
  }

  /** The groups an agent in code joins in code; an agent created as data joins none there. */
  private codeGroupsOf(agentId: string): string[] {
    if (this.context.agents().find(agent => agent.id === agentId)?.source !== 'code') {
      return [];
    }
    return this.groups.filter(group => group.agentIds?.includes(agentId)).map(group => group.id);
  }
}

function guidN(id: string): string {
  return id.replace(/-/g, '').toLowerCase();
}

/** Fails as the server fails at startup for groups it would not register. */
function validate(groups: readonly NhAssistantMockAgentGroup[]): void {
  if (groups.length > maxGroups) {
    throw new Error(`The mock scenario registers more than ${maxGroups} agent groups.`);
  }
  const seen = new Set<string>();
  for (const group of groups) {
    if (!dashCase.test(group.id) || group.id.length > maxGroupId) {
      throw new Error(`The mock agent group '${group.id}' is not a dash-case id of at most ${maxGroupId} characters.`);
    }
    if (seen.has(group.id)) {
      throw new Error(`The mock agent group '${group.id}' is registered twice.`);
    }
    seen.add(group.id);
    const fallback = group.fallbackLanguage ?? 'en';
    checkTexts(group.id, 'name', group.names, fallback, maxName);
    if (group.descriptions) {
      checkTexts(group.id, 'description', group.descriptions, fallback, maxDescription);
    }
  }
}

function checkTexts(groupId: string, kind: string, texts: Record<string, string> | undefined, fallback: string, max: number): void {
  const entries = Object.entries(texts ?? {});
  if (entries.length === 0 || !entries.some(([key]) => key.toLowerCase() === fallback.toLowerCase())) {
    throw new Error(`The mock agent group '${groupId}' has no ${kind} in its fallback language '${fallback}'.`);
  }
  for (const [key, text] of entries) {
    if (!language.test(key) || text.trim().length === 0 || text.trim().length > max) {
      throw new Error(`The ${kind} of mock agent group '${groupId}' in '${key}' must be 1 to ${max} characters.`);
    }
  }
}
