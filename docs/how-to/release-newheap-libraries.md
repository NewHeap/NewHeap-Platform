# Release NewHeap libraries publicly

The release pipeline publishes NuGet packages to nuget.org, scoped npm packages to npmjs.org, and immutable archives to GitHub Releases. Release units, versions, package membership, and tag prefixes are maintained in `release/manifest.json`. Never publish from a maintainer workstation.

## Public distribution contract

- Consumer restore is anonymous through `https://api.nuget.org/v3/index.json` and `https://registry.npmjs.org/`.
- Publication uses GitHub Actions OIDC trusted publishing. Do not create long-lived npm or NuGet publication tokens.
- Scoped npm packages publish with `--access public`; NuGet packages push to the v3 nuget.org endpoint.
- The workflow creates a draft GitHub Release, uploads artifacts and `SHA256SUMS`, publishes registry packages, verifies every exact version anonymously, and only then publishes the GitHub Release.
- Package versions and release tags are immutable and never reused for different bits.

## Release units

| Unit | Contents | Example tag |
|---|---|---|
| `nuget-common` | Mapping, Common and ASP.NET foundations, AI packages and test helpers, Events.Cap, and DatabaseRead.Tool | `nuget-common-v<version>` |
| `nuget-caching` | AspNet.Caching | `nuget-caching-v<version>` |
| `nuget-media` | Core, SQL Server, PostgreSQL, HTTP, file system, S3, and the bundle | `nuget-media-v<version>` |
| `npm-platform-common` | `@newheap/platform-common` | `npm-platform-common-v<version>` |
| `npm-nh-toastr` | `@newheap/nh-toastr` | `npm-nh-toastr-v<version>` |
| `newheap-platform-plugin` | Installable NewHeap Platform plugin and consumer skill suite | `newheap-platform-plugin-v<version>` |

Versions remain independent. The `all` option applies the selected SemVer bump to every unit but preserves separate versions, tags, artifacts, and GitHub Releases. In an all-unit release, Common is packed first and exposed as a temporary local NuGet source for dependent Media packages.

## Established release configuration

The public repository, `@newheap` npm scope, NuGet.org `NewHeap` owner, branch protection, immutable GitHub Releases, and trusted-publisher identities are already established. They are release infrastructure, not steps to repeat before an ordinary release.

- npm trusted publishers are configured per package for `NewHeap/NewHeap-Platform` and the top-level calling workflow `prepare-release.yml`. Existing packages should disallow traditional publication tokens after OIDC has been verified.
- The NuGet.org owner policy trusts `NewHeap/NewHeap-Platform` and `publish-release.yml`; repository variable `NUGET_USER` contains the NuGet.org profile name, not an email address.
- Optional public previews use the separate `publish-preview.yml` policy and protected `public-package-preview` environment.
- Publication jobs require `id-token: write`; external Actions remain pinned to full commit SHAs.

Treat changes to these identities, policies, or protections as security-sensitive administration. If authentication fails, stop and repair the workflow-policy match. Never bypass it with a permanent registry token.

## Add a new package name

This section applies only when `release/manifest.json` gains a NuGet ID or npm name that has never been published. It does not apply to a new version of an existing package.

1. Verify the exact public name is available or already controlled by NewHeap before merging the manifest change. Keep package metadata, repository URLs, licensing, and release-unit membership reviewable as normal source changes.
2. Confirm the established owner or trusted-publisher policy covers the new target. npm trusted publishing is package-specific, while the NuGet policy is owned by the `NewHeap` account.
3. A brand-new npm package may need one exceptional bootstrap publication because its package settings do not exist yet. If so, use a reviewed, temporary main-only workflow and a short-lived granular token scoped to that single public package. Package and verify it through the normal tooling, then immediately configure its trusted publisher for `prepare-release.yml`, disallow traditional publication tokens, delete the secret and workflow, and revoke the token.
4. Release the owning unit with **Prepare release** and an appropriate SemVer bump. Adding a new package name does not by itself require a major bump: use patch or minor during rapid development when the existing packages remain compatible, and reserve major for an actual breaking contract. Never add new package membership to a unit version that has already been published, and never use `all` merely to test a new registry identity.

No bootstrap credential or temporary publication workflow may remain in the established repository.

## Preview packages

Run **Publish preview packages**. Leave `publish` disabled to create a 14-day workflow artifact first. When enabled, the protected job publishes an immutable `<version>-ci.<run-number>.<short-sha>` prerelease to nuget.org through OIDC and verifies it anonymously. Preview publishing is optional because public prerelease versions remain part of the registry record.

## Stable release

Do not edit unit version fields in `release/manifest.json`, `guidance/version.json` or the plugin manifest in a feature branch. They stay on the last published version while guidance generation updates only content hashes, package compatibility metadata and content that actually changed. Structural release-manifest changes remain reviewable as ordinary feature work. The generated consumer references route through one release-pinned immutable-evidence catalog instead of embedding the version in every file.

1. Run **Prepare release** on `main`; select one unit or `all` and a `patch`, `minor`, or `major` bump. This selection is the only normal manual release action.
2. The workflow validates release tooling, guidance, executable samples, change impact, and the public API snapshot. It computes and writes every coupled version, refreshes the immutable-evidence catalog, and creates one generated release commit.
3. The release contract fast-forwards unchanged `main` to that exact commit and invokes the publisher for the validated SHA.
4. The publisher packs immutable artifacts, obtains short-lived registry credentials through OIDC, pushes the selected packages, and verifies the exact public versions anonymously with bounded retries.
5. Finalization publishes only complete GitHub Release drafts containing package artifacts and `SHA256SUMS`.
6. After a successful individual NuGet or npm release, the top-level workflow queues a separate plugin patch release so its immutable artifact records the new `compatiblePackages`. A plugin release never queues itself, and `all` already includes the plugin.

If registry publication succeeds but finalization is interrupted, run **Finalize pending release** for the current unit or `all`. Recovery never bumps or republishes; it verifies the current public versions, commit, artifacts, and checksums before publishing drafts.

## Local verification

```text
npm run release:test
node tools/release/prepare-release.mjs --component nuget-common --bump patch --dry-run
node tools/release/prepare-release.mjs --component all --bump patch --dry-run
node tools/release/package-release.mjs --component nuget-common --dry-run
node tools/release/package-release.mjs --component npm-platform-common --dry-run
```

Local commands pack and validate only. Registry publication belongs to the protected GitHub workflow.
