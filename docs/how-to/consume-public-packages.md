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

## AI plugin and consumer skill

Download `newheap-platform-<version>.tar.gz` and `SHA256SUMS` from the immutable GitHub Release tagged `newheap-platform-plugin-v<version>`, verify the archive, and install the plugin, or run `scripts/install-consumer-skills.mjs --consumer <consumer-root>` from the extracted artifact. The default Codex target writes `.agents/skills`; use `--target claude` for `.claude/skills` or `--target both` for both discovery roots. If the matching release does not exist, that plugin version is not available for stable installation. Commit the pinned NewHeap skill directories and `.newheap-platform-install.json` under every selected root. The suite is self-contained; its optional sample links target immutable public source and do not require a SampleProjectManagement checkout.

For every upgrade, verify that package versions, plugin version, and `distribution.json` compatibility metadata agree. Change registry source, declared versions, central version files, and lockfiles in one reviewed change.

## Empty repository sequence

1. Download the immutable plugin release asset and verify its checksum.
2. Install the bundled consumer skill suite into the empty repository.
3. Confirm the smallest useful product scope and summarize what remains deferred.
4. Run `bootstrap-newheap-consumer.mjs` with an application name, explicit profile, and persistence choice.
5. Require anonymous `dotnet restore`, the profile-relevant `npm install`, and `inspect-newheap-consumer.mjs --mode foundation` to pass before feature work.
6. Build only the confirmed capabilities and run the inspector with `--mode validate` before handoff.

The bootstrap accepts `--aspire`, `--docker`, and `--elasticsearch` only as explicit options. None is part of the default baseline.
