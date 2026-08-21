using Microsoft.EntityFrameworkCore.Storage;
using NewHeap.Platform.AspNet.Common.DAL.Bulk;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.DAL;

public static class RepositoryBulkExtensions
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";

    /// <summary>
    /// Immediately inserts or updates the supplied entities through a provider-native bulk operation.
    /// This operation bypasses EF Core change tracking and does not require SaveChanges.
    /// </summary>
    /// <remarks>
    /// A single store-generated numeric or <see cref="Guid"/> primary key is written back to each
    /// inserted entity. Matched entities and other generated properties are not refreshed.
    /// </remarks>
    /// <returns>The number of inserted or updated rows.</returns>
    /// <exception cref="NotSupportedException">The configured database provider is not SQL Server or PostgreSQL.</exception>
    public static async Task<int> ExecuteUpsertAsync<TEntity, TMatch>(
        this IRepository<TEntity> repository,
        IEnumerable<TEntity> entities,
        Expression<Func<TEntity, TMatch>> matchOn,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(matchOn);

        var providerName = repository.Context.Database.ProviderName;
        if (providerName is not SqlServerProviderName and not DatabaseProviderConfigurationExtensions.PostgreSqlProviderName)
        {
            throw new NotSupportedException(
                $"Bulk upsert is not supported by database provider '{providerName ?? "unknown"}'. " +
                "Supported providers are SQL Server and PostgreSQL.");
        }

        var plan = BulkUpsertPlan<TEntity>.Create(repository.Context, matchOn);
        await using var transactionScope = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        try
        {
            var transaction = transactionScope.Transaction.DbContextTransaction.GetDbTransaction();
            var affected = providerName switch
            {
                SqlServerProviderName => await SqlServerBulkUpsertExecutor.ExecuteAsync(
                    repository.Context,
                    plan,
                    entities,
                    transaction,
                    cancellationToken),
                DatabaseProviderConfigurationExtensions.PostgreSqlProviderName =>
                    await PostgreSqlBulkUpsertExecutor.ExecuteAsync(
                        repository.Context,
                        plan,
                        entities,
                        transaction,
                        cancellationToken),
                _ => throw new NotSupportedException(
                    $"Bulk upsert is not supported by database provider '{providerName}'.")
            };

            await transactionScope.CommitAsync(cancellationToken);
            return affected;
        }
        catch
        {
            await transactionScope.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
