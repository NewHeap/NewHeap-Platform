# v-next

## NewHeap.Platform.AI.Chat.AspNet: request delegates generated at build time

The library builds with ASP.NET Core's Request Delegate Generator (`EnableRequestDelegateGenerator`), so
the request delegates of the assistant's 139 minimal API endpoints are written when the package is built.
A host that calls `MapNewHeapAssistant` no longer compiles them with `RequestDelegateFactory` while it
starts. Applications change nothing.

Measured with `AssistantRequestDelegateTests.Measure_mapping_and_building_the_assistant_endpoints`
(`NH_MEASURE_ENDPOINTS=1`, Release, Windows 11, Intel Core Ultra 9 275HX, .NET 10.0.12): the time of
`MapNewHeapAssistant` plus the first read of every endpoint, for 20 hosts in one process after three
warm-up hosts, and for the first host of the process. The ranges cover four runs before and five after;
the first host was recorded in two and four of them.

| Per host | Before | After |
|---|---:|---:|
| Wall-clock time, median | 225–231 ms | 6.5–7.0 ms |
| Process CPU time, mean | 248–270 ms | 6–11 ms |
| First host of the process, wall-clock time | 270–271 ms | 91–95 ms |

Routes, endpoint names, summaries, `Produces` metadata, tags, filters, authorization, the binding of
route and query values (a malformed value still answers 400) and the 404 of a disabled assistant are
unchanged; the sample's OpenAPI document is the same. The endpoints' metadata now also holds the
generator's `GeneratedCodeAttribute`, and the generator's `IParameterBindingMetadata` reports
`HasTryParse` false for `string` parameters, which need no parsing.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AspNet.Common: background-operation errors during a cancellation and the hub path

The runner's failure path now checks the platform's stop signals before it records an error that a
handler threw outside a step, as the recording of a failed step already does:

- While the attempt's cancellation is requested, the error no longer fails the operation. It ends
  `Cancelled` (`cancelled`, `background-operation.cancelled`), or `TimedOut` (`soft-timeout`) when
  the soft time-out fired first, and nothing retries the error, which is logged as a warning with the
  operation and attempt. This covers a client library that reports a cancelled call as an error of
  its own as well as an unrelated error after the owner cancelled.
- A lost fence and contention for the operation lock that the heartbeat recorded still win: the
  stopped attempt records nothing, or the operation is rescheduled as busy without spending a retry.
- Without such a signal the error fails the attempt as `handler-failed` with the operation's retry
  rules, as before.

`NhBackgroundOperationsOptions.HubPath` no longer has to lie below `/hub`. Since the query token
follows the configured hub paths, any absolute application path below the root works, such as
`/hubs/background-operations`, except a path on the operations' own API under
`/background-operations`. The default stays `/hub/background-operations`.

| Breaking change | Required action |
|---|---|
| An error that a handler throws outside a step while its cancellation is requested ends the operation `Cancelled`, or `TimedOut` after a fired soft time-out, without a retry, instead of `handler-failed` with a scheduled retry or a terminal failure; it is logged as a warning without a diagnostic correlation id. | None, unless monitoring, a notification policy or a test expected `handler-failed` for such an error; expect `Cancelled`. |
| An error that a handler throws outside a step after the heartbeat stopped the attempt for lock contention reschedules the operation as `concurrency-busy` without spending a retry, instead of recording `handler-failed`. | None. |
| None for `HubPath`: a path outside `/hub` is now accepted, while a relative, empty or root path and a path under `/background-operations` are still refused. | None. A host that moves the hub points the Angular client's `hubUrlSuffix` at the same path. |

## NewHeap.Platform.AI.Chat.AspNet: a run's facts by its id

`INhAssistantAgentRunLookup.FindRunAsync(runId, tenantId)` reads one agent run for the application, such
as the run a receipt or `FindByEventAsync` named: `NhAssistantAgentRunFacts` gives its agent, subject and
thread, whether it is shadow work, its trigger with the trigger's kind, version and candidate flag, how it
worked, its status, failure class and code, attempt, proposal count, summary, its result with
`ResultTruncated` (in the caller's UI culture when the run's rules wrote it per culture) and when it was
created, started and ended. It returns `null` for an unknown run. Like the event lookup it checks no
person's access, and with a tenant it finds only that tenant's work and the whole application's work for
a tenant that takes part (`UseSingleTenant`). The result is model output: render it only with a safe
Markdown renderer, never as HTML. The event lookup is unchanged.

