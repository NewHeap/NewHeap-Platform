using System.Collections.Concurrent;
using System.Text.Json;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// One background operation as its handler sees it across attempts, without the operation store:
/// checkpoints survive from one attempt to the next, and a signal wait ends when the application's
/// clock reaches its expiry, as the runner's suspension does. A wait that has not expired suspends
/// the attempt with <see cref="Suspended"/>, which stands for the runner releasing the worker.
/// </summary>
internal sealed class AssistantTestOperation(TimeProvider clock) : INhBackgroundOperationContext
{
    private readonly TestCheckpoints _checkpoints = new();
    private readonly TestSuspension _suspension = new(clock);

    public Guid OperationId { get; } = Guid.NewGuid();

    public Guid AttemptId { get; private set; } = Guid.NewGuid();

    public int AttemptNumber { get; private set; } = 1;

    public long FencingToken => AttemptNumber;

    public string IdempotencyKey => "operation-" + OperationId.ToString("N");

    public INhBackgroundOperationProgressContext Progress { get; } = Substitute.For<INhBackgroundOperationProgressContext>();

    public INhBackgroundOperationMessageSink Messages { get; } = Substitute.For<INhBackgroundOperationMessageSink>();

    public INhBackgroundOperationCheckpointStore Checkpoints => _checkpoints;

    public INhBackgroundOperationLeaseManager Leases { get; } = Substitute.For<INhBackgroundOperationLeaseManager>();

    public INhBackgroundOperationIdempotencyManager Idempotency { get; } = Substitute.For<INhBackgroundOperationIdempotencyManager>();

    public INhBackgroundOperationFanOutContext FanOut { get; } = Substitute.For<INhBackgroundOperationFanOutContext>();

    public INhBackgroundOperationSuspensionContext Suspension => _suspension;

    /// <summary>
    /// Starts the next attempt, as the runner does when the operation wakes.
    /// </summary>
    public void NextAttempt()
    {
        AttemptId = Guid.NewGuid();
        AttemptNumber++;
    }

    public Task ThrowIfCancellationRequestedAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SetResultAsync(NhBackgroundOperationResultReference result, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The attempt suspended on a signal wait that has not expired yet.
    /// </summary>
    public sealed class Suspended(string waitKey) : Exception("The operation waits for signal '" + waitKey + "'.");

    /// <summary>
    /// Keeps checkpoints as the operation store does: serialized, so a value comes back as a new
    /// instance that went through JSON.
    /// </summary>
    private sealed class TestCheckpoints : INhBackgroundOperationCheckpointStore
    {
        private readonly ConcurrentDictionary<string, (string Json, int SchemaVersion, long Version)> _values = new(StringComparer.Ordinal);

        public Task<NhBackgroundOperationCheckpointValue<T>?> GetAsync<T>(string checkpointKey, CancellationToken cancellationToken = default)
        {
            if (!_values.TryGetValue(checkpointKey, out var stored))
            {
                return Task.FromResult<NhBackgroundOperationCheckpointValue<T>?>(null);
            }

            var value = JsonSerializer.Deserialize<T>(stored.Json)!;
            return Task.FromResult<NhBackgroundOperationCheckpointValue<T>?>(
                new NhBackgroundOperationCheckpointValue<T>(value, stored.SchemaVersion, stored.Version));
        }

        public Task<TaskResult> SetAsync<T>(
            string checkpointKey,
            T value,
            int schemaVersion = 1,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            var version = _values.TryGetValue(checkpointKey, out var stored) ? stored.Version : 0;
            if (expectedVersion is { } expected && expected != version)
            {
                return Task.FromResult(TaskResult.Failed("checkpoint-conflict", "The checkpoint changed."));
            }

            _values[checkpointKey] = (JsonSerializer.Serialize(value), schemaVersion, version + 1);
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class TestSuspension(TimeProvider clock) : INhBackgroundOperationSuspensionContext
    {
        public Task<NhBackgroundOperationSignalWaitResult<TSignal>> WaitForSignalAsync<TSignal>(
            string waitKey,
            DateTimeOffset expiresAt,
            int signalSchemaVersion = 1,
            CancellationToken cancellationToken = default)
        {
            if (clock.GetUtcNow() < expiresAt)
            {
                throw new Suspended(waitKey);
            }

            return Task.FromResult(new NhBackgroundOperationSignalWaitResult<TSignal>(
                NhBackgroundOperationSignalWaitStatus.Expired,
                default,
                null,
                null,
                expiresAt));
        }
    }
}
