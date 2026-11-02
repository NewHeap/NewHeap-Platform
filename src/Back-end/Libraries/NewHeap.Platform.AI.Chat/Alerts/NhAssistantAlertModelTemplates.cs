using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The type of one parameter that an agent's model fills when it raises an alert of a kind
/// (<see cref="NhAssistantAlertKindBuilder.RaisedByModel"/>).
/// </summary>
public enum NhAssistantAlertModelParameterType
{
    /// <summary>One value from a fixed list in code.</summary>
    Choice = 0,

    /// <summary>A whole number between bounds in code.</summary>
    WholeNumber = 1,

    /// <summary>A calendar date, written <c>yyyy-MM-dd</c>.</summary>
    Date = 2,

    /// <summary>A short plain text of a bounded length, without links, markup or control characters.</summary>
    Text = 3
}

/// <summary>
/// Why the platform refused a value the model gave for a parameter of an alert's template, in the
/// tool's result next to <see cref="NhAssistantAlertCodes.ParameterInvalid"/>. The value itself is
/// never repeated.
/// </summary>
public static class NhAssistantAlertParameterErrors
{
    /// <summary>The template declares the parameter, and the model gave no value for it.</summary>
    public const string Missing = "missing";

    /// <summary>The template declares no parameter of that name, or the call has an argument the tool does not take.</summary>
    public const string Unknown = "unknown";

    /// <summary>The value is of another JSON type, such as a number for a text.</summary>
    public const string WrongType = "wrong-type";

    /// <summary>The value is none of the choices in code.</summary>
    public const string NotAChoice = "not-a-choice";

    /// <summary>The whole number lies outside its bounds, or the value is no whole number or date at all.</summary>
    public const string OutOfRange = "out-of-range";

    /// <summary>The text is empty once surrounding white space is removed.</summary>
    public const string Empty = "empty";

    /// <summary>The text is longer than the parameter's bound.</summary>
    public const string TooLong = "too-long";

    /// <summary>
    /// The text holds a link: a URL or a scheme such as <c>mailto:</c>, a <c>www.</c> address, an IP
    /// address, or a word with a dot between letters such as <c>example.com</c>, which mail clients and
    /// phones turn into a link.
    /// </summary>
    public const string Link = "link";

    /// <summary>The text holds markup: <c>&lt;</c>, <c>&gt;</c>, brackets, braces, a backtick or an HTML entity.</summary>
    public const string Markup = "markup";

    /// <summary>
    /// The text holds a control, format or private-use character, a line or paragraph separator, or a
    /// broken surrogate pair.
    /// </summary>
    public const string ControlCharacter = "control-character";
}

/// <summary>
/// One parameter of an alert's template that an agent's model fills, as its kind declares it in code.
/// </summary>
public sealed class NhAssistantAlertModelParameter
{
    internal NhAssistantAlertModelParameter(
        string name,
        NhAssistantAlertModelParameterType type,
        string description,
        IReadOnlyList<string> choices,
        int? minimum,
        int? maximum,
        int? maxLength)
    {
        Name = name;
        Type = type;
        Description = description;
        Choices = choices;
        Minimum = minimum;
        Maximum = maximum;
        MaxLength = maxLength;
    }

    /// <summary>The parameter's name, which the kind's title and text read.</summary>
    public string Name { get; }

    public NhAssistantAlertModelParameterType Type { get; }

    /// <summary>What the model writes in it, in the model's instructions.</summary>
    public string Description { get; }

    /// <summary>The values of a <see cref="NhAssistantAlertModelParameterType.Choice"/>, in order; empty otherwise.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>The lowest whole number, inclusive, of a <see cref="NhAssistantAlertModelParameterType.WholeNumber"/>.</summary>
    public int? Minimum { get; }

    /// <summary>The highest whole number, inclusive, of a <see cref="NhAssistantAlertModelParameterType.WholeNumber"/>.</summary>
    public int? Maximum { get; }

