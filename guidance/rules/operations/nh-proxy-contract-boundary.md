---
id: nh-proxy-contract-boundary
title: "Manage SQLite rewrites and redirects through embedded MVC administration"
area: configuration
reference: runtime-configuration
summary: "The two-call proxy loads exact and opt-in regex redirects before requests and provides secured MVC management, local draft testing, login IP auditing and revision-checked save/activation. Regex targets use captures without automatic query merging. Stored rewrites use native YARP matching/transforms and an isolated managed-rule test tool."
sample-cases: ["SPM-238", "SPM-239"]
public-symbols: ["NhProxyRewriteRule", "NhProxyDraftTester", "INhProxyDraftTester", "SaveRewritesAsync", "NhProxyRedirectRule", "NhProxyConfigurationValidator", "NhProxyRuntime", "RedirectResolutionTimeoutMilliseconds", "NhProxySqliteConfigurationStore", "NhProxySqliteLoginAuditStore", "INhProxyConfigurationService", "INhProxyAdministrationService", "ConfigureYarp", "INhProxyConfigurationStore", "INhProxyRuntime", "AddNewHeapProxy", "UseNewHeapProxy", "MapNewHeapProxy"]
skills: ["newheap-runtime-configuration"]
providers: ["provider-neutral", "sqlite"]
risk: high
---
## Preferred approach

Install NewHeap.Platform.AspNet.Proxy.Sqlite, which also brings in the
neutral NewHeap.Platform.AspNet.Proxy package. Both packages belong to the
independent nuget-proxy release unit. Release packaging consumes the published
Common dependency; local development uses its project reference.
Use the two-call AddNewHeapProxy / UseNewHeapProxy WebApplication flow. Add registers
MVC, dedicated cookie authentication, configuration/audit storage, validation,
runtime, administration and a hosted lifecycle initializer. StartingAsync loads
and publishes the persisted snapshot before the HTTP server accepts requests.
Use reserves /newheap-proxy in its own MVC branch, installs redirects and maps YARP.
Do not also call MapNewHeapProxy; that remains a lower-level YARP-only alternative.
Put trusted forwarded headers before UseNewHeapProxy. Host the proxy at the
origin root with an empty PathBase; subdirectory hosting is not supported.
Do not recommend UsePathBase or mounting the proxy under a prefix such as
/proxy. The URL/draft tester does not model the mount prefix and can disagree
with runtime matching. Support is deferred until a concrete use case requires
it, outside the current production scope. Existing PathBase-specific cookie
and administration tests do not establish support for the complete proxy.

For live environments, recommend deployment-secret injection through environment
variables NewHeapProxy__Administrator__UserName and NewHeapProxy__Administrator__Password.
Bind builder.Configuration.GetSection("NewHeapProxy") through AddNewHeapProxy;
the default ASP.NET Core environment provider overrides appsettings values.
Alternatively inject NewHeapProxy__Administrator__PasswordHash with a precomputed
ASP.NET Identity hash and leave Password unset. Configuring both fails startup.
Password is hashed once at startup, cleared from proxy options and excluded from
JSON serialization; never persist it in SQLite, audit events or committed settings.
The original environment/configuration value remains host-owned. Preserve spaces;
reject whitespace-only passwords and passwords longer than 1024 characters.
Restart after rotation. Password generates a fresh salted hash each startup, so
sessions must sign in again; PasswordHash keeps existing session continuity.
Use user-secrets for local development. Missing credentials leave administration unavailable with
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
activity and an opt-in Use regular expression checkbox. Test draft evaluates one unsaved rule with the runtime
matcher. It neither saves nor makes network requests and does not simulate the
entire ordered configuration. The rewrite editor and INhProxyDraftTester evaluate isolated candidates against both stored managed engines (SPM-239).

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
literal; Prefix and RouteTemplate are rejected. Regex with capture groups covers
these requirements; separate match modes are not planned. Optional hosts compare without
case; a configured port must match, while no port accepts any port. Methods are
case-sensitive tokens. Disabled rules are ignored; lower priority wins, then Guid
order. The administration path and descendants never redirect.

302 is the default; 301, 303, 307 and 308 are supported. Targets are literal
root-relative paths or HTTP(S) URLs. Reject controls, backslashes, network-path
URLs, userinfo and unsupported schemes. Root-relative same-path targets are
rejected even with changed queries. The local chain-depth guard also refuses
overlong chains and absolute self-redirects before execution. Preserve merges query values with target keys winning
case-insensitively; Replace keeps target values only; Discard removes all queries.
Repeated values and fragments survive. Query escaping is normalized by ASP.NET.
No client Host value is reflected into Location.

