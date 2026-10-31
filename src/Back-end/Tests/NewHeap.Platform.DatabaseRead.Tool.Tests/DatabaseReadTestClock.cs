namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// A clock that tests move forward, so a test of the session's bounds never waits for them. It
/// fires the timers that <see cref="Task.WaitAsync(TimeSpan, TimeProvider, CancellationToken)"/> and
/// a timed <see cref="CancellationTokenSource"/> create.
/// </summary>
internal sealed class DatabaseReadTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);

        return timer;
    }

    public void Advance(TimeSpan time)
    {
        List<ClockTimer> due;
        lock (_gate)
        {
            _now += time;
            due = _timers.Where(timer => timer.DueAt <= _now).ToList();
            _timers.RemoveAll(due.Contains);
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    /// <summary>
    /// Whether anything waits on the clock, such as a session waiting for its next line. A test
    /// polls this before it moves the clock, so the move cannot come before the wait.
    /// </summary>
    public bool HasTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count > 0;
            }
        }
    }

    private sealed class ClockTimer(DatabaseReadTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; }

        public void Fire()
        {
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = clock._now + dueTime;
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
