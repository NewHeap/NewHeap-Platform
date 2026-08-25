using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed class NhBackgroundOperationFanOutPendingException : Exception;

internal sealed class NhBackgroundOperationFanOutContext : INhBackgroundOperationFanOutContext
{
    private readonly NhBackgroundOperationAttemptClaim _claim;
    private readonly NhBackgroundOperationFanOutCoordinator _coordinator;

    public NhBackgroundOperationFanOutContext(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationFanOutCoordinator coordinator)
    {
        _claim = claim;
        _coordinator = coordinator;
    }

    public Task<TaskResult<NhBackgroundOperationFanOutResult>> RunAsync<TRequest>(
        string fanOutKey,
        IEnumerable<NhBackgroundOperationFanOutItem<TRequest>> items,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(fanOutKey, items, new NhBackgroundOperationFanOutOptions(), cancellationToken);
    }

    public Task<TaskResult<NhBackgroundOperationFanOutResult>> RunAsync<TRequest>(
        string fanOutKey,
        IEnumerable<NhBackgroundOperationFanOutItem<TRequest>> items,
        NhBackgroundOperationFanOutOptions options,
        CancellationToken cancellationToken = default)
    {
        return _coordinator.RunAsync(_claim, fanOutKey, items, options, cancellationToken);
    }
}

