# NewHeap ASP.NET Proxy

The library loads exact and opt-in regex redirects from SQLite at startup and applies them
from an immutable in-memory snapshot before proxy and host endpoint execution.
The embedded MVC panel manages redirects and stored rewrites, tests unsaved rules,
audits logins and activates saved changes. Stored rewrites use native YARP routes,
matching and transforms. Rewrites from appsettings or other sources are covered
by host-owned native YARP APIs/configuration through `ConfigureYarp`; no additional
NewHeap source integration is planned. Managed editing and testing cover stored rules only.

## Host integration

```csharp
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNewHeapProxy();

var app = builder.Build();
app.UseNewHeapProxy();
app.Run();
```

`AddNewHeapProxy` registers options, SQLite storage, both rule engines and draft testing,
and a hosted initializer. Initialization runs in the host's `StartingAsync`
phase, before its HTTP server starts, including with concurrent hosted-service
startup enabled. Missing databases are created; unreadable, invalid, or
incompatible storage fails startup instead of substituting empty rules.

`UseNewHeapProxy` installs the administration branch and redirect middleware and calls `MapNewHeapProxy`
internally. Do not call both on the same `WebApplication`. Standalone
`MapNewHeapProxy(IEndpointRouteBuilder)` remains a lower-level YARP-only mapping
alternative; it does not install redirect middleware. Host-configured forwarded
headers and other required host middleware belong before `UseNewHeapProxy`.

Native YARP mapping loads stored rewrites through a dedicated InMemoryConfigProvider;
redirect processing does not depend on rewrite activation.
The reserved administration branch is installed before redirects and YARP, including catch-all routes.

## Administration

Open `/newheap-proxy`. The MVC panel provides search, create/edit, enable/disable,
priority, host/method restrictions, query modes, deletion confirmation, login
activity, a **Use regular expression** checkbox and a local draft test. Test evaluates one unsaved rule with the
same runtime matcher, without storage writes or outbound requests. It does not
simulate the full ordered rule set. The separate rewrite editor tests a candidate
against both saved managed engines using `INhProxyDraftTester`.

Configure the single account through host-owned options:

```csharp
builder.Services.AddNewHeapProxy(options =>
{
    options.Administrator.UserName = builder.Configuration["ProxyAdmin:UserName"]!;
    options.Administrator.PasswordHash = builder.Configuration["ProxyAdmin:PasswordHash"]!;
    options.Administrator.CredentialVersion = "1";
    options.IpAllowlist.Enabled = true;
    options.IpAllowlist.Entries = ["192.0.2.0/24"];
});
```

Use an ASP.NET Identity `PasswordHasher<string>` hash from your secret provider.
No default account is created; missing credentials return 503 for the panel while
ordinary proxy traffic continues. HTTPS is required outside Development. The panel
uses a dedicated HttpOnly, SameSite=Strict session cookie scoped to its PathBase,
CSRF protection on POST forms, no-store responses and a restrictive CSP. The host's
explicit/default authentication scheme is preserved. Do not explicitly choose the
NewHeapProxy scheme as your host's default scheme. A custom host scheme provider
remains host-owned and must likewise keep the administration scheme isolated.

Every administration request checks the optional IP allowlist. Enabled with no
entries denies access. Only trusted host forwarding middleware may alter the
client IP; configure it before UseNewHeapProxy. Login attempts are audited with
UTC timestamps and IP addresses, without attempted usernames or passwords. Audit
failure cannot grant a new session. One process-wide account budget defaults to
five attempts per minute. Retention cleanup deletes up to 1,000 expired events
per credential attempt; idle hosts perform no background cleanup.

Changing credentials or CredentialVersion invalidates existing cookies after the
new startup options are loaded. Session duration defaults to eight hours. Host
Data Protection configuration controls key persistence across ordinary restarts.

See the [runnable administration sample](../../../../examples/SampleProjectManagement/docs/proxy-administration.md)
for password-hash generation, host configuration and verification.
## Literal redirects

- `PathMode = Exact` remains the default. Compare against `HttpRequest.Path` using
  ordinal, case-sensitive equality. Trailing slashes matter. With `UsePathBase`,
  matching uses the remaining path. The query is not part of path matching.
- With the checkbox off, regex characters and target substitutions remain literal.
  The separate Prefix and RouteTemplate modes are rejected; use Regex with capture
  groups for those patterns. Additional match modes are not planned.
