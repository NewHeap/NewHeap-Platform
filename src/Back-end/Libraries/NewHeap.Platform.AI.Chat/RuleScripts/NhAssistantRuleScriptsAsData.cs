namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The application's ceiling of rule scripts (<see cref="NhAssistantBuilder.UseRuleScriptsAsData"/>, RB-2,
/// RB-4): which agents may have their rules, and the rules of their skills, written as a script stored as
/// data, which writing tools such a script may call and how many writes one run may make. An administrator
/// chooses per script version which of those writes it may call; every write still gets the decision it
/// gets on its own. Without the ceiling no agent has rule scripts and nothing changes.
/// </summary>
public sealed class NhAssistantRuleScriptCeiling
{
    private readonly Dictionary<string, NhAssistantRuleScriptAgentCeiling> _agents = new(StringComparer.Ordinal);

    internal NhAssistantRuleScriptCeiling()
    {
    }

    /// <summary>The agents in code that may have rule scripts, by id.</summary>
    public IReadOnlyDictionary<string, NhAssistantRuleScriptAgentCeiling> Agents => _agents;

    /// <summary>
    /// The ceiling of every agent created in the administration; <see langword="null"/> when such agents
    /// have no rule scripts.
    /// </summary>
    public NhAssistantRuleScriptAgentCeiling? AgentsCreatedInAdministration { get; internal set; }

    /// <summary>
    /// Whether a version goes live only by another administrator than its author (four eyes, RB-3): the
    /// person who saved a version, also through the agent builder, cannot set it live.
    /// </summary>
    public bool RequireFourEyes { get; internal set; }

    /// <summary>
    /// The ceiling of <paramref name="agentId"/>: its own, otherwise the one of agents created in the
    /// administration when it is such an agent; <see langword="null"/> when it may have no rule scripts.
    /// </summary>
    public NhAssistantRuleScriptAgentCeiling? For(string agentId, bool createdInAdministration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (_agents.TryGetValue(agentId, out var ceiling))
        {
            return ceiling;
        }

        return createdInAdministration ? AgentsCreatedInAdministration : null;
    }

    internal NhAssistantRuleScriptAgentCeiling GetOrAdd(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var ceiling))
        {
            ceiling = new NhAssistantRuleScriptAgentCeiling();
            _agents.Add(agentId, ceiling);
        }

        return ceiling;
    }
}

/// <summary>
/// What code allows the rule scripts of one agent, and of its skills: the writing tools a version may
/// opt in to, the most writes one run makes and the destructive tools among them that may run in a
/// script at all.
/// </summary>
public sealed class NhAssistantRuleScriptAgentCeiling
{
    private readonly HashSet<string> _writes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _destructive = new(StringComparer.Ordinal);

    internal NhAssistantRuleScriptAgentCeiling()
    {
    }

    /// <summary>
    /// The ids of the tools that change something which a version may opt in to; empty for read-only rule
    /// scripts, which only read, raise and resolve alerts, propose and end the run.
    /// </summary>
    public IReadOnlySet<string> Writes => _writes;

    /// <summary>
    /// The most calls of writing tools one run of a script makes, whether they run or become proposals;
    /// 0 while <see cref="Writes"/> is empty. At most <see cref="NhAssistantRuleScriptLimits.MaxWritesPerRun"/>.
    /// </summary>
    public int MaxWritesPerRun { get; internal set; }

    /// <summary>
    /// The destructive tools of <see cref="Writes"/> that may run in a script; another destructive tool is
    /// refused even when a version opts in to it.
    /// </summary>
    public IReadOnlySet<string> Destructive => _destructive;

    internal void AddWrite(string toolId)
    {
        _writes.Add(toolId);
    }

    internal void AddDestructive(string toolId)
    {
        _destructive.Add(toolId);
    }
}

/// <summary>
/// Configures <see cref="NhAssistantRuleScriptCeiling"/>; see <see cref="NhAssistantBuilder.UseRuleScriptsAsData"/>.
/// </summary>
public sealed class NhAssistantRuleScriptCeilingBuilder
{
    private readonly NhAssistantRuleScriptCeiling _ceiling;

    internal NhAssistantRuleScriptCeilingBuilder(NhAssistantRuleScriptCeiling ceiling)
    {
        _ceiling = ceiling;
    }

    /// <summary>
    /// The agent in code <paramref name="agentId"/> may have rule scripts, for its own rules and those of
    /// its skills; <paramref name="configure"/> names the writes they may opt in to.
    /// </summary>
    public NhAssistantRuleScriptCeilingBuilder ForAgent(
        string agentId,
        Action<NhAssistantRuleScriptAgentCeilingBuilder>? configure = null)
    {
        NhAssistantNames.ValidateSegment(agentId, nameof(agentId));
        var ceiling = _ceiling.GetOrAdd(agentId);
        configure?.Invoke(new NhAssistantRuleScriptAgentCeilingBuilder(ceiling));
        return this;
    }

