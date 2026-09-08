using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NewHeap.Platform.AspNet.Common.DAL.Bulk;
using System.Data.Common;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.DAL;

public static class RepositoryBulkExtensions
{
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

        var provider = NhRepositoryProvider.GetRequired(repository.Context);
        var plan = BulkUpsertPlan<TEntity>.Create(repository.Context, matchOn);
        await using var transactionScope = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        try
        {
            var transaction = transactionScope.Transaction.DbContextTransaction.GetDbTransaction();
            var affected = await ExecuteProviderAsync(
                repository.Context,
                provider,
                plan,
                entities,
                transaction,
                hydrateMatchedPrimaryKeys: false,
                cancellationToken);

            await transactionScope.CommitAsync(cancellationToken);
            return affected;
        }
        catch
        {
            await transactionScope.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Immediately upserts the supplied roots and selected one-to-one or one-to-many dependents.
    /// Dependents match only on a single numeric or <see cref="Guid"/> primary key.
    /// </summary>
    /// <remarks>
    /// Select only principal-to-dependent navigations. A default store-generated dependent key inserts
    /// and is written back; a non-default store-generated key must match an existing row and updates it.
    /// Missing dependents are not deleted. Populated nested dependent navigations fail before SQL is issued.
    /// The complete graph operation uses one transaction and bypasses EF Core change tracking and SaveChanges.
    /// </remarks>
    /// <returns>The total number of inserted or updated root and dependent rows.</returns>
    /// <exception cref="NotSupportedException">A selected dependent contains a populated nested dependency, or the provider or relationship shape is unsupported.</exception>
    public static async Task<int> ExecuteUpsertAsync<TEntity, TMatch>(
        this IRepository<TEntity> repository,
        IEnumerable<TEntity> entities,
        Expression<Func<TEntity, TMatch>> matchOn,
        IReadOnlyCollection<Expression<Func<TEntity, object?>>> navigationSelectors,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(matchOn);
        ArgumentNullException.ThrowIfNull(navigationSelectors);

        var provider = NhRepositoryProvider.GetRequired(repository.Context);
        var navigations = BulkUpsertGraph.GetNavigations(repository.Context, navigationSelectors);
        var plan = BulkUpsertPlan<TEntity>.Create(repository.Context, matchOn);
        var roots = entities.Select(entity => entity
                ?? throw new InvalidOperationException("Bulk upsert input cannot contain null entities."))
            .ToList();
        BulkUpsertGraph.ValidateNoNestedDependencies(roots, navigations);

        await using var transactionScope = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        try
        {
            var transaction = transactionScope.Transaction.DbContextTransaction.GetDbTransaction();
            var affected = await ExecuteProviderAsync(
                repository.Context,
                provider,
                plan,
                roots,
                transaction,
                hydrateMatchedPrimaryKeys: navigations.Count > 0,
                cancellationToken);
            affected += await BulkUpsertGraph.ExecuteAsync(
                repository.Context,
                provider,
                roots,
                navigations,
                transaction,
                cancellationToken);

            await transactionScope.CommitAsync(cancellationToken);
            return affected;
        }
        catch
        {
            await transactionScope.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    internal static Task<int> ExecuteProviderAsync<TEntity>(
        DbContext context,
        NhRepositoryProvider provider,
        BulkUpsertPlan<TEntity> plan,
        IEnumerable<TEntity> entities,
        DbTransaction transaction,
        bool hydrateMatchedPrimaryKeys,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        return provider.ExecuteUpsertAsync(
            context, plan, entities, transaction, hydrateMatchedPrimaryKeys, cancellationToken);
    }
}
