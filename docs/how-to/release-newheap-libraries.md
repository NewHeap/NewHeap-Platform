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
| `nuget-common` | Common, Common.Test, AspNet.Common, AspNet.Common.Test, and Events.Cap | `nuget-common-v1.1.3` |
| `nuget-caching` | AspNet.Caching | `nuget-caching-v0.1.3` |
| `nuget-media` | Core, SQL Server, PostgreSQL, HTTP, file system, S3, and the bundle | `nuget-media-v1.1.3` |
| `npm-platform-common` | `@newheap/platform-common` | `npm-platform-common-v0.20.6` |
| `npm-nh-toastr` | `@newheap/nh-toastr` | `npm-nh-toastr-v0.0.5` |
| `newheap-platform-plugin` | Installable AI plugin and consumer skill | `newheap-platform-plugin-v1.11.1` |

Versions remain independent. The `all` option applies the selected SemVer bump to every unit but preserves separate versions, tags, artifacts, and GitHub Releases. In an all-unit release, Common is packed first and exposed as a temporary local NuGet source for dependent Media packages.

## One-time public registry setup

1. Recreate `NewHeap/NewHeap-Platform` as a public repository and protect `main` against deletion and non-fast-forward changes while allowing the release workflow to fast-forward its validated commit.
2. Create or verify the `@newheap` npm organization/scope and the NuGet.org `NewHeap` owner. Confirm every package ID and npm name is controlled by NewHeap before the first push.
3. Bootstrap the first version of each npm package if npm requires an existing package before trusted publishing can be configured. Immediately configure the package's trusted publisher for the public repository and the calling workflow `release-contract.yml`; then revoke any bootstrap token.
4. On nuget.org, add a trusted publishing policy for the NewHeap owner, repository, and calling workflow `release-contract.yml`. Set repository variable `NUGET_USER` to the NuGet.org profile name, not an email address.
5. For optional preview publication, create a separate nuget.org policy for `publish-preview.yml`, create the protected `public-package-preview` environment, and bind the policy to that environment.
6. Give the relevant jobs `id-token: write`. Keep external Actions pinned to full commit SHAs and enable GitHub Release immutability.

Trusted publishing validates the calling workflow for reusable-workflow chains. Perform a one-package smoke release before selecting `all`; if the registry does not accept the nested reusable-workflow identity, stop and adjust the policy/workflow boundary without falling back to a permanent token.

## Preview packages

Run **Publish preview packages**. Leave `publish` disabled to create a 14-day workflow artifact first. When enabled, the protected job publishes an immutable `<version>-ci.<run-number>.<short-sha>` prerelease to nuget.org through OIDC and verifies it anonymously. Preview publishing is optional because public prerelease versions remain part of the registry record.

## Stable release

1. Run **Prepare release** on `main`; select one unit or `all` and a `patch`, `minor`, or `major` bump.
2. The workflow validates release tooling, guidance, executable samples, change impact, and the public API snapshot. It creates one generated release commit.
3. The release contract fast-forwards unchanged `main` to that exact commit and invokes the publisher for the validated SHA.
4. The publisher packs immutable artifacts, obtains short-lived registry credentials through OIDC, pushes the selected packages, and verifies the exact public versions anonymously with bounded retries.
5. Finalization publishes only complete GitHub Release drafts containing package artifacts and `SHA256SUMS`.

If registry publication succeeds but finalization is interrupted, run **Finalize pending release** for the current unit or `all`. Recovery never bumps or republishes; it verifies the current public versions, commit, artifacts, and checksums before publishing drafts.

## First public release order

1. Verify all names are available or already owned and complete the trusted-publisher setup.
2. Publish `nuget-common` first.
3. Publish `nuget-caching` and `nuget-media`; Media restores the just-published Common version or the locally packed Common artifact in an all-unit release.
4. Publish both npm units and verify their repository metadata and provenance.
5. Publish the AI plugin and test a clean consumer bootstrap using only public registries.
6. Revoke migration/bootstrap tokens and remove any remaining private-feed configuration.

## Local verification

```text
npm run release:test
node tools/release/prepare-release.mjs --component nuget-common --bump patch --dry-run
node tools/release/prepare-release.mjs --component all --bump patch --dry-run
node tools/release/package-release.mjs --component nuget-common --dry-run
node tools/release/package-release.mjs --component npm-platform-common --dry-run
```

Local commands pack and validate only. Registry publication belongs to the protected GitHub workflow.
