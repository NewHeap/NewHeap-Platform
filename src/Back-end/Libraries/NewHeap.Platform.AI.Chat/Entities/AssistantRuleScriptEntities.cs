namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// The rule script of one agent, or of one skill of the agent (RB-2): which version is live, which is the
/// candidate and whether it runs in shadow. Its <see cref="Revision"/> is the optimistic concurrency of
/// every change of the rule script: saving a version, setting one live, trying the candidate in shadow and
/// discarding it each expect the revision they read and make the next one. The versions are the history.
/// </summary>
public sealed class AssistantRuleScript
{
    public Guid Id { get; set; }

    public string AgentId { get; set; } = string.Empty;

    /// <summary>The skill whose rules the script is; empty for the agent's own rules.</summary>
    public string SkillId { get; set; } = string.Empty;

    /// <summary>The revision, from 1, one more for every change; a concurrency token.</summary>
    public int Revision { get; set; }

    /// <summary>The version in effect; <see langword="null"/> while none went live.</summary>
    public int? LiveVersion { get; set; }

    /// <summary>The version that waits to go live; <see langword="null"/> without one.</summary>
    public int? CandidateVersion { get; set; }

    /// <summary>Whether the candidate runs in shadow next to the live rules.</summary>
    public bool CandidateShadow { get; set; }

    /// <summary>The highest version, which the next saved version follows.</summary>
    public int LatestVersion { get; set; }

    /// <summary>When it changed last, on the application's clock.</summary>
    public DateTimeOffset ChangedAt { get; set; }

    public string ChangedBy { get; set; } = string.Empty;

    public List<AssistantRuleScriptVersion> Versions { get; set; } = [];
}

/// <summary>
/// One version of a rule script (RB-2): the script text and its hash, the writing tools it may call
/// within the code's ceiling, its author and reason, created on the application's clock. Its content
/// never changes; only its status does (<see cref="NhAssistantRuleScriptStatuses"/>).
/// </summary>
public sealed class AssistantRuleScriptVersion
{
    public Guid Id { get; set; }

    public Guid RuleScriptId { get; set; }

    public AssistantRuleScript RuleScript { get; set; } = null!;

    public string AgentId { get; set; } = string.Empty;

    /// <summary>The skill whose rules the script is; empty for the agent's own rules.</summary>
    public string SkillId { get; set; } = string.Empty;

    /// <summary>The version, from 1, per agent and skill.</summary>
    public int Version { get; set; }

    /// <summary>One of <see cref="NhAssistantRuleScriptStatuses"/>.</summary>
    public string Status { get; set; } = NhAssistantRuleScriptStatuses.Candidate;

    /// <summary>The script, the body of an async JavaScript function.</summary>
    public string Script { get; set; } = string.Empty;

    /// <summary>The SHA-256 of <see cref="Script"/>, lowercase hexadecimal, as the calls' origin names it.</summary>
    public string ScriptHash { get; set; } = string.Empty;

    /// <summary>The writing tools the version may call, a JSON array of tool ids within the ceiling.</summary>
    public string WritesJson { get; set; } = "[]";

    /// <summary>The person who wrote the version, also when the agent builder wrote it for them.</summary>
    public string AuthorActorId { get; set; } = string.Empty;

    /// <summary>The agent that wrote the version for <see cref="AuthorActorId"/>, such as the agent builder.</summary>
    public string? AuthorAgentId { get; set; }

    /// <summary>Why, kept with the version and left out of the business audit.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the version went live, on the application's clock.</summary>
    public DateTimeOffset? LiveAt { get; set; }

    /// <summary>Who set the version live.</summary>
    public string? LiveBy { get; set; }

    /// <summary>When the version was superseded or discarded.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Who discarded the version, or set the version live that superseded it.</summary>
    public string? EndedBy { get; set; }
}

/// <summary>
/// What one version did without effects (RB-5): a dry run an administrator or the agent builder started on
/// chosen subjects, or a shadow run next to the live rules of a run. The record lists per subject the
/// calls, writes, proposals and alerts it would make, with their outcomes. The latest dry run of a version
/// and its newest shadow runs are kept.
/// </summary>
public sealed class AssistantRuleScriptRunRecord
{
    public Guid Id { get; set; }

    public Guid VersionId { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public string SkillId { get; set; } = string.Empty;

    public int Version { get; set; }

    /// <summary><c>dry-run</c> or <c>shadow</c> (<see cref="NhAssistantRuleScriptRunKinds"/>).</summary>
    public string Kind { get; set; } = NhAssistantRuleScriptRunKinds.DryRun;

    /// <summary>The run a shadow run went along with; <see langword="null"/> for a dry run.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Who started a dry run; the agent's actor for a shadow run.</summary>
    public string RanBy { get; set; } = string.Empty;

    public DateTimeOffset RanAt { get; set; }

    public int SubjectCount { get; set; }

    /// <summary>How many subjects the script completed on.</summary>
    public int CompletedCount { get; set; }

    /// <summary>The record as JSON, bounded.</summary>
    public string RecordJson { get; set; } = "{}";
}

/// <summary>
/// The kinds of runs of a rule script version without effects.
/// </summary>
public static class NhAssistantRuleScriptRunKinds
{
    /// <summary>A dry run on chosen subjects.</summary>
    public const string DryRun = "dry-run";

    /// <summary>A shadow run of the candidate next to the live rules of a run.</summary>
    public const string Shadow = "shadow";
}
