using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

public interface INhBackgroundOperationHandler<in TRequest>
{
    /// <summary>
    /// Executes the durable operation. Return a failed <see cref="TaskResult"/>
    /// for an expected business outcome that must end the operation without an
    /// automatic retry. Throw only for cancellation or an unexpected failure;
    /// unexpected failures follow the configured retry policy.
    /// </summary>
    Task<TaskResult> ExecuteAsync(
        TRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// A failed task result that explicitly asks the runner to apply the operation's
/// configured retry policy. Use this for a known transient outcome that does not
/// need an exception and can be retried safely.
/// </summary>
public sealed class NhBackgroundOperationRetryResult : TaskResult
{
    private NhBackgroundOperationRetryResult(TimeSpan? retryAfter)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }

    public static NhBackgroundOperationRetryResult Retry(
        string failureCode,
        string messageKey,
        TimeSpan? retryAfter = null)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException("A safe failure code is required.", nameof(failureCode));
        }

        if (string.IsNullOrWhiteSpace(messageKey))
        {
            throw new ArgumentException("A safe failure message key is required.", nameof(messageKey));
        }

        if (retryAfter.HasValue && retryAfter.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        }

        var result = new NhBackgroundOperationRetryResult(retryAfter);
        result.AddError(failureCode, messageKey);
        return result;
    }
}

public interface INhBackgroundOperationContext
{
    Guid OperationId { get; }
    Guid AttemptId { get; }
    int AttemptNumber { get; }
    long FencingToken { get; }
    string IdempotencyKey { get; }
    INhBackgroundOperationProgressContext Progress { get; }
    INhBackgroundOperationMessageSink Messages { get; }
    INhBackgroundOperationCheckpointStore Checkpoints { get; }
    INhBackgroundOperationLeaseManager Leases { get; }
    INhBackgroundOperationIdempotencyManager Idempotency { get; }
    INhBackgroundOperationFanOutContext FanOut { get; }
    INhBackgroundOperationSuspensionContext Suspension { get; }

