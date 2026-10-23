namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// One version of a data-defined skill of an agent. The live version is in effect; a candidate runs
/// in shadow next to it until it is promoted or discarded; superseded versions are the history.
/// </summary>
public sealed class AssistantAgentSkillDefinition
{
    public Guid Id { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public string SkillId { get; set; } = string.Empty;

    public int Version { get; set; }

    /// <summary>
    /// One of <see cref="NhAssistantSkillDefinitionStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantSkillDefinitionStatuses.Live;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The instructions appended to the agent's own, as the application stored them.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// The SHA-256 of <see cref="Instructions"/>, lowercase hex.
    /// </summary>
    public string? InstructionsHash { get; set; }

    public string ToolSelectorsJson { get; set; } = "[]";

    public string? ProfileName { get; set; }

    /// <summary>
    /// The classification of the data the skill's runs send to the model, one of
    /// <see cref="NhAssistantDataClassificationNames"/>; <see langword="null"/> uses the agent's.
    /// </summary>
    public string? DataClassification { get; set; }

    /// <summary>
    /// Whether a run in the skill reads files, when its agent accepts them too. A new skill accepts
    /// none until an administrator switches it on.
    /// </summary>
    public bool AcceptsFiles { get; set; }

    /// <summary>
    /// The largest file in bytes a run in the skill reads, at most the application's
    /// <see cref="NhAssistantLimits.MaxFileBytes"/>; <see langword="null"/> keeps the agent's.
    /// </summary>
    public int? MaxFileBytes { get; set; }

    /// <summary>
    /// The most pages of one file; <see langword="null"/> keeps the agent's.
    /// </summary>
    public int? MaxFilePages { get; set; }

    /// <summary>
    /// The most files of one turn; <see langword="null"/> keeps the agent's.
    /// </summary>
    public int? MaxFilesPerTurn { get; set; }

    /// <summary>
    /// The most estimated file tokens of one turn; <see langword="null"/> keeps the agent's.
    /// </summary>
    public int? MaxFileTokensPerTurn { get; set; }

    /// <summary>
    /// The name of a rule handler registered with <c>AddRuleHandler</c>.
    /// </summary>
    public string? RuleHandlerName { get; set; }

    public long? MaxTokensPerRun { get; set; }

    public int? DailyToolCalls { get; set; }

    public long? DailyTokens { get; set; }

    public string? ApprovalPolicyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string? Reason { get; set; }
}

/// <summary>
/// One version of a data-defined trigger: a named trigger event or a schedule that starts an agent, in
/// one of its skills or routed by the agent itself. Versions are append-only, as those of a data skill:
/// the live version is in effect; a candidate fires in shadow next to it until it is promoted or
/// discarded; superseded and discarded versions are the history. One row per trigger id and version.
/// </summary>
public sealed class AssistantAgentTriggerDefinition
{
    public string Id { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// One of <see cref="NhAssistantSkillDefinitionStatuses"/>: the statuses of trigger versions are
    /// those of skill versions. A trigger stored before trigger versions existed is live.
    /// </summary>
    public string Status { get; set; } = NhAssistantSkillDefinitionStatuses.Live;

    public string? SkillId { get; set; }

    /// <summary>
    /// <c>event</c> or <c>schedule</c>.
    /// </summary>
    public string Source { get; set; } = NhAssistantDataTriggerSources.Event;

    /// <summary>
    /// The trigger event registered with <c>AddTriggerEvent</c>, for an event trigger.
    /// </summary>
    public string? EventName { get; set; }

    /// <summary>
    /// The names of the trigger event's filters the trigger applies, as a JSON array in the order
    /// the administration chose them; <see langword="null"/> applies none. Only an event trigger
    /// has filters.
    /// </summary>
    public string? FiltersJson { get; set; }

    public string? Cron { get; set; }

    /// <summary>
    /// The IANA or Windows id of the schedule's time zone.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// The subject set registered with <c>AddSubjectSet</c>, for a schedule.
    /// </summary>
    public string? SubjectSetName { get; set; }

    /// <summary>
    /// The message every run of a schedule receives.
    /// </summary>
    public string? Message { get; set; }

    public string Conflict { get; set; } = string.Empty;

    public int DebounceSeconds { get; set; }

    /// <summary>
    /// The trigger's runs work in the agent's shadow thread. A new trigger starts in shadow. The live
    /// version carries the trigger's mode; a new version takes it over, and a candidate works in shadow
    /// whatever it says.
    /// </summary>
    public bool Shadow { get; set; } = true;

    public int Version { get; set; } = 1;

    /// <summary>
    /// When the version was stored; <see cref="UpdatedAt"/> follows its status and mode.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public Guid ConcurrencyStamp { get; set; }
}

/// <summary>
/// The run settings of an agent created in the administration: who answers for its runs, how its
/// proposals are approved, its budgets, ceiling and retries, gateway reads, triage and rules. Grants
/// are never data: such an agent acts with the rights of the account it answers to.
/// </summary>
public sealed class AssistantAgentRunConfiguration
{
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Incremented on every change.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// One of <see cref="NhAssistantAccountabilities"/>.
    /// </summary>
    public string Accountability { get; set; } = NhAssistantAccountabilities.Agent;

    public string? ServiceAccountActorId { get; set; }

    public bool DelegateFromInitiator { get; set; }

    public bool AllowGatewayReads { get; set; }

    /// <summary>
    /// One of <see cref="NhAssistantElevatedReadNames"/>.
    /// </summary>
    public string ElevatedReads { get; set; } = NhAssistantElevatedReadNames.Restricted;

    public string? ApprovalPolicyId { get; set; }