    /// <summary>The longest <see cref="NhAssistantAlertModelParameterType.Text"/>, in characters.</summary>
    public int? MaxLength { get; }
}

/// <summary>
/// What an agent's model may fill when it raises an alert of a kind (FB87-2, FB87-9): when the kind
/// applies and its typed parameters, as <see cref="NhAssistantAlertKindBuilder.RaisedByModel"/> declares
/// them. The model fills nothing else: the subject is its run's own, the recipients are the kind's and
/// the key is derived from the kind, the subject and the parameters of <see cref="KeyParameters"/>. The
/// values are untrusted, so the platform checks every one before anything is stored, and the alert is
/// marked as raised by the model.
/// </summary>
public sealed class NhAssistantAlertModelTemplate
{
    /// <summary>What prefixes the key of every alert a model raised, so it never meets the key of an alert of the rules or the application.</summary>
    internal const string KeyPrefix = "model:";

    private readonly Dictionary<string, NhAssistantAlertModelParameter> _byName;

    internal NhAssistantAlertModelTemplate(
        string kindId,
        string description,
        IReadOnlyList<NhAssistantAlertModelParameter> parameters,
        IReadOnlyList<string> keyParameters)
    {
        Description = description;
        Parameters = parameters;
        KeyParameters = keyParameters;
        _byName = parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        ContractHash = HashOf(kindId, description, parameters, keyParameters);
    }

    /// <summary>When the kind applies, in the model's instructions.</summary>
    public string Description { get; }

    /// <summary>The parameters the model fills, each required, in the order they were declared.</summary>
    public IReadOnlyList<NhAssistantAlertModelParameter> Parameters { get; }

    /// <summary>
    /// The parameters whose values tell the kind's alerts about one subject apart, in the order they were
    /// declared; empty for one alert of the kind per subject from the model.
    /// </summary>
    public IReadOnlyList<string> KeyParameters { get; }

    /// <summary>
    /// The SHA-256 of the template's content, lowercase hex: an administrator's switch applies to the
    /// template it was made for, and a changed template reads as off until it is switched again.
    /// </summary>
    internal string ContractHash { get; }

    /// <summary>
    /// Checks the values the model gave and returns them as the alert's parameters, written as the store
    /// keeps them; or the parameter that failed and why (<see cref="NhAssistantAlertParameterErrors"/>).
    /// Every declared parameter needs a value, and no other name is accepted.
    /// </summary>
    internal NhAssistantAlertModelValues Check(JsonElement? values)
    {
        var given = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (values is { } element && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return NhAssistantAlertModelValues.Refused("parameters", NhAssistantAlertParameterErrors.WrongType);
            }

            foreach (var property in element.EnumerateObject())
            {
                if (!_byName.ContainsKey(property.Name) || !given.TryAdd(property.Name, property.Value))
                {
                    return NhAssistantAlertModelValues.Refused(Bounded(property.Name), NhAssistantAlertParameterErrors.Unknown);
                }
            }
        }

