using NewHeap.Platform.AI.Chat.Collaboration;

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

/// <summary>
/// The turn's person has to connect an MCP server, which each person connects to with OAuth, before
/// the agent can use it; the turn continues without its tools.
/// </summary>
internal sealed record NhAssistantConnectionRequiredEvent(
    string ServerId,
    string DisplayName,
    string Reason) : NhAssistantTurnEvent;

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
    /// One of the values in <see cref="NhAssistantConversationKinds"/>.
    /// </summary>
    public string Kind { get; init; } = NhAssistantConversationKinds.Chat;

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

    /// <summary>
    /// The title, link and sender of a message the application posted; <see langword="null"/> for
    /// other messages.
    /// </summary>
    public NhAssistantApplicationMessageView? Application { get; init; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantMessageAuthorKinds"/> for a user message, an
    /// agent message a rule run wrote or a message the application posted, or
    /// <see langword="null"/> for the model's messages.
    /// </summary>
    public string? AuthorKind
    {
        get
        {
            if (string.Equals(Role, NhAssistantMessageRoles.Application, StringComparison.Ordinal))
            {
                return NhAssistantMessageAuthorKinds.Application;
            }
            if (!string.Equals(Role, NhAssistantMessageRoles.User, StringComparison.Ordinal))
            {
                return AuthorActorId?.StartsWith(NhAssistantSpeakers.RuleActorPrefix, StringComparison.Ordinal) == true
                    ? NhAssistantMessageAuthorKinds.Rule
                    : null;
            }

            return AuthorActorId?.StartsWith(NhAssistantSpeakers.TriggerActorPrefix, StringComparison.Ordinal) == true
                ? NhAssistantMessageAuthorKinds.Trigger
                : NhAssistantMessageAuthorKinds.Person;
        }
    }
}

/// <summary>
/// What the application said about a message it posted, besides its text: an optional title, link
/// and name of the sender, and whether the model follows it as the application's instruction
/// (<paramref name="Trusted"/>).
/// </summary>
internal sealed record NhAssistantApplicationMessageView(
    string? Title,
    string? LinkUrl,
    string? LinkLabel,
    string? SenderName,
    bool Trusted)
{
    /// <summary>
    /// The view of the stored application part of a message, or <see langword="null"/> without one.
    /// A part stored without the trusted flag is untrusted.
    /// </summary>
    public static NhAssistantApplicationMessageView? From(IEnumerable<NhAssistantStoredPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var part = parts.FirstOrDefault(item => item.Type == NhAssistantStoredPart.ApplicationType);
        return part is null
            ? null
            : new NhAssistantApplicationMessageView(part.Title, part.LinkUrl, part.LinkLabel, part.Sender, part.Trusted == true);
    }
}

internal abstract record NhAssistantPartView;

internal sealed record NhAssistantTextPartView(string Text) : NhAssistantPartView;

/// <summary>A server the turn's person had to connect before the agent could use it.</summary>
internal sealed record NhAssistantConnectionRequiredPartView(string ServerId, string DisplayName, string Reason) : NhAssistantPartView;

/// <summary>
/// A file a person attached to a message: what people see of it, never its content, the
/// application's id or a hash.
/// </summary>
internal sealed record NhAssistantFilePartView(string Name, string MediaType, long Size) : NhAssistantPartView
{
    /// <summary>
    /// The view of an attachment as its message stores it.
    /// </summary>
    public static NhAssistantFilePartView From(NhAssistantStoredFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new NhAssistantFilePartView(file.Name, NhAiMediaTypes.Normalize(file.MediaType) ?? file.MediaType, file.Size);
    }

    /// <summary>
    /// The view of an attachment as the message names it.
    /// </summary>
    public static NhAssistantFilePartView From(NhAiFileReference file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new NhAssistantFilePartView(file.Name, NhAiMediaTypes.Normalize(file.MediaType) ?? file.MediaType, file.Size);
    }
}

internal sealed record NhAssistantToolCallPartView(
    Guid InvocationId,
    string ToolId,
    int ToolVersion,
    string DisplayName,
    string Status,
    string? ArgumentsPreview,
    string? ResultPreview,
    string? ResultCode) : NhAssistantPartView
{
    /// <summary>
    /// Whose rights the call used: the person's own, delegated from the initiator, or the agent's grant.
    /// </summary>
    public NhAiGrantSource GrantSource { get; init; }

    /// <summary>
    /// The result came from the agent's own grant or a delegated read and the viewer may not see it.
    /// </summary>
    public bool Restricted { get; init; }

    /// <summary>
    /// For a delegated read, the person whose rights the call read with. Never sent to clients.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ReadForActorId { get; init; }
}

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
    string Status) : NhAssistantPartView
{
    /// <summary>
    /// Why a stale proposal no longer fits, one of <see cref="NhAssistantStaleKinds"/>.
    /// </summary>
    public string? StaleKind { get; init; }

    /// <summary>
    /// The explanation of a stale proposal in the viewer's culture.
    /// </summary>
    public string? StaleExplanation { get; init; }

    /// <summary>
    /// Why the agent's rules proposed it, in the viewer's culture.
    /// </summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// The newer proposal for the same objects that replaced this withdrawn one.
    /// </summary>
    public Guid? SupersededByApprovalId { get; init; }

    /// <summary>
    /// The consultation that proposed it, when another agent proposed it while the conversation's
    /// assistant consulted it and the person who asked decides it here; <see langword="null"/> for the
    /// conversation's own proposals.
    /// </summary>
    public NhAssistantApprovalConsultationView? Consultation { get; init; }

    /// <summary>
    /// The person who approved it; <see langword="null"/> while it waits for a decision.
    /// </summary>
    public string? DecidedByActorId { get; init; }

    /// <summary>
    /// The result of its execution, such as the application's code or <c>report-overdue</c>.
    /// </summary>
    public string? ExecutionResultCode { get; init; }

    /// <summary>
    /// Until when the application's report is due, once it was handed to the application.
    /// </summary>
    public DateTimeOffset? ExecutionReportDueAt { get; init; }
}

/// <summary>
/// Where a proposal the person who asked decides in their own conversation comes from.
/// </summary>
/// <param name="AgentId">The consulted agent that proposed it.</param>
/// <param name="LeaveRoute">
/// The route at the subject the person may leave it for instead of deciding it, one of
/// <see cref="NhAssistantConsultationRoutes"/>; <see langword="null"/> when none applies.
/// </param>
internal sealed record NhAssistantApprovalConsultationView(
    Guid ConsultationId,
    string AgentId,
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName,
    string? LeaveRoute)
{
    /// <summary>
    /// The consulted agent's answer, bounded as a preview, when its classification allows a preview.
    /// </summary>
    public string? Answer { get; init; }

    /// <summary>
    /// The proposal that replaced this one at the subject after the person left it for the subject's
    /// approvers.
    /// </summary>
    public Guid? MovedToApprovalId { get; init; }
}
