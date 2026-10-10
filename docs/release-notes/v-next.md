# v-next

## NewHeap.Platform.Media.FileStructureStorage.SqlServer: configured media schema

SQL Server media migrations now apply to the schema configured through
`FileStructureDbContextOptions.Scheme`, whether the startup migration, a direct
`MigrateAsync` or a generated migration script runs them. As the PostgreSQL
provider already did, the provider routes every migration operation to that
schema while it generates SQL, and keeps a separate EF model per schema. A
direct `MigrateAsync` with a custom schema no longer reports pending model
changes.

This fixes the `20260902133212_IndexSeekLookup` upgrade, which always targeted
`nhmedia`. With any other schema, the startup migration failed and was rolled
back while the package already used the new `PathLookup` columns, so media
requests failed with `Invalid column name 'PathLookup'`. Applications on the
default `nhmedia` schema and PostgreSQL applications were not affected; their
migration SQL is unchanged.

| Adoption note | Required action |
|---|---|
| A SQL Server media database with a custom `Scheme` still has `20260902133212_IndexSeekLookup` pending after the failed upgrade. | Update the package and restart the application; the startup migration, or your own `MigrateAsync` call when `RunMigrations` is disabled, applies the pending migration in the configured schema. |
| A database where the same migration was applied by hand in the configured schema and recorded in that schema's `_migrations` table is already up to date. | No action; the migration is skipped. |
| `BaseMigration.DefaultScheme` now always returns the schema the migrations are authored in, `nhmedia`, instead of the configured schema. | Read `FileStructureDbContextOptions.Scheme` when you need the configured schema. |