        var checkedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in Parameters)
        {
            if (!given.TryGetValue(parameter.Name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return NhAssistantAlertModelValues.Refused(parameter.Name, NhAssistantAlertParameterErrors.Missing);
            }

            var (text, error) = parameter.Type switch
            {
                NhAssistantAlertModelParameterType.Choice => CheckChoice(parameter, value),
                NhAssistantAlertModelParameterType.WholeNumber => CheckWholeNumber(parameter, value),
                NhAssistantAlertModelParameterType.Date => CheckDate(value),
                _ => NhAssistantAlertModelText.Check(value, parameter.MaxLength!.Value)
            };
            if (error is not null)
            {
                return NhAssistantAlertModelValues.Refused(parameter.Name, error);
            }

            checkedValues[parameter.Name] = text!;
        }

        return NhAssistantAlertModelValues.Accepted(checkedValues);
    }

    /// <summary>
    /// The key of the alert the model raises with <paramref name="values"/>: the SHA-256 of the kind, the
    /// subject and the values of <see cref="KeyParameters"/>, after <see cref="KeyPrefix"/>.
    /// </summary>
    internal string KeyOf(string kindId, NhAiSubjectRef subject, IReadOnlyDictionary<string, string> values)
    {
        var material = new StringBuilder()
            .Append(kindId).Append('\n')
            .Append(subject.Type).Append('\n')
            .Append(subject.Id).Append('\n');
        foreach (var name in KeyParameters)
        {
            material.Append(name).Append('=').Append(values[name]).Append('\n');
        }

        return KeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    /// <summary>
    /// The JSON schema of the parameters for the model: every parameter required and typed, nothing else.
    /// </summary>
    internal Dictionary<string, object?> SchemaOf()
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in Parameters)
        {
            properties[parameter.Name] = parameter.Type switch
            {
                NhAssistantAlertModelParameterType.Choice => new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = parameter.Choices,
                    ["description"] = parameter.Description
                },
                NhAssistantAlertModelParameterType.WholeNumber => new Dictionary<string, object?>
                {
                    ["type"] = "integer",
                    ["minimum"] = parameter.Minimum,
                    ["maximum"] = parameter.Maximum,
                    ["description"] = parameter.Description
                },
                NhAssistantAlertModelParameterType.Date => new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["format"] = "date",
                    ["description"] = parameter.Description + " A date as yyyy-MM-dd."
                },
                _ => new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["maxLength"] = parameter.MaxLength,
                    ["description"] = parameter.Description + " Plain text only: no links, web addresses or markup."
                }
            };
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = Parameters.Select(parameter => parameter.Name).ToArray(),
            ["additionalProperties"] = false
        };
    }

    private static (string? Value, string? Error) CheckChoice(NhAssistantAlertModelParameter parameter, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return (null, NhAssistantAlertParameterErrors.WrongType);
        }

        var text = value.GetString()!;
        return parameter.Choices.Contains(text, StringComparer.Ordinal)
            ? (text, null)
            : (null, NhAssistantAlertParameterErrors.NotAChoice);
    }

    private static (string? Value, string? Error) CheckWholeNumber(NhAssistantAlertModelParameter parameter, JsonElement value)
    {
        long number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out number))
            {
                // 3.0 is a whole number; 3.5 and 1e40 are not.
                if (!value.TryGetDecimal(out var exact) || exact != decimal.Truncate(exact) || exact is < long.MinValue or > long.MaxValue)
                {
                    return (null, NhAssistantAlertParameterErrors.OutOfRange);
                }

                number = (long)exact;
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            // Models sometimes write a number as text; only plain digits count.
            if (!long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
            {
                return (null, NhAssistantAlertParameterErrors.WrongType);
            }
        }
        else
        {
            return (null, NhAssistantAlertParameterErrors.WrongType);
        }

        if (number < parameter.Minimum!.Value || number > parameter.Maximum!.Value)
        {
            return (null, NhAssistantAlertParameterErrors.OutOfRange);
        }

        return (number.ToString(CultureInfo.InvariantCulture), null);
    }

    private static (string? Value, string? Error) CheckDate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return (null, NhAssistantAlertParameterErrors.WrongType);
        }

        return DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null)
            : (null, NhAssistantAlertParameterErrors.OutOfRange);
    }

    /// <summary>
    /// A name the model wrote, bounded for the tool's result; the platform's own names are short.
    /// </summary>
    private static string Bounded(string name)
    {
        var printable = new string(name.Where(character => !char.IsControl(character)).Take(NhAssistantAlertLimits.MaxParameterNameLength).ToArray());
        return printable.Length == 0 ? "?" : printable;
    }

    private static string HashOf(
        string kindId,
        string description,
        IReadOnlyList<NhAssistantAlertModelParameter> parameters,
        IReadOnlyList<string> keyParameters)
    {
        var material = JsonSerializer.Serialize(new
        {
            kind = kindId,
            description,
            parameters = parameters.Select(parameter => new
            {
                name = parameter.Name,
                type = parameter.Type.ToString(),
                description = parameter.Description,
                choices = parameter.Choices,
                minimum = parameter.Minimum,
                maximum = parameter.Maximum,
                maxLength = parameter.MaxLength
            }),
            key = keyParameters
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

/// <summary>
/// Declares what an agent's model fills when it raises an alert of a kind; see
/// <see cref="NhAssistantAlertKindBuilder.RaisedByModel"/>.
/// </summary>
public sealed partial class NhAssistantAlertModelTemplateBuilder
{
    private readonly string _kindId;
    private readonly List<NhAssistantAlertModelParameter> _parameters = [];
    private string? _description;
    private IReadOnlyList<string> _key = [];

    internal NhAssistantAlertModelTemplateBuilder(string kindId)
    {
        _kindId = kindId;
    }

    /// <summary>
    /// When the kind applies, for the model's instructions, at most
    /// <see cref="NhAssistantAlertLimits.MaxModelDescriptionLength"/> characters, such as "A document of
    /// the project has its deadline within a week and its work is not ready." Required.
    /// </summary>
    public NhAssistantAlertModelTemplateBuilder Describe(string whenToRaise)
    {
        _description = ValidateDescription(whenToRaise, nameof(whenToRaise));
        return this;
    }

    /// <summary>
    /// A parameter whose value is one of <paramref name="choices"/>, compared exactly; each choice at most
    /// <see cref="NhAssistantAlertLimits.MaxModelChoiceLength"/> printable characters, at most
    /// <see cref="NhAssistantAlertLimits.MaxModelChoices"/> of them.
    /// </summary>
    public NhAssistantAlertModelTemplateBuilder Choice(string name, string description, params string[] choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Length == 0 || choices.Length > NhAssistantAlertLimits.MaxModelChoices)
        {
            throw new ArgumentException(
                $"A choice parameter has one to {NhAssistantAlertLimits.MaxModelChoices} choices.",
                nameof(choices));
        }
        foreach (var choice in choices)
        {
            if (string.IsNullOrWhiteSpace(choice)
                || choice.Length > NhAssistantAlertLimits.MaxModelChoiceLength
                || choice.Trim() != choice
                || choice.Any(character => char.IsControl(character)))
            {
                throw new ArgumentException(
                    $"A choice has at most {NhAssistantAlertLimits.MaxModelChoiceLength} printable characters without surrounding white space.",
                    nameof(choices));
            }
        }
        if (choices.Distinct(StringComparer.Ordinal).Count() != choices.Length)
        {
            throw new ArgumentException("Every choice is listed once.", nameof(choices));
        }

        return Add(name, NhAssistantAlertModelParameterType.Choice, description, [.. choices], null, null, null);
    }

    /// <summary>
    /// A parameter whose value is a whole number from <paramref name="minimum"/> to <paramref name="maximum"/>, inclusive.
    /// </summary>
    public NhAssistantAlertModelTemplateBuilder WholeNumber(string name, string description, int minimum, int maximum)
    {
        if (minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "The highest whole number is at least the lowest.");
        }

        return Add(name, NhAssistantAlertModelParameterType.WholeNumber, description, [], minimum, maximum, null);
    }

    /// <summary>
    /// A parameter whose value is a calendar date, which the model writes as <c>yyyy-MM-dd</c> and the
    /// kind's texts read in the same form.
    /// </summary>
    public NhAssistantAlertModelTemplateBuilder Date(string name, string description)
    {
        return Add(name, NhAssistantAlertModelParameterType.Date, description, [], null, null, null);
    }

    /// <summary>
    /// A parameter whose value is a short plain text of at most <paramref name="maxLength"/> characters,
    /// from 1 to <see cref="NhAssistantAlertLimits.MaxModelTextLength"/>, such as a document's name. The
    /// platform refuses a text with a link, a web address, markup or a control character
    /// (<see cref="NhAssistantAlertParameterErrors"/>), because the model may have read it from content an
    /// attacker wrote. Prefer a <see cref="Choice"/> or a <see cref="WholeNumber"/> where they fit.
    /// </summary>
    public NhAssistantAlertModelTemplateBuilder Text(string name, string description, int maxLength)
    {
        if (maxLength < 1 || maxLength > NhAssistantAlertLimits.MaxModelTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLength),
                $"A text parameter has at most 1 to {NhAssistantAlertLimits.MaxModelTextLength} characters.");
        }

        return Add(name, NhAssistantAlertModelParameterType.Text, description, [], null, null, maxLength);
    }

    /// <summary>
    /// The parameters whose values tell the kind's alerts about one subject apart, such as a deadline:
    /// the platform derives the key from the kind, the subject and these values, so the same values
    /// update the same open alert and other values raise another. Without it the model raises one alert
    /// of the kind per subject. A person who dismissed an alert gets it again only once these values change.
    /// </summary>
    public NhAssistantAlertModelTemplateBuilder Key(params string[] parameterNames)
    {
        ArgumentNullException.ThrowIfNull(parameterNames);
        if (parameterNames.Distinct(StringComparer.Ordinal).Count() != parameterNames.Length)
        {
            throw new ArgumentException("A key names every parameter once.", nameof(parameterNames));
        }

        _key = [.. parameterNames];
        return this;
    }

    internal NhAssistantAlertModelTemplate Build()
    {
        if (_description is null)
        {
            throw new InvalidOperationException($"Alert kind '{_kindId}' is raised by a model, so its template needs Describe(...).");
        }

        var names = _parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in _key.Where(name => !names.Contains(name)))
        {
            throw new InvalidOperationException(
                $"The key of alert kind '{_kindId}' names parameter '{name}', which its template does not declare.");
        }

        // The key parameters in the order of the declaration, so the derived key does not depend on how Key was called.
        var key = _parameters.Select(parameter => parameter.Name).Where(name => _key.Contains(name, StringComparer.Ordinal)).ToArray();
        return new NhAssistantAlertModelTemplate(_kindId, _description, [.. _parameters], key);
    }

    private NhAssistantAlertModelTemplateBuilder Add(
        string name,
        NhAssistantAlertModelParameterType type,
        string description,
        IReadOnlyList<string> choices,
        int? minimum,
        int? maximum,
        int? maxLength)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length > NhAssistantAlertLimits.MaxParameterNameLength || !ParameterName().IsMatch(name))
        {
            throw new ArgumentException(
                $"An alert parameter name is a letter followed by letters, digits, '-' or '_', at most {NhAssistantAlertLimits.MaxParameterNameLength} characters.",
                nameof(name));
        }
        if (_parameters.Any(parameter => parameter.Name == name))
        {
            throw new ArgumentException($"Alert kind '{_kindId}' declares parameter '{name}' twice.", nameof(name));
        }
        if (_parameters.Count >= NhAssistantAlertLimits.MaxParameters)
        {
            throw new InvalidOperationException($"An alert's template has at most {NhAssistantAlertLimits.MaxParameters} parameters.");
        }

        _parameters.Add(new NhAssistantAlertModelParameter(
            name,
            type,
            ValidateDescription(description, nameof(description)),
            choices,
            minimum,
            maximum,
            maxLength));
        return this;
    }

    private static string ValidateDescription(string description, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description, parameterName);
        var trimmed = description.Trim();
        if (trimmed.Length > NhAssistantAlertLimits.MaxModelDescriptionLength || trimmed.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                $"A description for the model has at most {NhAssistantAlertLimits.MaxModelDescriptionLength} characters on one line.",
                parameterName);
        }

        return trimmed;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterName();
}

