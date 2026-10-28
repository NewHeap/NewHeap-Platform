namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The application's ceiling for agents created in the administration
/// (<see cref="NhAssistantBuilder.UseCompositionsAsData"/>): which composition features such an agent
/// may be switched on for. An administrator switches them on per agent within it; the application's
/// <see cref="INhAssistantCompositionSwitch"/> decides on top, per turn.
/// </summary>
public sealed class NhAssistantCompositionCeiling
{
    internal NhAssistantCompositionCeiling()
    {
    }

    /// <summary>
    /// Agents created in the administration may be switched on for compositions.
    /// </summary>
    public bool AllowCompositions { get; internal set; }

    /// <summary>
    /// Agents created in the administration may be switched on for the script tool.
    /// </summary>
    public bool AllowScripts { get; internal set; }

    /// <summary>
    /// Whether the ceiling allows <paramref name="feature"/>.
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
}

/// <summary>
/// Configures <see cref="NhAssistantCompositionCeiling"/>; see <see cref="NhAssistantBuilder.UseCompositionsAsData"/>.
/// </summary>
public sealed class NhAssistantCompositionCeilingBuilder
{
    private readonly NhAssistantCompositionCeiling _ceiling;

    internal NhAssistantCompositionCeilingBuilder(NhAssistantCompositionCeiling ceiling)
    {
        _ceiling = ceiling;
    }

    /// <summary>
    /// Administrators may switch compositions on for an agent created in the administration.
    /// </summary>
    public NhAssistantCompositionCeilingBuilder AllowCompositions(bool enabled = true)
    {
        _ceiling.AllowCompositions = enabled;
        return this;
    }

    /// <summary>
    /// Administrators may switch the script tool on for an agent created in the administration.
    /// </summary>
    public NhAssistantCompositionCeilingBuilder AllowScripts(bool enabled = true)
    {
        _ceiling.AllowScripts = enabled;
        return this;
    }
}

/// <summary>
/// What an agent's own definition allows of compositions and the script tool: for an agent in code
/// what its code allows (<see cref="NhAssistantAgentContextBuilder.AllowCompositions"/>,
/// <see cref="NhAssistantAgentContextBuilder.AllowScripts"/>); for an agent created in the
/// administration the setting an administrator saved with its definition, within
/// <see cref="NhAssistantBuilder.UseCompositionsAsData"/>.
/// </summary>
public sealed record NhAssistantAgentCompositionSettings(bool Compositions, bool Scripts)
{
    /// <summary>
    /// Neither feature: the default of every agent.
    /// </summary>
    public static NhAssistantAgentCompositionSettings None { get; } = new(false, false);

    /// <summary>
    /// Whether the setting allows <paramref name="feature"/>.
    /// </summary>
    public bool Allows(NhAiCompositionFeature feature)
    {
        return feature switch
        {
            NhAiCompositionFeature.Compositions => Compositions,
            NhAiCompositionFeature.Scripts => Scripts,
            _ => false
        };
    }
}
