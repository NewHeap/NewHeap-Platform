import { AgentTriggerReceipt } from './assistant-agent-run.models';
import { ApprovalPart, ClientToolRequest, ConnectionRequiredReason, ElicitationPart, QuestionPart, ToolResultPresentation } from './assistant-api.models';

/*
 * Server-sent events of an assistant turn.
 *
 * Every event arrives as `event: <name>` plus `data: <JSON>` followed by an empty line.
 * The stream closes after `turn.completed` or `error`.
 */

export interface TurnStartedEvent {
  turnId: string;
  userMessageId: string;
  assistantMessageId: string;
}

export interface MessageDeltaEvent {
  messageId: string;
  text: string;
}

export interface ToolStartedEvent {
  invocationId: string;
  toolId: string;
  toolVersion: number;
  displayName: string;
  argumentsPreview: string | null;
}

export interface ToolCompletedEvent {
  invocationId: string;
  status: 'succeeded' | 'failed';
  resultCode: string | null;
  resultPreview: string | null;
  /**
   * The application's trusted card of what the call returned, in the language of the turn. Only sent
   * for a call the application presents and whose result is not shielded; missing on older servers.
   */
  resultPresentation?: ToolResultPresentation | null;
}

export type ApprovalRequiredEvent = ApprovalPart;

/** The agent asked a person of the conversation a question; the turn waits for the answer. */
export type QuestionAskedEvent = QuestionPart;

/** The addressee answered the question; the turn continues with the answer. */
export type QuestionAnsweredEvent = QuestionPart;

/**
 * The agent called a client tool; the turn waits for the browser of `actorId` to run it. Only that
 * person's own stream carries the `arguments`; the store runs the registered handler once the stream
 * ended and reports its result.
 */
export type ClientToolRequestedEvent = ClientToolRequest;

/** An MCP server asked the person of the turn for input while its tool runs; the call waits. */
export type ElicitationRequestedEvent = ElicitationPart;

/** The request for input was answered, declined, cancelled or expired; the call continues. */
export type ElicitationClosedEvent = ElicitationPart;

/** The person who asked has to connect an MCP server before the agent can use it. */
export interface ConnectionRequiredEvent {
  serverId: string;
  displayName: string;
  reason: ConnectionRequiredReason;
}

export type TurnCompletionStatus = 'completed' | 'waiting-for-approval' | 'cancelled' | 'failed';

export interface TurnUsage {
  inputTokens: number;
  outputTokens: number;
  toolCalls: number;
}

export interface TurnCompletedEvent {
  turnId: string;
  status: TurnCompletionStatus;
  usage: TurnUsage;
  errorCode: string | null;
}

export interface AssistantErrorEvent {
  code: string;
  messageKey: string;
}

/**
 * The server accepted a message for the next run of the agent that works in the thread, with its
 * files, instead of starting a turn: it answered `202` with the trigger receipt. The client emits it
 * in place of a stream, which then ends; a server never streams it.
 */
export type MessageQueuedEvent = AgentTriggerReceipt;

/** Map from SSE event name to its data payload. */
export interface NhAssistantSseEventMap {
  'turn.started': TurnStartedEvent;
  'message.delta': MessageDeltaEvent;
  'tool.started': ToolStartedEvent;
  'tool.completed': ToolCompletedEvent;
  'approval.required': ApprovalRequiredEvent;
  'question.asked': QuestionAskedEvent;
  'question.answered': QuestionAnsweredEvent;
  'client-tool.requested': ClientToolRequestedEvent;
  'elicitation.requested': ElicitationRequestedEvent;
  'elicitation.closed': ElicitationClosedEvent;
  'connection.required': ConnectionRequiredEvent;
  'turn.completed': TurnCompletedEvent;
  'error': AssistantErrorEvent;
}

export type NhAssistantSseEventType = keyof NhAssistantSseEventMap;

/** One parsed assistant event, discriminated by its SSE event name. */
export type NhAssistantSseEvent = {
  [K in NhAssistantSseEventType]: { type: K; data: NhAssistantSseEventMap[K] };
}[NhAssistantSseEventType];

/**
 * What `NhAssistantApiService.sendMessage` emits: the events of the turn it started, or, when the
 * message waits for the next run of the agent working in the thread, one `message.queued` event.
 */
export type NhAssistantMessageEvent = NhAssistantSseEvent | { type: 'message.queued'; data: MessageQueuedEvent };

export const NH_ASSISTANT_SSE_EVENT_TYPES: readonly NhAssistantSseEventType[] = [
  'turn.started',
  'message.delta',
  'tool.started',
  'tool.completed',
  'approval.required',
  'question.asked',
  'question.answered',
  'client-tool.requested',
  'elicitation.requested',
  'elicitation.closed',
  'connection.required',
  'turn.completed',
  'error'
];

/**
 * Stable client-side failure codes. The client emits these as `error` events when the
 * transport fails before the server could send its own `error` event.
 */
export const NhAssistantClientErrorCodes = {
  network: 'assistant-network',
  unauthenticated: 'assistant-unauthenticated',
  forbidden: 'assistant-forbidden',
  notFound: 'assistant-not-found',
  conversationBusy: 'assistant-conversation-busy',
  validation: 'assistant-validation',
  versionConflict: 'assistant-version-conflict',
  invalidResponse: 'assistant-invalid-response',
  streamInterrupted: 'assistant-stream-interrupted',
  server: 'assistant-server',
  /** The open conversation was deleted by its owner, or the user left or was removed. */
  conversationRemoved: 'assistant-conversation-removed',
  /** The person closed the authorization window of an MCP connection before it finished. */
  mcpAuthorizationCancelled: 'assistant-mcp-authorization-cancelled',
  /** The browser blocked the authorization window of an MCP connection. */
  mcpPopupBlocked: 'assistant-mcp-popup-blocked'
} as const;

export type NhAssistantClientErrorCode = typeof NhAssistantClientErrorCodes[keyof typeof NhAssistantClientErrorCodes];
