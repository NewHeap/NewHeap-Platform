# Public package sources

Use this reference when installing or upgrading NewHeap npm/NuGet packages or the distributed AI skill.

## Inspect before changing

1. Read installed `@newheap/*` and `NewHeap.*` versions and the pinned skill `distribution.json` when present.
2. Confirm the requested versions exist anonymously on npmjs.org or nuget.org before changing a lockfile.
3. Remove stale Azure Artifacts and GitHub Packages overrides; do not create a permanent mixed-source setup.

## Restore anonymously

- npm: use `https://registry.npmjs.org/` as the default registry and, when an explicit mapping is desired, map `@newheap` to the same public registry.
- NuGet: use `https://api.nuget.org/v3/index.json` and allow `NewHeap.*` to resolve from nuget.org.
- Do not add a PAT, `NODE_AUTH_TOKEN`, NuGet password, GitHub Packages source, or `always-auth` for NewHeap packages.
- Registry publication credentials belong only to the protected Platform release workflow; consumer builds never publish NewHeap packages.

A `401` or `403` during consumer restore is configuration drift, not a request for a new credential. Inspect repository, user-level, and CI package configuration for old `npm.pkg.github.com`, `nuget.pkg.github.com`, or Azure mappings and remove the narrow stale override. Do not print or collect unrelated user-level credentials while diagnosing the source chain.

NewHeap mapping consumers reference `NewHeap.Platform.Mapping` and import the `NewHeap.Platform.Mapping` namespace. The package intentionally exposes the familiar profile, `CreateMap`, `ForMember`, `MapFrom`, `MaxDepth`, `IMapper`, and `AddAutoMapper` surface used by NewHeap applications without depending on the AutoMapper package. Every map receives a default `MaxDepth(64)` guard, including maps created by an independent `MapperConfiguration`; explicit consumer limits remain authoritative. Treat APIs outside this focused surface as unsupported and verify nested-object, collection, null, and existing-destination behavior when migrating a consumer.

## Delay new third-party versions, not NewHeap releases

With the .NET 11 SDK (11.0.100 or later, NuGet 8.0), recommend a minimum publish age for nuget.org in the committed `src/Back-end/nuget.config`, and exempt NewHeap's own packages so a coordinated NewHeap upgrade can adopt a release on the day it is published:

```xml
<packageSources>
  <clear />
  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" minPublishAgeHours="72" />
</packageSources>
<minPublishAgeExceptions>
  <package pattern="NewHeap.*" />
</minPublishAgeExceptions>
```

- `minPublishAgeHours` filters automatic version choices only: `dotnet package add` and `dotnet package update` without an explicit version skip versions younger than the age, and restore fails with `NU1020` for a floating version such as `1.2.*` whose package is not exempt. Restore of the pinned versions in `Directory.Packages.props`, including transitive dependencies, is not age-checked, and an explicitly requested version bypasses the delay. It is a cooling-off period, not a safety guarantee; keep NuGet Audit on.
- `minPublishAgeExceptions` uses the package source mapping pattern syntax and applies to every source. The nearest configuration file that defines the section replaces the whole inherited list, so every repository or CI file that defines it keeps `NewHeap.*`. A file that redefines the `nuget.org` source must repeat `minPublishAgeHours`; omitting it resets the age to zero.
- `dotnet package update --vulnerable` honors the age as well. Request a reviewed security fix by its explicit version instead of removing the age or adding a broad exception.
- Older SDKs, such as 10.0.4xx, ignore the attribute and the section, so the same file restores unchanged before the upgrade. Dependabot, Renovate and similar tools have their own cooldown settings; configure them separately with the same `NewHeap.*` exemption.
- The bootstrap does not write these settings. Add them as a reviewed change after the foundation gate, keeping the source and mapping it generated.

## Upgrade atomically

Change the package source, declared package version, central version file, and lockfile in one reviewed change. Restore from a clean cache, build the consumer, run focused tests, and verify resolved URLs point to the public registries. Keep SQL Server/PostgreSQL provider packages aligned where the feature requires both.

Across several versions, act on every breaking change in between, not only on those of the target version. With a Platform checkout, list them per release unit, for example `npm run release:breaking -- --unit nuget-common --from 8.0.0 --to 32.0.0` and the same for each npm unit with its own versions; otherwise read every `Breaking change | Required action` table of the release notes `v<version>.md` after the installed version, for the sections that name the installed packages.

For exact `.npmrc`, `nuget.config`, and AI-plugin commands, use `docs/how-to/consume-public-packages.md` when a Platform checkout is available.

## Gate an empty-repository bootstrap

Install the versioned consumer skill from the verified plugin artifact first, then run `scripts/bootstrap-newheap-consumer.mjs`. Do not continue to domain or UI generation until its anonymous NuGet restore, npm install, and `--mode foundation` inspection succeed. The bootstrap is idempotent for identical managed files, so correct package-source drift and rerun it instead of applying project-specific workarounds. Run the inspector with `--mode validate` before the final handoff.
