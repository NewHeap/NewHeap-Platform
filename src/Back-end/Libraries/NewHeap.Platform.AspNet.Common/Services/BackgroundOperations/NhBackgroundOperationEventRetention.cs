using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal static class NhBackgroundOperationEventRetention
{
    internal static async Task TrimAsync(
        IRepository<NhBackgroundOperation> repository,
        NhBackgroundOperation operation,
        NhBackgroundOperationsOptions options,
        CancellationToken cancellationToken)
    {
        var addedEntries = repository.Context.ChangeTracker
            .Entries<NhBackgroundOperationEvent>()
            .Where(entry => entry.State == EntityState.Added
                            && entry.Entity.OperationId == operation.Id)
            .OrderBy(entry => entry.Entity.Sequence)
            .ToList();
        var eventSet = repository.GetDbSet<NhBackgroundOperationEvent>();
        var persistedCount = await eventSet.CountAsync(
            item => item.OperationId == operation.Id,
            cancellationToken);
        var excess = persistedCount + addedEntries.Count - options.MaxEventsPerOperation;
        if (excess <= 0)
        {
            return;
        }

        var persistedToRemove = await eventSet
            .Where(item => item.OperationId == operation.Id
                           && (!item.IsMilestone
                               || item.IsOperatorOnly
                               || item.Sequence <= operation.LastProjectedNotificationEventSequence))
            .OrderBy(item => item.Sequence)
            .Take(excess)
            .ToListAsync(cancellationToken);
        eventSet.RemoveRange(persistedToRemove);
        excess -= persistedToRemove.Count;

        if (excess <= 0)
        {
            return;
        }

        var addedToRemove = addedEntries
            .Where(entry => !entry.Entity.IsMilestone
                            || entry.Entity.IsOperatorOnly
                            || entry.Entity.Sequence <= operation.LastProjectedNotificationEventSequence)
            .Take(excess)
            .ToList();
        foreach (var entry in addedToRemove)
        {
            entry.State = EntityState.Detached;
            operation.Events.Remove(entry.Entity);
        }

        // Unprojected milestones are the durable notification source. They are
        // intentionally retained even when a projector outage temporarily puts
        // the event stream above the configured retention target.
    }
}
