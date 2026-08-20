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

NewHeap currently depends on the official `AutoMapper` 14.0.0 package and application code uses the `AutoMapper` namespace. The selected version is affected by GHSA-rvv3-g6hj-g44x, so NewHeap applies the documented pre-patch mitigation to every mapper configuration managed through `NewHeapAspNetCommonOptionsBuilder.ConfigureAutoMapper`: maps without an explicit limit receive `MaxDepth(64)`, while explicit consumer limits remain unchanged. Public releases must retain the focused circular-map regression tests and the time-bounded exception in `docs/security/dependency-decisions.md`; every other high or critical NuGet advisory remains release-blocking. A consumer that constructs an independent `MapperConfiguration` is outside this mitigation boundary and must apply and test its own depth guard.

## Upgrade atomically

Change the package source, declared package version, central version file, and lockfile in one reviewed change. Restore from a clean cache, build the consumer, run focused tests, and verify resolved URLs point to the public registries. Keep SQL Server/PostgreSQL provider packages aligned where the feature requires both.

For exact `.npmrc`, `nuget.config`, and AI-plugin commands, use `docs/how-to/consume-public-packages.md` when a Platform checkout is available.

## Gate an empty-repository bootstrap

Install the versioned consumer skill from the verified plugin artifact first, then run `scripts/bootstrap-newheap-consumer.mjs`. Do not continue to domain or UI generation until its anonymous NuGet restore, npm install, and `--mode foundation` inspection succeed. The bootstrap is idempotent for identical managed files, so correct package-source drift and rerun it instead of applying project-specific workarounds. Run the inspector with `--mode validate` before the final handoff.
