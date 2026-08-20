# Install NewHeap Platform guidance

The plugin contains one provider-neutral `newheap-platform-development` skill with focused internal modules for foundation setup, backend, frontend, authentication, databases, media, background processing, runtime configuration and testing. Its root `SKILL.md` only routes the task and loads the smallest relevant module; the domain concerns remain separated. It does not contain the Platform maintainer workflow.

Download `newheap-platform-<version>.tar.gz` and `SHA256SUMS` from the matching immutable GitHub Release tagged `newheap-platform-plugin-v<version>`. Verify the archive against the checksum file before extracting it. If that release does not exist, the plugin version is not available for stable consumer installation; do not substitute an unversioned workflow artifact.

For a repository-pinned installation, run from this plugin directory:

```text
node scripts/install-consumer-skills.mjs --consumer <consumer-root>
```

The default target is `codex` and installs everything into `<consumer-root>/.agents/skills/newheap-platform-development`. Use `--target claude` for `<consumer-root>/.claude/skills/newheap-platform-development`, or `--target both` for a repository used by both agents:

```text
node scripts/install-consumer-skills.mjs --consumer <consumer-root> --target claude
node scripts/install-consumer-skills.mjs --consumer <consumer-root> --target both
```

Commit the single managed `newheap-platform-development` directory, including its `.newheap-platform-install.json`, under every selected discovery root. Verify it after updating the plugin with the same target:

```text
node scripts/install-consumer-skills.mjs --consumer <consumer-root> --target <codex|claude|both> --check
```

The installer safely migrates the earlier flat `newheap-*` directories into the grouped layout after verifying their recorded hashes. It refuses to overwrite locally changed installed files. Review those changes first, or pass `--force` only when replacement of the NewHeap-managed suite is intentional. Other skills in `.agents/skills` and `.claude/skills` are never removed.

The shipped rules are self-contained. Their optional sample links point to the immutable public source at the matching plugin release and are only for resolving an unclear API-composition detail; the SampleProjectManagement source tree is not required in the consumer repository.

The plugin follows the guidance version in `guidance/version.json`. Compatible library-package versions are recorded separately in `distribution.json`.

## Bootstrap an empty consumer repository

After extracting and verifying the versioned plugin artifact, install its skill
into the empty Git repository and run the pinned bootstrapper. This example
creates a management portal backed by PostgreSQL; select another explicit
profile and persistence choice when that better matches the confirmed scope:

```text
node <extracted-plugin>/scripts/install-consumer-skills.mjs --consumer <consumer-root>
node <consumer-root>/.agents/skills/newheap-platform-development/skills/foundation/scripts/bootstrap-newheap-consumer.mjs <consumer-root> --name Example.Portal --profile management-portal --database postgresql
```

For a Claude-only installation, pass `--target claude` and run the bootstrapper from `.claude/skills/newheap-platform-development/skills/foundation`. A combined installation creates or preserves both `AGENTS.md` and `CLAUDE.md`; existing repository instructions are never overwritten.

The bootstrap creates the solution and central props in `src/Back-end`. With
the `management-portal` profile shown above, it also creates the Angular
workspace in `src/Front-end` with applications under `projects`. It restores
compatible NewHeap packages anonymously from
`https://api.nuget.org/v3/index.json` and
`https://registry.npmjs.org/`. When restore or install fails, remove stale
private-feed overrides or fix public registry connectivity, then rerun the
idempotent command before any feature scaffolding. Do not add consumer package
credentials for NewHeap.

Use `--aspire`, `--docker` and `--elasticsearch` only when those optional
capabilities are requested. After the agent completes the management portal,
require this audit:

```text
node <consumer-root>/.agents/skills/newheap-platform-development/skills/foundation/scripts/inspect-newheap-consumer.mjs <consumer-root> --mode validate
```
