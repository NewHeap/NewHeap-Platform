using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Data;
using System.Data.Common;

namespace NewHeap.Platform.AspNet.Common.DAL.Bulk;

internal static class PostgreSqlBulkUpsertExecutor
{
    public static async Task<int> ExecuteAsync<TEntity>(
        DbContext context,
        BulkUpsertPlan<TEntity> plan,
        IEnumerable<TEntity> entities,
        DbTransaction transaction,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        return await ExecuteAsync(
            context,
            plan,
            entities,
            transaction,
            hydrateMatchedPrimaryKeys: false,
            cancellationToken);
    }

    internal static async Task<int> ExecuteAsync<TEntity>(
        DbContext context,
        BulkUpsertPlan<TEntity> plan,
        IEnumerable<TEntity> entities,
        DbTransaction transaction,
        bool hydrateMatchedPrimaryKeys,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var connection = context.Database.GetDbConnection() as NpgsqlConnection
            ?? throw new InvalidOperationException("The PostgreSQL provider did not expose an NpgsqlConnection.");
        var npgsqlTransaction = transaction as NpgsqlTransaction
            ?? throw new InvalidOperationException("The PostgreSQL provider did not expose an NpgsqlTransaction.");
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var sql = context.GetService<ISqlGenerationHelper>();
        var temporaryTableName = $"nh_bulk_upsert_{Guid.NewGuid():N}";
        var temporaryIndexName = $"ix_nh_bulk_upsert_{Guid.NewGuid():N}";
        var sourceOrdinalColumnName = plan.GeneratedPrimaryKey is null
            ? null
            : $"__nh_source_ordinal_{Guid.NewGuid():N}";
        var insertedPrimaryKeyColumnName = plan.GeneratedPrimaryKey is null
            ? null
            : $"__nh_inserted_key_{Guid.NewGuid():N}";
        var temporaryTable = sql.DelimitIdentifier(temporaryTableName);
        var targetTable = sql.DelimitIdentifier(plan.TableName, plan.Schema);
        var columns = JoinColumns(sql, plan.StagingProperties);
        var stagingColumns = plan.GeneratedPrimaryKey is null
            ? columns
            : $"{columns}, 0::bigint AS {sql.DelimitIdentifier(sourceOrdinalColumnName!)}, NULL::{plan.GeneratedPrimaryKey.StoreTypeName} AS {sql.DelimitIdentifier(insertedPrimaryKeyColumnName!)}";

        await ExecuteCommandAsync(
            connection,
            npgsqlTransaction,
            $"CREATE TEMP TABLE {temporaryTable} ON COMMIT DROP AS SELECT {stagingColumns} FROM {targetTable} WITH NO DATA;",
            cancellationToken);

        var stagedEntities = plan.GeneratedPrimaryKey is null ? null : new List<TEntity>();
        var sourceOrdinal = 0L;
        var copyColumns = sourceOrdinalColumnName is null
            ? columns
            : $"{columns}, {sql.DelimitIdentifier(sourceOrdinalColumnName)}";
        await using (var importer = await connection.BeginBinaryImportAsync(
                         $"COPY {temporaryTable} ({copyColumns}) FROM STDIN (FORMAT BINARY)",
                         cancellationToken))
        {
            foreach (var entity in entities)
            {
                if (entity is null)
                {
                    throw new InvalidOperationException("Bulk upsert input cannot contain null entities.");
                }

                await importer.StartRowAsync(cancellationToken);
                foreach (var property in plan.StagingProperties)
                {
                    var value = property.GetProviderValue(entity);
                    if (value is null)
                    {
                        await importer.WriteNullAsync(cancellationToken);
                    }
                    else
                    {
                        await importer.WriteAsync(value, property.StoreTypeName, cancellationToken);
                    }
                }

                if (stagedEntities is not null)
                {
                    stagedEntities.Add(entity);
                    await importer.WriteAsync(sourceOrdinal++, "bigint", cancellationToken);
                }
            }

            await importer.CompleteAsync(cancellationToken);
        }

        if (plan.Operation != BulkUpsertOperation.InsertOnly)
        {
            await ExecuteCommandAsync(
                connection,
                npgsqlTransaction,
                $"CREATE UNIQUE INDEX {sql.DelimitIdentifier(temporaryIndexName)} ON {temporaryTable} ({JoinColumns(sql, plan.MatchProperties)});",
                cancellationToken);
        }

        int affected;
        if (plan.Operation == BulkUpsertOperation.InsertOnly)
        {
            await PopulateGeneratedKeysAsync(
                connection,
                npgsqlTransaction,
                temporaryTable,
                insertedPrimaryKeyColumnName!,
                targetTable,
                plan.GeneratedPrimaryKey!,
                cancellationToken);
            affected = await ExecuteCommandAsync(
                connection,
                npgsqlTransaction,
                BuildInsertOnlySql(
                    sql,
                    targetTable,
                    temporaryTable,
                    insertedPrimaryKeyColumnName!,
                    plan),
                cancellationToken);
            await HydrateGeneratedKeysAsync(
                connection,
                npgsqlTransaction,
                temporaryTable,
                sourceOrdinalColumnName!,
                insertedPrimaryKeyColumnName!,
                plan.GeneratedPrimaryKey!,
                stagedEntities!,
                cancellationToken);
        }
        else if (plan.Operation == BulkUpsertOperation.UpdateOnly)
        {
            affected = await ExecuteCommandAsync(
                connection,
                npgsqlTransaction,
                BuildUpdateExistingSql(
                    sql,
                    targetTable,
                    temporaryTable,
                    insertedPrimaryKeyColumnName!,
                    plan),
                cancellationToken);
            if (affected != stagedEntities!.Count)
            {
                throw new InvalidOperationException(
                    $"Bulk upsert navigation update expected {stagedEntities.Count} existing '{typeof(TEntity).Name}' rows but matched {affected}.");
            }
        }
        else if (plan.GeneratedPrimaryKey is null)
        {
            affected = await ExecuteCommandAsync(
                connection,
                npgsqlTransaction,
                BuildUpsertSql(sql, targetTable, temporaryTable, plan),
                cancellationToken);
        }
        else
        {
            var inserted = await ExecuteCommandAsync(
                connection,
                npgsqlTransaction,
                BuildInsertAndCaptureSql(
                    sql,
                    targetTable,
                    temporaryTable,
                    insertedPrimaryKeyColumnName!,
                    plan),
                cancellationToken);
            var updated = plan.UpdateProperties.Count == 0
                ? 0
                : await ExecuteCommandAsync(
                    connection,
                    npgsqlTransaction,
                    BuildUpdateExistingSql(
                        sql,
                        targetTable,
                        temporaryTable,
                        insertedPrimaryKeyColumnName!,
                        plan),
                    cancellationToken);
            if (hydrateMatchedPrimaryKeys)
            {
                await ExecuteCommandAsync(
                    connection,
                    npgsqlTransaction,
                    BuildCaptureMatchedKeysSql(
                        sql,
                        targetTable,
                        temporaryTable,
                        insertedPrimaryKeyColumnName!,
                        plan),
                    cancellationToken);
            }
            await HydrateGeneratedKeysAsync(
                connection,
                npgsqlTransaction,
                temporaryTable,
                sourceOrdinalColumnName!,
                insertedPrimaryKeyColumnName!,
                plan.GeneratedPrimaryKey,
                stagedEntities!,
                cancellationToken);
            affected = inserted + updated;
        }

        await ExecuteCommandAsync(
            connection,
            npgsqlTransaction,
            $"DROP TABLE {temporaryTable};",
            cancellationToken);
        return affected;
    }

