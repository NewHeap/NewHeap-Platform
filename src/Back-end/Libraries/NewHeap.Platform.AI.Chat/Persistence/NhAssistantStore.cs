using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

internal sealed class NhAssistantStore(NhAssistantDbContextFactory contextFactory) : INhAssistantStore
{
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

    public async Task<(IReadOnlyList<AssistantConversation> Items, int Total)> ListConversationsAsync(
        string ownerActorId,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var query = context.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.OwnerActorId == ownerActorId
                && conversation.Status != NhAssistantConversationStatuses.Archived);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .ThenBy(conversation => conversation.Id)
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage)
            .ToListAsync(cancellationToken);
        return (items, total);
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
                    .SetProperty(conversation => conversation.UpdatedAt, now)
                    .SetProperty(conversation => conversation.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryBeginTurnAsync(
        Guid conversationId,
        string ownerActorId,
        IReadOnlyCollection<string> expectedStatuses,
        Guid turnId,
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedStatuses);
        var statuses = expectedStatuses.ToArray();
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var affected = await context.Conversations
            .Where(conversation => conversation.Id == conversationId
                && conversation.OwnerActorId == ownerActorId
                && (statuses.Contains(conversation.Status)
                    || (conversation.Status == NhAssistantConversationStatuses.Running
                        && conversation.UpdatedAt < staleBefore)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(conversation => conversation.Status, NhAssistantConversationStatuses.Running)
                    .SetProperty(conversation => conversation.ActiveTurnId, turnId)
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
        Guid? activeTurnId = status == NhAssistantConversationStatuses.WaitingForApproval
            ? turnId
            : null;
        var affected = await context.Conversations
            .Where(conversation => conversation.Id == conversationId
                && conversation.ActiveTurnId == turnId
                && conversation.Status == NhAssistantConversationStatuses.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(conversation => conversation.Status, status)
                    .SetProperty(conversation => conversation.ActiveTurnId, activeTurnId)
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

    public async Task AddApprovalAsync(
        AssistantApproval approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await using var context = contextFactory.CreateDbContext();
        context.Approvals.Add(approval);
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
                && approval.Status == NhAssistantApprovalStatuses.Pending)
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
}
