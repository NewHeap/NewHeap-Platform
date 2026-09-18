using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Routes the checked-in migrations, which are generated for the default <c>nhai</c> schema,
/// to the configured schema without rewriting migration history. Provider SQL generators call
/// this before generating SQL.
/// </summary>
internal static class NhAssistantMigrationSchema
{
    public static string GetConfiguredSchema(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context is NhAssistantDbContext assistant
            ? assistant.Schema
            : NhAssistantDbContextOptions.DefaultSchema;
    }

    public static void Apply(IEnumerable<MigrationOperation> operations, string schema)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (string.Equals(schema, NhAssistantDbContextOptions.DefaultSchema, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case EnsureSchemaOperation ensure:
                    ensure.Name = Route(ensure.Name, schema)!;
                    break;
                case CreateTableOperation create:
                    create.Schema = Route(create.Schema, schema);
                    foreach (var column in create.Columns)
                    {
                        column.Schema = Route(column.Schema, schema);
                    }
                    if (create.PrimaryKey is not null)
                    {
                        create.PrimaryKey.Schema = Route(create.PrimaryKey.Schema, schema);
                    }
                    foreach (var foreignKey in create.ForeignKeys)
                    {
                        foreignKey.Schema = Route(foreignKey.Schema, schema);
                        foreignKey.PrincipalSchema = Route(foreignKey.PrincipalSchema, schema);
                    }
                    foreach (var unique in create.UniqueConstraints)
                    {
                        unique.Schema = Route(unique.Schema, schema);
                    }
                    break;
                case AddForeignKeyOperation foreignKey:
                    foreignKey.Schema = Route(foreignKey.Schema, schema);
                    foreignKey.PrincipalSchema = Route(foreignKey.PrincipalSchema, schema);
                    break;
                case TableOperation table:
                    table.Schema = Route(table.Schema, schema);
                    break;
                case ColumnOperation column:
                    column.Schema = Route(column.Schema, schema);
                    break;
                case DropColumnOperation column:
                    column.Schema = Route(column.Schema, schema);
                    break;
                case CreateIndexOperation index:
                    index.Schema = Route(index.Schema, schema);
                    break;
                case DropIndexOperation index:
                    index.Schema = Route(index.Schema, schema);
                    break;
                case DropTableOperation table:
                    table.Schema = Route(table.Schema, schema);
                    break;
                case DropForeignKeyOperation foreignKey:
                    foreignKey.Schema = Route(foreignKey.Schema, schema);
                    break;
                case AddPrimaryKeyOperation primaryKey:
                    primaryKey.Schema = Route(primaryKey.Schema, schema);
                    break;
                case DropPrimaryKeyOperation primaryKey:
                    primaryKey.Schema = Route(primaryKey.Schema, schema);
                    break;
                case RenameTableOperation rename:
                    rename.Schema = Route(rename.Schema, schema);
                    rename.NewSchema = Route(rename.NewSchema, schema);
                    break;
                case RenameColumnOperation rename:
                    rename.Schema = Route(rename.Schema, schema);
                    break;
                case RenameIndexOperation rename:
                    rename.Schema = Route(rename.Schema, schema);
                    break;
            }
        }
    }

    private static string? Route(string? current, string schema)
    {
        return string.Equals(current, NhAssistantDbContextOptions.DefaultSchema, StringComparison.Ordinal)
            ? schema
            : current;
    }
}
