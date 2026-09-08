# Embedded proxy administration (SPM-238)

The standalone `SampleProjectManagement.Proxy` application demonstrates the library's
MVC panel with the same two calls used by a consuming application. It uses a real
SQLite file and serves `/projects` as a neutral redirect destination. It does not
require the SampleProjectManagement database or AppHost.

From `examples/SampleProjectManagement/src/Back-end`:

```text
dotnet run --project Applications/SampleProjectManagement.Proxy -- --hash-password
dotnet user-secrets set --project Applications/SampleProjectManagement.Proxy NewHeapProxy:Administrator:UserName administrator
dotnet user-secrets set --project Applications/SampleProjectManagement.Proxy NewHeapProxy:Administrator:PasswordHash "<generated hash>"
dotnet run --project Applications/SampleProjectManagement.Proxy -- --environment Development --urls http://localhost:5289
```

The hash command reads the password without displaying it and prints an ASP.NET
Identity password hash. Do not commit credentials or hashes. User secrets are a
local development facility; production uses the host's secret provider and HTTPS.
No account is enabled by default. Set `NewHeapProxy:Sqlite:DatabasePath` to change
the database path, which otherwise defaults to `App_Data/newheap-proxy.db` beneath
the application's content root.

Open `/newheap-proxy`, sign in, and create a redirect:

- Name: `Moved project overview`
- Source path: `/old-projects`
- Destination: `/projects?source=proxy`
- Status: `302`
- Test URL: `https://example.com/old-projects?campaign=sample`

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
(including a host PathBase), and require a new login after credentials or
`CredentialVersion` change. Credential options are startup snapshots: rotate them
in the host's secret provider and restart the application. Persist ASP.NET Data
Protection keys when sessions should survive ordinary process restarts. Production
administration requires HTTPS; HTTP is accepted only in Development.

Verification:

```text
dotnet test Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~ProxyAdministrationSamplesTests
dotnet test ../../../../src/Back-end/Tests/NewHeap.Platform.AspNet.Proxy.Sqlite.Tests --filter FullyQualifiedName~NhProxyAdministrationTests
```

The internal HTTP tests exercise CSRF, authentication, audit failure, IP denial,
throttling, credential rotation, PathBase, draft isolation, save/activation,
conflicting writes and deletion. The consumer test demonstrates the public
configuration service and the standalone application provides the real MVC UI.
SQLite is the implemented storage provider. SQL Server/PostgreSQL, managed rewrite
editing, full-pipeline draft testing and general redirect-cycle analysis remain
explicit SPM-239 gaps.