    public int RetryCount { get; set; } = 3;

    public int? DailyToolCalls { get; set; }

    public long? DailyTokens { get; set; }

    public int MaxConcurrentRuns { get; set; } = 4;

    public int MaxRunsPerSubjectPerDay { get; set; } = 10;

    public long? MaxTokensPerRun { get; set; }

    public string? TriageProfileName { get; set; }

    /// <summary>
    /// The name of a rule handler registered with <c>AddRuleHandler</c>.
    /// </summary>
    public string? RuleHandlerName { get; set; }

    /// <summary>
    /// Whether people's messages and the agent's tools may give it files. An agent accepts none until
    /// an administrator switches it on.
    /// </summary>
    public bool AcceptsFiles { get; set; }

    /// <summary>
    /// The largest file in bytes the agent reads, at most the application's
    /// <see cref="NhAssistantLimits.MaxFileBytes"/>; <see langword="null"/> keeps the application's.
    /// </summary>
    public int? MaxFileBytes { get; set; }

    /// <summary>
    /// The most pages of one file; <see langword="null"/> keeps the application's.
    /// </summary>
    public int? MaxFilePages { get; set; }

    /// <summary>
    /// The most files of one turn; <see langword="null"/> keeps the application's.
    /// </summary>
    public int? MaxFilesPerTurn { get; set; }

    /// <summary>
    /// The most estimated file tokens of one turn; <see langword="null"/> keeps the application's.
    /// </summary>
    public int? MaxFileTokensPerTurn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public Guid ConcurrencyStamp { get; set; }
}

/// <summary>
/// A consultation an administrator defined as data: other assistants may consult the agent about
/// subjects of one type, with this description and instructions, by whom and by which routes, within
/// the ceiling in code. One row per agent and subject type; every change is its next version.
/// </summary>
public sealed class AssistantAgentConsultationDefinition
{
    public string AgentId { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    /// <summary>
    /// Incremented on every change.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// What the asking model reads about the agent, so it consults it at the right moment.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Extra instructions the agent follows when it is consulted, next to its own.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// The SHA-256 of <see cref="Instructions"/>, lowercase hex.
    /// </summary>
    public string? InstructionsHash { get; set; }

    /// <summary>
    /// The role the asking person needs on the subject, one of <see cref="NhAssistantSubjectRoleNames"/>.
    /// </summary>
    public string MinimumRole { get; set; } = NhAssistantSubjectRoleNames.Reader;

    /// <summary>
    /// The authorization policy the asking person must pass, one the application lists with
    /// <c>AddAgentPolicy</c>; <see langword="null"/> uses the agent's required policy, else the
    /// assistant's access policy.
    /// </summary>
    public string? AskerPolicy { get; set; }

    /// <summary>
    /// The routes of the agent's proposals, a JSON array of <see cref="NhAssistantConsultationRoutes"/>;
    /// an empty array keeps the consultation read-only.
    /// </summary>
    public string RoutesJson { get; set; } = "[]";

    /// <summary>
    /// The role the asking person needs before the agent may propose, one of
    /// <see cref="NhAssistantSubjectRoleNames"/>.
    /// </summary>
    public string ProposalRole { get; set; } = NhAssistantSubjectRoleNames.Participant;

    /// <summary>
    /// The approval policy of the consultation's proposals, registered with <c>AddApprovalPolicy</c>;
    /// <see langword="null"/> uses the agent's.
    /// </summary>
    public string? ProposalPolicyId { get; set; }

    /// <summary>
    /// The tools the consulted agent is offered, a JSON array of tool selectors within the agent's own;
    /// <see langword="null"/> offers every tool of the agent.
    /// </summary>
    public string? ToolSelectorsJson { get; set; }

    public int MaxToolCalls { get; set; } = 4;

    public int TimeoutSeconds { get; set; } = 60;

    public int MaxAnswerChars { get; set; } = 4_000;

    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>
    /// Also generates the request tool, when the ceiling allows requests.
    /// </summary>
    public bool AllowRequests { get; set; }

    /// <summary>
    /// MCP clients may see the consultation's tools, when the ceiling allows MCP.
    /// </summary>
    public bool ExposeToMcp { get; set; }

    /// <summary>
    /// The classification of the answers, one of <see cref="NhAssistantDataClassificationNames"/>;
    /// <see langword="null"/> uses the agent's.
    /// </summary>
    public string? DataClassification { get; set; }

    /// <summary>
    /// The consultation is switched off: it is not offered, and asking agents get none of its tools,
    /// until an administrator switches it on. A consultation stored before it could be switched off is
    /// on.
    /// </summary>
    public bool Off { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public Guid ConcurrencyStamp { get; set; }
}

/// <summary>
/// One row whose value changes with every change of the data-defined run configuration, so every
/// host knows when to reload.
/// </summary>
public sealed class AssistantRunConfigurationStamp
{
    public int Id { get; set; }

    public Guid Value { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>
/// The statuses of a data-defined skill version.
/// </summary>
public static class NhAssistantSkillDefinitionStatuses
{
    /// <summary>The version in effect.</summary>
    public const string Live = "live";

    /// <summary>A version that runs in shadow next to the live one until it is promoted or discarded.</summary>
    public const string Candidate = "candidate";

    /// <summary>An earlier version, kept as history.</summary>
    public const string Superseded = "superseded";

    /// <summary>A candidate that was discarded or replaced, kept as history.</summary>
    public const string Discarded = "discarded";
}

/// <summary>
/// The sources of a data-defined trigger.
/// </summary>
public static class NhAssistantDataTriggerSources
{
    public const string Event = "event";

    public const string Schedule = "schedule";
}
