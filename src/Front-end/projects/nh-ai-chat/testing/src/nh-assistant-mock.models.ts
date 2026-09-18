import {
  AdminAgentInput,
  AgentSummary,
  AssistantPreferences,
  AssistantStatus,
  ClientContext,
  Conversation,
  McpServerInput,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';

/** Streams text as `message.delta` events, split into word-sized pieces. */
export interface NhAssistantMockTextStep {
  /** Fixed text, or text built from the page context the client sent with the message. */
  text: string | ((pageContext: ClientContext | null) => string);
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
  match?: string | RegExp | ((text: string, agentId: string, pageContext: ClientContext | null) => boolean);
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

/** A tool that a simulated remote MCP server lists. */
export interface NhAssistantMockRemoteTool {
  remoteName: string;
  description: string;
  /** Changing the hash between two syncs simulates a changed remote input schema. */
  inputSchemaHash: string;
  /** Remote annotation; the mock passes it on as an untrusted hint. */
  readOnlyHint?: boolean | null;
}

/** A simulated MCP server. The mock stores `secret` but never returns it. */
export interface NhAssistantMockMcpServer extends McpServerInput {
  remoteTools?: NhAssistantMockRemoteTool[];
}

/** A preconfigured agent. Code agents default to the scenario's chat agents. */
export interface NhAssistantMockAdminAgent extends AdminAgentInput {
  source: 'code' | 'admin';
}

/**
 * Administration data of the mock. Hosts without an admin scenario still get working
 * `admin/*` endpoints with code agents derived from `agents` and an empty context.
 */
export interface NhAssistantMockAdminScenario {
  /** Default `true`. Without it `admin/*` answers `403`. */
  canAdminister?: boolean;
  context?: string;
  /** Tool catalog for the selector picker. Enabled MCP tools are added automatically. */
  tools?: ToolCatalogEntry[];
  agents?: NhAssistantMockAdminAgent[];
  mcpServers?: NhAssistantMockMcpServer[];
  /** Policies the host knows; `requiredPolicy` must be one of them. Default: any value. */
  policies?: string[];
  /** Hosts that may receive the user's token (`forward-user-token`). Default: none. */
  forwardUserTokenHosts?: string[];
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
  /** Stored preferences of the caller. Default: default style, informal, normal length. */
  preferences?: AssistantPreferences;
  admin?: NhAssistantMockAdminScenario;
}

/** One request the mock API received, for assertions in tests. */
export interface NhAssistantMockRequest {
  method: string;
  path: string;
  headers: Record<string, string>;
  body: unknown;
}
