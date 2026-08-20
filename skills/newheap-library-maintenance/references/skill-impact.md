# Skill impact

A library change affects consumer guidance when it adds or changes a public type, method, option, extension, export, DI registration or configuration key; changes observable behavior; fixes a consumer-visible defect; alters EF/query/provider semantics; or changes the preferred usage pattern without changing a signature.

## Ownership decision

- Provider-independent primitives and algorithms: `NewHeap.Platform.Common`.
- ASP.NET and provider-neutral repository/query behavior: `NewHeap.Platform.AspNet.Common`.
- SQL Server implementation, SQL and migrations: matching `.SqlServer` project.
- PostgreSQL implementation, SQL and migrations: matching `.PostgreSql` project.
- Angular library behavior: `src/Front-end/projects/nh-common`.
- Consumer entities, permissions, services, DbContext and migrations: consuming implementation; for executable evidence, SampleProjectManagement.

## Guidance decision

Update the closest atomic rule. Its frontmatter must name implemented sample cases, real public symbols, target skill, providers and risk. Put normative instructions under `Preferred approach`, known traps under `Avoid`, and reproducible checks under `Verification`.

Add a new rule only when the behavior has a distinct trigger or verification boundary. Do not create one large wiki page: the generator groups atomic rules into task-oriented references so an agent can load only what it needs.

Any change to consumer rules, the canonical consumer skill suite, its portable installer or its executable case contract is a distributable guidance change. Increment `guidance/version.json` and the `newheap-platform` plugin manifest to the same semantic version. Regeneration updates the suite content hash and package compatibility metadata; CI rejects an unchanged version.

If a public change is truly internal and has no observable impact, the snapshot may change without a new consumer rule only when the declaration itself is not consumer-facing. Record deliberate exceptions in `guidance/impact-exceptions.json` with scoped path prefixes, an owner, rationale and expiry; permanent blanket exceptions are invalid.
