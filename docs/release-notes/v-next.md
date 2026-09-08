# v-next

## NewHeap.Platform.AspNet.Common

| Breaking change | Required action |
|---|---|
| SQL Server and PostgreSQL dependencies removed | Add `NewHeap.Platform.AspNet.Common.SqlServer` or `NewHeap.Platform.AspNet.Common.PostgreSql`. |
| `UseConfiguredDatabase(...)` removed | Use `UseNewHeapSqlServer(...)` or `UseNewHeapPostgreSql(...)`. |
| `WithHangfire(...)` now requires a storage callback | Pass `o => o.UseSqlServerStorage(connectionString)` or `o => o.UsePostgreSqlStorage(s => s.UseNpgsqlConnection(connectionString))`. |
| `InternalNhIdentityDbContextFactory<...>` constructor changed | Replace the `IConfiguration` argument with `Action<DbContextOptionsBuilder>`. |

## NewHeap.Platform.AspNet.Common.SqlServer

| Breaking change | Required action |
|---|---|
| SQL Server bulk operations and locks require explicit registration | Configure affected contexts with `UseNewHeapSqlServer(...)` from this package. |

## NewHeap.Platform.AspNet.Common.PostgreSql

| Breaking change | Required action |
|---|---|
| PostgreSQL bulk operations and locks require explicit registration | Configure affected contexts with `UseNewHeapPostgreSql(...)` from this package. |
| PostgreSQL migrations assembly no longer configured automatically | Pass `Database:PostgreSqlMigrationsAssembly` to `MigrationsAssembly(...)` in the provider options callback. |
| `ValidatePostgreSqlColumnTypes(...)` moved to this package | Reference this package; change qualified static calls to `PostgreSqlModelBuilderExtensions.ValidatePostgreSqlColumnTypes(...)`. |