    private static string BuildInsertAndCaptureSql<TEntity>(
        ISqlGenerationHelper sql,
        string targetTable,
        string temporaryTable,
        string insertedPrimaryKeyColumnName,
        BulkUpsertPlan<TEntity> plan)
        where TEntity : class
    {
        var generateGuidInStatement = plan.GeneratedPrimaryKey is not null &&
                                      (Nullable.GetUnderlyingType(plan.GeneratedPrimaryKey.ModelClrType) ??
                                       plan.GeneratedPrimaryKey.ModelClrType) == typeof(Guid) &&
                                      plan.GeneratedPrimaryKey.DefaultValueSql is null;
        var columns = string.Join(
            ", ",
            (generateGuidInStatement
                    ? new[] { plan.GeneratedPrimaryKey! }.Concat(plan.InsertProperties)
                    : plan.InsertProperties)
                .Select(property => sql.DelimitIdentifier(property.ColumnName)));
        var selectColumns = string.Join(
            ", ",
            (generateGuidInStatement ? new[] { "gen_random_uuid()" } : [])
                .Concat(plan.InsertProperties.Select(property => sql.DelimitIdentifier(property.ColumnName))));
        var conflictColumns = JoinColumns(sql, plan.MatchProperties);
        var match = string.Join(
            " AND ",
            plan.MatchProperties.Select(property =>
                $"source.{sql.DelimitIdentifier(property.ColumnName)} = inserted_rows.{sql.DelimitIdentifier(property.ColumnName)}"));
        var returningColumns = string.Join(
            ", ",
            new[] { plan.GeneratedPrimaryKey! }
                .Concat(plan.MatchProperties)
                .Select(property => sql.DelimitIdentifier(property.ColumnName)));

        return $"""
            WITH inserted_rows AS (
                INSERT INTO {targetTable} ({columns})
                SELECT {selectColumns} FROM {temporaryTable}
                ON CONFLICT ({conflictColumns}) DO NOTHING
                RETURNING {returningColumns}
            )
            UPDATE {temporaryTable} AS source
            SET {sql.DelimitIdentifier(insertedPrimaryKeyColumnName)} = inserted_rows.{sql.DelimitIdentifier(plan.GeneratedPrimaryKey!.ColumnName)}
            FROM inserted_rows
            WHERE {match};
            """;
    }

