using System.Collections.Concurrent;

namespace NewHeap.Platform.AI;

public sealed record NhAiConcurrencyDecision(
    bool Acquired,
    string Code,
    IAsyncDisposable? Lease = null);

public interface INhAiToolConcurrencyLimiter
{
    ValueTask<NhAiConcurrencyDecision> TryAcquireAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiInProcessToolConcurrencyLimiter : INhAiToolConcurrencyLimiter
{
    private readonly ConcurrentDictionary<string, LimitState> _limits = new(StringComparer.Ordinal);

    public async ValueTask<NhAiConcurrencyDecision> TryAcquireAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (descriptor.MaxConcurrency < 1)
        {
            throw new InvalidOperationException(
                $"AI tool '{descriptor.Id}' has an invalid concurrency limit.");
        }

        var key = $"{descriptor.Id}@{descriptor.Version}";
        var state = _limits.GetOrAdd(
            key,
            _ => new LimitState(descriptor.MaxConcurrency));
        if (state.Limit != descriptor.MaxConcurrency)
        {
            throw new InvalidOperationException(
                $"AI tool '{key}' was invoked with conflicting concurrency limits.");
        }

        if (!await state.Semaphore.WaitAsync(0, cancellationToken))
        {
            return new NhAiConcurrencyDecision(false, "concurrency-limit-reached");
        }
        return new NhAiConcurrencyDecision(
            true,
            "concurrency-acquired",
            new Lease(state.Semaphore));
    }

    private sealed class LimitState(int limit)
    {
        public int Limit { get; } = limit;
        public SemaphoreSlim Semaphore { get; } = new(limit, limit);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
