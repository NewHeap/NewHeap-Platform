# Install NewHeap Platform guidance

The plugin contains the consumer-facing `newheap-consumer-development` skill. It does not contain the Platform maintainer workflow.

Download the `newheap-platform-plugin-v*` asset from the matching immutable GitHub Release and verify it against that release's `SHA256SUMS` before extracting it. Do not install an unversioned workflow artifact as a stable consumer dependency.

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
into the empty Git repository and run the pinned bootstrapper:

```text
node <extracted-plugin>/scripts/install-consumer-skill.mjs --consumer <consumer-root>
node <consumer-root>/.agents/skills/newheap-consumer-development/scripts/bootstrap-newheap-consumer.mjs <consumer-root> --name Example.Portal --database postgresql
```

The bootstrap creates the solution and central props in `src/Back-end`, creates
the Angular workspace in `src/Front-end` with applications under `projects`,
restores the compatible NewHeap NuGet packages and installs the compatible npm
package. Package authentication is a hard gate: when restore or install fails,
configure machine-level credentials and rerun the idempotent command before any
feature scaffolding.

Use `--aspire`, `--docker` and `--elasticsearch` only when those optional
capabilities are requested. After the agent completes the management portal,
require this audit:

```text
node <consumer-root>/.agents/skills/newheap-consumer-development/scripts/inspect-newheap-consumer.mjs <consumer-root> --mode validate
```
