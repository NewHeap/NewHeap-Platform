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
        await using var connection = provider.CreateConnection(connectionString, requestId, limits);
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

            return await provider.ReadSchemaAsync(
                connection,
                transaction,
                request,
                limits,
                cancellationToken);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }
}
