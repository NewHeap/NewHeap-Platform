# v-next

## NewHeap.Platform.AspNet.Proxy: host-relative redirect destinations stay on this origin

A redirect destination that starts with `/` is now validated as the path the `Location` header
carries, after dot segments (`..`, `.`, `%2e%2e`, `%2e`) are removed, and must then still start with
exactly one `/`. This applies when a rule is saved or activated and to every regex expansion. An
expansion whose path resolves to a network path (`//host`) skips redirect evaluation and the request
passes through, as other unsafe expansions do.

| Breaking change | Required action |
|---|---|
| An exact target or regex template whose path resolves to `//host` once dot segments are removed now fails validation; a snapshot with one no longer saves or activates. | Use the host's absolute URL, or a path that resolves to one leading `/`. |

## NewHeap.Platform.AspNet.Proxy: denied addresses no longer spend the administrator's login budget

`NhProxyAdministrationService.SignInAsync` now refuses a caller outside the IP allowlist before it takes a
permit from the shared login budget (`Administrator:LoginAttemptLimit` per `LoginAttemptWindow`), so only
attempts from allowlisted addresses spend that budget. Denied attempts are still audited as `IpDenied`, now
within a separate budget of `LoginAttemptLimit` rows per `LoginAttemptWindow`; further denied attempts in
that window are refused with 403 without an audit row, so the login audit stays bounded.

| Breaking change | Required action |
|---|---|
| Denied attempts beyond `LoginAttemptLimit` per `LoginAttemptWindow` no longer appear in the login audit. | None, unless monitoring counts `IpDenied` rows to measure outside traffic; count refused requests in the host's request logs instead. |
