using System.Globalization;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Describes one subject type for people, next to its access resolver: its name in each language,
/// the application entity it stands for and where the application shows one subject. Every part is
/// optional; without <see cref="Named"/> people see the type id.
/// </summary>
public sealed class NhAssistantSubjectTypeBuilder
{
    private readonly NhAssistantSubjectTypeOptions _options;

    internal NhAssistantSubjectTypeBuilder(NhAssistantSubjectTypeOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// The name of one subject per language, keyed by culture name such as <c>en</c> or
    /// <c>nl-NL</c>, at most 100 characters each. A request in another language reads the name of
    /// <paramref name="fallbackLanguage"/>, which must have one.
    /// </summary>
    public NhAssistantSubjectTypeBuilder Named(Dictionary<string, string> names, string fallbackLanguage)
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

        _options.Names = validated;
        _options.FallbackLanguage = fallbackLanguage;
        return this;
    }

    /// <summary>
    /// The name of several subjects per language, such as "Projects", with the fallback language of
    /// <see cref="Named"/>. Needs <see cref="Named"/>.
    /// </summary>
    public NhAssistantSubjectTypeBuilder NamedPlural(Dictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _options.PluralNames = NhAssistantSubjectTypeNames.Validate(names, nameof(names));
        return this;
    }

    /// <summary>
    /// Binds the subject type to the application entity it stands for, recorded as its short and full
    /// type name like the object types of the NewHeap log, so logs, audit and the assistant point at
    /// the same object.
    /// </summary>
    public NhAssistantSubjectTypeBuilder ForEntity<TEntity>()
        where TEntity : class
    {
        _options.EntityType = typeof(TEntity).Name;
        _options.EntityTypeFull = typeof(TEntity).FullName ?? typeof(TEntity).Name;
        return this;
    }

    /// <summary>
    /// Where the application shows one subject: a path within the application such as
    /// <c>/projects/{id}</c>, or an absolute <c>https</c> URL, with exactly one <c>{id}</c> in its
    /// path or query. The subject id replaces it URL-encoded. Screens link to the subject and an
    /// Angular host may replace the link with a router function.
    /// </summary>
    public NhAssistantSubjectTypeBuilder OpenAt(string linkTemplate)
    {
        if (!NhAssistantSubjectLinks.IsValidTemplate(linkTemplate))
        {
            throw new ArgumentException(
                "A subject link is a path that starts with one '/' or an absolute https URL, with exactly one {id} in its path or query and no other braces.",
                nameof(linkTemplate));
        }

        _options.LinkTemplate = linkTemplate;
        return this;
    }
}

/// <summary>
/// Marks the access resolver of a subject type the platform registers itself, such as the whole
/// application. Only such a resolver may handle a type in the reserved <c>nh-</c> prefix.
/// </summary>
internal interface INhAssistantPlatformSubjectResolver : INhAiSubjectAccessResolver;

/// <summary>
/// How people see the platform's subject type of the whole application.
/// </summary>
internal static class NhAssistantSystemSubjectType
{
    public const string FallbackLanguage = "en";

    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "The whole application",
        ["nl"] = "De hele applicatie",
        ["de"] = "Die gesamte Anwendung"
    };

    /// <summary>
    /// "The whole application" in <paramref name="culture"/>, English for a language the platform
    /// does not ship.
    /// </summary>
    public static string NameIn(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return NhAssistantSubjectTypeNames.Pick(Names, FallbackLanguage, culture) ?? Names[FallbackLanguage];
    }

    /// <summary>
    /// The type's configuration for the registration, named in the languages the platform ships.
    /// </summary>
    public static NhAssistantSubjectTypeOptions CreateOptions(Type resolverType)
    {
        ArgumentNullException.ThrowIfNull(resolverType);
        var options = new NhAssistantSubjectTypeOptions(resolverType);
        new NhAssistantSubjectTypeBuilder(options).Named(new Dictionary<string, string>(Names), FallbackLanguage);
        return options;
    }
}

