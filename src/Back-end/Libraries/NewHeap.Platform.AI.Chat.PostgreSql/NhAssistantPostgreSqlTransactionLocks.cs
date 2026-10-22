using System.Data.Common;
using System.Diagnostics;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The PostgreSQL transaction lock: a transaction-level advisory lock on the 64-bit hash of the
/// resource name, tried until the timeout as the platform's PostgreSQL repository provider does.
/// </summary>
internal sealed class NhAssistantPostgreSqlTransactionLocks : NhAssistantTransactionLocks
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(25);

    protected override async Task<bool> TryAcquireAsync(
        DbConnection connection,
        DbTransaction transaction,
        string resourceName,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        var stopwatch = Stopwatch.StartNew();

        while (true)
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
    }
}
