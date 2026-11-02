# v-next

## @newheap/platform-ai-chat: a new chat after a chat about a subject

- After a chat with an agent that only a chat about a subject offers, "+" (`NhAssistantStore.startNewConversation`)
  kept that agent chosen. The picker then showed the first agent of the list, and the store chose the default
  agent only when the first message went out, so the message reached another agent than the picker showed.
  `startNewConversation` now chooses among the agents of a new chat right away, unless they are still loading.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the inbox by kind of action

- `GET approvals/inbox` filters on the kind of action with `toolId` (repeated, at most 20) and on the decision
  deadline with `expiresBefore`, and orders by `sort=expiresAt` or `sort=createdAt` (`NhAssistantInboxSorts`),
  earliest first, flat and grouped. Another sort or an empty kind answers `400 assistant-message-invalid`.
- `GET approvals/inbox/summary` counts what the inbox lists with the same filter and visibility
  (`NhAssistantInboxSummaryDto`): in total, per agent, per kind of action with the name its cards show (the
  stored presentation in the caller's language, otherwise the tool id), per subject type with its distinct
  subjects, and how many expire within 24 hours on the application's clock. The counts say what is listed;
  deciding still checks four-eyes, the approver rules and the tool's approve policy per proposal.
- Each count is a grouped query on columns the approval row already has. No migration is needed.

## @newheap/platform-ai-chat: the inbox by kind of action

- `nh-assistant-agent-inbox` takes `toolId` (one or several kinds of action), `expiresBefore` and `sort`
  (`AgentInboxSort`); changing one reloads it from the first page. `AgentInboxFilter` has the same fields and
  `getInbox` sends one `toolId` parameter per kind.
- `NhAssistantAgentRunApiService.getInboxSummary(filter)` (`AgentInboxSummaryFilter`, `AgentInboxSummary`) counts
  what the inbox lists per agent, kind of action and subject type, for a host's own filter; label the counts
  "listed".
- The mock API of `@newheap/platform-ai-chat/testing` filters and sorts the inbox the same way and serves
  `GET approvals/inbox/summary`.
