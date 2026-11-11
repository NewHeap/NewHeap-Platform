namespace NewHeap.Platform.AI;

/// <summary>
/// The time a run, such as an assistant turn, stands still while it waits for a person, such as the
/// person who answers an MCP server's request for input. While the run is paused, the timeouts it
/// started with <see cref="StartTimeout"/> stand still, and its own <see cref="Deadline"/> moves later
/// by the time it stood still: <see cref="DeadlineOf"/> gives the model and tool calls of the run the
/// deadline as it stands now, while their invocation context keeps the deadline the run started with.
/// Only the run's own deadline moves; a deadline someone else set, such as an earlier one of the
/// application's invocation gate or the deadline of a consultation the run started, stays where it was.
/// Pauses may overlap; the run's clock runs again when the last one ends. The caller's own
/// cancellation still ends the run during a pause.
/// <para>
/// The run makes it <see cref="Current"/> around its model loop, so the chat executor and the tool
/// invoker find it in the run's flow. It holds no prompt, argument or result content.
/// </para>
/// </summary>
internal sealed class NhAiRunPause
{
    private static readonly AsyncLocal<NhAiRunPause?> CurrentPause = new();

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly List<NhAiRunTimeout> _timeouts = [];
    private TimeSpan _pausedBefore;
    private long _pausedSince;
    private int _pauses;

    /// <summary>
    /// The pause of a run whose own deadline is <paramref name="deadline"/>; its pauses and timeouts are
    /// measured on <paramref name="clock"/>.
    /// </summary>
    public NhAiRunPause(DateTimeOffset deadline, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Deadline = deadline;
        _clock = clock;
    }

    /// <summary>
    /// The pause of the run in progress in this asynchronous flow; <see langword="null"/> outside one.
    /// </summary>
    public static NhAiRunPause? Current => CurrentPause.Value;

    /// <summary>
    /// The run's own deadline, as it started.
    /// </summary>
    public DateTimeOffset Deadline { get; }

    /// <summary>
    /// Whether the run waits for a person now.
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
    /// How long the run stood still so far, the pause in progress included.
    /// </summary>
    public TimeSpan Paused
    {
        get
        {
            lock (_gate)
            {
                return PausedLocked();
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="pause"/> the pause of this asynchronous flow until the returned scope is
    /// disposed.
    /// </summary>
    public static IDisposable Enter(NhAiRunPause pause)
    {
        ArgumentNullException.ThrowIfNull(pause);
        var previous = CurrentPause.Value;
        CurrentPause.Value = pause;
        return new Scope(previous);
    }

    /// <summary>
    /// The deadline of <paramref name="context"/> as it stands now: its own
    /// <see cref="NhAiInvocationContext.Deadline"/>, moved later by the time the current run stood still
    /// when it is that run's own deadline.
    /// </summary>
    public static DateTimeOffset? DeadlineOf(NhAiInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Current is { } pause ? pause.Extend(context.Deadline) : context.Deadline;
    }

    /// <summary>
    /// <paramref name="deadline"/> moved later by the time the run stood still when it is the run's own
    /// <see cref="Deadline"/>; any other deadline as it is.
    /// </summary>
    public DateTimeOffset? Extend(DateTimeOffset? deadline)
    {
        if (deadline != Deadline)
        {
            return deadline;
        }

        return Deadline + Paused;
    }

    /// <summary>
    /// Starts a timeout of <paramref name="timeout"/> within <paramref name="cancellationToken"/> that
    /// stands still while the run is paused.
    /// </summary>
    public NhAiRunTimeout StartTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A run's timeout is greater than zero.");
        }

        lock (_gate)
        {
            var started = new NhAiRunTimeout(this, _clock, timeout, cancellationToken, running: _pauses == 0);
            _timeouts.Add(started);
            return started;
        }
    }

    /// <summary>
    /// Stops the run's clock until the returned scope is disposed: its timeouts stand still and its
    /// deadline moves later by the time it stood still.
    /// </summary>
    public IDisposable Pause()
    {
        lock (_gate)
        {
            _pauses++;
            if (_pauses == 1)
            {
                _pausedSince = _clock.GetTimestamp();
                foreach (var timeout in _timeouts)
                {
                    timeout.Stop();
                }
            }
        }

        return new Pausing(this);
    }

    internal void Remove(NhAiRunTimeout timeout)
    {
        lock (_gate)
        {
            _timeouts.Remove(timeout);
        }
    }

    private void Resume()
    {
        lock (_gate)
        {
            _pauses--;
            if (_pauses > 0)
            {
                return;
            }

            _pausedBefore += _clock.GetElapsedTime(_pausedSince);
            foreach (var timeout in _timeouts)
            {
                timeout.Run();
            }
        }
    }

    private TimeSpan PausedLocked()
    {
        return _pauses > 0 ? _pausedBefore + _clock.GetElapsedTime(_pausedSince) : _pausedBefore;
    }

    private sealed class Scope(NhAiRunPause? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentPause.Value = previous;
        }
    }

    private sealed class Pausing(NhAiRunPause pause) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            pause.Resume();
        }
    }
}

/// <summary>
/// One timeout of a run (<see cref="NhAiRunPause.StartTimeout"/>): <see cref="Token"/> ends when its time
/// is up or its caller cancels, and its time stands still while the run is paused.
/// </summary>
internal sealed class NhAiRunTimeout : IDisposable
{
    private readonly object _gate = new();
    private readonly NhAiRunPause _pause;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _source;
    private readonly ITimer _timer;
    private TimeSpan _remaining;
    private long _runningSince;
    private bool _running;
    private bool _disposed;

    internal NhAiRunTimeout(
        NhAiRunPause pause,
        TimeProvider clock,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool running)
    {
        _pause = pause;
        _clock = clock;
        _remaining = timeout;
        _running = running;
        _runningSince = clock.GetTimestamp();
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = clock.CreateTimer(
            static state => ((NhAiRunTimeout)state!).Expire(),
            this,
            running ? timeout : Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Ends when the run's time is up or its caller cancels.
    /// </summary>
    public CancellationToken Token => _source.Token;

    /// <summary>
    /// The time the run has left, as of now.
    /// </summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                return _running ? _remaining - _clock.GetElapsedTime(_runningSince) : _remaining;
            }
        }
    }

    public void Dispose()
    {
        _pause.Remove(this);
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

    internal void Stop()
    {
        lock (_gate)
        {
            if (!_running || _disposed)
            {
                return;
            }

            _running = false;
            _remaining -= _clock.GetElapsedTime(_runningSince);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    internal void Run()
    {
        lock (_gate)
        {
            if (_running || _disposed)
            {
                return;
            }

            _running = true;
            _runningSince = _clock.GetTimestamp();
            _timer.Change(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private void Expire()
    {
        lock (_gate)
        {
            if (_disposed || !_running)
            {
                // A pause that began while the timer fired keeps the run's time.
                return;
            }
        }

        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended while its time ran out.
        }
    }
}
