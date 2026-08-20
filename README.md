# NewHeap Platform

NewHeap Platform is a set of reusable .NET and Angular libraries for building
enterprise applications. The repository includes the libraries, focused tests,
versioned consumer guidance, release tooling, and an executable reference
application in `examples/SampleProjectManagement`.

## Repository layout

- `src/Back-end/Libraries`: reusable .NET libraries and test-support packages.
- `src/Back-end/Tests`: non-packable NewHeap regression tests.
- `src/Front-end/projects`: reusable Angular packages.
- `examples/SampleProjectManagement`: executable backend and frontend examples.
- `guidance` and `skills`: generated, versioned consumer guidance backed by the sample.
- `release` and `tools/release`: release definitions and local validation tooling.

## Getting started

Install .NET 10, Node.js 22 or later, and npm. NewHeap package configuration
uses anonymous public restore sources: nuget.org and npmjs.org. The first public
versions are published only through the protected release workflow.

Run the core backend tests from the repository root:

```text
dotnet test src/Back-end/NewHeap.Platform.sln
dotnet test examples/SampleProjectManagement/src/Back-end/SampleProjectManagement.slnx
```

Validate the executable sample and generated guidance with the commands in the
repository `AGENTS.md` verification checklist.

## AI-assisted changes

Repository-wide coding-agent instructions are defined in [AGENTS.md](AGENTS.md).
Codex and Claude entrypoints are available in [CODEX.md](CODEX.md) and
[CLAUDE.md](CLAUDE.md); both point to the same authoritative policy.

The [NewHeap consumer guide](docs/consumer-guide/index.md) and executable
[SampleProjectManagement catalog](examples/SampleProjectManagement/docs/sample-catalog.md)
share one canonical case registry. Reusable agent skills live under `skills`:

- the focused consumer suite in [the skill manifest](skills/skill-manifest.json)
  for foundation, backend, frontend, authentication, databases, media,
  background processing, runtime configuration and testing;
- [library maintenance](skills/newheap-library-maintenance/SKILL.md) for keeping
  libraries, samples, guidance, and skills synchronized.

Run `npm run guidance:generate` after changing the case registry or atomic rules,
and `npm run skills:validate` before committing guidance or public library work.
To pin the supported consumer workflow into another repository for Codex, run:

```text
node tools/guidance/install-consumer-skills.mjs --consumer <consumer-root>
```

The default target is `codex` and writes `.agents/skills`. Use `--target
claude` for `.claude/skills`, or `--target both` for a mixed-agent repository.
Commit the generated `newheap-*` directories and
`.newheap-platform-install.json` under every selected skill root. The same
provider-neutral skill suite is packaged in the
[NewHeap Platform plugin](plugins/newheap-platform/.codex-plugin/plugin.json).

## Releases

Library versions and package groups are defined in
[release/manifest.json](release/manifest.json). The release workflows prepare
reviewed SemVer changes, package exact commits, and create checksummed release
artifacts. Do not publish packages directly from a maintainer workstation.

NuGet packages target nuget.org and scoped npm packages target npmjs.org. The
protected workflow uses OIDC trusted publishing, verifies every exact package
version anonymously, and finalizes checksummed GitHub Releases only afterward.
See [the public release guide](docs/how-to/release-newheap-libraries.md). Do not
publish until the one-time registry ownership and trusted-publisher setup is
approved and complete.

## Contributing and support

Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing a change. Community
support is described in [SUPPORT.md](SUPPORT.md); use [SECURITY.md](SECURITY.md)
for private vulnerability reporting.

## License

Unless otherwise noted, NewHeap-authored software in this repository is
licensed under the [Apache License 2.0](LICENSE). See [NOTICE](NOTICE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for required attributions.

The license does not grant rights to the NewHeap name or logos. See
[TRADEMARKS.md](TRADEMARKS.md).
