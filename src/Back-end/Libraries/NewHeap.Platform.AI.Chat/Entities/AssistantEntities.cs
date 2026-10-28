namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// One assistant conversation owned by exactly one authenticated actor. The owner may share it with
/// participants of the same tenant; see <see cref="AssistantConversationParticipant"/>.
/// </summary>
public sealed class AssistantConversation
{
    public Guid Id { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantConversationKinds"/>. A thread belongs to an agent
    /// working on a subject; see <see cref="AssistantThread"/>.
    /// </summary>
    public string Kind { get; set; } = NhAssistantConversationKinds.Chat;

    public string OwnerActorId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the owner shown to participants, refreshed whenever the owner uses the conversation.
    /// </summary>
    public string? OwnerDisplayName { get; set; }

    /// <summary>
    /// Highest message sequence the owner has read. <see langword="null"/> means read state was never
    /// tracked, which counts as read, so conversations from before read tracking show no unread marker.
    /// </summary>
    public int? OwnerLastReadSequence { get; set; }

    public string? TenantId { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public int AgentVersion { get; set; }

    public string? Title { get; set; }

    /// <summary>
    /// The type of the subject a person's chat is about (FB25), with <see cref="SubjectId"/>;
    /// <see langword="null"/> for a chat without a subject and for an agent's thread, whose subject is
    /// on <see cref="AssistantThread"/>. The subject's resolver decides on every turn, read and invitation
    /// whether a person may use the chat; it never becomes the agent's thread about the subject.
    /// </summary>
    public string? SubjectType { get; set; }

    /// <summary>
    /// The id of the subject of <see cref="SubjectType"/>.
    /// </summary>
    public string? SubjectId { get; set; }

    /// <summary>
    /// The subject's name as its resolver described it when the chat was created.
    /// </summary>
    public string? SubjectDisplayName { get; set; }

    /// <summary>
    /// The scope key of the subject as its resolver described it when the chat was created, so subject
    /// retention forgets the chats about the subject in its own scope only.
    /// </summary>
    public string? SubjectScopeKey { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantConversationStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantConversationStatuses.Idle;

    /// <summary>
    /// The turn that currently owns the conversation while it is running or waiting for approval.
    /// </summary>
    public Guid? ActiveTurnId { get; set; }

    /// <summary>
    /// The owner or participant who started the active turn. Only this actor decides its approval.
    /// </summary>
    public string? ActiveActorId { get; set; }

    /// <summary>
    /// The invitation-link token, protected with ASP.NET Data Protection, or <see langword="null"/>
    /// while the owner has no active invitation link.
    /// </summary>
    public string? ProtectedShareToken { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Provider-neutral optimistic concurrency token that changes on every status transition.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; }

    public List<AssistantMessage> Messages { get; set; } = [];

    public List<AssistantConversationParticipant> Participants { get; set; } = [];
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

    /// <summary>
    /// The owner or participant who wrote a user message. <see langword="null"/> for assistant messages
    /// and for user messages written before conversations could be shared, which belong to the owner.
    /// </summary>
    public string? AuthorActorId { get; set; }

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

    /// <summary>
    /// Whether an agent grant, the initiator's own rights or a delegated read allowed the call.
    /// </summary>
    public NhAiGrantSource GrantSource { get; set; }

    /// <summary>
    /// For a delegated read, the person whose rights the call read with; only they see its result.
    /// </summary>
    public string? ReadForActorId { get; set; }

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

    /// <summary>
    /// The presentation in the culture the proposal was made in; the fallback for every viewer.
    /// </summary>
    public string? PresentationJson { get; set; }

    /// <summary>
    /// The presentation in every supported UI culture, keyed by culture name, so each viewer reads
    /// the proposal in their own language. <see langword="null"/> when only one culture is supported.
    /// </summary>
    public string? PresentationsJson { get; set; }

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

    /// <summary>
    /// The run that created the proposal; the proposals of one run form its proposal set.
    /// </summary>
    public Guid? RunId { get; set; }

    /// <summary>
    /// The thread of the run that placed this proposal here, at another object than its own;
    /// <see langword="null"/> for a proposal made in its own thread. A placed proposal waits for the
    /// approvers of this thread's subject and never for the run's initiator.
    /// </summary>
    public Guid? PlacedFromConversationId { get; set; }

    public string? PolicyId { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantApprovalModes"/>.
    /// </summary>
    public string Mode { get; set; } = NhAssistantApprovalModes.Inline;

    /// <summary>
    /// The approval policy as it applied when the proposal was created, so later policy changes
    /// never alter a proposal that is waiting.
    /// </summary>
    public string? PolicyJson { get; set; }

    /// <summary>
    /// The person who caused the turn that created the proposal, for four-eyes and initiator rules.
    /// </summary>
    public string? InitiatorActorId { get; set; }

    public string? SubjectVersion { get; set; }

    /// <summary>
    /// The type of the subject the proposal's subject belonged to when the proposal was made, such as
    /// the project of a task, as the subject access resolver described it; <see langword="null"/>
    /// without a parent. The inbox groups by it without asking a resolver per proposal.
    /// </summary>
    public string? ParentSubjectType { get; set; }

    /// <summary>
    /// The id of that parent; set together with <see cref="ParentSubjectType"/>.
    /// </summary>
    public string? ParentSubjectId { get; set; }

    /// <summary>
    /// The parent's name when the proposal was made.
    /// </summary>
    public string? ParentDisplayName { get; set; }

    public NhAiGrantSource GrantSource { get; set; }

    /// <summary>
    /// The consultation that proposed it. Such a proposal executes with the initiator's rights only,
    /// never with the agent's grants.
    /// </summary>
    public Guid? ConsultationId { get; set; }

    public Guid? ExecutionOperationId { get; set; }

    public int ExecutionAttempts { get; set; }

    public DateTimeOffset? ExecutedAt { get; set; }

    public string? ExecutionResultCode { get; set; }

    /// <summary>
    /// Until when the application's report of its execution is due, on the application's clock: set
    /// each time the proposal of a tool the application executes is handed to it, and kept afterwards,
    /// so it also says that the application executes the proposal. <see langword="null"/> for a
    /// proposal that was never handed to the application.
    /// </summary>
    public DateTimeOffset? ExecutionReportDueAt { get; set; }

    public int RemindersSent { get; set; }

    /// <summary>
    /// When the next reminder of a waiting deferred proposal is due, or <see langword="null"/>.
    /// </summary>
    public DateTimeOffset? NextReminderAt { get; set; }

    public DateTimeOffset? EscalatedAt { get; set; }

    /// <summary>
    /// Why a stale proposal no longer fits: one of the values in <see cref="NhAssistantStaleKinds"/>.
    /// </summary>
    public string? StaleKind { get; set; }

    /// <summary>
    /// The explanation of a stale proposal per UI culture, as a JSON object from culture name to text.
    /// </summary>
    public string? StaleExplanationsJson { get; set; }

    /// <summary>
    /// Who made the proposal stale, when its guard named them.
    /// </summary>
    public string? StaleByActorId { get; set; }

    /// <summary>
    /// The identity of the proposal: a hash of its tool id and canonical arguments, so the same
    /// proposal is not recorded twice.
    /// </summary>
    public string? ArgumentsHash { get; set; }

    /// <summary>
    /// The newer proposal that replaced this withdrawn or <c>shadow-superseded</c> one.
    /// </summary>
    public Guid? SupersededByApprovalId { get; set; }

    /// <summary>
    /// Why a rule proposed it, per UI culture, as a JSON object from culture name to text.
    /// </summary>
    public string? RationalesJson { get; set; }

    /// <summary>
    /// The UI culture the person decided the proposal in, such as <c>nl-NL</c>, at most
    /// <see cref="MaxDecisionCultureLength"/> characters, when the application supports it as a UI culture:
    /// the culture of the person's turn for an inline approval handed to the application
    /// (<see cref="NhAssistantApplicationExecutionBuilder.AlsoInline"/>), and for a deferred decision the
    /// request culture of the decision endpoints or the deciding person's culture of the decision service
    /// (FB22). A hand-off to the application carries it (<see cref="NhAssistantProposalHandOff.DecisionCulture"/>).
    /// What a person's chat reads about the close of the hand-off (the report, an uncertain outcome and a
    /// passed deadline) is written in it when the application still supports it, otherwise in the default
    /// request culture. <see langword="null"/> for approvals decided before it was recorded; an agent
    /// thread always writes in the default culture.
    /// </summary>
    public string? DecisionCulture { get; set; }

    public Guid ConcurrencyStamp { get; set; }

    /// <summary>
    /// The longest culture name <see cref="DecisionCulture"/> keeps, the length a language tag is
    /// recommended to fit in; a longer name is not recorded.
    /// </summary>
    public const int MaxDecisionCultureLength = 35;
}

/// <summary>
/// An application object an agent proposal changes, indexed so the application can revalidate open
/// proposals when that object changes.
/// </summary>
public sealed class AssistantApprovalKey
{
    public Guid ApprovalId { get; set; }

    /// <summary>The isolation key of the thread, so equal ids of different tenants never meet.</summary>
    public string ScopeKey { get; set; } = string.Empty;

    public string KeyType { get; set; } = string.Empty;

    public string KeyId { get; set; } = string.Empty;
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
