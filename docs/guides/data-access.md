# Query and update data

Use `IRepository<TEntity>` for application queries and writes. Choose SQL Server
or PostgreSQL when configuring the host; the service code uses the same repository.

## Connect the database

Install the matching `NewHeap.Platform.AspNet.Common.PostgreSql` or
`NewHeap.Platform.AspNet.Common.SqlServer` package. In an application already
using `AddNewHeapPlatformAspNetCommon`, configure its identity DbContext with
one of these registration calls. `connectionString` comes from your host's
configuration.

For PostgreSQL, import `NewHeap.Platform.AspNet.Common.PostgreSql`:

```csharp
.WithIdentityEntityFramework(options =>
{
    options.UseNewHeapPostgreSql(connectionString);
})
```

For SQL Server, import `NewHeap.Platform.AspNet.Common.SqlServer`:

```csharp
.WithIdentityEntityFramework(options =>
{
    options.UseNewHeapSqlServer(connectionString);
})
```

See the [startup](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Program.cs)
for the surrounding NewHeap registration and
[sample DbContext](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.DAL/SampleProjectManagementDbContext.cs)
for entities and relationships. Your application's DAL owns its schema migrations.

## Read only what you need

Register `services.AddScopedNhDbRepository<Project>()`, then inject
`IRepository<Project>` from `NewHeap.Platform.AspNet.Common.DAL` into a service.
With `Microsoft.EntityFrameworkCore` imported, query through `GetAll()`:

```csharp
var items = await _repository.GetAll()
    .OrderBy(item => item.Name)
    .Select(item => new ProjectShortViewModel
    {
        Id = item.Id,
        Key = item.Key,
        Name = item.Name
    })
    .ToListAsync(cancellationToken);
```

This returns names in order while selecting just the dropdown fields. Add any
required user/division restriction before materializing the query. Filtering,
ordering and paging should also run before `ToListAsync` so the database does the
work. See `GetShortAsync` and `GetCollectionQuery` in
[ProjectService](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Services/ProjectService.cs).

## Commit a business operation

For a service operation that combines writes and events, open the transaction
before the first write:

```csharp
await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
```

Perform the writes, check every returned `TaskResult`, and publish events inside
that scope. Return a failure immediately when a nested result fails. Commit once,
after all steps succeed:

```csharp
await transaction.CommitAsync(cancellationToken);
```

The sample's [ProjectService.CreateAsync](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Services/ProjectService.cs) shows the complete sequence, including
rollback when event publication throws.

## Next steps

- [Angular collections](collections.md) to send filters and paging to your API.
- [Units of work](../consumer-guide/backend-unit-of-work.md) for nested scopes and retries.
- [Provider reference](../consumer-guide/database-providers.md) for SQL Server/PostgreSQL differences and relational tests.
