using System.Globalization;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Describes one group of agents for people, such as "Project management": its name and an optional
/// description per language. An application shows a group on its own page with the
/// <c>nh-assistant-agent-board</c> component. Agents join it in code with
/// <see cref="NhAssistantAgentRunBuilder.InGroup"/> or in the administration, whose choice wins.
/// </summary>
public sealed class NhAssistantAgentGroupBuilder
{
    private readonly NhAssistantAgentGroupDefinition _group;

    internal NhAssistantAgentGroupBuilder(NhAssistantAgentGroupDefinition group)
    {
        _group = group;
    }

    /// <summary>
    /// The group's name per language, keyed by culture name such as <c>en</c> or <c>nl-NL</c>, at most
    /// 100 characters each. A request in another language reads the name of
    /// <paramref name="fallbackLanguage"/>, which must have one.
    /// </summary>
    public NhAssistantAgentGroupBuilder Named(Dictionary<string, string> names, string fallbackLanguage)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (!NhAssistantSubjectTypeNames.IsLanguage(fallbackLanguage))
        {
            throw new ArgumentException("The fallback language is a culture name such as 'en'.", nameof(fallbackLanguage));
        }

        var validated = NhAssistantSubjectTypeNames.Validate(names, nameof(names));
        if (!validated.ContainsKey(fallbackLanguage))
        {
            throw new ArgumentException(
                $"The names have no text in the fallback language '{fallbackLanguage}'.",
                nameof(fallbackLanguage));
        }

        _group.Names = validated;
        _group.FallbackLanguage = fallbackLanguage;
        return this;
    }

    /// <summary>
    /// What the group's agents do, per language, at most 300 characters each, with the fallback
    /// language of <see cref="Named"/>, which must have a text. Needs <see cref="Named"/>.
    /// </summary>
    public NhAssistantAgentGroupBuilder Describe(Dictionary<string, string> descriptions)
    {
        ArgumentNullException.ThrowIfNull(descriptions);
        _group.Descriptions = NhAssistantSubjectTypeNames.Validate(
            descriptions,
            nameof(descriptions),
            NhAssistantAgentGroupDefinition.MaxDescriptionLength);
        return this;
    }
}

/// <summary>
/// A group of agents in code: its id, its names and descriptions per language.
/// </summary>
internal sealed class NhAssistantAgentGroupDefinition(string id)
{
    /// <summary>
    /// The longest group id.
    /// </summary>
    public const int MaxIdLength = 64;

    /// <summary>
    /// The longest description in one language.
    /// </summary>
    public const int MaxDescriptionLength = 300;

    /// <summary>
    /// The most groups one application registers.
    /// </summary>
    public const int MaxGroups = 100;

    public string Id { get; } = id;

    public IReadOnlyDictionary<string, string>? Names { get; set; }

    public IReadOnlyDictionary<string, string>? Descriptions { get; set; }

    public string? FallbackLanguage { get; set; }

    /// <summary>
    /// Whether <paramref name="groupId"/> is a dash-case group id of at most <see cref="MaxIdLength"/>
    /// characters.
    /// </summary>
    public static bool IsValidId(string? groupId)
    {
        return groupId is { Length: <= MaxIdLength } && NhAiSubjectRef.IsValidType(groupId);
    }

    /// <summary>
    /// Checks what one builder call cannot check by itself: a group has a name, and a description has
    /// a text in the name's fallback language.
    /// </summary>
    public void Validate()
    {
        if (Names is null || FallbackLanguage is null)
        {
            throw new InvalidOperationException($"Agent group '{Id}' has no name; call Named.");
        }
        if (Descriptions is not null && !Descriptions.ContainsKey(FallbackLanguage))
        {
            throw new InvalidOperationException(
                $"The description of agent group '{Id}' has no text in the fallback language '{FallbackLanguage}'.");
        }
    }

    /// <summary>
    /// Whether <paramref name="other"/> is the same group with the same texts.
    /// </summary>
    public bool IsEquivalentTo(NhAssistantAgentGroupDefinition other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Id, other.Id, StringComparison.Ordinal)
            && string.Equals(FallbackLanguage, other.FallbackLanguage, StringComparison.OrdinalIgnoreCase)
            && SameTexts(Names, other.Names)
            && SameTexts(Descriptions, other.Descriptions);
    }

    /// <summary>
    /// The name in <paramref name="culture"/> or the fallback language.
    /// </summary>
    public string NameIn(CultureInfo culture)
    {
        return NhAssistantSubjectTypeNames.Pick(Names!, FallbackLanguage!, culture) ?? Id;
    }

    /// <summary>
    /// The description in <paramref name="culture"/> or the fallback language; <see langword="null"/>
    /// without descriptions.
    /// </summary>
    public string? DescriptionIn(CultureInfo culture)
    {
        return Descriptions is null ? null : NhAssistantSubjectTypeNames.Pick(Descriptions, FallbackLanguage!, culture);
    }

    private static bool SameTexts(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Count == right.Count
            && left.All(entry => right.TryGetValue(entry.Key, out var value) && string.Equals(value, entry.Value, StringComparison.Ordinal));
    }
}
