namespace NewHeap.Platform.AI;

/// <summary>
/// The timeout of one governed tool call while it runs: <see cref="Token"/> ends when the call's own
/// time is up or its caller cancels. While the call waits for a person, such as an MCP server's request
/// for input that the person of the call answers, its time stands still: <see cref="Pause"/> stops the
/// clock, and disposing the returned scope lets it run on with the time that was left. Pauses may
/// overlap; the clock runs again when the last one ends. A pause also stops the timeout of the governed
/// call this one runs within, such as a composition's step, so a person's answer counts against neither.
/// The caller's own cancellation, such as the turn's timeout, still ends the call during a pause.
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
    private TimeSpan _remaining;
    private long _runningSince;
    private int _pauses;
    private bool _disposed;

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