Opt into NhProxyRedirectPathMatchMode.Regex through Match.PathMode or the checkbox.
The pattern receives the escaped HttpRequest.Path plus the query string, including
the question mark, excluding PathBase. Use anchors for full-input matching. The
first match expands the entire target using .NET substitutions ($1, ${name}, $$).
QueryMode is ignored for regex rules: include every query value explicitly in the
target or capture it from the input. No merge or query parsing occurs in this mode.
Absolute authorities stay fixed; captures belong in the path, query or fragment.
The runtime prepares regexes per immutable snapshot, validates expanded targets,
and rejects unsafe or root-relative self expansions. A shared wall-clock resolver
budget includes exact rules, host/method checks and target construction. Configure
NewHeapProxy:Limits:RedirectResolutionTimeoutMilliseconds as an integer number of milliseconds (default 50)
through the section-binding overload or options callback. The runtime captures
the value at startup and the draft tester uses the same option. Values must be
between 1 and 2,147,483,646 ms, matching the regex engine's supported range.
Expiry or a regex timeout returns HTTP 503 with Cache-Control: no-store and no
Location header; downstream endpoints do not execute. Each regex receives only
the remaining budget as its engine timeout, rounded down to whole milliseconds.
With less than 1 ms left, do not start another match. RegexMatchTimeoutException
interrupts the matching itself; do not use Task.Run/WaitAsync to abandon CPU work.
Cache at most 50 timeout variants per rule and snapshot regardless of the configured
budget; run additional variants without retaining them. Recheck the remaining
budget after preparing a variant. Check the deadline between resolver stages and
before returning. Other synchronous operations and runtime scheduling remain
cooperative rather than hard-preemptible.
Input/output limits are 65,536 characters; patterns are limited to 2,048 characters
and targets to 4,096. Over-limit or unsafe expansions still pass through unless
the resolver budget expired. The draft tester explains failures. Existing rules
stay Exact, and no schema migration is needed. Older runtimes cannot load new regex rules.

Keep the SQLite file on durable local storage outside the webroot. Its default is
App_Data/newheap-proxy.db under the host content root. One lock-file handle enforces
exclusive NewHeap ownership. Schema version 3 transactionally adds a separate rewrite document after the version-2 login-audit upgrade, retaining redirect documents and audit data. Back up before upgrading; old schema-2 runtimes cannot reopen the upgraded database. Unknown/corrupt state
fails rather than resetting. WAL, parameterized conditional writes, filtered audit
queries and bounded retention SQL stay in the SQLite project. SQL Server and
PostgreSQL are explicit v1 capability gaps.

Use options.ConfigureYarp(yarp => { ... }) for host-owned YARP customization. It
runs once during registration, is not JSON configuration and is not persisted.
Native YARP APIs/configuration satisfy the requirement for rewrites from appsettings
or other sources. This is a completed scope decision, not a missing NewHeap feature.
Managed editing/testing intentionally cover stored rules only; do not treat
external-source preview as planned work.
Both options objects are startup snapshots. SPM-238 includes the standalone
SampleProjectManagement.Proxy application and consumer behavior tests. Its explicit
Proxy demo launch profile uses the test account administrator / NewHeap123!, a
separate SQLite demo file and a first-run literal redirect. This sample-only mode
requires Development, binds localhost in its launch profile and allowlists only
loopback addresses for administration; Configured proxy keeps host-owned
settings and has no default account. The verify-proxy-demo.ps1 smoke check exercises
the real executable, health endpoints, login, redirect, persisted deletion and Production guard.
The sample AppHost selects the same demo profile as sample-project-management-proxy,
assigns its HTTP port and links directly to /newheap-proxy in the dashboard. Shared
service defaults expose Development health endpoints, and Aspire checks /alive.
The proxy has no database-container or API dependency; its administration endpoint
is excluded from service discovery references. SPM-239 demonstrates stored rewrite preview, real forwarding and local chain-depth refusal. Regex with capture groups satisfies prefix and route-template redirects; external cycle analysis is excluded.

Use SaveRewritesAsync with the complete stored rule/cluster snapshot and expected
rewrite revision. The store validates native YARP configuration before its
conditional commit; the runtime publishes a dedicated InMemoryConfigProvider.
Never mutate its collections. A unique publication token is confirmed through
IConfigChangeListener.ConfigurationApplied, rather than inferring activation from
Update or GetConfig. Rejected application retains the previous active revision;
five seconds without confirmation reports Unconfirmed. A late callback may still
confirm it. Separate engine gates keep redirect saves independent. Both engines
load before the server listens, and proxy requests never open SQLite.

The Rewrites panel supports basic matching, shared destinations, ordered path
transforms and advanced JSON for header/query conditions, additional transforms,
policies, timeouts and health checks. JSON is the agreed developer interface for
these settings; dedicated form controls are not required. Contextual links open
YARP matching/policy, transform, health-check and timeout documentation in a new
tab without leaving the draft. Keep the displayed NewHeap JSON structure: native
YARP examples use different property names and nesting. Load a shared destination explicitly before
editing it; changes affect every referencing rule. JSON shows the complete current
draft, including the first path transform. Form fields and valid JSON edits synchronize
in both directions; invalid JSON is preserved for correction. IDs remain editor-owned.
The path control replaces the first path transform without duplicating it. Without
JavaScript, visible form fields take precedence on submission. Unknown fields, protected/secret header
changes, unsafe destinations and exact duplicate enabled matches are rejected.
AllowedDestinationHosts restricts destination hosts when configured. Keep exactly
one HTTP(S) destination per cluster. Avoid forwarding back into the same rule;
external backend responses and cross-service cycles are outside the local chain analysis.

