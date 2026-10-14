using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Services.Actors;

/// <summary>
/// Removes the background operations of a user or division, as the foreign keys of identity
/// hosting do for a user. It runs before the notifications module, because operations refer to
/// the user notifications they projected.
/// </summary>
internal sealed class NhBackgroundOperationRetentionParticipant : INhRetentionParticipant
{
    internal const int RetentionOrder = -100;

    private readonly IRepository<NhBackgroundOperation> _operations;

    public NhBackgroundOperationRetentionParticipant(IRepository<NhBackgroundOperation> operations)
    {
        _operations = operations;
    }

    public int Order => RetentionOrder;

    public Task<TaskResult> ForgetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return RemoveAsync(operation => operation.OwnerUserId == userId, cancellationToken);
    }

    public Task<TaskResult> ForgetDivisionAsync(Guid divisionId, CancellationToken cancellationToken)
    {
        return RemoveAsync(operation => operation.DivisionId == divisionId, cancellationToken);
    }

    private async Task<TaskResult> RemoveAsync(
        System.Linq.Expressions.Expression<Func<NhBackgroundOperation, bool>> selector,
        CancellationToken cancellationToken)
    {
        // A parent operation may only go once its fan-out children are gone, so the matching
        // operations are removed leaf first. The database removes attempts, steps, events,
        // checkpoints and idempotency records with each operation and releases its leases.
        while (true)
        {
            var removed = await _operations.GetAll()
                .Where(selector)
                .Where(operation => !operation.ChildOperations.Any())
                .ExecuteDeleteAsync(cancellationToken);
            if (removed == 0)
            {
                break;
            }
        }

        if (await _operations.GetAll().AnyAsync(selector, cancellationToken))
        {
            return TaskResult.Failed(
                "background-operation-retention-blocked",
                "background-operation.retention-blocked");
        }

        return TaskResult.Succeeded();
    }
}

/// <summary>
/// Removes a user's notifications and forgets who created outgoing notifications, as the foreign
/// keys of identity hosting do. Notifications have no division.
/// </summary>
internal sealed class NhNotificationRetentionParticipant : INhRetentionParticipant
{
    private readonly IRepository<NhUserNotification> _userNotifications;

    public NhNotificationRetentionParticipant(IRepository<NhUserNotification> userNotifications)
    {
        _userNotifications = userNotifications;
    }

    public int Order => 0;

    public async Task<TaskResult> ForgetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _userNotifications.GetAll()
            .Where(notification => notification.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        await _userNotifications.GetDbSet<NhNotification>()
            .Where(notification => notification.CreatedByUserId == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(notification => notification.CreatedByUserId, (Guid?)null),
                cancellationToken);

        return TaskResult.Succeeded();
    }

    public Task<TaskResult> ForgetDivisionAsync(Guid divisionId, CancellationToken cancellationToken)
    {
        return Task.FromResult(TaskResult.Succeeded());
    }
}