internal sealed class NhBackgroundOperationFanOutCoordinator
{
    private static readonly NhBackgroundOperationStatus[] TerminalStatuses =
    [
        NhBackgroundOperationStatus.Succeeded,
        NhBackgroundOperationStatus.Failed,
        NhBackgroundOperationStatus.Cancelled,
        NhBackgroundOperationStatus.TimedOut
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationRegistry _registry;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly INhHangfireQueueNameResolver _queueNameResolver;
    private readonly INhBackgroundOperationLiveUpdatePublisher _liveUpdates;
    private readonly INhBackgroundOperationNotificationProjector _notificationProjector;
    private readonly ILogger<NhBackgroundOperationFanOutCoordinator> _logger;

    public NhBackgroundOperationFanOutCoordinator(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        INhHangfireQueueNameResolver queueNameResolver,
        INhBackgroundOperationLiveUpdatePublisher liveUpdates,
        INhBackgroundOperationNotificationProjector notificationProjector,
        ILogger<NhBackgroundOperationFanOutCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _options = options;
        _queueNameResolver = queueNameResolver;
        _liveUpdates = liveUpdates;
        _notificationProjector = notificationProjector;
        _logger = logger;
    }

    internal async Task<TaskResult<NhBackgroundOperationFanOutResult>> RunAsync<TRequest>(
        NhBackgroundOperationAttemptClaim claim,
        string fanOutKey,
        IEnumerable<NhBackgroundOperationFanOutItem<TRequest>> items,
        NhBackgroundOperationFanOutOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        NhBackgroundOperationKeys.ValidateStepKey(fanOutKey);
        if (options.TitleKey?.Length > 300)
        {
            throw new ArgumentException("A fan-out title key cannot exceed 300 characters.", nameof(options));
        }

        var descriptor = _registry.GetForRequest(typeof(TRequest));
        var prepared = items.Select(item => PrepareItem(item, descriptor)).ToList();
        if (prepared.Count > _options.MaxFanOutChildren)
        {
            throw new InvalidOperationException($"Fan-out '{fanOutKey}' exceeds the configured child limit of {_options.MaxFanOutChildren}.");
        }

        var duplicateKey = prepared.GroupBy(x => x.ItemKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateKey is not null)
        {
            throw new InvalidOperationException($"Fan-out '{fanOutKey}' contains duplicate item key '{duplicateKey}'.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }

        var parent = await repository.GetAll()
            .Include(x => x.Steps)
            .SingleAsync(x => x.Id == claim.OperationId, cancellationToken);
        EnsureFenced(parent, claim);
        var now = DateTimeOffset.UtcNow;
        var root = parent.Steps.Single(x => x.ParentStepId == null && x.StepKey == "root");
        var step = parent.Steps.SingleOrDefault(x => x.ParentStepId == root.Id && x.StepKey == fanOutKey);
        if (step is null)
        {
            root.AggregationMode = NhBackgroundOperationAggregationMode.WeightedChildren;
            step = new NhBackgroundOperationStep
            {
                Id = Guid.NewGuid(),
                OperationId = parent.Id,
                ParentStepId = root.Id,
                StepKey = fanOutKey,
                TitleKey = options.TitleKey,
                Weight = 1,
                DisplayOrder = parent.Steps.Count(x => x.ParentStepId == root.Id),
                Depth = 1,
                Status = NhBackgroundOperationStepStatus.Running,
                AggregationMode = NhBackgroundOperationAggregationMode.ChildOperations,
                ContinueOnChildFailure = options.FailureMode == NhBackgroundOperationFanOutFailureMode.Continue,
                StartedAt = now,
                CurrentAttemptId = claim.AttemptId,
                FencingVersion = claim.FencingToken,
                Version = 1
            };
            parent.Steps.Add(step);
        }

        var existing = await repository.GetAll()
            .Where(x => x.ParentOperationId == parent.Id && x.FanOutKey == fanOutKey)
            .OrderBy(x => x.FanOutItemKey)
            .ToListAsync(cancellationToken);
        var fanOutAlreadyCreated = existing.Count > 0;
        var childrenCreated = !fanOutAlreadyCreated && prepared.Count > 0;
        if (!fanOutAlreadyCreated)
        {
            step.ContinueOnChildFailure = options.FailureMode == NhBackgroundOperationFanOutFailureMode.Continue;
        }

        if (existing.Count > 0)
        {
            if (step.ContinueOnChildFailure != (options.FailureMode == NhBackgroundOperationFanOutFailureMode.Continue))
            {
                throw new InvalidOperationException($"Fan-out '{fanOutKey}' cannot change its failure mode after children were created.");
            }

            var expectedKeys = prepared.Select(x => x.ItemKey).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var actualKeys = existing.Select(x => x.FanOutItemKey!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Fan-out '{fanOutKey}' must use the same stable item keys after the parent resumes.");
            }

            var preparedByKey = prepared.ToDictionary(x => x.ItemKey, StringComparer.Ordinal);
            if (existing.Any(child =>
                    child.OperationType != descriptor.OperationType
                    || child.PayloadSchemaVersion != descriptor.PayloadSchemaVersion
                    || child.PayloadJson != preparedByKey[child.FanOutItemKey!].PayloadJson))
            {
                throw new InvalidOperationException($"Fan-out '{fanOutKey}' cannot change a child request after it was durably created.");
            }
        }
        else if (prepared.Count > 0)
        {
            var rootOperationId = parent.RootOperationId ?? parent.Id;
            foreach (var item in prepared)
            {
                var child = CreateChildOperation(parent, rootOperationId, fanOutKey, item, descriptor, now);
                repository.GetDbSet<NhBackgroundOperation>().Add(child);
                existing.Add(child);
                await NhBackgroundOperationEventRetention.TrimAsync(
                    repository,
                    child,
                    _options,
                    cancellationToken);
            }
            NhBackgroundOperationService.AppendEvent(
                parent,
                NhBackgroundOperationEventType.ChildrenCreated,
                NhBackgroundOperationMessageSeverity.Information,
                "background-operation.children-created",
                new { fanOutKey, count = prepared.Count },
                false,
                claim.AttemptId,
                step.Id,
                step.StepKey);
        }

        step.TitleKey ??= options.TitleKey;
        step.AggregationMode = NhBackgroundOperationAggregationMode.ChildOperations;
        step.Status = NhBackgroundOperationStepStatus.Running;
        step.StartedAt ??= now;
        step.CurrentAttemptId = claim.AttemptId;
        step.FencingVersion = claim.FencingToken;
        step.LastModifiedDateTime = now;
        step.Version++;
        ApplyAggregate(parent, step, existing, now);
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            parent,
            _options,
            cancellationToken);

        if (existing.Count == 0 || existing.All(child => IsTerminal(child.Status)))
        {
            CompleteFanOutStep(parent, step, existing, now);
            NhBackgroundOperationService.Touch(parent, now);
            await repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await PublishSafelyAsync(parent, cancellationToken);
            var result = CreateResult(existing);
            if (result.HasFailures && !step.ContinueOnChildFailure)
            {
                return TaskResult<NhBackgroundOperationFanOutResult>
                    .Failed(
                        "child-operation-failed",
                        "background-operation.child-operation-failed")
                    .WithData(result);
            }

            return TaskResult<NhBackgroundOperationFanOutResult>.Succeeded(result);
        }

        var attempt = await repository.GetDbSet<NhBackgroundOperationAttempt>()
            .SingleAsync(x => x.Id == claim.AttemptId, cancellationToken);
        attempt.Status = NhBackgroundOperationAttemptStatus.Suspended;
        attempt.CompletedAt = now;
        attempt.LastModifiedDateTime = now;
        attempt.Version++;
        parent.CurrentAttemptId = null;
        parent.Status = NhBackgroundOperationStatus.WaitingForChildren;
        parent.SchedulerJobId = null;
        // This due time is a convergence fallback when the final child update
        // cannot wake the parent. Dispatching performs a short durable recheck;
        // it never holds the worker while children remain active.
        parent.NextDispatchAt = now + _options.ReconciliationInterval;
        parent.HeartbeatAt = now;
        NhBackgroundOperationService.Touch(parent, now);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (childrenCreated)
        {
            foreach (var child in existing)
            {
                NhBackgroundOperationMetrics.RecordEnqueued(child.OperationType, child.Queue);
            }
        }
        await PublishSafelyAsync(parent, cancellationToken);
        throw new NhBackgroundOperationFanOutPendingException();
    }

    internal async Task OperationChangedAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var lookupScope = _scopeFactory.CreateAsyncScope();
        var lookupRepository = lookupScope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        var parentOperationId = await lookupRepository.GetAll().AsNoTracking()
            .Where(x => x.Id == operationId)
            .Select(x => x.ParentOperationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!parentOperationId.HasValue)
        {
            return;
        }
        await AggregateParentAsync(parentOperationId.Value, cancellationToken);
    }

    internal async Task<int> ReconcileWaitingAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        var parentIds = await repository.GetAll().AsNoTracking()
            .Where(x => x.ProcessorKey == _options.ProcessorKey)
            .Where(x => x.Status == NhBackgroundOperationStatus.WaitingForChildren
                        || (x.Status == NhBackgroundOperationStatus.CancelRequested && x.CurrentAttemptId == null))
            .Where(x => x.ChildOperations.Any())
            .OrderBy(x => x.LastModifiedDateTime)
            .Take(_options.ReconciliationBatchSize)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        foreach (var parentId in parentIds)
        {
            await AggregateParentAsync(parentId, cancellationToken);
        }

        return parentIds.Count;
    }

