using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

internal sealed class NhAssistantStore(
    NhAssistantDbContextFactory contextFactory,
    ILogger<NhAssistantStore> logger) : INhAssistantStore
{
    /// <summary>
    /// How long a join waits for the other joins of its conversation: the default
    /// <c>TransactionLockTimeoutMilliseconds</c> of the platform's background operations. A join holds
    /// the lock for a few short statements, so only a stalled database makes another join wait this long.
    /// </summary>
    internal static readonly TimeSpan DefaultParticipantLockTimeout = TimeSpan.FromSeconds(5);

    internal TimeSpan ParticipantLockTimeout { get; init; } = DefaultParticipantLockTimeout;

    public async Task AddConversationAsync(
        AssistantConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        await using var context = contextFactory.CreateDbContext();
        context.Conversations.Add(conversation);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AssistantConversation?> FindConversationAsync(
        Guid conversationId,
        string ownerActorId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.Id == conversationId
                && conversation.OwnerActorId == ownerActorId
                && conversation.Status != NhAssistantConversationStatuses.Archived)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<NhAssistantConversationAccess?> FindAccessAsync(
        Guid conversationId,
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        await using var context = contextFactory.CreateDbContext();
        var conversation = await context.Conversations
            .AsNoTracking()
            .Where(item => item.Id == conversationId
                && item.Status != NhAssistantConversationStatuses.Archived)
            .SingleOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            return null;
        }
        if (string.Equals(conversation.OwnerActorId, actorId, StringComparison.Ordinal))
        {
            return new NhAssistantConversationAccess(conversation, actorId, null);
        }

        // A participant never reaches a conversation of another tenant, even with a stale row.
        if (!string.Equals(conversation.TenantId, tenantId, StringComparison.Ordinal))
        {
            return null;
        }
        var participant = await context.Participants
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ConversationId == conversationId && item.ActorId == actorId,
                cancellationToken);
        return participant is null
            ? null
            : new NhAssistantConversationAccess(conversation, actorId, participant);
    }

    public async Task<AssistantConversation?> FindShareableConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.Id == conversationId
                && conversation.Status != NhAssistantConversationStatuses.Archived)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<NhAssistantConversationListItem> Items, int Total)> ListConversationsAsync(
        string actorId,
        string? tenantId,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        await using var context = contextFactory.CreateDbContext();
        // Agent threads live on their subject's page, not in the chat list.
        var query = context.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.Status != NhAssistantConversationStatuses.Archived
                && conversation.Kind == NhAssistantConversationKinds.Chat
                && (conversation.OwnerActorId == actorId
                    || (conversation.TenantId == tenantId
                        && conversation.Participants.Any(participant => participant.ActorId == actorId))));
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .ThenBy(conversation => conversation.Id)
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage)
            .Select(conversation => new
            {
                Conversation = conversation,
                ParticipantCount = conversation.Participants.Count(),
                LastMessageSequence = conversation.Messages.Max(message => (int?)message.Sequence) ?? 0,
                ParticipantLastRead = conversation.Participants
                    .Where(participant => participant.ActorId == actorId)
                    .Select(participant => (int?)participant.LastReadSequence)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var items = new List<NhAssistantConversationListItem>(rows.Count);
        foreach (var row in rows)
        {
            var isOwner = string.Equals(row.Conversation.OwnerActorId, actorId, StringComparison.Ordinal);
            // An owner whose read state was never tracked has read everything.
            var lastRead = isOwner
                ? row.Conversation.OwnerLastReadSequence ?? row.LastMessageSequence
                : row.ParticipantLastRead ?? 0;
            items.Add(new NhAssistantConversationListItem(
                row.Conversation,
                isOwner ? NhAssistantParticipantRoles.Owner : NhAssistantParticipantRoles.Participant,
                row.ParticipantCount,
                row.LastMessageSequence,
                lastRead));
        }
        return (items, total);
    }

    public async Task<NhAssistantConversationState?> GetConversationStateAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.Id == conversationId)
            .Select(conversation => new NhAssistantConversationState(
                conversation.Id,
                conversation.Status,
                conversation.Title,
                conversation.UpdatedAt,
                conversation.ActiveTurnId == null
                    ? null
                    : conversation.ActiveActorId ?? conversation.OwnerActorId,
                conversation.Messages.Max(message => (int?)message.Sequence) ?? 0,
                conversation.Participants.Count()))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> TryArchiveConversationAsync(
        Guid conversationId,
        string ownerActorId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var affected = await context.Conversations
            .Where(conversation => conversation.Id == conversationId
                && conversation.OwnerActorId == ownerActorId
                && conversation.Status != NhAssistantConversationStatuses.Running
                && conversation.Status != NhAssistantConversationStatuses.Archived)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(conversation => conversation.Status, NhAssistantConversationStatuses.Archived)
                    .SetProperty(conversation => conversation.ActiveTurnId, (Guid?)null)
                    .SetProperty(conversation => conversation.ActiveActorId, (string?)null)
                    .SetProperty(conversation => conversation.ProtectedShareToken, (string?)null)
                    .SetProperty(conversation => conversation.UpdatedAt, now)
                    .SetProperty(conversation => conversation.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        if (affected != 1)
        {
            return false;
        }

        // The texts reading tools made of the conversation's files go with it (D32); a deleted thread
        // takes them along by cascade.
        await context.FileReaderTexts
            .Where(text => text.ConversationId == conversationId)
            .ExecuteDeleteAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TryBeginTurnAsync(
        Guid conversationId,
        string actorId,
        IReadOnlyCollection<string> expectedStatuses,
        Guid turnId,
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedStatuses);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var statuses = expectedStatuses.ToArray();
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var affected = await context.Conversations
            .Where(conversation => conversation.Id == conversationId
                && (statuses.Contains(conversation.Status)
                    || (conversation.Status == NhAssistantConversationStatuses.Running
                        && conversation.UpdatedAt < staleBefore)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(conversation => conversation.Status, NhAssistantConversationStatuses.Running)
                    .SetProperty(conversation => conversation.ActiveTurnId, turnId)
                    .SetProperty(conversation => conversation.ActiveActorId, actorId)
                    .SetProperty(conversation => conversation.UpdatedAt, now)
                    .SetProperty(conversation => conversation.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryEndTurnAsync(
        Guid conversationId,
        Guid turnId,
        string status,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var waiting = status == NhAssistantConversationStatuses.WaitingForApproval;
        Guid? activeTurnId = waiting ? turnId : null;
        var affected = await context.Conversations
            .Where(conversation => conversation.Id == conversationId
                && conversation.ActiveTurnId == turnId
                && conversation.Status == NhAssistantConversationStatuses.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(conversation => conversation.Status, status)
                    .SetProperty(conversation => conversation.ActiveTurnId, activeTurnId)
                    // The actor who waits for approval keeps the turn; any other outcome releases it.
                    .SetProperty(
                        conversation => conversation.ActiveActorId,
                        conversation => waiting ? conversation.ActiveActorId : null)
                    .SetProperty(conversation => conversation.UpdatedAt, now)
                    .SetProperty(conversation => conversation.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return affected == 1;
    }

    public async Task SetTitleIfEmptyAsync(
        Guid conversationId,
        string title,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var bounded = title.Length > 200 ? title[..200] : title;
        await using var context = contextFactory.CreateDbContext();
        await context.Conversations
            .Where(conversation => conversation.Id == conversationId && conversation.Title == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(conversation => conversation.Title, bounded),
                cancellationToken);
    }

    public async Task<bool> ClientMessageExistsAsync(
        Guid conversationId,
        string clientMessageId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Messages
            .AnyAsync(
                message => message.ConversationId == conversationId
                    && message.ClientMessageId == clientMessageId,
                cancellationToken);
    }

    public async Task AddMessageAsync(
        AssistantMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var context = contextFactory.CreateDbContext();
        var lastSequence = await context.Messages
            .Where(item => item.ConversationId == message.ConversationId)
            .Select(item => (int?)item.Sequence)
            .MaxAsync(cancellationToken);
        message.Sequence = (lastSequence ?? 0) + 1;
        context.Messages.Add(message);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AssistantMessage?> FindApplicationMessageAsync(
        Guid conversationId,
        string clientMessageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientMessageId);
        await using var context = contextFactory.CreateDbContext();
        return await context.Messages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId
                && message.Role == NhAssistantMessageRoles.Application
                && message.ClientMessageId == clientMessageId)
            .OrderBy(message => message.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task AddApplicationMessageAsync(
        AssistantMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await AddMessageAsync(message, cancellationToken);

        // The conversation moves up in its people's lists. A running turn keeps its own update time,
        // which tells an abandoned turn from a live one.
        await using var context = contextFactory.CreateDbContext();
        await context.Conversations
            .Where(conversation => conversation.Id == message.ConversationId
                && conversation.Status != NhAssistantConversationStatuses.Running
                && conversation.UpdatedAt < message.CreatedAt)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(conversation => conversation.UpdatedAt, message.CreatedAt),
                cancellationToken);
    }

    public async Task UpdateMessageAsync(
        Guid messageId,
        string partsJson,
        int inputTokens,
        int outputTokens,
        int toolCalls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(partsJson);
        await using var context = contextFactory.CreateDbContext();
        await context.Messages
            .Where(message => message.Id == messageId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.PartsJson, partsJson)
                    .SetProperty(message => message.InputTokens, inputTokens)
                    .SetProperty(message => message.OutputTokens, outputTokens)
                    .SetProperty(message => message.ToolCalls, toolCalls),
                cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantMessage>> GetMessagesAsync(
        Guid conversationId,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var latest = await context.Messages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderByDescending(message => message.Sequence)
            .Take(maximum)
            .ToListAsync(cancellationToken);
        latest.Reverse();
        return latest;
    }

    public async Task<AssistantMessage?> GetFirstUserMessageAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Messages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId && message.Role == NhAssistantMessageRoles.User)
            .OrderBy(message => message.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task AddToolInvocationAsync(
        AssistantToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        await using var context = contextFactory.CreateDbContext();
        context.ToolInvocations.Add(invocation);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateToolInvocationAsync(
        AssistantToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        await using var context = contextFactory.CreateDbContext();
        context.ToolInvocations.Update(invocation);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AssistantToolInvocation?> FindToolInvocationAsync(
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.ToolInvocations
            .AsNoTracking()
            .SingleOrDefaultAsync(invocation => invocation.Id == invocationId, cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantToolInvocation>> GetToolInvocationsAsync(
        Guid conversationId,
        IReadOnlyCollection<Guid> invocationIds,
        CancellationToken cancellationToken)
    {
        if (invocationIds.Count == 0)
        {
            return [];
        }
        var ids = invocationIds.ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.ToolInvocations
            .AsNoTracking()
            .Where(invocation => invocation.ConversationId == conversationId && ids.Contains(invocation.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task SetMessageAuthorAsync(
        Guid messageId,
        string authorActorId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorActorId);
        await using var context = contextFactory.CreateDbContext();
        await context.Messages
            .Where(message => message.Id == messageId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.AuthorActorId, authorActorId), cancellationToken);
    }

    public Task AddApprovalAsync(
        AssistantApproval approval,
        CancellationToken cancellationToken)
    {
        return AddApprovalAsync(approval, [], cancellationToken);
    }

    public async Task AddApprovalAsync(
        AssistantApproval approval,
        IReadOnlyCollection<AssistantApprovalKey> keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(keys);
        await using var context = contextFactory.CreateDbContext();
        context.Approvals.Add(approval);
        context.ApprovalKeys.AddRange(keys);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AssistantApproval?> FindApprovalAsync(
        Guid approvalId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .SingleOrDefaultAsync(approval => approval.Id == approvalId, cancellationToken);
    }

    public async Task<AssistantApproval?> FindPendingApprovalAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.ConversationId == conversationId
                && approval.Status == NhAssistantApprovalStatuses.Pending
                // A consultation's proposal waits in the consulted agent's thread, never for the
                // thread's own waiting turn.
                && approval.ConsultationId == null)
            .OrderByDescending(approval => approval.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantApproval>> GetApprovalsAsync(
        Guid conversationId,
        IReadOnlyCollection<Guid> approvalIds,
        CancellationToken cancellationToken)
    {
        if (approvalIds.Count == 0)
        {
            return [];
        }
        var ids = approvalIds.ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.ConversationId == conversationId && ids.Contains(approval.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryDecideApprovalAsync(
        Guid approvalId,
        string status,
        string? decidedByActorId,
        DateTimeOffset decidedAt,
        DateTimeOffset? approvalExpiresAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        await using var context = contextFactory.CreateDbContext();
        var affected = await context.Approvals
            .Where(approval => approval.Id == approvalId
                && approval.Status == NhAssistantApprovalStatuses.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(approval => approval.Status, status)
                    .SetProperty(approval => approval.DecidedByActorId, decidedByActorId)
                    .SetProperty(approval => approval.DecidedAt, decidedAt)
                    .SetProperty(approval => approval.ApprovalExpiresAt, approvalExpiresAt)
                    .SetProperty(approval => approval.Reason, reason)
                    .SetProperty(approval => approval.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return affected == 1;
    }

    public async Task<int> GetToolCallsAsync(
        string actorId,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.BudgetLedgers
            .Where(ledger => ledger.ActorId == actorId && ledger.Day == day)
            .Select(ledger => (int?)ledger.ToolCalls)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
    }

    public async Task<bool> TryReleaseWaitingTurnAsync(
        Guid conversationId,
        Guid turnId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var affected = await context.Conversations
            .Where(conversation => conversation.Id == conversationId
                && conversation.ActiveTurnId == turnId
                && conversation.Status == NhAssistantConversationStatuses.WaitingForApproval)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(conversation => conversation.Status, NhAssistantConversationStatuses.Idle)
                    .SetProperty(conversation => conversation.ActiveTurnId, (Guid?)null)
                    .SetProperty(conversation => conversation.ActiveActorId, (string?)null)
                    .SetProperty(conversation => conversation.UpdatedAt, now)
                    .SetProperty(conversation => conversation.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryReleaseWaitingConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var affected = await context.Conversations
            .Where(conversation => conversation.Id == conversationId
                && conversation.Status == NhAssistantConversationStatuses.WaitingForApproval)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(conversation => conversation.Status, NhAssistantConversationStatuses.Idle)
                    .SetProperty(conversation => conversation.ActiveTurnId, (Guid?)null)
                    .SetProperty(conversation => conversation.ActiveActorId, (string?)null)
                    .SetProperty(conversation => conversation.UpdatedAt, now)
                    .SetProperty(conversation => conversation.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return affected == 1;
    }

    public async Task<int?> MarkReadAsync(
        NhAssistantConversationAccess access,
        int sequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        await using var context = contextFactory.CreateDbContext();
        var conversationId = access.Conversation.Id;
        var latest = await context.Messages
            .Where(message => message.ConversationId == conversationId)
            .Select(message => (int?)message.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
        var target = Math.Clamp(sequence, 0, latest);

        int affected;
        if (access.IsOwner)
        {
            affected = await context.Conversations
                .Where(conversation => conversation.Id == conversationId
                    && conversation.OwnerActorId == access.ActorId
                    && (conversation.OwnerLastReadSequence == null || conversation.OwnerLastReadSequence < target))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(conversation => conversation.OwnerLastReadSequence, target),
                    cancellationToken);
        }
        else
        {
            affected = await context.Participants
                .Where(participant => participant.ConversationId == conversationId
                    && participant.ActorId == access.ActorId
                    && participant.LastReadSequence < target)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(participant => participant.LastReadSequence, target),
                    cancellationToken);
        }
        return affected == 1 ? target : null;
    }

    public async Task UpdateDisplayNameAsync(
        NhAssistantConversationAccess access,
        string displayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(displayName);
        await using var context = contextFactory.CreateDbContext();
        var conversationId = access.Conversation.Id;
        if (access.IsOwner)
        {
            await context.Conversations
                .Where(conversation => conversation.Id == conversationId
                    && conversation.OwnerActorId == access.ActorId
                    && (conversation.OwnerDisplayName == null || conversation.OwnerDisplayName != displayName))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(conversation => conversation.OwnerDisplayName, displayName),
                    cancellationToken);
            return;
        }

        await context.Participants
            .Where(participant => participant.ConversationId == conversationId
                && participant.ActorId == access.ActorId
                && participant.DisplayName != displayName)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(participant => participant.DisplayName, displayName),
                cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantConversationParticipant>> GetParticipantsAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Participants
            .AsNoTracking()
            .Where(participant => participant.ConversationId == conversationId)
            .OrderBy(participant => participant.JoinedAt)
            .ThenBy(participant => participant.ActorId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Joins of one conversation take turns under the platform's transaction lock, so the check, the
    /// count and the insert of a join see every earlier join: a second join of the same actor finds
    /// the row instead of failing on the primary key, and joins of different actors cannot pass the
    /// limit together. When the lock stays taken past <see cref="ParticipantLockTimeout"/>, the join
    /// continues without it, as before the lock, with the primary key against duplicates.
    /// </summary>
    public async Task<NhAssistantParticipantAddResult> TryAddParticipantAsync(
        AssistantConversationParticipant participant,
        int maximumParticipants,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(participant);

        var locked = await TryAddParticipantUnderLockAsync(participant, maximumParticipants, cancellationToken);
        if (locked is not null)
        {
            return locked.Value;
        }

        logger.LogWarning(
            "A join of assistant conversation {ConversationId} waited {LockTimeout} for the conversation's participant lock and continues without it; the participant limit is not guaranteed for this join.",
            participant.ConversationId,
            ParticipantLockTimeout);
        await using var context = contextFactory.CreateDbContext();
        return await AddParticipantAsync(context, participant, maximumParticipants, cancellationToken);
    }

    /// <summary>
    /// Adds the participant in a transaction that holds the conversation's participant lock;
    /// <see langword="null"/> when the lock could not be taken in time.
    /// </summary>
    private async Task<NhAssistantParticipantAddResult?> TryAddParticipantUnderLockAsync(
        AssistantConversationParticipant participant,
        int maximumParticipants,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await contextFactory.TransactionLocks.TryAcquireAsync(
            context,
            ParticipantLockResource(participant.ConversationId),
            ParticipantLockTimeout,
            cancellationToken))
        {
            return null;
        }

        var result = await AddParticipantAsync(context, participant, maximumParticipants, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<NhAssistantParticipantAddResult> AddParticipantAsync(
        NhAssistantDbContext context,
        AssistantConversationParticipant participant,
        int maximumParticipants,
        CancellationToken cancellationToken)
    {
        var conversationId = participant.ConversationId;
        var actorId = participant.ActorId;
        if (await context.Participants.AnyAsync(
            item => item.ConversationId == conversationId && item.ActorId == actorId,
            cancellationToken))
        {
            return NhAssistantParticipantAddResult.AlreadyParticipant;
        }

        var count = await context.Participants.CountAsync(
            item => item.ConversationId == conversationId,
            cancellationToken);
        if (count >= maximumParticipants)
        {
            return NhAssistantParticipantAddResult.LimitReached;
        }

        context.Participants.Add(participant);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return NhAssistantParticipantAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // A writer without the lock, such as an instance of an earlier version during a rolling
            // deploy or a join that gave up waiting, inserted the same actor first. EF Core rolled the
            // failed insert back to its savepoint, so the transaction can still read; any other
            // failure is rethrown.
            if (await context.Participants.AnyAsync(
                item => item.ConversationId == conversationId && item.ActorId == actorId,
                cancellationToken))
            {
                return NhAssistantParticipantAddResult.AlreadyParticipant;
            }

            throw;
        }
    }

    /// <summary>
    /// The transaction lock that joins of one conversation take.
    /// </summary>
    internal static string ParticipantLockResource(Guid conversationId)
    {
        return $"NhAssistant:ConversationParticipants:{conversationId:N}";
    }

    public async Task<bool> TryRemoveParticipantAsync(
        Guid conversationId,
        string actorId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var affected = await context.Participants
            .Where(participant => participant.ConversationId == conversationId && participant.ActorId == actorId)
            .ExecuteDeleteAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> SetProtectedShareTokenAsync(
        Guid conversationId,
        string ownerActorId,
        string? protectedToken,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var affected = await context.Conversations
            .Where(conversation => conversation.Id == conversationId
                && conversation.OwnerActorId == ownerActorId
                && conversation.Status != NhAssistantConversationStatuses.Archived)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(conversation => conversation.ProtectedShareToken, protectedToken),
                cancellationToken);
        return affected == 1;
    }

    public async Task<IReadOnlyList<string>> GetAudienceAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var owner = await context.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.Id == conversationId
                && conversation.Status != NhAssistantConversationStatuses.Archived)
            .Select(conversation => conversation.OwnerActorId)
            .SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return [];
        }
        var participants = await context.Participants
            .AsNoTracking()
            .Where(participant => participant.ConversationId == conversationId)
            .Select(participant => participant.ActorId)
            .ToListAsync(cancellationToken);
        return [owner, .. participants];
    }
}
