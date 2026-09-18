/*
 * Wire models of the NewHeap assistant HTTP API.
 *
 * These interfaces are a literal translation of the assistant API contract. Keep the
 * field names identical to the JSON the server returns (camelCase, ISO-8601 UTC dates,
 * GUID strings for ids).
 */

export interface AssistantStatus {
  enabled: boolean;
  agents: AgentSummary[];
  limits: {
    maxMessageChars: number;
    maxToolCallsPerTurn: number;
  };
  /** True when the caller also passes the admin policy and may use the `admin/*` endpoints. */
  canAdminister: boolean;
}

export interface AgentSummary {
  id: string;
  version: number;
  displayNameKey: string;
  descriptionKey: string;
  canMutate: boolean;
}

export interface ConversationSummary {
  id: string;
  agentId: string;
  title: string | null;
  status: ConversationStatus;
  createdAt: string;
  updatedAt: string;
}

export interface Conversation extends ConversationSummary {
  agentVersion: number;
  messages: Message[];
  pendingApproval: ApprovalPart | null;
}

export type ConversationStatus = 'idle' | 'running' | 'waiting-for-approval' | 'failed' | 'archived';

export interface Message {
  id: string;
  role: 'user' | 'assistant' | 'tool';
  createdAt: string;
  parts: MessagePart[];
}

export type MessagePart = TextPart | ToolCallPart | ApprovalPart;

export interface TextPart {
  type: 'text';
  text: string;
}

export type ToolCallStatus = 'running' | 'succeeded' | 'failed' | 'awaiting-approval' | 'rejected';

export interface ToolCallPart {
  type: 'tool-call';
  invocationId: string;
  toolId: string;
  toolVersion: number;
  displayName: string;
  status: ToolCallStatus;
  argumentsPreview: string | null;
  resultPreview: string | null;
  resultCode: string | null;
}

export type ApprovalStatus = 'pending' | 'approved' | 'rejected' | 'expired';

export interface ApprovalPart {
  type: 'approval';
  approvalId: string;
  proposalId: string;
  proposalHash: string;
  toolId: string;
  summary: string;
  argumentsPreview: string;
  targets: string[];
  expiresAt: string;
  status: ApprovalStatus;
}

/** Response of `GET conversations?page=&itemsPerPage=`. */
export interface ConversationPage {
  items: ConversationSummary[];
  total: number;
}

/** Body of `POST conversations`. */
export interface CreateConversationRequest {
  agentId: string;
  title?: string;
}

/** An entity the user has open, for example `{ type: 'project', id: 'AA09027', label: 'Project AA09027' }`. */
export interface ClientContextEntity {
  /** Dash-case, for example `order-group`. */
  type: string;
  /** At most 64 characters. */
  id: string;
  /** At most 120 characters. */
  label?: string | null;
}

/**
 * What the user has open when sending a message. Untrusted page data for the model:
 * entity ids are search hints only and never grant access.
 */
export interface ClientContext {
  /** For example `/order-group/123`; at most 200 characters. */
  route: string;
  /** Page title; at most 120 characters. */
  title?: string | null;
  /** At most 5 entities. */
  entities?: ClientContextEntity[];
}

/** Library name of the contract's `ClientContext`. */
export type NhAssistantClientContext = ClientContext;

/** Body of `POST conversations/{id}/messages`. */
export interface SendMessageRequest {
  text: string;
  clientMessageId: string;
  /** Page context of this message; `null` when the user left it out. */
  clientContext?: ClientContext | null;
}

export type ApprovalDecision = 'approve' | 'reject';

/** Body of `POST conversations/{id}/approvals/{approvalId}/decide`. */
export interface DecideApprovalRequest {
  decision: ApprovalDecision;
  expectedProposalHash: string;
  reason?: string;
}
