using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
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
    /// Sends an update to the connections of each recipient: all of them, or only those signed in for
    /// one of the recipient's tenants. A transport that does not know the tenant of a connection sends
    /// to every connection of the recipients.
    /// </summary>
    Task SendAsync(
        IReadOnlyCollection<NhAssistantLiveRecipient> recipients,
        NhAssistantLiveEvent evt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        return SendAsync(
            recipients.Select(recipient => recipient.ActorId).Distinct(StringComparer.Ordinal).ToArray(),
            evt,
            cancellationToken);
    }

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
/// One actor a live update reaches: on every connection, or only on the connections signed in for one
/// of <see cref="Tenants"/>, the sign-ins through which the actor reaches the conversation.
/// </summary>
internal sealed record NhAssistantLiveRecipient(string ActorId, IReadOnlyList<string?>? Tenants)
{
    /// <summary>The actor on every connection, such as for their own read position.</summary>
    public static NhAssistantLiveRecipient Everywhere(string actorId)
    {
        return new NhAssistantLiveRecipient(actorId, null);
    }

    /// <summary>The actor on the connections signed in for one of <paramref name="tenants"/>.</summary>
    public static NhAssistantLiveRecipient InTenants(string actorId, IReadOnlyList<string?> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        return new NhAssistantLiveRecipient(actorId, tenants);
    }
}

/// <summary>
/// Who belongs to a conversation: the conversation, its owner and participants, and its agent thread
/// when it is one. The publisher keeps the conversation's read rule and, for
/// <paramref name="readerLifetime"/>, whether each person may read it.
/// </summary>
internal sealed class NhAssistantConversationAudienceEntry(
    AssistantConversation? conversation,
    IReadOnlyList<string> actorIds,
    AssistantThread? thread,
    TimeSpan readerLifetime)
{
    private readonly ConcurrentDictionary<string, (bool MayRead, long At)> _readers = new(StringComparer.Ordinal);

    /// <summary>The conversation, or <see langword="null"/> when it was deleted or never existed.</summary>
    public AssistantConversation? Conversation { get; } = conversation;

    /// <summary>The owner, then the participants.</summary>
    public IReadOnlyList<string> ActorIds { get; } = actorIds;

    /// <summary>The conversation's agent thread, or <see langword="null"/> for a chat.</summary>
    public AssistantThread? Thread { get; } = thread;

    /// <summary>The read rule, once a publisher decided it.</summary>
    public NhAssistantConversationReadRule? Rule { get; set; }

    public bool TryGetReader(string actorId, out bool mayRead)
    {
        if (_readers.TryGetValue(actorId, out var cached)
            && Environment.TickCount64 - cached.At < (long)readerLifetime.TotalMilliseconds)
        {
            mayRead = cached.MayRead;
            return true;
        }

        mayRead = false;
        return false;
    }

    public void SetReader(string actorId, bool mayRead)
    {
        if (_readers.Count >= NhAssistantConversationAudience.MaxReadersPerEntry)
        {
            _readers.Clear();
        }
        _readers[actorId] = (mayRead, Environment.TickCount64);
    }
}

