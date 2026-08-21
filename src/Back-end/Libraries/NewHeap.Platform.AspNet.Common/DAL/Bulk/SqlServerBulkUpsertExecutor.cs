using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Data.Common;

namespace NewHeap.Platform.AspNet.Common.DAL.Bulk;

internal static class SqlServerBulkUpsertExecutor
{
    public static async Task<int> ExecuteAsync<TEntity>(
        DbContext context,
        BulkUpsertPlan<TEntity> plan,
        IEnumerable<TEntity> entities,
        DbTransaction transaction,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var connection = context.Database.GetDbConnection() as SqlConnection
            ?? throw new InvalidOperationException("The SQL Server provider did not expose a SqlConnection.");
        var sqlTransaction = transaction as SqlTransaction
            ?? throw new InvalidOperationException("The SQL Server provider did not expose a SqlTransaction.");
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var sql = context.GetService<ISqlGenerationHelper>();
        var temporaryTableName = $"#nh_bulk_upsert_{Guid.NewGuid():N}";
        var temporaryIndexName = $"ix_nh_bulk_upsert_{Guid.NewGuid():N}";
        var sourceOrdinalColumnName = plan.GeneratedPrimaryKey is null
            ? null
            : $"__nh_source_ordinal_{Guid.NewGuid():N}";
        var temporaryTable = sql.DelimitIdentifier(temporaryTableName);
        var targetTable = sql.DelimitIdentifier(plan.TableName, plan.Schema);
        var columns = JoinColumns(sql, plan.InsertProperties);
        var stagingColumns = sourceOrdinalColumnName is null
            ? columns
            : $"{columns}, CAST(0 AS bigint) AS {sql.DelimitIdentifier(sourceOrdinalColumnName)}";

        await ExecuteCommandAsync(
            connection,
            sqlTransaction,
            $"SELECT TOP (0) {stagingColumns} INTO {temporaryTable} FROM {targetTable};",
            cancellationToken);

        using var reader = new BulkUpsertDataReader<TEntity>(
            entities,
            plan.InsertProperties,
            sourceOrdinalColumnName);
        using (var bulkCopy = new SqlBulkCopy(
                   connection,
                   SqlBulkCopyOptions.TableLock,
                   sqlTransaction)
               {
                   DestinationTableName = temporaryTable,
                   EnableStreaming = true
               })
        {
            foreach (var property in plan.InsertProperties)
            {
                bulkCopy.ColumnMappings.Add(property.ColumnName, property.ColumnName);
            }
            if (sourceOrdinalColumnName is not null)
            {
                bulkCopy.ColumnMappings.Add(sourceOrdinalColumnName, sourceOrdinalColumnName);
            }

            await bulkCopy.WriteToServerAsync(reader, cancellationToken);
        }

        await ExecuteCommandAsync(
            connection,
            sqlTransaction,
            $"CREATE UNIQUE INDEX {sql.DelimitIdentifier(temporaryIndexName)} ON {temporaryTable} ({JoinColumns(sql, plan.MatchProperties)});",
            cancellationToken);

        var mergeSql = BuildMergeSql(
            sql,
            targetTable,
            temporaryTable,
            plan,
            sourceOrdinalColumnName);
        var affected = plan.GeneratedPrimaryKey is null
            ? await ExecuteCommandAsync(
                connection,
                sqlTransaction,
                mergeSql,
                cancellationToken)
            : await ExecuteMergeAndHydrateAsync(
                connection,
                sqlTransaction,
                mergeSql,
                plan.GeneratedPrimaryKey,
                reader.StagedEntities,
                cancellationToken);

        await ExecuteCommandAsync(
            connection,
            sqlTransaction,
            $"DROP TABLE {temporaryTable};",
            cancellationToken);
        return affected;
    }

    private static string BuildMergeSql<TEntity>(
        ISqlGenerationHelper sql,
        string targetTable,
        string temporaryTable,
        BulkUpsertPlan<TEntity> plan,
        string? sourceOrdinalColumnName)
        where TEntity : class
    {
        var match = string.Join(
            " AND ",
            plan.MatchProperties.Select(property =>
                $"target.{sql.DelimitIdentifier(property.ColumnName)} = source.{sql.DelimitIdentifier(property.ColumnName)}"));
        var update = plan.UpdateProperties.Count == 0
            ? string.Empty
            : $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", plan.UpdateProperties.Select(property => $"target.{sql.DelimitIdentifier(property.ColumnName)} = source.{sql.DelimitIdentifier(property.ColumnName)}"))}\n";
        var insertColumns = JoinColumns(sql, plan.InsertProperties);
        var insertValues = string.Join(
            ", ",
            plan.InsertProperties.Select(property => $"source.{sql.DelimitIdentifier(property.ColumnName)}"));
        var output = plan.GeneratedPrimaryKey is null
            ? string.Empty
            : $"\nOUTPUT $action, source.{sql.DelimitIdentifier(sourceOrdinalColumnName!)}, inserted.{sql.DelimitIdentifier(plan.GeneratedPrimaryKey.ColumnName)}";

        return $"""
            MERGE {targetTable} WITH (HOLDLOCK) AS target
            USING {temporaryTable} AS source
            ON {match}
            {update}WHEN NOT MATCHED BY TARGET THEN
                INSERT ({insertColumns}) VALUES ({insertValues}){output};
            """;
    }

    private static async Task<int> ExecuteMergeAndHydrateAsync<TEntity>(
        SqlConnection connection,
        SqlTransaction transaction,
        string commandText,
        BulkUpsertProperty<TEntity> generatedPrimaryKey,
        IReadOnlyList<TEntity> stagedEntities,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await using var result = await command.ExecuteReaderAsync(cancellationToken);
        var affected = 0;
        while (await result.ReadAsync(cancellationToken))
        {
            affected++;
            if (!string.Equals(result.GetString(0), "INSERT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceOrdinal = checked((int)result.GetInt64(1));
            generatedPrimaryKey.SetProviderValue(stagedEntities[sourceOrdinal], result.GetValue(2));
        }

        return affected;
    }

    private static string JoinColumns<TEntity>(
        ISqlGenerationHelper sql,
        IEnumerable<BulkUpsertProperty<TEntity>> properties)
        where TEntity : class
    {
        return string.Join(", ", properties.Select(property => sql.DelimitIdentifier(property.ColumnName)));
    }

    private static async Task<int> ExecuteCommandAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
