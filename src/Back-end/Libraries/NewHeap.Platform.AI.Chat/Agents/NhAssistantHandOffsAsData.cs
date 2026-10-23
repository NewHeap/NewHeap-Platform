using System.Text.Json;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The ceiling in code of hand-offs that administrators define as data: per subject type, which
/// agents may accept hand-offs as data and from which agents, and the highest daily limit of one
/// hand-off. An administrator connects agents only within it; a stored hand-off that no longer fits
/// it is out of effect. See <see cref="NhAssistantBuilder.UseHandOffsAsData"/>.
/// </summary>
public sealed class NhAssistantHandOffCeiling
{
    private readonly List<NhAssistantHandOffCeilingRule> _rules = [];

    internal NhAssistantHandOffCeiling()
    {
    }

    /// <summary>
    /// The connections the ceiling allows, in the order the application named them.
    /// </summary>
    public IReadOnlyList<NhAssistantHandOffCeilingRule> Rules => _rules;

    /// <summary>
    /// The highest daily limit per source and subject a hand-off defined as data may set, 1 to 100;
    /// 100, the bound of <c>Limits</c> in code, by default.
    /// </summary>
    public int MaxPerSubjectPerDay { get; internal set; } = NhAssistantDataHandOffLimits.MaxPerSubjectPerDay;

    /// <summary>
    /// Whether the ceiling lets <paramref name="sourceAgentId"/> hand work on subjects of
    /// <paramref name="subjectType"/> to <paramref name="targetAgentId"/>.
    /// <paramref name="createdInAdministration"/> tells whether an agent was created in the
    /// administration, for the rules that allow those agents.
    /// </summary>
    public bool Allows(
        string targetAgentId,
        string subjectType,
        string sourceAgentId,
        Func<string, bool> createdInAdministration)
    {
        ArgumentNullException.ThrowIfNull(createdInAdministration);
        return _rules.Any(rule => rule.AllowsTarget(targetAgentId, subjectType, createdInAdministration)
                                  && rule.AllowsSource(sourceAgentId, createdInAdministration));
    }

    /// <summary>
    /// Whether any rule lets <paramref name="targetAgentId"/> accept hand-offs as data, about any
    /// subject type.
    /// </summary>
    public bool AllowsTarget(string targetAgentId, Func<string, bool> createdInAdministration)
    {
        ArgumentNullException.ThrowIfNull(createdInAdministration);
        return _rules.Any(rule => rule.AllowsTarget(targetAgentId, rule.SubjectType, createdInAdministration));
    }

    internal void Add(NhAssistantHandOffCeilingRule rule)
    {
        _rules.Add(rule);
    }
}

/// <summary>
/// One connection of the ceiling: agents that may accept hand-offs about one subject type as data,
/// and the agents that may hand off to them.
/// </summary>
public sealed class NhAssistantHandOffCeilingRule
{
    private readonly List<string> _targets = [];
    private readonly List<string> _sources = [];

    internal NhAssistantHandOffCeilingRule(string subjectType)
    {
        SubjectType = subjectType;
    }

    public string SubjectType { get; }

    /// <summary>
    /// The agents in code that may accept the hand-offs.
    /// </summary>
    public IReadOnlyList<string> Targets => _targets;

    /// <summary>
    /// Every agent created in the administration may accept them too.
    /// </summary>
    public bool TargetsCreatedInAdministration { get; internal set; }

    /// <summary>
    /// The agents in code that may hand off.
    /// </summary>
    public IReadOnlyList<string> Sources => _sources;

    /// <summary>
    /// Every agent created in the administration may hand off too.
    /// </summary>
    public bool SourcesCreatedInAdministration { get; internal set; }

    internal bool AllowsTarget(string agentId, string subjectType, Func<string, bool> createdInAdministration)
    {
        return string.Equals(subjectType, SubjectType, StringComparison.Ordinal)
               && (_targets.Contains(agentId, StringComparer.Ordinal)
                   || (TargetsCreatedInAdministration && createdInAdministration(agentId)));
    }

    internal bool AllowsSource(string agentId, Func<string, bool> createdInAdministration)
    {
        return _sources.Contains(agentId, StringComparer.Ordinal)
               || (SourcesCreatedInAdministration && createdInAdministration(agentId));
    }

