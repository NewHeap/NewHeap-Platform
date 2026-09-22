import {
  ApprovalPart,
  Conversation,
  ConversationStatus,
  Message,
  MessagePart,
  ToolCallPart
} from '../models/assistant-api.models';
import { NhAssistantSseEvent, TurnCompletionStatus } from '../models/assistant-sse.models';

/**
 * Applies one assistant event to a conversation and returns the new conversation.
 * The input is never mutated, so the result can be stored in a signal directly.
 *
 * @param clientMessageId id of the optimistic user message of the current turn; its id is
 * replaced by the server's `userMessageId` when `turn.started` arrives.
 */
export function applyNhAssistantEvent(
  conversation: Conversation,
  event: NhAssistantSseEvent,
  clientMessageId?: string
): Conversation {
  switch (event.type) {
    case 'turn.started': {
      let messages = conversation.messages.map(message =>
        clientMessageId && message.id === clientMessageId ? { ...message, id: event.data.userMessageId } : message
      );
      if (!messages.some(message => message.id === event.data.assistantMessageId)) {
        messages = [...messages, createAssistantMessage(event.data.assistantMessageId)];
      }

      return { ...conversation, status: 'running', messages };
    }

    case 'message.delta': {
      const messages = ensureMessage(conversation.messages, event.data.messageId);
      return {
        ...conversation,
        messages: messages.map(message =>
          message.id === event.data.messageId ? appendText(message, event.data.text) : message
        )
      };
    }

    case 'tool.started': {
      const data = event.data;
      if (findToolCall(conversation.messages, data.invocationId)) {
        return updateToolCall(conversation, data.invocationId, part => ({
          ...part,
          toolId: data.toolId,
          toolVersion: data.toolVersion,
          displayName: data.displayName,
          argumentsPreview: data.argumentsPreview,
          status: 'running'
        }));
      }

      const part: ToolCallPart = {
        type: 'tool-call',
        invocationId: data.invocationId,
        toolId: data.toolId,
        toolVersion: data.toolVersion,
        displayName: data.displayName,
        status: 'running',
        argumentsPreview: data.argumentsPreview,
        resultPreview: null,
        resultCode: null
      };
      return appendToCurrentAssistantMessage(conversation, part);
    }

    case 'tool.completed': {
      const data = event.data;
      return updateToolCall(conversation, data.invocationId, part => ({
        ...part,
        status: data.status,
        resultCode: data.resultCode,
        resultPreview: data.resultPreview
      }));
    }

    case 'approval.required': {
      const approval: ApprovalPart = { ...event.data, type: 'approval' };
      const withToolAwaiting = markLatestRunningToolAwaiting(
        conversation,
        approval.toolId,
        approval.presentation?.toolDisplayName);
      const exists = withToolAwaiting.messages.some(message =>
        message.parts.some(part => part.type === 'approval' && part.approvalId === approval.approvalId)
      );
      const withApproval = exists
        ? replaceApproval(withToolAwaiting, approval)
        : appendToCurrentAssistantMessage(withToolAwaiting, approval);

      return { ...withApproval, status: 'waiting-for-approval', pendingApproval: approval };
    }

    case 'turn.completed': {
      const status = conversationStatusFor(event.data.status);
      return {
        ...conversation,
        status,
        pendingApproval: status === 'waiting-for-approval' ? conversation.pendingApproval : null
      };
    }

    case 'error':
      return {
        ...conversation,
        status: conversation.status === 'running' ? 'failed' : conversation.status
      };
  }
}

/** Records the user's approval decision locally until the server state is reloaded. */
export function applyNhAssistantApprovalDecision(
  conversation: Conversation,
  approvalId: string,
  decision: 'approve' | 'reject'
): Conversation {
  const approval = conversation.messages
    .flatMap(message => message.parts)
    .find((part): part is ApprovalPart => part.type === 'approval' && part.approvalId === approvalId)
    ?? conversation.pendingApproval;
  const toolId = approval?.approvalId === approvalId ? approval.toolId : null;

  const messages = conversation.messages.map(message => ({
    ...message,
    parts: message.parts.map(part =>
      part.type === 'approval' && part.approvalId === approvalId
        ? { ...part, status: decision === 'approve' ? 'approved' : 'rejected' } as ApprovalPart
        : part
    )
  }));

  let updated: Conversation = {
    ...conversation,
    messages,
    status: 'running',
    pendingApproval: null
  };

  if (toolId) {
    const awaiting = findLatestToolCall(updated.messages, part => part.toolId === toolId && part.status === 'awaiting-approval');
    if (awaiting) {
      updated = updateToolCall(updated, awaiting.invocationId, part => ({
        ...part,
        status: decision === 'approve' ? 'running' : 'rejected'
      }));
    }
  }

  return updated;
}

