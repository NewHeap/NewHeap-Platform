---
id: nh-backend-project-foundation
title: "Create the shared .NET project foundation"
area: backend
reference: backend-modules
summary: "Start a consumer solution with one shared MSBuild baseline and one central package version catalog so generated projects do not drift independently."
sample-cases: ["SPM-216"]
public-symbols: ["NewHeapPlatformCommonConfigurator", "NewHeapPlatformAspNetCommonConfigurator", "NhEventConfigurationBuilder"]
skills: ["newheap-consumer-development"]
providers: ["provider-neutral"]
risk: medium
---
## Preferred approach

Create `Directory.Build.props` and `Directory.Packages.props` at the nearest common ancestor of all backend projects when scaffolding a consumer repository. For the standard NewHeap repository layout, keep both beside the solution in `src/Back-end`, not at the repository root. Put the shared target framework, nullable setting, implicit usings, language version and safe backend-wide build defaults in `Directory.Build.props`. Default consumer projects to non-packable unless a specific project is intentionally published.

Set `ManagePackageVersionsCentrally` to `true` in `Directory.Packages.props` and add one `PackageVersion` for every direct NuGet dependency. Keep `PackageReference` items in project files versionless. Pin NewHeap package versions alongside the consumer's other dependencies, and keep SQL Server and PostgreSQL provider packages aligned when both are supported. Inspect the consumer's SDK and existing package requirements instead of copying the Platform library's internal version catalog wholesale.

## Avoid

- Generating a multi-project solution without the two central `Directory.*.props` files.
- Putting the backend solution or central props at the repository root, or putting Angular workspace files outside `src/Front-end`.
- Repeating target-framework, nullable or language settings in every project file.
- Mixing inline `PackageReference` versions with central `PackageVersion` declarations.
- Making every consumer library packable because one deliberately published project needs packaging metadata.
- Copying unrelated dependencies or private project-specific build tooling from another repository.

## Verification

Restore and build the complete solution from a clean state. Confirm every project imports the intended `Directory.Build.props`, central package management is enabled, each direct `PackageReference` resolves to exactly one `PackageVersion`, and no project file carries an inline `Version` or `VersionOverride`. Run the consumer inspector and require an empty `projectFoundation.missingRecommendedFiles` result.
