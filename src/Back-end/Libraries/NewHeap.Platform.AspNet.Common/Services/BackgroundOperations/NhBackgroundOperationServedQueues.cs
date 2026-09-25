using NewHeap.Platform.Common.Utilities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

/// <summary>
/// The Hangfire queues on which this process places the background operations it
/// registers. The library-registered Hangfire server listens to them, and the dispatcher
/// only claims operations on them. Processes that share a <c>ProcessorKey</c> and database
/// but not Hangfire storage or queue names, such as developer machines with in-memory
/// storage, therefore never schedule each other's work into a queue nobody serves.
/// </summary>
internal sealed class NhBackgroundOperationServedQueues
{
    public NhBackgroundOperationServedQueues(
        NhBackgroundOperationRegistry registry,
        INhHangfireQueueNameResolver queueNameResolver)
    {
        Queues = registry.Descriptors
            .Select(descriptor => NhBackgroundOperationKeys.NormalizeQueueName(
                queueNameResolver.GetQueueName(descriptor.Queue)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> Queues { get; }
}
