---
name: newheap-library-maintenance
description: Change or review NewHeap Platform libraries while keeping public APIs, behavior, provider support, executable samples, consumer guidance and distributable skills synchronized. Use for new or changed public types, methods, options, exports, registrations or configuration keys; observable bug fixes; EF/query/provider/raw-SQL changes; compatibility-sensitive Angular behavior; package upgrades; release preparation; and guidance or sample maintenance after library changes.
---

# NewHeap library maintenance

Treat the public implementation, focused tests, SampleProjectManagement, atomic guidance rules and generated consumer skill as one versioned product surface.

## Establish impact before editing

1. Read repository and subtree `AGENTS.md` files.
2. Inspect the owning library, public exports and existing focused tests.
3. Run `node <skill-directory>/scripts/assess-change-impact.mjs --base <target-branch>` when a target branch is available.
4. Search `examples/SampleProjectManagement/docs/cases/sample-case-registry.json` and `guidance/rules` for the affected surface. Extend an existing case and rule before adding duplicates.
5. Read [skill impact](references/skill-impact.md) for compatibility and ownership decisions.

## Change the complete contract

Write all repository documentation, AI instructions, skills, guidance rules,
sample-case titles/outcomes and generated narrative in English. Require every
sample UI to provide a complete English translation set; additional languages
may remain when their keys stay aligned with English. When language drift is
found in generated output, update the canonical rule, registry, template or
generator and regenerate instead of editing the generated file.

For every new or observably changed public surface:

1. Implement it in the correct neutral or provider-specific project.
2. Add focused regression/behavior tests.
3. Add or update executable evidence in SampleProjectManagement.
4. Update the canonical case registry and the applicable atomic rule under `guidance/rules`.
5. Generate consumer documentation and skill references; never hand-edit generated files.
6. Refresh the public API snapshot when the exported surface intentionally changed.
7. Keep every release-unit, guidance and `newheap-platform` plugin version unchanged during feature work. Regenerate the consumer-skill mirror and review package compatibility metadata; the protected `Prepare release` workflow owns every eventual SemVer bump.

Keep packable test-support projects and internal regression tests separate.
`NewHeap.Platform.*.Test` contains only reusable consumer helpers; NewHeap's own
`[Fact]` and `[Theory]` tests belong under `src/Back-end/Tests` in non-packable
plural `*.Tests` projects. Release workflows test the latter and publish only
the former.

For preview packages, stable versioning, public npm/NuGet publication, release assets or feed migration, follow [GitHub releases](references/github-releases.md). The release manifest and protected workflows replace the deleted PowerShell publish scripts.

Follow [sample maintenance](references/sample-maintenance.md) for registry fields, evidence rules and generation. A Markdown snippet is not executable evidence. Register a `library-gap` when the public library cannot actually support the intended sample.

## Protect compatibility

- Separate provider-neutral code from SQL Server and PostgreSQL implementations, SQL and migrations.
- Preserve existing Angular defaults and provider scopes unless an intentional breaking change has a versioned migration plan.
- Treat interceptor URL matching, lifecycle ordering, fluent filter serialization, falsy filters, enum reverse mappings and request-key composition as compatibility-sensitive.
- Never put consumer migrations or resource permission types in a reusable library.
- Do not use a sample-only improvement as permission to refactor unrelated library behavior.
- Never copy identifying names, domains, local paths, certificates, stack traces
  or fixtures from internal comparison applications into source, samples,
  guidance, skills or release artifacts; translate useful patterns into neutral
  NewHeap-owned evidence.

## Generate and verify

From the repository root run:

```text
npm run guidance:generate
npm run guidance:snapshot
npm run guidance:validate
npm run skills:eval
npm run plugin:validate
```

Then complete the applicable commands in [release checklist](references/release-checklist.md), including both real relational providers for database behavior and both Angular sample builds for frontend behavior. Run the skill creator's `quick_validate.py` for both skill directories when their structure or metadata changes.

The final handoff identifies changed public behavior, linked sample cases/evidence, generated guidance, compatibility impact, provider matrix and any explicit gap. Do not mark the work complete while generated artifacts or the API snapshot are stale.
