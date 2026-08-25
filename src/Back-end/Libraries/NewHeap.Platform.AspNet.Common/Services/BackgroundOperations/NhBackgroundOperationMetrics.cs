using System.Diagnostics.Metrics;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal static class NhBackgroundOperationMetrics
{
    internal const string MeterName = "NewHeap.Platform.BackgroundOperations";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Enqueued = Meter.CreateCounter<long>("nh.background_operations.enqueued");
    private static readonly Counter<long> AttemptsStarted = Meter.CreateCounter<long>("nh.background_operations.attempts.started");
    private static readonly Counter<long> AttemptsCompleted = Meter.CreateCounter<long>("nh.background_operations.attempts.completed");
    private static readonly Counter<long> Reconciled = Meter.CreateCounter<long>("nh.background_operations.reconciled");
    private static readonly Counter<long> CleanupRows = Meter.CreateCounter<long>("nh.background_operations.cleanup.rows");
    private static readonly Histogram<double> CleanupDuration = Meter.CreateHistogram<double>(
        "nh.background_operations.cleanup.duration",
        "ms");

    internal static void RecordEnqueued(string operationType, string queue)
    {
        Enqueued.Add(1, new("operation.type", operationType), new("queue", queue));
    }

    internal static void RecordAttemptStarted(string operationType)
    {
        AttemptsStarted.Add(1, new KeyValuePair<string, object?>("operation.type", operationType));
    }

    internal static void RecordAttemptCompleted(string operationType, NhBackgroundOperationStatus status)
    {
        AttemptsCompleted.Add(1,
            new("operation.type", operationType),
            new("status", status.ToString()));
    }

    internal static void RecordReconciled(int count)
    {
        Reconciled.Add(count);
    }

    internal static void RecordCleanup(
        int redactedOperations,
        int removedEvents,
        int removedOperations,
        double elapsedMilliseconds)
    {
        CleanupRows.Add(redactedOperations, new KeyValuePair<string, object?>("action", "redacted-operation"));
        CleanupRows.Add(removedEvents, new KeyValuePair<string, object?>("action", "removed-event"));
        CleanupRows.Add(removedOperations, new KeyValuePair<string, object?>("action", "removed-operation"));
        CleanupDuration.Record(elapsedMilliseconds);
    }
}
