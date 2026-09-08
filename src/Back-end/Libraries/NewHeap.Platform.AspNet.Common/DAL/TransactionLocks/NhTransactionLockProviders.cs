using System.Data.Common;

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
