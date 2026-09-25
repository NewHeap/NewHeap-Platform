using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using NewHeap.Platform.Mapping;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

public sealed class NhBackgroundOperationService :
    INhBackgroundOperationService,
    INhBackgroundOperationAdministrationService
{
    private const string CancellationRequestedMessageKey = "background-operation.cancellation-requested";
    private const string AdministratorCancellationRequestedMessageKey =
        "background-operation.administrator-cancellation-requested";
    private const string RetryRequestedMessageKey = "background-operation.retry-requested";
    private const string AdministratorRetryRequestedMessageKey =
        "background-operation.administrator-retry-requested";

    private static readonly NhBackgroundOperationStatus[] ActiveStatuses =
    [
        NhBackgroundOperationStatus.PendingDispatch,
        NhBackgroundOperationStatus.Queued,
        NhBackgroundOperationStatus.Running,
        NhBackgroundOperationStatus.WaitingForChildren,
        NhBackgroundOperationStatus.WaitingForSignal,
        NhBackgroundOperationStatus.CancelRequested,
        NhBackgroundOperationStatus.RetryScheduled
    ];

    private readonly IRepository<NhBackgroundOperation> _repository;
    private readonly NhBackgroundOperationRegistry _registry;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly INhHangfireQueueNameResolver _queueNameResolver;
    private readonly INhBackgroundOperationScheduler _scheduler;
    private readonly INhBackgroundOperationLiveUpdatePublisher _liveUpdates;
    private readonly INhBackgroundOperationNotificationProjector _notificationProjector;
    private readonly IMapper _mapper;
    private readonly ILogger<NhBackgroundOperationService> _logger;
    private readonly INhBackgroundOperationOwnerDirectory? _ownerDirectory;

    public NhBackgroundOperationService(
        IRepository<NhBackgroundOperation> repository,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        INhHangfireQueueNameResolver queueNameResolver,
        INhBackgroundOperationScheduler scheduler,
        INhBackgroundOperationLiveUpdatePublisher liveUpdates,
        INhBackgroundOperationNotificationProjector notificationProjector,
        IMapper mapper,
        ILogger<NhBackgroundOperationService> logger)
        : this(
            repository,
            registry,
            options,
            queueNameResolver,
            scheduler,
            liveUpdates,
            notificationProjector,
            mapper,
            logger,
            null)
    {
    }

    public NhBackgroundOperationService(
        IRepository<NhBackgroundOperation> repository,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        INhHangfireQueueNameResolver queueNameResolver,
        INhBackgroundOperationScheduler scheduler,
        INhBackgroundOperationLiveUpdatePublisher liveUpdates,
        INhBackgroundOperationNotificationProjector notificationProjector,
        IMapper mapper,
        ILogger<NhBackgroundOperationService> logger,
        INhBackgroundOperationOwnerDirectory? ownerDirectory)
    {
        _repository = repository;
        _registry = registry;
        _options = options;
        _queueNameResolver = queueNameResolver;
        _scheduler = scheduler;
        _liveUpdates = liveUpdates;
        _notificationProjector = notificationProjector;
        _mapper = mapper;
        _logger = logger;
        _ownerDirectory = ownerDirectory;
    }

    public async Task<TaskResult<NhBackgroundOperationViewModel>> EnqueueAsync<TRequest>(
        TRequest request,
        NhBackgroundOperationEnqueueOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        if (options.OwnerUserId == Guid.Empty)
        {
            return TaskResult<NhBackgroundOperationViewModel>.Failed(nameof(options.OwnerUserId), "An operation owner is required.");
        }

        if (!_registry.TryGetForRequest(typeof(TRequest), out var descriptor))
        {
            return TaskResult<NhBackgroundOperationViewModel>.Failed(
                nameof(request),
                $"No background operation is registered for request type '{typeof(TRequest).Name}'.");
        }

        if (descriptor.Idempotency == NhBackgroundOperationIdempotency.IdempotentWithKey
            && string.IsNullOrWhiteSpace(options.IdempotencyKey))
        {
            return TaskResult<NhBackgroundOperationViewModel>.Failed(
                nameof(options.IdempotencyKey),
                "An idempotency key is required for this operation.");
        }

        var payloadJson = NhBackgroundOperationJson.Serialize(request);
        if (Encoding.UTF8.GetByteCount(payloadJson) > _options.MaxPayloadBytes)
        {
            return TaskResult<NhBackgroundOperationViewModel>.Failed(nameof(request), "The operation payload exceeds the configured size limit.");
        }

        var concurrencyKey = NormalizeOptionalKey(
            options.ConcurrencyKey ?? descriptor.ConcurrencyKeyFactory?.Invoke(request));
        var conflictBehavior = options.ConflictBehavior ?? descriptor.ConflictBehavior;
        // Admission deduplication must never let a caller discover an operation
        // owned by another user or active division through a reused raw key.
        var idempotencyScope = NormalizeScope(
            $"{options.OwnerUserId:N}:{options.DivisionId?.ToString("N") ?? "global"}:{options.IdempotencyScope ?? descriptor.OperationType}");
        var idempotencyHash = string.IsNullOrWhiteSpace(options.IdempotencyKey)
            ? null
            : NhBackgroundOperationKeys.HashIdempotencyKey(options.IdempotencyKey);
        var admissionKey = idempotencyHash is not null
            ? $"idempotency:{idempotencyScope}:{idempotencyHash}"
            : concurrencyKey is not null
                ? $"concurrency:{concurrencyKey}"
                : $"operation:{Guid.NewGuid():N}";

        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await _repository.TryAcquireTransactionLockAsync(
                transaction,
                $"NhBackgroundOperation:Admission:{admissionKey}",
                _options.TransactionLockTimeoutMilliseconds,
                cancellationToken))
        {
            return TaskResult<NhBackgroundOperationViewModel>.Failed("The operation could not acquire its admission lock. Please retry.");
        }

        if (idempotencyHash is not null)
        {
            var record = await _repository.GetDbSet<NhBackgroundOperationIdempotencyRecord>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.Scope == idempotencyScope && x.KeyHash == idempotencyHash,
                    cancellationToken);
            if (record is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                var existing = await GetAsync(record.OperationId, options.OwnerUserId, null, cancellationToken);
                return existing is null
                    ? TaskResult<NhBackgroundOperationViewModel>.Failed("The idempotency record references an unavailable operation.")
                    : TaskResult<NhBackgroundOperationViewModel>.Succeeded(existing);
            }
        }

        if (concurrencyKey is not null)
        {
            var conflicting = await _repository.GetAll()
                .AsNoTracking()
                .Where(x => x.ConcurrencyKey == concurrencyKey && ActiveStatuses.Contains(x.Status))
                .OrderBy(x => x.CreationDateTime)
                .FirstOrDefaultAsync(cancellationToken);
            if (conflicting is not null)
            {
                if (conflictBehavior == NhBackgroundOperationConflictBehavior.ReturnExisting)
                {
                    await transaction.CommitAsync(cancellationToken);
                    var existing = await GetAsync(conflicting.Id, options.OwnerUserId, null, cancellationToken);
                    return existing is null
                        ? TaskResult<NhBackgroundOperationViewModel>.Failed("A conflicting operation exists but is not visible to this owner.")
                        : TaskResult<NhBackgroundOperationViewModel>.Succeeded(existing);
                }

                if (conflictBehavior == NhBackgroundOperationConflictBehavior.Reject)
                {
                    return TaskResult<NhBackgroundOperationViewModel>.Failed("An operation for the same resource and action is already active.");
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        var operation = new NhBackgroundOperation
        {
            Id = Guid.NewGuid(),
            CreationDateTime = now,
            LastModifiedDateTime = now,
            OperationType = descriptor.OperationType,
            PayloadSchemaVersion = descriptor.PayloadSchemaVersion,
            PayloadJson = payloadJson,
            OwnerUserId = options.OwnerUserId,
            DivisionId = options.DivisionId,
            ProcessorKey = _options.ProcessorKey,
            Queue = NhBackgroundOperationKeys.NormalizeQueueName(
                _queueNameResolver.GetQueueName(descriptor.Queue)),
            Priority = options.Priority,
            Status = NhBackgroundOperationStatus.PendingDispatch,
            NextDispatchAt = now,
            ConcurrencyKey = concurrencyKey,
            DomainObjectType = options.DomainObjectType,
            DomainObjectId = options.DomainObjectId,
            CorrelationId = options.CorrelationId,
            Version = 1
        };
        operation.Steps.Add(new NhBackgroundOperationStep
        {
            Id = Guid.NewGuid(),
            OperationId = operation.Id,
            StepKey = "root",
            Status = NhBackgroundOperationStepStatus.Pending,
            AggregationMode = NhBackgroundOperationAggregationMode.Manual,
            Weight = 1,
            Version = 1
        });
        AppendEvent(operation, NhBackgroundOperationEventType.StateChanged, NhBackgroundOperationMessageSeverity.Information,
            "background-operation.queued", null, false);

        await _repository.AddAsync(operation, cancellationToken);
        if (idempotencyHash is not null)
        {
            await _repository.AddAsync(new NhBackgroundOperationIdempotencyRecord
            {
                Scope = idempotencyScope,
                KeyHash = idempotencyHash,
                OperationId = operation.Id,
                CreationDateTime = now
            }, cancellationToken);
        }

        await NhBackgroundOperationEventRetention.TrimAsync(
            _repository,
            operation,
            _options,
            cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        NhBackgroundOperationMetrics.RecordEnqueued(operation.OperationType, operation.Queue);
        await PublishChangedSafelyAsync(operation, cancellationToken);
        return TaskResult<NhBackgroundOperationViewModel>.Succeeded(_mapper.Map<NhBackgroundOperationViewModel>(operation));
    }

    public IQueryable<NhBackgroundOperation> QueryForOwner(Guid ownerUserId, Guid? divisionId = null)
    {
        var query = _repository.GetAll().Where(x => x.OwnerUserId == ownerUserId && x.ParentOperationId == null);
        if (divisionId.HasValue)
        {
            query = query.Where(x => x.DivisionId == null || x.DivisionId == divisionId);
        }
        else
        {
            query = query.Where(x => x.DivisionId == null);
        }

        return query;
    }

    public Task<NhBackgroundOperationViewModel?> GetAsync(
        Guid operationId,
        Guid ownerUserId,
        long? eventsAfterSequence = null,
        CancellationToken cancellationToken = default)
    {
        return GetInternalAsync<NhBackgroundOperationViewModel>(
            operationId,
            OwnedBy(ownerUserId),
            eventsAfterSequence,
            false,
            cancellationToken);
    }

    public Task<TaskResult<NhBackgroundOperationViewModel>> RequestCancellationAsync(
        Guid operationId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        return RequestCancellationInternalAsync<NhBackgroundOperationViewModel>(
            operationId,
            OwnedBy(ownerUserId),
            ownerUserId,
            CancellationRequestedMessageKey,
            false,
            cancellationToken);
    }

    public Task<TaskResult<NhBackgroundOperationViewModel>> RetryAsync(
        Guid operationId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        return RetryInternalAsync<NhBackgroundOperationViewModel>(
            operationId,
            OwnedBy(ownerUserId),
            RetryRequestedMessageKey,
            false,
            cancellationToken);
    }

    public IQueryable<NhBackgroundOperation> QueryForAdministration(Guid? divisionId)
    {
        return _repository.GetAll()
            .Where(VisibleInDivision(divisionId))
            .Where(x => x.ParentOperationId == null);
    }

    public async Task PopulateOwnersAsync(
        IReadOnlyCollection<NhBackgroundOperationAdministrationViewModel> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0 || _ownerDirectory is null)
        {
            return;
        }

        var ownerIds = operations
            .Select(x => x.OwnerUserId)
            .Distinct()
            .ToList();
        var owners = await _ownerDirectory.GetOwnersAsync(ownerIds, cancellationToken);
        foreach (var operation in operations)
        {
            operation.OwnerDisplayName = owners.TryGetValue(operation.OwnerUserId, out var owner)
                ? owner.DisplayName
                : null;
        }
    }

    async Task<NhBackgroundOperationAdministrationViewModel?> INhBackgroundOperationAdministrationService.GetAsync(
        Guid operationId,
        Guid? divisionId,
        long? eventsAfterSequence,
        CancellationToken cancellationToken)
    {
        var view = await GetInternalAsync<NhBackgroundOperationAdministrationViewModel>(
            operationId,
            VisibleInDivision(divisionId),
            eventsAfterSequence,
            true,
            cancellationToken);
        if (view is not null)
        {
            await PopulateOwnersAsync([view], cancellationToken);
        }

        return view;
    }

    async Task<TaskResult<NhBackgroundOperationAdministrationViewModel>> INhBackgroundOperationAdministrationService.RequestCancellationAsync(
        Guid operationId,
        Guid administratorUserId,
        Guid? divisionId,
        CancellationToken cancellationToken)
    {
        if (administratorUserId == Guid.Empty)
        {
            return TaskResult<NhBackgroundOperationAdministrationViewModel>.Failed(
                nameof(administratorUserId),
                "An administrator is required.");
        }

        var result = await RequestCancellationInternalAsync<NhBackgroundOperationAdministrationViewModel>(
            operationId,
            VisibleInDivision(divisionId),
            administratorUserId,
            AdministratorCancellationRequestedMessageKey,
            true,
            cancellationToken);
        if (result.Success && result.Data is not null)
        {
            await PopulateOwnersAsync([result.Data], cancellationToken);
        }

        return result;
    }

    async Task<TaskResult<NhBackgroundOperationAdministrationViewModel>> INhBackgroundOperationAdministrationService.RetryAsync(
        Guid operationId,
        Guid administratorUserId,
        Guid? divisionId,
        CancellationToken cancellationToken)
    {
        if (administratorUserId == Guid.Empty)
        {
            return TaskResult<NhBackgroundOperationAdministrationViewModel>.Failed(
                nameof(administratorUserId),
                "An administrator is required.");
        }

        var result = await RetryInternalAsync<NhBackgroundOperationAdministrationViewModel>(
            operationId,
            VisibleInDivision(divisionId),
            AdministratorRetryRequestedMessageKey,
            true,
            cancellationToken);
        if (result.Success && result.Data is not null)
        {
            await PopulateOwnersAsync([result.Data], cancellationToken);
        }

        return result;
    }

    private async Task<TaskResult<TView>> RequestCancellationInternalAsync<TView>(
        Guid operationId,
        Expression<Func<NhBackgroundOperation, bool>> visibility,
        Guid requestedByUserId,
        string messageKey,
        bool includeOperatorEvents,
        CancellationToken cancellationToken)
        where TView : NhBackgroundOperationViewModel
    {
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await AcquireOperationLockAsync(transaction, operationId, cancellationToken))
        {
            return TaskResult<TView>.Failed("The operation is busy. Please retry.");
        }

        var operation = await _repository.GetAll()
            .Where(visibility)
            .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
        if (operation is null)
        {
            return TaskResult<TView>.Failed("The operation was not found.");
        }

        if (IsTerminal(operation.Status))
        {
            await transaction.CommitAsync(cancellationToken);
            var terminalView = await GetInternalAsync<TView>(
                operation.Id,
                visibility,
                null,
                includeOperatorEvents,
                cancellationToken);
            return terminalView is null
                ? TaskResult<TView>.Failed("The operation was not found.")
                : TaskResult<TView>.Succeeded(terminalView);
        }

        var hierarchy = await LoadHierarchyFromAsync(
            operation,
            cancellationToken: cancellationToken);
        foreach (var descendantId in hierarchy.Where(x => x.Id != operation.Id).Select(x => x.Id).Order())
        {
            if (!await AcquireOperationLockAsync(transaction, descendantId, cancellationToken))
            {
                return TaskResult<TView>.Failed("A child operation is busy. Please retry cancellation.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var schedulerJobIds = new List<string>();
        foreach (var target in hierarchy.Where(x => !IsTerminal(x.Status)))
        {
            target.CancelRequestedAt ??= now;
            target.CancelRequestedByUserId = requestedByUserId;
            target.Status = NhBackgroundOperationStatus.CancelRequested;
            if (!target.CurrentAttemptId.HasValue)
            {
                target.NextDispatchAt = now;
                target.SchedulerJobId = null;
            }
            Touch(target, now);
            AppendEvent(
                target,
                NhBackgroundOperationEventType.CancellationRequested,
                NhBackgroundOperationMessageSeverity.Information,
                messageKey,
                null,
                target.Id == operation.Id);
            await NhBackgroundOperationEventRetention.TrimAsync(
                _repository,
                target,
                _options,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(target.SchedulerJobId))
            {
                schedulerJobIds.Add(target.SchedulerJobId);
            }
        }
        await _repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var schedulerJobId in schedulerJobIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await _scheduler.DeleteAsync(schedulerJobId, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to delete scheduler job {SchedulerJobId} while cancelling operation hierarchy {OperationId}",
                    schedulerJobId,
                    operation.Id);
            }
        }

        foreach (var target in hierarchy)
        {
            await PublishChangedSafelyAsync(target, cancellationToken);
        }
        var view = await GetInternalAsync<TView>(
            operation.Id,
            visibility,
            null,
            includeOperatorEvents,
            cancellationToken);
        return view is null
            ? TaskResult<TView>.Failed("The operation was not found after cancellation.")
            : TaskResult<TView>.Succeeded(view);
    }

    private async Task<TaskResult<TView>> RetryInternalAsync<TView>(
        Guid operationId,
        Expression<Func<NhBackgroundOperation, bool>> visibility,
        string messageKey,
        bool includeOperatorEvents,
        CancellationToken cancellationToken)
        where TView : NhBackgroundOperationViewModel
    {
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await AcquireOperationLockAsync(transaction, operationId, cancellationToken))
        {
            return TaskResult<TView>.Failed("The operation is busy. Please retry.");
        }

        var operation = await _repository.GetAll()
            .Where(visibility)
            .Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
        if (operation is null)
        {
            return TaskResult<TView>.Failed("The operation was not found.");
        }

        if (!IsTerminal(operation.Status) || operation.Status == NhBackgroundOperationStatus.Succeeded)
        {
            return TaskResult<TView>.Failed("Only an unsuccessful terminal operation can be retried.");
        }
        var hierarchy = await LoadHierarchyFromAsync(operation, true, cancellationToken);
        var retryTargets = hierarchy
            .Where(x => x.Status != NhBackgroundOperationStatus.Succeeded && IsTerminal(x.Status))
            .ToList();
        foreach (var target in retryTargets)
        {
            if (target.SensitiveDataRedactedAt.HasValue)
            {
                return TaskResult<TView>.Failed("This operation hierarchy can no longer be retried because a payload retention period expired.");
            }

            if (!_registry.TryGetForOperationType(target.OperationType, out var targetDescriptor))
            {
                return TaskResult<TView>.Failed(
                    "This operation hierarchy contains an operation type that is no longer registered.");
            }

            if (target.PayloadSchemaVersion != targetDescriptor.PayloadSchemaVersion)
            {
                return TaskResult<TView>.Failed("This operation hierarchy contains an unsupported payload schema and cannot be retried.");
            }

            if (targetDescriptor.Idempotency == NhBackgroundOperationIdempotency.NonIdempotent)
            {
                return TaskResult<TView>.Failed("This operation hierarchy contains a non-idempotent operation and cannot be retried safely.");
            }
        }
        foreach (var descendantId in retryTargets.Where(x => x.Id != operation.Id).Select(x => x.Id).Order())
        {
            if (!await AcquireOperationLockAsync(transaction, descendantId, cancellationToken))
            {
                return TaskResult<TView>.Failed("A child operation is busy. Please retry.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var target in retryTargets)
        {
            ResetForRetry(target, now);
            AppendEvent(
                target,
                NhBackgroundOperationEventType.RetryScheduled,
                NhBackgroundOperationMessageSeverity.Information,
                messageKey,
                null,
                target.Id == operation.Id);
            await NhBackgroundOperationEventRetention.TrimAsync(
                _repository,
                target,
                _options,
                cancellationToken);
        }
        await _repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var target in retryTargets)
        {
            await PublishChangedSafelyAsync(target, cancellationToken);
        }
        var view = await GetInternalAsync<TView>(
            operation.Id,
            visibility,
            null,
            includeOperatorEvents,
            cancellationToken);
        return view is null
            ? TaskResult<TView>.Failed("The operation was not found after retry scheduling.")
            : TaskResult<TView>.Succeeded(view);
    }

    private async Task<List<NhBackgroundOperation>> LoadHierarchyFromAsync(
        NhBackgroundOperation operation,
        bool includeSteps = false,
        CancellationToken cancellationToken = default)
    {
        // Fan-out children inherit the owner, so the owner bounds the hierarchy for
        // owner and administrator requests alike.
        var ownerUserId = operation.OwnerUserId;
        var rootOperationId = operation.RootOperationId ?? operation.Id;
        IQueryable<NhBackgroundOperation> query = _repository.GetAll()
            .Where(x => x.OwnerUserId == ownerUserId)
            .Where(x => x.Id == rootOperationId || x.RootOperationId == rootOperationId);
        if (includeSteps)
        {
            query = query.Include(x => x.Steps);
        }
        var candidates = await query.ToListAsync(cancellationToken);
        var includedIds = new HashSet<Guid> { operation.Id };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var candidate in candidates.Where(x => x.ParentOperationId.HasValue))
            {
                if (!includedIds.Contains(candidate.ParentOperationId!.Value) || !includedIds.Add(candidate.Id))
                {
                    continue;
                }
                added = true;
            }
        }
        return candidates.Where(x => includedIds.Contains(x.Id)).ToList();
    }

    private static void ResetForRetry(NhBackgroundOperation operation, DateTimeOffset now)
    {
        operation.Status = NhBackgroundOperationStatus.PendingDispatch;
        operation.NextDispatchAt = now;
        operation.SchedulerJobId = null;
        operation.CurrentAttemptId = null;
        operation.CancelRequestedAt = null;
        operation.CancelRequestedByUserId = null;
        operation.CompletedAt = null;
        operation.FailureCode = null;
        operation.FailureMessageKey = null;
        operation.DiagnosticCorrelationId = null;
        operation.ProgressCurrent = 0;
        operation.ProgressTotal = null;
        operation.ProgressPercentage = 0;
        operation.ProgressPhaseKey = null;
        operation.ProgressMessageKey = null;
        operation.ProgressMessageArgumentsJson = null;
        operation.ResultReferenceType = null;
        operation.ResultReferenceId = null;
        operation.ResultUrl = null;
        foreach (var step in operation.Steps)
        {
            step.Status = NhBackgroundOperationStepStatus.Pending;
            step.Current = 0;
            step.Total = null;
            step.Percentage = 0;
            step.MessageKey = null;
            step.MessageArgumentsJson = null;
            step.DiscoveredItems = 0;
            step.ProcessedItems = 0;
            step.SucceededItems = 0;
            step.FailedItems = 0;
            step.SkippedItems = 0;
            step.RetriedItems = 0;
            step.ActiveItems = 0;
            step.StartedAt = null;
            step.HeartbeatAt = null;
            step.CompletedAt = null;
            step.CurrentAttemptId = null;
            step.FencingVersion++;
            step.Version++;
            step.LastModifiedDateTime = now;
        }
        Touch(operation, now);
    }

    private async Task<TView?> GetInternalAsync<TView>(
        Guid operationId,
        Expression<Func<NhBackgroundOperation, bool>> visibility,
        long? eventsAfterSequence,
        bool includeOperatorEvents,
        CancellationToken cancellationToken)
        where TView : NhBackgroundOperationViewModel
    {
        var operation = await _repository.GetAll()
            .AsNoTracking()
            .Where(visibility)
            .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
        if (operation is null)
        {
            return null;
        }

        var attempts = await _repository.GetDbSet<NhBackgroundOperationAttempt>()
            .AsNoTracking()
            .Where(x => x.OperationId == operationId)
            .OrderBy(x => x.AttemptNumber)
            .ToListAsync(cancellationToken);
        var steps = await _repository.GetDbSet<NhBackgroundOperationStep>()
            .AsNoTracking()
            .Where(x => x.OperationId == operationId)
            .OrderBy(x => x.Depth).ThenBy(x => x.DisplayOrder)
            .ToListAsync(cancellationToken);
        var eventQuery = _repository.GetDbSet<NhBackgroundOperationEvent>()
            .AsNoTracking()
            .Where(x => x.OperationId == operationId);
        if (!includeOperatorEvents)
        {
            eventQuery = eventQuery.Where(x => !x.IsOperatorOnly);
        }
        if (eventsAfterSequence.HasValue)
        {
            eventQuery = eventQuery.Where(x => x.Sequence > eventsAfterSequence.Value);
        }
        var events = await eventQuery.OrderBy(x => x.Sequence).ToListAsync(cancellationToken);
        var ownerUserId = operation.OwnerUserId;
        var rootOperationId = operation.RootOperationId ?? operation.Id;
        var hierarchyCandidates = await _repository.GetAll()
            .AsNoTracking()
            .Where(x => x.OwnerUserId == ownerUserId && x.RootOperationId == rootOperationId)
            .OrderBy(x => x.CreationDateTime)
            .ToListAsync(cancellationToken);

        var view = _mapper.Map<TView>(operation);
        view.Attempts = _mapper.Map<List<NhBackgroundOperationAttemptViewModel>>(attempts);
        view.Events = _mapper.Map<List<NhBackgroundOperationEventViewModel>>(events);
        var stepViews = steps.ToDictionary(x => x.Id, x => _mapper.Map<NhBackgroundOperationStepViewModel>(x));
        foreach (var step in steps.Where(x => x.ParentStepId.HasValue))
        {
            if (stepViews.TryGetValue(step.ParentStepId!.Value, out var parent))
            {
                parent.Children.Add(stepViews[step.Id]);
            }
        }
        view.Steps = steps.Where(x => !x.ParentStepId.HasValue).Select(x => stepViews[x.Id]).ToList();
        var descendantIds = new HashSet<Guid> { operation.Id };
        var childViews = hierarchyCandidates.ToDictionary(
            x => x.Id,
            x => _mapper.Map<NhBackgroundOperationChildViewModel>(x));
        var added = true;
        while (added)
        {
            added = false;
            foreach (var child in hierarchyCandidates.Where(x => x.ParentOperationId.HasValue))
            {
                if (!descendantIds.Contains(child.ParentOperationId!.Value) || !descendantIds.Add(child.Id))
                {
                    continue;
                }

                if (child.ParentOperationId == operation.Id)
                {
                    view.Children.Add(childViews[child.Id]);
                }
                else if (childViews.TryGetValue(child.ParentOperationId.Value, out var parentView))
                {
                    parentView.Children.Add(childViews[child.Id]);
                }

                added = true;
            }
        }
        return view;
    }

    private static Expression<Func<NhBackgroundOperation, bool>> OwnedBy(Guid ownerUserId)
    {
        return x => x.OwnerUserId == ownerUserId;
    }

    private static Expression<Func<NhBackgroundOperation, bool>> VisibleInDivision(Guid? divisionId)
    {
        if (divisionId.HasValue)
        {
            var scopedDivisionId = divisionId.Value;
            return x => x.DivisionId == null || x.DivisionId == scopedDivisionId;
        }

        return x => x.DivisionId == null;
    }

    private Task<bool> AcquireOperationLockAsync(
        INhDbTransactionScope transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return _repository.TryAcquireTransactionLockAsync(
            transaction,
            $"NhBackgroundOperation:Operation:{operationId:N}",
            _options.TransactionLockTimeoutMilliseconds,
            cancellationToken);
    }

    internal static void Touch(NhBackgroundOperation operation, DateTimeOffset now)
    {
        operation.LastModifiedDateTime = now;
        operation.Version++;
    }

    internal static void AppendEvent(
        NhBackgroundOperation operation,
        NhBackgroundOperationEventType eventType,
        NhBackgroundOperationMessageSeverity severity,
        string? messageKey,
        object? messageArguments,
        bool milestone,
        Guid? attemptId = null,
        Guid? stepId = null,
        string? stepKey = null,
        NhBackgroundOperationResultReference? result = null,
        string? serializedMessageArguments = null)
    {
        operation.LatestEventSequence++;
        operation.Events.Add(new NhBackgroundOperationEvent
        {
            Id = Guid.NewGuid(),
            OperationId = operation.Id,
            Sequence = operation.LatestEventSequence,
            AttemptId = attemptId,
            StepId = stepId,
            StepKey = stepKey,
            EventType = eventType,
            Severity = severity,
            MessageKey = messageKey,
            MessageArgumentsJson = serializedMessageArguments
                ?? (messageArguments is null ? null : NhBackgroundOperationJson.Serialize(messageArguments)),
            SnapshotVersion = operation.Version,
            IsMilestone = milestone,
            ResultReferenceType = result?.Type,
            ResultReferenceId = result?.Id,
            ResultUrl = result?.Url
        });
    }

    internal static bool IsTerminal(NhBackgroundOperationStatus status)
    {
        return status is
            NhBackgroundOperationStatus.Succeeded or
            NhBackgroundOperationStatus.Failed or
            NhBackgroundOperationStatus.Cancelled or
            NhBackgroundOperationStatus.TimedOut;
    }

    private async Task PublishChangedSafelyAsync(NhBackgroundOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            var projectionResult = await _notificationProjector.ProjectAsync(operation.Id, cancellationToken);
            if (!projectionResult.Success)
            {
                _logger.LogWarning(
                    "User-notification projection was rejected for background operation {OperationId}: {@ProjectionErrors}",
                    operation.Id,
                    projectionResult.AllErrorMessages);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to project user notification for background operation {OperationId}", operation.Id);
        }
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
            _logger.LogWarning(exception, "Failed to publish live update for background operation {OperationId}", operation.Id);
        }
    }

    private static string NormalizeScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Idempotency scope is required.", nameof(scope));
        }
        var value = scope.Trim().ToLowerInvariant();
        return value.Length <= 100
            ? value
            : NhBackgroundOperationKeys.HashIdempotencyKey(value);
    }

    private static string? NormalizeOptionalKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        return NhBackgroundOperationKeys.HashResourceKey(key);
    }
}

internal sealed class NhNoOpBackgroundOperationLiveUpdatePublisher : INhBackgroundOperationLiveUpdatePublisher
{
    public Task PublishChangedAsync(
        Guid ownerUserId,
        NhBackgroundOperationChangedMessage message,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
