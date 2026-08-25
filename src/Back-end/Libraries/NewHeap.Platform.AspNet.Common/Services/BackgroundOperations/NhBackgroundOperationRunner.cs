using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

public sealed class NhBackgroundOperationRunner
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationPersistence _persistence;
    private readonly NhBackgroundOperationRegistry _registry;
    private readonly NhBackgroundOperationFanOutCoordinator _fanOutCoordinator;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly ILogger<NhBackgroundOperationRunner> _logger;

    internal NhBackgroundOperationRunner(
        IServiceProvider serviceProvider,
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        NhBackgroundOperationsOptions options,
        ILogger<NhBackgroundOperationRunner> logger)
    {
        _serviceProvider = serviceProvider;
        _scopeFactory = scopeFactory;
        _persistence = persistence;
        _registry = registry;
        _fanOutCoordinator = fanOutCoordinator;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(Guid operationId, int dispatchGeneration)
    {
        var claim = await _persistence.TryStartAttemptAsync(operationId, dispatchGeneration, CancellationToken.None);
        if (claim is null)
        {
            return;
        }

        using var operationScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["operation_id"] = claim.OperationId,
            ["attempt_id"] = claim.AttemptId,
            ["operation_type"] = claim.OperationType,
            ["queue"] = claim.Queue
        });

        if (!_registry.TryGetForOperationType(claim.OperationType, out var descriptor))
        {
            await _persistence.CompleteAsync(
                claim,
                NhBackgroundOperationStatus.Failed,
                "unregistered-operation-type",
                "background-operation.unregistered-operation-type",
                null,
                null,
                CancellationToken.None);
            return;
        }

        if (claim.PayloadSchemaVersion != descriptor.PayloadSchemaVersion)
        {
            await _persistence.CompleteAsync(
                claim,
                NhBackgroundOperationStatus.Failed,
                "unsupported-payload-schema",
                "background-operation.unsupported-payload-schema",
                null,
                null,
                CancellationToken.None);
            return;
        }
        var leaseManager = new NhBackgroundOperationLeaseManager(_scopeFactory, _options, claim);
        var leases = new List<INhBackgroundOperationLeaseHandle>();
        try
        {
            if (_options.MaxConcurrentOperations != int.MaxValue)
            {
                var globalLease = await leaseManager.AcquireAsync(
                    $"processor:{_options.ProcessorKey}",
                    new NhBackgroundOperationLeaseOptions { Slots = _options.MaxConcurrentOperations },
                    CancellationToken.None);
                if (globalLease is null)
                {
                    await RescheduleForContentionAsync(claim);
                    return;
                }
                leases.Add(globalLease);
            }
            if (_options.QueueConcurrencyLimits.TryGetValue(claim.Queue, out var queueConcurrency))
            {
                var queueLease = await leaseManager.AcquireAsync(
                    $"queue:{claim.Queue}",
                    new NhBackgroundOperationLeaseOptions { Slots = queueConcurrency },
                    CancellationToken.None);
                if (queueLease is null)
                {
                    await RescheduleForContentionAsync(claim);
                    return;
                }
                leases.Add(queueLease);
            }
            if (descriptor.MaxConcurrency != int.MaxValue)
            {
                var typeLease = await leaseManager.AcquireAsync(
                    $"operation-type:{descriptor.OperationType}",
                    new NhBackgroundOperationLeaseOptions { Slots = descriptor.MaxConcurrency },
                    CancellationToken.None);
                if (typeLease is null)
                {
                    await RescheduleForContentionAsync(claim);
                    return;
                }
                leases.Add(typeLease);
            }
            if (!string.IsNullOrWhiteSpace(claim.ConcurrencyKey))
            {
                var keyLease = await leaseManager.AcquireAsync(
                    claim.ConcurrencyKey,
                    new NhBackgroundOperationLeaseOptions { Slots = 1 },
                    CancellationToken.None);
                if (keyLease is null)
                {
                    await RescheduleForContentionAsync(claim);
                    return;
                }
                leases.Add(keyLease);
            }

            var timeout = descriptor.SoftTimeout ?? _options.DefaultSoftTimeout;
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token);
            var stopState = new NhBackgroundOperationExecutionStopState();
            var heartbeatTask = RunHeartbeatAsync(
                claim,
                leases,
                executionSource,
                stopState);

            try
            {
                var request = JsonSerializer.Deserialize(
                    claim.PayloadJson,
                    descriptor.RequestType,
                    NhBackgroundOperationJson.Options)
                    ?? throw new InvalidOperationException("The persisted background operation payload is empty.");
                var handler = _serviceProvider.GetRequiredService(descriptor.HandlerType);
                var context = new NhBackgroundOperationContext(
                    claim,
                    _persistence,
                    _options,
                    leaseManager,
                    _fanOutCoordinator);
                var result = await InvokeHandlerAsync(
                    descriptor,
                    handler,
                    request,
                    context,
                    executionSource.Token);
                if (await HandleExecutionStopAsync(claim, descriptor, stopState))
                {
                    return;
                }

                if (timeoutSource.IsCancellationRequested)
                {
                    await CompleteTimedOutAsync(claim);
                    return;
                }

                var failure = GetFailure(result);
                var retryAt = result is NhBackgroundOperationRetryResult retryResult
                    ? await GetRetryAtAsync(claim, descriptor, retryResult.RetryAfter)
                    : null;
                await _persistence.CompleteAsync(
                    claim,
                    result.Success
                        ? NhBackgroundOperationStatus.Succeeded
                        : NhBackgroundOperationStatus.Failed,
                    failure?.Code,
                    failure?.MessageKey,
                    null,
                    retryAt,
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                if (!await HandleExecutionStopAsync(claim, descriptor, stopState))
                {
                    await CompleteTimedOutAsync(claim);
                }
            }
            catch (NhBackgroundOperationContentionSignal exception)
            {
                await RescheduleForContentionAsync(claim, exception.RetryAfter);
            }
            catch (NhBackgroundOperationFanOutPendingException)
            {
                // The fan-out coordinator durably suspended this attempt. The
                // final child wakes the parent without consuming a retry or a
                // worker slot while child operations are active.
            }
            catch (Exception exception)
            {
                await CompleteUnexpectedFailureAsync(
                    claim,
                    descriptor,
                    exception,
                    "handler-failed");
            }
            finally
            {
                executionSource.Cancel();
                await heartbeatTask;
            }
        }
        finally
        {
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                await leases[index].DisposeAsync();
            }
        }
    }

    private async Task RunHeartbeatAsync(
        NhBackgroundOperationAttemptClaim claim,
        IReadOnlyList<INhBackgroundOperationLeaseHandle> leases,
        CancellationTokenSource executionSource,
        NhBackgroundOperationExecutionStopState stopState)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(executionSource.Token))
            {
                var heartbeatResult = await _persistence.HeartbeatAsync(claim, CancellationToken.None);
                if (heartbeatResult == NhBackgroundOperationHeartbeatResult.CancellationRequested)
                {
                    stopState.TrySet(NhBackgroundOperationExecutionStopReason.DurableCancellation);
                    executionSource.Cancel();
                    return;
                }

                if (heartbeatResult == NhBackgroundOperationHeartbeatResult.OwnershipLost)
                {
                    stopState.TrySet(NhBackgroundOperationExecutionStopReason.OwnershipLost);
                    executionSource.Cancel();
                    return;
                }

                foreach (var lease in leases)
                {
                    await lease.RenewAsync(CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (executionSource.IsCancellationRequested)
        {
        }
        catch (NhBackgroundOperationContentionSignal exception)
        {
            stopState.TrySet(
                NhBackgroundOperationExecutionStopReason.Contention,
                exception);
            executionSource.Cancel();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Heartbeat failed for background operation {OperationId} attempt {AttemptId}.", claim.OperationId, claim.AttemptId);
            stopState.TrySet(
                NhBackgroundOperationExecutionStopReason.Failure,
                exception);
            executionSource.Cancel();
        }
    }

    private async Task<bool> HandleExecutionStopAsync(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationDescriptor descriptor,
        NhBackgroundOperationExecutionStopState stopState)
    {
        var stop = stopState.Get();
        switch (stop.Reason)
        {
            case NhBackgroundOperationExecutionStopReason.None:
                return false;
            case NhBackgroundOperationExecutionStopReason.DurableCancellation:
                await _persistence.CompleteAsync(
                    claim,
                    NhBackgroundOperationStatus.Cancelled,
                    "cancelled",
                    "background-operation.cancelled",
                    null,
                    null,
                    CancellationToken.None);
                return true;
            case NhBackgroundOperationExecutionStopReason.Contention:
                var contention = (NhBackgroundOperationContentionSignal?)stop.Exception;
                await RescheduleForContentionAsync(claim, contention?.RetryAfter);
                return true;
            case NhBackgroundOperationExecutionStopReason.OwnershipLost:
                _logger.LogWarning(
                    "Background operation {OperationId} attempt {AttemptId} stopped after losing durable ownership.",
                    claim.OperationId,
                    claim.AttemptId);
                return true;
            case NhBackgroundOperationExecutionStopReason.Failure:
                await CompleteUnexpectedFailureAsync(
                    claim,
                    descriptor,
                    stop.Exception ?? new InvalidOperationException("The operation heartbeat failed without diagnostic information."),
                    "heartbeat-failed");
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(stop.Reason));
        }
    }

    private Task CompleteTimedOutAsync(NhBackgroundOperationAttemptClaim claim)
    {
        return _persistence.CompleteAsync(
            claim,
            NhBackgroundOperationStatus.TimedOut,
            "soft-timeout",
            "background-operation.timed-out",
            null,
            null,
            CancellationToken.None);
    }

    private async Task CompleteUnexpectedFailureAsync(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationDescriptor descriptor,
        Exception exception,
        string failureCode)
    {
        var diagnosticCorrelationId = Guid.NewGuid().ToString("N");
        _logger.LogError(
            exception,
            "Background operation {OperationId} attempt {AttemptId} failed. Diagnostic correlation {DiagnosticCorrelationId}.",
            claim.OperationId,
            claim.AttemptId,
            diagnosticCorrelationId);
        var failedAttempts = await _persistence.GetFailedAttemptCountAsync(
            claim.OperationId,
            CancellationToken.None) + 1;
        var retryAt = failedAttempts <= descriptor.RetryCount
            ? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, failedAttempts)))
            : (DateTimeOffset?)null;
        await _persistence.CompleteAsync(
            claim,
            NhBackgroundOperationStatus.Failed,
            failureCode,
            "background-operation.failed",
            diagnosticCorrelationId,
            retryAt,
            CancellationToken.None);
    }

    private async Task RescheduleForContentionAsync(
        NhBackgroundOperationAttemptClaim claim,
        TimeSpan? retryAfter = null)
    {
        await _persistence.CompleteAsync(
            claim,
            NhBackgroundOperationStatus.Failed,
            "concurrency-busy",
            "background-operation.waiting-for-resource",
            null,
            DateTimeOffset.UtcNow + (retryAfter ?? TimeSpan.FromSeconds(2)),
            CancellationToken.None,
            abandoned: true);
    }

    private static async Task<TaskResult> InvokeHandlerAsync(
        NhBackgroundOperationDescriptor descriptor,
        object handler,
        object request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        var contract = typeof(INhBackgroundOperationHandler<>).MakeGenericType(descriptor.RequestType);
        var method = contract.GetMethod(nameof(INhBackgroundOperationHandler<object>.ExecuteAsync))
            ?? throw new MissingMethodException(contract.FullName, nameof(INhBackgroundOperationHandler<object>.ExecuteAsync));
        try
        {
            var task = (Task<TaskResult>?)method.Invoke(handler, [request, context, cancellationToken])
                ?? throw new InvalidOperationException("The background operation handler returned no result task.");
            return await task;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static NhBackgroundOperationFailure? GetFailure(TaskResult result)
    {
        if (result.Success)
        {
            return null;
        }

        var resultItem = result.GetResultItems()
            .FirstOrDefault(item => item.ErrorMessages.Count > 0);
        var failureCode = string.IsNullOrWhiteSpace(resultItem?.Name)
            || resultItem.Name.Length > 200
                ? "handler-rejected"
                : resultItem.Name;
        var messageKey = resultItem?.ErrorMessages.FirstOrDefault()?.Format;
        return new NhBackgroundOperationFailure(
            failureCode,
            string.IsNullOrWhiteSpace(messageKey)
                ? "background-operation.failed"
                : messageKey);
    }

    private async Task<DateTimeOffset?> GetRetryAtAsync(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationDescriptor descriptor,
        TimeSpan? requestedDelay)
    {
        var failedAttempts = await _persistence.GetFailedAttemptCountAsync(
            claim.OperationId,
            CancellationToken.None) + 1;
        if (failedAttempts > descriptor.RetryCount)
        {
            return null;
        }

        var retryDelay = requestedDelay
            ?? TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, failedAttempts)));
        return DateTimeOffset.UtcNow + retryDelay;
    }

    private sealed record NhBackgroundOperationFailure(string Code, string MessageKey);

    private enum NhBackgroundOperationExecutionStopReason
    {
        None,
        DurableCancellation,
        Contention,
        OwnershipLost,
        Failure
    }

    private sealed class NhBackgroundOperationExecutionStopState
    {
        private readonly object _lock = new();
        private NhBackgroundOperationExecutionStopReason _reason;
        private Exception? _exception;

        internal void TrySet(
            NhBackgroundOperationExecutionStopReason reason,
            Exception? exception = null)
        {
            lock (_lock)
            {
                if (_reason != NhBackgroundOperationExecutionStopReason.None)
                {
                    return;
                }

                _reason = reason;
                _exception = exception;
            }
        }

        internal (NhBackgroundOperationExecutionStopReason Reason, Exception? Exception) Get()
        {
            lock (_lock)
            {
                return (_reason, _exception);
            }
        }
    }
}