/**
 * True when an assistant message after the latest user message contains non-blank text,
 * so the latest turn produced a visible answer. Tool calls and approvals alone do not count.
 */
export function nhAssistantLatestTurnHasText(conversation: Conversation): boolean {
  for (let position = conversation.messages.length - 1; position >= 0; position--) {
    const message = conversation.messages[position];
    if (message.role === 'user') {
      return false;
    }
    if (message.role === 'assistant' && message.parts.some(part => part.type === 'text' && part.text.trim().length > 0)) {
      return true;
    }
  }

  return false;
}

function conversationStatusFor(status: TurnCompletionStatus): ConversationStatus {
  switch (status) {
    case 'completed':
    case 'cancelled':
      return 'idle';
    case 'waiting-for-approval':
      return 'waiting-for-approval';
    case 'failed':
      return 'failed';
  }
}

function createAssistantMessage(id: string): Message {
  return { id, role: 'assistant', createdAt: new Date().toISOString(), parts: [] };
}

function ensureMessage(messages: Message[], messageId: string): Message[] {
  return messages.some(message => message.id === messageId)
    ? messages
    : [...messages, createAssistantMessage(messageId)];
}

function appendText(message: Message, text: string): Message {
  const last = message.parts[message.parts.length - 1];
  if (last?.type === 'text') {
    return { ...message, parts: [...message.parts.slice(0, -1), { type: 'text', text: last.text + text }] };
  }

  return { ...message, parts: [...message.parts, { type: 'text', text }] };
}

function appendToCurrentAssistantMessage(conversation: Conversation, part: MessagePart): Conversation {
  let index = -1;
  for (let position = conversation.messages.length - 1; position >= 0; position--) {
    if (conversation.messages[position].role === 'assistant') {
      index = position;
      break;
    }
  }

  if (index < 0) {
    const message = createAssistantMessage(`local-${partKey(part)}`);
    return { ...conversation, messages: [...conversation.messages, { ...message, parts: [part] }] };
  }

  return {
    ...conversation,
    messages: conversation.messages.map((message, position) =>
      position === index ? { ...message, parts: [...message.parts, part] } : message
    )
  };
}

function partKey(part: MessagePart): string {
  if (part.type === 'tool-call') {
    return part.invocationId;
  }
  if (part.type === 'approval') {
    return part.approvalId;
  }

  return 'text';
}

function findToolCall(messages: Message[], invocationId: string): ToolCallPart | undefined {
  return findLatestToolCall(messages, part => part.invocationId === invocationId);
}

function findLatestToolCall(messages: Message[], predicate: (part: ToolCallPart) => boolean): ToolCallPart | undefined {
  for (let messageIndex = messages.length - 1; messageIndex >= 0; messageIndex--) {
    const parts = messages[messageIndex].parts;
    for (let partIndex = parts.length - 1; partIndex >= 0; partIndex--) {
      const part = parts[partIndex];
      if (part.type === 'tool-call' && predicate(part)) {
        return part;
      }
    }
  }

  return undefined;
}

function updateToolCall(
  conversation: Conversation,
  invocationId: string,
  update: (part: ToolCallPart) => ToolCallPart
): Conversation {
  return {
    ...conversation,
    messages: conversation.messages.map(message =>
      message.parts.some(part => part.type === 'tool-call' && part.invocationId === invocationId)
        ? {
          ...message,
          parts: message.parts.map(part =>
            part.type === 'tool-call' && part.invocationId === invocationId ? update(part) : part
          )
        }
        : message
    )
  };
}

function markLatestRunningToolAwaiting(
  conversation: Conversation,
  toolId: string,
  displayName?: string
): Conversation {
  const running = findLatestToolCall(conversation.messages, part => part.toolId === toolId && part.status === 'running');
  if (!running) {
    return conversation;
  }

  return updateToolCall(conversation, running.invocationId, part => ({
    ...part,
    displayName: displayName ?? part.displayName,
    status: 'awaiting-approval'
  }));
}

function replaceApproval(conversation: Conversation, approval: ApprovalPart): Conversation {
  return {
    ...conversation,
    messages: conversation.messages.map(message => ({
      ...message,
      parts: message.parts.map(part =>
        part.type === 'approval' && part.approvalId === approval.approvalId ? approval : part
      )
    }))
  };
}
