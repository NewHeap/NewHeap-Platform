using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The checks of hand-offs defined as data and of data shadow variants, the same on every save and
/// every load, and mirroring the startup checks of <c>AcceptHandOffs</c> and <c>ShadowOf</c>.
/// </summary>
internal sealed partial class NhAssistantRunConfigurationValidator
{
    /// <summary>
    /// Checks a hand-off defined as data against the ceiling and the code: its stored values are
    /// within the bounds of <c>AcceptHandOffs</c>, the application calls <c>UseHandOffsAsData</c>, the
    /// accepting agent has a run configuration in <paramref name="agentRuns"/> and works on the
    /// subject type, it accepts no hand-offs about the type in code, every source has a run
    /// configuration, the ceiling connects every source to the agent about the type and the daily
    /// limit stays within the ceiling's. A hand-off that fails a check is out of effect as a whole; it
    /// is never in effect with less than it was saved with.
    /// </summary>
    public NhAssistantDataHandOffCheck CheckHandOff(
        AssistantAgentHandOffDefinition row,
        IReadOnlyDictionary<string, NhAssistantAgentRunOptions> agentRuns)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(agentRuns);
        var sources = NhAssistantDataHandOffSources.Read(row.SourcesJson);
        if (sources is null
            || !NhAiSubjectRef.IsValidType(row.SubjectType)
            || row.Description is { Length: 0 or > NhAssistantDataHandOffLimits.MaxDescriptionLength }
            || row.MaxPerSubjectPerDay is < 1 or > NhAssistantDataHandOffLimits.MaxPerSubjectPerDay
            || sources.Contains(row.AgentId, StringComparer.Ordinal))
        {
            return NhAssistantDataHandOffCheck.Out(NhAssistantRunConfigurationProblems.Invalid);
        }
        if (registration.HandOffsAsData is not { } ceiling)
        {
            return NhAssistantDataHandOffCheck.Out(NhAssistantRunConfigurationProblems.OutsideCeiling);
        }
        if (!agentRuns.TryGetValue(row.AgentId, out var target))
        {
            return NhAssistantDataHandOffCheck.Out(NhAssistantRunConfigurationProblems.UnknownAgent);
        }
        if (registration.CodeAgentRuns.TryGetValue(row.AgentId, out var code)
            && code.HandOffs.Any(handOff => string.Equals(handOff.SubjectType, row.SubjectType, StringComparison.Ordinal)))
        {
            return NhAssistantDataHandOffCheck.Out(NhAssistantRunConfigurationProblems.ConflictsWithCode);
        }
        if (!target.WorksOn.Covers(row.SubjectType))
        {
            return NhAssistantDataHandOffCheck.Out(NhAssistantRunConfigurationProblems.NotWorkedOn);
        }
        if (sources.Any(source => !agentRuns.ContainsKey(source)))
        {
            return NhAssistantDataHandOffCheck.Out(NhAssistantRunConfigurationProblems.UnknownSource);
        }
        if (sources.Any(source => !ceiling.Allows(row.AgentId, row.SubjectType, source, IsCreatedInAdministration)))
        {
            return NhAssistantDataHandOffCheck.Out(NhAssistantRunConfigurationProblems.OutsideCeiling);
        }
        if (row.MaxPerSubjectPerDay > ceiling.MaxPerSubjectPerDay)
        {
            return NhAssistantDataHandOffCheck.Out(NhAssistantRunConfigurationProblems.LimitAboveCeiling);
        }

        return new NhAssistantDataHandOffCheck(
            NhAssistantHandOffOptions.FromData(
                row.AgentId,
                row.SubjectType,
                sources,
                row.Description,
                row.NotifySource,
                row.MaxPerSubjectPerDay,
                row.Version),
            null);
    }

    /// <summary>
    /// The problem of a data shadow variant of <paramref name="liveAgentId"/>, or
    /// <see langword="null"/> when it fits: the application calls <c>UseShadowVariantsAsData</c> and
    /// it allows variants of the live agent, the live agent has a run configuration in
    /// <paramref name="agentRuns"/> and is a live agent, neither in shadow by its code nor a variant
    /// itself, as startup checks <c>ShadowOf</c>.
    /// </summary>
    public string? CheckShadowVariant(
        string variantId,
        string liveAgentId,
        IReadOnlyDictionary<string, NhAssistantAgentRunOptions> agentRuns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variantId);
        return string.Equals(variantId, liveAgentId, StringComparison.Ordinal)
            ? NhAssistantRunConfigurationProblems.Invalid
            : CheckLiveAgentOfVariants(liveAgentId, agentRuns);
    }

    /// <summary>
    /// The problem a data shadow variant of <paramref name="liveAgentId"/> would have because of its
    /// live agent and the ceiling, or <see langword="null"/> when the agent may have data variants.
    /// </summary>
    public string? CheckLiveAgentOfVariants(
        string liveAgentId,
        IReadOnlyDictionary<string, NhAssistantAgentRunOptions> agentRuns)
    {
        ArgumentNullException.ThrowIfNull(agentRuns);
        if (string.IsNullOrWhiteSpace(liveAgentId) || !NhAssistantNames.IsSegment(liveAgentId))
        {
            return NhAssistantRunConfigurationProblems.Invalid;
        }
        if (registration.ShadowVariantsAsData is not { } ceiling
            || !ceiling.Allows(liveAgentId, IsCreatedInAdministration(liveAgentId)))
        {
            return NhAssistantRunConfigurationProblems.OutsideCeiling;
        }
        if (!agentRuns.TryGetValue(liveAgentId, out var live))
        {
            return NhAssistantRunConfigurationProblems.UnknownLiveAgent;
        }

        // In code an agent that starts in shadow is no live agent; an administrator's runtime shadow
        // switch of a live agent does not change what it is.
        var inCode = registration.CodeAgentRuns.ContainsKey(liveAgentId);
        return live.ShadowOfAgentId is not null || (inCode && live.Shadow)
            ? NhAssistantRunConfigurationProblems.LiveAgentNotLive
            : null;
    }

    /// <summary>
    /// Whether an agent with a run configuration was created in the administration: it is not
    /// registered in code with <c>AddAgent</c>.
    /// </summary>
    internal bool IsCreatedInAdministration(string agentId)
    {
        return !registration.TryGetAgent(agentId, out _);
    }
}

