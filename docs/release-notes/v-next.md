# v-next

## NewHeap.Platform.AI.Chat: presentation limits that a presenter's unit test checks

`NhAssistantPresentationLimits.FindBrokenRule(presentation)` returns the first limit an
`NhAssistantApprovalPresentation` breaks, as an `NhAssistantPresentationRules` name, or `null`. It is the
check the assistant drops a presenter's result by, so a presenter's unit test can present with the longest
texts the application stores, in every supported culture, and fail before production logs the warning and
shows approvers the generic summary (SPM-347). Nothing else changes.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the culture a person decided in

`NhAssistantProposalHandOff.DecisionCulture` and `NhAssistantExecutionReportReceipt.DecisionCulture` name
the UI culture in which the person approved a proposal, so the application that executes it can address
them in their language. Deferred decisions now record it as inline approvals did: the decision endpoints
record the request culture, and `INhAssistantProposalDecisionService.DecideAsync` records the new optional
`NhAssistantDecidingPerson.Culture`, the language of the message the channel asked the person in. Only a
supported UI culture of `RequestLocalizationOptions.SupportedUICultures` is recorded, by its exact name or
as the supported culture of its language (`nl-BE` as `nl-NL`); a culture of another language records none
and the decision stands. An inline approval records the turn's culture the same way, now also in an agent
thread. The assistant's own posts into an agent thread keep the default culture, and a chat still reads
the close of its hand-off in the recorded culture. The `ApprovalCulture` migrations hold the column; no
migration is added (SPM-348).

| Breaking change | Required action |
|---|---|
| A record comparison with `new NhAssistantExecutionReportReceipt(...)` or `new NhAssistantProposalHandOff(...)` now also compares `DecisionCulture`, which decisions through the endpoints, inline approvals and a deciding person's `Culture` fill. | Set `DecisionCulture` in the expected record, or compare the members you mean, such as `ApprovalId`, `Status` and `Duplicate`. |

## NewHeap.Platform.AI.Common and NewHeap.Platform.AI.Chat: a proposal guard that knows the call

`NhAiProposalGuardContext` has init-only `Invocation`, `ConversationId`, `TurnId` and `Channel`. The
assistant sets them when a call proposes, for the `NhAiProposalGuardStages.Proposal` check and the keys of
the new proposal, so a guard can refuse before an approval card is shown, for example a change that a
channel's chat may not propose (SPM-349). They are `null` at revalidation, decision and execution, and
for a proposal an asker leaves for the subject's approvers; they take no part in the proposal hash or the
approval evidence.

| Breaking change | Required action |
|---|---|
| `NhAiProposalGuardContext` record equality now also compares `Invocation`, `ConversationId`, `TurnId` and `Channel`, which the proposal stage fills. | Compare the members you need, such as `ProposalId`, instead of whole contexts in a guard's cache key or a test; decide on the new members only in a guard registered for `NhAiProposalGuardStages.Proposal`, never at the later stages where they are `null`. |
