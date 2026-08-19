---
id: nh-database-provider-portability
title: "SQL Server and PostgreSQL as equivalent providers"
area: database
reference: database-providers
summary: "Keep neutral EF code provider-translatable and put registration, SQL, and migrations in the project that actually owns the provider or consumer schema."
sample-cases: ["SPM-051", "SPM-054", "SPM-055", "SPM-179", "SPM-194"]
public-symbols: ["IRepository", "Repository", "StartOrGetTransactionScopeAsync"]
skills: ["newheap-consumer-development"]
providers: ["sql-server", "postgresql"]
risk: critical
---
## Preferred approach

Use provider-neutral LINQ and model configuration in shared code. Put `UseSqlServer` or `UseNpgsql`, provider SQL, and provider-owned migrations in the owning implementation. Consumer entities and migrations belong in the consumer's database project. Use UTC-safe values in translated queries, preferably a captured `DateTimeOffset.UtcNow` value.

Raw SQL is provider-specific until both dialects have executable proof. Parameterize data values and obtain schema, table, and column names from EF metadata where possible. Provide separate implementations or clearly separated subcases for SQL Server and PostgreSQL.

## Avoid

- `DateTimeOffset.Now` in a translated query.
- Hard-coded SQL Server quoting, types, or functions in neutral code.
- `AsEnumerable` or premature materialization to bypass translation.
- Combining `EnsureCreated` with migrations as the normal schema-update path.
- EF Core InMemory as evidence for raw SQL, constraints, transactions, or migrations.

## Verification

Run the same integration scenarios on real SQL Server and PostgreSQL: query translation, migration to an empty database, constraints, transactions, and raw SQL. Explicitly report any provider that was not run as unverified.
