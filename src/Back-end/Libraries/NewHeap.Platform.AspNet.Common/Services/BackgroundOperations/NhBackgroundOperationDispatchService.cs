using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed record NhBackgroundOperationDispatchClaim(
    Guid OperationId,
    int DispatchGeneration,
    string Queue,
    Guid OwnerUserId);

internal sealed class NhBackgroundOperationDispatchService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly INhBackgroundOperationLiveUpdatePublisher _liveUpdates;
    private readonly INhBackgroundOperationNotificationProjector _notificationProjector;
    private readonly NhBackgroundOperationFanOutCoordinator _fanOutCoordinator;
    private readonly ILogger<NhBackgroundOperationDispatchService> _logger;

    public NhBackgroundOperationDispatchService(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions options,
        INhBackgroundOperationLiveUpdatePublisher liveUpdates,
        INhBackgroundOperationNotificationProjector notificationProjector,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        ILogger<NhBackgroundOperationDispatchService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _liveUpdates = liveUpdates;
        _notificationProjector = notificationProjector;
        _fanOutCoordinator = fanOutCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await DispatchAvailableAsync(stoppingToken);
                if (dispatched == 0)
                {
                    await Task.Delay(_options.DispatchInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Background operation dispatch loop failed.");
                await Task.Delay(_options.DispatchInterval, stoppingToken);
            }
        }
    }

    internal async Task<int> DispatchAvailableAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < _options.DispatchBatchSize)
        {
            var claim = await TryClaimAsync(cancellationToken);
            if (claim is null)
            {
                break;
            }
            await ScheduleClaimAsync(claim, cancellationToken);
            count++;
        }
        return count;
    }

    private async Task<NhBackgroundOperationDispatchClaim?> TryClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await repository.TryAcquireTransactionLockAsync(
                transaction,
                $"NhBackgroundOperation:Dispatcher:{_options.ProcessorKey}",
                _options.TransactionLockTimeoutMilliseconds,
                cancellationToken))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var operation = await repository.GetAll()
            .Where(x => x.ProcessorKey == _options.ProcessorKey)
            .Where(x => (x.Status == NhBackgroundOperationStatus.PendingDispatch
                         || x.Status == NhBackgroundOperationStatus.RetryScheduled
                         || x.Status == NhBackgroundOperationStatus.WaitingForChildren
                         || x.Status == NhBackgroundOperationStatus.WaitingForSignal
                         || (x.Status == NhBackgroundOperationStatus.CancelRequested
                             && x.CurrentAttemptId == null))
                        && (x.NextDispatchAt == null || x.NextDispatchAt <= now))
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.CreationDateTime)
            .FirstOrDefaultAsync(cancellationToken);
        if (operation is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (operation.CancelRequestedAt.HasValue)
        {
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
            await repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            try
            {
                await _liveUpdates.PublishChangedAsync(
                    operation.OwnerUserId,
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
                _logger.LogWarning(exception, "Failed to publish cancelled operation {OperationId}.", operation.Id);
            }
            try
            {
                var projectionResult = await _notificationProjector.ProjectAsync(operation.Id, cancellationToken);
                if (!projectionResult.Success)
                {
                    _logger.LogWarning(
                        "Cancelled-operation notification projection was rejected for operation {OperationId}: {@ProjectionErrors}",
                        operation.Id,
                        projectionResult.AllErrorMessages);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to project cancelled operation {OperationId}.", operation.Id);
            }
            await _fanOutCoordinator.OperationChangedAsync(operation.Id, cancellationToken);
            return null;
        }

        operation.DispatchGeneration++;
        operation.Status = NhBackgroundOperationStatus.Queued;
        operation.SchedulerJobId = null;
        operation.NextDispatchAt = null;
        NhBackgroundOperationService.Touch(operation, now);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new NhBackgroundOperationDispatchClaim(
            operation.Id,
            operation.DispatchGeneration,
            operation.Queue,
            operation.OwnerUserId);
    }

    private async Task ScheduleClaimAsync(
        NhBackgroundOperationDispatchClaim claim,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var scheduler = scope.ServiceProvider.GetRequiredService<INhBackgroundOperationScheduler>();
            var scheduleResult = await scheduler.EnqueueAsync(
                claim.OperationId,
                claim.DispatchGeneration,
                claim.Queue,
                cancellationToken);
            await StoreSchedulerJobIdAsync(claim, scheduleResult.SchedulerJobId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Failed to schedule background operation {OperationId} generation {DispatchGeneration}.", claim.OperationId, claim.DispatchGeneration);
            await ReturnForDispatchAsync(claim, cancellationToken);
        }
    }

    private async Task StoreSchedulerJobIdAsync(
        NhBackgroundOperationDispatchClaim claim,
        string schedulerJobId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockOperationAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            return;
        }
        var operation = await repository.GetAll().SingleOrDefaultAsync(x => x.Id == claim.OperationId, cancellationToken);
        if (operation is null || operation.DispatchGeneration != claim.DispatchGeneration)
        {
            return;
        }
        operation.SchedulerJobId = schedulerJobId;
        NhBackgroundOperationService.Touch(operation, DateTimeOffset.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ReturnForDispatchAsync(
        NhBackgroundOperationDispatchClaim claim,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockOperationAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            return;
        }
        var operation = await repository.GetAll().SingleOrDefaultAsync(x => x.Id == claim.OperationId, cancellationToken);
        if (operation is null
            || operation.DispatchGeneration != claim.DispatchGeneration
            || operation.Status != NhBackgroundOperationStatus.Queued
            || operation.CurrentAttemptId.HasValue)
        {
            return;
        }

        operation.Status = NhBackgroundOperationStatus.PendingDispatch;
        operation.NextDispatchAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        operation.SchedulerJobId = null;
        NhBackgroundOperationService.Touch(operation, DateTimeOffset.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private Task<bool> LockOperationAsync(
        IRepository<NhBackgroundOperation> repository,
        INhDbTransactionScope transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return repository.TryAcquireTransactionLockAsync(
            transaction,
            $"NhBackgroundOperation:Operation:{operationId:N}",
            _options.TransactionLockTimeoutMilliseconds,
            cancellationToken);
    }
}
