# Install NewHeap Platform guidance

The plugin contains the consumer-facing `newheap-consumer-development` skill. It does not contain the Platform maintainer workflow.

Download `newheap-platform-<version>.tar.gz` and `SHA256SUMS` from the matching immutable GitHub Release tagged `newheap-platform-plugin-v<version>`. Verify the archive against the checksum file before extracting it. If that release does not exist, the plugin version is not available for stable consumer installation; do not substitute an unversioned workflow artifact.

For a repository-pinned installation, run from this plugin directory:

```text
node scripts/install-consumer-skill.mjs --consumer <consumer-root>
```

Commit `<consumer-root>/.agents/skills/newheap-consumer-development`. Codex discovers that repository skill automatically. Verify it after updating the plugin with:

```text
node scripts/install-consumer-skill.mjs --consumer <consumer-root> --check
```

The installer refuses to overwrite locally changed installed files. Review those changes first, or pass `--force` only when full replacement is intentional.

The plugin follows the guidance version in `guidance/version.json`. Compatible library-package versions are recorded separately in `distribution.json`.

## Bootstrap an empty consumer repository

After extracting and verifying the versioned plugin artifact, install its skill
into the empty Git repository and run the pinned bootstrapper. This example
creates a management portal backed by PostgreSQL; select another explicit
profile and persistence choice when that better matches the confirmed scope:

```text
node <extracted-plugin>/scripts/install-consumer-skill.mjs --consumer <consumer-root>
node <consumer-root>/.agents/skills/newheap-consumer-development/scripts/bootstrap-newheap-consumer.mjs <consumer-root> --name Example.Portal --profile management-portal --database postgresql
```

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
node <consumer-root>/.agents/skills/newheap-consumer-development/scripts/inspect-newheap-consumer.mjs <consumer-root> --mode validate
```
