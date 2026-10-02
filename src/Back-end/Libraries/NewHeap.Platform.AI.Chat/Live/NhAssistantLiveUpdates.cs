using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Live;

/// <summary>
/// One live update for the people of a conversation. Live updates are best effort: clients reload
/// snapshots after a reconnect, so every snapshot endpoint stays authoritative.
/// </summary>
internal abstract record NhAssistantLiveEvent(Guid ConversationId);

/// <summary>
/// Status, title, active actor, latest message or participants changed.
/// </summary>
internal sealed record NhAssistantLiveConversationChanged(NhAssistantConversationState State)
    : NhAssistantLiveEvent(State.ConversationId);

/// <summary>
/// The receiving actor read the conversation up to <paramref name="LastReadSequence"/>, for example in another tab.
/// </summary>
internal sealed record NhAssistantLiveConversationRead(Guid ConversationId, int LastReadSequence)
    : NhAssistantLiveEvent(ConversationId);

/// <summary>
/// The receiving actor no longer has access: the conversation was deleted, or the actor left or was removed.
/// </summary>
internal sealed record NhAssistantLiveConversationRemoved(Guid ConversationId)
    : NhAssistantLiveEvent(ConversationId);

/// <summary>
/// A participant's user message was stored, so others can show it before the answer streams.
/// </summary>
internal sealed record NhAssistantLiveMessageCreated(Guid ConversationId, string ActorId, NhAssistantMessageView Message)
    : NhAssistantLiveEvent(ConversationId);

/// <summary>
/// One event of a running turn: the same events the starting request streams.
/// </summary>
internal sealed record NhAssistantLiveTurnEvent(Guid ConversationId, string ActorId, NhAssistantTurnEvent Event)
    : NhAssistantLiveEvent(ConversationId);

/// <summary>
/// Delivers live updates to the open connections of actors. The ASP.NET package sends them through
/// a SignalR hub; without it, live updates are off and clients rely on snapshots.
/// </summary>
internal interface INhAssistantLiveUpdateTransport
{
    Task SendAsync(
        IReadOnlyCollection<string> actorIds,
        NhAssistantLiveEvent evt,
        CancellationToken cancellationToken);
}

internal sealed class NhAssistantNoLiveUpdateTransport : INhAssistantLiveUpdateTransport
{
    public Task SendAsync(
        IReadOnlyCollection<string> actorIds,
        NhAssistantLiveEvent evt,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Caches who belongs to a conversation, so every streamed delta reaches the owner and current
/// participants without a database round trip. Membership changes in this process take effect
/// immediately; changes made by another instance after at most <see cref="TimeToLive"/>.
/// </summary>
internal sealed class NhAssistantConversationAudience(NhAssistantDbContextFactory contextFactory)
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public async ValueTask<IReadOnlyList<string>> GetAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_entries.TryGetValue(conversationId, out var cached) && now - cached.LoadedAt < TimeToLive)
        {
            return cached.ActorIds;
        }

        var actorIds = await new NhAssistantStore(contextFactory).GetAudienceAsync(conversationId, cancellationToken);
        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }
        _entries[conversationId] = new Entry(actorIds, now);
        return actorIds;
    }

    public void Invalidate(Guid conversationId)
    {
        _entries.TryRemove(conversationId, out _);
    }

    private sealed record Entry(IReadOnlyList<string> ActorIds, DateTimeOffset LoadedAt);
}

/// <summary>
/// Publishes conversation changes and turn events to everyone who belongs to the conversation.
/// Publishing never fails a turn or a request: a failure is logged with its exception type only,
/// because clients recover through snapshots.
/// </summary>
internal sealed class NhAssistantLivePublisher(
    INhAssistantLiveUpdateTransport transport,
    NhAssistantConversationAudience audience,
    INhAssistantStore store,
    ILogger<NhAssistantLivePublisher> logger)
{
    public async ValueTask PublishAsync(NhAssistantLiveEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        try
        {
            var actorIds = await audience.GetAsync(evt.ConversationId, cancellationToken);
            await transport.SendAsync(actorIds, evt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogFailure(evt.ConversationId, exception);
        }
    }

    /// <summary>
    /// Reads the shared conversation state and announces it.
    /// </summary>
    public async ValueTask PublishChangedAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        NhAssistantConversationState? state;
        try
        {
            state = await store.GetConversationStateAsync(conversationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogFailure(conversationId, exception);
            return;
        }
        if (state is not null)
        {
            await PublishAsync(new NhAssistantLiveConversationChanged(state), cancellationToken);
        }
    }

    /// <summary>
    /// Sends an event to one actor only, such as their own read position or their removal.
    /// </summary>
    public async ValueTask PublishToAsync(string actorId, NhAssistantLiveEvent evt, CancellationToken cancellationToken = default)
    {
        try
        {
            await transport.SendAsync([actorId], evt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogFailure(evt.ConversationId, exception);
        }
    }

    /// <summary>
    /// Forgets the cached audience after a membership change in this process.
    /// </summary>
    public void MembershipChanged(Guid conversationId)
    {
        audience.Invalidate(conversationId);
    }

    private void LogFailure(Guid conversationId, Exception exception)
    {
        logger.LogWarning(
            "Assistant live update for conversation {ConversationId} was not published ({ExceptionType}).",
            conversationId,
            exception.GetType().Name);
    }
}
