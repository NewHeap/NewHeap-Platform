# NewHeap ASP.NET Proxy

The library loads exact and opt-in regex redirects from SQLite at startup and applies them
from an immutable in-memory snapshot before proxy and host endpoint execution.
The embedded MVC panel manages redirects, tests unsaved rules, audits
logins and activates saved changes. Managed rewrite storage/editing remains
unimplemented; native YARP customization is available.

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

`AddNewHeapProxy` registers options, SQLite storage, redirect validation/runtime,
and a hosted initializer. Initialization runs in the host's `StartingAsync`
phase, before its HTTP server starts, including with concurrent hosted-service
startup enabled. Missing databases are created; unreadable, invalid, or
incompatible storage fails startup instead of substituting empty rules.

`UseNewHeapProxy` installs the administration branch and redirect middleware and calls `MapNewHeapProxy`
internally. Do not call both on the same `WebApplication`. Standalone
`MapNewHeapProxy(IEndpointRouteBuilder)` remains a lower-level YARP-only mapping
alternative; it does not install redirect middleware. Host-configured forwarded
headers and other required host middleware belong before `UseNewHeapProxy`.

Native YARP mapping loads its own initial configuration. Its provider remains
empty until configured; redirect processing does not depend on rewrite activation.
The reserved administration branch is installed before redirects and YARP, including catch-all routes.

## Administration

Open `/newheap-proxy`. The MVC panel provides search, create/edit, enable/disable,
priority, host/method restrictions, query modes, deletion confirmation, login
activity, a **Use regular expression** checkbox and a local draft test. Test evaluates one unsaved rule with the
same runtime matcher, without storage writes or outbound requests. It does not
simulate the full ordered rule set or execute managed rewrites.

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
  Prefix and route-template matching are still rejected.
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
to the host/YARP pipeline. General cycle analysis and absolute self-redirect
diagnostics remain future work.

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
an actual redirect. General multi-rule cycle detection remains pending.

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
Conflicts do not overwrite another editor's work. Managed rewrite state remains
independent and uninitialized.

The lower-level store persists only, and the lower-level runtime publishes only.
Both remain useful for offline setup or explicit orchestration. Requests never
query SQLite. Do not bypass the configuration service for routine live changes.

## Native YARP customization

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
storage connection is disposed. SPM-239 remains a `library-gap` for the remaining
managed rewrite features, full-pipeline draft tests and general cycle analysis.
SQLite is the implemented provider; SQL Server and PostgreSQL are outside v1.

The neutral library references ASP.NET Core, YARP, and NewHeap Common contracts;
SQLite-specific dependencies stay in the SQLite composition project. See the
[design document](../../../../docs/plans/newheap-proxy-design.md) for the complete
planned scope. These unreleased packages have no release-unit membership yet;
release versions remain unchanged.