Use INhProxyDraftTester.TestRewriteAsync with both expected engine revisions,
the draft rule, synthetic URL/method/headers and optional replacement DraftClusters.
TestRedirectAsync uses the same managed-rule pipeline. Stale revisions fail.
TestSavedAsync accepts the expected revisions and synthetic input without a draft.
The administration top-bar URL tester uses this API for a GET preview of saved
rules, shows the selected rule and target with an edit link, and distinguishes
no-match and reserved paths. It warns when saved and active revisions differ.
An input such as /foo?s=1 uses the administration request's scheme, host and port;
an absolute HTTP(S) URL keeps its own origin. The result displays the resolved URL.
No administration path or PathBase is prepended. Configure trusted forwarding
middleware before UseNewHeapProxy when the public origin comes from an upstream proxy.
Disabled rules stay disabled unless SimulateEnabled is explicit. The administration
path is reserved and redirects precede rewrite matching, including ambiguous
rewrites. Native isolated routing and transforms produce route values, target URL,
safe headers and the winner. No server starts, health checks run or outbound
requests occur. Nothing is saved or published. Ambiguity returns a failed result.
Only supplied non-secret headers are accepted; administrator credentials/cookies
are never copied. The input limit defaults to 64 KiB and the cooperative test
budget to five seconds. Response header transforms remain unevaluated. Host
callbacks, custom policies/constraints/transforms, other configuration sources,
connectivity, health and upstream responses are explicitly outside the preview.
Host policy registration and optional middleware composition remain host-owned.

Configure NewHeapProxy:Limits:MaximumChainDepth as a positive integer, default 2,
captured at startup. Before executing a request, follow its local managed-rule
chain without outbound calls; each redirect or rewrite counts once. Exactly the
configured number is allowed; a further match returns HTTP 508, no-store, no
Location and no backend request. This also refuses legitimate overlong chains.
The tester uses the same check and returns NhProxyErrorCodes.MaximumChainDepth.
Carry both general and content headers from native transforms into the next
match and the preview's SafeRequestHeaders, preserving multiple values and
honoring header replacement/removal. Native YARP creates empty content for
content headers; simulation never reads the incoming body. Existing secret and
framing-header exclusions still apply.
Rules are still saved normally; refusal uses the concrete request at runtime.
Follow only the original scheme/host/port and current PathBase; reserved paths,
external destinations and native routes from other sources end analysis. Host
endpoint behavior, aliases and backend responses are outside the guarantee.
Simulate 303 as GET except for HEAD and 301/302 POST as GET; 307/308 keep the method.
Do not add hop cookies/headers or perform network probes. Use native route
selection and standard transforms without executing endpoint delegates.

## Avoid

- Querying SQLite per request, mutating live snapshots or bypassing revision checks.
- Treating a low-level storage save as automatic runtime publication.
- Calling MapNewHeapProxy again after UseNewHeapProxy.
- Persisting plaintext credentials in rule storage, logs or committed settings, or enabling a default administrator password.
- Trusting forwarding headers from arbitrary clients or exposing administration over production HTTP.
- Issuing a login cookie when the audit write failed.
- Discarding failed TaskResult values, including failed activation after commit.
- Treating preview as a connectivity/authorization check, claiming it replays host customizations or other configuration sources, or claiming SQL Server/PostgreSQL storage support.
- Editing consumer DAL migrations or copying only the database while WAL writes are active.

## Verification

Run both proxy library test projects plus ProxyContractBoundarySamplesTests,
ProxyLiteralRedirectSamplesTests, ProxyAdministrationSamplesTests and ProxyRewriteSamplesTests. Real SQLite
covers startup, compare-and-swap, reopen, ownership, corruption, schema upgrade,
audit idempotency/filtering/paging and retention. HTTP tests cover CSRF, login/IP
checks, throttling, audit failure, scoped cookies, credential rotation, host auth,
PathBase, local draft isolation, save/activation, stale writes and deletion.
Resolver tests cover engine-enforced remaining-budget timeouts, exhausted-budget
short-circuiting, configured budgets, invalid values, bounded caching, slow exact
hits/misses and accumulated rule work. The persisted HTTP sample binds a 75 ms
budget from configuration, verifies that an expensive regex returns 503 and that
a later request succeeds. Registration tests verify the 50 ms default.
Run the standalone sample and inspect login, list, editor and audit pages on desktop
and mobile. Exercise rewrite persistence/restart, native preview/forwarding parity, synthetic-input isolation, shared destinations, disabled rules, stale revisions, ambiguous matching, redirect precedence and rejected YARP reload/retry.
