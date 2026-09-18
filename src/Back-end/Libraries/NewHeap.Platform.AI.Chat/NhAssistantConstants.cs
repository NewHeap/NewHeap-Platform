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

/// <summary>
/// Agent source values: from code (<c>AddAgent</c>) or created by an administrator.
/// </summary>
public static class NhAssistantAgentSources
{
    public const string Code = "code";
    public const string Admin = "admin";
}

public static class NhAssistantApplicationContexts
{
    public const string DefaultId = "default";
    public const int MaxTextLength = 20_000;
}

public static class NhAssistantMcpAuthModes
{
    public const string None = "none";
    public const string Bearer = "bearer";
    public const string ApiKey = "api-key";
    public const string ForwardUserToken = "forward-user-token";

    public static bool IsValid(string? value)
    {
        return value is None or Bearer or ApiKey or ForwardUserToken;
    }
}

public static class NhAssistantMcpToolEffects
{
    public const string ReadOnly = "read-only";
    public const string Mutation = "mutation";
}

public static class NhAssistantMcpToolStatuses
{
    public const string Available = "available";
    public const string SchemaChanged = "schema-changed";
    public const string Missing = "missing";
}

public static class NhAssistantStyles
{
    public const string Default = "default";
    public const string Direct = "direct";
    public const string Personal = "personal";
    public const string Detailed = "detailed";

    public static bool IsValid(string? value)
    {
        return value is Default or Direct or Personal or Detailed;
    }
}

public static class NhAssistantAddressForms
{
    public const string Informal = "informal";
    public const string Formal = "formal";

    public static bool IsValid(string? value)
    {
        return value is Informal or Formal;
    }
}

public static class NhAssistantResponseLengths
{
    public const string Short = "short";
    public const string Normal = "normal";
    public const string Long = "long";

    public static bool IsValid(string? value)
    {
        return value is Short or Normal or Long;
    }
}

/// <summary>
/// Stable, content-free failure codes of the administration and MCP surface.
/// </summary>
public static class NhAssistantAdminErrorCodes
{
    public const string VersionConflict = "assistant-version-conflict";
    public const string ValidationFailed = "assistant-validation-failed";
    public const string AgentExists = "assistant-agent-exists";
    public const string CodeAgentNotDeletable = "assistant-code-agent-not-deletable";
    public const string NotCodeAgent = "assistant-agent-not-code";
    public const string McpServerNotFound = "assistant-mcp-server-not-found";
    public const string McpServerExists = "assistant-mcp-server-exists";
    public const string McpToolNotFound = "assistant-mcp-tool-not-found";
    public const string McpUnreachable = "assistant-mcp-unreachable";
    public const string McpUnauthorized = "assistant-mcp-unauthorized";
    public const string McpHostBlocked = "assistant-mcp-host-blocked";
    public const string McpSchemaChanged = "assistant-mcp-schema-changed";
    public const string McpToolDisabled = "assistant-mcp-tool-disabled";
    public const string InstructionsTooLong = "assistant-instructions-too-long";
    public const string ContextNotFound = "assistant-context-not-found";
}
