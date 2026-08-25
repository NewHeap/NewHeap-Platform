using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed class NhBackgroundOperationHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _options;

    public NhBackgroundOperationHealthCheck(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions options)
    {
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
            var now = DateTimeOffset.UtcNow;
            var staleBefore = now - _options.StaleAttemptTimeout;
            var undispatchedBefore = now - TimeSpan.FromTicks(Math.Max(
                _options.StaleAttemptTimeout.Ticks,
                _options.DispatchInterval.Ticks * 10));
            var query = repository.GetAll()
                .AsNoTracking()
                .Where(x => x.ProcessorKey == _options.ProcessorKey);
            var staleAttempts = await query.CountAsync(x =>
                (x.Status == NhBackgroundOperationStatus.Running
                 || (x.Status == NhBackgroundOperationStatus.CancelRequested && x.CurrentAttemptId != null))
                && (x.HeartbeatAt == null || x.HeartbeatAt <= staleBefore), cancellationToken);
            var overdueDispatches = await query.CountAsync(x =>
                ((x.Status == NhBackgroundOperationStatus.PendingDispatch
                  || x.Status == NhBackgroundOperationStatus.RetryScheduled
                  || x.Status == NhBackgroundOperationStatus.WaitingForChildren
                  || x.Status == NhBackgroundOperationStatus.Queued)
                 && x.LastModifiedDateTime <= undispatchedBefore)
                || (x.Status == NhBackgroundOperationStatus.WaitingForSignal
                    && x.NextDispatchAt <= undispatchedBefore), cancellationToken);

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
