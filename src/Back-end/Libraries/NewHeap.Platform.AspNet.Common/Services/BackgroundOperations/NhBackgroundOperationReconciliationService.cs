using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed class NhBackgroundOperationReconciliationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly NhBackgroundOperationRegistry _registry;
    private readonly NhBackgroundOperationFanOutCoordinator _fanOutCoordinator;
    private readonly INhBackgroundOperationLiveUpdatePublisher _liveUpdates;
    private readonly INhBackgroundOperationNotificationProjector _notificationProjector;
    private readonly ILogger<NhBackgroundOperationReconciliationService> _logger;

    public NhBackgroundOperationReconciliationService(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions options,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        INhBackgroundOperationLiveUpdatePublisher liveUpdates,
        INhBackgroundOperationNotificationProjector notificationProjector,
        ILogger<NhBackgroundOperationReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _registry = registry;
        _fanOutCoordinator = fanOutCoordinator;
        _liveUpdates = liveUpdates;
        _notificationProjector = notificationProjector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
                await Task.Delay(_options.ReconciliationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Background operation reconciliation loop failed.");
                await Task.Delay(_options.ReconciliationInterval, stoppingToken);
            }
        }
    }

    internal async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await repository.TryAcquireTransactionLockAsync(
                transaction,
                $"NhBackgroundOperation:Reconciler:{_options.ProcessorKey}",
                _options.TransactionLockTimeoutMilliseconds,
                cancellationToken))
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - _options.StaleAttemptTimeout;
        var operations = await repository.GetAll()
            .Where(x => x.ProcessorKey == _options.ProcessorKey)
            .Where(x =>
                (x.Status == NhBackgroundOperationStatus.RetryScheduled && x.NextDispatchAt <= now)
                || (x.Status == NhBackgroundOperationStatus.CancelRequested && x.CurrentAttemptId == null)
                || (x.Status == NhBackgroundOperationStatus.Queued && x.LastModifiedDateTime <= staleBefore)
                || (x.Status == NhBackgroundOperationStatus.Running && (x.HeartbeatAt == null || x.HeartbeatAt <= staleBefore)))
            .OrderBy(x => x.LastModifiedDateTime)
            .Take(_options.ReconciliationBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var operation in operations)
        {
            if (operation.Status == NhBackgroundOperationStatus.RetryScheduled)
            {
                operation.Status = NhBackgroundOperationStatus.PendingDispatch;
                operation.NextDispatchAt = now;
                NhBackgroundOperationService.Touch(operation, now);
                continue;
            }
            if (operation.Status == NhBackgroundOperationStatus.CancelRequested && operation.CurrentAttemptId is null)
            {
                if (await repository.GetAll().AnyAsync(
                        x => x.ParentOperationId == operation.Id,
                        cancellationToken))
                {
                    continue;
                }

                operation.Status = NhBackgroundOperationStatus.Cancelled;
                operation.CompletedAt = now;
                operation.NextDispatchAt = null;
                NhBackgroundOperationService.Touch(operation, now);
                NhBackgroundOperationService.AppendEvent(operation,
                    NhBackgroundOperationEventType.StateChanged,
                    NhBackgroundOperationMessageSeverity.Warning,
                    "background-operation.cancelled",
                    null,
                    true);
                await NhBackgroundOperationEventRetention.TrimAsync(
                    repository,
                    operation,
                    _options,
                    cancellationToken);
                continue;
            }
            if (operation.Status == NhBackgroundOperationStatus.Queued)
            {
                operation.Status = NhBackgroundOperationStatus.PendingDispatch;
                operation.NextDispatchAt = now;
                operation.SchedulerJobId = null;
                NhBackgroundOperationService.Touch(operation, now);
                NhBackgroundOperationService.AppendEvent(operation,
                    NhBackgroundOperationEventType.RetryScheduled,
                    NhBackgroundOperationMessageSeverity.Warning,
                    "background-operation.dispatch-recovered",
                    null,
                    false);
                await NhBackgroundOperationEventRetention.TrimAsync(
                    repository,
                    operation,
                    _options,
                    cancellationToken);
                continue;
            }
            if (operation.Status != NhBackgroundOperationStatus.Running)
            {
                continue;
            }

            if (operation.CurrentAttemptId.HasValue)
            {
                var attempt = await repository.GetDbSet<NhBackgroundOperationAttempt>()
                    .SingleOrDefaultAsync(x => x.Id == operation.CurrentAttemptId.Value, cancellationToken);
                if (attempt is not null)
                {
                    attempt.Status = NhBackgroundOperationAttemptStatus.Abandoned;
                    attempt.CompletedAt = now;
                    attempt.RecoveryReason = "stale-heartbeat";
                    attempt.LastModifiedDateTime = now;
                    attempt.Version++;
                }
            }
            operation.CurrentAttemptId = null;
            operation.SchedulerJobId = null;
            operation.DispatchGeneration++;
            var descriptor = _registry.GetForOperationType(operation.OperationType);
            if (descriptor.Idempotency == NhBackgroundOperationIdempotency.NonIdempotent)
            {
                operation.Status = NhBackgroundOperationStatus.Failed;
                operation.CompletedAt = now;
                operation.FailureCode = "stale-non-idempotent-attempt";
                operation.FailureMessageKey = "background-operation.operator-recovery-required";
            }
            else
            {
                operation.Status = NhBackgroundOperationStatus.RetryScheduled;
                operation.NextDispatchAt = now;
            }
            NhBackgroundOperationService.Touch(operation, now);
            NhBackgroundOperationService.AppendEvent(operation,
                operation.Status == NhBackgroundOperationStatus.Failed
                    ? NhBackgroundOperationEventType.StateChanged
                    : NhBackgroundOperationEventType.RetryScheduled,
                NhBackgroundOperationMessageSeverity.Warning,
                operation.Status == NhBackgroundOperationStatus.Failed
                    ? "background-operation.operator-recovery-required"
                    : "background-operation.stale-attempt-recovered",
                null,
                true);
            await NhBackgroundOperationEventRetention.TrimAsync(
                repository,
                operation,
                _options,
                cancellationToken);
        }

        var pendingNotificationProjectionIds = _options.UserNotificationProjectionEnabled
            ? await repository.GetAll()
                .AsNoTracking()
                .Where(x => x.ProcessorKey == _options.ProcessorKey)
                .Where(x => x.ParentOperationId == null)
                .Where(x => x.Events.Any(operationEvent =>
                    operationEvent.IsMilestone
                    && !operationEvent.IsOperatorOnly
                    && operationEvent.Sequence > x.LastProjectedNotificationEventSequence))
                .OrderBy(x => x.LastModifiedDateTime)
                .Take(_options.ReconciliationBatchSize)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken)
            : [];
        if (operations.Count > 0)
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        var projectedOperationIds = new HashSet<Guid>();
        foreach (var operation in operations)
        {
            try
            {
                await _liveUpdates.PublishChangedAsync(operation.OwnerUserId,
                    new NhBackgroundOperationChangedMessage(
                        operation.Id,
                        operation.Version,
                        operation.LatestEventSequence,
                        operation.Status,
                        operation.DivisionId),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to publish reconciled operation {OperationId}.", operation.Id);
            }
            projectedOperationIds.Add(operation.Id);
            try
            {
                var projectionResult = await _notificationProjector.ProjectAsync(operation.Id, cancellationToken);
                if (!projectionResult.Success)
                {
                    _logger.LogWarning(
                        "Notification projection was rejected for reconciled operation {OperationId}: {@ProjectionErrors}",
                        operation.Id,
                        projectionResult.AllErrorMessages);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to project notification for reconciled operation {OperationId}.", operation.Id);
            }
        }
        foreach (var operationId in pendingNotificationProjectionIds.Where(x => !projectedOperationIds.Contains(x)))
        {
            try
            {
                var projectionResult = await _notificationProjector.ProjectAsync(operationId, cancellationToken);
                if (!projectionResult.Success)
                {
                    _logger.LogWarning(
                        "Notification reconciliation was rejected for operation {OperationId}: {@ProjectionErrors}",
                        operationId,
                        projectionResult.AllErrorMessages);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to reconcile notification for operation {OperationId}.", operationId);
            }
        }
        var reconciledFanOuts = await _fanOutCoordinator.ReconcileWaitingAsync(cancellationToken);
        NhBackgroundOperationMetrics.RecordReconciled(operations.Count + reconciledFanOuts);
        return operations.Count + reconciledFanOuts;
    }
}
