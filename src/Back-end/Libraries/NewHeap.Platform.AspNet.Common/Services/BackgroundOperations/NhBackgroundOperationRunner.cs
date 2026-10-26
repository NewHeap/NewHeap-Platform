using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

public sealed class NhBackgroundOperationRunner
{
    private const string HandlerRejectedFailureCode = "handler-rejected";
    private const string GenericFailureMessageKey = "background-operation.failed";

    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationPersistence _persistence;
    private readonly NhBackgroundOperationRegistry _registry;
    private readonly NhBackgroundOperationFanOutCoordinator _fanOutCoordinator;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly ILogger<NhBackgroundOperationRunner> _logger;
    private readonly TimeProvider _dueClock;

    internal NhBackgroundOperationRunner(
        IServiceProvider serviceProvider,
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        NhBackgroundOperationsOptions options,
        ILogger<NhBackgroundOperationRunner> logger,
        TimeProvider? timeProvider = null)
    {
        _serviceProvider = serviceProvider;
        _scopeFactory = scopeFactory;
        _persistence = persistence;
        _registry = registry;
        _fanOutCoordinator = fanOutCoordinator;
        _options = options;
        _logger = logger;
        _dueClock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Hangfire entry point of one dispatch generation. Hangfire retries are disabled:
    /// the runner records handler failures itself, and reconciliation redispatches or
    /// escalates an operation whose job did not start it.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
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
                var globalLease = await AcquireGlobalSlotAsync(leaseManager, claim.Queue);
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
                    _fanOutCoordinator,
                    _logger);
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

