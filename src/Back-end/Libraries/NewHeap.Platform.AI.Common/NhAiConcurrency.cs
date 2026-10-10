using System.Collections.Concurrent;

namespace NewHeap.Platform.AI;

public sealed record NhAiConcurrencyDecision(
    bool Acquired,
    string Code,
    IAsyncDisposable? Lease = null);

/// <summary>
/// Admits a tool invocation within the descriptor's
/// <see cref="NhAiToolDescriptor.MaxConcurrency"/>. A refusal is an expected outcome: the
/// invoker returns it as <see cref="NhAiToolFailureCodes.ConcurrencyLimited"/> without running
/// the tool.
/// </summary>
public interface INhAiToolConcurrencyLimiter
{
    ValueTask<NhAiConcurrencyDecision> TryAcquireAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Selects which invocations of one tool share a <see cref="NhAiToolDescriptor.MaxConcurrency"/>
/// bound in the default process-local limiter.
/// </summary>
public enum NhAiToolConcurrencyPartition
{
    /// <summary>
    /// Each <see cref="NhAiInvocationContext.TenantId"/> has its own bound per tool ID and
    /// version, so one tenant's load never refuses another tenant's call. Invocations without a
    /// tenant share one partition.
    /// </summary>
    ToolAndTenant = 0,

    /// <summary>
    /// One bound per tool ID and version is shared by every tenant, issuer and client in the
    /// process. Use it when the bound protects a resource that all tenants share.
    /// </summary>
    Tool = 1
}

/// <summary>
/// Options for the default process-local tool concurrency limiter. Its bounds apply per process;
/// register a distributed <see cref="INhAiToolConcurrencyLimiter"/> when several instances must
/// share them.
/// </summary>
public sealed class NhAiInProcessConcurrencyOptions
{
    public NhAiToolConcurrencyPartition Partition { get; set; } =
        NhAiToolConcurrencyPartition.ToolAndTenant;
}

internal sealed class NhAiInProcessToolConcurrencyLimiter : INhAiToolConcurrencyLimiter
{
    private const string TenantlessPartition = "";

    private readonly NhAiToolConcurrencyPartition _partition;
    private readonly ConcurrentDictionary<string, ToolLimit> _limits = new(StringComparer.Ordinal);

    public NhAiInProcessToolConcurrencyLimiter()
        : this(new NhAiInProcessConcurrencyOptions())
    {
    }

    public NhAiInProcessToolConcurrencyLimiter(NhAiInProcessConcurrencyOptions options)
    {
        Validate(options);
        _partition = options.Partition;
    }

    public ValueTask<NhAiConcurrencyDecision> TryAcquireAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.MaxConcurrency < 1)
        {
            throw new InvalidOperationException(
                $"AI tool '{descriptor.Id}' has an invalid concurrency limit.");
        }

        var key = $"{descriptor.Id}@{descriptor.Version}";
        var limit = _limits.GetOrAdd(
            key,
            _ => new ToolLimit(descriptor.MaxConcurrency));
        if (limit.Limit != descriptor.MaxConcurrency)
        {
            throw new InvalidOperationException(
                $"AI tool '{key}' was invoked with conflicting concurrency limits.");
        }

        var partition = GetPartition(context);
        if (!limit.TryEnter(partition))
        {
            return ValueTask.FromResult(
                new NhAiConcurrencyDecision(false, "concurrency-limit-reached"));
        }

        return ValueTask.FromResult(
            new NhAiConcurrencyDecision(
                true,
                "concurrency-acquired",
                new Lease(limit, partition)));
    }

    internal static void Validate(NhAiInProcessConcurrencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Partition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The AI tool concurrency partition is not supported.");
        }
    }

    private string GetPartition(NhAiInvocationContext context)
    {
        if (_partition == NhAiToolConcurrencyPartition.Tool
            || string.IsNullOrWhiteSpace(context.TenantId))
        {
            return TenantlessPartition;
        }

        return context.TenantId;
    }

    private sealed class ToolLimit(int limit)
    {
        // Only partitions with an active lease are kept, so idle tenants hold no state.
        private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);

        public int Limit { get; } = limit;

        public bool TryEnter(string partition)
        {
            lock (_active)
            {
                var active = _active.GetValueOrDefault(partition);
                if (active >= Limit)
                {
                    return false;
                }

                _active[partition] = active + 1;
                return true;
            }
        }

        public void Exit(string partition)
        {
            lock (_active)
            {
                var active = _active[partition] - 1;
                if (active == 0)
                {
                    _active.Remove(partition);
                }
                else
                {
                    _active[partition] = active;
                }
            }
        }
    }

    private sealed class Lease(ToolLimit limit, string partition) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                limit.Exit(partition);
            }
            return ValueTask.CompletedTask;
        }
    }
}
