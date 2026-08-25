using System.Security.Cryptography;
using System.Text;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed class NhBackgroundOperationContext : INhBackgroundOperationContext
{
    private readonly NhBackgroundOperationAttemptClaim _claim;
    private readonly NhBackgroundOperationPersistence _persistence;

    internal NhBackgroundOperationContext(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationsOptions options,
        INhBackgroundOperationLeaseManager leases,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator)
    {
        _claim = claim;
        _persistence = persistence;
        Progress = new NhBackgroundOperationProgressContext(claim, persistence, options);
        Messages = new NhBackgroundOperationMessageSink(claim, persistence);
        Checkpoints = new NhBackgroundOperationCheckpointStore(claim, persistence);
        Leases = leases;
        Idempotency = new NhBackgroundOperationIdempotencyManager(claim, Checkpoints);
        FanOut = new NhBackgroundOperationFanOutContext(claim, fanOutCoordinator);
    }

    public Guid OperationId => _claim.OperationId;
    public Guid AttemptId => _claim.AttemptId;
    public int AttemptNumber => _claim.AttemptNumber;
    public long FencingToken => _claim.FencingToken;
    public string IdempotencyKey => $"nh-operation-{_claim.OperationId:N}";
    public INhBackgroundOperationProgressContext Progress { get; }
    public INhBackgroundOperationMessageSink Messages { get; }
    public INhBackgroundOperationCheckpointStore Checkpoints { get; }
    public INhBackgroundOperationLeaseManager Leases { get; }
    public INhBackgroundOperationIdempotencyManager Idempotency { get; }
    public INhBackgroundOperationFanOutContext FanOut { get; }

    public async Task ThrowIfCancellationRequestedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await _persistence.IsCancellationRequestedAsync(_claim, cancellationToken))
        {
            throw new OperationCanceledException("Background operation cancellation was requested.", cancellationToken);
        }
    }

    public Task SetResultAsync(
        NhBackgroundOperationResultReference result,
        CancellationToken cancellationToken = default)
    {
        return _persistence.SetResultAsync(_claim, result, cancellationToken);
    }
}

internal sealed class NhBackgroundOperationProgressContext : INhBackgroundOperationProgressContext
{
    private readonly NhBackgroundOperationAttemptClaim _claim;
    private readonly NhBackgroundOperationPersistence _persistence;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;

    internal NhBackgroundOperationProgressContext(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationsOptions options)
    {
        _claim = claim;
        _persistence = persistence;
        _options = options;
    }