                var failure = GetFailure(claim, result);
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
                    await CompleteUnrecordedCancellationAsync(claim, timeoutSource);
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
            catch (NhBackgroundOperationSignalPendingException)
            {
                // The suspension context atomically closed this attempt and
                // released the worker. A signal or durable expiry wakes a new
                // attempt without consuming retry budget.
            }
            catch (Exception exception)
            {
                if (!await CompleteStoppedHandlerErrorAsync(
                        claim,
                        descriptor,
                        stopState,
                        timeoutSource,
                        exception))
                {
                    await CompleteUnexpectedFailureAsync(
                        claim,
                        descriptor,
                        exception,
                        "handler-failed");
                }
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

    /// <summary>
    /// Refreshes the attempt's heartbeat and leases every <c>HeartbeatInterval</c>. The beat and the
    /// attempt's own writes take the same operation lock; through the claim's write gate the beat
    /// never waits for that lock behind one of those writes, so it stops the attempt as contention
    /// only for another holder. A beat that falls due while an own write is in progress is skipped:
    /// that write shows the attempt is alive. The next beat does not skip again but waits for the
    /// writes in progress to end, so a steady stream of writes cannot starve the heartbeat. A skipped
    /// or waiting beat refreshes nothing, so an own write that never ends lets the heartbeat go stale
    /// and reconciliation recovers the attempt after <c>StaleAttemptTimeout</c>.
    /// </summary>
    private async Task RunHeartbeatAsync(
        NhBackgroundOperationAttemptClaim claim,
        IReadOnlyList<INhBackgroundOperationLeaseHandle> leases,
        CancellationTokenSource executionSource,
        NhBackgroundOperationExecutionStopState stopState)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        var writeGate = claim.WriteGate;
        var skippedBeat = false;
        try
        {
            while (await timer.WaitForNextTickAsync(executionSource.Token))
            {
                if (!writeGate.TryEnterBeat())
                {
                    if (!skippedBeat)
                    {
                        skippedBeat = true;
                        _logger.LogDebug(
                            "Skipped a heartbeat of background operation {OperationId} attempt {AttemptId} while the attempt's own write is in progress.",
                            claim.OperationId,
                            claim.AttemptId);
                        continue;
                    }

                    await writeGate.EnterBeatAsync(executionSource.Token);
                }

                NhBackgroundOperationHeartbeatResult heartbeatResult;
                try
                {
                    heartbeatResult = await _persistence.HeartbeatAsync(claim, CancellationToken.None);
                }
                finally
                {
                    writeGate.ExitBeat();
                }

                skippedBeat = false;
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
                await CompleteCancelledAsync(claim);
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

    /// <summary>
    /// Classifies an execution that unwound with a cancellation the heartbeat did not record. It
    /// timed out only when the soft time-out fired. Otherwise a durable cancellation of this attempt
    /// stopped it, as a handler that checks <c>ThrowIfCancellationRequestedAsync</c> sees before the
    /// next heartbeat does, and the operation is cancelled. Any other cancellation is reported as a
    /// time-out, as before.
    /// </summary>
    private async Task CompleteUnrecordedCancellationAsync(
        NhBackgroundOperationAttemptClaim claim,
        CancellationTokenSource timeoutSource)
    {
        if (timeoutSource.IsCancellationRequested)
        {
            await CompleteTimedOutAsync(claim);
            return;
        }

        if (await _persistence.IsCancellationRequestedAsync(claim, CancellationToken.None))
        {
            await CompleteCancelledAsync(claim);
            return;
        }

        await CompleteTimedOutAsync(claim);
    }

    /// <summary>
    /// Lets the platform's own stop signals win over an error that the handler threw outside a step,
    /// as they win over a step's own error when the step's write finds them: a lost fence, contention
    /// for the operation's lock and a durable cancellation request of this attempt. The handler's
    /// error is then logged as a warning with the operation and attempt, and the attempt is recorded
    /// as that stop says, so nothing retries an error that its owner cancelled:
    /// <list type="bullet">
    /// <item>a stop the heartbeat recorded first (cancellation, contention or lost ownership) ends the
    /// attempt as it ends a cancelled execution;</item>
    /// <item>an error of the attempt's own write that found its fence lost writes nothing;</item>
    /// <item>otherwise the fenced durable cancellation of the attempt is checked, and when it is
    /// requested the operation ends <c>Cancelled</c>, or <c>TimedOut</c> when the soft time-out fired
    /// first, as a cancelled execution is classified.</item>
    /// </list>
    /// Returns <see langword="false"/> when no signal stopped the attempt: a heartbeat failure or none
    /// at all. The error then fails the attempt as <c>handler-failed</c> with the operation's retry
    /// rules, as before.
    /// </summary>
    private async Task<bool> CompleteStoppedHandlerErrorAsync(
        NhBackgroundOperationAttemptClaim claim,
        NhBackgroundOperationDescriptor descriptor,
        NhBackgroundOperationExecutionStopState stopState,
        CancellationTokenSource timeoutSource,
        Exception exception)
    {
        var stop = stopState.Get().Reason;
        if (stop is NhBackgroundOperationExecutionStopReason.DurableCancellation
            or NhBackgroundOperationExecutionStopReason.Contention
            or NhBackgroundOperationExecutionStopReason.OwnershipLost)
        {
            LogHandlerErrorAfterStop(claim, exception, stop);
            await HandleExecutionStopAsync(claim, descriptor, stopState);
            return true;
        }

        if (exception is NhBackgroundOperationOwnershipLostException)
        {
            _logger.LogWarning(
                "Background operation {OperationId} attempt {AttemptId} stopped after losing durable ownership.",
                claim.OperationId,
                claim.AttemptId);
            return true;
        }

        if (!await _persistence.IsCancellationRequestedAsync(claim, CancellationToken.None))
        {
            return false;
        }

        LogHandlerErrorAfterStop(
            claim,
            exception,
            NhBackgroundOperationExecutionStopReason.DurableCancellation);
        if (timeoutSource.IsCancellationRequested)
        {
            await CompleteTimedOutAsync(claim);
            return true;
        }

        await CompleteCancelledAsync(claim);
        return true;
    }

    private void LogHandlerErrorAfterStop(
        NhBackgroundOperationAttemptClaim claim,
        Exception exception,
        NhBackgroundOperationExecutionStopReason stop)
    {
        _logger.LogWarning(
            exception,
            "Background operation {OperationId} attempt {AttemptId} threw an error outside a step while it was stopped by {StopSignal}; the attempt ends as that stop says, not as a handler failure, and the error is not retried.",
            claim.OperationId,
            claim.AttemptId,
            stop switch
            {
                NhBackgroundOperationExecutionStopReason.DurableCancellation => "a cancellation request",
                NhBackgroundOperationExecutionStopReason.Contention => "contention for the operation lock",
                NhBackgroundOperationExecutionStopReason.OwnershipLost => "lost ownership",
                _ => stop.ToString()
            });
    }

    private Task CompleteCancelledAsync(NhBackgroundOperationAttemptClaim claim)
    {
        return _persistence.CompleteAsync(
            claim,
            NhBackgroundOperationStatus.Cancelled,
            "cancelled",
            "background-operation.cancelled",
            null,
            null,
            CancellationToken.None);
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
            ? _dueClock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, failedAttempts)))
            : (DateTimeOffset?)null;
        await _persistence.CompleteAsync(
            claim,
            NhBackgroundOperationStatus.Failed,
            failureCode,
            GenericFailureMessageKey,
            diagnosticCorrelationId,
            retryAt,
            CancellationToken.None);
    }

