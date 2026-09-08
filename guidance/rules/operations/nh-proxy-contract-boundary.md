---
id: nh-proxy-contract-boundary
title: "Manage SQLite literal redirects through embedded MVC administration"
area: configuration
reference: runtime-configuration
summary: "The two-call proxy loads literal redirects before requests and provides secured MVC management, local draft testing, login IP auditing and revision-checked save/activation. Managed rewrites remain a gap."
sample-cases: ["SPM-238"]
public-symbols: ["NhProxyRedirectRule", "NhProxyConfigurationValidator", "NhProxyRuntime", "NhProxySqliteConfigurationStore", "NhProxySqliteLoginAuditStore", "INhProxyConfigurationService", "INhProxyAdministrationService", "ConfigureYarp", "INhProxyConfigurationStore", "INhProxyRuntime", "AddNewHeapProxy", "UseNewHeapProxy", "MapNewHeapProxy"]
skills: ["newheap-runtime-configuration"]
providers: ["provider-neutral", "sqlite"]
risk: high
---
## Preferred approach

Use the two-call AddNewHeapProxy / UseNewHeapProxy WebApplication flow. Add registers
MVC, dedicated cookie authentication, configuration/audit storage, validation,
runtime, administration and a hosted lifecycle initializer. StartingAsync loads
and publishes the persisted snapshot before the HTTP server accepts requests.
Use reserves /newheap-proxy in its own MVC branch, installs redirects and maps YARP.
Do not also call MapNewHeapProxy; that remains a lower-level YARP-only alternative.
Put trusted forwarded headers and UsePathBase before UseNewHeapProxy.

Configure one host-owned Administrator.UserName and ASP.NET Identity PasswordHash
from a secret provider. Missing credentials leave administration unavailable with
503 while normal requests continue. Production requires HTTPS; HTTP administration
is accepted only in Development. The session cookie is HttpOnly, SameSite=Strict
and scoped to the administration PathBase. CredentialVersion or credential changes
invalidate old cookies after startup options reload. Host Data Protection settings
control session continuity across ordinary restarts. Preserve host authentication
defaults and do not make the NewHeapProxy scheme the host's default. Custom host
scheme providers remain host-owned and must preserve the same isolation.

Every administration request checks the optional literal IP/CIDR allowlist.
Enabled with no entries denies all. Trust only RemoteIpAddress after explicitly
configured host forwarding middleware. Do not trust arbitrary X-Forwarded-For.
The raw connection peer is unavailable once host middleware has replaced it.
All POST forms use antiforgery validation; Razor escapes rule text. Login attempts
are audited before issuing a cookie, and audit failure cannot grant access.
Attempted usernames and passwords are never audited. One process-wide fixed-window
budget defaults to five attempts per minute for the single account. Bounded audit
retention cleanup runs on credential attempts; idle hosts have no cleanup worker.

The MVC panel supports search, creation, editing, enable/disable, priority,
host/method restrictions, status/query options, deletion confirmation and login
activity. Test draft evaluates that one unsaved literal rule with the runtime
matcher. It neither saves nor makes network requests and does not simulate the
entire ordered configuration. Managed rewrite editing and full draft APIs remain
SPM-239 gaps.

Use INhProxyConfigurationService.SaveRedirectsAsync for live changes: read the
current configuration, submit its expected revision and complete rule collection,
then inspect TaskResult. A successful commit is followed by activation even after
request cancellation. If activation fails, the failed result retains SavedRevision
in Data. The UI shows saved/active revisions and offers RetryActivationAsync.
A stale editor cannot overwrite a newer configuration. The lower-level store
persists only; direct runtime publication validates and replaces the immutable
snapshot only. It rejects equal or older revisions. Offline setup remains an
alternative: initialize a store, save with its revision and dispose before startup.
Requests never query SQLite.

Use exact literal paths. HttpRequest.Path matching is ordinal and case-sensitive,
excludes queries and distinguishes trailing slashes. Regex metacharacters remain
literal; Prefix and RouteTemplate are rejected. Optional hosts compare without
case; a configured port must match, while no port accepts any port. Methods are
case-sensitive tokens. Disabled rules are ignored; lower priority wins, then Guid
order. The administration path and descendants never redirect.

302 is the default; 301, 303, 307 and 308 are supported. Targets are literal
root-relative paths or HTTP(S) URLs. Reject controls, backslashes, network-path
URLs, userinfo and unsupported schemes. Root-relative same-path targets are
rejected even with changed queries. General cycle and absolute self-redirect
analysis remain future work. Preserve merges query values with target keys winning
case-insensitively; Replace keeps target values only; Discard removes all queries.
Repeated values and fragments survive. Query escaping is normalized by ASP.NET.
No client Host value is reflected into Location.

Keep the SQLite file on durable local storage outside the webroot. Its default is
App_Data/newheap-proxy.db under the host content root. One lock-file handle enforces
exclusive NewHeap ownership. Schema version 2 transactionally upgrades version 1
with login auditing while retaining redirect documents. Unknown/corrupt state
fails rather than resetting. WAL, parameterized conditional writes, filtered audit
queries and bounded retention SQL stay in the SQLite project. SQL Server and
PostgreSQL are explicit v1 capability gaps.

Use options.ConfigureYarp(yarp => { ... }) for host-owned YARP customization. It
runs once during registration, is not JSON configuration and is not persisted.
Both options objects are startup snapshots. SPM-238 includes the standalone
SampleProjectManagement.Proxy application and consumer behavior tests. SPM-239
tracks remaining managed rewrites, full-pipeline drafts and cycle analysis.

## Avoid

- Querying SQLite per request, mutating live snapshots or bypassing revision checks.
- Treating a low-level storage save as automatic runtime publication.
- Calling MapNewHeapProxy again after UseNewHeapProxy.
- Storing plaintext credentials or enabling a default administrator password.
- Trusting forwarding headers from arbitrary clients or exposing administration over production HTTP.
- Issuing a login cookie when the audit write failed.
- Discarding failed TaskResult values, including failed activation after commit.
- Claiming complete managed rewrites, full-pipeline draft testing or SQL Server/PostgreSQL support.
- Editing consumer DAL migrations or copying only the database while WAL writes are active.

## Verification

Run both proxy library test projects plus ProxyContractBoundarySamplesTests,
ProxyLiteralRedirectSamplesTests and ProxyAdministrationSamplesTests. Real SQLite
covers startup, compare-and-swap, reopen, ownership, corruption, schema upgrade,
audit idempotency/filtering/paging and retention. HTTP tests cover CSRF, login/IP
checks, throttling, audit failure, scoped cookies, credential rotation, host auth,
PathBase, local draft isolation, save/activation, stale writes and deletion.
Run the standalone sample and inspect login, list, editor and audit pages on desktop
and mobile. Keep SPM-239's remaining capabilities explicitly unimplemented.
