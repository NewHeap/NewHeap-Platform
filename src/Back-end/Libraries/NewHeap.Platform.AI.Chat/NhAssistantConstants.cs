namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Conversation status values exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantConversationStatuses
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string WaitingForApproval = "waiting-for-approval";
    public const string Failed = "failed";
    public const string Archived = "archived";
}

/// <summary>
/// Message role values exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantMessageRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>
/// Tool-call status values exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantToolCallStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string AwaitingApproval = "awaiting-approval";
    public const string Rejected = "rejected";
}

/// <summary>
/// Approval status values exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantApprovalStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
}

/// <summary>
/// Turn completion status values used by the <c>turn.completed</c> event.
/// </summary>
public static class NhAssistantTurnStatuses
{
    public const string Completed = "completed";
    public const string WaitingForApproval = "waiting-for-approval";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

/// <summary>
/// Idempotency lease status values.
/// </summary>
public static class NhAssistantLeaseStatuses
{
    public const string InProgress = "in-progress";
    public const string Completed = "completed";
}

/// <summary>
/// Stable, content-free assistant failure codes. Front-ends localize them through
/// <c>nh-assistant.errors.&lt;code&gt;</c>.
/// </summary>
public static class NhAssistantErrorCodes
{
    public const string Disabled = "assistant-disabled";
    public const string ConversationNotFound = "assistant-conversation-not-found";
    public const string ConversationBusy = "assistant-conversation-busy";
    public const string AgentNotFound = "assistant-agent-not-found";
    public const string AgentForbidden = "assistant-agent-forbidden";
    public const string MessageInvalid = "assistant-message-invalid";
    public const string MessageTooLong = "assistant-message-too-long";
    public const string MessageDuplicate = "assistant-message-duplicate";
    public const string TitleInvalid = "assistant-title-invalid";
    public const string ApprovalNotFound = "assistant-approval-not-found";
    public const string ApprovalNotPending = "assistant-approval-not-pending";
    public const string ApprovalDecisionInvalid = "assistant-approval-decision-invalid";
    public const string ProposalHashMismatch = "assistant-proposal-hash-mismatch";
    public const string ApprovalExpired = "assistant-approval-expired";
    public const string ApprovalInvalid = "assistant-approval-invalid";
    public const string BudgetExhausted = "assistant-budget-exhausted";
    public const string ToolCallLimitReached = "assistant-tool-call-limit-reached";
    public const string TurnTimeout = "assistant-turn-timeout";
    public const string ModelUnavailable = "assistant-model-unavailable";
    public const string TurnFailed = "assistant-turn-failed";
    public const string ContextUnavailable = "assistant-context-unavailable";

    /// <summary>
    /// Creates the localization key for a code.
    /// </summary>
    public static string MessageKey(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return "nh-assistant.errors." + code;
    }
}
