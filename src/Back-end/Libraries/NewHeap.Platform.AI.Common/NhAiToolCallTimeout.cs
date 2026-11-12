namespace NewHeap.Platform.AI;

/// <summary>
/// The timeout of one governed tool call while it runs: <see cref="Token"/> ends when the call's own
/// time is up or its caller cancels. While the call waits for a person, such as an MCP server's request
/// for input that the person of the call answers, its time stands still: <see cref="Pause"/> stops the
/// clock, and disposing the returned scope lets it run on with the time that was left. Pauses may
/// overlap; the clock runs again when the last one ends. A pause also stops the timeout of the governed
/// call this one runs within, such as a composition's step, so a person's answer counts against neither.
/// The caller's own cancellation, such as a person's stop, still ends the call during a pause.
/// <para>
/// <see cref="WaitForPersonAsync"/> also lets the call's concurrency <see cref="Slot"/> go while the
/// person is asked, so another call of the same tool can run meanwhile, and takes one again before the
/// call goes on (<see cref="NhAiPersonWait.ResumeAsync"/>). A call that finds no free slot within the
/// time it has left loses it (<see cref="SlotLost"/>): it ends at once, and the invoker refuses it with
/// <see cref="NhAiToolFailureCodes.ConcurrencyLimited"/>.
/// </para>
/// <para>
/// The invoker makes it <see cref="Current"/> around the tool's own work, so the protocol layer that
/// waits for the person finds it in the call's flow. It holds no argument or result content.
/// </para>
/// </summary>
internal sealed class NhAiToolCallTimeout : IDisposable
{
    private static readonly AsyncLocal<NhAiToolCallTimeout?> CurrentTimeout = new();

    private readonly object _gate = new();
    private readonly CancellationTokenSource _source;
    private readonly ITimer _timer;
    private readonly TimeProvider _clock;
    private readonly NhAiToolCallTimeout? _outer;
    private readonly CancellationToken _callerToken;
    private TimeSpan _remaining;
    private long _runningSince;
    private int _pauses;
    private bool _disposed;
    private volatile bool _slotLost;

    /// <summary>
    /// Starts the timeout of a call that may run for <paramref name="timeout"/>, measured on
    /// <paramref name="clock"/>, within <paramref name="cancellationToken"/>.
    /// </summary>
    public NhAiToolCallTimeout(TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A tool call's timeout is greater than zero.");
        }

