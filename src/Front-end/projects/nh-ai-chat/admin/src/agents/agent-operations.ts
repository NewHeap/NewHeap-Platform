import {
  AdminAgent,
  AgentShadowMode,
  AgentShadowState,
  AgentSkillState,
  AgentState,
  SetAgentStateRequest
} from '@newheap/platform-ai-chat';

/**
 * What an administrator does with an agent or one of its skills while it runs: change its run state,
 * its shadow mode and, for an agent in code, its technical account. The agent list, the header of an
 * agent's page and its skills share these rules.
 */
export type AgentStateAction = SetAgentStateRequest['state'];

/** The one status an agent shows in the list and in the header of its page, by how much it matters. */
export type AgentStatus = 'stopped' | 'paused' | 'off' | 'configured-off' | 'shadow' | 'live';

/** Where an agent works: in the chat, on its own, both, or in the chat only (`ChatOnly()`). */
export type AgentWork = 'chat' | 'background' | 'both' | 'chat-only';

/** The longest reason the server stores with a state change. */
export const maxStateReason = 500;

/** The state changes the agent allows: none for an agent that is disabled in code. */
export function agentStateActions(agent: AdminAgent): AgentStateAction[] {
  // A chat-only agent has no runs to pause or stop; one changed elsewhere can still be resumed.
  if (agent.chatOnly) {
    return agent.runState === 'paused' || agent.runState === 'off' || agent.runState === 'stopped' ? ['active'] : [];
  }
  switch (agent.runState) {
    case 'active':
      return ['paused', 'off', 'stopped'];
    case 'paused':
      return ['active', 'off', 'stopped'];
    case 'off':
      return ['active', 'stopped'];
    case 'stopped':
      return ['active', 'off'];
    default:
      return [];
  }
}

/** The state changes a skill allows. */
export function skillStateActions(skill: AgentSkillState): AgentStateAction[] {
  switch (skill.runState) {
    case 'active':
      return ['paused', 'off', 'stopped'];
    case 'paused':
      return ['active', 'off', 'stopped'];
    case 'off':
      return ['active', 'stopped'];
    default:
      return ['active'];
  }
}

/**
 * The key of a state change's label, explanation or confirmation: making an agent or skill that is
 * off active again switches it on, which replays nothing, unlike a resume.
 */
export function stateKey(part: 'action' | 'explain' | 'confirm', from: string, action: AgentStateAction): string {
  return `nh-assistant.admin.agents.state.${part}.${from === 'off' && action === 'active' ? 'switch-on' : action}`;
}

/** The explanation of a state change of one skill. */
export function skillStateExplainKey(from: string, action: AgentStateAction): string {
  return `nh-assistant.admin.agents.skills.explain.${from === 'off' && action === 'active' ? 'switch-on' : action}`;
}

/**
 * The shadow modes an agent or skill can change to: the opposite of its own mode, and the mode
 * in code once an administrator overrode it. A shadow variant, and an agent without runs that
 * only chats, offer none.
 */
export function shadowActions(shadow: AgentShadowState | null | undefined): AgentShadowMode[] {
  if (!shadow || shadow.locked) {
    return [];
  }
  const own = shadow.override ?? shadow.configured;
  const actions: AgentShadowMode[] = [own ? 'live' : 'shadow'];
  return shadow.override === null ? actions : [...actions, 'code'];
}

/**
 * The label of a shadow action. An agent created here has no code: returning to its "code" mode
 * means returning to the mode it was set up with.
 */
export function shadowActionKey(agent: AdminAgent, mode: AgentShadowMode): string {
  return mode === 'code' && agent.source !== 'code'
    ? 'nh-assistant.admin.agents.shadow.action.own'
    : `nh-assistant.admin.agents.shadow.action.${mode}`;
}

/** The explanation of a shadow change of the agent or one of its skills. */
export function shadowExplainKey(agent: AdminAgent, mode: AgentShadowMode, skill: boolean): string {
  const group = skill ? 'skill-explain' : 'explain';
  return `nh-assistant.admin.agents.shadow.${group}.${mode === 'code' && agent.source !== 'code' ? 'own' : mode}`;
}

