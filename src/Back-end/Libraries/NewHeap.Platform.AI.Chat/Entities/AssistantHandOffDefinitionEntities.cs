namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// A hand-off an administrator defined as data: the agent accepts work on subjects of one type from
/// the stored source agents, within the ceiling in code (<c>UseHandOffsAsData</c>). One row per
/// accepting agent and subject type; every change is its next version.
/// </summary>
public sealed class AssistantAgentHandOffDefinition
{
    /// <summary>
    /// The agent that accepts the hand-offs.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    /// <summary>
    /// Incremented on every change.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// The agents that may hand off, a JSON array of distinct agent ids.
    /// </summary>
    public string SourcesJson { get; set; } = "[]";

    /// <summary>
    /// What the source's model reads about the agent's work, so it hands off at the right moment.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The source gets a follow-up when the agent's run for the hand-off ends.
    /// </summary>
    public bool NotifySource { get; set; }

    /// <summary>
    /// How many hand-offs one source may make about one subject per 24 hours.
    /// </summary>
    public int MaxPerSubjectPerDay { get; set; } = NhAssistantDataHandOffLimits.DefaultPerSubjectPerDay;

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public Guid ConcurrencyStamp { get; set; }
}