    Task ThrowIfCancellationRequestedAsync(CancellationToken cancellationToken = default);
    Task SetResultAsync(
        NhBackgroundOperationResultReference result,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durably suspends an operation until an authorized application service
/// supplies a typed signal or the wait expires. The handler is re-entered from
/// its beginning after wake-up, so work before this call must be repeatable or
/// protected by checkpoints and idempotency.
/// </summary>
public interface INhBackgroundOperationSuspensionContext
{
    Task<NhBackgroundOperationSignalWaitResult<TSignal>> WaitForSignalAsync<TSignal>(
        string waitKey,
        DateTimeOffset expiresAt,
        int signalSchemaVersion = 1,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists a signal and wakes a suspended operation. Application code must
/// authorize the signaling actor before calling this service; the owner ID is
/// matched again at the persistence boundary.
/// </summary>
public interface INhBackgroundOperationSignalService
{
    Task<TaskResult<NhBackgroundOperationSignalWriteResult>> SignalForOwnerAsync<TSignal>(
        Guid operationId,
        Guid ownerUserId,
        Guid signaledByUserId,
        string waitKey,
        TSignal signal,
        int signalSchemaVersion = 1,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationProgressContext
{
    Task ReportAsync(
        decimal current,
        decimal total,
        string? messageKey = null,
        object? messageArguments = null,
        CancellationToken cancellationToken = default);

    Task DefineAsync(
        Action<NhBackgroundOperationProgressPlanBuilder> define,
        CancellationToken cancellationToken = default);

    Task<TaskResult> RunStepAsync(
        string stepKey,
        Func<INhBackgroundOperationStepContext, CancellationToken, Task<TaskResult>> action,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationStepContext
{
    Guid StepId { get; }
    string StepKey { get; }

    Task ReportAsync(
        decimal current,
        decimal total,
        string? messageKey = null,
        object? messageArguments = null,
        CancellationToken cancellationToken = default);

    Task<TaskResult> RunStepAsync(
        string stepKey,
        decimal weight,
        Func<INhBackgroundOperationStepContext, CancellationToken, Task<TaskResult>> action,
        CancellationToken cancellationToken = default);

    Task<TaskResult> RunStepAsync(
        string stepKey,
        decimal weight,
        string titleKey,
        Func<INhBackgroundOperationStepContext, CancellationToken, Task<TaskResult>> action,
        CancellationToken cancellationToken = default);

    Task<INhBackgroundOperationBatchContext> BeginBatchAsync(
        long? totalItems,
        NhBackgroundOperationBatchOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationBatchContext : IAsyncDisposable
{
    NhBackgroundOperationBatchSnapshot Snapshot { get; }
    Task ItemStartedAsync(CancellationToken cancellationToken = default);
    Task ItemSucceededAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Records an item failure. The result succeeds while the configured batch
    /// policy allows processing to continue, and fails once processing must stop.
    /// </summary>
    Task<TaskResult> ItemFailedAsync(
        string? safeFailureCode = null,
        CancellationToken cancellationToken = default);
    Task ItemSkippedAsync(CancellationToken cancellationToken = default);
    Task ItemRetriedAsync(CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates a durable set of child operations and resumes the parent only after
/// every child is terminal. Owner, division, priority, correlation, queue and
/// concurrency policies are inherited or resolved automatically. The parent
/// handler is re-entered from its beginning after suspension, so work before
/// the fan-out call must be repeatable or protected by checkpoints/idempotency.
/// </summary>
public interface INhBackgroundOperationFanOutContext
{
    Task<TaskResult<NhBackgroundOperationFanOutResult>> RunAsync<TRequest>(
        string fanOutKey,
        IEnumerable<NhBackgroundOperationFanOutItem<TRequest>> items,
        CancellationToken cancellationToken = default);

    Task<TaskResult<NhBackgroundOperationFanOutResult>> RunAsync<TRequest>(
        string fanOutKey,
        IEnumerable<NhBackgroundOperationFanOutItem<TRequest>> items,
        NhBackgroundOperationFanOutOptions options,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationMessageSink
{
    Task PublishAsync(
        NhBackgroundOperationMessage message,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationCheckpointStore
{
    Task<NhBackgroundOperationCheckpointValue<T>?> GetAsync<T>(
        string checkpointKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a checkpoint. Compare-and-set conflicts and values exceeding the
    /// configured persistence limit are expected failures returned as a result.
    /// </summary>
    Task<TaskResult> SetAsync<T>(
        string checkpointKey,
        T value,
        int schemaVersion = 1,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationLeaseManager
{
    Task<INhBackgroundOperationLeaseHandle?> AcquireAsync(
        string resourceKey,
        NhBackgroundOperationLeaseOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<INhBackgroundOperationLeaseHandle> AcquireRequiredAsync(
        string resourceKey,
        NhBackgroundOperationLeaseOptions? options = null,
        TimeSpan? rescheduleDelay = null,
        CancellationToken cancellationToken = default);

    Task<INhBackgroundOperationLeaseSet?> AcquireManyAsync(
        IEnumerable<string> resourceKeys,
        NhBackgroundOperationLeaseOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<INhBackgroundOperationLeaseSet> AcquireManyRequiredAsync(
        IEnumerable<string> resourceKeys,
        NhBackgroundOperationLeaseOptions? options = null,
        TimeSpan? rescheduleDelay = null,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationLeaseHandle : IAsyncDisposable
{
    string ResourceKey { get; }
    int Slot { get; }
    long FencingToken { get; }
    DateTimeOffset ExpiresAt { get; }
    Task RenewAsync(CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationLeaseSet : IAsyncDisposable
{
    IReadOnlyList<INhBackgroundOperationLeaseHandle> Leases { get; }
}

public interface INhBackgroundOperationIdempotencyManager
{
    string GetExternalKey(string stepKey, string? itemKey = null);
    Task<NhBackgroundOperationIdempotentStep> BeginStepAsync(
        string stepKey,
        string? itemKey = null,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationService
{
    Task<TaskResult<NhBackgroundOperationViewModel>> EnqueueAsync<TRequest>(
        TRequest request,
        NhBackgroundOperationEnqueueOptions options,
        CancellationToken cancellationToken = default);

    Task<NhBackgroundOperationViewModel?> GetAsync(
        Guid operationId,
        Guid ownerUserId,
        long? eventsAfterSequence = null,
        CancellationToken cancellationToken = default);

    IQueryable<NhBackgroundOperation> QueryForOwner(Guid ownerUserId, Guid? divisionId = null);

    Task<TaskResult<NhBackgroundOperationViewModel>> RequestCancellationAsync(
        Guid operationId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<TaskResult<NhBackgroundOperationViewModel>> RetryAsync(
        Guid operationId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationScheduler
{
    Task<NhBackgroundOperationScheduleResult> EnqueueAsync(
        Guid operationId,
        int dispatchGeneration,
        string queue,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string schedulerJobId, CancellationToken cancellationToken = default);

    Task<NhBackgroundOperationExecutionState?> GetStateAsync(
        string schedulerJobId,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationLiveUpdatePublisher
{
    Task PublishChangedAsync(
        Guid ownerUserId,
        NhBackgroundOperationChangedMessage message,
        CancellationToken cancellationToken = default);
}

public interface INhBackgroundOperationNotificationProjector
{
    Task<TaskResult> ProjectAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Formats milestone notifications. Applications can replace the default to
/// apply the operation owner's language without changing operation execution.
/// </summary>
public interface INhBackgroundOperationNotificationFormatter
{
    Task<NhBackgroundOperationNotificationContent> FormatAsync(
        NhBackgroundOperation operation,
        NhBackgroundOperationEvent milestone,
        CancellationToken cancellationToken = default);
}

public sealed record NhBackgroundOperationScheduleResult(string SchedulerJobId);

public sealed record NhBackgroundOperationExecutionState(string Name, bool IsTerminal);

public sealed record NhBackgroundOperationChangedMessage(
    Guid OperationId,
    long Version,
    long LatestEventSequence,
    NhBackgroundOperationStatus Status,
    Guid? DivisionId = null);

public sealed record NhBackgroundOperationNotificationContent(string Title, string Message);

public sealed record NhBackgroundOperationResultReference(
    string Type,
    string Id,
    string? Url = null);

public sealed record NhBackgroundOperationCheckpointValue<T>(
    T Value,
    int SchemaVersion,
    long Version);

public enum NhBackgroundOperationSignalWaitStatus
{
    Signaled = 0,
    Expired = 10
}

public sealed record NhBackgroundOperationSignalWaitResult<TSignal>(
    NhBackgroundOperationSignalWaitStatus Status,
    TSignal? Signal,
    Guid? SignaledByUserId,
    DateTimeOffset? SignaledAt,
    DateTimeOffset ExpiresAt);

public enum NhBackgroundOperationSignalWriteStatus
{
    Accepted = 0,
    Duplicate = 10
}

public sealed record NhBackgroundOperationSignalWriteResult(
    NhBackgroundOperationSignalWriteStatus Status,
    Guid OperationId,
    string WaitKey,
    DateTimeOffset SignaledAt);

public sealed record NhBackgroundOperationBatchSnapshot(
    long Discovered,
    long Processed,
    long Succeeded,
    long Failed,
    long Skipped,
    long Retried,
    long Active,
    long? Total);

public sealed record NhBackgroundOperationFanOutItem<TRequest>(
    string ItemKey,
    TRequest Request);

public static class NhBackgroundOperationFanOut
{
    public static NhBackgroundOperationFanOutItem<TRequest> Item<TRequest>(
        string itemKey,
        TRequest request)
    {
        return new NhBackgroundOperationFanOutItem<TRequest>(itemKey, request);
    }
}

public enum NhBackgroundOperationFanOutFailureMode
{
    FailParent = 0,
    Continue = 10
}

public sealed class NhBackgroundOperationFanOutOptions
{
    public string? TitleKey { get; init; }
    public NhBackgroundOperationFanOutFailureMode FailureMode { get; init; } =
        NhBackgroundOperationFanOutFailureMode.FailParent;
}

public sealed record NhBackgroundOperationFanOutChildResult(
    Guid OperationId,
    string ItemKey,
    NhBackgroundOperationStatus Status,
    decimal? ProgressPercentage,
    NhBackgroundOperationResultReference? Result,
    string? FailureCode);

public sealed record NhBackgroundOperationFanOutResult(
    int Total,
    int Succeeded,
    int Failed,
    int Cancelled,
    IReadOnlyList<NhBackgroundOperationFanOutChildResult> Children)
{
    public bool HasFailures => Failed > 0 || Cancelled > 0;
}

public sealed class NhBackgroundOperationIdempotentStep
{
    private readonly Func<CancellationToken, Task<TaskResult>> _complete;

    internal NhBackgroundOperationIdempotentStep(
        string stepKey,
        string externalIdempotencyKey,
        bool alreadyCompleted,
        Func<CancellationToken, Task<TaskResult>> complete)
    {
        StepKey = stepKey;
        ExternalIdempotencyKey = externalIdempotencyKey;
        AlreadyCompleted = alreadyCompleted;
        _complete = complete;
    }

    public string StepKey { get; }
    public string ExternalIdempotencyKey { get; }
    public bool AlreadyCompleted { get; }

    /// <summary>
    /// Records completion of this step. This closes duplicate retries after the
    /// record is committed, but cannot atomically close the crash window around
    /// a non-transactional external side effect. Pass <see cref="ExternalIdempotencyKey"/>
    /// to that external system whenever it supports idempotency.
    /// </summary>
    public Task<TaskResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        return _complete(cancellationToken);
    }
}

public sealed record NhBackgroundOperationMessage(
    NhBackgroundOperationMessageSeverity Severity,
    string MessageKey,
    object? Arguments = null,
    bool IsMilestone = false,
    bool IsOperatorOnly = false,
    NhBackgroundOperationResultReference? Result = null)
{
    public static NhBackgroundOperationMessage Info(string key, object? arguments = null, bool milestone = false)
    {
        return new NhBackgroundOperationMessage(
            NhBackgroundOperationMessageSeverity.Information,
            key,
            arguments,
            milestone);
    }

    public static NhBackgroundOperationMessage Success(string key, object? arguments = null, bool milestone = true)
    {
        return new NhBackgroundOperationMessage(
            NhBackgroundOperationMessageSeverity.Success,
            key,
            arguments,
            milestone);
    }

    public static NhBackgroundOperationMessage Warning(string key, object? arguments = null, bool milestone = false)
    {
        return new NhBackgroundOperationMessage(
            NhBackgroundOperationMessageSeverity.Warning,
            key,
            arguments,
            milestone);
    }

    public static NhBackgroundOperationMessage Error(string key, object? arguments = null, bool milestone = true)
    {
        return new NhBackgroundOperationMessage(
            NhBackgroundOperationMessageSeverity.Error,
            key,
            arguments,
            milestone);
    }
}
