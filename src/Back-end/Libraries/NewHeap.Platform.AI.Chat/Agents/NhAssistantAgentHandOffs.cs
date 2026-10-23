using System.Text.Json;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Which agents may hand work on subjects of one type to an agent, and what the agent tells them
/// afterwards. See <see cref="NhAssistantAgentRunBuilder.AcceptHandOffs"/>.
/// </summary>
public sealed class NhAssistantHandOffOptions
{
    private readonly List<string> _sources = [];

    internal NhAssistantHandOffOptions(string agentId, string subjectType)
    {
        AgentId = agentId;
        SubjectType = subjectType;
    }

    /// <summary>
    /// The agent that accepts the hand-offs.
    /// </summary>
    public string AgentId { get; }

    public string SubjectType { get; }

    /// <summary>
    /// The id of the generated tool through which a source agent's model hands work off.
    /// </summary>
    public string ToolId => NhAssistantHandOffTools.ToolId(AgentId, SubjectType);

    /// <summary>
    /// The agents that may hand work off; no other agent and no person can.
    /// </summary>
    public IReadOnlyList<string> Sources => _sources;

    /// <summary>
    /// Tells the source's model what the agent does with the work, so it hands off at the right moment.
    /// </summary>
    public string? Description { get; internal set; }

    /// <summary>
    /// The source gets a follow-up when the agent's run for the hand-off ends.
    /// </summary>
    public bool NotifySource { get; internal set; }

    /// <summary>
    /// How many hand-offs one source may make about one subject per 24 hours. The default is 5.
    /// </summary>
    public int MaxPerSubjectPerDay { get; internal set; } = 5;

    /// <summary>
    /// Where the hand-off comes from: <see cref="NhAssistantHandOffSources.Code"/> for
    /// <c>AcceptHandOffs</c>, <see cref="NhAssistantHandOffSources.Data"/> for one an administrator
    /// defined within <c>UseHandOffsAsData</c>.
    /// </summary>
    public string Source { get; internal set; } = NhAssistantHandOffSources.Code;

    /// <summary>
    /// The version of a hand-off defined as data; <see langword="null"/> for one in code.
    /// </summary>
    public int? DataVersion { get; internal set; }

    /// <summary>
    /// Whether an administrator defined the hand-off as data.
    /// </summary>
    public bool IsData => string.Equals(Source, NhAssistantHandOffSources.Data, StringComparison.Ordinal);

    /// <summary>
    /// The options of a hand-off defined as data, after its checks.
    /// </summary>
    internal static NhAssistantHandOffOptions FromData(
        string agentId,
        string subjectType,
        IEnumerable<string> sources,
        string? description,
        bool notifySource,
        int maxPerSubjectPerDay,
        int version)
    {
        var options = new NhAssistantHandOffOptions(agentId, subjectType)
        {
            Description = description,
            NotifySource = notifySource,
            MaxPerSubjectPerDay = maxPerSubjectPerDay,
            Source = NhAssistantHandOffSources.Data,
            DataVersion = version
        };
        options.AddSources(sources);
        return options;
    }

    internal void AddSources(IEnumerable<string> agentIds)
    {
        foreach (var agentId in agentIds)
        {
            if (!_sources.Contains(agentId, StringComparer.Ordinal))
            {
                _sources.Add(agentId);
            }
        }
    }
}

/// <summary>
/// Configures which agents may hand work off; see <see cref="NhAssistantAgentRunBuilder.AcceptHandOffs"/>.
/// </summary>
public sealed class NhAssistantHandOffBuilder
{
    private readonly NhAssistantHandOffOptions _options;

    internal NhAssistantHandOffBuilder(NhAssistantHandOffOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// The agents that may hand work off. The agent itself is never one of them.
    /// </summary>
    public NhAssistantHandOffBuilder From(params string[] agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        if (agentIds.Length == 0)
        {
            throw new ArgumentException("Name at least one agent.", nameof(agentIds));
        }
        foreach (var agentId in agentIds)
        {
            NhAssistantNames.ValidateSegment(agentId, nameof(agentIds));
            if (string.Equals(agentId, _options.AgentId, StringComparison.Ordinal))
            {
                throw new ArgumentException("An agent does not hand work off to itself.", nameof(agentIds));
            }
        }

        _options.AddSources(agentIds);
        return this;
    }

    public NhAssistantHandOffBuilder Description(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(description), "A hand-off description has at most 500 characters.");
        }

