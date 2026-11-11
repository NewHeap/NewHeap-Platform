namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// The application's clock of a test that also runs the timers created on it: it stands still at
/// <c>start</c> until the test moves it forward, and a timer fires when the clock passes its due time,
/// never in real time. Use it where a deadline on the application's clock must end without waiting,
/// such as the wait time of an MCP server's request for input; <see cref="AssistantTestClock"/> leaves
/// timers on the system clock.
/// </summary>
public sealed class AssistantManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward by <paramref name="duration"/> and fires the timers it passes on the
    /// calling thread. Call it on a pool thread (<c>Task.Run</c>) when a timer's callback continues work
    /// the test waits for, as a system timer would run it.
    /// </summary>
    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The clock only moves forward.");
        }

        ManualTimer[] due;
        lock (_gate)
        {
            _now += duration;
            due = _timers.Where(timer => timer.DueAt <= _now).ToArray();
            foreach (var timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(AssistantManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                // Only one-shot timers are needed; a period is not repeated.
                DueAt = clock._now + dueTime;
                clock._timers.Add(this);
                return true;
            }
        }

        public void Fire()
        {
            callback(state);
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
