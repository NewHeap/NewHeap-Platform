/** Fragment the panel reads to join a shared conversation: `#nh-assistant-join=<id>.<token>`. */
export const NH_ASSISTANT_JOIN_FRAGMENT = 'nh-assistant-join';

/** Fragment a clicked notification opens: `#nh-assistant-conversation=<id>`. */
export const NH_ASSISTANT_CONVERSATION_FRAGMENT = 'nh-assistant-conversation';

/** Builds the default invitation link: the current page with the join fragment. */
export function nhAssistantShareLink(conversationId: string, token: string): string {
  const location = globalThis.location;
  const page = location ? `${location.origin}${location.pathname}${location.search}` : '';
  return `${page}#${NH_ASSISTANT_JOIN_FRAGMENT}=${encodeURIComponent(conversationId)}.${encodeURIComponent(token)}`;
}
