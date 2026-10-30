using System.Threading.Channels;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Lets a caller on this host follow the turns of a conversation it did not start. A turn that runs on
/// another host reaches no watcher here, so whoever waits also reads the conversation.
/// </summary>
internal sealed class NhAssistantTurnWatchers
{
    /// <summary>
    /// The events one watcher holds before the oldest go: text a slow caller missed is in the stored
    /// message, and the turn's last event is always the newest.
    /// </summary>
    internal const int MaxPendingEvents = 1_024;

    private readonly object _sync = new();
    private readonly Dictionary<Guid, NhAssistantTurnWatch[]> _watches = [];

    /// <summary>
    /// Starts following the turns of <paramref name="conversationId"/> until the watch is disposed.
    /// </summary>
    public NhAssistantTurnWatch Watch(Guid conversationId)
    {
        var watch = new NhAssistantTurnWatch(this, conversationId);
        lock (_sync)
        {
            _watches[conversationId] = _watches.TryGetValue(conversationId, out var existing)
                ? [.. existing, watch]
                : [watch];
        }

        return watch;
    }

    /// <summary>
    /// Hands an event of a turn to its watchers. It never blocks the turn.
    /// </summary>
    public void Publish(Guid conversationId, NhAssistantTurnEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        NhAssistantTurnWatch[]? watches;
        lock (_sync)
        {
            if (!_watches.TryGetValue(conversationId, out watches))
            {
                return;
            }
        }

        foreach (var watch in watches)
        {
            watch.Write(evt);
        }
    }

    /// <summary>
    /// Whether a caller follows the turns of <paramref name="conversationId"/> now.
    /// </summary>
    internal bool IsWatching(Guid conversationId)
    {
        lock (_sync)
        {
            return _watches.ContainsKey(conversationId);
        }
    }

    internal void Stop(NhAssistantTurnWatch watch)
    {
        lock (_sync)
        {
            if (!_watches.TryGetValue(watch.ConversationId, out var existing))
            {
                return;
            }

            var remaining = existing.Where(item => !ReferenceEquals(item, watch)).ToArray();
            if (remaining.Length == 0)
            {
                _watches.Remove(watch.ConversationId);
            }
            else
            {
                _watches[watch.ConversationId] = remaining;
            }
        }
    }
}

/// <summary>
/// One caller's view of the turns of a conversation.
/// </summary>
internal sealed class NhAssistantTurnWatch(NhAssistantTurnWatchers watchers, Guid conversationId) : IDisposable
{
    private readonly Channel<NhAssistantTurnEvent> _events = Channel.CreateBounded<NhAssistantTurnEvent>(
        new BoundedChannelOptions(NhAssistantTurnWatchers.MaxPendingEvents)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public Guid ConversationId { get; } = conversationId;

    public ChannelReader<NhAssistantTurnEvent> Events => _events.Reader;

    internal void Write(NhAssistantTurnEvent evt)
    {
        _events.Writer.TryWrite(evt);
    }

    public void Dispose()
    {
        watchers.Stop(this);
        _events.Writer.TryComplete();
    }
}