    private async Task AggregateParentAsync(Guid parentOperationId, CancellationToken cancellationToken)
    {
        NhBackgroundOperation? parent;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
            await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
            if (!await LockAsync(repository, transaction, parentOperationId, cancellationToken))
            {
                return;
            }
            parent = await repository.GetAll()
                .Include(x => x.Steps)
                .SingleOrDefaultAsync(x => x.Id == parentOperationId, cancellationToken);
            if (parent is null)
            {
                return;
            }
            var children = await repository.GetAll()
                .AsNoTracking()
                .Where(x => x.ParentOperationId == parentOperationId)
                .OrderBy(x => x.FanOutKey).ThenBy(x => x.FanOutItemKey)
                .ToListAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var changed = false;
            foreach (var group in children.GroupBy(x => x.FanOutKey, StringComparer.Ordinal))
            {
                var fanOutKey = group.Key;
                if (fanOutKey is null)
                {
                    continue;
                }
                var step = parent.Steps.SingleOrDefault(x => x.StepKey == fanOutKey);
                if (step is null)
                {
                    continue;
                }
                var childList = group.ToList();
                ApplyAggregate(parent, step, childList, now);
                if (childList.All(child => IsTerminal(child.Status)))
                {
                    CompleteFanOutStep(parent, step, childList, now);
                }

                changed = true;
            }
            if (!changed)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var allChildrenTerminal = children.Count > 0 && children.All(child => IsTerminal(child.Status));
            if (allChildrenTerminal && parent.Status == NhBackgroundOperationStatus.WaitingForChildren)
            {
                parent.Status = NhBackgroundOperationStatus.PendingDispatch;
                parent.NextDispatchAt = now;
                parent.SchedulerJobId = null;
                NhBackgroundOperationService.AppendEvent(
                    parent,
                    NhBackgroundOperationEventType.ChildrenCompleted,
                    NhBackgroundOperationMessageSeverity.Information,
                    "background-operation.children-completed",
                    new { count = children.Count },
                    false);
            }
            else if (allChildrenTerminal
                     && parent.Status == NhBackgroundOperationStatus.CancelRequested
                     && parent.CurrentAttemptId is null)
            {
                parent.Status = NhBackgroundOperationStatus.Cancelled;
                parent.CompletedAt = now;
                parent.NextDispatchAt = null;
                var root = parent.Steps.Single(x => x.ParentStepId == null && x.StepKey == "root");
                root.Status = NhBackgroundOperationStepStatus.Cancelled;
                root.CompletedAt = now;
                root.LastModifiedDateTime = now;
                root.Version++;
                NhBackgroundOperationService.AppendEvent(
                    parent,
                    NhBackgroundOperationEventType.StateChanged,
                    NhBackgroundOperationMessageSeverity.Warning,
                    "background-operation.cancelled",
                    null,
                    true);
            }
            NhBackgroundOperationService.Touch(parent, now);
            await NhBackgroundOperationEventRetention.TrimAsync(
                repository,
                parent,
                _options,
                cancellationToken);
            await repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await PublishSafelyAsync(parent, cancellationToken);
        if (parent.ParentOperationId.HasValue)
        {
            await AggregateParentAsync(parent.ParentOperationId.Value, cancellationToken);
        }
    }

