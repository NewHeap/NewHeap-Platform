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
  /** Sharing, live update and notification features; missing on servers without them. */
  collaboration?: AssistantCollaboration | null;
}

export interface AssistantCollaboration {
  /** Owners can invite colleagues directly through the application's directory. */
  directory: boolean;
  /** Path of the SignalR hub for live updates, for example `/hub/assistant`; `null` when off. */
  hubPath: string | null;
  /** Web Push notifications are configured on the server. */
  push: boolean;
  /** Maximum participants per conversation, not counting the owner. */
  maxParticipants: number;
}

/** The caller's relation to a conversation. */
export type ConversationRole = 'owner' | 'participant';

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
  /** Default `owner` for servers without sharing. */
  role?: ConversationRole;
  /** People the owner shared the conversation with. */
  participantCount?: number;
  /** Sequence of the latest message. */
  lastMessageSequence?: number;
  /** The caller's read position; the conversation is unread while it is lower than `lastMessageSequence`. */
  lastReadSequence?: number;
  /** The owner or participant whose turn runs or waits for approval. */
  activeActorId?: string | null;
}

export interface Conversation extends ConversationSummary {
  agentVersion: number;
  messages: Message[];
  pendingApproval: ApprovalPart | null;
  /** The owner followed by the participants; empty while the conversation is not shared. */
  members?: ConversationMember[];
  /** The current invitation-link token; only for the owner. */
  shareToken?: string | null;
  /** The caller's actor id, to recognize the caller's own messages. */
  currentActorId?: string | null;
}

export type ConversationStatus = 'idle' | 'running' | 'waiting-for-approval' | 'failed' | 'archived';

/** The owner or a participant of a shared conversation. */
export interface ConversationMember {
  actorId: string;
  /** `null` when the application provides no name. */
  displayName: string | null;
  role: ConversationRole;
  joinedAt: string;
}

export interface Message {
  id: string;
  role: 'user' | 'assistant' | 'tool';
  createdAt: string;
  parts: MessagePart[];
  /** Position in the conversation; missing on optimistic messages and old servers. */
  sequence?: number;
  /** The writer of a user message. */
  authorActorId?: string | null;
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

export interface ApprovalPresentationField {
  label: string;
  value: string;
}

export interface ApprovalPresentation {
  toolDisplayName: string;
  summary: string;
  fields: ApprovalPresentationField[];
  notice: string | null;
}

export interface ApprovalPart {
  type: 'approval';
  approvalId: string;
  proposalId: string;
  proposalHash: string;
  toolId: string;
  summary: string;
  /** Trusted server-side presentation. Missing on old events and when no presenter handled the tool. */
  presentation?: ApprovalPresentation | null;
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

/** Response of `POST conversations/{id}/share-link`. */
export interface ShareLink {
  token: string;
}

/** A person the owner may invite, from the application's directory. */
export interface DirectoryEntry {
  actorId: string;
  displayName: string;
  detail: string | null;
}

/** Response of `GET` and `PUT notifications`. */
export interface NotificationSettings {
  /** The caller's choice; on by default. */
  pushEnabled: boolean;
  /** The server has Web Push configured. */
  pushAvailable: boolean;
  /** The VAPID key browsers subscribe with. */
  publicKey: string | null;
}

/** Body of `PUT notifications/push-subscription`: `PushSubscription.toJSON()` plus the text language. */
export interface PushSubscriptionRequest {
  endpoint: string;
  keys: { p256dh: string; auth: string };
  language?: string;
}
