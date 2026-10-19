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
/// A data-defined trigger: a named trigger event or a schedule that starts an agent, in one of its
/// skills or routed by the agent itself.
/// </summary>
public sealed class AssistantAgentTriggerDefinition
{
    public string Id { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string? SkillId { get; set; }

    /// <summary>
    /// <c>event</c> or <c>schedule</c>.
    /// </summary>
    public string Source { get; set; } = NhAssistantDataTriggerSources.Event;

    /// <summary>
    /// The trigger event registered with <c>AddTriggerEvent</c>, for an event trigger.
    /// </summary>
    public string? EventName { get; set; }

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
    /// The trigger's runs work in the agent's shadow thread. A new trigger starts in shadow.
    /// </summary>
    public bool Shadow { get; set; } = true;

    public int Version { get; set; } = 1;

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
}

/// <summary>
/// The sources of a data-defined trigger.
/// </summary>
public static class NhAssistantDataTriggerSources
{
    public const string Event = "event";

    public const string Schedule = "schedule";
}
