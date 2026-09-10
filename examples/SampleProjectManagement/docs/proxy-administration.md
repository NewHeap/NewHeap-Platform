# Embedded proxy administration (SPM-238)

The standalone `SampleProjectManagement.Proxy` application demonstrates the library's
MVC panel with the same two calls used by a consuming application. It uses a real
SQLite file and serves `/projects` as a neutral redirect destination. It does not
require the SampleProjectManagement database or AppHost.

## Supported hosting

Host the proxy at the origin root, for example `https://proxy.example/`, with
administration at `/newheap-proxy`. Hosting the proxy under a subdirectory such
as `https://example.com/proxy/` with a non-empty `PathBase` is not supported.
The URL/draft tester does not model that mount prefix and can report no match
for requests that match at runtime. Do not configure `UsePathBase` for this
deployment. Subdirectory hosting is deferred until a concrete use case requires
it; no implementation is planned as part of the current production scope.

## Run the demo

From `examples/SampleProjectManagement/src/Back-end`:

```text
dotnet run --project Applications/SampleProjectManagement.Proxy --launch-profile "Proxy demo"
```

Alternatively, select this project and the **Proxy demo** profile in Visual Studio
or Rider. Open http://localhost:5289/newheap-proxy and sign in with the fixed test
account `administrator` / `NewHeap123!`. No credential or database setup is needed.
This explicit mode requires Development and restricts administration to loopback
addresses using the library's IP allowlist. Its launch profile listens on localhost.
It overrides the account and database settings only for this demo; the library
does not gain default credentials.

The demo creates `App_Data/proxy-demo.db`, excluded from Git, and seeds a literal
302 from `/old-projects` to `/projects?source=proxy` only at revision zero. Try
http://localhost:5289/old-projects?campaign=demo, then edit or test the rule in the
panel. Changes and deletions survive restarts without reseeding. Restarting also
requires a fresh login because the test password receives a new salted hash.

## Run through Aspire

Select `SampleProjectManagement.AppHost` as the startup project, or run from
`examples/SampleProjectManagement/src/Back-end`:

```text
aspire start --apphost Orchestration/SampleProjectManagement.AppHost/SampleProjectManagement.AppHost.csproj
```

Open the `sample-project-management-proxy` resource URL in the dashboard. It points
to `/newheap-proxy` on an automatically assigned localhost port. The AppHost selects
the **Proxy demo** profile, so the same `administrator` / `NewHeap123!` account and
`App_Data/proxy-demo.db` apply. Stop a standalone instance before starting Aspire;
the SQLite file permits one owning process.

The proxy starts independently of PostgreSQL, RabbitMQ and the API. Shared service
defaults provide Development `/health` and `/alive` endpoints; Aspire checks `/alive`
for resource health. The administration endpoint is excluded from service discovery
references to other resources.

## Configure your own account

For a live environment, the recommended approach is to inject credentials through
environment variables using the deployment platform's secret settings:

```text
NewHeapProxy__Administrator__UserName=administrator
NewHeapProxy__Administrator__Password=<password supplied by your deployment secret>
```

The sample binds the `NewHeapProxy` configuration section. A consuming host uses
`AddNewHeapProxy(builder.Configuration.GetSection("NewHeapProxy"))`. The standard
environment provider maps `__` to configuration sections and overrides appsettings.
Keep credential values out of committed files. Live administration requires HTTPS.
Run without `ProxyDemo`; the Development-only demo deliberately overrides credentials.

`Password` is hashed once at startup and cleared from the proxy options; it is not
saved in SQLite, the login audit or serialized options. The original environment
value remains managed by the host. Passwords must contain a non-whitespace character
and be at most 1024 characters; spaces are preserved. Restart after changing the
environment. Each startup with `Password` creates a fresh salted hash, requiring
administrators to sign in again.

For local development, user-secrets can supply the same `UserName` and `Password`
keys. Alternatively, configure a precomputed ASP.NET Identity hash using
`NewHeapProxy__Administrator__PasswordHash` and leave `Password` unset. Setting
both fails startup. A stable hash retains the existing session behavior.

To use the precomputed-hash alternative locally, run from
`examples/SampleProjectManagement/src/Back-end`:

