using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace NewHeap.Media.FileStructureStorage;

/// <summary>
/// Routes provider migration operations to the configured media schema while SQL is generated.
/// </summary>
internal static class FileStructureMigrationSchema
{
    // Provider migrations are authored in the default schema. Route their operations without rewriting migration history.
    // Raw SQL is provider-specific, so each provider passes the quoted default schema exactly as its migrations write it.
    internal static void Route(IReadOnlyList<MigrationOperation> operations, string schema,
        string authoredSqlSchemaIdentifier, ISqlGenerationHelper sqlGenerationHelper)
    {
        var authoredSchemaPrefix = authoredSqlSchemaIdentifier + ".";
        var configuredSchemaPrefix = sqlGenerationHelper.DelimitIdentifier(schema) + ".";

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case EnsureSchemaOperation ensure:
                    ensure.Name = schema;
                    break;
                case TableOperation table:
                    table.Schema = schema;
                    if (table is CreateTableOperation create)
                    {
                        foreach (var column in create.Columns)
                        {
                            column.Schema = schema;
                        }
                        if (create.PrimaryKey is not null)
                        {
                            create.PrimaryKey.Schema = schema;
                        }
                    }
                    break;
                case ColumnOperation column:
                    column.Schema = schema;
                    break;
                case DropColumnOperation column:
                    column.Schema = schema;
                    break;
                case CreateIndexOperation index:
                    index.Schema = schema;
                    break;
                case DropIndexOperation index:
                    index.Schema = schema;
                    break;
                case DropTableOperation table:
                    table.Schema = schema;
                    break;
                case SqlOperation sql:
                    sql.Sql = sql.Sql.Replace(authoredSchemaPrefix, configuredSchemaPrefix, StringComparison.Ordinal);
                    break;
                case ITableMigrationOperation unsupported:
                    // Fail closed instead of silently creating objects in the default schema.
                    throw new NotSupportedException(
                        $"Media migration operation '{unsupported.GetType().Name}' cannot be routed to schema '{schema}'.");
            }
        }
    }
}
