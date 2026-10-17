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
    /// The thread holds the agent's shadow work about the subject: administrators alone see it,
    /// nobody is notified and nothing proposed in it executes. An agent has at most one live and
    /// one shadow thread per subject.
    /// </summary>
    public bool Shadow { get; set; }

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

    /// <summary>
    /// The skill of the agent the run works in; <see langword="null"/> for the agent's own tools.
    /// </summary>
    public string? SkillId { get; set; }

    /// <summary>
    /// Who chose the skill: one of the values in <see cref="NhAssistantSkillChoosers"/>.
    /// </summary>
    public string? SkillChosenBy { get; set; }

    /// <summary>
    /// The agent that handed the run's work off; <see langword="null"/> for a run no agent asked for.
    /// </summary>
    public string? RequestedByAgentId { get; set; }

    /// <summary>
    /// The run of <see cref="RequestedByAgentId"/> that handed the work off.
    /// </summary>
    public Guid? RequestedByRunId { get; set; }

    /// <summary>
    /// How many hand-offs led to the run: 0 for a run no agent asked for.
    /// </summary>
    public int HandOffDepth { get; set; }

    /// <summary>
    /// Whether the run read results with its agent's own grant that people without the tool's
    /// policy may not see. Such a run's summary never reaches another agent.
    /// </summary>
    public bool UsedRestrictedResults { get; set; }

    public int AgentVersion { get; set; }

    /// <summary>
    /// The turn the run currently executes or waits in.
    /// </summary>
    public Guid? TurnId { get; set; }

    public int ToolCalls { get; set; }

    /// <summary>
    /// The model calls of the run's turns, including triage.
    /// </summary>
    public int ModelCalls { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public int ProposalCount { get; set; }

    /// <summary>
    /// The run of another agent whose recorded inputs this run replays; <see langword="null"/> for a
    /// run that is no replay.
    /// </summary>
    public Guid? ReplayOfRunId { get; set; }

    /// <summary>
    /// A bounded summary of what the run did, proposed and left open.
    /// </summary>
    public string? Summary { get; set; }

    public bool Shadow { get; set; }

    /// <summary>
    /// How the run worked: one of the values in <see cref="NhAssistantRunKinds"/>.
    /// </summary>
    public string Kind { get; set; } = NhAssistantRunKinds.Model;

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
/// A replay of the recorded inputs of one agent's runs in a period into a shadow variant: what was
/// asked, how far it got and why it stopped.
/// </summary>
public sealed class AssistantAgentReplay
{
    public Guid Id { get; set; }

    public string VariantAgentId { get; set; } = string.Empty;

    public string SourceAgentId { get; set; } = string.Empty;

    public DateTimeOffset From { get; set; }

    public DateTimeOffset To { get; set; }

    public int MaxRuns { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantReplayStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantReplayStatuses.Running;

    /// <summary>
    /// Why the replay stopped before it replayed every run, such as <c>assistant-budget-exhausted</c>.
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>
    /// The source runs the replay fired into the variant.
    /// </summary>
    public int RunsReplayed { get; set; }

    /// <summary>
    /// The source runs the replay passed over: without inputs or with a subject that no longer exists.
    /// </summary>
    public int RunsSkipped { get; set; }

    /// <summary>
    /// The creation time and id of the last source run the replay handled, oldest first.
    /// </summary>
    public DateTimeOffset? CursorCreatedAt { get; set; }

    public Guid? CursorRunId { get; set; }

    public string RequestedByActorId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public Guid ConcurrencyStamp { get; set; }
}

/// <summary>
/// The content-free record of one hand-off from a run of one agent to another agent: who handed
/// which subject to whom, how it was received and which run took it. The message stays in the
/// target's thread.
/// </summary>
public sealed class AssistantHandOff
{
    public Guid Id { get; set; }

    public string ScopeKey { get; set; } = string.Empty;

    public string SourceAgentId { get; set; } = string.Empty;

    public Guid SourceRunId { get; set; }

    public Guid SourceConversationId { get; set; }

    /// <summary>
    /// The skill the source run worked in; its follow-up returns there.
    /// </summary>
    public string? SourceSkillId { get; set; }

    public string TargetAgentId { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// The hand-off's place in its chain: 1 for a hand-off from a run no agent asked for.
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// The input the hand-off wrote into the target's thread; the run that claims it takes the work.
    /// </summary>
    public Guid InputId { get; set; }

    /// <summary>
    /// The target's thread, once the hand-off reached it.
    /// </summary>
    public Guid? TargetConversationId { get; set; }

    /// <summary>
    /// The target's run that took the work.
    /// </summary>
    public Guid? TargetRunId { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantHandOffOutcomes"/>, or <c>pending</c> while it fires.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    public string? Code { get; set; }

    public bool NotifySource { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the source received its follow-up.
    /// </summary>
    public DateTimeOffset? NotifiedAt { get; set; }
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

    /// <summary>
    /// The small typed payload a trigger stored with the event for rule handlers, as JSON.
    /// </summary>
    public string? PayloadJson { get; set; }

    /// <summary>
    /// The trigger's dedupe key, so an event that waits while its agent is paused waits once.
    /// </summary>
    public string? EventKey { get; set; }

    /// <summary>
    /// The person who caused the event, kept so that the run that starts for a waiting input when
    /// its agent resumes has the same initiator as a run started at once.
    /// </summary>
    public string? InitiatorActorId { get; set; }

    /// <summary>
    /// The skill the trigger named; only a run of that skill, or a resume of it, claims the input.
    /// </summary>
    public string? SkillId { get; set; }

    public string? ClientMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? ConsumedByRunId { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}

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

/// <summary>
/// One person's feedback on a closed agent proposal: why it was wrong, or right at the wrong
/// moment. It carries the agent version and tool contract the proposal came from, so operators
/// can compare rejection patterns across versions. Deleting the proposal deletes its feedback.
/// </summary>
public sealed class AssistantProposalFeedback
{
    public Guid Id { get; set; }

    public Guid ApprovalId { get; set; }

    /// <summary>
    /// One of the values in <see cref="NhAssistantFeedbackTargetKinds"/>.
    /// </summary>
    public string TargetKind { get; set; } = NhAssistantFeedbackTargetKinds.AgentProposal;

    public Guid ConversationId { get; set; }

    public string ScopeKey { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// The agent row version of the run that proposed it, or <see langword="null"/> for a proposal
    /// of a consultation.
    /// </summary>
    public int? AgentVersion { get; set; }

    public string ToolId { get; set; } = string.Empty;

    public string ToolContractHash { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// The proposal's status when the feedback was given, such as <c>rejected</c> or <c>expired</c>.
    /// </summary>
    public string ApprovalStatus { get; set; } = string.Empty;

    /// <summary>
    /// One of the values in <see cref="NhAssistantProposalFeedbackCategories"/>.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    public string? Text { get; set; }

    public string AuthorActorId { get; set; } = string.Empty;

    /// <summary>
    /// The feedback was given once for several proposals that were rejected together.
    /// </summary>
    public bool Bulk { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One person with a role on a subject, as the subject access resolver last listed it. Listings,
/// notifications and live updates use the snapshot; authorization always asks the resolver.
/// </summary>
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
