# Consume public NewHeap packages

NewHeap NuGet packages are distributed through nuget.org and scoped npm packages through npmjs.org. Consumer restores are anonymous: do not add a GitHub Packages source, a personal access token, or an npm auth block for `@newheap`.

## npm

The default npm registry is sufficient. A repository may make the scope mapping explicit without credentials:

```text
registry=https://registry.npmjs.org/
@newheap:registry=https://registry.npmjs.org/
```

Install the required package normally:

```text
npm install @newheap/platform-common
npm install @newheap/nh-toastr
```

AI Chat 0.33 and later require Common 6.1 or later within major 6 for the `@newheap/platform-common/select` entry point.

Verify resolved package URLs in `package-lock.json` point to `registry.npmjs.org`. A `401` or `403` for a NewHeap package indicates stale private-registry configuration in the repository, user-level npm configuration, or CI environment; remove that override rather than adding a token.

## NuGet

Use nuget.org as the only committed package source unless the consumer has an unrelated, explicitly approved feed:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Run restore from an empty local package cache when validating a source cutover. No NuGet API key is required for restore. API keys and OIDC credentials belong only to the protected Platform publication workflow.

### Minimum publish age

With the .NET 11 SDK (11.0.100 or later, NuGet 8.0), add a minimum publish age to the nuget.org source and exempt NewHeap's own packages, as the Platform's `src/Back-end/nuget.config` does:

```xml
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" minPublishAgeHours="72" />
```

```xml
<minPublishAgeExceptions>
  <package pattern="NewHeap.*" />
</minPublishAgeExceptions>
```

The age delays only the versions that NuGet chooses for you: `dotnet package add` and `dotnet package update` without an explicit version skip versions published less than 72 hours ago, and restore fails with `NU1020` for a floating version of a package that is not exempt. Pinned versions in `Directory.Packages.props` restore unchanged, and an explicitly requested version bypasses the age. The exception keeps a NewHeap release available on the day it is published. The nearest configuration file that defines `minPublishAgeExceptions` replaces the inherited list, so keep `NewHeap.*` in every such file, and repeat `minPublishAgeHours` in a file that redefines the nuget.org source. Older SDKs, such as 10.0.4xx, ignore both settings. Configure Dependabot or Renovate cooldowns separately. See [Minimum package publish age](https://learn.microsoft.com/nuget/consume-packages/minimum-package-publish-age).

## AI plugin and consumer skill

Download `newheap-platform-<version>.tar.gz` and `SHA256SUMS` from the immutable GitHub Release tagged `newheap-platform-plugin-v<version>`, verify the archive, and install the plugin, or run `scripts/install-consumer-skills.mjs --consumer <consumer-root>` from the extracted artifact. The default Codex target writes one `.agents/skills/newheap-platform-development` directory; use `--target claude` for the matching `.claude/skills/newheap-platform-development` directory or `--target both` for both discovery roots. If the matching release does not exist, that plugin version is not available for stable installation. Commit each pinned `newheap-platform-development` directory, including its `.newheap-platform-install.json`. The suite is self-contained; its optional sample links target immutable public source and do not require a SampleProjectManagement checkout.

For every upgrade, verify that package versions, plugin version, and `distribution.json` compatibility metadata agree. Change registry source, declared versions, central version files, and lockfiles in one reviewed change.

## Upgrade across several versions

Act on every breaking change between the installed and the new version, not only on those of the new one. In a Platform checkout, list them per release unit with `npm run release:breaking -- --unit nuget-common --from 8.0.0 --to 32.0.0`, and again for each npm unit with its own versions, such as `--unit npm-platform-common`. The command prints every `Breaking change | Required action` row of the release notes after `--from` up to `--to`, grouped by version and package; see [Breaking changes between two versions](release-newheap-libraries.md#breaking-changes-between-two-versions).

## Empty repository sequence

1. Download the immutable plugin release asset and verify its checksum.
2. Install the bundled consumer skill suite into the empty repository.
3. Confirm the smallest useful product scope and summarize what remains deferred.
4. Run `bootstrap-newheap-consumer.mjs` with an application name, explicit profile, and persistence choice.
5. Require anonymous `dotnet restore`, the profile-relevant `npm install`, and `inspect-newheap-consumer.mjs --mode foundation` to pass before feature work.
6. Build only the confirmed capabilities and run the inspector with `--mode validate` before handoff.

The bootstrap accepts `--aspire`, `--docker`, and `--elasticsearch` only as explicit options. None is part of the default baseline.
