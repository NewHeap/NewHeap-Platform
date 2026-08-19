# Sample maintenance

Use English for registry titles, outcomes, status reasons, plan templates,
catalog narrative and all AI-facing instructions. Executable UI samples must
always include English translations. Additional languages are allowed, but
their translation key sets must remain aligned with English.

`examples/SampleProjectManagement/docs/cases/sample-case-registry.json` is the canonical case inventory. `library-sample-plan.md`, `sample-implementation-status.json`, the Angular `sample-cases.ts`, human consumer guide and consumer-skill references are generated views.

## Update a case

1. Reuse an existing case for the affected public behavior where possible.
2. Add executable backend or frontend evidence under `examples/SampleProjectManagement`.
3. Update the case's title, surface, outcome, implementation state and evidence in the registry.
4. Update or add an atomic `guidance/rules/**/*.md` rule that maps the public symbol to implemented sample cases.
5. Run `npm run guidance:generate`, then the sample's `npm run generate:samples`.
6. Run `npm run guidance:validate` and the sample evidence verifier.

Evidence paths are relative to `examples/SampleProjectManagement`. A test is preferred alongside runtime evidence. Use `implemented` only when the sample path executes. Use `partial`, `planned` or `library-gap` with a concrete `statusReason` when it does not.

Never edit these generated files directly:

- `examples/SampleProjectManagement/docs/library-sample-plan.md`
- `examples/SampleProjectManagement/docs/sample-implementation-status.json`
- `examples/SampleProjectManagement/src/Front-end/projects/sample-project-management-common/src/lib/sample-cases.ts`
- `docs/consumer-guide/**`
- `skills/newheap-consumer-development/references/**`
- `skills/skill-manifest.json`
- `plugins/newheap-platform/skills/newheap-consumer-development/**`

The sample must stand alone. Do not refer to copied comparison applications, temporary workspaces or proprietary consumer implementations.
