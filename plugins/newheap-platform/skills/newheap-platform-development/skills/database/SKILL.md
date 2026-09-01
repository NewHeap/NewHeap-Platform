---
name: newheap-database-development
description: Implement, review or debug NewHeap consumer EF Core queries, provider wiring, migrations, raw SQL and relational behavior across SQL Server and PostgreSQL. Use also to investigate unexpected staging or production API and persisted-data behavior with supported read-only database diagnostics.
---

# NewHeap database development

Read [database providers](references/database-providers.md) for every query, schema, migration, raw SQL or provider-composition change.

Read [database diagnostics](references/database-diagnostics.md) when a task investigates unexpected persisted state, missing or incorrect records, an API/database discrepancy, or unexplained staging or production behavior for which database evidence could answer the question. Do not wait for the user to name `newheap-db`, and do not stop after offering it: take the supported read-only route directly when the requested scope authorizes that local diagnostic. Do not use it for a bug that database evidence cannot materially clarify.

When the consumer repository pins the tool and checks in `.newheap/database-read.json`, local database-read instructions and request files, treat those files as the execution contract. Start in the documented tool-manifest or backend directory and use the repository-declared profile and ceilings. Do not inspect controllers, entities, EF configuration, appsettings, secrets or NuGet package internals merely to reconstruct those choices or the JSON request shape. `dotnet tool list --local` proves only that a manifest entry exists; restore the tool only when `newheap-db` reports that the local command is unavailable.

Prefer a structured MCP call for agent diagnostics. For direct CLI use, stream JSON through stdin or pass only a request path with `--request-file`; never inline serialized JSON in a shell or process-launch argument. When deployed identifiers are unknown, use only the typed schema requests needed to confirm them, then validate and execute the bounded parameterized query. Stop when the requested database evidence is available. Do not switch to source or package investigation merely because the checked-in contract already answers a routing or request-shape question, and do not narrow a correct candidate set merely to fit a Windows command-line limit.

Treat staging and production as live, potentially large databases. Every diagnostic data query needs a provider-native SQL row cap as well as bounded request limits. When table size or predicate selectivity is uncertain, make at most one focused index-metadata check for the already-confirmed object. Use it when a suitable leading key is immediately clear; otherwise continue with a tightly bounded query. Do not spend additional calls tuning or resolving index ambiguity, and never compensate by broadening the query or automatically increasing timeouts.

Prefer provider-translatable LINQ and keep filtering, ordering and paging server-side. Provider choice belongs in the composition layer. Raw SQL is provider-specific until proven otherwise and every data value is parameterized.

Never hand-edit an existing migration or model snapshot. Generate new consumer-owned migrations only for schema changes explicitly in scope.

Test relational semantics on real SQL Server and PostgreSQL providers when translation, constraints, transactions, migrations or raw SQL matter. EF Core InMemory is not equivalent. Report the provider matrix actually exercised and any explicit provider gap.
