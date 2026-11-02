# v-next

## @newheap/platform-ai-chat: a new chat after a chat about a subject

- After a chat with an agent that only a chat about a subject offers, "+" (`NhAssistantStore.startNewConversation`)
  kept that agent chosen. The picker then showed the first agent of the list, and the store chose the default
  agent only when the first message went out, so the message reached another agent than the picker showed.
  `startNewConversation` now chooses among the agents of a new chat right away, unless they are still loading.

## @newheap/platform-ai-chat: decision times and the Kinds of action section (FB82)

- The administration (`nh-assistant-admin`) has a Kinds of action section (`NhAssistantAdminTab` `proposal-kinds`,
  `NhAssistantAdminProposalKindsComponent`) between Agents and Tools & connections: per kind of action over all agents
  the name the cards show, the decisions, the rejection rate and the decision times, and per agent whether the
  numbers make the kind a candidate for running without approval, with the thresholds explained and a note that the
  candidates are suggestions for administrators only. Agent rows are `NhAssistantRoutineCandidateRowComponent`;
  `candidateActions` adds a column with the host's own action per candidate.
- `NhAssistantAdminApiService.getProposalKinds` with `ProposalKinds`, `RoutineCandidate` and the related models;
  `AgentFeedbackSummaryRow.decisionTime`, which the feedback report shows as two columns.
- The mock API of `@newheap/platform-ai-chat/testing` answers `GET admin/proposal-kinds` with an example: a status
  change that is a candidate and a task that misses three thresholds.

| Breaking change | Required action |
|---|---|
| `NhAssistantAdminTab` has the value `proposal-kinds`. | A host that maps its URL to the section or switches over the tab accepts `proposal-kinds`, as `sampleAssistantAdminLocation` does with `?section=proposal-kinds`. |

## @newheap/platform-ai-chat: running a kind of action without approval (FB82)

- An agent's Rollout lists the kinds of action it may run without approval: the tools whose code allows it, each with
  its switch ("Try in shadow", then "Run without approval", and "Switch off"), who switched it last, the note and
  whether the tool changed since. The Kinds of action section shows the same switch in each agent's row through its
  `candidateActions`; going live from a kind that is no candidate names the thresholds it misses first.
  `NhAssistantWithoutApprovalActionComponent` is exported for hosts that render that section themselves.
- `NhAssistantAdminApiService.getAgentWithoutApproval`, `setAgentWithoutApproval` and
  `getAgentWithoutApprovalVersions` with `AgentWithoutApproval`, `WithoutApprovalSwitch`, `WithoutApprovalRequest`,
  `WithoutApprovalVersion` and `WithoutApprovalMode`; `ToolCatalogEntry.allowWithoutApproval`; English and Dutch
  texts for `assistant-without-approval-not-allowed` and `assistant-without-approval-shadow-first`.
- The mock API of `@newheap/platform-ai-chat/testing` keeps the switches with their versions and refuses as the server
  does. The notice of the Kinds of action section now says that a candidate changes nothing by itself.

## @newheap/platform-ai-chat: the inbox by kind of action

- `nh-assistant-agent-inbox` takes `toolId` (one or several kinds of action), `expiresBefore` and `sort`
  (`AgentInboxSort`); changing one reloads it from the first page. `AgentInboxFilter` has the same fields and
  `getInbox` sends one `toolId` parameter per kind.
- `NhAssistantAgentRunApiService.getInboxSummary(filter)` (`AgentInboxSummaryFilter`, `AgentInboxSummary`) counts
  what the inbox lists per agent, kind of action and subject type, for a host's own filter; label the counts
  "listed".
- The mock API of `@newheap/platform-ai-chat/testing` filters and sorts the inbox the same way and serves
  `GET approvals/inbox/summary`.

## @newheap/platform-ai-chat: deciding a whole filter of the inbox

- `nh-assistant-agent-inbox` offers "Decide all N" for its whole filter (`decideAll`, on by default). It shows the
  overview first: how many times which kind of action on how many objects, the proposals that are decided one by
  one, and what remains beyond one set. Then it decides that set and shows the tallies.
- `NhAssistantAgentRunApiService.getBulkDecisionOverview(filter)` and `decideBulk(token, decision, reason)`, with
  `AgentBulkDecisionOverview`, `AgentBulkDecisionResult` and `NH_ASSISTANT_MAX_BULK_DECISION`.
- `decideMany` also takes the shown proposals and then sends their proposal hashes; ids alone still work.
- The mock API of `@newheap/platform-ai-chat/testing` serves the overview and the decision of a set, checks the
  hashes of `approvals/decisions`, and takes `requireIndividualDecision` per mock proposal.

## @newheap/platform-ai-chat: compact proposals, a compact board and a selection that follows the list

- The selection of `nh-assistant-agent-proposals` outlived its page: the inbox keeps the same list when it
  pages, so "Approve selected" also sent proposals selected on an earlier page without showing them. The
  selection now follows the list: a refresh of the same page keeps it, another page, filter or a decision
  elsewhere drops what the list no longer shows, and "Approve selected" and "Reject selected" send only the
  shown proposals, each with the proposal hash the person saw (`decideMany`).
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
  tile's count opens the tile's own inbox, in compact rows and without "Decide all", and a comfortable tile
  stays as it was.
