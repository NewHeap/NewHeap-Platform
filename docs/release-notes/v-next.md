# v-next

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet: proposals whose object no longer exists

| Breaking change | Required action |
|---|---|
| A proposal of an agent run or consultation whose subject the access resolver describes as unknown (`DescribeAsync` returns `null`) now closes as stale (`not-applicable`, `assistant-proposal-subject-gone`): on any decision, which answers `assistant-proposal-stale` instead of `assistant-approval-policy-denied`, on `RevalidateSubjectAsync` and `RevalidateKeysAsync` whatever the guards say, and in the run maintenance once per `NhAssistantLimits.GoneSubjectCheckInterval` (an hour). | Let `INhAiSubjectAccessResolver.DescribeAsync` return `null` only for a subject that does not exist, independent of the caller, and throw when it cannot tell; drop application jobs that closed such proposals themselves. |
| `RevalidateSubjectAsync` now reads the subject's open proposals and describes the subject also when no guard checks at revalidation. | None; expect one resolver call per revalidated subject with open proposals. |
