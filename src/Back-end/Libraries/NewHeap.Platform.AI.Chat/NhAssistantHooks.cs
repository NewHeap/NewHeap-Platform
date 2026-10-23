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
    /// An agent run ended. <c>ResultCode</c> is the run status, followed by the failure code as
    /// <c>status:code</c>; <see cref="NhAssistantAuditEvent.RunStatus"/> and
    /// <see cref="NhAssistantAuditEvent.RunFailureCode"/> carry them apart. The run facts
    /// <see cref="NhAssistantAuditEvent.TriggerKind"/>, <see cref="NhAssistantAuditEvent.Shadow"/>,
    /// <see cref="NhAssistantAuditEvent.SkillVersion"/> and
    /// <see cref="NhAssistantAuditEvent.ModelCallCount"/> let an audit sink count runs, runs without a
    /// model call and shadow runs per agent and skill without reading the assistant's tables.
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
    /// An administrator or the application resumed a paused or stopped agent, or switched on one
    /// that was off. <c>ResultCode</c> is <c>resumed-{count}</c>, the runs it started for waiting
    /// inputs.
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
    AdminAgentServiceAccountChanged = 49,

    /// <summary>
    /// An administrator or the application saved or removed a skill defined as data.
    /// <c>ObjectId</c> and <c>SkillId</c> are the skill, <c>AgentVersion</c> its version and
    /// <c>InstructionsHash</c> the hash of its instructions; <c>ResultCode</c> is <c>created</c>,
    /// <c>changed</c> or <c>removed</c>.
    /// </summary>
    AdminAgentSkillDefinitionChanged = 50,

    /// <summary>
    /// An administrator or the application saved, removed or put in shadow or live a trigger defined
    /// as data. <c>ObjectId</c> and <c>TriggerId</c> are the trigger, <c>SkillId</c> the skill it
    /// starts and <c>AgentVersion</c> its version; <c>ResultCode</c> is <c>created</c>,
    /// <c>changed</c>, <c>removed</c>, <c>shadow</c> or <c>live</c>.
    /// </summary>
    AdminAgentTriggerDefinitionChanged = 51,

    /// <summary>
    /// An administrator or the application saved or removed the run settings of an agent created in
    /// the administration. <c>ObjectId</c> is the agent and <c>AgentVersion</c> the settings'
    /// version; <c>ResultCode</c> is <c>created</c>, <c>changed</c> or <c>removed</c>.
    /// </summary>
    AdminAgentRunSettingsChanged = 52,

    /// <summary>
    /// Headless requests to a <c>forward-user-token</c> MCP server received delegated tokens for the
    /// person the run acts for, or were refused them; one event per turn, server, person and outcome.
    /// <see cref="NhAssistantAuditEvent.ObjectId"/> is the server, <see cref="NhAssistantAuditEvent.ActorId"/>
    /// the person, <see cref="NhAssistantAuditEvent.ResultCode"/> <c>issued</c> or the refusal code and
    /// <see cref="NhAssistantAuditEvent.TokenCount"/> the number of tokens.
    /// </summary>
    McpDelegatedTokenIssued = 53,

    /// <summary>
    /// A person gave or withdrew consent to background use of an MCP server.
    /// <see cref="NhAssistantAuditEvent.ResultCode"/> is <c>allowed</c> or <c>withdrawn</c>.
    /// </summary>
    McpBackgroundUseChanged = 54,

    /// <summary>
    /// A personal or organization OAuth connection became active. <see cref="NhAssistantAuditEvent.ObjectId"/>
    /// is the server, <see cref="NhAssistantAuditEvent.ActorId"/> the person who connected it and
    /// <see cref="NhAssistantAuditEvent.ResultCode"/> <c>organization</c> or <c>user</c>.
    /// </summary>
    McpConnectionConnected = 55,

    /// <summary>
    /// An OAuth connection was removed; <see cref="NhAssistantAuditEvent.ResultCode"/> is <c>person</c>,
    /// <c>administrator</c>, <c>server-changed</c> or <c>retention</c>.
    /// </summary>
    McpConnectionDisconnected = 56,

    /// <summary>
    /// Renewing an OAuth connection failed permanently; its owner has to connect again.
    /// </summary>
    McpConnectionReauthorizationRequired = 57,

    /// <summary>
    /// An administrator revoked all personal connections of a server.
    /// </summary>
    AdminMcpConnectionsRevoked = 58,

    /// <summary>
    /// An administrator confirmed a server's authorization server and resource after discovery.
    /// </summary>
    AdminMcpServerAuthorizationConfirmed = 59,

    /// <summary>
    /// A run placed a proposal for another object in the agent's thread at that object.
    /// <see cref="NhAssistantAuditEvent.ConversationId"/> is the thread at the object,
    /// <see cref="NhAssistantAuditEvent.SubjectType"/> and <see cref="NhAssistantAuditEvent.SubjectId"/>
    /// name the object and <see cref="NhAssistantAuditEvent.ObjectId"/> is the run's own thread.
    /// </summary>
    AgentProposalPlaced = 60,

    /// <summary>
    /// A run's proposal for another object was not placed there.
    /// <see cref="NhAssistantAuditEvent.ResultCode"/> is the reason, one of
    /// <see cref="NhAssistantPlacementReasons"/>, and <see cref="NhAssistantAuditEvent.ObjectId"/> the
    /// object the call named as <c>type/id</c>, when it named a valid one.
    /// </summary>
    AgentProposalNotPlaced = 61,

    /// <summary>
    /// An administrator or the application switched an agent off, or one skill with
    /// <c>SkillId</c>: its triggers are ignored until it is switched on. <c>ResultCode</c> is
    /// <c>off</c>; switching it on again is <see cref="AdminAgentRunsResumed"/>.
    /// </summary>
    AdminAgentSwitchedOff = 62,

    /// <summary>
    /// A turn gave a file to the model. <see cref="NhAssistantAuditEvent.FileId"/>,
    /// <see cref="NhAssistantAuditEvent.FileName"/>, <see cref="NhAssistantAuditEvent.FileMediaType"/>
    /// and <see cref="NhAssistantAuditEvent.FileHash"/> name it,
    /// <see cref="NhAssistantAuditEvent.ObjectId"/> is how it reached the turn (one of
    /// <see cref="NhAssistantFileOrigins"/>) and, for a tool result, <c>ToolId</c> and
    /// <c>InvocationId</c> name the call. For a file a reading tool turned into text,
    /// <see cref="NhAssistantAuditEvent.FileReaderId"/> names the reader and
    /// <see cref="NhAssistantAuditEvent.FileTextCached"/> says whether the text came from the
    /// conversation's cache. Never its content.
    /// </summary>
    FileRead = 63,

    /// <summary>
    /// A turn did not give a file to the model. <see cref="NhAssistantAuditEvent.ResultCode"/> is the
    /// refusal, such as <see cref="NhAssistantErrorCodes.FileForbidden"/>; the file fields and
    /// <see cref="NhAssistantAuditEvent.ObjectId"/> are as for <see cref="FileRead"/>, without a hash
    /// when the file was not read.
    /// </summary>
    FileRefused = 64,

    /// <summary>
    /// An administrator or the application switched the routes of an agent's consultations within
    /// the ceiling in code. <see cref="NhAssistantAuditEvent.ResultCode"/> lists each route with
    /// <c>on</c>, <c>off</c> or <c>code</c>, such as
    /// <c>asker-decides=on;subject-agent-rights=off;subject-asker-rights=code</c>.
    /// </summary>
    AdminAgentConsultationRoutesChanged = 65,

    /// <summary>
    /// An administrator or the application added, changed or removed a consultation defined as data.
    /// <see cref="NhAssistantAuditEvent.SubjectType"/> and <see cref="NhAssistantAuditEvent.ObjectId"/>
    /// name its subject type, <see cref="NhAssistantAuditEvent.AgentVersion"/> and
    /// <see cref="NhAssistantAuditEvent.ConsultationVersion"/> its version,
    /// <see cref="NhAssistantAuditEvent.InstructionsHash"/> the hash of its instructions and
    /// <see cref="NhAssistantAuditEvent.ResultCode"/> is <c>created</c>, <c>changed</c> or
    /// <c>removed</c>. Never its description or instructions.
    /// </summary>
    AdminAgentConsultationDefinitionChanged = 66,

    /// <summary>
    /// The application posted a message into a conversation with
    /// <see cref="INhAssistantConversationMessages.PostAsync"/>. <see cref="NhAssistantAuditEvent.ActorId"/>
    /// is <c>application</c>, <see cref="NhAssistantAuditEvent.ObjectId"/> the message and
    /// <see cref="NhAssistantAuditEvent.ResultCode"/> <c>posted</c>, or <c>notified</c> when the
    /// conversation's people were notified too (by Web Push, in the application's inbox or both);
    /// <see cref="NhAssistantAuditEvent.ApplicationMessageTrusted"/> says whether the model follows it
    /// as the application's instruction; for an agent thread
    /// <see cref="NhAssistantAuditEvent.SubjectType"/> and <see cref="NhAssistantAuditEvent.SubjectId"/>
    /// name its subject. The text, title, link and sender are never recorded. A repeated post with the
    /// same idempotency key records nothing.
    /// </summary>
    ApplicationMessagePosted = 70
}

