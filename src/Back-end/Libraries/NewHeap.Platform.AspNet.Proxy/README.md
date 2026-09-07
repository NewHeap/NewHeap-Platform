# NewHeap ASP.NET Proxy

Startup registration and typed contracts are available. `AddNewHeapProxy` registers
options and YARP with an empty in-memory configuration, allowing an ASP.NET host to
start. Managed forwarding, redirects, MVC administration, authentication, draft
execution, and persistence are not implemented. `UseNewHeapProxy()` on a
`WebApplication` maps native YARP endpoints, loads the initial configuration,
and returns the same application. Administration endpoints are not installed yet.

The .NET 10 Razor SDK project is prepared for ASP.NET Core MVC views and uses
the shared ASP.NET Core framework. It references `NewHeap.Platform.Common` for
the existing `TaskResult` and `Filterable` contracts. `Yarp.ReverseProxy` supplies
the native `IReverseProxyBuilder` callback contract; SQLite dependencies will be
added with persistence implementation.

The SQLite composition project references this library; this library does not
reference a storage provider. Both libraries are in the backend solution and
have dedicated, non-packable xUnit test projects using central package versions.
The tests verify host startup, option binding, YARP callbacks, typed JSON roundtrips,
defaults, and native route endpoint creation. They do not prove managed
forwarding or database behavior.

## Contract map

| Boundary | Contracts |
| --- | --- |
| Host settings | `NhProxyOptions`, `ConfigureYarp`, fixed administrator, allowlist, audit retention, and limits |
| Rewrite rules | `NhProxyRewriteRule`, match conditions, ordered typed transforms, single-destination `NhProxyCluster` |
| Redirect rules | `NhProxyRedirectRule`, path matching, status and query handling |
| Desired state | Immutable engine configurations and revision-checked replacement requests |
| Orchestration | `INhProxyConfigurationService`, `INhProxyConfigurationValidator`, `INhProxyRuntime` |
| Persistence | `INhProxyConfigurationStore` with independent writes and async ownership disposal |
| Draft tests | `INhProxyDraftTester`, synthetic input, match diagnostics and safe previews |
| Administration | `INhProxyAdministrationService`, login input, `INhProxyLoginAuditStore` |

Full-snapshot save requests cover create, update, enable/disable, and delete
without separate persistence methods per editor action. Revision zero represents
a new empty engine. Never infer runtime activation from a successful database
commit. Propagate failed activation results while retaining committed-save data.

Collection values use immutable standard-library types. Host options are mutable
for configuration binding. Credential options and login inputs are separate from
serializable rule and audit records. Inbound proxy trust uses the host's standard
ASP.NET Core forwarded-header configuration.

## Native YARP configuration callback

Call `NhProxyOptions.ConfigureYarp` with an
`Action<Microsoft.Extensions.DependencyInjection.IReverseProxyBuilder>`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Proxy.Sqlite;

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

Registration invokes the callback once after NewHeap's YARP base setup, not on
rule updates or dry-runs. `builder.Services.AddNewHeapProxy()` also works with
defaults. Complete the pipeline after building the host:

```csharp
app.UseNewHeapProxy();
app.Run();
```

`UseNewHeapProxy` calls `MapNewHeapProxy` internally, which uses native `MapReverseProxy` to install the YARP endpoint
source and loads its initial configuration. No separate initialization call is needed.
With the default empty provider, no forwarding routes are created.
Do not call `MapNewHeapProxy` separately after `UseNewHeapProxy`. The standalone
mapping method remains a lower-level alternative for `IEndpointRouteBuilder` hosts.
Managed redirect middleware will be added to `UseNewHeapProxy` later.
Both `NhProxyOptions` and `NhProxySqliteOptions` are available through `IOptions<T>`
as startup snapshots. Configuration reload does not rebind these options.
The read-only `YarpConfiguration` callback defaults to null. Repeated calls to
`ConfigureYarp` replace it; null arguments are rejected. It is excluded from JSON and cannot be configured
through appsettings or SQLite. Standard YARP extension methods remain available.
Host credentials are configured separately; the callback does not configure login.

## Verification

From `src/Back-end`, using the pinned SDK:

```text
dotnet build Libraries/NewHeap.Platform.AspNet.Proxy.Sqlite/NewHeap.Platform.AspNet.Proxy.Sqlite.csproj
dotnet test Tests/NewHeap.Platform.AspNet.Proxy.Tests/NewHeap.Platform.AspNet.Proxy.Tests.csproj
dotnet test Tests/NewHeap.Platform.AspNet.Proxy.Sqlite.Tests/NewHeap.Platform.AspNet.Proxy.Sqlite.Tests.csproj
```

Sample case SPM-238 starts a host with only Add/Use and constructs the contracts.
SPM-239 remains a `library-gap` for the managed proxy runtime,
including SQLite storage. SQL Server and PostgreSQL implementations are outside
version-one scope. Replace stub checks with executable behavior tests as features
are implemented.

See the [design document](../../../../docs/plans/newheap-proxy-design.md) for
agreed scope and remaining technical decisions. These scaffold packages are not
enrolled in the release manifest; release registration and version selection
belong to later release preparation.
