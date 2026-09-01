using System.Data;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseSchemaReader
{
    public static async Task<DatabaseSchemaResultResponse> ExecuteAsync(
        IDatabaseReadProvider provider,
        string connectionString,
        string requestId,
        ResolvedDatabaseSchemaRequest request,
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

            if (request.Operation != DatabaseSchemaOperation.SearchAndDescribe)
            {
                return await provider.ReadSchemaAsync(
                    connection,
                    transaction,
                    request,
                    limits,
                    cancellationToken);
            }

            var searchResult = await provider.ReadSchemaAsync(
                connection,
                transaction,
                request with { Operation = DatabaseSchemaOperation.Search },
                limits,
                cancellationToken);
            if (searchResult.Truncated || searchResult.Objects is not [var singleObject])
            {
                return new DatabaseSchemaResultResponse
                {
                    Operation = "search-and-describe",
                    Objects = searchResult.Objects,
                    Truncated = searchResult.Truncated
                };
            }

            var describeResult = await provider.ReadSchemaAsync(
                connection,
                transaction,
                new ResolvedDatabaseSchemaRequest(
                    DatabaseSchemaOperation.Describe,
                    singleObject.Schema,
                    singleObject.Name,
                    null),
                limits,
                cancellationToken);

            return new DatabaseSchemaResultResponse
            {
                Operation = "search-and-describe",
                Objects = searchResult.Objects,
                Object = describeResult.Object,
                Truncated = false
            };
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }
}
