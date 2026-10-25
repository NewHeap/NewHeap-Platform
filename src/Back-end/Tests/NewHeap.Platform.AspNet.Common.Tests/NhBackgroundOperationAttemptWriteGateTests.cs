using AwesomeAssertions;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The write gate between an attempt's own writes and its heartbeat. A heartbeat that takes longer
/// than its interval has its next beat due at once, so the heartbeat loop asks for the next beat
/// right after the previous one ended, before a write that waited can continue. The end of a beat
/// admits the writes that waited for it, so such beats never hold a write back.
/// </summary>
public sealed class NhBackgroundOperationAttemptWriteGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_write_that_waited_for_a_beat_starts_before_a_beat_that_is_due_at_once()
    {
        var gate = new NhBackgroundOperationAttemptWriteGate();
        gate.TryEnterBeat().Should().BeTrue();
        var write = gate.EnterWriteAsync(CancellationToken.None).AsTask();
        write.IsCompleted.Should().BeFalse("a write that starts while a beat runs waits for that beat");

        gate.ExitBeat();

        gate.TryEnterBeat().Should().BeFalse("the end of the beat admitted the write that waited for it");
        using (await write.WaitAsync(Patience))
        {
            gate.TryEnterBeat().Should().BeFalse();
        }

        gate.TryEnterBeat().Should().BeTrue();
        gate.ExitBeat();
    }

    [Fact]
    public async Task A_beat_that_does_not_skip_waits_for_the_writes_the_previous_beat_admitted()
    {
        var gate = new NhBackgroundOperationAttemptWriteGate();
        gate.TryEnterBeat().Should().BeTrue();
        var first = gate.EnterWriteAsync(CancellationToken.None).AsTask();
        var second = gate.EnterWriteAsync(CancellationToken.None).AsTask();
        gate.ExitBeat();

        var beat = gate.EnterBeatAsync(CancellationToken.None);

        beat.IsCompleted.Should().BeFalse();
        ((IDisposable)await first.WaitAsync(Patience)).Dispose();
        beat.IsCompleted.Should().BeFalse("the second admitted write is still in progress");
        ((IDisposable)await second.WaitAsync(Patience)).Dispose();
        await beat.WaitAsync(Patience);
        var waitsForTheBeat = gate.EnterWriteAsync(CancellationToken.None).AsTask();
        waitsForTheBeat.IsCompleted.Should().BeFalse();
        gate.ExitBeat();
        ((IDisposable)await waitsForTheBeat.WaitAsync(Patience)).Dispose();
    }

    [Fact]
    public async Task A_write_that_stops_waiting_for_a_beat_holds_no_later_beat_back()
    {
        var gate = new NhBackgroundOperationAttemptWriteGate();
        gate.TryEnterBeat().Should().BeTrue();
        using var cancellation = new CancellationTokenSource();
        var write = gate.EnterWriteAsync(cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        await write.Invoking(task => task.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        gate.ExitBeat();
        gate.TryEnterBeat().Should().BeTrue();
        gate.ExitBeat();
    }
}
