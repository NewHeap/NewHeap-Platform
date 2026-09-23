# Test application code

Use `NewHeap.Platform.Common.Test` for DI contexts and result assertions, and
`NewHeap.Platform.AspNet.Common.Test` for repository/DbContext helpers. Add them
to your application's test project alongside its own test framework.

## Test a repository with a small context

`NhDbContextTestingContext<TContext>` builds a service provider and registers
repositories for your DbSets. In a sample test, the following stores and reloads
a project:

```csharp
await using var context = new NhDbContextTestingContext<SampleProjectManagementDbContext>();
await context.BuildAsync();
using var scope = context.CreateScope();
var repository = scope.ServiceProvider.GetRequiredService<IRepository<Project>>();

var project = new Project
{
    Id = Guid.NewGuid(),
    DivisionId = Guid.NewGuid(),
    Key = "DOC",
    Name = "Documentation",
    Status = ProjectStatus.Active
};
repository.Add(project);
await repository.SaveChangesAsync();
repository.ClearTracking();

var persisted = await repository.FindAsync(project.Id);
Assert.Equal("DOC", persisted?.Key);
```

Import `NewHeap.Platform.AspNet.Common.Test`,
`NewHeap.Platform.AspNet.Common.DAL` and `Microsoft.Extensions.DependencyInjection`,
plus your DbContext and entity namespaces. `Assert` here is from xUnit.
The [complete test file](../../examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests/NewHeapTestHelperSamplesTests.cs)
includes the imports and other context examples.

This context uses EF InMemory for isolated tests. Use real SQL Server and
PostgreSQL databases to test query translation, relational constraints and
transactions.

## Assert service results

Import `NewHeap.Platform.Common.Test.Extensions` and assert the expected outcome
on the result returned by your service:

```csharp
var result = (await projectService.CreateAsync(model)).AsSuccess();
Assert.Equal(model.Key, result.Data?.Key);
```

Use `AsError()` when a validation failure is the expected outcome. The helper
fails the test if the result has the opposite status; you can then inspect its
data or errors.

## Run the example

From the repository root:

```sh
dotnet test examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~NewHeapTestHelperSamplesTests
```

The tests exercise repository registration, result assertions, scoped services
and substitute predicates.

## Further reading

See the [testing reference](../consumer-guide/testing.md) for custom contexts,
fixtures and relational test boundaries.
