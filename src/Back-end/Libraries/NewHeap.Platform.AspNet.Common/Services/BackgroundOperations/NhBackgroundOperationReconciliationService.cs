using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed class NhBackgroundOperationReconciliationService : BackgroundService
{
    private const string DispatchRecoveredMessageKey = "background-operation.dispatch-recovered";
    private const string DispatchStalledMessageKey = "background-operation.dispatch-stalled";
    private const string OperatorRecoveryRequiredMessageKey = "background-operation.operator-recovery-required";
    private const string StaleAttemptRecoveredMessageKey = "background-operation.stale-attempt-recovered";
    private const string CancelledMessageKey = "background-operation.cancelled";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly NhBackgroundOperationRegistry _registry;
    private readonly NhBackgroundOperationFanOutCoordinator _fanOutCoordinator;
    private readonly INhBackgroundOperationLiveUpdatePublisher _liveUpdates;
    private readonly INhBackgroundOperationNotificationProjector _notificationProjector;
    private readonly ILogger<NhBackgroundOperationReconciliationService> _logger;
    private readonly TimeProvider _dueClock;

    public NhBackgroundOperationReconciliationService(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions options,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        INhBackgroundOperationLiveUpdatePublisher liveUpdates,
        INhBackgroundOperationNotificationProjector notificationProjector,
        ILogger<NhBackgroundOperationReconciliationService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _registry = registry;
        _fanOutCoordinator = fanOutCoordinator;
        _liveUpdates = liveUpdates;
        _notificationProjector = notificationProjector;
        _logger = logger;
        _dueClock = timeProvider ?? TimeProvider.System;
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
        var scheduler = scope.ServiceProvider.GetService<INhBackgroundOperationScheduler>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await repository.TryAcquireTransactionLockAsync(
                transaction,
                $"NhBackgroundOperation:Reconciler:{_options.ProcessorKey}",
                _options.TransactionLockTimeoutMilliseconds,
                cancellationToken))
        {
            return 0;
        }

        // Heartbeats and modification times are on the system clock, due times on the application's
        // clock: advancing a test clock makes waiting work due without making a running attempt stale.
        // A dispatch counts as stalled only when it has been due for the stale timeout on the
        // application's clock and nothing touched it for that long on the system clock, so a jump of
        // the application's clock never takes work that just became due for a stalled dispatch.
        // Without a due time an operation is due at once, as the dispatcher treats it. The readiness
        // check (NhBackgroundOperationHealthCheck) counts overdue work and stale attempts by the same
        // definitions; stale attempts, running or being cancelled, by NhBackgroundOperationStaleAttempts.
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - _options.StaleAttemptTimeout;
        var due = _dueClock.GetUtcNow();
        var dueStaleBefore = due - _options.StaleAttemptTimeout;
        var staleAttempt = NhBackgroundOperationStaleAttempts.Where(staleBefore);
        var processorOperations = repository.GetAll()
            .AsNoTracking()
            .Where(x => x.ProcessorKey == _options.ProcessorKey);
        var dueWork = processorOperations
            .Where(x =>
                (x.Status == NhBackgroundOperationStatus.RetryScheduled
                    && (x.NextDispatchAt == null || x.NextDispatchAt <= due))
                || (x.Status == NhBackgroundOperationStatus.CancelRequested && x.CurrentAttemptId == null)
                || (x.Status == NhBackgroundOperationStatus.Queued && x.LastModifiedDateTime <= staleBefore)
                || (x.Status == NhBackgroundOperationStatus.PendingDispatch
                    && (x.NextDispatchAt == null || x.NextDispatchAt <= dueStaleBefore)
                    && x.LastModifiedDateTime <= staleBefore))
            .Select(x => new { x.Id, x.LastModifiedDateTime });
        var staleAttempts = processorOperations
            .Where(staleAttempt)
            .Select(x => new { x.Id, x.LastModifiedDateTime });
        // The two sets never overlap: a cancellation without an attempt is due work, one with an
        // attempt counts only once that attempt is stale.
        var candidateOperationIds = await dueWork
            .Concat(staleAttempts)
            .OrderBy(x => x.LastModifiedDateTime)
            .Take(_options.ReconciliationBatchSize)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var hasStaleAttempt = staleAttempt.Compile();

        var operations = new List<NhBackgroundOperation>();
        var cancelledOperationIds = new List<Guid>();
        var waitingInBacklog = 0;
        var orphanedSchedulerJobIds = new List<string>();
        var servedQueues = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var operationId in candidateOperationIds)
        {
            if (!await repository.TryAcquireTransactionLockAsync(
                    transaction,
                    $"NhBackgroundOperation:Operation:{operationId:N}",
                    _options.TransactionLockTimeoutMilliseconds,
                    cancellationToken))
            {
                continue;
            }

            var operation = await repository.GetAll()
                .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
            // The row as it is now, under the operation's lock: a worker may have changed it since
            // the candidates were read.
            if (operation is null
                || operation.ProcessorKey != _options.ProcessorKey
                || !RequiresReconciliation(operation, staleBefore, due, dueStaleBefore, hasStaleAttempt))
            {
                continue;
            }

            if (operation.Status == NhBackgroundOperationStatus.RetryScheduled)
            {
                operation.Status = NhBackgroundOperationStatus.PendingDispatch;
                operation.NextDispatchAt = due;
                NhBackgroundOperationService.Touch(operation, now);
                operations.Add(operation);
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
                    CancelledMessageKey,
                    null,
                    true);
                await NhBackgroundOperationEventRetention.TrimAsync(
                    repository,
                    operation,
                    _options,
                    cancellationToken);
                operations.Add(operation);
                continue;
            }
            if (operation.Status is NhBackgroundOperationStatus.Queued or NhBackgroundOperationStatus.PendingDispatch)
            {
                if (await IsWaitingInServedBacklogAsync(scheduler, operation, servedQueues, cancellationToken))
                {
                    // A busy worker has not reached the job yet. Check again after the next
                    // stale interval without counting this as a stalled dispatch.
                    operation.LastModifiedDateTime = now;
                    waitingInBacklog++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(operation.SchedulerJobId))
                {
                    orphanedSchedulerJobIds.Add(operation.SchedulerJobId);
                }

                await RecoverStalledDispatchAsync(
                    repository,
                    scheduler,
                    operation,
                    servedQueues,
                    now,
                    due,
                    cancellationToken);
                await NhBackgroundOperationEventRetention.TrimAsync(
                    repository,
                    operation,
                    _options,
                    cancellationToken);
                operations.Add(operation);
                continue;
            }
            if (operation.Status is not (NhBackgroundOperationStatus.Running or NhBackgroundOperationStatus.CancelRequested))
            {
                continue;
            }

            // The attempt is stale: its worker stopped. It is abandoned and fenced off, so every
            // late write of that worker fails on its claim: no current attempt and a new dispatch
            // generation.
            var staleAttemptId = operation.CurrentAttemptId;
            if (staleAttemptId.HasValue)
            {
                var attempt = await repository.GetDbSet<NhBackgroundOperationAttempt>()
                    .SingleOrDefaultAsync(x => x.Id == staleAttemptId.Value, cancellationToken);
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
            if (operation.Status == NhBackgroundOperationStatus.CancelRequested)
            {
                if (await CompleteStaleCancellationAsync(repository, transaction, operation, staleAttemptId, now, cancellationToken))
                {
                    cancelledOperationIds.Add(operation.Id);
                }
                await NhBackgroundOperationEventRetention.TrimAsync(
                    repository,
                    operation,
                    _options,
                    cancellationToken);
                operations.Add(operation);
                continue;
            }

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
                operation.NextDispatchAt = due;
            }
            NhBackgroundOperationService.Touch(operation, now);
            NhBackgroundOperationService.AppendEvent(operation,
                operation.Status == NhBackgroundOperationStatus.Failed
                    ? NhBackgroundOperationEventType.StateChanged
                    : NhBackgroundOperationEventType.RetryScheduled,
                NhBackgroundOperationMessageSeverity.Warning,
                operation.Status == NhBackgroundOperationStatus.Failed
                    ? "background-operation.operator-recovery-required"
                    : StaleAttemptRecoveredMessageKey,
                null,
                true);
            await NhBackgroundOperationEventRetention.TrimAsync(
                repository,
                operation,
                _options,
                cancellationToken);
            operations.Add(operation);
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
        if (operations.Count > 0 || waitingInBacklog > 0)
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await DeleteOrphanedSchedulerJobsAsync(scheduler, orphanedSchedulerJobIds, cancellationToken);
        var projectedOperationIds = new HashSet<Guid>();
        foreach (var operation in operations)
        {
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
            projectedOperationIds.Add(operation.Id);
            try
            {
                await _liveUpdates.PublishToOwnerAsync(operation, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to publish reconciled operation {OperationId}.", operation.Id);
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
        // A cancellation finished for a stopped worker reaches the fan-out parent, as the worker's
        // own end of an attempt does.
        foreach (var operationId in cancelledOperationIds)
        {
            try
            {
                await _fanOutCoordinator.OperationChangedAsync(operationId, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to aggregate the fan-out parent of reconciled operation {OperationId}.", operationId);
            }
        }
        var reconciledFanOuts = await _fanOutCoordinator.ReconcileWaitingAsync(cancellationToken);
        NhBackgroundOperationMetrics.RecordReconciled(operations.Count + reconciledFanOuts);
        return operations.Count + reconciledFanOuts;
    }

    /// <summary>
    /// Handles an operation that no worker started within <c>StaleAttemptTimeout</c>: its
    /// Hangfire job was not picked up, or no dispatcher claimed it. Each round records an
    /// operator-only diagnosis; after <c>MaxDispatchRecoveries</c> consecutive rounds the
    /// operation fails and asks for operator recovery instead of cycling forever. A round touches
    /// the operation, so the next round waits at least <c>StaleAttemptTimeout</c> of real time,
    /// however far the application's clock moves in between.
    /// </summary>
    private async Task RecoverStalledDispatchAsync(
        IRepository<NhBackgroundOperation> repository,
        INhBackgroundOperationScheduler? scheduler,
        NhBackgroundOperation operation,
        Dictionary<string, bool?> servedQueues,
        DateTimeOffset now,
        DateTimeOffset due,
        CancellationToken cancellationToken)
    {
        var stalledStatus = operation.Status;
        var schedulerState = await GetSchedulerStateAsync(scheduler, operation.SchedulerJobId, cancellationToken);
        if (!servedQueues.TryGetValue(operation.Queue, out var queueServed))
        {
            queueServed = await IsQueueServedAsync(scheduler, operation.Queue, cancellationToken);
            servedQueues[operation.Queue] = queueServed;
        }
        var previousRecoveries = await CountConsecutiveDispatchRecoveriesAsync(repository, operation.Id, cancellationToken);
        var recovery = previousRecoveries + 1;
        var escalate = previousRecoveries >= _options.MaxDispatchRecoveries;

        operation.SchedulerJobId = null;
        if (escalate)
        {
            operation.Status = NhBackgroundOperationStatus.Failed;
            operation.CompletedAt = now;
            operation.NextDispatchAt = null;
            operation.FailureCode = "dispatch-stalled";
            operation.FailureMessageKey = OperatorRecoveryRequiredMessageKey;
        }
        else
        {
            operation.Status = NhBackgroundOperationStatus.PendingDispatch;
            operation.NextDispatchAt = due;
        }
        NhBackgroundOperationService.Touch(operation, now);

        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.Message,
            NhBackgroundOperationMessageSeverity.Warning,
            DispatchStalledMessageKey,
            new
            {
                queue = operation.Queue,
                status = stalledStatus.ToString(),
                schedulerState,
                queueServed,
                recovery,
                maxRecoveries = _options.MaxDispatchRecoveries
            },
            false);
        operation.Events[^1].IsOperatorOnly = true;

        if (escalate)
        {
            _logger.LogError(
                "Background operation {OperationId} failed because no worker started it: it stayed {Status} on queue {Queue} " +
                "for {Recoveries} reconciliation rounds (scheduler state {SchedulerState}; a worker serves the queue: {QueueServed}). " +
                "Check that a Hangfire server with access to the same storage listens to this queue.",
                operation.Id,
                stalledStatus,
                operation.Queue,
                previousRecoveries,
                schedulerState ?? "unknown",
                queueServed?.ToString() ?? "unknown");
            NhBackgroundOperationService.AppendEvent(operation,
                NhBackgroundOperationEventType.StateChanged,
                NhBackgroundOperationMessageSeverity.Error,
                OperatorRecoveryRequiredMessageKey,
                null,
                true);
            return;
        }

        _logger.LogWarning(
            "Background operation {OperationId} was not started by a worker: it stayed {Status} on queue {Queue} " +
            "(scheduler state {SchedulerState}; a worker serves the queue: {QueueServed}). Dispatching it again, " +
            "recovery {Recovery} of {MaxRecoveries}.",
            operation.Id,
            stalledStatus,
            operation.Queue,
            schedulerState ?? "unknown",
            queueServed?.ToString() ?? "unknown",
            recovery,
            _options.MaxDispatchRecoveries);
        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.RetryScheduled,
            NhBackgroundOperationMessageSeverity.Warning,
            DispatchRecoveredMessageKey,
            null,
            false);
    }

    /// <summary>
    /// True when the operation's Hangfire job is still enqueued or processing on a queue
    /// that an active worker serves, so it waits behind other work instead of being lost.
    /// </summary>
    private async Task<bool> IsWaitingInServedBacklogAsync(
        INhBackgroundOperationScheduler? scheduler,
        NhBackgroundOperation operation,
        Dictionary<string, bool?> servedQueues,
        CancellationToken cancellationToken)
    {
        if (operation.Status != NhBackgroundOperationStatus.Queued
            || string.IsNullOrWhiteSpace(operation.SchedulerJobId))
        {
            return false;
        }

        var schedulerState = await GetSchedulerStateAsync(scheduler, operation.SchedulerJobId, cancellationToken);
        if (!string.Equals(schedulerState, "Enqueued", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(schedulerState, "Processing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!servedQueues.TryGetValue(operation.Queue, out var queueServed))
        {
            queueServed = await IsQueueServedAsync(scheduler, operation.Queue, cancellationToken);
            servedQueues[operation.Queue] = queueServed;
        }
        return queueServed == true;
    }

    /// <summary>
    /// Counts the dispatch recoveries since the operation last made visible progress.
    /// Operator-only diagnostics do not interrupt the streak; any other event does.
    /// </summary>
    private async Task<int> CountConsecutiveDispatchRecoveriesAsync(
        IRepository<NhBackgroundOperation> repository,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var recentMessageKeys = await repository.GetDbSet<NhBackgroundOperationEvent>()
            .AsNoTracking()
            .Where(x => x.OperationId == operationId && !x.IsOperatorOnly)
            .OrderByDescending(x => x.Sequence)
            .Take(_options.MaxDispatchRecoveries)
            .Select(x => x.MessageKey)
            .ToListAsync(cancellationToken);
        return recentMessageKeys.TakeWhile(key => key == DispatchRecoveredMessageKey).Count();
    }

    private async Task<string?> GetSchedulerStateAsync(
        INhBackgroundOperationScheduler? scheduler,
        string? schedulerJobId,
        CancellationToken cancellationToken)
    {
        if (scheduler is null || string.IsNullOrWhiteSpace(schedulerJobId))
        {
            return null;
        }

        try
        {
            return (await scheduler.GetStateAsync(schedulerJobId, cancellationToken))?.Name;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not read the state of scheduler job {SchedulerJobId}.", schedulerJobId);
            return null;
        }
    }

    private async Task<bool?> IsQueueServedAsync(
        INhBackgroundOperationScheduler? scheduler,
        string queue,
        CancellationToken cancellationToken)
    {
        if (scheduler is null)
        {
            return null;
        }

        try
        {
            return await scheduler.IsQueueServedAsync(queue, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not determine whether a worker serves queue {Queue}.", queue);
            return null;
        }
    }

    /// <summary>
    /// Removes the Hangfire jobs of recovered dispatch generations. They can no longer
    /// start the operation; deleting them keeps queues and retry schedules clean.
    /// </summary>
    private async Task DeleteOrphanedSchedulerJobsAsync(
        INhBackgroundOperationScheduler? scheduler,
        IReadOnlyCollection<string> schedulerJobIds,
        CancellationToken cancellationToken)
    {
        if (scheduler is null)
        {
            return;
        }

        foreach (var schedulerJobId in schedulerJobIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await scheduler.DeleteAsync(schedulerJobId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "Could not delete orphaned scheduler job {SchedulerJobId}.", schedulerJobId);
            }
        }
    }

    /// <summary>
    /// Finishes the cancellation of an operation whose attempt went stale while it was being
    /// cancelled: its worker stopped before it saw the request. The operation ends as the worker
    /// would have ended it: cancelled, with the root step cancelled and the cancellation milestone
    /// that notification policies and live updates follow, after an operator-only note that a stale
    /// attempt was recovered. The leases the stale attempt held are released. A fan-out parent waits
    /// without an attempt until its children, which the request cancelled as well, have ended; the
    /// fan-out coordinator then ends it, as it ends every cancelled parent. Returns whether the
    /// operation ended now.
    /// </summary>
    private async Task<bool> CompleteStaleCancellationAsync(
        IRepository<NhBackgroundOperation> repository,
        INhDbTransactionScope transaction,
        NhBackgroundOperation operation,
        Guid? staleAttemptId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await ReleaseLeasesAsync(repository, transaction, operation.Id, staleAttemptId, now, cancellationToken);
        NhBackgroundOperationService.Touch(operation, now);
        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.Message,
            NhBackgroundOperationMessageSeverity.Warning,
            StaleAttemptRecoveredMessageKey,
            null,
            false,
            staleAttemptId);
        operation.Events[^1].IsOperatorOnly = true;
        if (await repository.GetAll().AnyAsync(x => x.ParentOperationId == operation.Id, cancellationToken))
        {
            return false;
        }

        operation.Status = NhBackgroundOperationStatus.Cancelled;
        operation.CompletedAt = now;
        operation.NextDispatchAt = null;
        operation.FailureCode = "cancelled";
        operation.FailureMessageKey = CancelledMessageKey;
        var root = await repository.GetDbSet<NhBackgroundOperationStep>()
            .SingleOrDefaultAsync(x => x.OperationId == operation.Id && x.ParentStepId == null && x.StepKey == "root", cancellationToken);
        if (root is not null)
        {
            root.Status = NhBackgroundOperationStepStatus.Cancelled;
            root.CompletedAt = now;
            root.LastModifiedDateTime = now;
            root.Version++;
        }
        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.StateChanged,
            NhBackgroundOperationMessageSeverity.Warning,
            CancelledMessageKey,
            null,
            true,
            staleAttemptId);
        _logger.LogWarning(
            "Background operation {OperationId} was cancelled after its attempt {AttemptId} stopped sending heartbeats while it was being cancelled.",
            operation.Id,
            staleAttemptId);
        return true;
    }

    /// <summary>
    /// Releases the leases a stale attempt still holds, each under its lease lock, so other work can
    /// take them before they expire. A lease that another worker holds now, or whose lock is busy,
    /// stays as it is; it expires by itself.
    /// </summary>
    private async Task ReleaseLeasesAsync(
        IRepository<NhBackgroundOperation> repository,
        INhDbTransactionScope transaction,
        Guid operationId,
        Guid? attemptId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attemptId is not { } staleAttemptId)
        {
            return;
        }

        var leases = repository.GetDbSet<NhBackgroundOperationLease>();
        var resourceKeys = await leases
            .AsNoTracking()
            .Where(x => x.OperationId == operationId && x.AttemptId == staleAttemptId)
            .Select(x => x.ResourceKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var resourceKey in resourceKeys)
        {
            if (!await repository.TryAcquireTransactionLockAsync(
                    transaction,
                    $"NhBackgroundOperation:Lease:{resourceKey}",
                    _options.TransactionLockTimeoutMilliseconds,
                    cancellationToken))
            {
                continue;
            }

            var held = await leases
                .Where(x => x.ResourceKey == resourceKey && x.OperationId == operationId && x.AttemptId == staleAttemptId)
                .ToListAsync(cancellationToken);
            foreach (var lease in held)
            {
                lease.OperationId = null;
                lease.AttemptId = null;
                lease.HeartbeatAt = now;
                lease.ExpiresAt = now;
                lease.Version++;
            }
        }
    }

    private static bool RequiresReconciliation(
        NhBackgroundOperation operation,
        DateTimeOffset staleBefore,
        DateTimeOffset due,
        DateTimeOffset dueStaleBefore,
        Func<NhBackgroundOperation, bool> hasStaleAttempt)
    {
        return (operation.Status == NhBackgroundOperationStatus.RetryScheduled
                && (operation.NextDispatchAt is null || operation.NextDispatchAt <= due))
            || (operation.Status == NhBackgroundOperationStatus.CancelRequested
                && operation.CurrentAttemptId is null)
            || (operation.Status == NhBackgroundOperationStatus.Queued
                && operation.LastModifiedDateTime <= staleBefore)
            || (operation.Status == NhBackgroundOperationStatus.PendingDispatch
                && (operation.NextDispatchAt is null || operation.NextDispatchAt <= dueStaleBefore)
                && operation.LastModifiedDateTime <= staleBefore)
            || hasStaleAttempt(operation);
    }
}
