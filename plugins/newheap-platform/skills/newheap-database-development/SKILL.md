---
name: newheap-database-development
description: Implement or review NewHeap consumer EF Core queries, provider wiring, migrations, raw SQL and relational behavior across SQL Server and PostgreSQL.
---

# NewHeap database development

Read [database providers](references/database-providers.md) for every query, schema, migration, raw SQL or provider-composition change.

Prefer provider-translatable LINQ and keep filtering, ordering and paging server-side. Provider choice belongs in the composition layer. Raw SQL is provider-specific until proven otherwise and every data value is parameterized.

Never hand-edit an existing migration or model snapshot. Generate new consumer-owned migrations only for schema changes explicitly in scope.

Test relational semantics on real SQL Server and PostgreSQL providers when translation, constraints, transactions, migrations or raw SQL matter. EF Core InMemory is not equivalent. Report the provider matrix actually exercised and any explicit provider gap.
