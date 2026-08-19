---
name: sample-project-management-development
description: Maintain SampleProjectManagement as executable documentation for NewHeap consumer applications. Use when adding, changing, reviewing or repairing sample backend/frontend behavior, case evidence, sample-owned EF migrations, Scalar controller contracts, generated sample documentation, repository structure, or examples required by a NewHeap library change.
---

# SampleProjectManagement development

Treat the sample implementation, focused tests, canonical case registry and
generated guidance as one contract. The sample must remain a realistic consumer
repository, not a collection of isolated snippets.

## Establish scope

Write all sample documentation, case metadata and AI-facing instructions in
English. Keep a complete English translation set for every sample application;
additional languages are allowed only when their keys remain aligned with
English. Fix language drift in canonical sources before regenerating outputs.

1. Read the repository-level and sample-level `AGENTS.md` files.
2. Read [repository structure](references/repository-structure.md) before choosing
   an owning project or changing paths.
3. Search `docs/cases/sample-case-registry.json` for the affected public surface.
4. If the work changes a NewHeap library, also use the repository's
   `newheap-library-maintenance` skill and inspect the applicable atomic guidance rule.
5. Read [sample workflow](references/sample-workflow.md) before changing evidence,
   generated artifacts, EF behavior or controller/frontend contracts.

## Implement the executable consumer path

- Extend the existing case instead of creating a duplicate.
- Put code in the owning `src` project and keep reusable library code separate
  from consumer-specific entities, claims, migrations and UI composition.
- Demonstrate the preferred API in the main flow. Label lower-level or legacy
  alternatives explicitly.
- Add focused behavior evidence. For provider-sensitive persistence, exercise
  real SQL Server and PostgreSQL behavior or record an explicit `library-gap`.
- Keep Scalar metadata, authorization intent, Angular lifecycle extension points,
  translations and loading/empty/error/disabled states complete.
- Never imitate missing NewHeap functionality inside the sample merely to mark a
  case implemented.

## Synchronize the catalog

Update only the canonical registry and source evidence first. Then run from the
sample root:

```text
npm run generate:samples
npm run verify:samples
```

Review the generated plan, catalog, status, consumer guide and Angular case data.
Do not hand-edit those outputs. Run the smallest focused tests plus both sample
application builds; add real relational provider runs when database behavior is in
scope.

The handoff names the affected case IDs, evidence paths, commands run, provider
matrix and any remaining explicit gap.