/// <summary>
/// The values of an alert's template as the platform checked them: the parameters to store, or the
/// parameter that failed and why.
/// </summary>
internal sealed record NhAssistantAlertModelValues(
    IReadOnlyDictionary<string, string>? Values,
    string? Parameter,
    string? Error)
{
    public bool Success => Values is not null;

    public static NhAssistantAlertModelValues Accepted(IReadOnlyDictionary<string, string> values)
    {
        return new NhAssistantAlertModelValues(values, null, null);
    }

    public static NhAssistantAlertModelValues Refused(string parameter, string error)
    {
        return new NhAssistantAlertModelValues(null, parameter, error);
    }
}

/// <summary>
/// The check of a text the model wrote for an alert (FB87-9): plain text only. Mail clients, chat
/// applications and phones turn web addresses and words such as <c>example.com</c> into links, and a
/// channel of the application may render markup, so the platform refuses both, with control and format
/// characters that could hide or reorder what people read. The text is checked in its compatibility form,
/// so look-alike characters such as a full-width dot do not slip through.
/// </summary>
internal static partial class NhAssistantAlertModelText
{
    /// <summary>
    /// The text to store, trimmed and in Unicode normalization form C; or why it is refused.
    /// </summary>
    public static (string? Value, string? Error) Check(JsonElement value, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return (null, NhAssistantAlertParameterErrors.WrongType);
        }

