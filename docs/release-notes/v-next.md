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

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Generators, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: deciding a whole filter of the inbox

- `POST approvals/bulk-decisions/overview` puts the pending proposals the caller's inbox lists with a filter in a
  set of at most 200 (`NhAssistantLimits.MaxBulkDecisionProposals`), counted per kind of action and subject type,
  with a token valid for 15 minutes for the caller and tenant only. `POST approvals/bulk-decisions` decides exactly
  that set: the inbox's visibility runs again, and each proposal keeps its own rules and the version the overview
  counted. The result counts succeeded, refused and stale; a repeat of the token reports and decides nothing.
- `NhAiToolAttribute.RequireIndividualDecision` keeps a tool's proposals out of every bulk decision; deciding one
  together with others answers `assistant-individual-decision-required`. It is not part of the contract hash.
- `POST approvals/decisions` takes `expectedProposalHashes` per id and refuses a changed proposal with
  `assistant-proposal-hash-mismatch`.
- `NhAssistantAuditEvent.BatchId` names the bulk decision of an `ApprovalApproved` or `ApprovalRejected` event;
  `Channel` stays `web`.

| Breaking change | Required action |
|---|---|
| The `BulkDecisionSets` migrations add the table `AssistantDecisionBatch` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `NhAssistantErrorCodes` has the new codes `assistant-bulk-decision-not-found` (HTTP 404), `assistant-bulk-decision-expired`, `assistant-bulk-decision-in-progress` and `assistant-individual-decision-required` (HTTP 409). | Let code that maps assistant codes handle values it does not know. |
| `NhAssistantAuditEvent` has the new property `BatchId`, set on the decisions of a bulk decision. | Let audit sinks that store every property accept it. |

## @newheap/platform-ai-chat: deciding a whole filter of the inbox

- `nh-assistant-agent-inbox` offers "Decide all N" for its whole filter (`decideAll`, on by default). It shows the
  overview first: how many times which kind of action on how many objects, the proposals that are decided one by
  one, and what remains beyond one set. Then it decides that set and shows the tallies.
- `NhAssistantAgentRunApiService.getBulkDecisionOverview(filter)` and `decideBulk(token, decision, reason)`, with
  `AgentBulkDecisionOverview`, `AgentBulkDecisionResult` and `NH_ASSISTANT_MAX_BULK_DECISION`.
- `decideMany` also takes the shown proposals and then sends their proposal hashes; ids alone still work.
- The mock API of `@newheap/platform-ai-chat/testing` serves the overview and the decision of a set, checks the
  hashes of `approvals/decisions`, and takes `requireIndividualDecision` per mock proposal.
