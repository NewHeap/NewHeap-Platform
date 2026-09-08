using System.Data.Common;
using System.Diagnostics;
using NewHeap.Platform.AspNet.Common.DAL.TransactionLocks;

namespace NewHeap.Platform.AspNet.Common.SqlServer;

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
