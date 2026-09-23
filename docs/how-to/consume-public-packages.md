# Install NewHeap packages

Install .NET packages from nuget.org and Angular packages from npmjs.org.
Both registries support anonymous installation; no NewHeap login or token is
needed. Choose a released version compatible with your application.

For a first Angular + API implementation, follow
[Step 0: install and configure the platform](../guides/platform-setup.md), then
build the lister and add CRUD. This page covers package installation only.

## NuGet

From the target .NET 10 project directory, install the package you need. Replace
`<version>` with the release version you have selected:

```sh
dotnet add package NewHeap.Platform.AspNet.Common --version <version>
dotnet restore
```

For domain libraries that only need common utilities, use
`NewHeap.Platform.Common`. Add the matching `.PostgreSql` or `.SqlServer`
package in your API project when you need database integration. The
[.NET setup guide](../guides/dotnet-foundation.md) explains project layout and
central package versions.

## npm

From an Angular 20 workspace, install the common package:

```sh
npm install @newheap/platform-common
```

Commit the lockfile to keep the resolved versions reproducible. Continue with
[root configuration and an API call](../guides/collections.md). Add
`@newheap/nh-toastr` separately if your application uses the toast integration.

## Resolve registry problems

If a repository still points NewHeap packages at a private feed, remove that
override from its configuration, user settings and CI. A clean public-only
`nuget.config` can use:

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

Keep additional sources if unrelated dependencies require them, with appropriate
source mappings. For npm, the default registry is sufficient, or make the scope
explicit in `.npmrc`:

```text
registry=https://registry.npmjs.org/
@newheap:registry=https://registry.npmjs.org/
```

NewHeap entries in `package-lock.json` should resolve from `registry.npmjs.org`.
Investigate stale registry configuration when public restores return `401` or
`403`; adding a private token is not part of installation.

## Optional coding-agent setup

Download `newheap-platform-<version>.tar.gz` and `SHA256SUMS` from the GitHub Release
tagged `newheap-platform-plugin-v<version>`. Verify the checksum and extract the
archive, then run from that directory:

```sh
node scripts/install-consumer-skills.mjs --consumer <consumer-root>
```

This installs a self-contained Codex skill under
`.agents/skills/newheap-platform-development`. Add `--target claude` for the
equivalent `.claude/skills` directory, or `--target both` for both tools. Commit
the installed directory, including `.newheap-platform-install.json`.

Use an existing release and check its `distribution.json` compatibility metadata
against your package versions. Upgrade the skill, declared versions and lockfiles
together.

### Start an empty repository with the installed skill

After installation, choose the application name, profile and database. Follow
the [bootstrap guide](../consumer-guide/consumer-bootstrap-sequence.md) to generate
the foundation and verify package restore before adding features. Aspire, Docker
and Elasticsearch are optional selections.

## For maintainers

Validate a feed migration with an empty package cache. Publishing credentials
belong to the protected [release workflow](release-newheap-libraries.md).