    private static string BuildInsertOnlySql<TEntity>(
        ISqlGenerationHelper sql,
        string targetTable,
        string temporaryTable,
        string insertedPrimaryKeyColumnName,
        BulkUpsertPlan<TEntity> plan)
        where TEntity : class
    {
        var insertedKey = sql.DelimitIdentifier(insertedPrimaryKeyColumnName);
        var insertColumns = string.Join(
            ", ",
            new[] { plan.GeneratedPrimaryKey! }
                .Concat(plan.InsertProperties)
                .Select(property => sql.DelimitIdentifier(property.ColumnName)));
        var selectColumns = string.Join(
            ", ",
            new[] { insertedKey }
                .Concat(plan.InsertProperties.Select(property => sql.DelimitIdentifier(property.ColumnName))));

        return $"""
            INSERT INTO {targetTable} ({insertColumns}) OVERRIDING SYSTEM VALUE
            SELECT {selectColumns} FROM {temporaryTable};
            """;
    }

    private static string BuildCaptureMatchedKeysSql<TEntity>(
        ISqlGenerationHelper sql,
        string targetTable,
        string temporaryTable,
        string insertedPrimaryKeyColumnName,
        BulkUpsertPlan<TEntity> plan)
        where TEntity : class
    {
        var match = string.Join(
            " AND ",
            plan.MatchProperties.Select(property =>
                $"target.{sql.DelimitIdentifier(property.ColumnName)} = source.{sql.DelimitIdentifier(property.ColumnName)}"));
        var insertedKey = sql.DelimitIdentifier(insertedPrimaryKeyColumnName);

        return $"""
            UPDATE {temporaryTable} AS source
            SET {insertedKey} = target.{sql.DelimitIdentifier(plan.GeneratedPrimaryKey!.ColumnName)}
            FROM {targetTable} AS target
            WHERE source.{insertedKey} IS NULL
              AND {match};
            """;
    }