/** The mode the agent, or one of its skills, was set up with: what returning to it means. */
export function ownShadowModeKey(agent: AdminAgent, skillId?: string): string {
  const shadow = skillId === undefined
    ? agent.shadow
    : agent.skills?.find(skill => skill.skillId === skillId)?.shadow;
  return shadow?.configured ? 'nh-assistant.admin.agents.shadow.mode.shadow' : 'nh-assistant.admin.agents.shadow.mode.live';
}

/**
 * The one status of an agent: a run state that holds its work wins over its shadow mode, and an
 * agent that works, live or in shadow, says which.
 */
export function agentStatus(agent: AdminAgent): AgentStatus {
  switch (agent.runState) {
    case 'stopped':
    case 'paused':
    case 'off':
    case 'configured-off':
      return agent.runState;
    default:
      return agent.shadow?.shadow ? 'shadow' : 'live';
  }
}

/** The status of one skill, by the same rule as its agent's. */
export function skillStatus(skill: AgentSkillState): AgentStatus {
  switch (skill.runState) {
    case 'stopped':
    case 'paused':
    case 'off':
      return skill.runState;
    default:
      return skill.shadow?.shadow ? 'shadow' : 'live';
  }
}

/** The class of a status pill: what needs attention first stands out most. */
export function statusTone(status: AgentStatus): 'success' | 'warning' | 'danger' | 'info' | 'neutral' {
  switch (status) {
    case 'live':
      return 'success';
    case 'shadow':
      return 'info';
    case 'paused':
      return 'warning';
    case 'stopped':
      return 'danger';
    default:
      return 'neutral';
  }
}

/**
 * Where the agent works, as people choose it in the agent editor or its code says. The server
 * reports a shadow state only for an agent with runs: an agent in code that configures them, or an
 * agent created here once it has run settings.
 */
export function agentWork(agent: AdminAgent): AgentWork {
  if (agent.chatOnly) {
    return 'chat-only';
  }
  if (agent.offeredInChat === false) {
    return 'background';
  }
  return agent.shadow ? 'both' : 'chat';
}

/**
 * Whether the agent has a run configuration: an agent in code with runs, or any agent created here,
 * which gets run settings as data.
 */
export function hasRunConfiguration(agent: AdminAgent): boolean {
  return agent.source === 'admin' || (agent.shadow !== null && agent.shadow !== undefined);
}

/** Whether the agent runs on its own: it has a run configuration and is not chat-only. */
export function runsOnItsOwn(agent: AdminAgent): boolean {
  return hasRunConfiguration(agent) && agent.chatOnly !== true;
}

/** Whether the agent is offered in people's chats, where an administrator can try it. */
export function offeredInChat(agent: AdminAgent): boolean {
  return agent.offeredInChat !== false && agent.runState !== 'configured-off';
}

/**
 * What needs the administrator's attention on an agent, as the list counts it: its configuration
 * warnings, instructions that no longer resolve, and inputs that wait while it is paused.
 */
export function agentAttention(agent: AdminAgent): number {
  return (agent.warnings?.length ?? 0) + (agent.instructionsProblem ? 1 : 0);
}

/** The skills of the agent that work in shadow while the agent itself does not. */
export function skillsInShadow(agent: AdminAgent): AgentSkillState[] {
  return (agent.skills ?? []).filter(skill => skill.shadow?.shadow === true && agent.shadow?.shadow !== true);
}

/** The agent with the run state, shadow mode and technical account a state change answered with. */
export function withState(agent: AdminAgent, state: AgentState): AdminAgent {
  return {
    ...agent,
    isEnabled: state.runState !== 'stopped',
    runState: state.runState,
    stateChangedAt: state.changedAt,
    stateChangedBy: state.changedBy,
    stateReason: state.reason,
    waitingInputs: state.waitingInputs,
    offBeforeStop: state.offBeforeStop,
    skills: state.skills ?? agent.skills,
    shadow: state.shadow ?? agent.shadow,
    serviceAccount: state.serviceAccount === undefined ? agent.serviceAccount : state.serviceAccount
  };
}

/** The agent with the state of one skill as a state change of that skill answered with it. */
export function withSkill(agent: AdminAgent, skill: AgentSkillState): AdminAgent {
  return {
    ...agent,
    skills: (agent.skills ?? []).map(item => item.skillId === skill.skillId ? skill : item)
  };
}