    /// <summary>
    /// Every agent created in the administration may have rule scripts within one ceiling.
    /// </summary>
    public NhAssistantRuleScriptCeilingBuilder ForAgentsCreatedInAdministration(
        Action<NhAssistantRuleScriptAgentCeilingBuilder>? configure = null)
    {
        var ceiling = _ceiling.AgentsCreatedInAdministration ?? new NhAssistantRuleScriptAgentCeiling();
        configure?.Invoke(new NhAssistantRuleScriptAgentCeilingBuilder(ceiling));
        _ceiling.AgentsCreatedInAdministration = ceiling;
        return this;
    }

    /// <summary>
    /// A version goes live only by another administrator than the person who saved it (four eyes).
    /// </summary>
    public NhAssistantRuleScriptCeilingBuilder RequireFourEyes(bool enabled = true)
    {
        _ceiling.RequireFourEyes = enabled;
        return this;
    }
}

/// <summary>
/// Configures one <see cref="NhAssistantRuleScriptAgentCeiling"/>.
/// </summary>
public sealed class NhAssistantRuleScriptAgentCeilingBuilder
{
    private readonly NhAssistantRuleScriptAgentCeiling _ceiling;

    internal NhAssistantRuleScriptAgentCeilingBuilder(NhAssistantRuleScriptAgentCeiling ceiling)
    {
        _ceiling = ceiling;
    }

    /// <summary>
    /// The writing tools, by id, a version may opt in to. A version's script may call only the ones its
    /// administrator chose, and each call still gets its own approval policy, guards and budget: a tool
    /// that needs approval records a proposal, one that does not runs. One write per run unless
    /// <see cref="MaxWritesPerRun"/> says more.
    /// </summary>
    public NhAssistantRuleScriptAgentCeilingBuilder AllowWrites(params string[] toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        foreach (var toolId in toolIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
            _ceiling.AddWrite(toolId);
        }

        if (_ceiling.MaxWritesPerRun == 0 && _ceiling.Writes.Count > 0)
        {
            _ceiling.MaxWritesPerRun = 1;
        }

        return this;
    }

    /// <summary>
    /// The most writes one run of a script makes, from 1 to <see cref="NhAssistantRuleScriptLimits.MaxWritesPerRun"/>.
    /// </summary>
    public NhAssistantRuleScriptAgentCeilingBuilder MaxWritesPerRun(int writes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(writes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(writes, NhAssistantRuleScriptLimits.MaxWritesPerRun);
        _ceiling.MaxWritesPerRun = writes;
        return this;
    }

    /// <summary>
    /// Lets the named destructive tools of <see cref="AllowWrites"/> run in a script. Without it a
    /// destructive tool is refused in every rule script, whatever a version opts in to.
    /// </summary>
    public NhAssistantRuleScriptAgentCeilingBuilder AllowDestructive(params string[] toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        foreach (var toolId in toolIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
            _ceiling.AddDestructive(toolId);
        }

        return this;
    }
}

/// <summary>
/// The bounds of rule scripts.
/// </summary>
public static class NhAssistantRuleScriptLimits
{
    /// <summary>The longest script a version stores, as the script engine accepts at all.</summary>
    public const int MaxScriptLength = 10_000;

    /// <summary>The most writes one run of a script may make under any ceiling.</summary>
    public const int MaxWritesPerRun = 16;

    /// <summary>The most writing tools one version opts in to.</summary>
    public const int MaxWritesPerVersion = 32;

    /// <summary>The longest reason of a version or a change.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>The most subjects one dry run runs on.</summary>
    public const int MaxDryRunSubjects = 20;

    /// <summary>The shadow runs of one candidate the store keeps, the newest.</summary>
    public const int MaxShadowRunsKept = 20;

    /// <summary>The largest record of one dry run or shadow run, in characters of JSON.</summary>
    public const int MaxRecordCharacters = 200_000;
}

/// <summary>
/// The statuses of a rule script version (RB-2), as data skills have them.
/// </summary>
public static class NhAssistantRuleScriptStatuses
{
    /// <summary>The version in effect: the rule turns of its agent or skill run it instead of a code rule.</summary>
    public const string Live = "live";

    /// <summary>
    /// A version that waits to go live; it runs in shadow next to the live rules when an administrator
    /// tries it in shadow, and records what it would do without effects.
    /// </summary>
    public const string Candidate = "candidate";

    /// <summary>An earlier live version, kept as history.</summary>
    public const string Superseded = "superseded";

    /// <summary>A candidate that was discarded or replaced by a newer one, kept as history.</summary>
    public const string Discarded = "discarded";
}

/// <summary>
/// Who wrote a rule script version (RB-8): a person in the administration, or the agent builder acting for
/// that person.
/// </summary>
/// <param name="ActorId">The person: their actor id, at most 256 characters.</param>
public sealed record NhAssistantRuleScriptAuthor(string ActorId)
{
    /// <summary>
    /// The agent that acted for the person, such as the agent builder; <see langword="null"/> when the
    /// person acted in the administration themselves.
    /// </summary>
    public string? AgentId { get; init; }
}
