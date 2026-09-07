# NewHeap ASP.NET Proxy SQLite

`AddNewHeapProxy` registers startup options and native YARP with an empty in-memory
configuration. A host can build, start, and stop with this registration.
`NhProxySqliteConfigurationStore` and `NhProxySqliteLoginAuditStore` remain stubs
and are not registered. No database access, schema initialization, or auditing is
implemented yet; no database file is created.

This .NET 10 library references `NewHeap.Platform.AspNet.Proxy`. SQLite packages
will be selected and added when persistence is implemented. Its non-packable
test project is `NewHeap.Platform.AspNet.Proxy.Sqlite.Tests`; it checks host startup,
option binding, YARP customization, and explicitly unavailable persistence operations.

`AddNewHeapProxy()` uses defaults. Overloads accept a configuration section or callbacks for
`NhProxyOptions` and `NhProxySqliteOptions`. Proxy options bind at the supplied
section; SQLite options bind under its `Sqlite` child. The default database path
is `App_Data/newheap-proxy.db`, relative to the host content root, with a five-second
busy timeout. Both option objects are available through `IOptions<T>` as startup
snapshots; configuration reload does not rebind them. The optional
`options.ConfigureYarp(yarp => { ... })` callback runs once per registration,
after YARP's empty configuration is registered. No temporary service provider is built.

Register once during host setup. Managed redirects/rewrites, storage initialization,
MVC administration, and authentication remain future work. Call `UseNewHeapProxy`
after building the `WebApplication`; it maps native YARP endpoints and loads the
initial configuration before returning the application. These two calls are sufficient;
do not additionally call `MapNewHeapProxy`. The default configuration is empty.
No separate YARP initialization call or administration mapping is performed by the host.

See the [proxy project README](../NewHeap.Platform.AspNet.Proxy/README.md) for
build/test commands and release status, and the
[design document](../../../../docs/plans/newheap-proxy-design.md) for the agreed scope.