    public async Task ReportAsync(
        decimal current,
        decimal total,
        string? messageKey = null,
        object? messageArguments = null,
        CancellationToken cancellationToken = default)
    {
        if (current != total && DateTimeOffset.UtcNow - _lastFlush < _options.ProgressFlushInterval)
        {
            return;
        }
        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            if (current != total && DateTimeOffset.UtcNow - _lastFlush < _options.ProgressFlushInterval)
            {
                return;
            }
            await _persistence.ReportRootAsync(_claim, current, total, messageKey, messageArguments, cancellationToken);
            _lastFlush = DateTimeOffset.UtcNow;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async Task DefineAsync(Action<NhBackgroundOperationProgressPlanBuilder> define, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(define);
        var builder = new NhBackgroundOperationProgressPlanBuilder();
        define(builder);
        if (builder.Steps.Count == 0)
        {
            throw new InvalidOperationException("A progress plan needs at least one step.");
        }
        await _persistence.DefinePlanAsync(_claim, builder.Steps, cancellationToken);
    }

    public async Task<TaskResult> RunStepAsync(
        string stepKey,
        Func<INhBackgroundOperationStepContext, CancellationToken, Task<TaskResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var stepId = await _persistence.StartStepAsync(_claim, null, stepKey, 1, cancellationToken);
        var context = new NhBackgroundOperationStepContext(_claim, _persistence, _options, stepId, stepKey);
        try
        {
            var result = await action(context, cancellationToken);
            await _persistence.CompleteStepAsync(
                _claim,
                stepId,
                result.Success
                    ? NhBackgroundOperationStepStatus.Succeeded
                    : NhBackgroundOperationStepStatus.Failed,
                CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException)
        {
            await _persistence.CompleteStepAsync(_claim, stepId, NhBackgroundOperationStepStatus.Cancelled, CancellationToken.None);
            throw;
        }
        catch
        {
            await _persistence.CompleteStepAsync(_claim, stepId, NhBackgroundOperationStepStatus.Failed, CancellationToken.None);
            throw;
        }
    }
}

internal sealed class NhBackgroundOperationStepContext : INhBackgroundOperationStepContext
{
    private readonly NhBackgroundOperationAttemptClaim _claim;
    private readonly NhBackgroundOperationPersistence _persistence;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;

    internal NhBackgroundOperationStepContext(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationsOptions options,
        Guid stepId,
        string stepKey)
    {
        _claim = claim;
        _persistence = persistence;
        _options = options;
        StepId = stepId;
        StepKey = stepKey;
    }

    public Guid StepId { get; }
    public string StepKey { get; }

    public async Task ReportAsync(
        decimal current,
        decimal total,
        string? messageKey = null,
        object? messageArguments = null,
        CancellationToken cancellationToken = default)
    {
        if (current != total && DateTimeOffset.UtcNow - _lastFlush < _options.ProgressFlushInterval)
        {
            return;
        }
        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            if (current != total && DateTimeOffset.UtcNow - _lastFlush < _options.ProgressFlushInterval)
            {
                return;
            }
            await _persistence.ReportStepAsync(_claim, StepId, current, total, messageKey, messageArguments, cancellationToken);
            _lastFlush = DateTimeOffset.UtcNow;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public Task<TaskResult> RunStepAsync(
        string stepKey,
        decimal weight,
        Func<INhBackgroundOperationStepContext, CancellationToken, Task<TaskResult>> action,
        CancellationToken cancellationToken = default)
    {
        return RunStepCoreAsync(stepKey, weight, null, action, cancellationToken);
    }

    public Task<TaskResult> RunStepAsync(
        string stepKey,
        decimal weight,
        string titleKey,
        Func<INhBackgroundOperationStepContext, CancellationToken, Task<TaskResult>> action,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleKey))
        {
            throw new ArgumentException("A nested progress-step title key is required.", nameof(titleKey));
        }

        return RunStepCoreAsync(stepKey, weight, titleKey, action, cancellationToken);
    }

    private async Task<TaskResult> RunStepCoreAsync(
        string stepKey,
        decimal weight,
        string? titleKey,
        Func<INhBackgroundOperationStepContext, CancellationToken, Task<TaskResult>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var childId = await _persistence.StartStepAsync(
            _claim, StepId, stepKey, weight, cancellationToken, titleKey);
        var context = new NhBackgroundOperationStepContext(_claim, _persistence, _options, childId, stepKey);
        try
        {
            var result = await action(context, cancellationToken);
            await _persistence.CompleteStepAsync(
                _claim,
                childId,
                result.Success
                    ? NhBackgroundOperationStepStatus.Succeeded
                    : NhBackgroundOperationStepStatus.Failed,
                CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException)
        {
            await _persistence.CompleteStepAsync(_claim, childId, NhBackgroundOperationStepStatus.Cancelled, CancellationToken.None);
            throw;
        }
        catch
        {
            await _persistence.CompleteStepAsync(_claim, childId, NhBackgroundOperationStepStatus.Failed, CancellationToken.None);
            throw;
        }
    }

    public Task<INhBackgroundOperationBatchContext> BeginBatchAsync(
        long? totalItems,
        NhBackgroundOperationBatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (totalItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalItems));
        }
        options ??= new NhBackgroundOperationBatchOptions();
        if (options.FlushEveryItems < 1 || options.FlushInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("Batch flush settings must be positive.", nameof(options));
        }

        INhBackgroundOperationBatchContext batch = new NhBackgroundOperationBatchContext(
            _claim, _persistence, StepId, totalItems, options);
        return Task.FromResult(batch);
    }
}

internal sealed class NhBackgroundOperationBatchContext : INhBackgroundOperationBatchContext
{
    private readonly NhBackgroundOperationAttemptClaim _claim;
    private readonly NhBackgroundOperationPersistence _persistence;
    private readonly Guid _stepId;
    private readonly NhBackgroundOperationBatchOptions _options;
    private readonly long? _total;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private long _discovered;
    private long _processed;
    private long _succeeded;
    private long _failed;
    private long _skipped;
    private long _retried;
    private long _active;
    private long _lastFlushedProcessed;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
    private bool _disposed;

    internal NhBackgroundOperationBatchContext(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationPersistence persistence,
        Guid stepId,
        long? total,
        NhBackgroundOperationBatchOptions options)
    {
        _claim = claim;
        _persistence = persistence;
        _stepId = stepId;
        _total = total;
        _options = options;
        _discovered = total ?? 0;
    }

