# Build an API module

A NewHeap module connects an entity, input/output models, a service and a
controller. The project module below exposes project CRUD through the standard
NewHeap base classes.

Start with the [Angular/API lister walkthrough](first-project-lister.md), then
[add CRUD](project-crud.md).

## Start from a working module

Use an application with [NewHeap and a database configured](data-access.md).
The sample's [ProjectController](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Controllers/ProjectController.cs)
and [ProjectService](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Services/ProjectService.cs)
provide the constructors and startup dependencies for the excerpts below.

| Part | Responsibility |
| --- | --- |
| `Project` and its `DbSet` | Persisted fields and relationships |
| `ProjectMutateModel` | Fields a caller may change; no creation/modification timestamps |
| `ProjectViewModel` | Returned fields; `Id` is marked `[Filterable]` |
| `ProjectService` | Validation and writes through `BaseDbEntityService` |
| `ProjectController` | HTTP routes through `DbEntityProtectedNhBaseController` |

## Register the module and mappings

In your service registration, import `NewHeap.Platform.AspNet.Common` and add
the repository and concrete service:

```csharp
services.AddScopedNhDbRepository<Project>();
services.AddScoped<ProjectService>();
```

In a `Profile` from `NewHeap.Platform.Mapping`, define the input and output maps.
`MapOnlyIfChanged` is an extension in `NewHeap.Platform.Common`:

```csharp
CreateMap<Project, ProjectViewModel>();
CreateMap<ProjectMutateModel, Project>().MapOnlyIfChanged();
```

Register the profile assembly through `ConfigureAutoMapper` on
`NewHeapAspNetCommonOptions.Builder(...)`, as in the
[sample startup](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Program.cs).
The [complete profile](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Utilities/AutomapperProfileConfiguration.cs)
also shows mappings for edit forms and related tasks.

## Expose a create action

Inside the project controller, delegate creation to the base class:

```csharp
[HttpPost]
[Authorize(Policy = "app.project.manage")]
[EndpointSummary("Create a project")]
[EndpointDescription("Validates and creates a project through the sample domain service.")]
[ProducesResponseType<ProjectViewModel>(StatusCodes.Status200OK)]
[ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public Task<IActionResult> Create(
    [FromBody] ProjectMutateModel mutateModel,
    CancellationToken cancellationToken = default)
{
    return DoCreate(mutateModel, cancellationToken: cancellationToken);
}
```

The base connects the action to the service and response mapping. The
[authorization guide](authentication.md) explains the policy. Use the sample's
`GetById`, `Update` and `Delete` actions for the other operations.

## Try it

[Run the sample](../../examples/SampleProjectManagement/README.md#run-the-sample),
sign in as `sample@example.test`, and create a project in the project register.
The API returns the created view model; a missing required name produces a
validation response. Inspect the request and response schemas at `/scalar` on
the API resource.

## Next steps

- [Data access](data-access.md) for queries and transaction ownership.
- [Partial updates](../consumer-guide/backend-partial-update.md) for changing selected fields.
- [Mapping reference](../consumer-guide/backend-module-composition.md) for nested maps, resolvers and conversion rules.
