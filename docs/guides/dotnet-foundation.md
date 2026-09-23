# Set up a .NET application

Use NewHeap packages in your own .NET 10 solution. Start with the packages your
application needs; a backend service does not require Angular, Docker or Aspire.

## Choose the packages

| Application needs | Package |
| --- | --- |
| Result models, configuration and common utilities | `NewHeap.Platform.Common` |
| Controllers, repositories and authentication | `NewHeap.Platform.AspNet.Common` |
| SQL Server integration | `NewHeap.Platform.AspNet.Common.SqlServer` |
| PostgreSQL integration | `NewHeap.Platform.AspNet.Common.PostgreSql` |
| Object mapping | `NewHeap.Platform.Mapping` |

Use [NuGet installation](../how-to/consume-public-packages.md#nuget) to add the
packages. Keep the database provider in the API/composition project; put your
entities and DbContext in your application's DAL project.

## Share the build settings

For a solution with multiple projects, place `Directory.Build.props` next to the
solution in `src/Back-end`. The child projects inherit these settings:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

Keep package versions in `Directory.Packages.props` in the same directory, with
`ManagePackageVersionsCentrally` enabled and a `PackageVersion` for each direct
dependency. Project files then use versionless references:

```xml
<ItemGroup>
  <PackageReference Include="NewHeap.Platform.Common" />
</ItemGroup>
```

Use versions from the release you are adopting. The sample's
[central package file](../../examples/SampleProjectManagement/src/Back-end/Directory.Packages.props)
shows the complete structure; its repository development dependencies are not a
package list to copy into every application.

Run `dotnet build` from the solution directory. All projects should build with
the shared framework and package versions.

## Build the first feature

Continue with [an API module](backend-modules.md), [database access](data-access.md)
or [configuration](configuration.md). The executable sample separates
[domain services](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/Services/ProjectService.cs),
[entities and DbContext](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.DAL/SampleProjectManagementDbContext.cs)
and [API startup](../../examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/Program.cs).

## Further reading

The [foundation reference](../consumer-guide/backend-project-foundation.md)
covers the Common/ASP.NET boundary and links to its executable checks.
