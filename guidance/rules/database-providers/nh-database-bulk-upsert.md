---
id: nh-database-bulk-upsert
title: "Stream large imports through provider-native bulk upsert"
area: database
reference: database-bulk-upsert
summary: "Use ExecuteUpsertAsync for immediate, untracked imports backed by SQL Server bulk copy and MERGE or PostgreSQL binary COPY and ON CONFLICT."
sample-cases: ["SPM-053"]
public-symbols: ["RepositoryBulkExtensions", "ExecuteUpsertAsync"]
skills: ["newheap-database-development", "newheap-backend-development"]
providers: ["sql-server", "postgresql"]
risk: critical
---
## Preferred approach

Call `ExecuteUpsertAsync` on an existing `IRepository<TEntity>` when an import supplies many independent entities and one mapped property or composite property selector identifies each row. Configure a non-nullable, non-filtered unique key or index over exactly those match properties. Normalize, validate, and set application-owned audit values before the call because the operation is immediate, bypasses EF change tracking, and does not run the regular CRUD service or `SaveChanges` pipeline.

The SQL Server implementation streams the entities into a temporary table with `SqlBulkCopy` and executes `MERGE WITH (HOLDLOCK)`. The PostgreSQL implementation uses binary `COPY`, `INSERT ... ON CONFLICT DO NOTHING` to capture inserts, and one set-based update for the conflicting existing rows. Both implementations join an existing NewHeap transaction or own and commit one transaction around staging and upsert. Store-generated properties, concurrency tokens, primary keys, match properties, and `CreationDateTime` on `IdDbEntity` are not overwritten during an update. When the entity has one store-generated numeric or `Guid` primary key, the generated key is written back to each inserted input entity without adding it to the change tracker. Matched input entities and other generated properties are not refreshed. An outer transaction rollback does not restore CLR key values that were already returned by the database.

## Avoid

- Calling `AddRange`, querying every match, or saving each imported entity separately for bulk imports.
- Matching nullable, non-unique, filtered, computed, or store-generated properties.
- Treating the upsert as graph persistence; navigations, inheritance, and table sharing are outside this API.
- Calling `SaveChangesAsync` to make the upsert take effect, or assuming tracked instances were refreshed.
- Expecting composite, string, or other generated key shapes and non-key generated values to be hydrated.
- Adding delete-by-absence behavior to an upsert. Synchronization is a separate, explicitly destructive operation.
- Writing one SQL dialect that conditionally interpolates SQL Server and PostgreSQL fragments.

## Verification

Run the same mixed insert/update import with thousands of records against real SQL Server and PostgreSQL instances. Verify the affected count, composite-key matching, preservation of creation timestamps, value conversion, no change-tracker entries, participation in an outer rollback, and deterministic failure for duplicate source keys. Verify that database-generated numeric and `Guid` primary keys are assigned only to inserted input entities. Verify that any other EF provider throws `NotSupportedException` before enumerating the input.
