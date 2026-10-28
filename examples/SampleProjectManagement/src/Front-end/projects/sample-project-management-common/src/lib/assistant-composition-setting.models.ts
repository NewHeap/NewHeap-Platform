/**
 * SPM-371: whether one agent uses compositions and the script tool in the portal's environment, as
 * `GET assistant-composition-settings` returns it. The agent `mcp` stands for the MCP exposure.
 */
export interface AssistantCompositionSettingViewModel {
  agentId: string;
  compositions: boolean;
  scripts: boolean;
}

/** SPM-371: the new setting of one agent. What code does not allow stays off, whatever is stored. */
export interface AssistantCompositionSettingMutateModel {
  compositions: boolean;
  scripts: boolean;
}

/** The agent id under which the setting of the MCP exposure is stored. */
export const ASSISTANT_COMPOSITION_MCP_AGENT_ID = 'mcp';
