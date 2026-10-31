using System.Data;
using System.Data.Common;
using System.Diagnostics;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseReadQueryExecutor
{
    public static async Task<DatabaseQueryResultResponse> ExecuteAsync(
        IDatabaseReadProvider provider,
        IDatabaseReadRequestHost host,
        DatabaseReadExecutionContext executionContext,
        string connectionString,
        string requestId,
        DatabaseReadRequest request,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var connection = await host.OpenVerifiedConnectionAsync(
            provider,
            executionContext,
            connectionString,
            requestId,
            limits,
            cancellationToken);

        await using var transaction = await provider.BeginReadOnlyTransactionAsync(
            connection,
            cancellationToken);

        try
        {
            await provider.ConfigureReadOnlyTransactionAsync(
                connection,
                transaction,
                limits,
                cancellationToken);

            executionContext.Enter(DatabaseReadExecutionStage.QueryExecution);
            await using var command = connection.CreateCommand();
            command.CommandText = request.Sql!;
            command.CommandTimeout = limits.TimeoutSeconds;
            command.Transaction = transaction;
            DatabaseReadParameterBinder.AddParameters(command, provider, request.Parameters ?? []);

            await using var reader = await ExecuteReaderAsync(command, cancellationToken);

            var columnSchema = reader.GetColumnSchema();
            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(index => new DatabaseReadColumnResponse
                {
                    Name = string.IsNullOrWhiteSpace(reader.GetName(index))
                        ? $"Column{index + 1}"
                        : reader.GetName(index),
                    ProviderType = reader.GetDataTypeName(index),
                    AllowsNull = columnSchema[index].AllowDBNull ?? true
                })
                .ToArray();
            var rows = new List<IReadOnlyList<object?>>();
            var truncatedCellCount = 0;
            var approximateOutputBytes = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= limits.MaximumRows)
                {
                    throw new DatabaseReadExpectedException(
                        "result-row-limit-exceeded",
                        $"The query returned more than the requested maximum of {limits.MaximumRows} rows. No partial result was returned. Narrow the query or request a permitted higher limit.",
                        DatabaseReadExitCode.PolicyRejected);
                }

                var row = new object?[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = await reader.IsDBNullAsync(index, cancellationToken)
                        ? null
                        : reader.GetValue(index);
                    row[index] = DatabaseReadValueFormatter.Format(
                        value,
                        limits.MaximumCellBytes,
                        out var cellWasTruncated);

                    if (cellWasTruncated)
                    {
                        truncatedCellCount++;
                    }
                }

                var rowBytes = DatabaseReadJson.Serialize(row).Length;
                if (approximateOutputBytes + rowBytes > limits.MaximumOutputBytes - 4096)
                {
                    throw new DatabaseReadExpectedException(
                        "result-output-limit-exceeded",
                        $"The query result exceeded the requested maximum output size of {limits.MaximumOutputBytes} bytes. No partial result was returned. Narrow the query or request a permitted higher limit.",
                        DatabaseReadExitCode.PolicyRejected);
                }

                approximateOutputBytes += rowBytes;
                rows.Add(row);
            }

            return new DatabaseQueryResultResponse
            {
                Columns = columns,
                Rows = rows,
                Truncated = false,
                TruncatedCellCount = truncatedCellCount
            };
        }
        finally
        {
            await host.RollBackAsync(connection, transaction, executionContext);
        }
    }

    /// <summary>
    /// A driver refuses a parameter value on the client, before the statement reaches the server,
    /// with one of these exceptions instead of a <see cref="DbException"/>. Their text can quote the
    /// value, so the caller gets what to check and not what the driver said. A value the tool cannot
    /// read at all was refused earlier with <c>invalid-parameter-value</c>.
    /// </summary>
    internal static async Task<DbDataReader> ExecuteReaderAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken);
        }
        catch (Exception exception) when (
            command.Parameters.Count > 0 &&
            exception is ArgumentException
                or FormatException
                or InvalidCastException
                or NotSupportedException
                or OverflowException)
        {
            throw new DatabaseReadExpectedException(
                "parameter-binding-failed",
                "The database driver refused a parameter value before the statement reached the server. Check the type and value of each parameter against what the SQL compares it with.",
                DatabaseReadExitCode.InvalidRequest);
        }
    }
}
