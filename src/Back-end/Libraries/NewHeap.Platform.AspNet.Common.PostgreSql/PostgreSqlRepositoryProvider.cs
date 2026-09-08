using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Bulk;
using NewHeap.Platform.AspNet.Common.DAL.TransactionLocks;
using System.Data.Common;

namespace NewHeap.Platform.AspNet.Common.PostgreSql;

internal sealed class PostgreSqlRepositoryProvider : NhRepositoryProvider
{
    internal static readonly PostgreSqlRepositoryProvider Instance = new();
    private readonly INhTransactionLockProvider _locks = new PostgreSqlNhTransactionLockProvider();

    internal override string ProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    internal override Task<int> ExecuteUpsertAsync<TEntity>(
        DbContext context,
        BulkUpsertPlan<TEntity> plan,
        IEnumerable<TEntity> entities,
        DbTransaction transaction,
        bool hydrateMatchedPrimaryKeys,
        CancellationToken cancellationToken)
    {
        return PostgreSqlBulkUpsertExecutor.ExecuteAsync(
            context, plan, entities, transaction, hydrateMatchedPrimaryKeys, cancellationToken);
    }

    internal override Task<bool> TryAcquireTransactionLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        string resourceName,
        int lockTimeoutInMilliseconds,
        CancellationToken cancellationToken)
    {
        return _locks.TryAcquireAsync(
            connection, transaction, resourceName, lockTimeoutInMilliseconds, cancellationToken);
    }
}
