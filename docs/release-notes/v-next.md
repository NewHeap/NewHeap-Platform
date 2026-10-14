# v-next

## NewHeap.Platform.AI.Chat: agent proposals that people overtook close on their own

An agent's proposal can wait for days while a person does the work by hand. Applications now
register an `INhAiProposalGuard` per tool with `AddProposalGuard<TGuard>(toolSelectors)`. A guard
names the application objects a proposal changes (`NhAiProposalKey`), which the platform indexes
with the thread's scope key, and checks whether the proposal still fits. It answers with the new
`NhAiPreconditionResult.AlreadyDone`, `NotApplicable` or `Stale` (changed), each with a reason code
and an explanation that the platform renders in every supported UI culture. Guards run when the
application revalidates, before every approval and right before execution; a registration with
`NhAiProposalGuardStages` can leave out revalidation for an expensive check.

The application revalidates after its change committed, explicitly with
`INhAssistantProposalRevalidation.RevalidateSubjectAsync` or `RevalidateKeysAsync`, or
declaratively with `AddProposalRevalidation<TEvent>` on the events it already publishes through
`INhAssistantAgentTriggers.PublishAsync`. When an agent has an open proposal about the event's
subject or objects, it queues the revalidation as a background operation that runs once the
publisher's transaction committed; other events cost one indexed query. A proposal that no longer
fits closes as
`stale` with `StaleKind` and `StaleExplanation` on `NhAssistantProposalDto` and on the approval part
of a conversation, its tool call reads `not-executed`, `AgentWorkChanged` reaches the subject's
members, `AgentProposalInvalidated` is audited without content, and an agent run that waits for it
continues with "not executed". Approving such a proposal is refused with
`409 assistant-proposal-stale`; a guard that fails refuses it with
`503 assistant-proposal-check-failed` and keeps it open. The feedback report counts proposals that
closed before a decision as `Invalidated` with `Invalidations` per kind and reason, never as
rejected, and the next run's continuity lists them in `<invalidated-proposals>`.

| Adoption note | Required action |
|---|---|
| New `nhai` migration `AgentProposalRevalidation` on SQL Server and PostgreSQL: the table `AssistantApprovalKey` and the columns `StaleKind` and `StaleExplanationsJson` of `AssistantApproval`. | Apply it, as the assistant does at startup unless `RunMigrations` is off. |
| Without guards nothing changes: proposals close as stale only through `RequireUnchangedSubject`, as before. | Register guards and revalidation for the tools whose work people also do by hand. |
| `RequireUnchangedSubject` closing a proposal before execution is now audited as `AgentProposalInvalidated` (stage `execution`, kind `changed`) instead of `AgentProposalExecuted` with status `stale`. | Update audit queries that counted stale executions. |
| `NhAssistantFeedbackSummaryRowDto.Stale` and `Approved` count only proposals that closed after their approval; proposals closed before a decision are `Invalidated`. | None; reports that summed the statuses themselves add `Invalidated`. |
| `NhAiProposalGuardContext.Subject` and `ScopeKey` are `null` for a proposal in a person's chat outside a subject thread. | Handle `null` in guards that apply to chat tools too. |

## NewHeap.Platform.AspNet.Common: platform-internal signals for a module's own waits

A platform module can now end a background-operation wait it started itself on behalf of the
operation's owner, without a person and without access policies. The assistant uses it to wake an
agent run whose inline proposal the platform closed. The signal is internal to the platform's
packages and is recorded as a `SignalReceived` event like any other.

| Adoption note | Required action |
|---|---|
| No public API changes. | None. |

## @newheap/platform-ai-chat: why a proposal no longer applies

A stale proposal shows its kind, "Already done", "Changed" or "No longer needed" (Dutch: "Al
gedaan", "Gewijzigd", "Niet meer nodig"), and the application's explanation in the approval card,
the proposals list, the thread view, the agent inbox and the subject suggestions. When a proposal
closes as stale while it is on screen in the inbox or the subject suggestions, or when an approval
is refused with `assistant-proposal-stale`, the list keeps it as a closed card with the reason until
it is dismissed (`holdClosedCards` on `NhAssistantAgentProposalsComponent`). The feedback report
shows the proposals that closed before a decision in the column "No longer needed".

| Adoption note | Required action |
|---|---|
| `AgentProposal.staleKind` and `staleExplanation`, `ApprovalPart.staleKind` and `staleExplanation`, and `AgentFeedbackSummaryRow.invalidated` and `invalidations` are optional. | None. |
| New translation keys `nh-assistant.approval.stale-kind.*`, `nh-assistant.agent-runs.proposal.closed`, `dismiss`, `dismiss-label`, `nh-assistant.admin.feedback.columns.invalidated`, `invalidated-hint` and `nh-assistant.errors.assistant-proposal-check-failed`. | Applications with their own translation files add them. |
