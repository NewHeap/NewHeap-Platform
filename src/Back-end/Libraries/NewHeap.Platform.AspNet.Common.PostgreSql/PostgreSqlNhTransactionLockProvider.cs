using System.Data.Common;
using System.Diagnostics;
using NewHeap.Platform.AspNet.Common.DAL.TransactionLocks;

namespace NewHeap.Platform.AspNet.Common.PostgreSql;

internal sealed class PostgreSqlNhTransactionLockProvider : INhTransactionLockProvider
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(25);

    async Task<bool> INhTransactionLockProvider.TryAcquireAsync(
        DbConnection connection,
        DbTransaction transaction,
        string resourceName,
        int lockTimeoutInMilliseconds,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(0, lockTimeoutInMilliseconds));
        var stopwatch = Stopwatch.StartNew();

        do
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT pg_try_advisory_xact_lock(hashtextextended(@resource, 0));";

            var resourceParameter = command.CreateParameter();
            resourceParameter.ParameterName = "@resource";
            resourceParameter.Value = resourceName;
            command.Parameters.Add(resourceParameter);

            if (Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)))
            {
                return true;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(
                remaining < RetryInterval ? remaining : RetryInterval,
                cancellationToken);
        }
        while (stopwatch.Elapsed < timeout);

        return false;
    }
}
