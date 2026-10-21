namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// The application's clock of a test host: it stands still at <c>start</c> until the test moves it
/// forward. Registered as the host's <see cref="TimeProvider"/>, it moves what the assistant reads
/// from the application's clock, such as the minute of a schedule tick and the expiry of a
/// proposal, without waiting.
/// </summary>
public sealed class AssistantTestClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow()
    {
        return new DateTimeOffset(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }

    /// <summary>Moves the clock forward by <paramref name="duration"/>.</summary>
    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The clock only moves forward.");
        }

        Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
