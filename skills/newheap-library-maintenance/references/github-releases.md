# GitHub release workflow

Read this reference for preview packages, stable releases, version bumps, public npm/NuGet publication, GitHub Releases, or feed migration.

## Use the release manifest

`release/manifest.json` is the source for release units, package membership, stable versions and tag prefixes. Do not restore the deleted `publish-common.ps1`, `publish-caching.ps1` or `publish-media.ps1` scripts and do not publish directly from a maintainer workstation.

- Preview: run `Publish preview packages`; a manual dispatch from another branch is a routing run that re-dispatches the same inputs on `main`. The main run derives `<main-version>-ci.<run>.<sha>` without editing source versions.
- Stable: run `Prepare release` from `main` and select one unit or `all`, plus the SemVer bump. This is the only normal manual action. The workflow validates the existing public API snapshot, updates versions, regenerates guidance and refreshes the snapshot for the expected guidance-version change. It then creates one generated `release/*` commit, invokes the reusable release contract, fast-forwards unchanged `main` to the validated commit, and publishes that exact SHA. Registry publication and GitHub Release finalization are separate jobs; finalization retries delayed public-registry indexing and requires every exact package version to be anonymously readable before publishing complete drafts. If `main` advances before the update, start Prepare release again instead of rebasing the generated branch.
- Package and validate locally only with `npm run release:test` and `node tools/release/package-release.mjs ... --dry-run`.

Keep npm common, npm toastr, NuGet common, caching, media and the AI-plugin independently versioned. A release commit updates the owning package metadata; `nuget-common` also updates the internal Common dependency, and a plugin release keeps guidance and plugin versions equal.

Use the `all` selection only when every unit should receive the same SemVer bump type in one automated release commit. It remains a batch, not a shared platform version: publishing creates and verifies a separate tag, artifact set and immutable GitHub Release for each unit. The all-unit workflow packs Common first and exposes those local artifacts as a temporary NuGet source before packing dependent media packages.

Package artifacts always use `main` as their only long-lived source branch. A preview routing run may only re-dispatch the current main ref. A stable publisher is reusable-only: it accepts the validated SHA produced by the release contract, checks out that exact commit and verifies that it is contained in `main` before packing. `staging` and `production` may remain application-deployment branches but must never be used to publish NewHeap packages.

`Finalize pending release` is an exceptional recovery action for package versions that were already pushed by a previous run while their complete GitHub Releases remained drafts. It derives the current manifest tags, requires one immutable release commit contained in `main`, verifies artifacts plus `SHA256SUMS`, waits for every exact public registry version and publishes only the drafts. It does not bump or republish anything and does not select older draft versions.

All NewHeap registry packages are public. npm packages target `https://registry.npmjs.org/` with public access; NuGet packages target `https://api.nuget.org/v3/index.json`. Consumer restores are anonymous and must not require GitHub Packages, Azure Artifacts, or a token. Publication uses OIDC trusted publishing with short-lived credentials, and the workflow verifies every exact version anonymously before finalizing a GitHub Release.

## Protect publication

Use `public-package-preview` when public prereleases need a separate environment and restrict it to `main`. Give publication jobs only the `id-token: write` and GitHub content permissions they require. Configure npm and nuget.org trusted-publisher policies for the calling workflow identity, create a draft GitHub Release, attach artifacts and checksums, publish registry packages, then publish the immutable release. Never reuse a package version or move a release tag to different bits.

Keep `main` protected against deletion and non-fast-forward changes while allowing GitHub Actions to perform a normal fast-forward. The release contract confirms that the generated commit has exactly the recorded main parent, runs all validations and verifies that main is unchanged immediately before updating the ref. Never query `gh pr checks` or depend on GraphQL check access.

For the initial public release, verify package ownership and trusted-publisher policies, publish Common first, then caching/media, then npm packages and the plugin. Follow `docs/how-to/release-newheap-libraries.md` for repository settings, bootstrap order, and recovery.
