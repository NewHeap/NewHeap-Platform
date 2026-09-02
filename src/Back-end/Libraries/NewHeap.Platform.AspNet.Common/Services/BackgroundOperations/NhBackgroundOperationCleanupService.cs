using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed record NhBackgroundOperationCleanupResult(
    int RedactedOperations,
    int RemovedEvents,
    int RemovedOperations);

/// <summary>
/// Redacts payload-derived data before audit rows expire and removes terminal
/// operations in bounded batches. A provider-specific transaction lock makes
/// one application instance the cleanup leader for each processor key.
/// </summary>
internal sealed class NhBackgroundOperationCleanupService : BackgroundService
{
    private static readonly NhBackgroundOperationStatus[] TerminalStatuses =
    [
        NhBackgroundOperationStatus.Succeeded,
        NhBackgroundOperationStatus.Failed,
        NhBackgroundOperationStatus.Cancelled,
        NhBackgroundOperationStatus.TimedOut
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly ILogger<NhBackgroundOperationCleanupService> _logger;

    public NhBackgroundOperationCleanupService(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions options,
        ILogger<NhBackgroundOperationCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
                await Task.Delay(_options.CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Background operation cleanup loop failed.");
                await Task.Delay(_options.CleanupInterval, stoppingToken);
            }
        }
    }

    internal async Task<NhBackgroundOperationCleanupResult> CleanupAsync(
        CancellationToken cancellationToken = default,
        DateTimeOffset? utcNow = null)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await repository.TryAcquireTransactionLockAsync(
                transaction,
                $"NhBackgroundOperation:Cleanup:{_options.ProcessorKey}",
                _options.TransactionLockTimeoutMilliseconds,
                cancellationToken))
        {
            return new NhBackgroundOperationCleanupResult(0, 0, 0);
        }

        var now = utcNow ?? DateTimeOffset.UtcNow;
        var payloadCutoff = now - _options.PayloadRetentionPeriod;
        var eventCutoff = now - _options.EventRetentionPeriod;

        var operationsToRedact = await repository.GetAll()
            .Where(x => x.ProcessorKey == _options.ProcessorKey)
            .Where(x => TerminalStatuses.Contains(x.Status))
            .Where(x => x.CompletedAt != null && x.CompletedAt <= payloadCutoff)
            .Where(x => x.SensitiveDataRedactedAt == null)
            .OrderBy(x => x.CompletedAt)
            .Take(_options.CleanupBatchSize)
            .ToListAsync(cancellationToken);

        if (operationsToRedact.Count > 0)
        {
            var redactedIds = operationsToRedact.Select(x => x.Id).ToArray();
            foreach (var operation in operationsToRedact)
            {
                operation.PayloadJson = "{}";
                operation.ProgressMessageArgumentsJson = null;
                operation.SensitiveDataRedactedAt = now;
            }

            var steps = await repository.GetDbSet<NhBackgroundOperationStep>()
                .Where(x => redactedIds.Contains(x.OperationId))
                .ToListAsync(cancellationToken);
            foreach (var step in steps)
            {
                step.TitleArgumentsJson = null;
                step.MessageArgumentsJson = null;
            }

            var events = await repository.GetDbSet<NhBackgroundOperationEvent>()
                .Where(x => redactedIds.Contains(x.OperationId))
                .ToListAsync(cancellationToken);
            foreach (var operationEvent in events)
            {
                operationEvent.MessageArgumentsJson = null;
            }

            var checkpoints = await repository.GetDbSet<NhBackgroundOperationCheckpoint>()
                .Where(x => redactedIds.Contains(x.OperationId))
                .ToListAsync(cancellationToken);
            repository.RemoveRange(checkpoints);
        }

        // Milestones remain available for notification reconciliation until the
        // operation itself expires. The durable snapshot remains authoritative
        // when non-milestone event deltas have aged out.
        var eventsToRemove = await repository.GetDbSet<NhBackgroundOperationEvent>()
            .Where(x => x.Operation != null && x.Operation.ProcessorKey == _options.ProcessorKey)
            .Where(x => !x.IsMilestone && x.CreationDateTime <= eventCutoff)
            .OrderBy(x => x.CreationDateTime)
            .Take(_options.CleanupBatchSize)
            .ToListAsync(cancellationToken);
        repository.RemoveRange(eventsToRemove);

        var succeededCutoff = now - _options.SucceededRetentionPeriod;
        var cancelledCutoff = now - _options.CancelledRetentionPeriod;
        var failedCutoff = now - _options.FailedRetentionPeriod;
        var operationsToRemove = await repository.GetAll()
            .Where(x => x.ProcessorKey == _options.ProcessorKey)
            .Where(x => !x.ChildOperations.Any())
            .Where(x => x.CompletedAt != null)
            .Where(x =>
                (x.Status == NhBackgroundOperationStatus.Succeeded && x.CompletedAt <= succeededCutoff)
                || (x.Status == NhBackgroundOperationStatus.Cancelled && x.CompletedAt <= cancelledCutoff)
                || ((x.Status == NhBackgroundOperationStatus.Failed || x.Status == NhBackgroundOperationStatus.TimedOut)
                    && x.CompletedAt <= failedCutoff))
            .OrderBy(x => x.CompletedAt)
            .Take(_options.CleanupBatchSize)
            .ToListAsync(cancellationToken);
        repository.RemoveRange(operationsToRemove);

        if (operationsToRedact.Count > 0 || eventsToRemove.Count > 0 || operationsToRemove.Count > 0)
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        stopwatch.Stop();
        NhBackgroundOperationMetrics.RecordCleanup(
            operationsToRedact.Count,
            eventsToRemove.Count,
            operationsToRemove.Count,
            stopwatch.Elapsed.TotalMilliseconds);
        _logger.LogInformation(
            "Background operation cleanup completed in {ElapsedMilliseconds} ms: {RedactedOperations} operations redacted, {RemovedEvents} events removed, {RemovedOperations} operations removed.",
            stopwatch.ElapsedMilliseconds,
            operationsToRedact.Count,
            eventsToRemove.Count,
            operationsToRemove.Count);
        return new NhBackgroundOperationCleanupResult(
            operationsToRedact.Count,
            eventsToRemove.Count,
            operationsToRemove.Count);
    }
}
