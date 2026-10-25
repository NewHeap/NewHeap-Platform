import {
  ApprovalDecision,
  ApprovalPart,
  Conversation,
  ConversationStatus,
  Message,
  MessagePart,
  QuestionPart,
  ToolCallPart
} from '../models/assistant-api.models';
import { LiveConversationChanged, LiveConversationEvent } from '../models/assistant-live.models';
import { NH_ASSISTANT_SSE_EVENT_TYPES, NhAssistantSseEvent, TurnCompletionStatus } from '../models/assistant-sse.models';

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

    case 'question.asked': {
      const question: QuestionPart = { ...event.data, type: 'question' };
      // The ask_question call waits for the answer as a call waits for an approval.
      const withToolAwaiting = updateToolCall(conversation, question.questionId, part => ({ ...part, status: 'awaiting-answer' }));
      const withQuestion = hasQuestion(withToolAwaiting, question.questionId)
        ? replaceQuestion(withToolAwaiting, question)
        : appendToCurrentAssistantMessage(withToolAwaiting, question);

      return { ...withQuestion, status: 'waiting-for-approval', pendingQuestion: question };
    }

    case 'question.answered': {
      const question: QuestionPart = { ...event.data, type: 'question' };
      return {
        ...replaceQuestion(conversation, question),
        pendingQuestion: conversation.pendingQuestion?.questionId === question.questionId ? null : conversation.pendingQuestion ?? null
      };
    }

    case 'connection.required': {
      const data = event.data;
      // One card per server and answer, also when a live update repeats the event.
      const current = [...conversation.messages].reverse().find(message => message.role === 'assistant');
      if (current?.parts.some(part => part.type === 'connection-required' && part.serverId === data.serverId)) {
        return conversation;
      }
      return appendToCurrentAssistantMessage(conversation, {
        type: 'connection-required',
        serverId: data.serverId,
        displayName: data.displayName,
        reason: data.reason
      });
    }

    case 'turn.completed': {
      const status = conversationStatusFor(event.data.status);
      return {
        ...conversation,
        status,
        pendingApproval: status === 'waiting-for-approval' ? conversation.pendingApproval : null,
        pendingQuestion: status === 'waiting-for-approval' ? conversation.pendingQuestion ?? null : null
      };
    }

    case 'error':
      return {
        ...conversation,
        status: conversation.status === 'running' ? 'failed' : conversation.status
      };
  }
}

/**
 * Records the user's approval decision locally until the server state is reloaded. A consulted
 * agent's proposal that the user left for the subject's approvers (`leave`) closes here as the server
 * closes it, rejected in this conversation, and can no longer be left; the waiting tool call goes on.
 */
export function applyNhAssistantApprovalDecision(
  conversation: Conversation,
  approvalId: string,
  decision: ApprovalDecision
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
        ? decidedApproval(part, decision)
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
        status: decision === 'reject' ? 'rejected' : 'running'
      }));
    }
  }

  return updated;
}

/**
 * Records the addressee's answer to a question locally until the server state is reloaded: the
 * question reads as answered with the chosen index and the turn runs again.
 */
export function applyNhAssistantQuestionAnswer(
  conversation: Conversation,
  questionId: string,
  choice: number
): Conversation {
  const messages = conversation.messages.map(message => ({
    ...message,
    parts: message.parts.map(part =>
      part.type === 'question' && part.questionId === questionId
        ? { ...part, status: 'answered' as const, answerIndex: choice }
        : part
    )
  }));

  return updateToolCall(
    { ...conversation, messages, status: 'running', pendingQuestion: null },
    questionId,
    part => ({ ...part, status: 'running' }));
}

/** An approval part after the user's decision. */
function decidedApproval(part: ApprovalPart, decision: ApprovalDecision): ApprovalPart {
  const decided: ApprovalPart = { ...part, status: decision === 'approve' ? 'approved' : 'rejected' };
  if (decision === 'leave' && part.consultation) {
    decided.consultation = { ...part.consultation, leaveRoute: null };
  }
  return decided;
}

/**
 * Applies a live update of another session: `message.created` adds a participant's or the
 * application's message once, where its sequence number puts it; the turn events behave exactly
 * like the starting request's stream. Unknown types are ignored.
 */
export function applyNhAssistantLiveEvent(conversation: Conversation, event: LiveConversationEvent): Conversation {
  if (event.type === 'message.created') {
    const message = event.data as Message;
    if (!message?.id || conversation.messages.some(existing => existing.id === message.id)) {
      return conversation;
    }
    return { ...conversation, messages: insertBySequence(conversation.messages, message) };
  }

  if (!(NH_ASSISTANT_SSE_EVENT_TYPES as readonly string[]).includes(event.type)) {
    return conversation;
  }
  return applyNhAssistantEvent(conversation, { type: event.type, data: event.data } as NhAssistantSseEvent);
}

/** Applies the shared state of a conversation that a live update announced. */
export function applyNhAssistantConversationChange(conversation: Conversation, change: LiveConversationChanged): Conversation {
  return {
    ...conversation,
    status: change.status,
    title: change.title,
    updatedAt: change.updatedAt,
    activeActorId: change.activeActorId,
    lastMessageSequence: change.lastMessageSequence,
    participantCount: change.participantCount,
    pendingApproval: change.status === 'waiting-for-approval' ? conversation.pendingApproval : null,
    pendingQuestion: change.status === 'waiting-for-approval' ? conversation.pendingQuestion ?? null : null
  };
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

/**
 * Places a stored message before the messages at the end that have a higher sequence number. A
 * message without a number, such as an optimistic message or the answer a running turn is still
 * writing, counts as earlier: the server numbers a turn's messages when the turn starts, so a message
 * stored during the turn follows its answer. A message without a number of its own goes last.
 */
function insertBySequence(messages: Message[], message: Message): Message[] {
  const sequence = message.sequence;
  let index = messages.length;
  if (typeof sequence === 'number' && sequence > 0) {
    while (index > 0) {
      const previous = messages[index - 1].sequence;
      if (typeof previous !== 'number' || previous <= sequence) {
        break;
      }
      index--;
    }
  }

  return [...messages.slice(0, index), message, ...messages.slice(index)];
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
  if (part.type === 'connection-required') {
    return `connection-${part.serverId}`;
  }
  if (part.type === 'question') {
    return `question-${part.questionId}`;
  }

  return 'text';
}

function hasQuestion(conversation: Conversation, questionId: string): boolean {
  return conversation.messages.some(message =>
    message.parts.some(part => part.type === 'question' && part.questionId === questionId));
}

function replaceQuestion(conversation: Conversation, question: QuestionPart): Conversation {
  return {
    ...conversation,
    messages: conversation.messages.map(message => ({
      ...message,
      parts: message.parts.map(part =>
        part.type === 'question' && part.questionId === question.questionId ? question : part
      )
    }))
  };
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
