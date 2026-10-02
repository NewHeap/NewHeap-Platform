namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Internal event stream of one turn. The ASP.NET package translates these events to the
/// server-sent events of the assistant contract.
/// </summary>
internal abstract record NhAssistantTurnEvent;

internal sealed record NhAssistantTurnStartedEvent(
    Guid TurnId,
    Guid UserMessageId,
    Guid AssistantMessageId) : NhAssistantTurnEvent;

internal sealed record NhAssistantMessageDeltaEvent(
    Guid MessageId,
    string Text) : NhAssistantTurnEvent;

internal sealed record NhAssistantToolStartedEvent(
    Guid InvocationId,
    string ToolId,
    int ToolVersion,
    string DisplayName,
    string? ArgumentsPreview) : NhAssistantTurnEvent;

internal sealed record NhAssistantToolCompletedEvent(
    Guid InvocationId,
    string Status,
    string? ResultCode,
    string? ResultPreview) : NhAssistantTurnEvent;

internal sealed record NhAssistantApprovalRequiredEvent(
    NhAssistantApprovalView Approval) : NhAssistantTurnEvent;

internal sealed record NhAssistantTurnUsage(
    int InputTokens,
    int OutputTokens,
    int ToolCalls);

internal sealed record NhAssistantTurnCompletedEvent(
    Guid TurnId,
    string Status,
    NhAssistantTurnUsage Usage,
    string? ErrorCode) : NhAssistantTurnEvent;

internal sealed record NhAssistantErrorEvent(
    string Code) : NhAssistantTurnEvent
{
    public string MessageKey => NhAssistantErrorCodes.MessageKey(Code);
}

/// <summary>
/// Read model of one conversation with hydrated message parts, seen by one caller.
/// </summary>
internal sealed record NhAssistantConversationView(
    Guid Id,
    string AgentId,
    int AgentVersion,
    string? Title,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<NhAssistantMessageView> Messages,
    NhAssistantApprovalView? PendingApproval)
{
    /// <summary>
    /// The caller's relation: <c>owner</c> or <c>participant</c>.
    /// </summary>
    public string Role { get; init; } = NhAssistantParticipantRoles.Owner;

    /// <summary>
    /// The caller's read position; the latest sequence when read state was never tracked.
    /// </summary>
    public int LastReadSequence { get; init; }

    public int LastMessageSequence { get; init; }

    /// <summary>
    /// The owner or participant whose turn is active, or <see langword="null"/>.
    /// </summary>
    public string? ActiveActorId { get; init; }

    /// <summary>
    /// The owner followed by the participants. Empty for an unshared conversation of its owner.
    /// </summary>
    public IReadOnlyList<NhAssistantMemberView> Members { get; init; } = [];

    /// <summary>
    /// The protected invitation-link token; only set for the owner.
    /// </summary>
    public string? ProtectedShareToken { get; init; }
}

/// <summary>
/// The owner or a participant of a shared conversation.
/// </summary>
internal sealed record NhAssistantMemberView(
    string ActorId,
    string DisplayName,
    string Role,
    DateTimeOffset JoinedAt);

internal sealed record NhAssistantMessageView(
    Guid Id,
    string Role,
    DateTimeOffset CreatedAt,
    IReadOnlyList<NhAssistantPartView> Parts)
{
    public int Sequence { get; init; }

    /// <summary>
    /// The writer of a user message; the owner for user messages from before sharing.
    /// </summary>
    public string? AuthorActorId { get; init; }
}

internal abstract record NhAssistantPartView;

internal sealed record NhAssistantTextPartView(string Text) : NhAssistantPartView;

internal sealed record NhAssistantToolCallPartView(
    Guid InvocationId,
    string ToolId,
    int ToolVersion,
    string DisplayName,
    string Status,
    string? ArgumentsPreview,
    string? ResultPreview,
    string? ResultCode) : NhAssistantPartView;

internal sealed record NhAssistantApprovalView(
    Guid ApprovalId,
    Guid ProposalId,
    string ProposalHash,
    string ToolId,
    string Summary,
    NhAssistantApprovalPresentation? Presentation,
    string ArgumentsPreview,
    IReadOnlyList<string> Targets,
    DateTimeOffset ExpiresAt,
    string Status) : NhAssistantPartView;
