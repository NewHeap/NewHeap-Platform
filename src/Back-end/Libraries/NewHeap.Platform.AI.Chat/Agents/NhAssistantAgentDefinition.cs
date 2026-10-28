using System.Text.Json;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// One assistant agent: a versioned identity, a model profile, versioned instructions,
/// the tools it may use and the authorization policy a user needs to see it.
/// </summary>
/// <param name="Id">Lowercase dash-case agent id, unique per application.</param>
/// <param name="Version">Agent contract version recorded in context, proposals and audit.</param>
/// <param name="DisplayNameKey">Front-end translation key of the display name.</param>
/// <param name="DescriptionKey">Front-end translation key of the description.</param>
/// <param name="ProfileName">Registered chat profile that serves the agent.</param>
/// <param name="Instructions">System instructions; id, version and hash flow into context and audit.</param>
/// <param name="ToolSelectors">Globs over <see cref="NhAiToolDescriptor.Id"/>: an exact id or a prefix ending in <c>.*</c>.</param>
/// <param name="Autonomy">Maximum autonomy; only <see cref="NhAiAutonomyLevel.Execute"/> offers mutating tools to the agent's model in its own turns, while its rules and a consultation with a proposal route offer them as proposals from <see cref="NhAiAutonomyLevel.Propose"/> on.</param>
/// <param name="RequiredPolicy">Optional authorization policy a user needs to see and use the agent.</param>
public sealed partial record NhAssistantAgentDefinition(
    string Id,
    int Version,
    string DisplayNameKey,
    string DescriptionKey,
    string ProfileName,
    NhAiTextAsset Instructions,
    IReadOnlyList<string> ToolSelectors,
    NhAiAutonomyLevel Autonomy,
    string? RequiredPolicy = null)
{
    /// <summary>
    /// True when the agent's model may be offered tools that change state in its own turns and runs.
    /// Its rules, whose every effect becomes a proposal, and a consultation with a proposal route
    /// offer them as proposals from <see cref="NhAiAutonomyLevel.Propose"/> on.
    /// </summary>
    public bool CanMutate => Autonomy >= NhAiAutonomyLevel.Execute;

    /// <summary>
    /// The non-human actor id used for this agent in invocation contexts and proposals.
    /// </summary>
    public string ActorId => "assistant-agent:" + Id;

    internal static bool IsValidSelector(string? selector)
    {
        return !string.IsNullOrWhiteSpace(selector)
            && selector.Length <= 128
            && SelectorPattern().IsMatch(selector);
    }

    /// <summary>
    /// The tool selector pattern. Every repetition starts at a literal <c>-</c> or <c>.</c>, so a match is
    /// linear in the input and needs no match timeout; a start-up check never depends on CPU time.
    /// </summary>
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)*(?:\\.\\*)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex SelectorPattern();

    /// <summary>
    /// True when the selectors' stored JSON fits <see cref="NhAssistantLimits.MaxStoredToolSelectorCharacters"/>.
    /// </summary>
    internal static bool FitsSelectorStorage(IEnumerable<string> selectors)
    {
        return JsonSerializer.Serialize(selectors.ToArray()).Length <= NhAssistantLimits.MaxStoredToolSelectorCharacters;
    }

    /// <summary>
    /// Validates the configurable selector count. It runs at host start because
    /// <c>WithLimits</c> may be called after <c>AddAgent</c>.
    /// </summary>
    internal static void ValidateToolSelectorCount(NhAssistantAgentDefinition agent, NhAssistantLimits limits)
    {
        if (agent.ToolSelectors.Count > limits.MaxToolSelectorsPerAgent)
        {
            throw new InvalidOperationException(
                $"Assistant agent '{agent.Id}' has {agent.ToolSelectors.Count} tool selectors; the limit is " +
                $"{limits.MaxToolSelectorsPerAgent}. Raise NhAssistantLimits.MaxToolSelectorsPerAgent through " +
                "WithLimits or combine exact tool ids into prefix selectors.");
        }
    }

    internal static void ValidateShape(NhAssistantAgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        NhAssistantNames.ValidateSegment(agent.Id, nameof(Id));
        NhAssistantNames.ValidateSegment(agent.ProfileName, nameof(ProfileName));
        ArgumentNullException.ThrowIfNull(agent.Instructions);
        ArgumentNullException.ThrowIfNull(agent.ToolSelectors);
        if (agent.Id.Length > 128
            || agent.Version < 1
            || string.IsNullOrWhiteSpace(agent.DisplayNameKey)
            || agent.DisplayNameKey.Length > 256
            || string.IsNullOrWhiteSpace(agent.DescriptionKey)
            || agent.DescriptionKey.Length > 256
            || string.IsNullOrWhiteSpace(agent.Instructions.Content)
            || agent.Instructions.Content.Length > 32_768
            || agent.RequiredPolicy is { Length: 0 or > 256 })
        {
            throw new ArgumentException(
                $"Assistant agent '{agent.Id}' has an invalid or unbounded definition.",
                nameof(agent));
        }
        if (agent.ToolSelectors.Count == 0
            || agent.ToolSelectors.Any(selector => !IsValidSelector(selector))
            || !FitsSelectorStorage(agent.ToolSelectors))
        {
            throw new ArgumentException(
                $"Assistant agent '{agent.Id}' requires a non-empty list of valid tool selectors of at most " +
                $"{NhAssistantLimits.MaxStoredToolSelectorCharacters} stored characters.",
                nameof(agent));
        }
    }

    internal bool IsEquivalentTo(NhAssistantAgentDefinition other)
    {
        return string.Equals(Id, other.Id, StringComparison.Ordinal)
            && Version == other.Version
            && string.Equals(DisplayNameKey, other.DisplayNameKey, StringComparison.Ordinal)
            && string.Equals(DescriptionKey, other.DescriptionKey, StringComparison.Ordinal)
            && string.Equals(ProfileName, other.ProfileName, StringComparison.Ordinal)
            && string.Equals(
                Instructions.Manifest.ContentHash,
                other.Instructions.Manifest.ContentHash,
                StringComparison.Ordinal)
            && Instructions.Manifest.Version == other.Instructions.Manifest.Version
            && ToolSelectors.SequenceEqual(other.ToolSelectors, StringComparer.Ordinal)
            && Autonomy == other.Autonomy
            && string.Equals(RequiredPolicy, other.RequiredPolicy, StringComparison.Ordinal);
    }
}
