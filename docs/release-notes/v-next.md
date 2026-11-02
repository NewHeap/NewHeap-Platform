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