    public NhBackgroundOperationBatchSnapshot Snapshot => new(
        Interlocked.Read(ref _discovered),
        Interlocked.Read(ref _processed),
        Interlocked.Read(ref _succeeded),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _skipped),
        Interlocked.Read(ref _retried),
        Interlocked.Read(ref _active),
        _total);

    public Task ItemStartedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_total.HasValue)
        {
            Interlocked.Increment(ref _discovered);
        }
        Interlocked.Increment(ref _active);
        return Task.CompletedTask;
    }

    public async Task ItemSucceededAsync(CancellationToken cancellationToken = default)
    {
        CompleteItem(ref _succeeded);
        await FlushWhenDueAsync(cancellationToken);
    }

    public async Task<TaskResult> ItemFailedAsync(
        string? safeFailureCode = null,
        CancellationToken cancellationToken = default)
    {
        CompleteItem(ref _failed);
        await FlushWhenDueAsync(cancellationToken);
        var failed = Interlocked.Read(ref _failed);
        if (!_options.ContinueOnItemFailure || failed > _options.MaximumToleratedFailures)
        {
            return TaskResult.Failed(
                safeFailureCode ?? "batch-item-failed",
                "background-operation.batch-failure-limit-exceeded");
        }

        return TaskResult.Succeeded();
    }

    public async Task ItemSkippedAsync(CancellationToken cancellationToken = default)
    {
        CompleteItem(ref _skipped);
        await FlushWhenDueAsync(cancellationToken);
    }

    public Task ItemRetriedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _retried);
        return FlushWhenDueAsync(cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Snapshot;
            await _persistence.FlushBatchAsync(_claim, _stepId, snapshot, cancellationToken);
            _lastFlushedProcessed = snapshot.Processed;
            _lastFlush = DateTimeOffset.UtcNow;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await FlushAsync(CancellationToken.None);
        _disposed = true;
        _flushLock.Dispose();
    }

    private void CompleteItem(ref long counter)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref counter);
        Interlocked.Increment(ref _processed);
        Interlocked.Decrement(ref _active);
    }

    private Task FlushWhenDueAsync(CancellationToken cancellationToken)
    {
        var processed = Interlocked.Read(ref _processed);
        return processed - Interlocked.Read(ref _lastFlushedProcessed) >= _options.FlushEveryItems
               || DateTimeOffset.UtcNow - _lastFlush >= _options.FlushInterval
            ? FlushAsync(cancellationToken)
            : Task.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NhBackgroundOperationBatchContext));
        }
    }
}

internal sealed class NhBackgroundOperationMessageSink : INhBackgroundOperationMessageSink
{
    private readonly NhBackgroundOperationAttemptClaim _claim;
    private readonly NhBackgroundOperationPersistence _persistence;

    internal NhBackgroundOperationMessageSink(NhBackgroundOperationAttemptClaim claim, NhBackgroundOperationPersistence persistence)
    {
        _claim = claim;
        _persistence = persistence;
    }

    public Task PublishAsync(
        NhBackgroundOperationMessage message,
        CancellationToken cancellationToken = default)
    {
        return _persistence.PublishMessageAsync(_claim, message, cancellationToken);
    }
}

internal sealed class NhBackgroundOperationCheckpointStore : INhBackgroundOperationCheckpointStore
{
    private readonly NhBackgroundOperationAttemptClaim _claim;
    private readonly NhBackgroundOperationPersistence _persistence;

    internal NhBackgroundOperationCheckpointStore(NhBackgroundOperationAttemptClaim claim, NhBackgroundOperationPersistence persistence)
    {
        _claim = claim;
        _persistence = persistence;
    }

    public Task<NhBackgroundOperationCheckpointValue<T>?> GetAsync<T>(
        string checkpointKey,
        CancellationToken cancellationToken = default)
    {
        return _persistence.GetCheckpointAsync<T>(_claim, checkpointKey, cancellationToken);
    }

    public Task<TaskResult> SetAsync<T>(
        string checkpointKey,
        T value,
        int schemaVersion = 1,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        return _persistence.SetCheckpointAsync(
            _claim,
            checkpointKey,
            value,
            schemaVersion,
            expectedVersion,
            cancellationToken);
    }
}

internal sealed record NhIdempotentStepCheckpoint(bool Completed);

internal sealed class NhBackgroundOperationIdempotencyManager : INhBackgroundOperationIdempotencyManager
{
    private readonly NhBackgroundOperationAttemptClaim _claim;
    private readonly INhBackgroundOperationCheckpointStore _checkpoints;

    internal NhBackgroundOperationIdempotencyManager(
        NhBackgroundOperationAttemptClaim claim,
        INhBackgroundOperationCheckpointStore checkpoints)
    {
        _claim = claim;
        _checkpoints = checkpoints;
    }

    public string GetExternalKey(string stepKey, string? itemKey = null)
    {
        NhBackgroundOperationKeys.ValidateStepKey(stepKey);
        var source = $"{_claim.OperationId:N}:{stepKey}:{itemKey?.Trim() ?? string.Empty}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    public async Task<NhBackgroundOperationIdempotentStep> BeginStepAsync(
        string stepKey,
        string? itemKey = null,
        CancellationToken cancellationToken = default)
    {
        var externalKey = GetExternalKey(stepKey, itemKey);
        var checkpointKey = $"idempotency-{externalKey[..40]}";
        var checkpoint = await _checkpoints.GetAsync<NhIdempotentStepCheckpoint>(checkpointKey, cancellationToken);
        return new NhBackgroundOperationIdempotentStep(
            stepKey,
            externalKey,
            checkpoint?.Value.Completed == true,
            completeCancellationToken => _checkpoints.SetAsync(
                checkpointKey,
                new NhIdempotentStepCheckpoint(true),
                1,
                checkpoint?.Version,
                completeCancellationToken));
    }
}