- Optional hosts match case-insensitively. A host without a port matches any
  request port; a configured port must match. Host wildcards are rejected.
  Methods are exact, case-sensitive HTTP tokens; empty host/method lists mean all.
- Disabled rules never match. Lower priority wins, then stable `Guid` ordering.
  `/newheap-proxy` and its descendants bypass redirect processing.
- Status codes `301`, `302`, `303`, `307`, and `308` are supported; `302` is the default.
- Targets are literal root-relative paths or absolute HTTP(S) URLs. Root-relative
  targets are rooted at the origin, not `PathBase`. Control characters, backslashes,
  network-path targets, userinfo, and other schemes are rejected. Root-relative
  same-path redirects are rejected conservatively even when only the query changes.
- Preserve mode merges incoming and target query values using ASP.NET query
  parsing. Target keys replace incoming keys case-insensitively; repeated values
  survive and query escaping is normalized. Replace keeps only target query
  values; Discard removes all query values. Fragments are retained.

Each request captures one snapshot and never queries SQLite. A matching redirect
sets the status and `Location` and ends processing; an unmatched request continues
to the host/YARP pipeline. Before execution, a bounded local chain check refuses
requests exceeding `Limits.MaximumChainDepth` (default 2), including local loops
and absolute self-redirects. External origins and backend responses are not followed.

Redirect resolution has a shared wall-clock budget (50 ms by default), including exact rules,
host/method checks and destination construction. Expiry or a regex timeout returns
HTTP 503 with `Cache-Control: no-store`, without a Location header or executing
downstream endpoints. Each regex receives only the remaining budget as its native
engine timeout, so backtracking is interrupted inside the engine. No background
task is abandoned. Checks between stages stop further resolver work. Non-regex
synchronous operations and runtime scheduling are not hard-preemptible; the final
result is checked before committing a redirect or allowing a no-match through.

Configure `NewHeapProxy:Limits:RedirectResolutionTimeoutMilliseconds` as an integer number of milliseconds:

```json
{
  "NewHeapProxy": {
    "Limits": {
      "RedirectResolutionTimeoutMilliseconds": 50
    }
  }
}
```

Use `AddNewHeapProxy(builder.Configuration.GetSection("NewHeapProxy"))` to bind
the section, or set `options.Limits.RedirectResolutionTimeoutMilliseconds` in the existing
registration callback. The runtime captures the value at startup; restart after
changing it. The same budget applies to the administration draft tester. Values
below 1 ms or above 2,147,483,646 ms (the regex engine limit) are rejected.

## Regex redirects

