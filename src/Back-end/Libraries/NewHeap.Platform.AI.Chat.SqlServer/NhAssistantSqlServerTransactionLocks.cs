using System.Data.Common;
using System.Globalization;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The SQL Server transaction lock: an exclusive <c>sp_getapplock</c> owned by the transaction, as
/// the platform's SQL Server repository provider takes it.
/// </summary>
internal sealed class NhAssistantSqlServerTransactionLocks : NhAssistantTransactionLocks
{
    protected override async Task<bool> TryAcquireAsync(
        DbConnection connection,
        DbTransaction transaction,
        string resourceName,
        int timeoutMilliseconds,
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
        lockTimeoutParameter.Value = timeoutMilliseconds;
        command.Parameters.Add(lockTimeoutParameter);

        // 0 and 1 grant the lock; -1 is a timeout, the other negative codes a cancelled or failed request.
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) >= 0;
    }
}