```text
dotnet run --project Applications/SampleProjectManagement.Proxy -- --hash-password
dotnet user-secrets set --project Applications/SampleProjectManagement.Proxy NewHeapProxy:Administrator:UserName administrator
dotnet user-secrets set --project Applications/SampleProjectManagement.Proxy NewHeapProxy:Administrator:PasswordHash "<generated hash>"
dotnet run --project Applications/SampleProjectManagement.Proxy --launch-profile "Configured proxy"
```

The hash command reads the password without displaying it and prints an ASP.NET
Identity password hash. Do not commit credentials or hashes. User secrets are a
local development facility; use the environment-variable setup above for production.
The configured profile has no default account. Set `NewHeapProxy:Sqlite:DatabasePath` to change
the database path, which otherwise defaults to `App_Data/newheap-proxy.db` beneath
the application's content root.

Open `/newheap-proxy`, sign in, and create a redirect:

- Name: `Moved project overview`
- Source path: `/old-projects`
- Destination: `/projects?source=proxy`
- Status: `302`
- Test URL: `https://example.com/old-projects?campaign=sample`

For regex matching, enable **Use regular expression**. For example:

- Source: `^/old-projects/([^?]+)(\?.*)?$`
- Destination: `/projects/$1$2`
- Test URL: `https://example.com/old-projects/42?campaign=sample`
- Result: `/projects/42?campaign=sample`

The regex receives the escaped path plus query string. Use numbered captures
such as `$1` or named captures such as `${id}` in the destination. Query controls
are hidden and ignored in this mode: only query values explicitly included in
the target survive. Omitting `$2` in this example drops the incoming query.
Leave the checkbox off to retain exact path matching and the existing query modes.
The test also reports invalid expanded destinations and regex timeouts without
saving the draft. Absolute destination hosts must remain fixed in the template.

Live redirect resolution has a shared budget for exact and regex rules, defaulting
to 50 ms. Set `NewHeapProxy:Limits:RedirectResolutionTimeoutMilliseconds` to an integer
such as `100` for 100 ms, then restart. The same setting applies to draft tests.
Expiry or a regex timeout returns HTTP 503 with `Cache-Control: no-store`; no
redirect or backend request follows. Each regex gets only the remaining budget;
the engine's own timeout interrupts matching without leaving background CPU work.
Other synchronous operations and runtime scheduling can still overrun the deadline.
`ProxyLiteralRedirectSamplesTests` binds a 75 ms budget and demonstrates the 503
response using a deliberately expensive pattern; avoid nested repetitions such
as `(a+)+` in real rules.

**Test draft** shows the response for the unsaved rule without making a network
request. **Save and activate** commits a new revision and publishes it immediately.
Visit `/old-projects?campaign=sample` to reach the destination. Edit the rule to
disable it or change its priority; deletion has a separate confirmation page.
A stale form receives a conflict and cannot overwrite another tab's changes.
The overview shows saved and active revisions and offers activation retry if needed.
Login activity shows UTC timestamps, outcomes and client IP addresses.

Optional settings include `NewHeapProxy:IpAllowlist:Enabled` and its `Entries`
array of literal IPv4/IPv6 addresses or CIDRs. An enabled empty array denies all
administration access. Only configure forwarded headers for proxies you trust,
and run that middleware before `UseNewHeapProxy`; the library does not trust
client-supplied forwarding headers on its own. The raw connection peer address
is not retained when host middleware has already changed it.

Sessions expire after eight hours by default, use a cookie scoped to the panel
and require a new login after credentials or
`CredentialVersion` change. Credential options are startup snapshots: rotate them
in the host's secret provider and restart the application. Persist ASP.NET Data
Protection keys when sessions should survive ordinary process restarts. Production
administration requires HTTPS; HTTP is accepted only in Development.

Verification:

```text
dotnet build Applications/SampleProjectManagement.Proxy --configuration Release
pwsh -NoProfile -File ../../tools/verify-proxy-demo.ps1
dotnet test Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~ProxyAdministrationSamplesTests
dotnet test ../../../../src/Back-end/Tests/NewHeap.Platform.AspNet.Proxy.Sqlite.Tests --filter FullyQualifiedName~NhProxyAdministrationTests
```

The PowerShell smoke check starts the actual compiled demo with a temporary content
root, checks the health endpoints, signs in with the documented test account, verifies the seeded 302, deletes
the example rule, restarts against the same SQLite file and verifies that deletion
persists. It also checks that Production rejects demo mode. It never touches the
developer's demo database.

