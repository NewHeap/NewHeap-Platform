using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The platform's transaction lock for the assistant's own context: an exclusive lock on a named
/// resource that the database releases when the transaction that took it ends. Background
/// operations and notifications take the same lock through
/// <c>IRepository.TryAcquireTransactionLockAsync</c> (<c>sp_getapplock</c> on SQL Server,
/// <c>pg_try_advisory_xact_lock</c> on PostgreSQL). The assistant's context does not use the NewHeap
/// repository providers, so its storage packages supply the lock with their registration.
/// </summary>
internal abstract class NhAssistantTransactionLocks
{
    /// <summary>
    /// The longest resource name; SQL Server accepts no longer application lock names.
    /// </summary>
    public const int MaxResourceNameLength = 255;

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the lock on <paramref name="resourceName"/> in the
    /// current transaction of <paramref name="context"/>. Returns <see langword="false"/> when another
    /// transaction still holds the lock after the timeout.
    /// </summary>
    public Task<bool> TryAcquireAsync(
        DbContext context,
        string resourceName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        if (resourceName.Length > MaxResourceNameLength)
        {
            throw new ArgumentException(
                $"An assistant lock resource name has at most {MaxResourceNameLength} characters.",
                nameof(resourceName));
        }

        var transaction = context.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "An assistant transaction lock is taken in a transaction, which releases it when it ends.");
        var timeoutMilliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

        return TryAcquireAsync(
            context.Database.GetDbConnection(),
            transaction.GetDbTransaction(),
            resourceName,
            timeoutMilliseconds,
            cancellationToken);
    }

    /// <summary>
    /// Takes the lock with the provider's own SQL on the connection of <paramref name="transaction"/>.
    /// </summary>
    protected abstract Task<bool> TryAcquireAsync(
        DbConnection connection,
        DbTransaction transaction,
        string resourceName,
        int timeoutMilliseconds,
        CancellationToken cancellationToken);
}