    private PreparedFanOutItem PrepareItem<TRequest>(
        NhBackgroundOperationFanOutItem<TRequest> item,
        NhBackgroundOperationDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(item.ItemKey) || item.ItemKey.Trim().Length > 200)
        {
            throw new ArgumentException("Fan-out item keys are required and cannot exceed 200 characters.", nameof(item));
        }

        var payloadJson = NhBackgroundOperationJson.Serialize(item.Request);
        if (Encoding.UTF8.GetByteCount(payloadJson) > _options.MaxPayloadBytes)
        {
            throw new InvalidOperationException($"Fan-out item '{item.ItemKey}' exceeds the configured payload size limit.");
        }
        var rawConcurrencyKey = descriptor.ConcurrencyKeyFactory?.Invoke(item.Request!);
        return new PreparedFanOutItem(
            item.ItemKey.Trim(),
            payloadJson,
            string.IsNullOrWhiteSpace(rawConcurrencyKey)
                ? null
                : NhBackgroundOperationKeys.HashResourceKey(rawConcurrencyKey));
    }

    private NhBackgroundOperation CreateChildOperation(
        NhBackgroundOperation parent,
        Guid rootOperationId,
        string fanOutKey,
        PreparedFanOutItem item,
        NhBackgroundOperationDescriptor descriptor,
        DateTimeOffset now)
    {
        var child = new NhBackgroundOperation
        {
            Id = Guid.NewGuid(),
            CreationDateTime = now,
            LastModifiedDateTime = now,
            OperationType = descriptor.OperationType,
            PayloadSchemaVersion = descriptor.PayloadSchemaVersion,
            PayloadJson = item.PayloadJson,
            OwnerUserId = parent.OwnerUserId,
            DivisionId = parent.DivisionId,
            ParentOperationId = parent.Id,
            RootOperationId = rootOperationId,
            FanOutKey = fanOutKey,
            FanOutItemKey = item.ItemKey,
            ProcessorKey = parent.ProcessorKey,
            Queue = NhBackgroundOperationKeys.NormalizeQueueName(_queueNameResolver.GetQueueName(descriptor.Queue)),
            Priority = parent.Priority,
            Status = NhBackgroundOperationStatus.PendingDispatch,
            NextDispatchAt = now,
            ConcurrencyKey = item.ConcurrencyKey,
            CorrelationId = parent.CorrelationId ?? parent.Id.ToString("N"),
            Version = 1
        };
        child.Steps.Add(new NhBackgroundOperationStep
        {
            Id = Guid.NewGuid(),
            OperationId = child.Id,
            StepKey = "root",
            Status = NhBackgroundOperationStepStatus.Pending,
            AggregationMode = NhBackgroundOperationAggregationMode.Manual,
            Weight = 1,
            Version = 1
        });
        NhBackgroundOperationService.AppendEvent(
            child,
            NhBackgroundOperationEventType.StateChanged,
            NhBackgroundOperationMessageSeverity.Information,
            "background-operation.queued",
            null,
            false);
        return child;
    }

    private static void ApplyAggregate(
        NhBackgroundOperation parent,
        NhBackgroundOperationStep step,
        IReadOnlyCollection<NhBackgroundOperation> children,
        DateTimeOffset now)
    {
        var terminal = children.Count(child => IsTerminal(child.Status));
        var succeeded = children.Count(child => child.Status == NhBackgroundOperationStatus.Succeeded);
        var failed = children.Count(child => child.Status is NhBackgroundOperationStatus.Failed or NhBackgroundOperationStatus.TimedOut);
        var cancelled = children.Count(child => child.Status == NhBackgroundOperationStatus.Cancelled);
        var percentage = children.Count == 0
            ? 100
            : children.Sum(ChildContribution) / children.Count;
        step.AggregationMode = NhBackgroundOperationAggregationMode.ChildOperations;
        step.DiscoveredItems = children.Count;
        step.ProcessedItems = terminal;
        step.SucceededItems = succeeded;
        step.FailedItems = failed;
        step.SkippedItems = cancelled;
        step.ActiveItems = children.Count - terminal;
        step.Current = terminal;
        step.Total = children.Count;
        step.Percentage = percentage;
        step.HeartbeatAt = now;
        step.LastModifiedDateTime = now;
        step.Version++;
        NhBackgroundOperationPersistence.RecalculateProgress(parent);

        static decimal ChildContribution(NhBackgroundOperation child)
        {
            return IsTerminal(child.Status)
                ? 100
                : child.ProgressPercentage ?? 0;
        }
    }

    private static void CompleteFanOutStep(
        NhBackgroundOperation parent,
        NhBackgroundOperationStep step,
        IReadOnlyCollection<NhBackgroundOperation> children,
        DateTimeOffset now)
    {
        var hasFailures = children.Any(child => child.Status != NhBackgroundOperationStatus.Succeeded);
        step.Status = hasFailures && !step.ContinueOnChildFailure
            ? NhBackgroundOperationStepStatus.Failed
            : NhBackgroundOperationStepStatus.Succeeded;
        step.Percentage = 100;
        step.CompletedAt = now;
        step.LastModifiedDateTime = now;
        step.Version++;
        NhBackgroundOperationPersistence.RecalculateProgress(parent);
    }

    private static NhBackgroundOperationFanOutResult CreateResult(
        IReadOnlyCollection<NhBackgroundOperation> children)
    {
        var results = children.OrderBy(child => child.FanOutItemKey, StringComparer.Ordinal)
            .Select(child => new NhBackgroundOperationFanOutChildResult(
                child.Id,
                child.FanOutItemKey!,
                child.Status,
                child.ProgressPercentage,
                child.ResultReferenceType is not null && child.ResultReferenceId is not null
                    ? new NhBackgroundOperationResultReference(
                        child.ResultReferenceType,
                        child.ResultReferenceId,
                        child.ResultUrl)
                    : null,
                child.FailureCode))
            .ToList();
        return new NhBackgroundOperationFanOutResult(
            results.Count,
            results.Count(x => x.Status == NhBackgroundOperationStatus.Succeeded),
            results.Count(x => x.Status is NhBackgroundOperationStatus.Failed or NhBackgroundOperationStatus.TimedOut),
            results.Count(x => x.Status == NhBackgroundOperationStatus.Cancelled),
            results);
    }

    private static bool IsTerminal(NhBackgroundOperationStatus status)
    {
        return TerminalStatuses.Contains(status);
    }

    private static void EnsureFenced(NhBackgroundOperation parent, NhBackgroundOperationAttemptClaim claim)
    {
        if (parent.CurrentAttemptId != claim.AttemptId
            || parent.DispatchGeneration != claim.DispatchGeneration
            || parent.Status != NhBackgroundOperationStatus.Running)
        {
            throw new InvalidOperationException("The parent operation attempt lost its fencing token.");
        }
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

    private async Task PublishSafelyAsync(NhBackgroundOperation operation, CancellationToken cancellationToken)
    {
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
            _logger.LogWarning(exception, "Failed to publish fan-out update for parent operation {OperationId}.", operation.Id);
        }

        try
        {
            var projectionResult = await _notificationProjector.ProjectAsync(operation.Id, cancellationToken);
            if (!projectionResult.Success)
            {
                _logger.LogWarning(
                    "Fan-out notification projection was rejected for parent operation {OperationId}: {@ProjectionErrors}",
                    operation.Id,
                    projectionResult.AllErrorMessages);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to project fan-out notification for parent operation {OperationId}.", operation.Id);
        }
    }

    private sealed record PreparedFanOutItem(
        string ItemKey,
        string PayloadJson,
        string? ConcurrencyKey);
}
