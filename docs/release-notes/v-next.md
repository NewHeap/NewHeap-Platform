# v-next

## @newheap/platform-ai-chat: a new chat after a chat about a subject

- After a chat with an agent that only a chat about a subject offers, "+" (`NhAssistantStore.startNewConversation`)
  kept that agent chosen. The picker then showed the first agent of the list, and the store chose the default
  agent only when the first message went out, so the message reached another agent than the picker showed.
  `startNewConversation` now chooses among the agents of a new chat right away, unless they are still loading.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: decision times and the view per kind of action (FB82)

- `GET admin/agents/{id}/feedback/summary` rows carry `decisionTime` (`NhAssistantDecisionTimeDto`): the median and
  the 90th percentile of the time from a proposal to a person's approval or rejection, in whole seconds, from at most
  the newest 1,000 decisions per row (`sampled` says when there were more). Expired, withdrawn and invalidated
  proposals and decisions of agents or of the platform do not count. Computed with provider-neutral LINQ and a
  bounded load on SQL Server and PostgreSQL; no migration.
- `GET admin/proposal-kinds` (`NhAssistantProposalKindsDto`) is the administration's view per kind of action over all
  agents: per tool the counts, approval and rejection rates, decision times, the newest rejection and the name the
  proposal cards show, and per agent a `NhAssistantRoutineCandidateDto` (`agentId`, `toolId`, the numbers,
  `candidate`, `unmetThresholds`) by the thresholds of `NhAssistantRoutineCandidateOptions`
  (`NewHeap:AI:Assistant:RoutineCandidates`, `NhAssistantBuilder.ConfigureRoutineCandidates`): at least 50
  decisions, at least 98% approved, no rejection in the last 7 days of the period and none stale or failed after
  approval by default. Filters `agentId` and `toolId`; behind the admin policy. A candidate is a suggestion only:
  nothing about deciding changes.

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
