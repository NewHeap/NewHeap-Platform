# Separate Common consumers from ASP.NET

`NewHeap.Platform.Common` no longer imports the `Hangfire` metapackage. It references
`Hangfire.Core` and `Hangfire.Console`, and directly declares generic hosting and
localization packages. A console or domain consumer no longer acquires
`Microsoft.AspNetCore.App`, `Hangfire.AspNetCore` or `Hangfire.SqlServer` through Common.
`NewHeap.Platform.AspNet.Common` explicitly retains Hangfire ASP.NET hosting and
SQL Server storage, so existing web composition still has those integrations.

## Migration

This is a breaking public API change intended for a breaking Platform release.
Feature work leaves package and guidance versions unchanged; the protected
Prepare release workflow owns the eventual version update.

1. Rebuild all consumers of Common. Already compiled calls to
   `TaskResult.ApplyTo(ModelStateDictionary)` and
   `TaskResult.ApplyToModelState(ModelStateDictionary, IStringLocalizer?)` no
   longer bind to instance methods on `TaskResult`.
2. In HTTP projects, reference `NewHeap.Platform.AspNet.Common` and add
   `using NewHeap.Platform.AspNet.Common;`. Ordinary calls keep their
   existing syntax, including the optional localizer argument. `WithResultErrors`
   continues to delegate to the HTTP adapter.
3. Replace overrides of the old virtual `ApplyToModelState` method with
   consumer-owned HTTP adapters and update their call sites explicitly.
   Extension methods do not provide virtual dispatch. Do not put ASP.NET types
   back into domain result subclasses to imitate the old override.
4. Consumers that previously acquired web hosting or SQL Server storage through
   Common must explicitly reference the appropriate Hangfire integration in
   their composition project. Use `Hangfire.AspNetCore` for web hosting and the
   intended storage provider package for persistence; Common itself only supplies
   the neutral Hangfire APIs and console helpers.

`TaskResult.ApplyTo(TaskResult)` and neutral result propagation are unchanged.
The moved adapter appends field and general errors without clearing existing
model-state entries or changing the source result. It preserves the existing
message formatting behavior: even when a localizer is supplied, model state
receives the original formatted message. Correcting that legacy localization
behavior is a separate observable change.

## Verification

From the repository root:

```text
dotnet test src/Back-end/Tests/NewHeap.Platform.Common.Tests/NewHeap.Platform.Common.Tests.csproj
dotnet test src/Back-end/Tests/NewHeap.Platform.AspNet.Common.Tests/NewHeap.Platform.AspNet.Common.Tests.csproj --filter FullyQualifiedName~ModelStateExtensionsTests
dotnet run --project examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.CommonConsole
dotnet test examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests/SampleProjectManagement.Core.Tests.csproj --filter FullyQualifiedName~RepositoryFoundationSamplesTests
```

SPM-216 includes the standalone executable and an automated launch from the
sample tests. Its build rejects an ASP.NET framework reference, and execution
checks both `runtimeconfig.json` and `deps.json` while exercising real Common
result propagation and queue resolution. The remaining sample API exercises
ModelState adaptation at the HTTP boundary. No schema or database-provider
behavior changes are required.
