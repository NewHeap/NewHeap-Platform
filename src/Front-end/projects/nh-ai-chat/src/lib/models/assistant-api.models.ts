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

/** Body of `POST conversations/{id}/messages`. */
export interface SendMessageRequest {
  text: string;
  clientMessageId: string;
}

export type ApprovalDecision = 'approve' | 'reject';

/** Body of `POST conversations/{id}/approvals/{approvalId}/decide`. */
export interface DecideApprovalRequest {
  decision: ApprovalDecision;
  expectedProposalHash: string;
  reason?: string;
}
