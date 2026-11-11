# v-next

## NewHeap.Platform.Common, NewHeap.Platform.AspNet.Common: .NET 10 and .NET 11

| Breaking change | Required action |
|---|---|
| Runtime packages now ship separate .NET 10 and .NET 11 assets with framework-aligned Microsoft dependencies in a new major release. | Restore and rebuild consumers for `net10.0` or `net11.0`; keep EF Core and its provider-dependent packages on 10.x. On .NET 11, align OpenAPI customizations with Microsoft.OpenApi 3.x. |

## NewHeap.Platform.AspNet.Caching: .NET 10 and .NET 11

| Breaking change | Required action |
|---|---|
| The caching package now ships separate .NET 10 and .NET 11 assets in a new major release. | Restore and rebuild consumers for `net10.0` or `net11.0` with the corresponding Microsoft dependency versions. |

## NewHeap.Platform.AspNet.Proxy, NewHeap.Platform.AspNet.Proxy.Sqlite: .NET 10 and .NET 11

| Breaking change | Required action |
|---|---|
| Proxy packages now ship separate .NET 10 and .NET 11 assets with framework-aligned Microsoft dependencies in a new major release. | Upgrade both proxy packages together and restore and rebuild consumers for `net10.0` or `net11.0`. |

## NewHeap.Platform.Media: .NET 10 and .NET 11

| Breaking change | Required action |
|---|---|
| Media packages now ship separate .NET 10 and .NET 11 assets in a new major release. | Upgrade the media packages together and restore and rebuild consumers for `net10.0` or `net11.0`; keep EF Core and its providers on 10.x. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet: proposals whose object no longer exists

| Breaking change | Required action |
|---|---|
| A proposal of an agent run or consultation whose subject the access resolver describes as unknown (`DescribeAsync` returns `null`) now closes as stale (`not-applicable`, `assistant-proposal-subject-gone`): on any decision, which answers `assistant-proposal-stale` instead of `assistant-approval-policy-denied`, on `RevalidateSubjectAsync` and `RevalidateKeysAsync` whatever the guards say, and in the run maintenance once per `NhAssistantLimits.GoneSubjectCheckInterval` (an hour). | Let `INhAiSubjectAccessResolver.DescribeAsync` return `null` only for a subject that does not exist, independent of the caller, and throw when it cannot tell; drop application jobs that closed such proposals themselves. |
| `RevalidateSubjectAsync` now reads the subject's open proposals and describes the subject also when no guard checks at revalidation. | None; expect one resolver call per revalidated subject with open proposals. |
