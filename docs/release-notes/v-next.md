# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: approver candidates the application names

An application that knows who passes a policy rule (`NhAiApprovers.Policy`) names those people with an
`INhAssistantApproverCandidates`, registered with `UseApproverCandidates<TCandidates>()`, so
`INhAssistantApproverDirectory` can answer `Complete: true` where it could not before: a policy rule admits
anyone who passes it, and the whole application lists no members. The directory asks it only when people
outside the members may approve and the members did not fill the limit, with an
`NhAssistantApproverCandidateQuery` (the policy, the subject, the subject's tenant and division, the
`PolicyNames` of the policy's rules, `SubjectRoleHolders` for a subject without listed members, and
`MaxCandidates`). It checks each `NhAssistantApproverCandidate` exactly as it checks a member, the rebuilt
principal, roles, the tool's approve policies and four eyes included, lists those who pass after the members,
and answers complete only when the application declared its `NhAssistantApproverCandidateList` complete and
neither the limit nor `MaxCandidates` applied. A candidates service that throws leaves the list incomplete.
Without a registration nothing changes (SPM-378).

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the recorded policy for guards and the executor

`NhAiProposalGuardContext` and `NhAssistantProposalHandOff` have init-only `PolicyId` and `Mode`
(`NhAiApprovalMode`): the approval policy a proposal waits under and the mode it got, as the proposal
recorded them, also a policy an approval policy selector chose. Guards read them at revalidation, decision
and execution; they are `null` when a call proposes and for the keys of a new proposal, because the policy
is chosen after the guards ran. A proposal in a person's own chat has no policy (`PolicyId` `null`, `Mode`
`Inline`). An application no longer reads them from the assistant's tables to check whether what decided a
selector's choice still holds (SPM-379). No migration: the approval already stores both.

| Breaking change | Required action |
|---|---|
| `NhAiProposalGuardContext` and `NhAssistantProposalHandOff` record equality now also compares `PolicyId` and `Mode`, which revalidation, decision, execution and every hand-off fill. | Compare the members you mean, such as `ProposalId` or `ApprovalId`, instead of whole records in a cache key or a test, or set `PolicyId` and `Mode` in the expected record. |