/// <summary>
/// Puts hand-offs defined as data and data shadow variants into the snapshot.
/// </summary>
internal sealed partial class NhAssistantRunConfigurations
{
    /// <summary>
    /// Takes the run settings of every data shadow variant that no longer fits out of the snapshot,
    /// with a problem of kind <c>agent</c>: a variant is never in effect as a live agent. Every
    /// variant is checked against the run configuration before any is taken out, and a live agent
    /// keeps the variants of the lowest ids up to the ceiling's maximum.
    /// </summary>
    private void RemoveVariantsOutOfEffect(
        NhAssistantStoredRunConfiguration stored,
        Dictionary<string, NhAssistantAgentRunOptions> agentRuns,
        Dictionary<string, NhAssistantRunAgent> runAgents,
        List<NhAssistantRunConfigurationProblem> problems)
    {
        var variants = stored.AdminAgents
            .Where(entry => entry.Value.ShadowOfAgentId is not null && agentRuns.ContainsKey(entry.Key))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (VariantId: entry.Key, LiveAgentId: entry.Value.ShadowOfAgentId!))
            .ToArray();
        if (variants.Length == 0)
        {
            return;
        }

        var outOfEffect = new List<(string VariantId, string Code)>();
        var kept = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxPerAgent = registration.ShadowVariantsAsData?.MaxPerAgent ?? 0;
        foreach (var (variantId, liveAgentId) in variants)
        {
            var problem = validator.CheckShadowVariant(variantId, liveAgentId, agentRuns);
            if (problem is null)
            {
                var count = kept.GetValueOrDefault(liveAgentId) + 1;
                kept[liveAgentId] = count;
                if (count > maxPerAgent)
                {
                    problem = NhAssistantRunConfigurationProblems.TooManyVariants;
                }
            }
            if (problem is not null)
            {
                outOfEffect.Add((variantId, problem));
            }
        }

        foreach (var (variantId, code) in outOfEffect)
        {
            agentRuns.Remove(variantId);
            runAgents.Remove(variantId);
            problems.Add(new NhAssistantRunConfigurationProblem(variantId, NhAssistantRunConfigurationProblems.AgentKind, variantId, code));
        }
    }

    /// <summary>
    /// Adds the hand-offs defined as data that fit the ceiling and the code to their accepting
    /// agents' options, checked once the run settings, skills and consultations are in, and lists
    /// every hand-off out of effect as a problem.
    /// </summary>
    private void AddDataHandOffs(
        NhAssistantStoredRunConfiguration stored,
        Dictionary<string, NhAssistantAgentRunOptions> agentRuns,
        List<NhAssistantRunConfigurationProblem> problems)
    {
        // Every hand-off is checked against the same run configuration.
        var checkedAgainst = new Dictionary<string, NhAssistantAgentRunOptions>(agentRuns, StringComparer.Ordinal);
        foreach (var group in stored.HandOffs.GroupBy(row => row.AgentId, StringComparer.Ordinal))
        {
            var dataHandOffs = new List<NhAssistantHandOffOptions>();
            foreach (var row in group)
            {
                var check = validator.CheckHandOff(row, checkedAgainst);
                if (check.Options is { } options)
                {
                    dataHandOffs.Add(options);
                    continue;
                }

                problems.Add(new NhAssistantRunConfigurationProblem(
                    row.AgentId,
                    NhAssistantRunConfigurationProblems.HandOffKind,
                    row.SubjectType,
                    check.Problem!));
            }

            if (dataHandOffs.Count > 0)
            {
                agentRuns[group.Key] = agentRuns[group.Key].WithDataHandOffs(dataHandOffs);
            }
        }
    }
}
