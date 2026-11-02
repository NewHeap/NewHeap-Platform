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
- `nh-assistant-agent-proposals` and `nh-assistant-agent-inbox` take `density: 'compact'`
  (`NhAssistantProposalDensity`) for lists of many proposals: one row per proposal with its object, kind of
  action, summary on one line, agent and expiry, and the markers that must not disappear (status once it no
  longer waits, new, waiting for someone else, from a consultation, the source of its rights). A row expands
  in place to its card. The rows share one tab stop with the arrow keys, Home and End; Enter expands and
  Space selects. While the focus is in a row, and never in a field or the feedback form, `j` and `k` move,
  `x` selects, `a` approves and `r` rejects that row's proposal, never the selection, and `?` lists the keys.
  Rejecting by key asks afterwards what was wrong, as a click does. The comfortable cards stay the default.
- `nh-assistant-agent-board` takes `compact` for a board of busy agents: what an agent is about folds behind
  "About this agent", the latest result shows two lines with "More", which opens the whole result, and the
  count of open proposals becomes the way to them. Its new output `openInbox` (`NhAssistantBoardInboxRequest`:
  the agent and the board's division) lets the host route to its own filtered inbox; while a host listens,
  the count is that button in either density and the tile has no inline inbox. Without a listener a compact
  tile's count opens the tile's own inbox and a comfortable tile stays as it was.