        _options.Description = description.Trim();
        return this;
    }

    /// <summary>
    /// Gives the source a follow-up when the run for the hand-off ends: the outcome and the number of
    /// proposals, and the run's summary when both agents worked on the same subject and the run read
    /// nothing restricted with the agent's own grant.
    /// </summary>
    public NhAssistantHandOffBuilder NotifySource()
    {
        _options.NotifySource = true;
        return this;
    }

    /// <param name="maxPerSubjectPerDay">Hand-offs one source may make about one subject per 24 hours, 1 to 100.</param>
    public NhAssistantHandOffBuilder Limits(int maxPerSubjectPerDay)
    {
        if (maxPerSubjectPerDay is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPerSubjectPerDay), "Allow 1 to 100 hand-offs per subject and day.");
        }

        _options.MaxPerSubjectPerDay = maxPerSubjectPerDay;
        return this;
    }
}

/// <summary>
/// The ids of the generated hand-off tools. Select them in the source agent's tool selectors, for
/// example <c>assistant.handoff.project-closer.project</c>.
/// </summary>
public static class NhAssistantHandOffTools
{
    public const string Prefix = "assistant.handoff.";

    /// <summary>
    /// The longest message a hand-off carries to the target.
    /// </summary>
    public const int MaxMessageChars = 2_000;

    public static string ToolId(string agentId, string subjectType)
    {
        NhAssistantNames.ValidateSegment(agentId, nameof(agentId));
        if (!NhAiSubjectRef.IsValidType(subjectType))
        {
            throw new ArgumentException("A subject type is a dash-case identifier.", nameof(subjectType));
        }

        return Prefix + agentId + "." + subjectType;
    }

    internal static bool IsHandOffTool(string toolId)
    {
        return toolId.StartsWith(Prefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// What became of a hand-off.
/// </summary>
/// <param name="Outcome">One of <see cref="NhAssistantHandOffOutcomes"/>.</param>
/// <param name="RunId">The target's run, once there is one.</param>
/// <param name="Code">Why the hand-off was refused or waits.</param>
public sealed record NhAssistantHandOffReceipt(
    string Outcome,
    Guid? RunId,
    string? Code)
{
    public bool Accepted => Outcome is NhAssistantHandOffOutcomes.Accepted or NhAssistantHandOffOutcomes.Waiting;

    internal static NhAssistantHandOffReceipt Refused(string code)
    {
        return new NhAssistantHandOffReceipt(NhAssistantHandOffOutcomes.Refused, null, code);
    }
}

public static class NhAssistantHandOffOutcomes
{
    /// <summary>The target has a run for the work.</summary>
    public const string Accepted = "accepted";

    /// <summary>The target is paused; the work waits in its thread until it resumes.</summary>
    public const string Waiting = "waiting";

    /// <summary>The same run already handed the same subject to the same agent.</summary>
    public const string Deduplicated = "deduplicated";

    public const string Refused = "refused";
}

/// <summary>
/// How a run for a hand-off ended, as the source's follow-up input carries it in its payload. Read
/// it in a rule handler with <c>input.GetPayload&lt;NhAssistantHandOffResult&gt;()</c> for an input of
/// trigger <see cref="NhAssistantTriggerIds.HandOffEnded"/>.
/// </summary>
/// <param name="Status">The run's status: <c>completed</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="Summary">The run's summary, only when both agents worked on the same subject and the
/// run read nothing restricted with its agent's own grant.</param>
public sealed record NhAssistantHandOffResult(
    Guid HandOffId,
    string AgentId,
    string SubjectType,
    string SubjectId,
    Guid RunId,
    string Status,
    string? FailureCode,
    int ProposalCount,
    string? Summary);

/// <summary>
/// A hand-off from a run of one agent to another agent, as the turn runner asks for it.
/// </summary>
/// <param name="SourceSkillId">The skill the source run worked in, which its follow-up returns to.</param>
internal sealed record NhAssistantHandOffRequest(
    Runtime.NhAssistantAgentTurnContext Source,
    string? SourceSkillId,
    string TargetAgentId,
    NhAiSubjectRef Subject,
    string Message,
    string? PayloadJson);

/// <summary>
/// Hands work from an agent run to another agent; implemented by the agent-run host.
/// </summary>
internal interface INhAssistantHandOffs
{
    Task<NhAssistantHandOffReceipt> HandOffAsync(NhAssistantHandOffRequest request, CancellationToken cancellationToken);
}

internal static class NhAssistantHandOffPayloads
{
    public static string? Serialize(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(payload, payload.GetType(), JsonSerializerOptions.Web);
        if (json.Length > NhAssistantLimits.MaxInputPayloadCharacters)
        {
            throw new ArgumentException(
                $"A hand-off payload has at most {NhAssistantLimits.MaxInputPayloadCharacters} characters of JSON.",
                nameof(payload));
        }

        return json;
    }
}
