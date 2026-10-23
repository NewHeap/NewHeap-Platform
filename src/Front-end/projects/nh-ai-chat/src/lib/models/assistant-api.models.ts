/*
 * Wire models of the NewHeap assistant HTTP API.
 *
 * These interfaces are a literal translation of the assistant API contract. Keep the
 * field names identical to the JSON the server returns (camelCase, ISO-8601 UTC dates,
 * GUID strings for ids).
 */

import type { AgentSubjectRole } from './assistant-agent-run.models';

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
  /**
   * What people may attach to a message to this agent; `null` or missing when they can attach
   * nothing: the application keeps no files for the assistant, accepts no uploads, or the agent
   * accepts no files.
   */
  uploads?: AgentUploads | null;
}

/** What people may upload with `POST conversations/{id}/files` and attach to one message to an agent. */
export interface AgentUploads {
  /** Lowercase media types, for example `application/pdf`. */
  acceptedMediaTypes: string[];
  /** The largest file in bytes. */
  maxFileBytes: number;
  /** The most files of one message. */
  maxFiles: number;
  /**
   * The file name extensions the application mapped to one of `acceptedMediaTypes`
   * (`NhAssistantUploadOptions.MapExtension` on the server). A file without a usable type takes the
   * type of its extension from them before the platform's table, as on the server, and the file picker
   * offers them. Empty, or missing from an older server, when the application maps none.
   */
  mappedExtensions?: AgentUploadExtension[] | null;
}

/** A file name extension the application mapped to an accepted media type for uploads. */
export interface AgentUploadExtension {
  /** The extension in lowercase with its dot, for example `.dwg`. */
  extension: string;
  /** The accepted media type it names, for example `application/acad`. */
  mediaType: string;
}

/**
 * A file the application keeps, as the upload endpoint returns it and a message names it in
 * `files`. It never carries content.
 */
export interface FileReference {
  id: string;
  name: string;
  mediaType: string;
  size: number;
  /** The application's hash of the content, when it keeps one. */
  hash?: string | null;
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
  /** Default `chat` for servers without agent runs. */
  kind?: ConversationKind;
  /**
   * The subject of an agent thread and the caller's standing in it, including who may decide its
   * open proposals; `null` for a chat.
   */
  thread?: ConversationThread | null;
}

/** `chat` for a conversation people start; `thread` for the durable thread of an agent about one subject. */
export type ConversationKind = 'chat' | 'thread';

/**
 * The subject of an agent thread as the caller sees it. In a thread the subject's approvers decide
 * the agent's proposals, not the author of a turn. The server checks the role on every request.
 */
export interface ConversationThread {
  subjectType: string;
  subjectId: string;
  subjectDisplayName: string | null;
  /** The caller's live role on the subject. */
  role: AgentSubjectRole;
  /** Each open proposal of the thread and whether the caller may decide it now, oldest first. */
  decisions: ConversationThreadDecision[];
  /** The name of the subject type in the caller's language, when the application named it. */
  subjectTypeName?: string | null;
  /** Where the application shows the subject, when the type has a link template. */
  subjectLink?: string | null;
}

/**
 * The caller's right to decide one open proposal of an agent thread, checked like the decision
 * itself: the approval policy, four-eyes and the tool's approve policy.
 */
export interface ConversationThreadDecision {
  approvalId: string;
  /**
   * The run that proposed it; such a proposal is decided with `POST approvals/{id}/decisions`.
   * `null` for a proposal of a person's turn in the thread, decided in the conversation.
   */
  runId: string | null;
  canDecide: boolean;
  /** Why the caller may not decide, for example `assistant-four-eyes-required`; `null` when they may. */
  reasonCode: string | null;
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
  /** `application` for a message the application posted into the conversation. */
  role: 'user' | 'assistant' | 'tool' | 'application';
  createdAt: string;
  parts: MessagePart[];
  /** Position in the conversation; missing on optimistic messages and old servers. */
  sequence?: number;
  /** The writer of a user message. */
  authorActorId?: string | null;
  /**
   * Who wrote the message: a person or a trigger of an agent for user messages, `rule` for the
   * summary an agent's rules wrote instead of the model, `application` for a message the
   * application posted; missing on old servers.
   */
  authorKind?: MessageAuthorKind | null;
  /** The title, sender, link and trusted flag of a message the application posted; missing for other messages. */
  application?: ApplicationMessageDetails | null;
}

/**
 * `person` for a message a person wrote; `trigger` for one a trigger wrote into an agent thread;
 * `rule` for an assistant message the agent's rules wrote without a model; `application` for a
 * message the application posted into the conversation.
 */
export type MessageAuthorKind = 'person' | 'trigger' | 'rule' | 'application';

/**
 * What the application said about a message it posted, besides its text: an optional heading, who in
 * the application posted it, where people continue and whether the assistant follows it. The text is
 * the message's `text` part.
 */
export interface ApplicationMessageDetails {
  title: string | null;
  senderName: string | null;
  link: ApplicationMessageLink | null;
  /**
   * The assistant follows the message as the application's instruction (`Trusted` on the server);
   * `false` for a message the assistant reads as information. The thread labels a trusted message.
   */
  trusted: boolean;
}

/** A path within the application, which the chat opens through the router, or an `https` address. */
export interface ApplicationMessageLink {
  url: string;
  /** The text of the link; the chat shows its own text without one. */
  label: string | null;
}

