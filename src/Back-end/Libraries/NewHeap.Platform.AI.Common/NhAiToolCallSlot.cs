namespace NewHeap.Platform.AI;

/// <summary>
/// The concurrency slot of one governed tool call (<see cref="NhAiToolDescriptor.MaxConcurrency"/>),
/// which the call lets go while it waits for a person and takes again before it goes on. The invoker
/// owns it for the whole call and releases the slot it holds when the call ends. Each lease is
/// released once.
/// </summary>
internal sealed class NhAiToolCallSlot : IAsyncDisposable
{
    private readonly Func<TimeSpan, CancellationToken, ValueTask<NhAiConcurrencyDecision>> _takeAgain;
    private IAsyncDisposable? _lease;

    /// <summary>
    /// The slot of a call that holds <paramref name="lease"/>; <paramref name="takeAgain"/> waits for a
    /// slot of the same tool and partition at most the given time, as the invoker's first wait does.
    /// </summary>
    public NhAiToolCallSlot(
        IAsyncDisposable lease,
        Func<TimeSpan, CancellationToken, ValueTask<NhAiConcurrencyDecision>> takeAgain)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(takeAgain);
        _lease = lease;
        _takeAgain = takeAgain;
    }

    /// <summary>
    /// Whether the call holds a slot now.
    /// </summary>
    public bool IsHeld => Volatile.Read(ref _lease) is not null;

    /// <summary>
    /// Lets the slot go, so another call of the tool can run meanwhile.
    /// </summary>
    public async ValueTask ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _lease, null) is { } lease)
        {
            await lease.DisposeAsync();
        }
    }

    /// <summary>
    /// Takes a slot again, waiting at most <paramref name="within"/> for one to become free. Returns
    /// <see langword="false"/> when none did in time; a slot the call still holds counts as taken.
    /// </summary>
    public async ValueTask<bool> TakeAgainAsync(TimeSpan within, CancellationToken cancellationToken)
    {
        if (IsHeld)
        {
            return true;
        }

        var decision = await _takeAgain(within, cancellationToken);
        if (!decision.Acquired || decision.Lease is null)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _lease, decision.Lease, null) is not null)
        {
            // Another wait of the same call took a slot first; the call needs only one.
            await decision.Lease.DisposeAsync();
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        return ReleaseAsync();
    }
}
