namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// The clock of one sample host: real time plus an offset that a test moves forward instead of
/// waiting, for example past the debounce of an agent's follow-up run. Registered as the host's
/// <see cref="TimeProvider"/>, it moves what the NewHeap libraries read from the application's
/// clock, such as when a background operation becomes due, how long a trigger debounces, the
/// minute a schedule tick belongs to and how long a proposal lives. Leases, heartbeats, tokens and
/// audit timestamps keep the system clock, so moving it forward never takes work away from a
/// running attempt. It never moves back. SPM-309 shows a deadline that a test releases by moving it.
/// </summary>
public sealed class SampleTestClock : TimeProvider
{
    private long _offsetTicks;

    /// <summary>How far the clock runs ahead of real time.</summary>
    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public override DateTimeOffset GetUtcNow()
    {
        return base.GetUtcNow() + Offset;
    }

    /// <summary>Moves the clock forward by <paramref name="duration"/>.</summary>
    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The clock only moves forward.");
        }

        Interlocked.Add(ref _offsetTicks, duration.Ticks);
    }
}