    internal void AddTargets(IEnumerable<string> agentIds)
    {
        foreach (var agentId in agentIds.Where(agentId => !_targets.Contains(agentId, StringComparer.Ordinal)))
        {
            _targets.Add(agentId);
        }
    }

    internal void AddSources(IEnumerable<string> agentIds)
    {
        foreach (var agentId in agentIds.Where(agentId => !_sources.Contains(agentId, StringComparer.Ordinal)))
        {
            _sources.Add(agentId);
        }
    }
}

/// <summary>
/// Configures the ceiling of hand-offs defined as data; see
/// <see cref="NhAssistantBuilder.UseHandOffsAsData"/>.
/// </summary>
public sealed class NhAssistantHandOffCeilingBuilder
{
    private readonly NhAssistantHandOffCeiling _ceiling;

    internal NhAssistantHandOffCeilingBuilder(NhAssistantHandOffCeiling ceiling)
    {
        _ceiling = ceiling;
    }

    /// <summary>
    /// Lets administrators connect agents about subjects of <paramref name="subjectType"/>: the
    /// agents named with <c>To</c> (or every agent created in the administration) may accept
    /// hand-offs as data from the agents named with <c>From</c> (or every agent created in the
    /// administration). Name at least one of each. Startup fails for an agent in code that is not
    /// configured with <c>ConfigureAgent</c>, and for a target in code that does not work on the type,
    /// as it does for <c>AcceptHandOffs</c>.
    /// </summary>
    public NhAssistantHandOffCeilingBuilder Allow(string subjectType, Action<NhAssistantHandOffCeilingRuleBuilder> configure)
    {
        if (!NhAiSubjectRef.IsValidType(subjectType))
        {
            throw new ArgumentException("A subject type is a dash-case identifier.", nameof(subjectType));
        }
        ArgumentNullException.ThrowIfNull(configure);

        var rule = new NhAssistantHandOffCeilingRule(subjectType);
        configure(new NhAssistantHandOffCeilingRuleBuilder(rule));
        if (rule.Targets.Count == 0 && !rule.TargetsCreatedInAdministration)
        {
            throw new InvalidOperationException(
                $"The hand-offs as data about '{subjectType}' have no target; name them with To or ToAgentsCreatedInAdministration.");
        }
        if (rule.Sources.Count == 0 && !rule.SourcesCreatedInAdministration)
        {
            throw new InvalidOperationException(
                $"The hand-offs as data about '{subjectType}' have no source; name them with From or FromAgentsCreatedInAdministration.");
        }

        _ceiling.Add(rule);
        return this;
    }

    /// <summary>
    /// The highest daily limit per source and subject a hand-off defined as data may set, 1 to 100.
    /// Without it data may use the bound of <c>Limits</c> in code, 100.
    /// </summary>
    public NhAssistantHandOffCeilingBuilder MaxPerSubjectPerDay(int maxPerSubjectPerDay)
    {
        if (maxPerSubjectPerDay is < 1 or > NhAssistantDataHandOffLimits.MaxPerSubjectPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPerSubjectPerDay), "Allow 1 to 100 hand-offs per subject and day.");
        }

        _ceiling.MaxPerSubjectPerDay = maxPerSubjectPerDay;
        return this;
    }
}

/// <summary>
/// Names the targets and sources of one connection of the ceiling; see
/// <see cref="NhAssistantHandOffCeilingBuilder.Allow"/>.
/// </summary>
public sealed class NhAssistantHandOffCeilingRuleBuilder
{
    private readonly NhAssistantHandOffCeilingRule _rule;

    internal NhAssistantHandOffCeilingRuleBuilder(NhAssistantHandOffCeilingRule rule)
    {
        _rule = rule;
    }

    /// <summary>
    /// Agents in code that may accept the hand-offs as data.
    /// </summary>
    public NhAssistantHandOffCeilingRuleBuilder To(params string[] agentIds)
    {
        _rule.AddTargets(Validate(agentIds));
        return this;
    }

    /// <summary>
    /// Every agent created in the administration with run settings may accept the hand-offs as data.
    /// </summary>
    public NhAssistantHandOffCeilingRuleBuilder ToAgentsCreatedInAdministration()
    {
        _rule.TargetsCreatedInAdministration = true;
        return this;
    }

