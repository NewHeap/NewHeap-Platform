using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Conversation persistence used by the turn runner and the endpoints. Every call uses its own
/// short-lived context; status transitions are compare-and-swap updates.
/// </summary>
internal interface INhAssistantStore
{
    Task AddConversationAsync(
        AssistantConversation conversation,
        CancellationToken cancellationToken);

    Task<AssistantConversation?> FindConversationAsync(
        Guid conversationId,
        string ownerActorId,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<AssistantConversation> Items, int Total)> ListConversationsAsync(
        string ownerActorId,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken);

    /// <summary>
    /// Archives an idle, failed or waiting conversation. Returns false when it is running or unknown.
    /// </summary>
    Task<bool> TryArchiveConversationAsync(
        Guid conversationId,
        string ownerActorId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically moves a conversation from one of <paramref name="expectedStatuses"/> to
    /// <c>running</c> for <paramref name="turnId"/>. A running conversation whose last update is
    /// older than <paramref name="staleBefore"/> is treated as abandoned and may be taken over.
    /// </summary>
    Task<bool> TryBeginTurnAsync(
        Guid conversationId,
        string ownerActorId,
        IReadOnlyCollection<string> expectedStatuses,
        Guid turnId,
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ends the turn that owns the conversation. Returns false when another turn took ownership.
    /// </summary>
    Task<bool> TryEndTurnAsync(
        Guid conversationId,
        Guid turnId,
        string status,
        CancellationToken cancellationToken);

    Task SetTitleIfEmptyAsync(
        Guid conversationId,
        string title,
        CancellationToken cancellationToken);

    Task<bool> ClientMessageExistsAsync(
        Guid conversationId,
        string clientMessageId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds a message with the next per-conversation sequence number.
    /// </summary>
    Task AddMessageAsync(
        AssistantMessage message,
        CancellationToken cancellationToken);

    Task UpdateMessageAsync(
        Guid messageId,
        string partsJson,
        int inputTokens,
        int outputTokens,
        int toolCalls,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssistantMessage>> GetMessagesAsync(
        Guid conversationId,
        int maximum,
        CancellationToken cancellationToken);

    Task AddToolInvocationAsync(
        AssistantToolInvocation invocation,
        CancellationToken cancellationToken);

    Task UpdateToolInvocationAsync(
        AssistantToolInvocation invocation,
        CancellationToken cancellationToken);

    Task<AssistantToolInvocation?> FindToolInvocationAsync(
        Guid invocationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssistantToolInvocation>> GetToolInvocationsAsync(
        Guid conversationId,
        IReadOnlyCollection<Guid> invocationIds,
        CancellationToken cancellationToken);

    Task AddApprovalAsync(
        AssistantApproval approval,
        CancellationToken cancellationToken);

    Task<AssistantApproval?> FindApprovalAsync(
        Guid approvalId,
        CancellationToken cancellationToken);

    Task<AssistantApproval?> FindPendingApprovalAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssistantApproval>> GetApprovalsAsync(
        Guid conversationId,
        IReadOnlyCollection<Guid> approvalIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically moves a pending approval to a decided status. Returns false when it was already decided.
    /// </summary>
    Task<bool> TryDecideApprovalAsync(
        Guid approvalId,
        string status,
        string? decidedByActorId,
        DateTimeOffset decidedAt,
        DateTimeOffset? approvalExpiresAt,
        string? reason,
        CancellationToken cancellationToken);
}
