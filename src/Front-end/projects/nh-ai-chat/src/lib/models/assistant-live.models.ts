import { ConversationStatus } from './assistant-api.models';

/*
 * Live updates of the assistant hub (`GET status` → `collaboration.hubPath`). They are best
 * effort: after a reconnect the client reloads snapshots, which stay authoritative.
 */

/** `ConversationChanged`: status, title, active actor, latest message or participants changed. */
export interface LiveConversationChanged {
  conversationId: string;
  status: ConversationStatus;
  title: string | null;
  updatedAt: string;
  activeActorId: string | null;
  lastMessageSequence: number;
  participantCount: number;
}

/** `ConversationRead`: the current user read the conversation, for example in another tab. */
export interface LiveConversationRead {
  conversationId: string;
  lastReadSequence: number;
}

/** `ConversationRemoved`: the conversation is no longer available to the current user. */
export interface LiveConversationRemoved {
  conversationId: string;
}

/** What changed in an agent thread: a run, a proposal or the feedback on a proposal. */
export type LiveAgentWorkReason = 'run' | 'proposal' | 'feedback';

/**
 * `AgentWorkChanged`: a run of an agent thread started, waits, ended or was cancelled, a proposal
 * was decided, resolved, executed, closed, escalated or expired, or feedback was given. Sent to the
 * thread's participants and everyone on its subject. It carries no content: reload the agent-run
 * endpoints that show the thread, which check access again. It names the thread's subject, so
 * lists and pages of the application refresh by subject without knowing conversation ids.
 */
export interface LiveAgentWorkChanged {
  conversationId: string;
  reason: LiveAgentWorkReason;
  approvalId: string | null;
  runId: string | null;
  subjectType: string;
  subjectId: string;
}

/**
 * The hub's answer to `WatchAgentWork`: how many agents' work of the whole application the connection
 * follows now, and when to renew the watch so the server checks the person's rights again.
 */
export interface LiveAgentWorkWatch {
  watched: number;
  refreshAfterSeconds: number;
}

/**
 * `ConversationEvent`: one turn event with the same `type` and `data` as the server-sent events
 * of the starting request, or `message.created` with a `Message` when a participant's message
 * was stored.
 */
export interface LiveConversationEvent {
  conversationId: string;
  actorId: string;
  type: string;
  data: unknown;
}