export type MessagePart = TextPart | ToolCallPart | ApprovalPart | ConnectionRequiredPart | FilePart;

/**
 * A file a person attached to a message: its name, media type and size in bytes, never its content.
 * The application keeps the file; a later turn opens it again with the rights of that turn.
 */
export interface FilePart {
  type: 'file';
  name: string;
  mediaType: string;
  size: number;
}

/**
 * Why a turn asks the person to connect an MCP server: never connected, the connection ended, or
 * the server asked for permissions the connection lacks.
 */
export type ConnectionRequiredReason = 'not-connected' | 'reauthorization-required' | 'insufficient-scope';

/**
 * An MCP server, which each person connects to with their own account, that the person who asked had
 * to connect before the agent could use it. The turn went on without its tools; the card connects
 * the viewer's own account.
 */
export interface ConnectionRequiredPart {
  type: 'connection-required';
  serverId: string;
  displayName: string;
  reason: ConnectionRequiredReason;
}

export interface TextPart {
  type: 'text';
  text: string;
}

/**
 * `awaiting-approval` waits for a person. A call whose agent proposal closed without running ends
 * as `rejected`, as `not-executed` when the proposal expired, was withdrawn or became stale, or as
 * `failed` when its execution failed before it started; its `resultCode` is then
 * `assistant-proposal-<approval status>`, for example `assistant-proposal-expired`.
 */
export type ToolCallStatus = 'running' | 'succeeded' | 'failed' | 'awaiting-approval' | 'rejected' | 'not-executed';

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
  /**
   * `delegated`, `agent` or `delegated-read` when an agent used rights beyond the caller's own:
   * the initiator's, its own grant, or a person's through a gateway read.
   */
  grantSource?: 'delegated' | 'agent' | 'delegated-read' | 'accountable' | null;
  /** The result came from rights this viewer does not have and is shielded from them. */
  restricted?: boolean;
}

/**
 * Chat approvals are `pending`, `approved`, `rejected` or `expired`; proposals of agents that run on
 * their own continue through execution (`executing`, `executed`, `execution-failed`,
 * `execution-unknown`, `resolved`) or end as `withdrawn`, `stale` or `shadow`.
 */
export type ApprovalStatus =
  | 'pending'
  | 'approved'
  | 'rejected'
  | 'expired'
  | 'withdrawn'
  | 'stale'
  | 'executing'
  | 'executed'
  | 'execution-failed'
  | 'execution-unknown'
  | 'resolved'
  | 'shadow';

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

/**
 * Why a stale proposal no longer fits: the work was already done another way, what it was based on
 * changed, or it no longer applies.
 */
export type StaleKind = 'already-done' | 'changed' | 'not-applicable';

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
  /** Why a stale proposal no longer fits. Only sent for stale proposals; missing on older servers. */
  staleKind?: StaleKind;
  /** The application's explanation of a stale proposal in the reader's language, such as who did the work. */
  staleExplanation?: string;
  /** Why the agent's rules proposed it, in the reader's language. Only sent for rule proposals. */
  rationale?: string;
  /** The newer proposal for the same objects that replaced this withdrawn one. Only sent then. */
  supersededByApprovalId?: string;
  /**
   * The consultation that proposed it, when the conversation's assistant consulted another agent and
   * the person who asked decides that agent's proposal here (`asker-decides`). Approving executes it
   * in the person's own request with their own rights. Missing for the conversation's own proposals.
   */
  consultation?: ApprovalConsultation | null;
}

/**
 * How a consulted agent's proposal is decided and whose rights execute it: `asker-decides`, the person
 * who asked decides it in their own chat and it runs with their own rights; `subject-agent-rights`, the
 * subject's approvers other than the asker decide it and it runs with the agent's own rights;
 * `subject-asker-rights`, the subject's approvers decide it and it runs with the asker's rights.
 */
export type NhAssistantConsultationRoute = 'asker-decides' | 'subject-agent-rights' | 'subject-asker-rights';

/** The routes at the subject a person may leave a consulted agent's proposal for (`decision: 'leave'`). */
export type NhAssistantConsultationLeaveRoute = Exclude<NhAssistantConsultationRoute, 'asker-decides'>;

/** Where a proposal the person who asked decides in their own conversation comes from. */
export interface ApprovalConsultation {
  /** The consultation record in the consulted agent's thread. */
  consultationId: string;
  /** The consulted agent that proposed it. */
  agentId: string;
  subjectType: string;
  subjectId: string;
  /** The subject's name as the consulted agent's thread knows it; `null` when unknown. */
  subjectDisplayName: string | null;
  /**
   * The route at the subject the person may leave the pending proposal for with the decision `leave`;
   * `null` when they can only approve or reject it.
   */
  leaveRoute: NhAssistantConsultationLeaveRoute | null;
  /** The consulted agent's answer, bounded, when the consultation's classification allows a preview. */
  answer: string | null;
  /** The proposal that waits at the subject since the person left this one for its approvers. */
  movedToApprovalId: string | null;
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
  /** Files the person attaches, as `POST conversations/{id}/files` returned them. */
  files?: FileReference[];
}

/**
 * `approve` or `reject`; for a consulted agent's proposal with a `consultation.leaveRoute` also
 * `leave`, which leaves it for the subject's approvers instead of deciding it.
 */
export type ApprovalDecision = 'approve' | 'reject' | 'leave';

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
