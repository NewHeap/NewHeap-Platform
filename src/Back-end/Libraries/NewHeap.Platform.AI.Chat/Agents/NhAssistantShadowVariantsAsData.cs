namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The ceiling in code of shadow variants that administrators create as data: which live agents may
/// get them and how many each. A data shadow variant is an agent created in the administration that
/// names its live agent when it is created: every trigger of the live agent also fires for it, and it
/// always works in shadow. See <see cref="NhAssistantBuilder.UseShadowVariantsAsData"/>.
/// </summary>
public sealed class NhAssistantShadowVariantCeiling
{
    /// <summary>
    /// The most data variants one live agent may have, by default.
    /// </summary>
    public const int DefaultMaxPerAgent = 3;

    /// <summary>
    /// The highest <see cref="MaxPerAgent"/>.
    /// </summary>
    public const int MaxVariantsPerAgent = 10;

    private readonly List<string> _liveAgents = [];

    internal NhAssistantShadowVariantCeiling()
    {
    }

    /// <summary>
    /// The live agents in code that may get data variants.
    /// </summary>
    public IReadOnlyList<string> LiveAgents => _liveAgents;

    /// <summary>
    /// Every agent created in the administration with run settings may get data variants too.
    /// </summary>
    public bool OfAgentsCreatedInAdministration { get; internal set; }

    /// <summary>
    /// The most data variants one live agent may have, 1 to <see cref="MaxVariantsPerAgent"/>.
    /// </summary>
    public int MaxPerAgent { get; internal set; } = DefaultMaxPerAgent;

    /// <summary>
    /// Whether <paramref name="liveAgentId"/> may get data variants; <paramref name="createdInAdministration"/>
    /// tells whether it was created in the administration.
    /// </summary>
    public bool Allows(string liveAgentId, bool createdInAdministration)
    {
        return _liveAgents.Contains(liveAgentId, StringComparer.Ordinal)
               || (OfAgentsCreatedInAdministration && createdInAdministration);
    }

    internal void AddLiveAgents(IEnumerable<string> agentIds)
    {
        foreach (var agentId in agentIds.Where(agentId => !_liveAgents.Contains(agentId, StringComparer.Ordinal)))
        {
            _liveAgents.Add(agentId);
        }
    }
}

/// <summary>
/// Configures the ceiling of shadow variants created as data; see
/// <see cref="NhAssistantBuilder.UseShadowVariantsAsData"/>.
/// </summary>
public sealed class NhAssistantShadowVariantCeilingBuilder
{
    private readonly NhAssistantShadowVariantCeiling _ceiling;

    internal NhAssistantShadowVariantCeilingBuilder(NhAssistantShadowVariantCeiling ceiling)
    {
        _ceiling = ceiling;
    }

    /// <summary>
    /// Live agents in code that may get data variants. Startup fails for an agent that is not
    /// configured with <c>ConfigureAgent</c>, starts in shadow or is a shadow variant itself, as it
    /// does for <c>ShadowOf</c>.
    /// </summary>
    public NhAssistantShadowVariantCeilingBuilder Of(params string[] liveAgentIds)
    {
        ArgumentNullException.ThrowIfNull(liveAgentIds);
        if (liveAgentIds.Length == 0)
        {
            throw new ArgumentException("Name at least one agent.", nameof(liveAgentIds));
        }
        foreach (var agentId in liveAgentIds)
        {
            NhAssistantNames.ValidateSegment(agentId, nameof(liveAgentIds));
        }

        _ceiling.AddLiveAgents(liveAgentIds);
        return this;
    }

    /// <summary>
    /// Every agent created in the administration with run settings may get data variants.
    /// </summary>
    public NhAssistantShadowVariantCeilingBuilder OfAgentsCreatedInAdministration()
    {
        _ceiling.OfAgentsCreatedInAdministration = true;
        return this;
    }

    /// <param name="maxPerAgent">The most data variants one live agent may have, 1 to 10; 3 by default.</param>
    public NhAssistantShadowVariantCeilingBuilder MaxPerAgent(int maxPerAgent)
    {
        if (maxPerAgent is < 1 or > NhAssistantShadowVariantCeiling.MaxVariantsPerAgent)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPerAgent), "Allow 1 to 10 shadow variants per agent.");
        }

        _ceiling.MaxPerAgent = maxPerAgent;
        return this;
    }
}

/// <summary>
/// Where a shadow variant comes from, as the administration lists them.
/// </summary>
public static class NhAssistantShadowVariantSources
{
    /// <summary>Configured in code with <c>ShadowOf</c>.</summary>
    public const string Code = "code";

    /// <summary>Created in the administration, within <c>UseShadowVariantsAsData</c>.</summary>
    public const string Data = "data";
}
