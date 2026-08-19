using System.Data.Common;
using System.Diagnostics;

namespace NewHeap.Platform.AspNet.Common.DAL.TransactionLocks;

internal interface INhTransactionLockProvider
{
    Task<bool> TryAcquireAsync(
        DbConnection connection,
        DbTransaction transaction,
        string resourceName,
        int lockTimeoutInMilliseconds,
        CancellationToken cancellationToken);
}

internal static class NhTransactionLockProviderFactory
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static readonly INhTransactionLockProvider SqlServer = new SqlServerNhTransactionLockProvider();
    private static readonly INhTransactionLockProvider PostgreSql = new PostgreSqlNhTransactionLockProvider();

    internal static INhTransactionLockProvider Create(string? providerName)
    {
        return providerName switch
        {
            SqlServerProviderName => SqlServer,
            PostgreSqlProviderName => PostgreSql,
            _ => throw new NotSupportedException(
                $"Transaction locks are not supported for Entity Framework provider '{providerName ?? "unknown"}'.")
        };
    }
}

internal sealed class SqlServerNhTransactionLockProvider : INhTransactionLockProvider
{
    async Task<bool> INhTransactionLockProvider.TryAcquireAsync(
        DbConnection connection,
        DbTransaction transaction,
        string resourceName,
        int lockTimeoutInMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              DECLARE @result int;
                              EXEC @result = sp_getapplock
                                  @Resource = @resource,
                                  @LockMode = 'Exclusive',
                                  @LockOwner = 'Transaction',
                                  @LockTimeout = @lockTimeout;
                              SELECT @result;
                              """;

        var resourceParameter = command.CreateParameter();
        resourceParameter.ParameterName = "@resource";
        resourceParameter.Value = resourceName;
        command.Parameters.Add(resourceParameter);

        var lockTimeoutParameter = command.CreateParameter();
        lockTimeoutParameter.ParameterName = "@lockTimeout";
        lockTimeoutParameter.Value = Math.Max(0, lockTimeoutInMilliseconds);
        command.Parameters.Add(lockTimeoutParameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) >= 0;
    }
}

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