        _clock = clock;
        _remaining = timeout;
        _outer = CurrentTimeout.Value;
        _callerToken = cancellationToken;
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runningSince = clock.GetTimestamp();
        _timer = clock.CreateTimer(static state => ((NhAiToolCallTimeout)state!).Expire(), this, timeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The timeout of the governed call in progress in this asynchronous flow; <see langword="null"/>
    /// outside a governed call.
    /// </summary>
    public static NhAiToolCallTimeout? Current => CurrentTimeout.Value;

    /// <summary>
    /// Ends when the call's own time is up or its caller cancels.
    /// </summary>
    public CancellationToken Token => _source.Token;

    /// <summary>
    /// The concurrency slot the call holds, which it lets go while it waits for a person;
    /// <see langword="null"/> for a call that runs outside the invoker, such as one a test makes by hand.
    /// </summary>
    public NhAiToolCallSlot? Slot { get; init; }

    /// <summary>
    /// Whether the call found no free slot again after it waited for a person: it ended then, and the
    /// invoker refuses it with <see cref="NhAiToolFailureCodes.ConcurrencyLimited"/>.
    /// </summary>
    public bool SlotLost => _slotLost;

    /// <summary>
    /// Whether a person is being waited for, so the call's time stands still.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _pauses > 0;
            }
        }
    }

    /// <summary>
    /// The time the call has left, as of now.
    /// </summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                return _pauses > 0 ? _remaining : _remaining - _clock.GetElapsedTime(_runningSince);
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="timeout"/> the current timeout of this asynchronous flow until the returned
    /// scope is disposed.
    /// </summary>
    public static IDisposable Enter(NhAiToolCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        var previous = CurrentTimeout.Value;
        CurrentTimeout.Value = timeout;
        return new Scope(previous);
    }

    /// <summary>
    /// Stops the call's clock, and the clock of the call it runs within, until the returned scope is
    /// disposed; the call then has the time that was left when it stopped.
    /// </summary>
    public IDisposable Pause()
    {
        lock (_gate)
        {
            if (_pauses == 0 && !_disposed)
            {
                _remaining -= _clock.GetElapsedTime(_runningSince);
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            _pauses++;
        }

        return new Pausing(this, _outer?.Pause());
    }

    /// <summary>
    /// Begins a wait for a person: stops the call's clock (<see cref="Pause"/>) and lets its concurrency
    /// slot go until <see cref="NhAiPersonWait.ResumeAsync"/> takes one again.
    /// </summary>
    public async ValueTask<NhAiPersonWait> WaitForPersonAsync()
    {
        var pausing = Pause();
        try
        {
            if (Slot is { } slot)
            {
                await slot.ReleaseAsync();
            }
        }
        catch
        {
            pausing.Dispose();
            throw;
        }

        return new NhAiPersonWait(this, pausing);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
        _source.Dispose();
    }

    /// <summary>
    /// Takes a concurrency slot again for a call that waited for a person, within the time it has left
    /// and its caller's cancellation, while its clock still stands still: the wait for a slot is the
    /// call's own, as its first wait before it ran. A call whose caller cancelled takes none, since it
    /// ends anyway. Returns <see langword="false"/> when no slot became free in time: the call then
    /// ends at once and <see cref="SlotLost"/> says why.
    /// </summary>
    internal async ValueTask<bool> TakeSlotAgainAsync()
    {
        if (Slot is not { } slot || _callerToken.IsCancellationRequested)
        {
            return true;
        }

        bool taken;
        try
        {
            taken = await slot.TakeAgainAsync(Remaining, _callerToken);
        }
        catch (OperationCanceledException) when (_callerToken.IsCancellationRequested)
        {
            return true;
        }

        if (taken)
        {
            return true;
        }

        // Another call holds the tool's slot: the call ends now rather than go on without one, and what
        // the person answered never reaches the tool.
        _slotLost = true;
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call ended meanwhile.
        }

        return false;
    }

    private void Resume()
    {
        lock (_gate)
        {
            _pauses--;
            if (_pauses > 0 || _disposed)
            {
                return;
            }

            _runningSince = _clock.GetTimestamp();
            _timer.Change(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private void Expire()
    {
        lock (_gate)
        {
            if (_disposed || _pauses > 0)
            {
                // A pause that began while the timer fired keeps the call's time.
                return;
            }
        }

        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call ended while its time ran out.
        }
    }

    private sealed class Scope(NhAiToolCallTimeout? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentTimeout.Value = previous;
        }
    }

    private sealed class Pausing(NhAiToolCallTimeout timeout, IDisposable? outer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            timeout.Resume();
            outer?.Dispose();
        }
    }
}

/// <summary>
/// A governed tool call's wait for a person (<see cref="NhAiToolCallTimeout.WaitForPersonAsync"/>): the
/// call's clock stands still and its concurrency slot is free until <see cref="ResumeAsync"/>. Disposing
/// it without resuming lets the clock run on and takes a slot again as well.
/// </summary>
internal sealed class NhAiPersonWait : IAsyncDisposable
{
    private readonly NhAiToolCallTimeout _timeout;
    private readonly IDisposable _pausing;
    private int _resumed;

    internal NhAiPersonWait(NhAiToolCallTimeout timeout, IDisposable pausing)
    {
        _timeout = timeout;
        _pausing = pausing;
    }

    /// <summary>
    /// Ends the wait: takes a concurrency slot again within the time the call has left and lets its clock
    /// run on. Returns <see langword="false"/> when no slot became free in time; the call has then ended,
    /// and what the person answered must not reach the tool.
    /// </summary>
    public async ValueTask<bool> ResumeAsync()
    {
        if (Interlocked.Exchange(ref _resumed, 1) == 1)
        {
            return !_timeout.SlotLost;
        }

        try
        {
            return await _timeout.TakeSlotAgainAsync();
        }
        finally
        {
            _pausing.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResumeAsync();
    }
}