Enable **Use regular expression**, or set `Match.PathMode = NhProxyRedirectPathMatchMode.Regex`.
The pattern matches `HttpRequest.Path.ToUriComponent()` plus the incoming query
string, including `?`. PathBase is excluded. Matching is case-sensitive unless
the pattern specifies otherwise; use `^` and `$` when the entire input must match.
The first match expands the complete destination using [.NET substitutions](https://learn.microsoft.com/en-us/dotnet/standard/base-types/substitutions-in-regular-expressions):
`$1`, `${name}` and `$$` for a literal dollar sign. Unmatched input is not appended.

For example, `^/old-projects/([^?]+)(\?.*)?$` with target `/projects/$1$2` maps
`/old-projects/42?tag=a%26b` to `/projects/42?tag=a%26b`. Omitting `$2` drops the
incoming query. **All QueryMode settings are ignored for regex rules**: there is
no automatic merge, preserve or discard step. Captures keep their URL escaping;
the final URL is normalized for the Location header without parsing/merging query values.

Patterns and reusable timeout variants belong to each published snapshot. Invalid patterns or unsafe
target templates fail validation. Absolute destination authorities must be fixed;
substitutions belong in the path, query or fragment. Expanded destinations are
validated again, including network-path targets and root-relative self-redirects.
Regex timeouts are rounded down to whole milliseconds of remaining budget. With
less than 1 ms left, no new regex starts. The engine throws RegexMatchTimeoutException
to stop matching; this is not a timer that merely stops waiting for CPU work.
Timeout variants are cached lazily, at most 50 per rule regardless of the configured
budget, and discarded with the snapshot. Additional variants run without being
retained. Preparing a new variant also consumes budget, which is checked again
before matching. Scheduling and the engine's timeout checks can still cause a
small overrun; this is not a real-time guarantee.
Regex inputs and expanded targets are limited to 65,536 characters, patterns to
2,048 and templates to 4,096. An input/output limit or unsafe expansion stops
redirect evaluation and passes through unless the resolver budget expired.
Timeouts return HTTP 503. The draft test explains the failure without returning
an actual redirect. Local regex self-redirects are refused by the chain-depth guard with HTTP 508.

Existing SQLite documents retain exact matching. Regex mode uses the existing
rule document without a schema migration; runtimes predating regex support cannot
load documents containing regex rules. Host/method filters, ordering, status codes
and administration-path protection apply equally to both modes.

## Persistence and activation

See the [SQLite README](../NewHeap.Platform.AspNet.Proxy.Sqlite/README.md) for file
ownership, schema, and an offline seeding example. `SaveRedirectsAsync` replaces
the complete redirect document using an expected revision. Validation and stale
writes return failed `TaskResult` values; always inspect `Success`.

Use `INhProxyConfigurationService.SaveRedirectsAsync` for live changes. It commits
first and publishes the new snapshot even if the request is cancelled after the
commit. A failed activation retains `NhProxySaveResult` in the failed TaskResult's
Data; the panel shows the saved/active revision difference and offers retry.
Conflicts do not overwrite another editor's work. Managed rewrite state activates
independently through `SaveRewritesAsync` and its own mutation gate.

The lower-level store persists only, and the lower-level runtime publishes only.
Both remain useful for offline setup or explicit orchestration. Requests never
query SQLite. Do not bypass the configuration service for routine live changes.

## Stored rewrites and the test tool

Open **Rewrites** in `/newheap-proxy`, create a rule, enter a path template and
HTTP(S) backend URL, and optionally choose a path transform. Destinations may be
shared; explicitly load an existing destination before editing it. Changes to a
shared destination affect every referencing rule. The advanced JSON sections
preserve ordered transforms, header/query conditions, policies, timeouts and
health checks. Visible form fields take precedence over their corresponding
advanced JSON properties; unknown properties are rejected.

The backend URL's base path is prepended after the rule's path transforms.
For `/api/{**rest}`, destination `https://backend.example/base/`, and a remove-prefix
transform `/api`, request `/api/projects/42?source=test` becomes
`https://backend.example/base/projects/42?source=test`. There is no browser redirect.
Matching retains ASP.NET/YARP host, method, path constraint, header, query and
priority semantics. Exact duplicate enabled matches at the same priority are
rejected; broader ambiguity is reported by the tester. Avoid destinations that
route back into the same proxy rule; the local chain-depth guard refuses overlong
chains before forwarding, while cross-service/backend response cycles are outside its scope.

`SaveRewritesAsync` validates the full rule/cluster candidate, commits its own
revision and publishes to the dedicated native provider. YARP's
`IConfigChangeListener.ConfigurationApplied` confirms the exact publication token.
An update notification alone is not success. A rejected reload retains the old
active revision; five seconds without confirmation reports `Unconfirmed`. A late
callback may still confirm it. The failed save result retains its committed
`SavedRevision` in `Data`. Retry activation without creating another revision.

**Test draft** evaluates the draft in place of the rule with the same ID, alongside
all other stored rules. It checks both expected revisions, includes optional draft
cluster replacements, preserves disabled state unless **Simulate enabled** is
selected, and gives redirects precedence. Output includes the winning rule, route
values, transformed target URL, safe outgoing headers and unevaluated response
header transforms. The reserved administration path bypasses both engines.
The administration top bar accepts a complete HTTP(S) URL or a path such as
`/foo?s=1`. A path uses the administration request's scheme, host and port, without
prepending the administration path or PathBase. Trusted forwarded-header middleware
must run before `UseNewHeapProxy` when an upstream proxy supplies the public origin.
The result displays the resolved request URL and previews a GET
against saved managed rules. It shows the winning redirect or rewrite, target URL
and an edit link, or an explicit no-match/reserved-path result. Saved-versus-active
revision differences are highlighted. It never contacts the destination, so an
upstream redirect or error is not part of this preview.

`INhProxyDraftTester.TestSavedAsync` takes both expected revisions and synthetic
input to test saved rules without supplying a draft. Disabled rules remain disabled.
`INhProxyDraftTester.TestRewriteAsync` and `TestRedirectAsync` expose the same
isolated managed-rule evaluator to code. No server is started, no live provider
is changed, no document is saved and no upstream requests or health probes run.

Only synthetic non-secret headers are accepted. Preview does not copy credentials
or cookies from the administrator request. The default request input limit is
64 KiB and the test budget is five seconds. Cancellation and stage deadlines are
cooperative; native routing constraints retain their own evaluation limits.
Preview does not prove connectivity, health, authorization, CORS, rate limiting
or upstream responses, and does not replay host callbacks, filters, custom
constraints/transforms or other configuration sources. Policy registration remains
host-owned. `UseNewHeapProxy` installs routing, authentication and authorization
after redirects. When using CORS, rate limiting or request timeouts, compose their
middleware after route selection and before endpoint execution as required by ASP.NET.

## Native YARP customization

For native customization, use the callback below. The managed chain limit is
configured separately through `Limits.MaximumChainDepth`.

```csharp
builder.Services.AddNewHeapProxy(options =>
{
    options.ConfigureYarp(yarp =>
    {
        yarp.ConfigureHttpClient((_, handler) =>
        {
            handler.ConnectTimeout = TimeSpan.FromSeconds(5);
        });
    });
});
```

Import `Microsoft.Extensions.DependencyInjection` for YARP builder extensions.
The callback runs once per registration after NewHeap's base YARP setup. Repeated
`ConfigureYarp` calls replace it; null arguments are rejected. `YarpConfiguration`
is excluded from JSON and cannot be configured through appsettings or SQLite.
Both option types are startup snapshots exposed through `IOptions<T>`.

## Local rule-chain limit

`UseNewHeapProxy` checks the active managed-rule chain before sending a redirect
or forwarding a request. Each selected redirect or rewrite counts as one step;
exactly two steps are allowed by default, while a third returns **HTTP 508 Loop
Detected** with `Cache-Control: no-store` and no `Location` or outbound request.
Even a legitimate longer chain is refused. Rules may still be saved; the check
uses the concrete incoming URL, method, headers and query at request time.

```json
{
  "NewHeapProxy": {
    "Limits": { "MaximumChainDepth": 2 }
  }
}
```

Set a positive integer through configuration binding or
`options.Limits.MaximumChainDepth`. The value is captured at startup. The tester
checks its saved/draft candidate with the same limit and returns the safe failure
code `NhProxyErrorCodes.MaximumChainDepth` when exceeded.

Analysis follows targets only within the initial scheme/host/port, respects
PathBase and reserved administration paths, and stops at external destinations
or native routes outside the managed source. It does not contact destinations,
query SQLite per request, collapse redirects, or carry counters in cookies,
headers or URLs. For redirect simulation, 303 switches to GET except for HEAD;
301/302 switch POST to GET, and 307/308 preserve the method. External aliases,
host endpoint behavior, custom runtime effects and backend responses are outside
this local analysis. Native routing is reused without executing endpoints;
standard request transforms are applied only when following a local rewrite.

## Verification and remaining scope

From `src/Back-end`, using the pinned SDK:

```text
dotnet test Tests/NewHeap.Platform.AspNet.Proxy.Tests/NewHeap.Platform.AspNet.Proxy.Tests.csproj
dotnet test Tests/NewHeap.Platform.AspNet.Proxy.Sqlite.Tests/NewHeap.Platform.AspNet.Proxy.Sqlite.Tests.csproj
```

SPM-238 demonstrates startup, contracts, and real SQLite-backed HTTP redirects in
`ProxyContractBoundarySamplesTests`, `ProxyLiteralRedirectSamplesTests` and `ProxyAdministrationSamplesTests`.
Tests cover matching, query handling, safe targets, atomic publication, persistence,
restart, stale writes, exclusive ownership, invalid data, and requests after the
storage connection is disposed. SPM-239 adds `ProxyRewriteSamplesTests`: isolated
preview, real forwarding, route capture/query transform parity and independent activation.
Library tests additionally cover restart, native header/query/host/method matching,
disabled drafts, conflicts, ambiguity, rejected reload/retry and secured MVC CRUD.
Regex with capture groups covers prefix and route-template redirects. Local
managed chains are bounded by MaximumChainDepth; external cycle analysis is excluded.
SQLite is the implemented provider; SQL Server and PostgreSQL are outside v1.

The neutral library references ASP.NET Core, YARP, and NewHeap Common contracts;
SQLite-specific dependencies stay in the SQLite composition project. See the
[design document](../../../../docs/plans/newheap-proxy-design.md) for the complete
planned scope. These unreleased packages have no release-unit membership yet;
release versions remain unchanged.