The internal HTTP tests exercise CSRF, authentication, audit failure, IP denial,
throttling, credential rotation, PathBase, draft isolation, save/activation,
conflicting writes and deletion. The consumer test demonstrates the public
configuration service and the standalone application provides the real MVC UI.
SQLite is the implemented storage provider. SQL Server/PostgreSQL remain explicit
capability gaps. Local managed chains are checked against a configurable depth limit.

## Stored rewrites and validation

Use **Test a URL** in the administration top bar for a quick GET preview against
all saved managed rules. The result identifies the winning redirect or rewrite,
its target and an **Edit matched rule** link.
Enter `/foo?s=1` to use the proxy's current scheme, host and port, or supply a
complete HTTP(S) URL to test another host. The result shows the resolved request
URL. Paths start at `/`; the administration path and PathBase are not prepended.
Redirects take precedence; disabled
rules are skipped. No match and reserved administration paths have explicit
results. A warning appears when saved revisions differ from active revisions.
The test sends no backend request and cannot reveal backend redirects or errors.
Use the rewrite editor for custom methods, synthetic headers and unsaved drafts.

Open **Rewrites** in the same panel, then **Create rewrite**. For a basic preview:

1. Enter a name and path template `/api/{**rest}`.
2. Set the backend URL to `https://backend.example/base/`.
3. Choose **Remove prefix** with value `/api`.
4. Enter test URL `https://public.example/api/projects/42?source=test` and choose **Test draft**.
5. Expect `https://backend.example/base/projects/42?source=test` as the backend request URL.

This example destination is for preview only. Replace it with your real backend
before saving. Testing does not make network requests, perform health probes,
write SQLite or change the running routes. Saved managed redirects take precedence
and the test shows when another rule wins. Both saved engine revisions must still
match the editor. A disabled draft only participates when **Simulate enabled** is
selected. Synthetic headers use JSON string arrays; credential headers are rejected.

Save and activate uses the separate rewrite revision and waits for native YARP
confirmation. A rejected reload retains the previous active revision, and the
list offers an activation retry. Deletion retains destinations for reuse. To share
a destination, select it and choose **Load destination** before editing; changes
affect every rule referencing that destination. Expand the advanced sections for
header/query matches, additional ordered transforms, host policy names, timeouts
and health checks. The JSON shows the complete current draft, including the first
path transform. Form fields and valid JSON edits synchronize immediately in both
directions. Invalid JSON stays visible for correction. The path control edits the
first transform when it is a path transform, without duplicating it on save.
IDs are editor-owned; without JavaScript, form fields take precedence on submission.
To verify synchronization, edit the path operation and backend URL, then inspect
both JSON fields. Change the name and a transform in JSON and check the form.
Temporarily enter invalid JSON and verify it stays intact when another field changes.
Restore valid JSON, save and reopen the rule; the transform order and count must
remain unchanged. Testing the draft must produce the same target URL.
JSON is the intended developer interface for these settings. Documentation links
beside the fields open YARP matching/policy, transform, health-check and timeout
references in a new tab, preserving the draft. Retain the displayed NewHeap JSON
structure; native YARP examples use different property names and nesting.

The test tool evaluates stored managed rules only. Appsettings/other configuration
sources, host callbacks/customizations, connectivity, policy enforcement and actual
backend responses are outside its scope. Response header transforms are reported
as unevaluated. Set `NewHeapProxy:Limits:MaximumChainDepth` to a positive integer
(default `2`). Each matched redirect or rewrite counts as a step; a third step
at the default limit is refused before execution with HTTP 508, no Location and
no outbound request. The URL/draft tester reports the same refusal. The check
uses the transformed general and content headers for subsequent matching,
including multiple values and header replacements/removals. Preview results
include safe content headers such as `Content-Type` and `Content-Language`.
Native YARP preserves these headers without reading the real request body;
secret and framing headers remain excluded from simulation. The check
follows only the current scheme/host/port and stops outside the host PathBase or
at the reserved administration path. External origins and backend responses are
not followed; this does not prove that external redirect chains are loop-free.

SPM-239 is executable in `ProxyRewriteSamplesTests`: a local backend proves that
the public tester and real forwarding agree, while the preview makes no backend
calls and leaves revisions untouched. It verifies content-header preview and
intact body forwarding, and also demonstrates a two-redirect chain
ending in a rewrite being refused at the default depth of two. From the sample backend directory run:

```text
dotnet test Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~ProxyRewriteSamplesTests
```
