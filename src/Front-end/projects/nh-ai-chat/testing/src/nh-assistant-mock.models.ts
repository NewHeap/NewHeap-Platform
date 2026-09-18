import { AgentSummary, AssistantStatus, Conversation } from '@newheap/platform-ai-chat';

/** Streams text as `message.delta` events, split into word-sized pieces. */
export interface NhAssistantMockTextStep {
  text: string;
}

/** Runs one tool: `tool.started` followed by `tool.completed`. */
export interface NhAssistantMockToolStep {
  tool: {
    toolId: string;
    displayName: string;
    toolVersion?: number;
    argumentsPreview?: string | null;
    resultPreview?: string | null;
    /** Default `succeeded`. */
    status?: 'succeeded' | 'failed';
    resultCode?: string | null;
  };
}

/**
 * Pauses the turn for approval: `tool.started`, `approval.required` and
 * `turn.completed` with status `waiting-for-approval`. The decision resumes the turn with
 * the `approved` or `rejected` steps.
 */
export interface NhAssistantMockApprovalStep {
  approval: {
    toolId: string;
    displayName: string;
    toolVersion?: number;
    summary: string;
    argumentsPreview: string;
    targets: string[];
    /** Default 300 seconds. */
    expiresInSeconds?: number;
    /** Result preview of the tool after approval. */
    resultPreview?: string | null;
    approved: NhAssistantMockStep[];
    rejected: NhAssistantMockStep[];
  };
}

/** Ends the stream with an `error` event. */
export interface NhAssistantMockErrorStep {
  error: {
    code: string;
    messageKey: string;
  };
}

/** Ends the turn with `turn.completed` status `failed`. */
export interface NhAssistantMockFailStep {
  fail: {
    errorCode: string;
  };
}

export type NhAssistantMockStep =
  | NhAssistantMockTextStep
  | NhAssistantMockToolStep
  | NhAssistantMockApprovalStep
  | NhAssistantMockErrorStep
  | NhAssistantMockFailStep;

/** A scripted assistant turn, chosen by matching the user's message. */
export interface NhAssistantMockTurn {
  /** Case-insensitive substring, regular expression or predicate. Omit to match every message. */
  match?: string | RegExp | ((text: string, agentId: string) => boolean);
  steps: NhAssistantMockStep[];
}

export interface NhAssistantMockTiming {
  /** Delay before the first event of a stream. Default 200 ms. */
  firstEventDelayMs?: number;
  /** Delay between events. Default 40 ms. */
  eventDelayMs?: number;
  /** Size of the byte chunks the stream is cut into, to exercise the client parser. Default 23. */
  chunkSize?: number;
}

/** The scenario the mock API plays without a back-end. */
export interface NhAssistantMockScenario {
  /** Default `true`. */
  enabled?: boolean;
  limits?: Partial<AssistantStatus['limits']>;
  agents: AgentSummary[];
  /** Conversations that exist before the first request. */
  conversations?: Conversation[];
  /** Tried in order; the first matching turn answers. */
  turns: NhAssistantMockTurn[];
  timing?: NhAssistantMockTiming;
}

/** One request the mock API received, for assertions in tests. */
export interface NhAssistantMockRequest {
  method: string;
  path: string;
  headers: Record<string, string>;
  body: unknown;
}
