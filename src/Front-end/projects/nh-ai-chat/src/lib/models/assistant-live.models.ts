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
