namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// One assistant conversation owned by exactly one authenticated actor.
/// </summary>
public sealed class AssistantConversation
{
    public Guid Id { get; set; }

    public string OwnerActorId { get; set; } = string.Empty;

    public string? TenantId { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public int AgentVersion { get; set; }

    public string? Title { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantConversationStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantConversationStatuses.Idle;

    /// <summary>
    /// The turn that currently owns the conversation while it is running or waiting for approval.
    /// </summary>
    public Guid? ActiveTurnId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Provider-neutral optimistic concurrency token that changes on every status transition.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; }

    public List<AssistantMessage> Messages { get; set; } = [];
}

/// <summary>
/// One persisted conversation message. Parts are stored as versioned JSON; tool-call and
/// approval parts reference their authoritative rows.
/// </summary>
public sealed class AssistantMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid? TurnId { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantMessageRoles"/>.
    /// </summary>
    public string Role { get; set; } = NhAssistantMessageRoles.User;

    public int Sequence { get; set; }

    public int PartsVersion { get; set; } = 1;

    public string PartsJson { get; set; } = "[]";

    public string? ClientMessageId { get; set; }

    /// <summary>
    /// The bounded page context the client sent with a user message, used again when the turn resumes
    /// after an approval. Never logged or audited.
    /// </summary>
    public string? ClientContextJson { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int ToolCalls { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public AssistantConversation? Conversation { get; set; }
}

/// <summary>
/// One governed tool call made during an assistant turn.
/// </summary>
public sealed class AssistantToolInvocation
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid TurnId { get; set; }

    public Guid MessageId { get; set; }

    public string CallId { get; set; } = string.Empty;

    public string FunctionName { get; set; } = string.Empty;

    public string ToolId { get; set; } = string.Empty;

    public int ToolVersion { get; set; }

    public string ContractHash { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// One of the values in <see cref="NhAssistantToolCallStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantToolCallStatuses.Running;

    public string? ResultCode { get; set; }

    /// <summary>
    /// Model-supplied arguments, bounded to <see cref="NhAssistantLimits.MaxStoredJsonCharacters"/>.
    /// </summary>
    public string? ArgumentsJson { get; set; }

    /// <summary>
    /// Serialized tool result, bounded to <see cref="NhAssistantLimits.MaxStoredJsonCharacters"/>.
    /// </summary>
    public string? ResultJson { get; set; }

    public NhAiDataClassification DataClassification { get; set; } = NhAiDataClassification.Internal;

    public NhAiRetentionCategory RetentionCategory { get; set; } = NhAiRetentionCategory.ConversationContent;

    public Guid? ProposalId { get; set; }

    public Guid? ApprovalId { get; set; }

    public string? IdempotencyKey { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// A persisted NewHeap proposal and the human decision about it.
/// </summary>
public sealed class AssistantApproval
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid TurnId { get; set; }

    public Guid ToolInvocationId { get; set; }

    public Guid ProposalId { get; set; }

    public string ProposalHash { get; set; } = string.Empty;

    public string ProposalJson { get; set; } = "{}";

    public string ToolId { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string? PresentationJson { get; set; }

    public string ArgumentsPreview { get; set; } = string.Empty;

    public string TargetsJson { get; set; } = "[]";

    /// <summary>
    /// One of the values in <see cref="NhAssistantApprovalStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantApprovalStatuses.Pending;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string? DecidedByActorId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset? ApprovalExpiresAt { get; set; }

    public string? Reason { get; set; }

    public Guid ConcurrencyStamp { get; set; }
}

/// <summary>
/// Durable per-actor, per-UTC-day budget ledger.
/// </summary>
public sealed class AssistantBudgetLedger
{
    public string ActorId { get; set; } = string.Empty;

    public DateOnly Day { get; set; }

    public int ToolCalls { get; set; }

    public int ModelCalls { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal EstimatedCost { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Durable idempotency lease with fencing and expiry.
/// </summary>
public sealed class AssistantIdempotencyLease
{
    public Guid Id { get; set; }

    /// <summary>
    /// SHA-256 over actor, tool, tool version and idempotency key; unique.
    /// </summary>
    public string KeyHash { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public string ToolId { get; set; } = string.Empty;

    public int ToolVersion { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string ArgumentHash { get; set; } = string.Empty;

    public string? FencingToken { get; set; }

    public string LeaseId { get; set; } = string.Empty;

    public int Generation { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantLeaseStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantLeaseStatuses.InProgress;

    public string? Outcome { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
