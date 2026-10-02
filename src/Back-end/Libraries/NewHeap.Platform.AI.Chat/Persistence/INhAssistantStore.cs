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

    /// <summary>
    /// Returns a conversation only when <paramref name="ownerActorId"/> owns it.
    /// </summary>
    Task<AssistantConversation?> FindConversationAsync(
        Guid conversationId,
        string ownerActorId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the caller's access to a conversation that is not archived: as its owner, or as a
    /// participant of the same tenant. Returns <see langword="null"/> otherwise.
    /// </summary>
    Task<NhAssistantConversationAccess?> FindAccessAsync(
        Guid conversationId,
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a conversation that is not archived without any access check, for validating an
    /// invitation link. Never return it to a caller before the link was verified.
    /// </summary>
    Task<AssistantConversation?> FindShareableConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the conversations the actor owns or participates in (same tenant), most recently
    /// updated first, with the actor's read state.
    /// </summary>
    Task<(IReadOnlyList<NhAssistantConversationListItem> Items, int Total)> ListConversationsAsync(
        string actorId,
        string? tenantId,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the state other people see change: status, title, active actor, latest message and the
    /// number of participants. Returns <see langword="null"/> for an unknown conversation.
    /// </summary>
    Task<NhAssistantConversationState?> GetConversationStateAsync(
        Guid conversationId,
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
    /// <c>running</c> for <paramref name="turnId"/> started by <paramref name="actorId"/>. A running
    /// conversation whose last update is older than <paramref name="staleBefore"/> is treated as
    /// abandoned and may be taken over. The caller has already checked the actor's access.
    /// </summary>
    Task<bool> TryBeginTurnAsync(
        Guid conversationId,
        string actorId,
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

    /// <summary>
    /// The first user message of the conversation, which may lie outside the recent history window.
    /// </summary>
    Task<AssistantMessage?> GetFirstUserMessageAsync(
        Guid conversationId,
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

    /// <summary>
    /// Moves a conversation that waits for approval back to idle. Returns false when it is not waiting.
    /// The caller has already checked the actor's access.
    /// </summary>
    Task<bool> TryReleaseWaitingConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    Task<int> GetToolCallsAsync(
        string actorId,
        DateOnly day,
        CancellationToken cancellationToken);

    /// <summary>
    /// Raises the read position of the owner or a participant to <paramref name="sequence"/>, capped at
    /// the latest message. Never lowers it. Returns the new position, or <see langword="null"/> when
    /// nothing changed.
    /// </summary>
    Task<int?> MarkReadAsync(
        NhAssistantConversationAccess access,
        int sequence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores the display name of the owner or participant when it changed.
    /// </summary>
    Task UpdateDisplayNameAsync(
        NhAssistantConversationAccess access,
        string displayName,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssistantConversationParticipant>> GetParticipantsAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds a participant unless the actor already participates or the conversation reached
    /// <paramref name="maximumParticipants"/>.
    /// </summary>
    Task<NhAssistantParticipantAddResult> TryAddParticipantAsync(
        AssistantConversationParticipant participant,
        int maximumParticipants,
        CancellationToken cancellationToken);

    Task<bool> TryRemoveParticipantAsync(
        Guid conversationId,
        string actorId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces or clears the protected invitation-link token of a conversation the owner owns.
    /// </summary>
    Task<bool> SetProtectedShareTokenAsync(
        Guid conversationId,
        string ownerActorId,
        string? protectedToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// The owner and participant actor ids of a conversation that is not archived; empty when unknown.
    /// </summary>
    Task<IReadOnlyList<string>> GetAudienceAsync(
        Guid conversationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The caller's access to one conversation.
/// </summary>
internal sealed record NhAssistantConversationAccess(
    AssistantConversation Conversation,
    string ActorId,
    AssistantConversationParticipant? Participant)
{
    public bool IsOwner => Participant is null;

    public string Role => IsOwner ? NhAssistantParticipantRoles.Owner : NhAssistantParticipantRoles.Participant;

    /// <summary>
    /// The read position; <see langword="null"/> for an owner whose read state was never tracked.
    /// </summary>
    public int? LastReadSequence => IsOwner ? Conversation.OwnerLastReadSequence : Participant!.LastReadSequence;

    /// <summary>
    /// The owner or participant who started the active turn. Turns from before conversations could be
    /// shared belong to the owner.
    /// </summary>
    public string? ActiveActorId => Conversation.ActiveTurnId is null
        ? null
        : Conversation.ActiveActorId ?? Conversation.OwnerActorId;
}

/// <summary>
/// One conversation in the caller's list with the caller's read state.
/// </summary>
internal sealed record NhAssistantConversationListItem(
    AssistantConversation Conversation,
    string Role,
    int ParticipantCount,
    int LastMessageSequence,
    int LastReadSequence);

/// <summary>
/// The shared, caller-independent state of a conversation that live updates announce.
/// </summary>
internal sealed record NhAssistantConversationState(
    Guid ConversationId,
    string Status,
    string? Title,
    DateTimeOffset UpdatedAt,
    string? ActiveActorId,
    int LastMessageSequence,
    int ParticipantCount);

internal enum NhAssistantParticipantAddResult
{
    Added = 0,
    AlreadyParticipant = 1,
    LimitReached = 2
}
