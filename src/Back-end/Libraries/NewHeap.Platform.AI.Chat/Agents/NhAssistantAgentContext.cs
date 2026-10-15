namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// What every turn of one agent starts with beyond its instructions: the application map and
/// retrieved context. It applies to people's turns, consultations and runs alike. See
/// <see cref="NhAssistantBuilder.ConfigureAgentContext"/>.
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
}
