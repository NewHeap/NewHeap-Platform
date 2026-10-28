namespace NewHeap.Platform.AI;

/// <summary>
/// The composition feature a governed tool belongs to, as <see cref="NhAiToolDescriptor.CompositionFeature"/>
/// marks it. A tool of a feature runs other governed tools for its caller: a composition runs a known
/// chain of them as one tool, and the script tool runs a short program the model writes over the tools
/// of its turn. Such a tool is offered only where code allows its feature (an agent or an MCP exposure)
/// and the application's switch has the feature on; a call that still arrives while the switch is off
/// is refused with <see cref="NhAiCompositionFeatures.OffCodeOf"/>. Individual tools are never affected.
/// </summary>
public enum NhAiCompositionFeature
{
    /// <summary>
    /// An individual tool, which every rule of compositions and scripts leaves alone. The default.
    /// </summary>
    None = 0,

    /// <summary>
    /// A composition: a known chain of governed tools declared as one tool.
    /// </summary>
    Compositions = 1,

    /// <summary>
    /// The script tool, which runs a short program over the tools of its turn.
    /// </summary>
    Scripts = 2
}

/// <summary>
/// The names and refusal codes of <see cref="NhAiCompositionFeature"/>.
/// </summary>
public static class NhAiCompositionFeatures
{
    /// <summary>
    /// A composition was called while compositions are off for the turn, run step or MCP request;
    /// nothing ran.
    /// </summary>
    public const string CompositionOffCode = "assistant-composition-off";

    /// <summary>
    /// The script tool was called while scripts are off for the turn, run step or MCP request;
    /// nothing ran.
    /// </summary>
    public const string ScriptingOffCode = "assistant-scripting-off";

    /// <summary>
    /// The name of <see cref="NhAiCompositionFeature.Compositions"/> in configuration, JSON and texts.
    /// </summary>
    public const string CompositionsName = "compositions";

    /// <summary>
    /// The name of <see cref="NhAiCompositionFeature.Scripts"/> in configuration, JSON and texts.
    /// </summary>
    public const string ScriptsName = "scripts";

    /// <summary>
    /// Both features, in a fixed order.
    /// </summary>
    public static IReadOnlyList<NhAiCompositionFeature> All { get; } =
    [
        NhAiCompositionFeature.Compositions,
        NhAiCompositionFeature.Scripts
    ];

    /// <summary>
    /// The code that refuses a call of a tool of <paramref name="feature"/> while the feature is off.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="feature"/> is
    /// <see cref="NhAiCompositionFeature.None"/> or unknown.</exception>
    public static string OffCodeOf(NhAiCompositionFeature feature)
    {
        return feature switch
        {
            NhAiCompositionFeature.Compositions => CompositionOffCode,
            NhAiCompositionFeature.Scripts => ScriptingOffCode,
            _ => throw new ArgumentOutOfRangeException(nameof(feature), "Only compositions and scripts can be off.")
        };
    }

    /// <summary>
    /// The name of <paramref name="feature"/>: <see cref="CompositionsName"/> or <see cref="ScriptsName"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="feature"/> is
    /// <see cref="NhAiCompositionFeature.None"/> or unknown.</exception>
    public static string NameOf(NhAiCompositionFeature feature)
    {
        return feature switch
        {
            NhAiCompositionFeature.Compositions => CompositionsName,
            NhAiCompositionFeature.Scripts => ScriptsName,
            _ => throw new ArgumentOutOfRangeException(nameof(feature), "Only compositions and scripts have a name.")
        };
    }
}
