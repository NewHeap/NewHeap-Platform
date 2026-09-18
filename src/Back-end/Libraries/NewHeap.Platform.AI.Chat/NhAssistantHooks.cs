namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The kind of content-free assistant event delivered to <see cref="INhAssistantBusinessAuditSink"/>.
/// </summary>
public enum NhAssistantAuditEventKind
{
    ToolInvoked = 0,
    ApprovalRequested = 1,
    ApprovalApproved = 2,
    ApprovalRejected = 3,
    ApprovalExpired = 4
}

/// <summary>
/// A content-free assistant audit event. It carries identifiers, versions, codes and timestamps
/// only: never prompts, messages, tool arguments or tool results.
/// </summary>
public sealed record NhAssistantAuditEvent(
    NhAssistantAuditEventKind Kind,
    Guid ConversationId,
    Guid TurnId,
    string ActorId,
    string AgentId,
    int AgentVersion,
    DateTimeOffset OccurredAt)
{
    public string? ToolId { get; init; }

    public int? ToolVersion { get; init; }

    public Guid? InvocationId { get; init; }

    /// <summary>
    /// Safe outcome code, such as <c>succeeded</c> or a stable failure code.
    /// </summary>
    public string? ResultCode { get; init; }

    public Guid? ApprovalId { get; init; }

    public Guid? ProposalId { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// Consumer hook for application audit logs. Implementations receive content-free events
/// for governed tool calls and approval decisions made through the assistant.
/// </summary>
public interface INhAssistantBusinessAuditSink
{
    ValueTask RecordAsync(NhAssistantAuditEvent evt, CancellationToken ct);
}

/// <summary>
/// Optional hook that names a conversation after its first user message.
/// </summary>
public interface INhAssistantTitleGenerator
{
    /// <summary>
    /// Returns a title of at most 200 characters, or <see langword="null"/> to leave the title empty.
    /// </summary>
    ValueTask<string?> GenerateAsync(
        string agentId,
        string firstMessage,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAssistantFirstLineTitleGenerator : INhAssistantTitleGenerator
{
    private const int MaxTitleLength = 80;

    public ValueTask<string?> GenerateAsync(
        string agentId,
        string firstMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var firstLine = firstMessage
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return ValueTask.FromResult<string?>(null);
        }

        var title = firstLine.Length <= MaxTitleLength
            ? firstLine
            : firstLine[..(MaxTitleLength - 1)].TrimEnd() + "…";
        return ValueTask.FromResult<string?>(title);
    }
}
