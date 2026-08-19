# Repository structure

Resolve all paths from the SampleProjectManagement root.

| Concern | Owning path |
|---|---|
| ASP.NET controllers and composition | `src/Back-end/Applications/SampleProjectManagement.Api` |
| Domain models, services and mappings | `src/Back-end/Libraries/SampleProjectManagement.Core` |
| Entities, DbContext and consumer migrations | `src/Back-end/Libraries/SampleProjectManagement.DAL` |
| Aspire and service defaults | `src/Back-end/Orchestration` |
| .NET behavior and contract tests | `src/Back-end/Tests` |
| Angular applications and shared consumer code | `src/Front-end` |
| Canonical and generated sample documentation | `docs` |
| Agent workflows | `skills` |
| Structural validation | `tools` |

The solution, `Directory.Build.props` and `Directory.Packages.props` live in
`src/Back-end`. Put shared target-framework and compiler defaults in
`Directory.Build.props`. Enable central package management in
`Directory.Packages.props`, declare every direct package version there and keep
individual `PackageReference` items versionless. Keep the Angular workspace
entry points in `src/Front-end`, with applications and shared libraries under
`src/Front-end/projects`. The NewHeap libraries are referenced from the
containing Platform repository and are not copied into this sample.

Do not add executable source to the sample root. Do not recreate the former
top-level `Applications`, `Libraries`, `Orchestration`, `Tests` or `Front-end`
directories. Run `npm run validate:structure` after any move or project addition.
