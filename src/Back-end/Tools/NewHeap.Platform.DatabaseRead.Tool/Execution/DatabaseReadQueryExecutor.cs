using System.Data;
using System.Data.Common;
using System.Diagnostics;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseReadQueryExecutor
{
    public static async Task<DatabaseQueryResultResponse> ExecuteAsync(
        IDatabaseReadProvider provider,
        string connectionString,
        string requestId,
        DatabaseReadRequest request,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var connection = DatabaseReadConnectionFactory.Create(
            provider,
            connectionString,
            requestId,
            limits);
        await connection.OpenAsync(cancellationToken);

        if (!await provider.VerifyReadOnlyPrincipalAsync(connection, limits, cancellationToken))
        {
            throw new DatabaseReadExpectedException(
                "read-only-principal-not-verified",
                "The database principal has write, DDL or elevated permissions. Use a dedicated read-only credential.",
                DatabaseReadExitCode.PolicyRejected);
        }

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            await provider.ConfigureReadOnlyTransactionAsync(
                connection,
                transaction,
                limits,
                cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = request.Sql!;
            command.CommandTimeout = limits.TimeoutSeconds;
            command.Transaction = transaction;
            DatabaseReadParameterBinder.AddParameters(command, request.Parameters ?? []);

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken);

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
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }
}