/// <summary>
/// The configuration of one subject type in code, registered with its resolver class.
/// </summary>
internal sealed class NhAssistantSubjectTypeOptions(Type resolverType)
{
    public Type ResolverType { get; } = resolverType;

    public IReadOnlyDictionary<string, string>? Names { get; set; }

    public IReadOnlyDictionary<string, string>? PluralNames { get; set; }

    public string? FallbackLanguage { get; set; }

    public string? EntityType { get; set; }

    public string? EntityTypeFull { get; set; }

    public string? LinkTemplate { get; set; }

    /// <summary>
    /// Whether the registration configured anything; a resolver added without a configuration has
    /// none.
    /// </summary>
    public bool IsConfigured => Names is not null
        || PluralNames is not null
        || EntityType is not null
        || LinkTemplate is not null;

    /// <summary>
    /// Checks what one builder cannot check by itself: a plural needs a name and the fallback
    /// language needs a plural too.
    /// </summary>
    public void Validate()
    {
        if (PluralNames is null)
        {
            return;
        }

        if (Names is null || FallbackLanguage is null)
        {
            throw new InvalidOperationException(
                $"The subject type of {ResolverType.Name} has a plural name but no name; call Named before NamedPlural.");
        }
        if (!PluralNames.ContainsKey(FallbackLanguage))
        {
            throw new InvalidOperationException(
                $"The plural name of the subject type of {ResolverType.Name} has no text in the fallback language '{FallbackLanguage}'.");
        }
    }

    public bool IsEquivalentTo(NhAssistantSubjectTypeOptions other)
    {
        return ResolverType == other.ResolverType
            && SameNames(Names, other.Names)
            && SameNames(PluralNames, other.PluralNames)
            && string.Equals(FallbackLanguage, other.FallbackLanguage, StringComparison.OrdinalIgnoreCase)
            && string.Equals(EntityType, other.EntityType, StringComparison.Ordinal)
            && string.Equals(EntityTypeFull, other.EntityTypeFull, StringComparison.Ordinal)
            && string.Equals(LinkTemplate, other.LinkTemplate, StringComparison.Ordinal);
    }

    private static bool SameNames(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Count == right.Count
            && left.All(entry => right.TryGetValue(entry.Key, out var value) && string.Equals(value, entry.Value, StringComparison.Ordinal));
    }
}

/// <summary>
/// One subject type the application registered: its id, the resolver that answers for it and how
/// people see it.
/// </summary>
internal sealed class NhAssistantSubjectType
{
    private readonly NhAssistantSubjectTypeOptions? _options;

    public NhAssistantSubjectType(string id, Type resolverType, NhAssistantSubjectTypeOptions? options, bool searchable, bool isSystem = false)
    {
        Id = id;
        ResolverType = resolverType;
        _options = options;
        Searchable = searchable;
        IsSystem = isSystem;
    }

    public string Id { get; }

    public Type ResolverType { get; }

    /// <summary>
    /// The resolver also implements <see cref="INhAiSubjectSearch"/>.
    /// </summary>
    public bool Searchable { get; }

    /// <summary>
    /// The type belongs to the platform, such as the whole application.
    /// </summary>
    public bool IsSystem { get; }

    /// <summary>
    /// The application named the type; without names people see its id.
    /// </summary>
    public bool IsNamed => _options?.Names is not null;

    public string? EntityType => _options?.EntityType;

    public string? EntityTypeFull => _options?.EntityTypeFull;

    public string? LinkTemplate => _options?.LinkTemplate;

    /// <summary>
    /// The name of one subject in <paramref name="culture"/> or the fallback language; the type id
    /// without names.
    /// </summary>
    public string NameIn(CultureInfo culture)
    {
        return _options?.Names is { } names
            ? NhAssistantSubjectTypeNames.Pick(names, _options.FallbackLanguage!, culture) ?? Id
            : Id;
    }

    /// <summary>
    /// The name of several subjects in <paramref name="culture"/> or the fallback language;
    /// <see langword="null"/> without plural names.
    /// </summary>
    public string? PluralNameIn(CultureInfo culture)
    {
        return _options?.PluralNames is { } names
            ? NhAssistantSubjectTypeNames.Pick(names, _options.FallbackLanguage!, culture)
            : null;
    }

