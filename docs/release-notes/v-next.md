# v-next

## @newheap/platform-ai-chat: a new chat after a chat about a subject

- After a chat with an agent that only a chat about a subject offers, "+" (`NhAssistantStore.startNewConversation`)
  kept that agent chosen. The picker then showed the first agent of the list, and the store chose the default
  agent only when the first message went out, so the message reached another agent than the picker showed.
  `startNewConversation` now chooses among the agents of a new chat right away, unless they are still loading.

## @newheap/platform-ai-chat: compact proposals, a compact board and a selection that follows the list

- The selection of `nh-assistant-agent-proposals` outlived its page: the inbox keeps the same list when it
  pages, so "Approve selected" also sent proposals selected on an earlier page without showing them. The
  selection now follows the list: a refresh of the same page keeps it, another page, filter or a decision
  elsewhere drops what the list no longer shows, and "Approve selected" and "Reject selected" send only ids
  of shown proposals.
