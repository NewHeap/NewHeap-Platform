using System.Linq.Expressions;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

/// <summary>
/// When the current attempt of an operation is stale: the operation runs, or is being cancelled
/// while an attempt still holds it, and that attempt's heartbeat is missing or not newer than the
/// stale timeout on the system clock. A worker that stopped, crashed or lost its database
/// connection leaves such an attempt behind. Reconciliation recovers it
/// (<see cref="NhBackgroundOperationReconciliationService"/>): a running operation is retried or, when
/// it is not idempotent, failed; an operation being cancelled is cancelled. The readiness check
/// (<see cref="NhBackgroundOperationHealthCheck"/>) counts the same operations until then. Liveness
/// stays on the system clock, so a jump of the application's clock never makes an attempt stale.
/// </summary>
internal static class NhBackgroundOperationStaleAttempts
{
    /// <summary>
    /// The operations whose current attempt is stale, as a translatable predicate.
    /// </summary>
    /// <param name="staleBefore">The system time before which a heartbeat is stale: now minus
    /// <c>StaleAttemptTimeout</c>.</param>
    internal static Expression<Func<NhBackgroundOperation, bool>> Where(DateTimeOffset staleBefore)
    {
        return operation =>
            (operation.Status == NhBackgroundOperationStatus.Running
             || (operation.Status == NhBackgroundOperationStatus.CancelRequested && operation.CurrentAttemptId != null))
            && (operation.HeartbeatAt == null || operation.HeartbeatAt <= staleBefore);
    }
}
