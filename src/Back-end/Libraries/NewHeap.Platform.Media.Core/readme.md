NhMedia is a library for implementing a media library.
This core package contains the core functionality.

Media uses `TaskResult`, `TaskResult<T>` and `DisposableTaskResult<T>` from
`NewHeap.Platform.Common`. Its Common dependency is `[6.0.0,)`: version 6.0.0 or
newer, with a minimum independent of the latest Common release.

Verify the public minimum from the repository root:

```text
dotnet test src/Back-end/Tests/NewHeap.Platform.Media.Tests/NewHeap.Platform.Media.Tests.csproj -p:UseLocalNewHeapProjects=false -p:RestoreConfigFile="$PWD/release/nuget.public.config"
```

To exercise a newer public Common version, add
`-p:MediaCommonTestVersion=7.8.0`. The release contract also runs the same tests
against current Common source, including real SQL Server and PostgreSQL.
