# NewHeap ASP.NET Proxy SQLite

`AddNewHeapProxy` registers exact and regex redirect persistence, validation, runtime, and
startup loading, plus native YARP. The `Microsoft.Data.Sqlite` provider and all
SQL/schema code belong to this project. No EF Core or consumer DAL migrations are used.

## Storage configuration

`AddNewHeapProxy()` uses `App_Data/newheap-proxy.db` relative to the host content
root and a five-second busy timeout. Overloads accept `NhProxyOptions` and
`NhProxySqliteOptions` callbacks, or a configuration section with SQLite options
in its `Sqlite` child. Options are startup snapshots and are not rebound on reload.
The timeout is rounded up to whole seconds for the SQLite provider.

Startup creates missing parent directories and a missing database, acquires
exclusive application ownership through `<database>.lock`, and loads the stored
redirect snapshot before the server starts accepting requests. The database must
be a file outside the webroot on writable, durable local storage. In-memory and
network deployments are unsupported. A second NewHeap owner of the same file fails.
The lock file may remain after shutdown; ownership is its open file handle, not
its existence. Never delete it to bypass ownership while a host is running.

Schema version 2 uses `PRAGMA user_version` and `NhProxyConfiguration`, with
`Engine`, `Revision`, `FormatVersion`, and `Document` columns. The initial Redirect
row is revision zero, format 1, with an empty rule collection. Documents use
System.Text.Json web defaults (camel-case properties, numeric enum values).
Schema creation is transactional. Unknown schemas, missing/inconsistent records,
unknown JSON properties, unsupported match modes, or invalid rules fail rather
than resetting data. Version-1 databases upgrade transactionally while preserving
redirects. Version 2 adds NhProxyLoginAudit with UTC ticks, IP addresses, outcome,
correlation ID and successful account name, plus a time/ID index. Unknown versions
are rejected.

SQLite WAL is enabled. One conditional, parameterized UPDATE commits a whole
redirect document only when its expected revision matches. Reads/writes share a
bounded configuration gate; request processing never enters it. Back up after a
clean shutdown or with SQLite's backup API. Do not copy only the main database
while WAL writes are active. External live database edits and multiple writers
are unsupported; there is no file watcher or automatic refresh.

## Seed a redirect before starting the host

Prefer the MVC panel or INhProxyConfigurationService for live changes. The
following lower-level storage alternative supports offline setup. Choose an absolute path outside the webroot and use the same path for the
host. Read the current revision before replacing its complete rule collection:

```csharp
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;

var storageOptions = new NhProxySqliteOptions { DatabasePath = databasePath };
await using (var store = new NhProxySqliteConfigurationStore(Options.Create(storageOptions)))
{
    await store.InitializeAsync();
    var current = await store.LoadRedirectsAsync();
    var rule = new NhProxyRedirectRule
    {
        Id = Guid.NewGuid(),
        Name = "Moved page",
        Match = new NhProxyRedirectMatch { Path = "/old" },
        Target = "/new"
    };
    var result = await store.SaveRedirectsAsync(
        new NhProxyRedirectSaveRequest(current.Revision, current.Rules.Add(rule)));
    if (!result.Success)
    {
        // Handle validation or revision conflicts before starting the host.
        return;
    }
}

builder.Services.AddNewHeapProxy(configureStorage: options =>
    options.DatabasePath = databasePath);
var app = builder.Build();
app.UseNewHeapProxy();
app.Run();
```

Dispose the offline store before starting the host so it can acquire ownership.
The store validates redirects but does not activate them. The registered
INhProxyConfigurationService implements commit-then-publish orchestration for live
changes; the panel uses that service. NhProxySqliteLoginAuditStore implements
idempotent append, parameterized filtered paging and bounded retention deletion.
Audit writes must succeed before a login cookie is issued. Managed rewrite
storage remains unimplemented.
See the [proxy README](../NewHeap.Platform.AspNet.Proxy/README.md) for exact and regex matching
and query semantics. Real SQLite coverage lives in
`NewHeap.Platform.AspNet.Proxy.Sqlite.Tests` and sample case SPM-238. SQL Server
and PostgreSQL are explicit v1 capability gaps. All schema work is owned here;
no NewHeap release versions or consumer migrations change.