    private static async Task PopulateGeneratedKeysAsync<TEntity>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string temporaryTable,
        string insertedPrimaryKeyColumnName,
        string targetTable,
        BulkUpsertProperty<TEntity> generatedPrimaryKey,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        string generatedValueSql;
        if ((Nullable.GetUnderlyingType(generatedPrimaryKey.ModelClrType) ?? generatedPrimaryKey.ModelClrType) == typeof(Guid))
        {
            generatedValueSql = generatedPrimaryKey.DefaultValueSql ?? "gen_random_uuid()";
        }
        else
        {
            // ponytail: Numeric generated navigation keys require an identity/serial sequence;
            // add modelled default-expression support before accepting other generators.
            await using var sequenceCommand = connection.CreateCommand();
            sequenceCommand.Transaction = transaction;
            sequenceCommand.CommandText = "SELECT pg_get_serial_sequence(@table_name, @column_name);";
            sequenceCommand.Parameters.AddWithValue("table_name", targetTable);
            sequenceCommand.Parameters.AddWithValue("column_name", generatedPrimaryKey.ColumnName);
            var sequence = await sequenceCommand.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new NotSupportedException(
                    $"PostgreSQL graph upsert requires a sequence-backed generated numeric key '{generatedPrimaryKey.ColumnName}'.");
            generatedValueSql = $"nextval('{sequence.Replace("'", "''", StringComparison.Ordinal)}'::regclass)";
        }

        await ExecuteCommandAsync(
            connection,
            transaction,
            $"UPDATE {temporaryTable} SET \"{insertedPrimaryKeyColumnName.Replace("\"", "\"\"", StringComparison.Ordinal)}\" = {generatedValueSql};",
            cancellationToken);
    }

    private static string BuildUpdateExistingSql<TEntity>(
        ISqlGenerationHelper sql,
        string targetTable,
        string temporaryTable,
        string insertedPrimaryKeyColumnName,
        BulkUpsertPlan<TEntity> plan)
        where TEntity : class
    {
        var update = string.Join(
            ", ",
            plan.UpdateProperties.Select(property =>
                $"{sql.DelimitIdentifier(property.ColumnName)} = source.{sql.DelimitIdentifier(property.ColumnName)}"));
        var match = string.Join(
            " AND ",
            plan.MatchProperties.Select(property =>
                $"target.{sql.DelimitIdentifier(property.ColumnName)} = source.{sql.DelimitIdentifier(property.ColumnName)}"));

        return $"""
            UPDATE {targetTable} AS target
            SET {update}
            FROM {temporaryTable} AS source
            WHERE {match}
              AND source.{sql.DelimitIdentifier(insertedPrimaryKeyColumnName)} IS NULL;
            """;
    }

    private static async Task HydrateGeneratedKeysAsync<TEntity>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string temporaryTable,
        string sourceOrdinalColumnName,
        string insertedPrimaryKeyColumnName,
        BulkUpsertProperty<TEntity> generatedPrimaryKey,
        IReadOnlyList<TEntity> stagedEntities,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {sourceOrdinalColumnName}, {insertedPrimaryKeyColumnName}
            FROM {temporaryTable}
            WHERE {insertedPrimaryKeyColumnName} IS NOT NULL
            ORDER BY {sourceOrdinalColumnName};
            """;
        await using var result = await command.ExecuteReaderAsync(cancellationToken);
        while (await result.ReadAsync(cancellationToken))
        {
            var sourceOrdinal = checked((int)result.GetInt64(0));
            generatedPrimaryKey.SetProviderValue(stagedEntities[sourceOrdinal], result.GetValue(1));
        }
    }

    private static string BuildUpsertSql<TEntity>(
        ISqlGenerationHelper sql,
        string targetTable,
        string temporaryTable,
        BulkUpsertPlan<TEntity> plan)
        where TEntity : class
    {
        var columns = JoinColumns(sql, plan.InsertProperties);
        var conflictColumns = JoinColumns(sql, plan.MatchProperties);
        var conflictAction = plan.UpdateProperties.Count == 0
            ? "DO NOTHING"
            : $"DO UPDATE SET {string.Join(", ", plan.UpdateProperties.Select(property => $"{sql.DelimitIdentifier(property.ColumnName)} = EXCLUDED.{sql.DelimitIdentifier(property.ColumnName)}"))}";

        return $"""
            INSERT INTO {targetTable} AS target ({columns})
            SELECT {columns} FROM {temporaryTable}
            ON CONFLICT ({conflictColumns}) {conflictAction};
            """;
    }

    private static string JoinColumns<TEntity>(
        ISqlGenerationHelper sql,
        IEnumerable<BulkUpsertProperty<TEntity>> properties)
        where TEntity : class
    {
        return string.Join(", ", properties.Select(property => sql.DelimitIdentifier(property.ColumnName)));
    }

    private static async Task<int> ExecuteCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
