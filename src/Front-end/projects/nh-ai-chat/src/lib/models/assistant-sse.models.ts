import { ApprovalPart } from './assistant-api.models';

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
}

export type ApprovalRequiredEvent = ApprovalPart;

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

/** Map from SSE event name to its data payload. */
export interface NhAssistantSseEventMap {
  'turn.started': TurnStartedEvent;
  'message.delta': MessageDeltaEvent;
  'tool.started': ToolStartedEvent;
  'tool.completed': ToolCompletedEvent;
  'approval.required': ApprovalRequiredEvent;
  'turn.completed': TurnCompletedEvent;
  'error': AssistantErrorEvent;
}

export type NhAssistantSseEventType = keyof NhAssistantSseEventMap;

/** One parsed assistant event, discriminated by its SSE event name. */
export type NhAssistantSseEvent = {
  [K in NhAssistantSseEventType]: { type: K; data: NhAssistantSseEventMap[K] };
}[NhAssistantSseEventType];

export const NH_ASSISTANT_SSE_EVENT_TYPES: readonly NhAssistantSseEventType[] = [
  'turn.started',
  'message.delta',
  'tool.started',
  'tool.completed',
  'approval.required',
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
  invalidResponse: 'assistant-invalid-response',
  streamInterrupted: 'assistant-stream-interrupted',
  server: 'assistant-server'
} as const;

export type NhAssistantClientErrorCode = typeof NhAssistantClientErrorCodes[keyof typeof NhAssistantClientErrorCodes];
