using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed record NhBackgroundOperationAttemptClaim(
    Guid OperationId,
    Guid AttemptId,
    int AttemptNumber,
    int DispatchGeneration,
    long FencingToken,
    string OperationType,
    int PayloadSchemaVersion,
    string PayloadJson,
    string Queue,
    string? ConcurrencyKey,
    Guid OwnerUserId);

internal enum NhBackgroundOperationHeartbeatResult
{
    Continue,
    CancellationRequested,
    OwnershipLost
}

internal sealed partial class NhBackgroundOperationPersistence
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly INhBackgroundOperationLiveUpdatePublisher _liveUpdates;
    private readonly INhBackgroundOperationNotificationProjector _notificationProjector;
    private readonly NhBackgroundOperationFanOutCoordinator _fanOutCoordinator;
    private readonly ILogger<NhBackgroundOperationPersistence> _logger;

    public NhBackgroundOperationPersistence(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions options,
        INhBackgroundOperationLiveUpdatePublisher liveUpdates,
        INhBackgroundOperationNotificationProjector notificationProjector,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        ILogger<NhBackgroundOperationPersistence> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _liveUpdates = liveUpdates;
        _notificationProjector = notificationProjector;
        _fanOutCoordinator = fanOutCoordinator;
        _logger = logger;
    }

    internal async Task<NhBackgroundOperationAttemptClaim?> TryStartAttemptAsync(
        Guid operationId,
        int dispatchGeneration,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, operationId, cancellationToken))
        {
            return null;
        }

        var operation = await repository.GetAll()
            .Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
        if (operation is null || operation.DispatchGeneration != dispatchGeneration)
        {
            return null;
        }

        if (operation.Status == NhBackgroundOperationStatus.CancelRequested)
        {
            await CompleteCancellationBeforeStartAsync(
                repository,
                operation,
                _options,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await PublishSafelyAsync(operation, cancellationToken);
            return null;
        }
        if (operation.Status != NhBackgroundOperationStatus.Queued || operation.CurrentAttemptId.HasValue)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var attempt = new NhBackgroundOperationAttempt
        {
            Id = Guid.NewGuid(),
            OperationId = operation.Id,
            AttemptNumber = operation.CurrentAttemptNumber + 1,
            DispatchGeneration = dispatchGeneration,
            SchedulerJobId = operation.SchedulerJobId,
            Status = NhBackgroundOperationAttemptStatus.Running,
            WorkerId = $"{Environment.MachineName}:{Environment.ProcessId}",
            StartedAt = now,
            HeartbeatAt = now,
            Version = 1
        };
        operation.Attempts.Add(attempt);
        operation.CurrentAttemptId = attempt.Id;
        operation.CurrentAttemptNumber = attempt.AttemptNumber;
        operation.Status = NhBackgroundOperationStatus.Running;
        operation.StartedAt ??= now;
        operation.HeartbeatAt = now;
        var root = operation.Steps.Single(x => x.ParentStepId == null && x.StepKey == "root");
        root.Status = NhBackgroundOperationStepStatus.Running;
        root.StartedAt ??= now;
        root.HeartbeatAt = now;
        root.CurrentAttemptId = attempt.Id;
        root.FencingVersion++;
        root.Version++;
        root.LastModifiedDateTime = now;
        NhBackgroundOperationService.Touch(operation, now);
        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.StateChanged,
            NhBackgroundOperationMessageSeverity.Information,
            "background-operation.started",
            new { attempt = attempt.AttemptNumber },
            true,
            attempt.Id);
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            operation,
            _options,
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        NhBackgroundOperationMetrics.RecordAttemptStarted(operation.OperationType);
        _logger.LogInformation(
            "Background operation {OperationId} attempt {AttemptNumber} started for {OperationType} on queue {Queue}.",
            operation.Id,
            attempt.AttemptNumber,
            operation.OperationType,
            operation.Queue);
        await PublishSafelyAsync(operation, cancellationToken);
        return new NhBackgroundOperationAttemptClaim(
            operation.Id,
            attempt.Id,
            attempt.AttemptNumber,
            dispatchGeneration,
            root.FencingVersion,
            operation.OperationType,
            operation.PayloadSchemaVersion,
            operation.PayloadJson,
            operation.Queue,
            operation.ConcurrencyKey,
            operation.OwnerUserId);
    }

    internal async Task<NhBackgroundOperationHeartbeatResult> HeartbeatAsync(
        NhBackgroundOperationAttemptClaim claim,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }

        var operation = await repository.GetAll().SingleOrDefaultAsync(x => x.Id == claim.OperationId, cancellationToken);
        if (operation is null
            || operation.CurrentAttemptId != claim.AttemptId
            || operation.DispatchGeneration != claim.DispatchGeneration
            || operation.Status is not (NhBackgroundOperationStatus.Running or NhBackgroundOperationStatus.CancelRequested))
        {
            return NhBackgroundOperationHeartbeatResult.OwnershipLost;
        }

        var now = DateTimeOffset.UtcNow;
        operation.HeartbeatAt = now;
        var attempt = await repository.GetDbSet<NhBackgroundOperationAttempt>()
            .SingleAsync(x => x.Id == claim.AttemptId, cancellationToken);
        attempt.HeartbeatAt = now;
        attempt.LastModifiedDateTime = now;
        attempt.Version++;
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return operation.Status == NhBackgroundOperationStatus.CancelRequested
            ? NhBackgroundOperationHeartbeatResult.CancellationRequested
            : NhBackgroundOperationHeartbeatResult.Continue;
    }

    internal async Task CompleteAsync(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationStatus status,
        string? failureCode,
        string? failureMessageKey,
        string? diagnosticCorrelationId,
        DateTimeOffset? retryAt,
        CancellationToken cancellationToken,
        bool abandoned = false)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }

        var operation = await repository.GetAll()
            .Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == claim.OperationId, cancellationToken);
        if (operation is null
            || operation.CurrentAttemptId != claim.AttemptId
            || operation.DispatchGeneration != claim.DispatchGeneration)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var attempt = await repository.GetDbSet<NhBackgroundOperationAttempt>()
            .SingleAsync(x => x.Id == claim.AttemptId, cancellationToken);
        attempt.CompletedAt = now;
        attempt.LastModifiedDateTime = now;
        attempt.FailureCode = failureCode;
        attempt.DiagnosticCorrelationId = diagnosticCorrelationId;
        attempt.Status = abandoned
            ? NhBackgroundOperationAttemptStatus.Abandoned
            : status switch
        {
            NhBackgroundOperationStatus.Succeeded => NhBackgroundOperationAttemptStatus.Succeeded,
            NhBackgroundOperationStatus.Cancelled => NhBackgroundOperationAttemptStatus.Cancelled,
            _ => NhBackgroundOperationAttemptStatus.Failed
        };
        attempt.Version++;

        operation.CurrentAttemptId = null;
        operation.FailureCode = failureCode;
        operation.FailureMessageKey = failureMessageKey;
        operation.DiagnosticCorrelationId = diagnosticCorrelationId;
        operation.Status = retryAt.HasValue ? NhBackgroundOperationStatus.RetryScheduled : status;
        operation.NextDispatchAt = retryAt;
        operation.SchedulerJobId = null;
        if (!retryAt.HasValue)
        {
            operation.CompletedAt = now;
        }

        var root = operation.Steps.Single(x => x.ParentStepId == null && x.StepKey == "root");
        root.Status = retryAt.HasValue
            ? NhBackgroundOperationStepStatus.Pending
            : status switch
            {
                NhBackgroundOperationStatus.Succeeded => NhBackgroundOperationStepStatus.Succeeded,
                NhBackgroundOperationStatus.Cancelled => NhBackgroundOperationStepStatus.Cancelled,
                _ => NhBackgroundOperationStepStatus.Failed
            };
        root.CompletedAt = retryAt.HasValue ? null : now;
        if (status == NhBackgroundOperationStatus.Succeeded)
        {
            root.Percentage = 100;
            operation.ProgressPercentage = 100;
        }
        root.LastModifiedDateTime = now;
        root.Version++;
        NhBackgroundOperationService.Touch(operation, now);
        NhBackgroundOperationService.AppendEvent(operation,
            retryAt.HasValue ? NhBackgroundOperationEventType.RetryScheduled : NhBackgroundOperationEventType.StateChanged,
            status == NhBackgroundOperationStatus.Succeeded
                ? NhBackgroundOperationMessageSeverity.Success
                : status == NhBackgroundOperationStatus.Cancelled
                    ? NhBackgroundOperationMessageSeverity.Warning
                    : NhBackgroundOperationMessageSeverity.Error,
            retryAt.HasValue ? "background-operation.retry-scheduled" : $"background-operation.{status.ToString().ToLowerInvariant()}",
            retryAt.HasValue ? new { retryAt, attempt = claim.AttemptNumber } : null,
            true,
            claim.AttemptId);
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            operation,
            _options,
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        NhBackgroundOperationMetrics.RecordAttemptCompleted(operation.OperationType, operation.Status);
        if (operation.Status is NhBackgroundOperationStatus.Failed or NhBackgroundOperationStatus.TimedOut)
        {
            _logger.LogWarning(
                "Background operation {OperationId} attempt {AttemptNumber} completed with status {Status}.",
                operation.Id,
                attempt.AttemptNumber,
                operation.Status);
        }
        else
        {
            _logger.LogInformation(
                "Background operation {OperationId} attempt {AttemptNumber} completed with status {Status}.",
                operation.Id,
                attempt.AttemptNumber,
                operation.Status);
        }
        await PublishSafelyAsync(operation, cancellationToken);
    }

    internal async Task<int> GetFailedAttemptCountAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperationAttempt>>();
        return await repository.GetAll().AsNoTracking().CountAsync(
            x => x.OperationId == operationId && x.Status == NhBackgroundOperationAttemptStatus.Failed,
            cancellationToken);
    }

    internal async Task<bool> IsCancellationRequestedAsync(
        NhBackgroundOperationAttemptClaim claim,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        return await repository.GetAll().AsNoTracking().AnyAsync(
            x => x.Id == claim.OperationId
                 && x.CurrentAttemptId == claim.AttemptId
                 && x.Status == NhBackgroundOperationStatus.CancelRequested,
            cancellationToken);
    }

    internal Task ReportRootAsync(
        NhBackgroundOperationAttemptClaim claim,
        decimal current,
        decimal total,
        string? messageKey,
        object? arguments,
        CancellationToken cancellationToken)
    {
        return UpdateProgressAsync(
            claim,
            null,
            current,
            total,
            messageKey,
            arguments,
            cancellationToken);
    }

    internal Task ReportStepAsync(
        NhBackgroundOperationAttemptClaim claim,
        Guid stepId,
        decimal current,
        decimal total,
        string? messageKey,
        object? arguments,
        CancellationToken cancellationToken)
    {
        return UpdateProgressAsync(
            claim,
            stepId,
            current,
            total,
            messageKey,
            arguments,
            cancellationToken);
    }

    internal async Task DefinePlanAsync(
        NhBackgroundOperationAttemptClaim claim,
        IReadOnlyList<NhBackgroundOperationProgressPlanStep> plan,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }
        var operation = await LoadFencedOperationAsync(repository, claim, cancellationToken);
        var root = operation.Steps.Single(x => x.ParentStepId == null && x.StepKey == "root");
        if (root.Percentage > 0 || operation.Steps.Any(x => x.ParentStepId == root.Id && x.Status != NhBackgroundOperationStepStatus.Pending))
        {
            var existingPlan = operation.Steps
                .Where(x => x.ParentStepId == root.Id)
                .OrderBy(x => x.DisplayOrder)
                .ToList();
            var unchanged = existingPlan.Count == plan.Count
                            && plan.All(definition => existingPlan.Any(step =>
                                step.StepKey == definition.Key
                                && step.TitleKey == definition.TitleKey
                                && step.Weight == definition.Weight
                                && step.DisplayOrder == definition.DisplayOrder));
            if (!unchanged)
            {
                throw new InvalidOperationException("A visible progress plan cannot be redefined after progress has started.");
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }

        root.AggregationMode = NhBackgroundOperationAggregationMode.WeightedChildren;
        foreach (var definition in plan)
        {
            var step = operation.Steps.SingleOrDefault(x => x.ParentStepId == root.Id && x.StepKey == definition.Key);
            if (step is null)
            {
                operation.Steps.Add(new NhBackgroundOperationStep
                {
                    Id = Guid.NewGuid(),
                    OperationId = operation.Id,
                    ParentStepId = root.Id,
                    StepKey = definition.Key,
                    TitleKey = definition.TitleKey,
                    Weight = definition.Weight,
                    DisplayOrder = definition.DisplayOrder,
                    Depth = 1,
                    Status = NhBackgroundOperationStepStatus.Pending,
                    AggregationMode = NhBackgroundOperationAggregationMode.Manual,
                    CurrentAttemptId = claim.AttemptId,
                    FencingVersion = claim.FencingToken,
                    Version = 1
                });
            }
            else
            {
                step.TitleKey = definition.TitleKey;
                step.Weight = definition.Weight;
                step.DisplayOrder = definition.DisplayOrder;
                step.Version++;
            }
        }
        var now = DateTimeOffset.UtcNow;
        root.LastModifiedDateTime = now;
        root.Version++;
        NhBackgroundOperationService.Touch(operation, now);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
    }

    internal async Task<Guid> StartStepAsync(
        NhBackgroundOperationAttemptClaim claim,
        Guid? parentStepId,
        string stepKey,
        decimal weight,
        CancellationToken cancellationToken,
        string? titleKey = null)
    {
        NhBackgroundOperationKeys.ValidateStepKey(stepKey);
        if (weight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }
        var operation = await LoadFencedOperationAsync(repository, claim, cancellationToken);
        var parent = parentStepId.HasValue
            ? operation.Steps.Single(x => x.Id == parentStepId.Value)
            : operation.Steps.Single(x => x.ParentStepId == null && x.StepKey == "root");
        var step = operation.Steps.SingleOrDefault(x => x.ParentStepId == parent.Id && x.StepKey == stepKey);
        var now = DateTimeOffset.UtcNow;
        if (step is null)
        {
            parent.AggregationMode = NhBackgroundOperationAggregationMode.WeightedChildren;
            step = new NhBackgroundOperationStep
            {
                Id = Guid.NewGuid(),
                OperationId = operation.Id,
                ParentStepId = parent.Id,
                StepKey = stepKey,
                Weight = weight,
                DisplayOrder = operation.Steps.Count(x => x.ParentStepId == parent.Id),
                Depth = parent.Depth + 1,
                AggregationMode = NhBackgroundOperationAggregationMode.Manual,
                Version = 1
            };
            operation.Steps.Add(step);
        }
        if (!string.IsNullOrWhiteSpace(titleKey))
        {
            step.TitleKey = titleKey;
        }

        step.Status = NhBackgroundOperationStepStatus.Running;
        step.StartedAt ??= now;
        step.HeartbeatAt = now;
        step.CurrentAttemptId = claim.AttemptId;
        step.FencingVersion = claim.FencingToken;
        step.LastModifiedDateTime = now;
        step.Version++;
        NhBackgroundOperationService.Touch(operation, now);
        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.StepStarted,
            NhBackgroundOperationMessageSeverity.Information,
            step.TitleKey,
            null,
            false,
            claim.AttemptId,
            step.Id,
            step.StepKey);
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            operation,
            _options,
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
        return step.Id;
    }

    internal async Task CompleteStepAsync(
        NhBackgroundOperationAttemptClaim claim,
        Guid stepId,
        NhBackgroundOperationStepStatus status,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }
        var operation = await LoadFencedOperationAsync(repository, claim, cancellationToken);
        var step = operation.Steps.Single(x => x.Id == stepId);
        var now = DateTimeOffset.UtcNow;
        step.Status = status;
        step.CompletedAt = now;
        if (status is NhBackgroundOperationStepStatus.Succeeded or NhBackgroundOperationStepStatus.Skipped)
        {
            step.Percentage = 100;
        }
        step.LastModifiedDateTime = now;
        step.Version++;
        RecalculateProgress(operation);
        NhBackgroundOperationService.Touch(operation, now);
        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.StepCompleted,
            status == NhBackgroundOperationStepStatus.Succeeded
                ? NhBackgroundOperationMessageSeverity.Success
                : status == NhBackgroundOperationStepStatus.Cancelled
                    ? NhBackgroundOperationMessageSeverity.Warning
                    : NhBackgroundOperationMessageSeverity.Error,
            step.MessageKey ?? step.TitleKey,
            null,
            false,
            claim.AttemptId,
            step.Id,
            step.StepKey,
            serializedMessageArguments: step.MessageArgumentsJson);
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            operation,
            _options,
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
    }

    internal async Task FlushBatchAsync(
        NhBackgroundOperationAttemptClaim claim,
        Guid stepId,
        NhBackgroundOperationBatchSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }
        var operation = await LoadFencedOperationAsync(repository, claim, cancellationToken);
        var step = operation.Steps.Single(x => x.Id == stepId);
        var now = DateTimeOffset.UtcNow;
        step.AggregationMode = snapshot.Total.HasValue
            ? NhBackgroundOperationAggregationMode.ItemCount
            : NhBackgroundOperationAggregationMode.Indeterminate;
        step.DiscoveredItems = snapshot.Discovered;
        step.ProcessedItems = snapshot.Processed;
        step.SucceededItems = snapshot.Succeeded;
        step.FailedItems = snapshot.Failed;
        step.SkippedItems = snapshot.Skipped;
        step.RetriedItems = snapshot.Retried;
        step.ActiveItems = snapshot.Active;
        step.Current = snapshot.Processed;
        step.Total = snapshot.Total;
        step.Percentage = snapshot.Total is > 0
            ? Math.Min(100, snapshot.Processed * 100m / snapshot.Total.Value)
            : null;
        step.HeartbeatAt = now;
        step.LastModifiedDateTime = now;
        step.Version++;
        RecalculateProgress(operation);
        NhBackgroundOperationService.Touch(operation, now);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
    }

    internal async Task PublishMessageAsync(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationMessage message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.MessageKey))
        {
            throw new ArgumentException("A message key is required.", nameof(message));
        }
        var argumentsJson = message.Arguments is null ? null : NhBackgroundOperationJson.Serialize(message.Arguments);
        if (argumentsJson is not null && Encoding.UTF8.GetByteCount(argumentsJson) > _options.MaxMessageArgumentsBytes)
        {
            throw new InvalidOperationException("Message arguments exceed the configured size limit.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }
        var operation = await LoadFencedOperationAsync(repository, claim, cancellationToken);
        NhBackgroundOperationService.Touch(operation, DateTimeOffset.UtcNow);
        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.Message,
            message.Severity,
            message.MessageKey,
            message.Arguments,
            message.IsMilestone,
            claim.AttemptId,
            result: message.Result);
        operation.Events[^1].IsOperatorOnly = message.IsOperatorOnly;
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            operation,
            _options,
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
    }

    internal async Task<NhBackgroundOperationCheckpointValue<T>?> GetCheckpointAsync<T>(
        NhBackgroundOperationAttemptClaim claim,
        string checkpointKey,
        CancellationToken cancellationToken)
    {
        ValidateCheckpointKey(checkpointKey);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        var checkpoint = await repository.GetDbSet<NhBackgroundOperationCheckpoint>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == claim.OperationId && x.CheckpointKey == checkpointKey, cancellationToken);
        if (checkpoint is null)
        {
            return null;
        }
        var value = JsonSerializer.Deserialize<T>(checkpoint.ValueJson, NhBackgroundOperationJson.Options)
            ?? throw new InvalidOperationException($"Checkpoint '{checkpointKey}' contains no value.");
        return new NhBackgroundOperationCheckpointValue<T>(value, checkpoint.SchemaVersion, checkpoint.Version);
    }

    internal async Task<TaskResult> SetCheckpointAsync<T>(
        NhBackgroundOperationAttemptClaim claim,
        string checkpointKey,
        T value,
        int schemaVersion,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateCheckpointKey(checkpointKey);
        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }
        var json = JsonSerializer.Serialize(value, NhBackgroundOperationJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > _options.MaxCheckpointBytes)
        {
            return TaskResult.Failed(
                "checkpoint-value-too-large",
                "background-operation.checkpoint-value-too-large");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }
        var operation = await LoadFencedOperationAsync(repository, claim, cancellationToken);
        var set = repository.GetDbSet<NhBackgroundOperationCheckpoint>();
        var checkpoint = await set.SingleOrDefaultAsync(
            x => x.OperationId == claim.OperationId && x.CheckpointKey == checkpointKey,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (checkpoint is null)
        {
            if (expectedVersion.HasValue && expectedVersion.Value != 0)
            {
                return TaskResult.Failed(
                    "checkpoint-version-conflict",
                    "background-operation.checkpoint-version-conflict");
            }

            checkpoint = new NhBackgroundOperationCheckpoint
            {
                OperationId = claim.OperationId,
                CheckpointKey = checkpointKey,
                CreationDateTime = now,
                Version = 1
            };
            set.Add(checkpoint);
        }
        else
        {
            if (expectedVersion.HasValue && checkpoint.Version != expectedVersion.Value)
            {
                return TaskResult.Failed(
                    "checkpoint-version-conflict",
                    "background-operation.checkpoint-version-conflict");
            }

            checkpoint.Version++;
        }
        checkpoint.SchemaVersion = schemaVersion;
        checkpoint.ValueJson = json;
        checkpoint.AttemptId = claim.AttemptId;
        checkpoint.LastModifiedDateTime = now;
        NhBackgroundOperationService.Touch(operation, now);
        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.CheckpointChanged,
            NhBackgroundOperationMessageSeverity.Information,
            "background-operation.checkpoint-saved",
            new { checkpointKey, schemaVersion },
            false,
            claim.AttemptId);
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            operation,
            _options,
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
        return TaskResult.Succeeded();
    }

    internal async Task SetResultAsync(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationResultReference result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.Type) || string.IsNullOrWhiteSpace(result.Id))
        {
            throw new ArgumentException("Result type and id are required.", nameof(result));
        }
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }
        var operation = await LoadFencedOperationAsync(repository, claim, cancellationToken);
        operation.ResultReferenceType = result.Type;
        operation.ResultReferenceId = result.Id;
        operation.ResultUrl = result.Url;
        NhBackgroundOperationService.Touch(operation, DateTimeOffset.UtcNow);
        NhBackgroundOperationService.AppendEvent(operation,
            NhBackgroundOperationEventType.ResultAvailable,
            NhBackgroundOperationMessageSeverity.Success,
            "background-operation.result-available",
            null,
            true,
            claim.AttemptId,
            result: result);
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            operation,
            _options,
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
    }

    private async Task UpdateProgressAsync(
        NhBackgroundOperationAttemptClaim claim,
        Guid? stepId,
        decimal current,
        decimal total,
        string? messageKey,
        object? arguments,
        CancellationToken cancellationToken)
    {
        if (total <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total));
        }

        if (current < 0 || current > total)
        {
            throw new ArgumentOutOfRangeException(nameof(current));
        }
        var argumentsJson = arguments is null ? null : NhBackgroundOperationJson.Serialize(arguments);
        if (argumentsJson is not null && Encoding.UTF8.GetByteCount(argumentsJson) > _options.MaxMessageArgumentsBytes)
        {
            throw new InvalidOperationException("Progress message arguments exceed the configured size limit.");
        }
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }
        var operation = await LoadFencedOperationAsync(repository, claim, cancellationToken);
        var step = stepId.HasValue
            ? operation.Steps.Single(x => x.Id == stepId.Value)
            : operation.Steps.Single(x => x.ParentStepId == null && x.StepKey == "root");
        var percentage = current * 100m / total;
        if (step.Percentage.HasValue && percentage < step.Percentage.Value)
        {
            percentage = step.Percentage.Value;
        }
        var now = DateTimeOffset.UtcNow;
        step.Current = current;
        step.Total = total;
        step.Percentage = percentage;
        step.MessageKey = messageKey;
        step.MessageArgumentsJson = argumentsJson;
        step.Status = NhBackgroundOperationStepStatus.Running;
        step.HeartbeatAt = now;
        step.LastModifiedDateTime = now;
        step.Version++;
        RecalculateProgress(operation);
        if (!stepId.HasValue)
        {
            operation.ProgressCurrent = current;
            operation.ProgressTotal = total;
            operation.ProgressPercentage = percentage;
            operation.ProgressMessageKey = messageKey;
            operation.ProgressMessageArgumentsJson = step.MessageArgumentsJson;
        }
        else
        {
            operation.ProgressPhaseKey = step.StepKey;
            operation.ProgressMessageKey = messageKey;
            operation.ProgressMessageArgumentsJson = step.MessageArgumentsJson;
        }
        NhBackgroundOperationService.Touch(operation, now);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
    }

    internal static void RecalculateProgress(NhBackgroundOperation operation)
    {
        var byParent = operation.Steps
            .Where(x => x.ParentStepId.HasValue)
            .GroupBy(x => x.ParentStepId!.Value)
            .ToDictionary(x => x.Key, x => x.ToList());
        foreach (var step in operation.Steps.OrderByDescending(x => x.Depth))
        {
            if (step.AggregationMode != NhBackgroundOperationAggregationMode.WeightedChildren
                || !byParent.TryGetValue(step.Id, out var children)
                || children.Count == 0)
            {
                continue;
            }

            var totalWeight = children.Sum(x => x.Weight);
            if (totalWeight <= 0)
            {
                continue;
            }
            var calculated = children.Sum(child => child.Weight * GetContribution(child)) / totalWeight;
            step.Percentage = Math.Max(step.Percentage ?? 0, calculated);
        }
        var root = operation.Steps.Single(x => x.ParentStepId == null && x.StepKey == "root");
        if (root.Percentage.HasValue)
        {
            operation.ProgressPercentage = Math.Max(operation.ProgressPercentage ?? 0, root.Percentage.Value);
        }

        static decimal GetContribution(NhBackgroundOperationStep step)
        {
            return step.Status is NhBackgroundOperationStepStatus.Succeeded or NhBackgroundOperationStepStatus.Skipped
                ? 100
                : step.Percentage ?? 0;
        }
    }

    private static async Task<NhBackgroundOperation> LoadFencedOperationAsync(
        IRepository<NhBackgroundOperation> repository,
        NhBackgroundOperationAttemptClaim claim,
        CancellationToken cancellationToken)
    {
        var operation = await repository.GetAll()
            .Include(x => x.Steps)
            .SingleAsync(x => x.Id == claim.OperationId, cancellationToken);
        if (operation.CurrentAttemptId != claim.AttemptId || operation.DispatchGeneration != claim.DispatchGeneration)
        {
            throw new InvalidOperationException("The operation attempt lost its fencing token.");
        }

        if (operation.Status == NhBackgroundOperationStatus.CancelRequested)
        {
            throw new OperationCanceledException("Operation cancellation was requested.", cancellationToken);
        }

        return operation;
    }

    private Task<bool> LockAsync(
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

    private static async Task CompleteCancellationBeforeStartAsync(
        IRepository<NhBackgroundOperation> repository,
        NhBackgroundOperation operation,
        NhBackgroundOperationsOptions options,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
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
            options,
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
    }

    private async Task PublishSafelyAsync(NhBackgroundOperation operation, CancellationToken cancellationToken)
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
            _logger.LogWarning(exception, "Failed to publish background operation change for {OperationId}", operation.Id);
        }

        try
        {
            var projectionResult = await _notificationProjector.ProjectAsync(operation.Id, cancellationToken);
            if (!projectionResult.Success)
            {
                _logger.LogWarning(
                    "Notification projection was rejected for background operation {OperationId}: {@ProjectionErrors}",
                    operation.Id,
                    projectionResult.AllErrorMessages);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to project notification for background operation {OperationId}", operation.Id);
        }

        try
        {
            await _fanOutCoordinator.OperationChangedAsync(operation.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to aggregate parent progress for background operation {OperationId}", operation.Id);
        }
    }

    private static void ValidateCheckpointKey(string checkpointKey)
    {
        if (string.IsNullOrWhiteSpace(checkpointKey) || checkpointKey.Length > 200)
        {
            throw new ArgumentException("Checkpoint keys are required and cannot exceed 200 characters.", nameof(checkpointKey));
        }
    }
}
