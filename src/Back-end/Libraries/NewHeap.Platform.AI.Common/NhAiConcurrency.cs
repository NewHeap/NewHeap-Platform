using System.Collections.Concurrent;

namespace NewHeap.Platform.AI;

public sealed record NhAiConcurrencyDecision(
    bool Acquired,
    string Code,
    IAsyncDisposable? Lease = null);

/// <summary>
/// Admits a tool invocation within the descriptor's
/// <see cref="NhAiToolDescriptor.MaxConcurrency"/>. The invoker waits for a free slot through
/// <see cref="WaitToAcquireAsync"/>; a refusal is an expected outcome that it returns as
/// <see cref="NhAiToolFailureCodes.ConcurrencyLimited"/> without running the tool.
/// </summary>
public interface INhAiToolConcurrencyLimiter
{
    /// <summary>
    /// Admits the invocation when a slot is free now, and refuses it at once otherwise.
    /// </summary>
    ValueTask<NhAiConcurrencyDecision> TryAcquireAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Admits the invocation, waiting for a free slot until <paramref name="cancellationToken"/> ends.
    /// The invoker passes a token that ends at the call's wait bound, the earlier of the tool's
    /// <see cref="NhAiToolDescriptor.Timeout"/> and the invocation context's
    /// <see cref="NhAiInvocationContext.Deadline"/> on the application's clock, or when its own caller
    /// cancels. A call that let its slot go while it waited for a person, such as the person who answers
    /// an MCP server's request for input, asks again before it goes on, bounded by the time it has left. Return a refusal, or throw <see cref="OperationCanceledException"/>, when the token ends
    /// before a slot is free: the invoker then refuses the call with
    /// <see cref="NhAiToolFailureCodes.ConcurrencyLimited"/>, or rethrows when its caller cancelled.
    /// The default implementation does not wait: it asks <see cref="TryAcquireAsync"/> once, so a
    /// limiter that implements only that, such as a distributed one, keeps refusing a full bound at
    /// once.
    /// </summary>
    ValueTask<NhAiConcurrencyDecision> WaitToAcquireAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        return TryAcquireAsync(descriptor, context, cancellationToken);
    }
}

/// <summary>
/// Selects which invocations of one tool share a <see cref="NhAiToolDescriptor.MaxConcurrency"/>
/// bound in the default process-local limiter.
/// </summary>
public enum NhAiToolConcurrencyPartition
{
    /// <summary>
    /// Each <see cref="NhAiInvocationContext.TenantId"/> has its own bound per tool ID and
    /// version, so one tenant's load never holds up another tenant's call. Invocations without a
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

/// <summary>
/// The default limiter: one bound per tool version and partition in this process. A call that finds
/// its bound full waits in line, first come first served, and a released slot passes straight to the
/// first waiting call of its partition.
/// </summary>
internal sealed class NhAiInProcessToolConcurrencyLimiter : INhAiToolConcurrencyLimiter
{
    private const string TenantlessPartition = "";
    private const string AcquiredCode = "concurrency-acquired";
    private const string LimitReachedCode = "concurrency-limit-reached";
    private const string WaitEndedCode = "concurrency-wait-ended";

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
        cancellationToken.ThrowIfCancellationRequested();
        var limit = GetLimit(descriptor, context);
        var partition = GetPartition(context);
        if (!limit.TryEnter(partition))
        {
            return ValueTask.FromResult(
                new NhAiConcurrencyDecision(false, LimitReachedCode));
        }

        return ValueTask.FromResult(Acquired(limit, partition));
    }

    public async ValueTask<NhAiConcurrencyDecision> WaitToAcquireAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = GetLimit(descriptor, context);
        var partition = GetPartition(context);
        if (!await limit.EnterAsync(partition, cancellationToken))
        {
            return new NhAiConcurrencyDecision(false, WaitEndedCode);
        }

        return Acquired(limit, partition);
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

    private static NhAiConcurrencyDecision Acquired(ToolLimit limit, string partition)
    {
        return new NhAiConcurrencyDecision(
            true,
            AcquiredCode,
            new Lease(limit, partition));
    }

    private ToolLimit GetLimit(NhAiToolDescriptor descriptor, NhAiInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
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

        return limit;
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
        // Only partitions with an active lease are kept, so idle tenants hold no state. A partition
        // has waiting calls only while all of its slots are taken: a released slot passes straight to
        // the first of them.
        private readonly Dictionary<string, PartitionState> _partitions = new(StringComparer.Ordinal);

        public int Limit { get; } = limit;

        public bool TryEnter(string partition)
        {
            lock (_partitions)
            {
                return TryEnterLocked(partition);
            }
        }

        /// <summary>
        /// Takes a slot of the partition, waiting in line until one is released or the token ends.
        /// Returns <see langword="false"/> when the token ended first.
        /// </summary>
        public async ValueTask<bool> EnterAsync(string partition, CancellationToken cancellationToken)
        {
            TaskCompletionSource waiter;
            LinkedListNode<TaskCompletionSource> place;
            lock (_partitions)
            {
                if (TryEnterLocked(partition))
                {
                    return true;
                }

                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                place = _partitions[partition].Waiting.AddLast(waiter);
            }

            try
            {
                await waiter.Task.WaitAsync(cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_partitions)
                {
                    if (place.List is { } line)
                    {
                        line.Remove(place);
                        return false;
                    }
                }

                // A released slot reached this call as its wait ended: pass it on.
                Exit(partition);
                return false;
            }
        }

        public void Exit(string partition)
        {
            lock (_partitions)
            {
                var state = _partitions[partition];
                if (state.Waiting.First is { } next)
                {
                    state.Waiting.RemoveFirst();
                    next.Value.SetResult();
                    return;
                }

                state.Active--;
                if (state.Active == 0)
                {
                    _partitions.Remove(partition);
                }
            }
        }

        private bool TryEnterLocked(string partition)
        {
            if (!_partitions.TryGetValue(partition, out var state))
            {
                state = new PartitionState();
                _partitions.Add(partition, state);
            }

            if (state.Active >= Limit)
            {
                return false;
            }

            state.Active++;
            return true;
        }
    }

    private sealed class PartitionState
    {
        public int Active { get; set; }

        public LinkedList<TaskCompletionSource> Waiting { get; } = new();
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
