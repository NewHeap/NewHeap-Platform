/** The sections of the administration page: its agents, its connections to MCP servers and the application context. */
export type NhAssistantAdminTab = 'context' | 'agents' | 'mcp-servers';

/**
 * The tabs of one agent's page: its overview, what it is built of, what of it is not live yet and
 * its activity.
 */
export type NhAssistantAgentTab = 'overview' | 'build' | 'rollout' | 'activity';

/** The tabs of an agent's page in their order. */
export const NH_ASSISTANT_AGENT_TABS: readonly NhAssistantAgentTab[] = ['overview', 'build', 'rollout', 'activity'];

/**
 * Where the administration is: its section and, on the agents section, the agent whose page is open
 * with its tab. A host that keeps this in its URL binds it to `location` of `nh-assistant-admin` and
 * follows `locationChange`, so a link or the browser's back button opens the same page.
 */
export interface NhAssistantAdminLocation {
  tab: NhAssistantAdminTab;
  /** The agent whose page is open; `null` for the list of agents. */
  agentId: string | null;
  /** The tab of the agent's page; `null` for the list of agents. */
  agentTab: NhAssistantAgentTab | null;
}

/** Reads an agent tab from a URL parameter; anything else is the overview. */
export function nhAssistantAgentTab(value: string | null | undefined): NhAssistantAgentTab {
  return NH_ASSISTANT_AGENT_TABS.find(tab => tab === value) ?? 'overview';
}
