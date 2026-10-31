using System.Data.Common;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseSchemaReader
{
    public static async Task<DatabaseSchemaResultResponse> ExecuteAsync(
        IDatabaseReadProvider provider,
        IDatabaseReadRequestHost host,
        DatabaseReadExecutionContext executionContext,
        string connectionString,
        string requestId,
        ResolvedDatabaseSchemaRequest request,
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

            executionContext.Enter(DatabaseReadExecutionStage.SchemaExecution);
            if (request.Operation == DatabaseSchemaOperation.Catalog)
            {
                return await ReadCatalogAsync(
                    provider,
                    connection,
                    transaction,
                    request,
                    limits,
                    host.TimeProvider,
                    cancellationToken);
            }

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
            await host.RollBackAsync(connection, transaction, executionContext);
        }
    }

    /// <summary>
    /// Describes every selectable object in this connection and transaction, in the order of a
    /// search and up to its row limit. The search's statements each keep their own timeout, so the
    /// request's timeout also bounds the catalog as a whole: objects that no longer fit that time
    /// or the output limit are left out and the catalog reports itself as truncated. Each hash is
    /// the one <c>describe</c> reports for that object, so a caller can compare the two.
    /// </summary>
    private static async Task<DatabaseSchemaResultResponse> ReadCatalogAsync(
        IDatabaseReadProvider provider,
        DbConnection connection,
        DbTransaction transaction,
        ResolvedDatabaseSchemaRequest request,
        DatabaseReadLimits limits,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(limits.TimeoutSeconds);
        var searchResult = await provider.ReadSchemaAsync(
            connection,
            transaction,
            request with { Operation = DatabaseSchemaOperation.Search },
            limits,
            cancellationToken);

        var objects = new List<DatabaseSchemaCatalogObjectResponse>(searchResult.Objects!.Count);
        var truncated = searchResult.Truncated;
        var outputBytes = 0;

        foreach (var summary in searchResult.Objects)
        {
            if (timeProvider.GetElapsedTime(started) >= timeout)
            {
                truncated = true;
                break;
            }

            var describeResult = await provider.ReadSchemaAsync(
                connection,
                transaction,
                new ResolvedDatabaseSchemaRequest(
                    DatabaseSchemaOperation.Describe,
                    summary.Schema,
                    summary.Name,
                    null),
                limits,
                cancellationToken);
            var catalogObject = new DatabaseSchemaCatalogObjectResponse
            {
                EvidenceHash = DatabaseReadJson.ComputeEvidenceHash(describeResult),
                Object = describeResult.Object!
            };

            outputBytes += DatabaseReadJson.Serialize(catalogObject).Length;
            if (outputBytes > limits.MaximumOutputBytes)
            {
                truncated = true;
                break;
            }

            objects.Add(catalogObject);
        }

        return new DatabaseSchemaResultResponse
        {
            Operation = "catalog",
            CatalogObjects = objects,
            Truncated = truncated
        };
    }
}