    /// <summary>
    /// Agents in code that may hand off. An agent never hands off to itself.
    /// </summary>
    public NhAssistantHandOffCeilingRuleBuilder From(params string[] agentIds)
    {
        _rule.AddSources(Validate(agentIds));
        return this;
    }

    /// <summary>
    /// Every agent created in the administration with run settings may hand off.
    /// </summary>
    public NhAssistantHandOffCeilingRuleBuilder FromAgentsCreatedInAdministration()
    {
        _rule.SourcesCreatedInAdministration = true;
        return this;
    }

    private static string[] Validate(string[] agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        if (agentIds.Length == 0)
        {
            throw new ArgumentException("Name at least one agent.", nameof(agentIds));
        }
        foreach (var agentId in agentIds)
        {
            NhAssistantNames.ValidateSegment(agentId, nameof(agentIds));
        }

        return agentIds;
    }
}

/// <summary>
/// Where a hand-off comes from: <see cref="NhAssistantHandOffOptions.Source"/> and the administration.
/// </summary>
public static class NhAssistantHandOffSources
{
    /// <summary>Registered in code with <c>AcceptHandOffs</c>.</summary>
    public const string Code = "code";

    /// <summary>Defined as data in the administration, within <c>UseHandOffsAsData</c>.</summary>
    public const string Data = "data";
}

/// <summary>
/// The bounds of a hand-off defined as data, the same as those of <c>AcceptHandOffs</c> in code.
/// </summary>
public static class NhAssistantDataHandOffLimits
{
    /// <summary>
    /// The most hand-offs defined as data one agent accepts, one per subject type.
    /// </summary>
    public const int MaxHandOffsPerAgent = 20;

    /// <summary>
    /// The most agents one hand-off defined as data accepts work from.
    /// </summary>
    public const int MaxSources = 20;

    /// <summary>
    /// The longest description, in characters, as in code.
    /// </summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>
    /// The highest daily limit per source and subject, as in code.
    /// </summary>
    public const int MaxPerSubjectPerDay = 100;

    /// <summary>
    /// The daily limit of a new hand-off, as in code.
    /// </summary>
    public const int DefaultPerSubjectPerDay = 5;

    /// <summary>
    /// The longest stored list of sources in characters; the most sources with the longest ids fit.
    /// </summary>
    internal const int MaxSourcesJsonLength = 2_700;
}

/// <summary>
/// The sources a hand-off defined as data stores: a JSON array of distinct agent ids, 1 to
/// <see cref="NhAssistantDataHandOffLimits.MaxSources"/>.
/// </summary>
internal static class NhAssistantDataHandOffSources
{
    /// <summary>
    /// The stored sources in order, or <see langword="null"/> when the value is not a valid list.
    /// </summary>
    public static IReadOnlyList<string>? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > NhAssistantDataHandOffLimits.MaxSourcesJsonLength)
        {
            return null;
        }

        string[]? sources;
        try
        {
            sources = JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        return sources is not null && IsValid(sources) ? sources : null;
    }

    public static string Write(IReadOnlyList<string> sources)
    {
        return JsonSerializer.Serialize(sources);
    }

    /// <summary>
    /// One to <see cref="NhAssistantDataHandOffLimits.MaxSources"/> valid agent ids, each once.
    /// </summary>
    public static bool IsValid(IReadOnlyList<string> sources)
    {
        return sources.Count is > 0 and <= NhAssistantDataHandOffLimits.MaxSources
               && sources.All(NhAssistantNames.IsSegment)
               && sources.Distinct(StringComparer.Ordinal).Count() == sources.Count;
    }
}

/// <summary>
/// The outcome of checking a hand-off defined as data: its options in effect, or
/// <see langword="null"/> with the problem that keeps it out of effect. A hand-off is never in effect
/// with less than it was saved with.
/// </summary>
internal sealed record NhAssistantDataHandOffCheck(NhAssistantHandOffOptions? Options, string? Problem)
{
    public static NhAssistantDataHandOffCheck Out(string problem)
    {
        return new NhAssistantDataHandOffCheck(null, problem);
    }
}
