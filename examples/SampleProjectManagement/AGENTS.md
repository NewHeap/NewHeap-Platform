# SampleProjectManagement instructions for coding agents

These instructions apply to the complete SampleProjectManagement subtree and add
sample-specific requirements to the repository-level `AGENTS.md`. They may not
weaken the repository requirements.

## Use the sample maintenance skill

Read and follow `skills/sample-project-management-development/SKILL.md` whenever
you add, change, review or repair a sample, its evidence, its documentation or its
repository structure. Load the referenced workflow documents selected by that
skill before editing.

## Keep the consumer-repository structure intact

- Put all executable product source under `src`.
- Put ASP.NET composition and controllers under `src/Back-end/Applications`.
- Put sample domain behavior and DAL implementation under `src/Back-end/Libraries`.
- Put Aspire composition under `src/Back-end/Orchestration` and tests under
  `src/Back-end/Tests`.
- Put both Angular apps and their shared consumer library under `src/Front-end`.
- Put agent workflows under `skills`, durable explanation under `docs` and
  repository validation under `tools`.
- Keep the solution, `Directory.Build.props` and `Directory.Packages.props` in
  `src/Back-end` so every backend project inherits one build baseline and one
  central package version catalog. Keep package versions out of individual
  project files.
- Keep Angular workspace files in `src/Front-end`; never put `angular.json`, the
  frontend `package.json`, TypeScript configs or Angular projects at the sample
  root.
- Do not recreate the legacy top-level `Applications`, `Libraries`,
  `Orchestration`, `Tests` or `Front-end` directories.

## Treat samples as executable contracts

Write sample documentation, case metadata, comments used as teaching material
and all coding-agent instructions in English. Every frontend must have a
complete English translation set. Additional languages may remain, but must use
the same translation keys as English.

Before implementing, search `docs/cases/sample-case-registry.json` and extend an
existing case where possible. Put consumer entities, relationships, DbContext and
migrations in `SampleProjectManagement.DAL`; never move them into NewHeap
libraries. Provider-independent application behavior belongs in Core. HTTP
composition belongs in the API.

Every implemented case has real evidence under `src` and a focused verification
path. Markdown snippets and copied external projects are not evidence. For EF,
query, transaction, migration and raw SQL behavior, follow the repository's SQL
Server/PostgreSQL matrix; EF Core InMemory is not relational evidence.

Sample tests may reference the packable `NewHeap.Platform.*.Test` support
libraries to demonstrate reusable consumer test contexts and assertions. They
must never reference NewHeap's internal plural `NewHeap.Platform.*.Tests`
projects; those contain the libraries' own regression tests, not consumer APIs.

For controller changes, preserve the Scalar contract: summaries, descriptions,
typed responses, actual error responses and explicit authorization intent. For
Angular work, preserve the NewHeap lifecycle extension points, translation
namespace/dash-case rules, root-only `NhCommonModule.forRoot(...)` registration,
production-quality interaction states and the documented compatibility defaults.

## Generate and verify

Update the canonical registry, then run from this directory:

```text
npm run generate:samples
npm run verify:samples
dotnet test src/Back-end/SampleProjectManagement.slnx
npm run build:management
npm run build:workspace
```

Never edit generated sample plans, status JSON, consumer guidance or
`sample-cases.ts` by hand. If a public NewHeap surface changed, also complete the
repository-level guidance, API snapshot, skill, provider and release checks.
