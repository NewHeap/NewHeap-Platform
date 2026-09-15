using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

#pragma warning disable EF1001 // Npgsql's SQL generator constructor requires its provider options.
internal sealed class PostgreSqlMediaMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions options) : NpgsqlMigrationsSqlGenerator(dependencies, options)
{
    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        var schema = PostgreSqlMediaModelCacheKeyFactory.GetSchema(Dependencies.CurrentContext.Context);

        // Historical migrations contain the original schema. Route their operations without rewriting migration history.
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
                    sql.Sql = sql.Sql.Replace("\"nhmedia\".",
                        Dependencies.SqlGenerationHelper.DelimitIdentifier(schema) + ".", StringComparison.Ordinal);
                    break;
            }
        }

        return base.Generate(operations, model, options);
    }
}
#pragma warning restore EF1001
