namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// The subject side of a thread conversation: one agent working on one domain object. A thread is
/// unique per scope key, agent and subject; every column of that key is non-null so the unique
/// index behaves the same on every provider.
/// </summary>
public sealed class AssistantThread
{
    public Guid ConversationId { get; set; }

    public string ScopeKey { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    public string? SubjectDisplayName { get; set; }

    public Guid? DivisionId { get; set; }

    public string? TenantId { get; set; }

    /// <summary>
    /// The last subject version the platform saw.
    /// </summary>
    public string? SubjectVersion { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantAccountabilities"/>.
    /// </summary>
    public string Accountability { get; set; } = NhAssistantAccountabilities.Agent;

    public string AccountableOwnerId { get; set; } = string.Empty;

    /// <summary>
    /// The structured working memory the agent keeps about the subject.
    /// </summary>
    public string? MemoryJson { get; set; }

    public int MemorySchemaVersion { get; set; }

    public Guid MemoryConcurrencyStamp { get; set; }

    public DateTimeOffset? MembersRefreshedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One headless execution of an agent in a thread, started by a trigger. The background operation
/// that executes it carries retries, cancellation and checkpoints; this row carries the outcome
/// people see.
/// </summary>
public sealed class AssistantRun
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid? BackgroundOperationId { get; set; }

    public string TriggerId { get; set; } = string.Empty;

    public string? TriggerEventKey { get; set; }

    public string? InitiatorActorId { get; set; }

    public string AccountableOwnerId { get; set; } = string.Empty;

    public int Attempt { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantRunStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantRunStatuses.Queued;

    /// <summary>
    /// One of the values in <see cref="NhAssistantRunFailureClasses"/>.
    /// </summary>
    public string? FailureClass { get; set; }

    public string? FailureCode { get; set; }

    public int AgentVersion { get; set; }

    /// <summary>
    /// The turn the run currently executes or waits in.
    /// </summary>
    public Guid? TurnId { get; set; }

    public int ToolCalls { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public int ProposalCount { get; set; }

    /// <summary>
    /// A bounded summary of what the run did, proposed and left open.
    /// </summary>
    public string? Summary { get; set; }

    public bool Shadow { get; set; }

    public string? CancelledByActorId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public Guid ConcurrencyStamp { get; set; }
}

/// <summary>
/// The content-free record of one consultation of an agent about its subject: who asked through
/// which agent, how it ended and what it cost. The question and the answer stay in the asking
/// conversation; proposals the consultation left carry its id.
/// </summary>
public sealed class AssistantConsultation
{
    public Guid Id { get; set; }

    /// <summary>
    /// The consulted agent's thread about the subject.
    /// </summary>
    public Guid ConversationId { get; set; }

    public string ScopeKey { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// The person whose rights the consultation used.
    /// </summary>
    public string AskerActorId { get; set; } = string.Empty;

    /// <summary>
    /// The agent whose turn consulted, or <see langword="null"/> for a direct call, such as from an
    /// MCP client.
    /// </summary>
    public string? AskingAgentId { get; set; }

    public Guid? AskingConversationId { get; set; }

    public Guid? AskingTurnId { get; set; }

    public int Depth { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantConsultationStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantConsultationStatuses.Running;

    public string? FailureCode { get; set; }

    public int ToolCalls { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public int ProposalCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>
/// A message or event that waits for the next turn of a thread, because it arrived while a run
/// was active or because a trigger coalesced it into a pending run.
/// </summary>
public sealed class AssistantQueuedInput
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantQueuedInputKinds"/>.
    /// </summary>
    public string Kind { get; set; } = NhAssistantQueuedInputKinds.Event;

    public string? AuthorActorId { get; set; }

    public string? TriggerId { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? ClientMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? ConsumedByRunId { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>
/// One person with a role on a subject, as the subject access resolver last listed it. Listings,
/// notifications and live updates use the snapshot; authorization always asks the resolver.
/// </summary>
/// <summary>
/// One version of a file a tool produced in an agent thread.
/// </summary>
public sealed class AssistantArtifact
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>
    /// The tool call that saved it; its grant source decides who may read it.
    /// </summary>
    public Guid? ToolInvocationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Version { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AssistantSubjectMember
{
    public string ScopeKey { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public NhAiSubjectRole Role { get; set; }

    public string? NamedRolesJson { get; set; }

    public string? DisplayName { get; set; }

    public DateTimeOffset SnapshotAt { get; set; }
}