| Breaking change | Required action |
|---|---|
| `INhAssistantAgentRunLookup` gains `FindRunAsync`. | A class of the application that implements the interface, such as a test fake, implements `FindRunAsync`; callers change nothing. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: shadow proposals that replace each other

A shadow proposal now stores the objects its guards name (`INhAiProposalGuard.GetKeysAsync`), as a live
proposal does; no migration is needed. After the identity rule, the approval policy's
`NhAiDuplicateProposals` applies among the agent's shadow proposals in its shadow threads of the scope
that are still within their proposal lifetime: `Skip` reports a newer shadow proposal for the same objects
as `AlreadyOpen` and records nothing, `Supersede` records it and closes the older one with the new status
`shadow-superseded` (`NhAssistantApprovalStatuses.ShadowSuperseded`, with `supersededByApprovalId` and
the `AgentProposalSuperseded` audit event), and `Allow` keeps both. The rejection cooldown does not apply
to shadow proposals. Revalidation, inboxes, `INhAssistantProposalCounts`, the feedback report and
notifications keep leaving shadow proposals out, `shadow-superseded` ones included
(`NhAssistantApprovalStatuses.IsShadow`). The evaluation of a shadow variant counts every proposal once,
in the run that made it, so a replaced shadow proposal pairs with the live proposal that a newer one
replaced too.

| Breaking change | Required action |
|---|---|
| The guards of a tool now name the objects of its shadow proposals too: `GetKeysAsync` runs when a shadow run proposes. | None, unless a guard's `GetKeysAsync` must not run for shadow work; it gets the shadow thread's subject and scope. |
| A newer shadow proposal for the same objects within the older one's lifetime replaces it (`Supersede`, the default) or is not recorded (`Skip`), instead of being recorded next to it. | None, unless a report counted every shadow proposal; count the `AgentProposalSuperseded` and `AgentProposalDeduplicated` events of shadow threads for them. |
| Proposals can have the status `shadow-superseded`, which `NhAssistantApprovalStatuses.IsClosed` treats as closed. | A client or report that lists the approval statuses accepts `shadow-superseded`, or uses `IsShadow` for both shadow statuses. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: a chat report in the conversation's language

When a person approves a tool declared with `AlsoInline` in their own chat, the approval records the UI
culture of that turn (`AssistantApproval.DecisionCulture`). The application message that a report, an
uncertain hand-off and a passed report deadline post into the chat, its title included, is written in that
culture when the application still supports it as a UI culture (`RequestLocalizationOptions.SupportedUICultures`),
otherwise in the default request culture, as for an approval handed off before the culture was recorded. A
resolution that hands the proposal to the application again keeps the recorded culture. An agent thread
keeps the default request culture for what reports and deadlines post (DN8). The migration
`ApprovalCulture` adds the nullable column `DecisionCulture` (at most 35 characters) to
`AssistantApproval` on SQL Server and PostgreSQL.

| Breaking change | Required action |
|---|---|
| The assistant schema gains the migration `ApprovalCulture` (one nullable column). | Apply it with `RunMigrations` or in the deployment step that applies the assistant's migrations. |
| The posts of a chat's inline hand-off use the culture of the turn that approved it instead of the application's default request culture. | None, unless a test or integration expected the default culture's text in the chat; expect the approving person's language. |

## @newheap/platform-ai-chat: shadow proposals that a newer one replaced

`AgentProposalStatus` and `ApprovalStatus` gain `shadow-superseded`, the status of a shadow proposal that
a newer one for the same objects replaced; the proposal names the newer one in `supersededByApprovalId`,
and the agent-run proposals view shows it as "Shadow, replaced" ("Schaduw, vervangen") with the way to its
successor. The mock API's `NhAssistantMockProposal` takes `supersededByApprovalId` to seed such a
proposal.

| Breaking change | Required action |
|---|---|
| `AgentProposalStatus` and `ApprovalStatus` gain `shadow-superseded`. | A `switch` or `Record` over the statuses that must be exhaustive handles `shadow-superseded`, for example like `shadow`. |