    /// <summary>
    /// The link to one subject of this type, or <see langword="null"/> without a link template.
    /// </summary>
    public string? LinkFor(string subjectId)
    {
        return LinkTemplate is null ? null : NhAssistantSubjectLinks.Build(LinkTemplate, subjectId);
    }

    /// <summary>
    /// The name people see of one subject: the whole application app-wide carries the type's name in
    /// <paramref name="culture"/>, every other subject the name its thread stored.
    /// </summary>
    public string? DisplayNameOf(string subjectId, string? storedName, CultureInfo culture)
    {
        return IsSystem && string.Equals(subjectId, NhAiSystemSubjects.GlobalId, StringComparison.Ordinal)
            ? NameIn(culture)
            : storedName;
    }
}

internal static partial class NhAssistantSubjectTypeNames
{
    public const int MaxNameLength = 100;
    private const int MaxLanguageLength = 35;

    public static bool IsLanguage(string? language)
    {
        return !string.IsNullOrEmpty(language)
            && language.Length <= MaxLanguageLength
            && LanguagePattern().IsMatch(language);
    }

    public static IReadOnlyDictionary<string, string> Validate(Dictionary<string, string> names, string parameterName)
    {
        if (names.Count == 0)
        {
            throw new ArgumentException("Give a name in at least one language.", parameterName);
        }

        var validated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, name) in names)
        {
            if (!IsLanguage(language))
            {
                throw new ArgumentException($"'{language}' is not a culture name such as 'en' or 'nl-NL'.", parameterName);
            }
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
            {
                throw new ArgumentException(
                    $"The name in '{language}' must contain 1 to {MaxNameLength} characters.",
                    parameterName);
            }
            if (!validated.TryAdd(language, name.Trim()))
            {
                throw new ArgumentException($"The language '{language}' is given twice.", parameterName);
            }
        }

        return validated;
    }

    /// <summary>
    /// The name in the culture, its parent cultures or another culture of the same language, else
    /// in the fallback language.
    /// </summary>
    public static string? Pick(IReadOnlyDictionary<string, string> names, string fallbackLanguage, CultureInfo culture)
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (names.TryGetValue(current.Name, out var name))
            {
                return name;
            }
        }

        var language = culture.TwoLetterISOLanguageName;
        var sameLanguage = names
            .Where(entry => entry.Key.Split('-')[0].Equals(language, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Value)
            .FirstOrDefault();
        if (sameLanguage is not null)
        {
            return sameLanguage;
        }

        return names.TryGetValue(fallbackLanguage, out var fallback) ? fallback : null;
    }

    [GeneratedRegex("^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();
}

internal static class NhAssistantSubjectLinks
{
    public const string Placeholder = "{id}";
    public const int MaxTemplateLength = 2_000;

    public static bool IsValidTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)
            || template.Length > MaxTemplateLength
            || template.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return false;
        }

        var placeholder = template.IndexOf(Placeholder, StringComparison.Ordinal);
        if (placeholder < 0
            || template.IndexOf(Placeholder, placeholder + Placeholder.Length, StringComparison.Ordinal) >= 0
            || template.Replace(Placeholder, string.Empty, StringComparison.Ordinal).IndexOfAny(['{', '}']) >= 0)
        {
            return false;
        }

        if (template.StartsWith('/'))
        {
            // One leading slash: "//host" and "/\host" would leave the application.
            return template.Length > 1 && template[1] is not ('/' or '\\');
        }

        // An absolute link names its host itself; the id only fills its path or query.
        if (!Uri.TryCreate(template.Replace(Placeholder, "x", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var authorityEnd = template.IndexOf('/', "https://".Length);
        return authorityEnd > 0 && placeholder > authorityEnd;
    }

    public static string Build(string template, string subjectId)
    {
        return template.Replace(Placeholder, Uri.EscapeDataString(subjectId), StringComparison.Ordinal);
    }
}
