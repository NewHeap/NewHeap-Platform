namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

/// <summary>
/// Coordinates the writes of one attempt with that attempt's heartbeat, inside the process. Both
/// take the operation's transaction lock, and a heartbeat that cannot get that lock in time stops
/// the attempt as contention. The gate keeps the heartbeat from meeting the attempt's own writes at
/// that lock, so a lock timeout of the heartbeat always means another holder.
/// <list type="bullet">
/// <item>An own write (plan, step, progress, batch, message, checkpoint, result, signal suspension,
/// fan-out or completion) holds the gate for its whole call, including the notification projection
/// that takes the lock again after the commit. Own writes do not exclude each other here; they meet
/// at the database lock as before.</item>
/// <item>A beat enters only when no own write is in progress (<see cref="TryEnterBeat"/>), or waits
/// for the writes in progress to end (<see cref="EnterBeatAsync"/>). An own write that starts while
/// a beat waits or runs waits for that beat.</item>
/// </list>
/// The gate holds no database resource and never refreshes liveness: only a completed beat does. An
/// attempt whose own write never ends sends no further heartbeat, so reconciliation recovers it once
/// its heartbeat is older than <c>StaleAttemptTimeout</c> on the system clock. Own writes never nest
/// inside each other, so a waiting beat cannot hold up the write it waits for.
/// </summary>
internal sealed class NhBackgroundOperationAttemptWriteGate
{
    private readonly object _sync = new();
    private int _writesInProgress;
    private TaskCompletionSource? _beat;
    private TaskCompletionSource? _writesEnded;

    /// <summary>
    /// Enters an own write of the attempt. It waits only while a beat waits or runs, which takes no
    /// longer than one heartbeat transaction. Dispose the result when the write has ended.
    /// </summary>
    internal async ValueTask<OwnWrite> EnterWriteAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task beat;
            lock (_sync)
            {
                if (_beat is null)
                {
                    _writesInProgress++;
                    return new OwnWrite(this);
                }

                beat = _beat.Task;
            }

            await beat.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Enters a beat when no own write is in progress. Returns <see langword="false"/> without
    /// waiting when one is. After <see langword="true"/>, call <see cref="ExitBeat"/>.
    /// </summary>
    internal bool TryEnterBeat()
    {
        lock (_sync)
        {
            if (_writesInProgress > 0)
            {
                return false;
            }

            _beat = CreateSignal();
            return true;
        }
    }

    /// <summary>
    /// Enters a beat once the own writes in progress have ended; own writes that start meanwhile
    /// wait for the beat. After it returns, call <see cref="ExitBeat"/>.
    /// </summary>
    internal async Task EnterBeatAsync(CancellationToken cancellationToken)
    {
        Task writesEnded;
        lock (_sync)
        {
            _beat = CreateSignal();
            if (_writesInProgress == 0)
            {
                return;
            }

            _writesEnded = CreateSignal();
            writesEnded = _writesEnded.Task;
        }

        try
        {
            await writesEnded.WaitAsync(cancellationToken);
        }
        catch
        {
            ExitBeat();
            throw;
        }
    }

    /// <summary>
    /// Ends a beat and lets the own writes that waited for it start.
    /// </summary>
    internal void ExitBeat()
    {
        TaskCompletionSource? beat;
        lock (_sync)
        {
            beat = _beat;
            _beat = null;
            _writesEnded = null;
        }

        beat?.TrySetResult();
    }

    private void ExitWrite()
    {
        TaskCompletionSource? writesEnded = null;
        lock (_sync)
        {
            _writesInProgress--;
            if (_writesInProgress == 0)
            {
                writesEnded = _writesEnded;
                _writesEnded = null;
            }
        }

        writesEnded?.TrySetResult();
    }

    private static TaskCompletionSource CreateSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// An own write in progress; disposing it ends the write.
    /// </summary>
    internal readonly struct OwnWrite : IDisposable
    {
        private readonly NhBackgroundOperationAttemptWriteGate _gate;

        internal OwnWrite(NhBackgroundOperationAttemptWriteGate gate)
        {
            _gate = gate;
        }

        void IDisposable.Dispose()
        {
            _gate.ExitWrite();
        }
    }
}
