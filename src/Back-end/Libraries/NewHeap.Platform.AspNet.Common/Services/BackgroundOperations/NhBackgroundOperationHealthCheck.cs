using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

/// <summary>
/// The <c>nh-background-operations</c> readiness check. It is degraded while work is stuck: an
/// attempt, running or being cancelled, whose heartbeat is older than <c>StaleAttemptTimeout</c>, or
/// work that is overdue by the reconciler's definition of a stalled dispatch. Heartbeats and
/// modification times are on the system clock, due times on the application's clock (a registered
/// <see cref="TimeProvider"/>, otherwise the system clock). Work that waits for a due time
/// (<c>NotBefore</c>, a retry backoff, a signal wait's expiry or a fan-out recheck) is overdue only
/// when it has been due for <c>StaleAttemptTimeout</c> on the application's clock and has not changed
/// for as long on the system clock; a queued operation that no worker started is overdue by its last
/// change alone. Debounced or backed-off work and a jump of the application's clock therefore never
/// degrade the check. Each count stops at <see cref="MaxCounted"/>.
/// </summary>
internal sealed class NhBackgroundOperationHealthCheck : IHealthCheck
{
    /// <summary>
    /// The highest number each count reports, so the check reads a bounded number of rows however
    /// much work is behind; any count above zero degrades it.
    /// </summary>
    internal const int MaxCounted = 1_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly TimeProvider _dueClock;

    public NhBackgroundOperationHealthCheck(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions options,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _dueClock = timeProvider ?? TimeProvider.System;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
            // The reconciler's definitions: liveness and changes on the system clock, due times on
            // the application's clock. Work is overdue when it has been due for the stale timeout
            // on the application's clock (no due time counts as due since its last change) and
            // nothing changed it for as long on the system clock.
            var staleBefore = DateTimeOffset.UtcNow - _options.StaleAttemptTimeout;
            var dueStaleBefore = _dueClock.GetUtcNow() - _options.StaleAttemptTimeout;
            var query = repository.GetAll()
                .AsNoTracking()
                .Where(x => x.ProcessorKey == _options.ProcessorKey);
            var staleAttempts = await query
                .Where(x =>
                    (x.Status == NhBackgroundOperationStatus.Running
                     || (x.Status == NhBackgroundOperationStatus.CancelRequested && x.CurrentAttemptId != null))
                    && (x.HeartbeatAt == null || x.HeartbeatAt <= staleBefore))
                .Take(MaxCounted)
                .CountAsync(cancellationToken);
            var overdueDispatches = await query
                .Where(x =>
                    ((x.Status == NhBackgroundOperationStatus.PendingDispatch
                      || x.Status == NhBackgroundOperationStatus.RetryScheduled
                      || x.Status == NhBackgroundOperationStatus.WaitingForChildren
                      || x.Status == NhBackgroundOperationStatus.WaitingForSignal)
                     && (x.NextDispatchAt == null || x.NextDispatchAt <= dueStaleBefore)
                     && x.LastModifiedDateTime <= staleBefore)
                    || (x.Status == NhBackgroundOperationStatus.Queued
                        && x.LastModifiedDateTime <= staleBefore))
                .Take(MaxCounted)
                .CountAsync(cancellationToken);

            var data = new Dictionary<string, object>
            {
                ["processorKey"] = _options.ProcessorKey,
                ["staleAttempts"] = staleAttempts,
                ["overdueDispatches"] = overdueDispatches
            };
            return staleAttempts > 0 || overdueDispatches > 0
                ? HealthCheckResult.Degraded(
                    "Background operations require reconciliation.",
                    data: data)
                : HealthCheckResult.Healthy("Background operation processing is converged.", data);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(
                "Background operation persistence is unavailable.",
                exception);
        }
    }
}
