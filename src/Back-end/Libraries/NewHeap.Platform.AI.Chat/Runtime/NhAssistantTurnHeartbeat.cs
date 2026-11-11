using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Keeps a turn's claim on its conversation while the turn stands still for its person. Another request
/// takes a running conversation over as abandoned once its last update is older than the turn timeout
/// and a minute (<see cref="INhAssistantStore.TryBeginTurnAsync"/>); a turn that waits for its person
/// may stand still longer than that. The heartbeat renews the claim when the wait begins, every
/// <see cref="NhAssistantTiming.TurnHeartbeatInterval"/> on the turn's clock while it lasts, and once more
/// when it ends, after which the turn has at most its own timeout left. It writes nothing but the
/// conversation's update time.
/// </summary>
internal sealed class NhAssistantTurnHeartbeat : IAsyncDisposable
{
    private readonly INhAssistantStore _store;
    private readonly Guid _conversationId;
    private readonly Guid _turnId;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task _beating = Task.CompletedTask;
    private bool _owned = true;

    private NhAssistantTurnHeartbeat(INhAssistantStore store, Guid conversationId, Guid turnId, ILogger? logger)
    {
        _store = store;
        _conversationId = conversationId;
        _turnId = turnId;
        _logger = logger;
    }

    /// <summary>
    /// Renews the claim of the turn of <paramref name="scope"/> now and keeps renewing it until the
    /// returned heartbeat is disposed.
    /// </summary>
    public static async Task<NhAssistantTurnHeartbeat> StartAsync(
        INhAssistantStore store,
        NhAssistantTurnScope scope,
        NhAssistantTiming timing,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(timing);
        var heartbeat = new NhAssistantTurnHeartbeat(store, scope.ConversationId, scope.TurnId, logger);
        await heartbeat.BeatAsync();
        heartbeat._beating = heartbeat.BeatEveryAsync(timing.TurnHeartbeatInterval, timing.TurnClock);
        return heartbeat;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _beating;
        _stop.Dispose();
        await BeatAsync();
    }

    private async Task BeatEveryAsync(TimeSpan interval, TimeProvider clock)
    {
        while (true)
        {
            try
            {
                await Task.Delay(interval, clock, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await BeatAsync();
        }
    }

    private async Task BeatAsync()
    {
        if (!_owned)
        {
            return;
        }

        try
        {
            if (!await _store.TryKeepTurnAsync(_conversationId, _turnId, CancellationToken.None))
            {
                // The turn ended, or another took the conversation over; nothing is left to keep.
                _owned = false;
                _logger?.LogWarning(
                    "Assistant turn {TurnId} no longer owns conversation {ConversationId} while it waits for its person.",
                    _turnId,
                    _conversationId);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A missed beat is caught up by the next; the turn goes on waiting.
            _logger?.LogWarning(
                "Assistant turn {TurnId} could not renew its claim on conversation {ConversationId} ({ExceptionType}).",
                _turnId,
                _conversationId,
                exception.GetType().Name);
        }
    }
}