/// <summary>
/// A content-free assistant audit event. It carries identifiers, versions, codes and timestamps,
/// and for a file its bounded, normalized name (<see cref="FileName"/>): never prompts, messages,
/// tool arguments, tool results or file content.
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
    /// What started the run of an <see cref="NhAssistantAuditEventKind.AgentRunEnded"/> event, one of
    /// <see cref="NhAssistantTriggerKinds"/>; <see langword="null"/> for other events and for a run
    /// whose trigger is no longer registered.
    /// </summary>
    public string? TriggerKind { get; init; }

    /// <summary>
    /// The version of the skill defined as data that the run of an
    /// <see cref="NhAssistantAuditEventKind.AgentRunEnded"/> event worked in; <see langword="null"/>
    /// for a skill in code, a run without a skill and other events.
    /// </summary>
    public int? SkillVersion { get; init; }

    /// <summary>
    /// Whether the run of an <see cref="NhAssistantAuditEventKind.AgentRunEnded"/> event worked in the
    /// agent's shadow thread, where nothing it proposes executes; <see langword="null"/> for other
    /// events.
    /// </summary>
    public bool? Shadow { get; init; }

    /// <summary>
    /// The governed model calls the run of an <see cref="NhAssistantAuditEventKind.AgentRunEnded"/>
    /// event made across its turns and attempts, triage included: 0 for a run by rules alone and for
    /// a run that ended before its first call. <see langword="null"/> for other events.
    /// </summary>
    public int? ModelCallCount { get; init; }

    /// <summary>
    /// The status the run of an <see cref="NhAssistantAuditEventKind.AgentRunEnded"/> event ended
    /// with, one of <see cref="NhAssistantRunStatuses"/>, without its failure code;
    /// <see langword="null"/> for other events.
    /// </summary>
    public string? RunStatus { get; init; }

    /// <summary>
    /// The failure code the run of an <see cref="NhAssistantAuditEventKind.AgentRunEnded"/> event
    /// ended with, such as <c>assistant-approval-expired</c>; <see langword="null"/> when it ended
    /// without one and for other events.
    /// </summary>
    public string? RunFailureCode { get; init; }

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
    /// How a consultation proposal is decided and whose rights execute it, one of
    /// <see cref="NhAssistantConsultationRoutes"/>, on the request, decision, expiry and execution
    /// events of a proposal a consultation left; <see langword="null"/> for other events.
    /// </summary>
    public string? ConsultationRoute { get; init; }

    /// <summary>
    /// Why a call of a consulted agent that changes something was not proposed, one of
    /// <see cref="NhAssistantConsultationRefusalReasons"/>, on its
    /// <see cref="NhAssistantAuditEventKind.ToolInvoked"/> event with
    /// <c>assistant-consult-proposal-refused</c>.
    /// </summary>
    public string? ConsultationRefusalReason { get; init; }

    /// <summary>
    /// Where the consultation of an <see cref="NhAssistantAuditEventKind.AgentConsulted"/> or
    /// <see cref="NhAssistantAuditEventKind.AdminAgentConsultationDefinitionChanged"/> event comes from,
    /// one of <see cref="NhAssistantConsultationSources"/>; <see langword="null"/> for other events.
    /// </summary>
    public string? ConsultationSource { get; init; }

    /// <summary>
    /// The version of the consultation defined as data the event concerns; <see langword="null"/>
    /// for a consultation in code and other events.
    /// </summary>
    public int? ConsultationVersion { get; init; }

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

    /// <summary>
    /// The number of delegated MCP tokens a <see cref="NhAssistantAuditEventKind.McpDelegatedTokenIssued"/>
    /// event covers: those issued, or refused, for one server and person in a turn.
    /// </summary>
    public int? TokenCount { get; init; }

    /// <summary>
    /// Whose credential a <see cref="NhAssistantAuditEventKind.ToolInvoked"/> call of an MCP tool
    /// carried: <c>organization</c> for the server's own credential, the person's actor id for a
    /// personal connection or a forwarded sign-in, or <c>none</c>. Null for other tools.
    /// </summary>
    public string? McpConnectionOwner { get; init; }

    /// <summary>
    /// Whether the message of an <see cref="NhAssistantAuditEventKind.ApplicationMessagePosted"/> event
    /// is trusted (<see cref="NhAssistantApplicationMessage.Trusted"/>): <see langword="true"/> when the
    /// model follows it as the application's instruction, <see langword="false"/> when the model reads
    /// it as information. Null for other events.
    /// </summary>
    public bool? ApplicationMessageTrusted { get; init; }

    /// <summary>
    /// The most characters of <see cref="FileName"/>.
    /// </summary>
    public const int MaxFileNameLength = 256;

    /// <summary>
    /// The application's id of the file a <see cref="NhAssistantAuditEventKind.FileRead"/> or
    /// <see cref="NhAssistantAuditEventKind.FileRefused"/> event concerns: an identifier.
    /// </summary>
    public string? FileId { get; init; }

    /// <summary>
    /// The name of the file a <see cref="NhAssistantAuditEventKind.FileRead"/> or
    /// <see cref="NhAssistantAuditEventKind.FileRefused"/> event concerns, as the application reports it
    /// in the file's reference: at most <see cref="MaxFileNameLength"/> characters, without control,
    /// format or separator characters, with white space as single spaces and in Unicode normalization
    /// form C. It is the only free text of the event, so an audit reader recognises a file without asking
    /// the application; never the content.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// The file's media type as a code, lowercase without parameters, such as <c>application/pdf</c>.
    /// </summary>
    public string? FileMediaType { get; init; }

    /// <summary>
    /// The SHA-256 of the content the model received, <c>sha256:&lt;hex&gt;</c>; never the content. For a
    /// file a reading tool turned into text, the SHA-256 of the file's content that the reader received.
    /// </summary>
    public string? FileHash { get; init; }

    /// <summary>
    /// The reading tool that turned the file into text, or that refused or failed it: the id of a
    /// reader of the application (<c>AddFileReader</c>), or <c>mcp.&lt;server&gt;</c> for an MCP server
    /// set up as reader, whose credential <see cref="McpConnectionOwner"/> names. Null when the model
    /// read the file itself.
    /// </summary>
    public string? FileReaderId { get; init; }

    /// <summary>
    /// For a <see cref="NhAssistantAuditEventKind.FileRead"/> of a file a reading tool turned into text:
    /// <see langword="true"/> when the text came from the conversation's cache of reader texts, so the
    /// reader of <see cref="FileReaderId"/> received nothing in this turn; <see langword="false"/> when
    /// the reader made it now. Null when the model read the file itself and for a refusal.
    /// </summary>
    public bool? FileTextCached { get; init; }
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
