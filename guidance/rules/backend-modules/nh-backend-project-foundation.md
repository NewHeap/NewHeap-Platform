---
id: nh-backend-project-foundation
title: "Create the shared .NET project foundation"
area: backend
reference: backend-project-foundation
summary: "Start a consumer solution with one shared MSBuild baseline and one central package version catalog so generated projects do not drift independently."
sample-cases: ["SPM-216"]
public-symbols: ["NewHeapPlatformCommonConfigurator", "NewHeapPlatformAspNetCommonConfigurator", "NhEventConfigurationBuilder", "TaskResult", "TaskResultExtensions"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: medium
---
## Preferred approach

Create `Directory.Build.props` and `Directory.Packages.props` at the nearest common ancestor of all backend projects when scaffolding a consumer repository. For the standard NewHeap repository layout, keep both beside the solution in `src/Back-end`, not at the repository root. Put the shared target framework, nullable setting, implicit usings, language version and safe backend-wide build defaults in `Directory.Build.props`. Default consumer projects to non-packable unless a specific project is intentionally published.

Set `ManagePackageVersionsCentrally` to `true` in `Directory.Packages.props` and add one `PackageVersion` for every direct NuGet dependency. Keep `PackageReference` items in project files versionless. Pin NewHeap package versions alongside the consumer's other dependencies, and keep SQL Server and PostgreSQL provider packages aligned when both are supported. Inspect the consumer's SDK and existing package requirements instead of copying the Platform library's internal version catalog wholesale.

NewHeap runtime libraries provide `net10.0` and `net11.0` assets; the source generator retains `netstandard2.0`. Use the .NET 11 SDK to build the Platform repository and install both runtimes to run its regression tests. Select framework-aligned Microsoft/System NuGet packages by `TargetFramework`: existing 10.x versions for `net10.0`, 11.0.0 for `net11.0`. Both targets temporarily retain EF Core 10, its directly dependent ASP.NET packages and matching SQL Server/PostgreSQL providers until a stable PostgreSQL provider supports EF 11. Keep independently versioned packages on their own versions; ASP.NET OpenAPI 11 requires Microsoft.OpenApi 3.x. Sample applications default to .NET 10 and accept `-p:SampleTargetFramework=net11.0` for a .NET 11 consumer build. Keep the shared C# version at 14 while both targets are supported.

## Avoid

- Adding the ASP.NET shared framework to a domain or console project merely to consume `TaskResult` or other Common primitives.
- Relying on Common to supply Hangfire ASP.NET hosting or SQL Server storage transitively.

- Generating a multi-project solution without the two central `Directory.*.props` files.
- Putting the backend solution or central props at the repository root, or putting Angular workspace files outside `src/Front-end`.
- Repeating target-framework, nullable or language settings in every project file.
- Mixing inline `PackageReference` versions with central `PackageVersion` declarations.
- Making every consumer library packable because one deliberately published project needs packaging metadata.
- Copying unrelated dependencies or private project-specific build tooling from another repository.

## Verification

Restore and build the complete solution from a clean state. Confirm every project imports the intended `Directory.Build.props`, central package management is enabled, each direct `PackageReference` resolves to exactly one `PackageVersion`, and no project file carries an inline `Version` or `VersionOverride`. Run the consumer inspector and require an empty `projectFoundation.missingRecommendedFiles` result.

Run `dotnet run --project examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.CommonConsole` from the Platform repository root. The SPM-216 console exercises result propagation and neutral queue helpers, and rejects ASP.NET framework or Hangfire ASP.NET/SQL Server dependencies in its runtime artifacts. `RepositoryFoundationSamplesTests` also launches this executable and exercises the HTTP adapter.

Run `dotnet test examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests --filter RepositoryFoundationSamplesTests` on the default target, then repeat with `-p:SampleTargetFramework=net11.0`. The executable console and HTTP adapter must work on both runtimes, and the test verifies matching NewHeap target assets, framework-aligned AI/SignalR dependency assemblies and the intentional EF Core 10 exception.

## Common and HTTP boundaries

Reference `NewHeap.Platform.Common` from domain libraries and console applications that need its result models, configuration or neutral utilities. It uses `Hangfire.Core` and `Hangfire.Console` without the Hangfire ASP.NET integration or SQL Server storage. Generic hosting and localization use explicit `Microsoft.Extensions` packages.

In the HTTP composition project, reference `NewHeap.Platform.AspNet.Common` and import `NewHeap.Platform.AspNet.Common` to call `result.ApplyToModelState(modelState)` or `result.ApplyTo(modelState)`. These are extension methods owned by the ASP.NET library; `TaskResult.ApplyTo(TaskResult)` remains in Common for propagating business failures. The ASP.NET library explicitly retains its existing Hangfire hosting and SQL Server storage dependencies.

This boundary change removes the former instance ModelState methods from `TaskResult`. Rebuild consumers, add the extension namespace to HTTP call sites, and move overrides of the former virtual `ApplyToModelState` method into consumer-owned HTTP adapters. Existing binaries must be rebuilt; extensions do not preserve binary compatibility or virtual dispatch. Schedule the change for a breaking Platform release through the protected release workflow. See `docs/how-to/migrate-common-aspnet-boundary.md` for the migration and unchanged formatting contract.