/// <summary>
/// Caches who belongs to a conversation, so every streamed delta reaches the owner and current
/// participants without a database round trip. Membership changes in this process take effect
/// immediately; changes made by another instance after at most <see cref="TimeToLive"/>. Whether each
/// person may read the conversation is decided again after <see cref="ReaderTimeToLive"/>, so a role or
/// permission that ended stops the updates within that time.
/// </summary>
internal sealed class NhAssistantConversationAudience(NhAssistantDbContextFactory contextFactory)
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);

    /// <summary>How long a decision about one person's reading is kept.</summary>
    public static readonly TimeSpan ReaderTimeToLive = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long this audience keeps a decision about one person's reading: <see cref="ReaderTimeToLive"/>,
    /// or less in a test that changes people between two updates.
    /// </summary>
    internal TimeSpan ReaderLifetime { get; init; } = ReaderTimeToLive;

    internal const int MaxReadersPerEntry = 1_024;
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

        await using var context = contextFactory.CreateDbContext();
        var conversation = await context.Conversations
            .AsNoTracking()
            .Where(item => item.Id == conversationId
                && item.Status != NhAssistantConversationStatuses.Archived)
            .SingleOrDefaultAsync(cancellationToken);
        IReadOnlyList<string> actorIds = [];
        AssistantThread? thread = null;
        if (conversation is not null)
        {
            var participants = await context.Participants
                .AsNoTracking()
                .Where(participant => participant.ConversationId == conversationId)
                .Select(participant => participant.ActorId)
                .ToListAsync(cancellationToken);
            actorIds = [conversation.OwnerActorId, .. participants];
            thread = await context.Threads
                .AsNoTracking()
                .Where(item => item.ConversationId == conversationId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        var audience = new NhAssistantConversationAudienceEntry(conversation, actorIds, thread, ReaderLifetime);
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

//// <summary>
/// Publishes conversation changes and turn events to the people who may read the conversation at the
/// moment of sending, with the rule its endpoints apply on every request
/// (<see cref="NhAssistantConversationReaders"/>): a chat reaches its owner and the participants who pass
/// the agent's policy, an agent thread the people the subject's resolver gives a role now or who were
/// invited, and for the whole application only those who pass the agent's view policy. A stale
/// participant row, such as that of a person whose role on the subject ended, reaches nobody.
/// Participants hear about a conversation only on the connections signed in for a tenant that reaches
/// it. Publishing never fails a turn or a request: a failure is logged, because clients recover through
/// snapshots.
/// </summary>
internal sealed class NhAssistantLivePublisher(
    INhAssistantLiveUpdateTransport transport,
    NhAssistantConversationAudience audience,
    INhAssistantStore store,
    NhAssistantConversationReaders readers,
    ILogger<NhAssistantLivePublisher> logger)
{
    public async ValueTask PublishAsync(NhAssistantLiveEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        try
        {
            var entry = await audience.GetEntryAsync(evt.ConversationId, cancellationToken);
            await transport.SendAsync(await RecipientsAsync(entry, entry.ActorIds, cancellationToken), evt, cancellationToken);
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
    /// Sends an event to one actor only, on every connection, such as their own read position or their
    /// removal.
    /// </summary>
    public async ValueTask PublishToAsync(string actorId, NhAssistantLiveEvent evt, CancellationToken cancellationToken = default)
    {
        try
        {
            await transport.SendAsync([NhAssistantLiveRecipient.Everywhere(actorId)], evt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogFailure(evt.ConversationId, exception);
        }
    }

    /// <summary>
    /// Sends an event to the conversation's audience and to <paramref name="actorIds"/>, each of them
    /// only while the conversation's read rule admits them.
    /// </summary>
    public async ValueTask PublishWithAsync(
        IReadOnlyCollection<string> actorIds,
        NhAssistantLiveEvent evt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = await audience.GetEntryAsync(evt.ConversationId, cancellationToken);
            await transport.SendAsync(await RecipientsAsync(entry, [.. entry.ActorIds, .. actorIds], cancellationToken), evt, cancellationToken);
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
    /// The people among <paramref name="actorIds"/> who may receive an update now: the owner of a chat
    /// on every connection, everyone else on the connections of the tenants that reach the conversation,
    /// once the read rule admits them. Decisions are kept for
    /// <see cref="NhAssistantConversationAudience.ReaderTimeToLive"/>.
    /// </summary>
    private async ValueTask<IReadOnlyCollection<NhAssistantLiveRecipient>> RecipientsAsync(
        NhAssistantConversationAudienceEntry entry,
        IReadOnlyCollection<string> actorIds,
        CancellationToken cancellationToken)
    {
        if (entry.Conversation is not { } conversation)
        {
            return [];
        }

        var rule = entry.Rule ??= await readers.RuleOfAsync(conversation, entry.Thread, cancellationToken);
        var recipients = new List<NhAssistantLiveRecipient>(actorIds.Count);
        foreach (var actorId in actorIds.Distinct(StringComparer.Ordinal))
        {
            if (rule.Thread is null && string.Equals(actorId, rule.OwnerActorId, StringComparison.Ordinal))
            {
                // The owner of a chat reads it in every tenant, as its snapshot endpoint answers them.
                recipients.Add(NhAssistantLiveRecipient.Everywhere(actorId));
                continue;
            }

            if (!entry.TryGetReader(actorId, out var mayRead))
            {
                mayRead = await readers.MayReadAsync(rule, actorId, cancellationToken);
                entry.SetReader(actorId, mayRead);
            }
            if (mayRead)
            {
                recipients.Add(NhAssistantLiveRecipient.InTenants(actorId, rule.Tenants));
            }
        }

        return recipients;
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