        return Check(value.GetString()!, maxLength);
    }

    public static (string? Value, string? Error) Check(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (HasControlCharacter(value))
        {
            return (null, NhAssistantAlertParameterErrors.ControlCharacter);
        }

        var text = value.Trim().Normalize(NormalizationForm.FormC);
        if (text.Length == 0)
        {
            return (null, NhAssistantAlertParameterErrors.Empty);
        }
        if (text.Length > maxLength)
        {
            return (null, NhAssistantAlertParameterErrors.TooLong);
        }

        var compatible = text.Normalize(NormalizationForm.FormKC)
            .Replace('。', '.')
            .Replace('｡', '.')
            .ToLowerInvariant();
        if (compatible.IndexOfAny(['<', '>', '[', ']', '{', '}', '`']) >= 0 || Entity().IsMatch(compatible))
        {
            return (null, NhAssistantAlertParameterErrors.Markup);
        }
        if (compatible.Contains("://", StringComparison.Ordinal)
            || compatible.Contains("www.", StringComparison.Ordinal)
            || Scheme().IsMatch(compatible)
            || IpAddress().IsMatch(compatible)
            || DomainLike().IsMatch(compatible))
        {
            return (null, NhAssistantAlertParameterErrors.Link);
        }

        return (text, null);
    }

    private static bool HasControlCharacter(string value)
    {
        for (var index = 0; index < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, index, out var rune))
            {
                // A broken surrogate pair.
                return true;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned)
            {
                return true;
            }

            index += rune.Utf16SequenceLength;
        }

        return false;
    }

    [GeneratedRegex(@"&(#x?[0-9a-f]+|[a-z][a-z0-9]*);", RegexOptions.CultureInvariant)]
    private static partial Regex Entity();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(https?|s?ftps?|file|mailto|tel|sms|callto|javascript|vbscript|data|blob|about|wss?|ssh|git|irc|news|urn|magnet|intent|market)\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex Scheme();

    [GeneratedRegex(@"(?<![\p{N}.])\d{1,3}(\.\d{1,3}){3}(?![\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex IpAddress();

    /// <summary>
    /// A word with a dot between it and a label that starts with a letter, such as <c>example.com</c>,
    /// <c>mail.example.org</c> or <c>report.pdf</c>; not <c>v1.2</c>, <c>e.g.</c> or <c>3.5</c>.
    /// </summary>
    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_-]*\.\p{L}[\p{L}\p{N}-]+", RegexOptions.CultureInvariant)]
    private static partial Regex DomainLike();
}
