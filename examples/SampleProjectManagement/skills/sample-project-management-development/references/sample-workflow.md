# Executable sample workflow

## Case and evidence

1. Find the affected entry in `docs/cases/sample-case-registry.json`.
2. Reuse its case ID and update the intended outcome only when the contract changed.
3. Implement behavior under `src` and add a focused test or reproducible runtime path.
4. Store evidence paths relative to the sample root. New sample evidence starts
   with `src/Back-end/` or `src/Front-end/`; direct NewHeap source evidence starts
   with `../../src/`.
5. Use `library-gap` with a precise missing capability when an executable sample
   cannot be built honestly.

## Backend contracts

- Keep controllers thin and orchestration in concrete services.
- Add entities, DbSets, relationships and consumer-owned migrations in the DAL.
- Keep mutate models free of audit timestamps and add mappings explicitly.
- Give every controller action complete Scalar/OpenAPI response and authorization metadata.
- Exercise both SQL Server and PostgreSQL for provider-sensitive EF/query/raw SQL behavior.

## Frontend contracts

- Keep root providers in app composition and feature imports provider-free.
- Use NewHeap `appOn...` lifecycle extension points for NewHeap base components.
- Prefer fluent filtering/ordering and translated enum option builders in primary examples.
- Preserve intentional loading, empty, error and disabled states in both applications.
- Build and inspect both management and workspace after shared UI changes.

## Generation and verification

From the sample root run:

```text
npm run generate:samples
npm run verify:samples
dotnet test src/Back-end/SampleProjectManagement.slnx
npm run build:management
npm run build:workspace
```

`generate:samples` owns generated documentation and `sample-cases.ts`.
`verify:samples` checks the repository layout, guidance consistency, evidence and
frontend integrity. Do not replace relational verification with EF Core InMemory.
