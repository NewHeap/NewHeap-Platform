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

/** The key an application may translate an authorization policy's name with. */
export function policyKeys(policy: string | null | undefined): string[] {
  return policy ? [`nh-assistant.policies.${policy}`] : [];
}

/** A readable name for a dash-case id: `task-follow-up` reads "Task follow up". */
export function readableId(id: string): string {
  const words = id.split('-').filter(word => word.length > 0).join(' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}
