# NewHeap ASP.NET Proxy SQLite

Store proxy redirects, rewrites and login activity in a local SQLite database.
`AddNewHeapProxy` sets up storage and loads saved rules at startup.

## Storage configuration

See [Configuration](../NewHeap.Platform.AspNet.Proxy/README.md#configuration) for
all proxy and SQLite options, their defaults and explanations.

Install `NewHeap.Platform.AspNet.Proxy.Sqlite` from nuget.org. It brings in
`NewHeap.Platform.AspNet.Proxy` automatically.

`AddNewHeapProxy()` uses `App_Data/newheap-proxy.db` relative to the host content
root and a five-second busy timeout. Overloads accept `NhProxyOptions` and
`NhProxySqliteOptions` callbacks, or a configuration section with SQLite options
in its `Sqlite` child. Restart the host after changing these settings.
The timeout is rounded up to whole seconds for the SQLite provider.

Startup creates missing parent directories and a missing database, acquires
exclusive application ownership through `<database>.lock`, and loads the stored
redirect and rewrite snapshots before the server starts accepting requests. The database must
be a file outside the webroot on writable, durable local storage. In-memory and
network deployments are unsupported. A second NewHeap owner of the same file fails.
The lock file may remain after shutdown; ownership is its open file handle, not
its existence. Never delete it to bypass ownership while a host is running.

The database is created and upgraded automatically while preserving existing
rules and login activity. Invalid or incompatible data prevents startup instead
of resetting the database. Back up before upgrading: older proxy versions may
not be able to reopen an upgraded database.

Back up after a clean shutdown or with SQLite's backup API. Do not copy only the
main database while the proxy is running; recent changes may be in SQLite's
write-ahead log. Use the administration panel or configuration service to edit
live rules. Direct database edits and multiple writers are unsupported.

## Seed a redirect before starting the host

Prefer the administration panel or `INhProxyConfigurationService` for live changes. The
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
The store saves rules but does not activate them. Use
`INhProxyConfigurationService` to save and activate changes in a running proxy.
See the [proxy README](../NewHeap.Platform.AspNet.Proxy/README.md) for exact and regex matching
and query behavior.
