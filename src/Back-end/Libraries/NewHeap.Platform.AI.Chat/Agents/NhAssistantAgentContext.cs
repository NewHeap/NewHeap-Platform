namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// What every turn of one agent starts with beyond its instructions: the application map,
/// retrieved context and whether compositions and the script tool may be offered. It applies to
/// people's turns, consultations and runs alike. See <see cref="NhAssistantBuilder.ConfigureAgentContext"/>.
/// </summary>
public sealed class NhAssistantAgentContextOptions
{
    internal NhAssistantAgentContextOptions(string agentId)
    {
        AgentId = agentId;
    }

    public string AgentId { get; }

    /// <summary>
    /// Turns start with the application map: the data the acting person may read, listed in turns
    /// that offer a tool that reads it.
    /// </summary>
    public bool UseApplicationMap { get; internal set; }

    /// <summary>
    /// Code allows the agent compositions (<see cref="NhAiCompositionFeature.Compositions"/>): the ceiling
    /// above the application's <see cref="INhAssistantCompositionSwitch"/>, which can only switch them on
    /// for an agent that allows them. False by default.
    /// </summary>
    public bool AllowCompositions { get; internal set; }

    /// <summary>
    /// Code allows the agent the script tool (<see cref="NhAiCompositionFeature.Scripts"/>), as
    /// <see cref="AllowCompositions"/> does for compositions. False by default.
    /// </summary>
    public bool AllowScripts { get; internal set; }

    /// <summary>
    /// Whether code allows the agent <paramref name="feature"/>.
    /// </summary>
    public bool Allows(NhAiCompositionFeature feature)
    {
        return feature switch
        {
            NhAiCompositionFeature.Compositions => AllowCompositions,
            NhAiCompositionFeature.Scripts => AllowScripts,
            _ => false
        };
    }

    /// <summary>
    /// The context sources every turn retrieves from, or <see langword="null"/> without retrieval.
    /// </summary>
    public NhAssistantRetrievedContextOptions? RetrievedContext { get; internal set; }
}

/// <summary>
/// What a turn retrieves: from which sources, how many items and how many characters.
/// </summary>
public sealed class NhAssistantRetrievedContextOptions
{
    private readonly List<string> _sources = [];

    /// <summary>
    /// The ids of the registered context sources (<c>AddContextSource</c>) the turn reads.
    /// </summary>
    public IReadOnlyList<string> Sources => _sources;

    /// <summary>
    /// The most items a turn reads; 8 by default.
    /// </summary>
    public int MaxItems { get; internal set; } = 8;

    /// <summary>
    /// The most characters of the items a turn reads, within
    /// <see cref="NhAssistantLimits.MaxRetrievedCharacters"/>; 6,000 by default.
    /// </summary>
    public int MaxCharacters { get; internal set; } = 6_000;

    internal void AddSources(IEnumerable<string> sources)
    {
        foreach (var source in sources)
        {
            if (!_sources.Contains(source, StringComparer.Ordinal))
            {
                _sources.Add(source);
            }
        }
    }
}

/// <summary>
/// Configures retrieval for the turns of one agent; see
/// <see cref="NhAssistantAgentContextBuilder.UseRetrievedContext"/>.
/// </summary>
public sealed class NhAssistantRetrievedContextBuilder
{
    private readonly NhAssistantRetrievedContextOptions _options;

    internal NhAssistantRetrievedContextBuilder(NhAssistantRetrievedContextOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// The context sources to read, by their descriptor ids. Startup fails on an unknown id.
    /// </summary>
    public NhAssistantRetrievedContextBuilder Sources(params string[] sourceIds)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        foreach (var sourceId in sourceIds)
        {
            NhAssistantNames.ValidateSegment(sourceId, nameof(sourceIds));
        }

        _options.AddSources(sourceIds);
        return this;
    }

    /// <param name="maxItems">1 to 50.</param>
    public NhAssistantRetrievedContextBuilder MaxItems(int maxItems)
    {
        if (maxItems is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "A turn retrieves 1 to 50 items.");
        }

        _options.MaxItems = maxItems;
        return this;
    }

    /// <param name="maxCharacters">500 to 24,000.</param>
    public NhAssistantRetrievedContextBuilder MaxCharacters(int maxCharacters)
    {
        if (maxCharacters is < 500 or > 24_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters), "A turn retrieves 500 to 24,000 characters.");
        }

        _options.MaxCharacters = maxCharacters;
        return this;
    }
}

/// <summary>
/// Configures what every turn of one agent starts with; see
/// <see cref="NhAssistantBuilder.ConfigureAgentContext"/>.
/// </summary>
public sealed class NhAssistantAgentContextBuilder
{
    private readonly NhAssistantAgentContextOptions _options;

    internal NhAssistantAgentContextBuilder(NhAssistantAgentContextOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Starts every turn of the agent with a generated map of the data the acting person may read:
    /// one line per resource with its operations and relations, from the API bridge gateway and the
    /// sources added with <c>AddApplicationMapSource</c>. A resource is listed only in turns that
    /// offer a tool that reads it, within <see cref="NhAssistantLimits.MaxApplicationMapCharacters"/>.
    /// The map follows the instructions as data and is not part of the prompt hash.
    /// </summary>
    public NhAssistantAgentContextBuilder UseApplicationMap()
    {
        _options.UseApplicationMap = true;
        return this;
    }

    /// <summary>
    /// Retrieves context for every turn of the agent once, before its first model call, from the
    /// named sources through the registered context resolver: the query is the person's latest
    /// message, the question of a consultation, or the messages of a run's inputs with the subject's
    /// name. The resolver authorizes every source, drops items outside the turn's execution scopes or
    /// above the classification the model profile permits, ranks and bounds them. The items reach the
    /// model as the last turn data block, marked as untrusted data.
    /// </summary>
    public NhAssistantAgentContextBuilder UseRetrievedContext(Action<NhAssistantRetrievedContextBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var retrieval = _options.RetrievedContext ?? new NhAssistantRetrievedContextOptions();
        configure(new NhAssistantRetrievedContextBuilder(retrieval));
        if (retrieval.Sources.Count == 0)
        {
            throw new InvalidOperationException(
                $"Agent '{_options.AgentId}' retrieves context from no source; name them with Sources.");
        }

        _options.RetrievedContext = retrieval;
        return this;
    }

    /// <summary>
    /// Allows the agent compositions in code: tools marked <see cref="NhAiCompositionFeature.Compositions"/>
    /// that its tool selectors select are offered in its turns, runs, consultations and hand-offs while
    /// the application's <see cref="INhAssistantCompositionSwitch"/> has compositions on for that turn.
    /// Without it no switch offers them to the agent.
    /// </summary>
    public NhAssistantAgentContextBuilder AllowCompositions(bool enabled = true)
    {
        _options.AllowCompositions = enabled;
        return this;
    }

    /// <summary>
    /// Allows the agent the script tool in code: the tool marked <see cref="NhAiCompositionFeature.Scripts"/>
    /// is offered wherever the agent works, without a tool selector of its own, while the application's
    /// <see cref="INhAssistantCompositionSwitch"/> has scripts on for that turn. A script only calls the
    /// tools its turn offers, so it never reaches beyond the agent's tools. Without it no switch offers the
    /// script tool to the agent.
    /// </summary>
    public NhAssistantAgentContextBuilder AllowScripts(bool enabled = true)
    {
        _options.AllowScripts = enabled;
        return this;
    }
}
