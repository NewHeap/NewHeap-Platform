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
