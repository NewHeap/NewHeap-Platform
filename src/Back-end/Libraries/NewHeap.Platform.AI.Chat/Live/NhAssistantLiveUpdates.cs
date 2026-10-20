using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
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
/// A run, a proposal or its feedback of an agent thread changed. It names what changed and carries
/// no content, so it may reach everyone on the subject, also people who never opened the thread.
/// </summary>
internal sealed record NhAssistantLiveAgentWorkChanged(
    Guid ConversationId,
    string Reason,
    Guid? ApprovalId,
    Guid? RunId,
    string SubjectType,
    string SubjectId)
    : NhAssistantLiveEvent(ConversationId);

/// <summary>
/// The work of one agent for the whole application in one scope (<c>nh-system/global</c> or a
/// division), which people who may read it follow live while a screen such as the approval inbox or a
/// board shows it. The whole application lists no members, so its updates reach these watchers besides
/// the people of the thread.
/// </summary>
internal sealed record NhAssistantWatchedWork(string AgentId, string SubjectId);

/// <summary>
/// The <c>Reason</c> values of <see cref="NhAssistantLiveAgentWorkChanged"/>.
/// </summary>
internal static class NhAssistantAgentWorkReasons
{
    public const string Run = "run";
    public const string Proposal = "proposal";
    public const string Feedback = "feedback";
}

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

    /// <summary>
    /// Sends an update about an agent's work for the whole application to the connections that watch
    /// that work. A transport without watchers sends nothing.
    /// </summary>
    Task SendToWatchersAsync(
        NhAssistantWatchedWork work,
        NhAssistantLiveAgentWorkChanged evt,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
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
/// Who belongs to a conversation: its owner and participants, and, for an agent's thread of the whole
/// application, the subject and agent whose view policy every listener must pass.
/// </summary>
internal sealed record NhAssistantConversationAudienceEntry(IReadOnlyList<string> ActorIds, NhAiSubjectRef? SystemSubject, string? AgentId);

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
        return (await GetEntryAsync(conversationId, cancellationToken)).ActorIds;
    }

    public async ValueTask<NhAssistantConversationAudienceEntry> GetEntryAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_entries.TryGetValue(conversationId, out var cached) && now - cached.LoadedAt < TimeToLive)
        {
            return cached.Audience;
        }

        var actorIds = await new NhAssistantStore(contextFactory).GetAudienceAsync(conversationId, cancellationToken);
        await using var context = contextFactory.CreateDbContext();
        var system = await context.Threads
            .AsNoTracking()
            .Where(thread => thread.ConversationId == conversationId && thread.SubjectType == NhAiSubjectRef.SystemType)
            .Select(thread => new { thread.SubjectId, thread.AgentId })
            .SingleOrDefaultAsync(cancellationToken);
        var audience = new NhAssistantConversationAudienceEntry(
            actorIds,
            system is null ? null : new NhAiSubjectRef(NhAiSubjectRef.SystemType, system.SubjectId),
            system?.AgentId);
        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }
        _entries[conversationId] = new Entry(audience, now);
        return audience;
    }

    public void Invalidate(Guid conversationId)
    {
        _entries.TryRemove(conversationId, out _);
    }

    private sealed record Entry(NhAssistantConversationAudienceEntry Audience, DateTimeOffset LoadedAt);
}

/// <summary>
/// Publishes conversation changes and turn events to everyone who belongs to the conversation. An
/// agent's thread of the whole application reaches only the people who may read it, those who pass
/// the agent's view policy, as its thread, results and proposals do. Publishing never fails a turn or
/// a request: a failure is logged, because clients recover through snapshots.
/// </summary>
internal sealed class NhAssistantLivePublisher(
    INhAssistantLiveUpdateTransport transport,
    NhAssistantConversationAudience audience,
    INhAssistantStore store,
    NhAssistantSubjectAccessService subjects,
    ILogger<NhAssistantLivePublisher> logger)
{
    public async ValueTask PublishAsync(NhAssistantLiveEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        try
        {
            var entry = await audience.GetEntryAsync(evt.ConversationId, cancellationToken);
            await transport.SendAsync(await ReadersAsync(entry, entry.ActorIds, cancellationToken), evt, cancellationToken);
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
    /// Sends an event to the conversation's audience and to <paramref name="actorIds"/>.
    /// </summary>
    public async ValueTask PublishWithAsync(
        IReadOnlyCollection<string> actorIds,
        NhAssistantLiveEvent evt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = await audience.GetEntryAsync(evt.ConversationId, cancellationToken);
            await transport.SendAsync(await ReadersAsync(entry, [.. entry.ActorIds, .. actorIds], cancellationToken), evt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogFailure(evt.ConversationId, exception);
        }
    }

    /// <summary>
    /// Sends an update about an agent's work for the whole application to the people who watch that
    /// work: they joined after the server checked that they read the agent's thread there, its view
    /// policy included, and that check is repeated while they keep watching.
    /// </summary>
    public async ValueTask PublishToWatchersAsync(
        NhAssistantWatchedWork work,
        NhAssistantLiveAgentWorkChanged evt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(evt);
        try
        {
            await transport.SendToWatchersAsync(work, evt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogFailure(evt.ConversationId, exception);
        }
    }

    /// <summary>
    /// The people among <paramref name="actorIds"/> who may hear about the conversation: everyone for
    /// a conversation or an object's thread, for an agent's thread of the whole application only those
    /// who pass the agent's view policy (the resolver keeps these decisions a few seconds).
    /// </summary>
    private async ValueTask<IReadOnlyCollection<string>> ReadersAsync(
        NhAssistantConversationAudienceEntry entry,
        IReadOnlyCollection<string> actorIds,
        CancellationToken cancellationToken)
    {
        if (entry.SystemSubject is not { } subject || entry.AgentId is not { } agentId)
        {
            return actorIds;
        }

        var readers = new List<string>(actorIds.Count);
        foreach (var actorId in actorIds.Distinct(StringComparer.Ordinal))
        {
            if (!actorId.StartsWith(NhAssistantAgentEvents.AgentActorPrefix, StringComparison.Ordinal)
                && await subjects.MayReadAgentThreadAsync(subject, agentId, actorId, cancellationToken))
            {
                readers.Add(actorId);
            }
        }

        return readers;
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
            exception,
            "Assistant live update for conversation {ConversationId} was not published.",
            conversationId);
    }
}