    /// <summary>
    /// Takes one slot of the global limit. Without reservations every operation shares the
    /// <c>processor:{key}</c> lease with all slots of the limit, as before. With reservations, an
    /// operation on a queue that reserves slots takes one of its own reserved slots first and a shared
    /// slot when they are in use; an operation on any other queue takes a shared slot only. The shared
    /// lease has the slots the reservations leave, so no queue takes another's reserved slots and the
    /// slots in use never exceed the global limit.
    /// </summary>
    private async Task<INhBackgroundOperationLeaseHandle?> AcquireGlobalSlotAsync(
        NhBackgroundOperationLeaseManager leaseManager,
        string queue)
    {
        var processorKey = $"processor:{_options.ProcessorKey}";
        if (_options.ReservedQueueConcurrency.TryGetValue(queue, out var reservedSlots))
        {
            var reservedLease = await leaseManager.AcquireAsync(
                $"{processorKey}:reserved:{queue.ToLowerInvariant()}",
                new NhBackgroundOperationLeaseOptions { Slots = reservedSlots },
                CancellationToken.None);
            if (reservedLease is not null)
            {
                return reservedLease;
            }
        }

        return await leaseManager.AcquireAsync(
            processorKey,
            new NhBackgroundOperationLeaseOptions { Slots = _options.SharedConcurrencySlots },
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
            _dueClock.GetUtcNow() + (retryAfter ?? TimeSpan.FromSeconds(2)),
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

    /// <summary>
    /// The safe failure code and message key of a failed result, checked against the columns that
    /// store them. A code that is missing or longer than its column becomes
    /// <c>handler-rejected</c>, and a message key that is missing or longer than its column the
    /// generic failure key; the oversized text goes to the operational log only. A key is not
    /// truncated, because a truncated key matches no translation. The result's status and retry
    /// request stay as the handler returned them.
    /// </summary>
    private NhBackgroundOperationFailure? GetFailure(
        NhBackgroundOperationAttemptClaim claim,
        TaskResult result)
    {
        if (result.Success)
        {
            return null;
        }

        var resultItem = result.GetResultItems()
            .FirstOrDefault(item => item.ErrorMessages.Count > 0);
        var failureCode = resultItem?.Name;
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            failureCode = HandlerRejectedFailureCode;
        }
        else if (failureCode.Length > NhBackgroundOperationColumnLimits.FailureCode)
        {
            _logger.LogWarning(
                "Background operation {OperationId} attempt {AttemptId} returned a failure code of {Length} characters, longer than the {MaximumLength} its column holds; it is stored as {StoredFailureCode}. Returned code: {ReturnedFailureCode}",
                claim.OperationId,
                claim.AttemptId,
                failureCode.Length,
                NhBackgroundOperationColumnLimits.FailureCode,
                HandlerRejectedFailureCode,
                failureCode);
            failureCode = HandlerRejectedFailureCode;
        }

        var messageKey = resultItem?.ErrorMessages.FirstOrDefault()?.Format;
        if (string.IsNullOrWhiteSpace(messageKey))
        {
            messageKey = GenericFailureMessageKey;
        }
        else if (messageKey.Length > NhBackgroundOperationColumnLimits.FailureMessageKey)
        {
            _logger.LogWarning(
                "Background operation {OperationId} attempt {AttemptId} returned a failure message key of {Length} characters, longer than the {MaximumLength} its column holds; it is stored as {StoredMessageKey}. Returned key: {ReturnedMessageKey}",
                claim.OperationId,
                claim.AttemptId,
                messageKey.Length,
                NhBackgroundOperationColumnLimits.FailureMessageKey,
                GenericFailureMessageKey,
                messageKey);
            messageKey = GenericFailureMessageKey;
        }

        return new NhBackgroundOperationFailure(failureCode, messageKey);
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
        return _dueClock.GetUtcNow() + retryDelay;
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
