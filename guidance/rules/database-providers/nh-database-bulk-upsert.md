---
id: nh-database-bulk-upsert
title: "Stream large imports through provider-native bulk upsert"
area: database
reference: database-bulk-upsert
summary: "Use ExecuteUpsertAsync for immediate, untracked imports of scalar rows or explicitly selected one-to-one and one-to-many dependents."
sample-cases: ["SPM-053"]
public-symbols: ["RepositoryBulkExtensions", "ExecuteUpsertAsync"]
skills: ["newheap-database-development", "newheap-backend-development"]
providers: ["sql-server", "postgresql"]
risk: critical
---
## Preferred approach

Call `ExecuteUpsertAsync` on an existing `IRepository<TEntity>` when an import supplies many entities and one mapped property or composite property selector identifies each root row. Configure a non-nullable, non-filtered unique key or index over exactly those root match properties. Normalize, validate, and set application-owned audit values before the call because the operation is immediate, bypasses EF change tracking, and does not run the regular CRUD service or `SaveChanges` pipeline.

Use the overload with navigation selectors only when the import also owns immediate one-to-one or one-to-many dependents. Select each principal-to-dependent navigation explicitly, for example `[project => project.Tasks]`; loaded inverse, lookup, or other unselected navigations are ignored. The relationship must reference the principal primary key, and each root and dependent must have one non-shadow numeric or `Guid` primary key. Dependents always match on that primary key:

- A default generated-on-add key inserts the dependent and receives its generated key.
- A non-default generated-on-add key updates an existing dependent; a missing key fails and rolls back the whole graph.
- A client-generated key must be non-default and provides normal insert-or-update semantics.

Before issuing SQL, the graph overload checks every selected dependent for populated principal-to-dependent or many-to-many navigations. It throws `NotSupportedException` with the nested navigation path instead of silently ignoring that data. Empty nested collections are allowed; inverse back-references and dependent-to-principal lookup navigations remain ignored.

Roots are written first, including hydrating generated keys for both inserted and matched roots when dependents need the key. The operation then propagates the principal key to each selected dependent foreign key and bulk-processes each dependent type. The returned affected count covers root and dependent inserts and updates. Omitting a reference or child from the supplied graph never deletes stored data.

The SQL Server implementation streams the entities into a temporary table with `SqlBulkCopy` and executes `MERGE WITH (HOLDLOCK)`. The PostgreSQL implementation uses binary `COPY`, `INSERT ... ON CONFLICT DO NOTHING` for root inserts, and set-based inserts or updates for dependents. Both implementations join an existing NewHeap transaction or own and commit one transaction around every selected table. Store-generated properties, concurrency tokens, primary keys, match properties, and `CreationDateTime` on `IdDbEntity` are not overwritten during an update. For the scalar overload, a supported generated primary key is written only to inserted input entities; matched inputs remain unrefreshed. Other generated properties are never refreshed. An outer transaction rollback does not restore CLR key values that were already returned by the database.

## Avoid

- Calling `AddRange`, querying every match, or saving each imported entity separately for bulk imports.
- Matching nullable, non-unique, filtered, computed, or store-generated properties.
- Automatically traversing every populated navigation. Select only import-owned principal-to-dependent navigations.
- Supplying a populated nested or many-to-many navigation; recursive graphs fail before the root upsert. Dependent-to-principal lookups, alternate principal keys, inheritance, owned/table-shared entities, and shadow foreign keys remain unsupported.
- Supplying a non-default generated dependent key for a row that may not exist; generated keys distinguish an update from an insert.
- Calling `SaveChangesAsync` to make the upsert take effect, or assuming tracked instances were refreshed.
- Expecting composite, string, or other generated key shapes and non-key generated values to be hydrated.
- Adding delete-by-absence behavior to an upsert. Synchronization is a separate, explicitly destructive operation.
- Writing one SQL dialect that conditionally interpolates SQL Server and PostgreSQL fragments.

## Verification

Run the same mixed insert/update import with thousands of records against real SQL Server and PostgreSQL instances. Verify the affected count, composite-key matching, preservation of creation timestamps, value conversion, no change-tracker entries, participation in an outer rollback, and deterministic failure for duplicate source keys. Verify scalar generated-key hydration separately from a graph containing a matched root, a one-to-one numeric-key insert/update, and one-to-many `Guid`-key inserts/updates. Assert the propagated foreign keys and stored updated values, that omitted children remain, and that a missing non-default generated dependent key rolls back root changes. Supply a populated grandchild collection and verify the operation reports its full path before changing either root or children. Verify that any other EF provider throws `NotSupportedException` before enumerating the input.
