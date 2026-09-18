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
/// Read model of one conversation with hydrated message parts.
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
    NhAssistantApprovalView? PendingApproval);

internal sealed record NhAssistantMessageView(
    Guid Id,
    string Role,
    DateTimeOffset CreatedAt,
    IReadOnlyList<NhAssistantPartView> Parts);

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
    string ArgumentsPreview,
    IReadOnlyList<string> Targets,
    DateTimeOffset ExpiresAt,
    string Status) : NhAssistantPartView;
