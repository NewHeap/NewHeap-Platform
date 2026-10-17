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
    ApprovalExpired = 4,
    AdminContextUpdated = 10,
    AdminAgentCreated = 11,
    AdminAgentUpdated = 12,
    AdminAgentDeleted = 13,
    AdminAgentReset = 14,
    AdminMcpServerCreated = 15,
    AdminMcpServerUpdated = 16,
    AdminMcpServerDeleted = 17,
    AdminMcpServerSynced = 18,
    AdminMcpToolUpdated = 19,

    /// <summary>
    /// The owner created or replaced the invitation link of a conversation.
    /// </summary>
    ConversationShareLinkCreated = 20,

    /// <summary>
    /// The owner revoked the invitation link of a conversation.
    /// </summary>
    ConversationShareLinkRevoked = 21,

    /// <summary>
    /// A person joined a conversation through its invitation link. <see cref="NhAssistantAuditEvent.ObjectId"/> is that person.
    /// </summary>
    ConversationParticipantJoined = 22,

    /// <summary>
    /// The owner invited a person directly. <see cref="NhAssistantAuditEvent.ObjectId"/> is that person.
    /// </summary>
    ConversationParticipantInvited = 23,

    /// <summary>
    /// The owner removed a participant. <see cref="NhAssistantAuditEvent.ObjectId"/> is that person.
    /// </summary>
    ConversationParticipantRemoved = 24,

    /// <summary>
    /// A participant left a conversation.
    /// </summary>
    ConversationParticipantLeft = 25,

    /// <summary>
    /// A trigger started, queued or joined an agent run. <c>ResultCode</c> is the trigger outcome.
    /// </summary>
    AgentTriggerAccepted = 30,

    /// <summary>
    /// A trigger was rejected, ignored or deduplicated. <c>ResultCode</c> is the reason.
    /// </summary>
    AgentTriggerRejected = 31,

    /// <summary>
    /// An agent run ended. <c>ResultCode</c> is the run status, followed by the failure code.
    /// </summary>
    AgentRunEnded = 32,

    /// <summary>
    /// An approved agent proposal finished executing. <c>ResultCode</c> is the proposal status.
    /// </summary>
    AgentProposalExecuted = 33,

    /// <summary>
    /// A person resolved an execution with an unknown outcome.
    /// </summary>
    AgentProposalResolved = 34,

    /// <summary>
    /// An administrator or the application stopped an agent (the kill switch): its active runs
    /// were cancelled. <c>ResultCode</c> is <c>cancelled-{count}</c>.
    /// </summary>
    AdminAgentRunsStopped = 35,

    /// <summary>
    /// An administrator or the application resumed a paused or stopped agent.
    /// <c>ResultCode</c> is <c>resumed-{count}</c>, the runs it started for waiting inputs.
    /// </summary>
    AdminAgentRunsResumed = 36,

    /// <summary>
    /// An assistant consulted an agent. <c>ResultCode</c> is <c>completed</c> or the refusal or
    /// failure code; <see cref="NhAssistantAuditEvent.ConsultationId"/> names the record when the
    /// consultation started.
    /// </summary>
    AgentConsulted = 37,

    /// <summary>
    /// An assistant asked an agent to work on a subject. <c>ResultCode</c> is the trigger outcome or
    /// the refusal code.
    /// </summary>
    AgentRequested = 38,

    /// <summary>
    /// A person said what was wrong with a closed agent proposal. <c>ResultCode</c> is the feedback
    /// category; the text never reaches the audit sink.
    /// </summary>
    AgentProposalFeedbackGiven = 39,

    /// <summary>
    /// An administrator exported the proposal feedback of an agent. <c>ObjectId</c> is the agent.
    /// </summary>
    AdminAgentFeedbackExported = 40,

    /// <summary>
    /// An open or approved proposal closed as stale because an application guard reported that it
    /// no longer fits. <c>ResultCode</c> is the kind and the guard's reason code as
    /// <c>kind:reason</c>, and <c>ObjectId</c> the stage (revalidation, decision or execution); no
    /// content is recorded.
    /// </summary>
    AgentProposalInvalidated = 41,

    /// <summary>
    /// An agent did not record a proposal because the same one is open or was rejected recently.
    /// <c>ApprovalId</c> is the existing proposal and <c>ResultCode</c> the outcome
    /// (<c>assistant-proposal-already-open</c> or <c>assistant-proposal-recently-rejected</c>).
    /// </summary>
    AgentProposalDeduplicated = 42,

    /// <summary>
    /// A newer proposal for the same objects replaced a pending one, which is withdrawn.
    /// <c>ApprovalId</c> is the replaced proposal and <c>ObjectId</c> the newer one.
    /// </summary>
    AgentProposalSuperseded = 43,

    /// <summary>
    /// An administrator or the application paused an agent: it starts no runs and its events wait.
    /// <c>ResultCode</c> is <c>paused</c>.
    /// </summary>
    AdminAgentPaused = 44,

    /// <summary>
    /// An agent handed work on a subject to another agent. <c>AgentId</c> is the target,
    /// <c>AskingAgentId</c> the source and <see cref="NhAssistantAuditEvent.SourceRunId"/> its run;
    /// <c>RunId</c> is the target's run once there is one. <c>ResultCode</c> is the outcome or the
    /// refusal code. The message is never recorded.
    /// </summary>
    AgentHandedOff = 45,

    /// <summary>
    /// An administrator exported the evaluation of a shadow variant against its live agent.
    /// <c>ObjectId</c> is the variant; <c>AgentId</c> the live agent.
    /// </summary>
    AdminAgentEvaluationExported = 46,

    /// <summary>
    /// An administrator started a replay of recorded runs into a shadow variant. <c>ObjectId</c> is
    /// the replay, <c>AgentId</c> the variant and <c>AskingAgentId</c> the agent whose runs it replays.
    /// </summary>
    AdminAgentReplayStarted = 47,

    /// <summary>
    /// An administrator or the application changed the shadow mode of an agent, or of one skill
    /// with <c>SkillId</c>. <c>ResultCode</c> is <c>shadow</c>, <c>live</c> or <c>code</c> when the
    /// code decides again.
    /// </summary>
    AdminAgentShadowChanged = 48,

    /// <summary>
    /// An administrator or the application changed the technical account that answers for an
    /// agent's runs. <c>ObjectId</c> is the account that answers now; <c>ResultCode</c> is
    /// <c>set</c>, or <c>code</c> when the account in code answers again.
    /// </summary>
    AdminAgentServiceAccountChanged = 49
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

    /// <summary>
    /// Identifier of the administered object (agent, MCP server, tool or context) for administration events.
    /// </summary>
    public string? ObjectId { get; init; }

    /// <summary>
    /// Identity of the application context used by the turn: <c>default@&lt;version&gt;</c> and its hash.
    /// </summary>
    public string? ApplicationContextVersion { get; init; }

    public string? ApplicationContextHash { get; init; }

    /// <summary>
    /// Identity of the agent instructions: <c>&lt;asset-id&gt;@&lt;version&gt;</c> and their hash.
    /// </summary>
    public string? InstructionsVersion { get; init; }

    public string? InstructionsHash { get; init; }

    /// <summary>
    /// Hash of the user preferences applied to the turn, never their text.
    /// </summary>
    public string? PreferencesHash { get; init; }

    /// <summary>
    /// Number of situational facts from turn context providers, never their text.
    /// </summary>
    public int? ContextFactCount { get; init; }

    /// <summary>
    /// Number of entities in the client's page context, never their ids or labels.
    /// </summary>
    public int? PageEntityCount { get; init; }

    /// <summary>
    /// Whether the client sent a page context with the user message.
    /// </summary>
    public bool? HadPageContext { get; init; }

    /// <summary>
    /// Hash of the application map the turn read, never its text.
    /// </summary>
    public string? ApplicationMapHash { get; init; }

    /// <summary>
    /// Context hash of the retrieved context the turn read, never its items.
    /// </summary>
    public string? RetrievedContextHash { get; init; }

    /// <summary>
    /// The agent run the event belongs to, for events of an agent thread.
    /// </summary>
    public Guid? RunId { get; init; }

    public string? TriggerId { get; init; }

    public string? SubjectType { get; init; }

    public string? SubjectId { get; init; }

    /// <summary>
    /// The person who caused the run or turn, when a person did.
    /// </summary>
    public string? InitiatorActorId { get; init; }

    /// <summary>
    /// Whether an agent grant or the initiator's own rights allowed the tool.
    /// </summary>
    public NhAiGrantSource GrantSource { get; init; }

    public string? ApprovalPolicyId { get; init; }

    /// <summary>
    /// The skill of the agent a trigger, run or state change concerns.
    /// </summary>
    public string? SkillId { get; init; }

    /// <summary>
    /// The consultation the event belongs to: the consultation itself, or a tool call or proposal of
    /// the consulted agent.
    /// </summary>
    public Guid? ConsultationId { get; init; }

    /// <summary>
    /// How deep the consultation is nested; 1 for an assistant that consults an agent directly.
    /// </summary>
    public int? ConsultationDepth { get; init; }

    /// <summary>
    /// The agent whose turn consulted or requested another agent, when an agent turn did, or that
    /// handed work off.
    /// </summary>
    public string? AskingAgentId { get; init; }

    /// <summary>
    /// The run of the agent that handed work off.
    /// </summary>
    public Guid? SourceRunId { get; init; }

    /// <summary>
    /// Usage of a consultation: tool calls of the consulted agent, model tokens and the proposals it
    /// left in its thread.
    /// </summary>
    public int? ToolCallCount { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public int? ProposalCount { get; init; }
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
/// One person's feedback on a closed agent proposal, as <see cref="INhAssistantProposalFeedbackSink"/>
/// receives it. Unlike audit events it carries the person's text, because the sink exists to
/// analyse it; it never carries the tool input.
/// </summary>
/// <param name="AgentVersion">The agent row version of the run that proposed it, or <see langword="null"/> for a consultation.</param>
/// <param name="ApprovalStatus">The proposal's status when the feedback was given, such as <c>rejected</c>.</param>
/// <param name="Category">One of <see cref="NhAssistantProposalFeedbackCategories"/>.</param>
/// <param name="Group">One of <see cref="NhAssistantProposalFeedbackGroups"/>.</param>
/// <param name="Bulk">The feedback was given once for several proposals rejected together.</param>
public sealed record NhAssistantProposalFeedback(
    Guid Id,
    Guid ApprovalId,
    Guid ProposalId,
    Guid? RunId,
    string ScopeKey,
    string AgentId,
    int? AgentVersion,
    string ToolId,
    string ToolContractHash,
    NhAiSubjectRef Subject,
    string ApprovalStatus,
    string Category,
    string Group,
    string? Text,
    string AuthorActorId,
    bool Bulk,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Consumer hook that receives every proposal feedback after it was stored, for example to forward
/// it to the application's own analytics. Register it with
/// <c>NhAssistantBuilder.AddProposalFeedbackSink</c>. A failing sink is logged and never undoes the
/// feedback.
/// </summary>
public interface INhAssistantProposalFeedbackSink
{
    ValueTask RecordAsync(NhAssistantProposalFeedback feedback, CancellationToken cancellationToken);
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
