import { Injectable } from '@angular/core';
import { AdminAgent, ClientContext, Conversation, ToolCallPart } from '@newheap/platform-ai-chat';

/** The id of the platform's agent builder, which the server switches on with `AddAgentBuilder`. */
export const NH_ASSISTANT_AGENT_BUILDER_ID = 'nh-agent-builder';

/** The tool with which the agent builder creates an agent, once the administrator approved it. */
export const NH_ASSISTANT_AGENT_BUILDER_CREATE_TOOL = 'assistant.agent-builder.create-agent';

/** The type of the page context's entity that names the agent the builder works on. */
export const NH_ASSISTANT_AGENT_BUILDER_ENTITY = 'assistant-agent';

/** Whether the administration offers the agent builder: it is registered and not stopped. */
export function nhAssistantBuildsWithHelp(agents: readonly AdminAgent[]): boolean {
  return agents.some(agent => agent.id === NH_ASSISTANT_AGENT_BUILDER_ID && agent.runState !== 'stopped');
}

/**
 * What the builder's chat sends as page context: the agent it works on, by its id in the route and as
 * an entity with its name, or that the administrator makes a new agent. It is a hint for the model,
 * never an authorization: every tool call names its agent, and the approval card shows it.
 */
export function nhAssistantAgentBuilderContext(agent: AdminAgent | null, name: string, newTitle: string): ClientContext {
  if (agent === null) {
    return { route: 'assistant/admin/agents/new', title: newTitle };
  }

  return {
    route: `assistant/admin/agents/${agent.id}`,
    title: name,
    entities: [{ type: NH_ASSISTANT_AGENT_BUILDER_ENTITY, id: agent.id, label: name }]
  };
}

/** The agent ids that succeeded calls of the builder's create tool name in `conversation`, by invocation. */
export function nhAssistantCreatedAgents(conversation: Conversation): Map<string, string> {
  const created = new Map<string, string>();
  for (const message of conversation.messages) {
    for (const part of message.parts) {
      if (part.type !== 'tool-call') {
        continue;
      }
      const call = part as ToolCallPart;
      // The result names the new agent first, so a preview that was cut keeps it.
      const agentId = call.toolId === NH_ASSISTANT_AGENT_BUILDER_CREATE_TOOL && call.status === 'succeeded'
        ? /"agentId"\s*:\s*"([a-z0-9]+(?:-[a-z0-9]+)*)"/.exec(call.resultPreview ?? '')?.[1]
        : undefined;
      if (agentId) {
        created.set(call.invocationId, agentId);
      }
    }
  }
  return created;
}

/**
 * The builder's chat per agent for this session of the administration: the chat on an agent's page
 * continues where it was, and the chat of "New agent with help" continues on the page of the agent it
 * created. After a reload the next chat starts anew; earlier chats stay in the assistant's conversations.
 */
@Injectable({ providedIn: 'root' })
export class NhAssistantAgentBuilderChats {
  private readonly conversations = new Map<string, string>();

  /** The chat about `agentId`, or about a new agent for `null`; `null` when there is none yet. */
  conversationOf(agentId: string | null): string | null {
    return this.conversations.get(agentId ?? '') ?? null;
  }

  /** Remembers the chat about `agentId`, or about a new agent for `null`. */
  remember(agentId: string | null, conversationId: string): void {
    this.conversations.set(agentId ?? '', conversationId);
  }

  /** Forgets the chat about a new agent once it created one, so the next new agent starts anew. */
  forgetNew(): void {
    this.conversations.delete('');
  }
}
