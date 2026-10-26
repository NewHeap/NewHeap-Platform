# v-next

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
