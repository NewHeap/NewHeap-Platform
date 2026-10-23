using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Describes one entry of agent metadata registered with
/// <see cref="NhAssistantBuilder.AddAgentMetadata{TValue}"/>: the largest value, the validators that
/// check it, its searchable value and whether administrators may change it on an agent in code.
/// NewHeap stores and checks metadata, but never interprets it: it is the application's own data about
/// an agent and never widens what the agent may do.
/// </summary>
/// <typeparam name="TValue">The value's type, which is its JSON shape (camelCase).</typeparam>
public sealed class NhAssistantAgentMetadataBuilder<TValue>
    where TValue : notnull
{
    private readonly NhAssistantAgentMetadataEntry _entry;

    internal NhAssistantAgentMetadataBuilder(NhAssistantAgentMetadataEntry entry)
    {
        _entry = entry;
    }

    /// <summary>
    /// The largest value: the bytes of its JSON in UTF-8, from 1 to
    /// <see cref="NhAssistantAgentMetadataLimits.MaxBytes"/>;
    /// <see cref="NhAssistantAgentMetadataLimits.DefaultMaxBytes"/> by default. A larger value is
    /// refused on save with the field error <c>value: too-long</c>, and a larger value in code fails
    /// startup.
    /// </summary>
    public NhAssistantAgentMetadataBuilder<TValue> MaxBytes(int maxBytes)
    {
        if (maxBytes is < 1 or > NhAssistantAgentMetadataLimits.MaxBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes),
                $"An agent metadata entry holds 1 to {NhAssistantAgentMetadataLimits.MaxBytes} bytes.");
        }

        _entry.MaxBytes = maxBytes;
        return this;
    }

    /// <summary>
    /// Checks a value on every save, and every value in code at startup, which then fails. The
    /// validator reports each problem with a dash-case code, of the whole value or of one of its
    /// fields; a save answers them as field errors <c>value</c> or <c>value.&lt;field&gt;</c>. Call it
    /// more than once to add validators; every one runs.
    /// </summary>
    public NhAssistantAgentMetadataBuilder<TValue> Validate(Action<TValue, NhAssistantAgentMetadataErrors> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        _entry.Validators.Add((value, errors) => validate((TValue)value, errors));
        return this;
    }

    /// <summary>
    /// Declares the value's searchable value, such as a code, which is stored in an indexed column on
    /// every save so that <see cref="INhAssistantAgentMetadata.ListAgentsAsync"/> finds the agents
    /// with it. It is trimmed and compared without regard to case; <see langword="null"/> or empty
    /// makes the value unsearchable. A searchable value longer than
    /// <see cref="NhAssistantAgentMetadataLimits.MaxIndexValueLength"/> characters is refused with
    /// <c>value: too-long</c>.
    /// </summary>
    public NhAssistantAgentMetadataBuilder<TValue> IndexBy(Func<TValue, string?> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _entry.IndexSelector = value => selector((TValue)value);
        return this;
    }

    /// <summary>
    /// Lets administrators change the entry of an agent in code: their value is an override with a
    /// version of its own, which wins over the value in code until it is removed. Without it the entry
    /// of an agent in code is read-only (<c>assistant-metadata-read-only</c>). Agents created at
    /// runtime always change their entries, as data of the agent.
    /// </summary>
    public NhAssistantAgentMetadataBuilder<TValue> Editable()
    {
        _entry.Editable = true;
        return this;
    }
}

/// <summary>
/// The problems a validator of agent metadata found in one value, each with a stable, dash-case code
/// such as <c>invalid</c> or <c>unknown-cost-center</c>. Codes are content-free: never put the value
/// itself in them.
/// </summary>
public sealed class NhAssistantAgentMetadataErrors
{
    private const int MaxCodeLength = 64;
    private const int MaxFieldLength = 128;
    private readonly List<(string? Field, string Code)> _errors = [];

    /// <summary>
    /// Whether no problem was reported.
    /// </summary>
    public bool IsEmpty => _errors.Count == 0;

    internal IReadOnlyList<(string? Field, string Code)> Items => _errors;

    /// <summary>
    /// Reports a problem of the whole value, answered as the field error <c>value</c>.
    /// </summary>
    public NhAssistantAgentMetadataErrors Add(string code)
    {
        return Add(null, code);
    }

    /// <summary>
    /// Reports a problem of one field of the value, named by its camelCase path such as <c>code</c> or
    /// <c>owner.email</c>, answered as the field error <c>value.code</c>.
    /// </summary>
    public NhAssistantAgentMetadataErrors Add(string? field, string code)
    {
        if (!NhAssistantNames.IsSegment(code) || code.Length > MaxCodeLength)
        {
            throw new ArgumentException(
                $"A metadata error code is lowercase dash-case of at most {MaxCodeLength} characters.",
                nameof(code));
        }
        if (field is not null && !IsField(field))
        {
            throw new ArgumentException(
                $"A metadata field is a camelCase path of letters, digits and dots of at most {MaxFieldLength} characters.",
                nameof(field));
        }

        if (!_errors.Contains((field, code)))
        {
            _errors.Add((field, code));
        }

        return this;
    }

    private static bool IsField(string field)
    {
        return field.Length is > 0 and <= MaxFieldLength
            && field[0] != '.'
            && field[^1] != '.'
            && !field.Contains("..", StringComparison.Ordinal)
            && field.All(character => character == '.' || char.IsAsciiLetterOrDigit(character));
    }
}

/// <summary>
/// The bounds of agent metadata.
/// </summary>
public static class NhAssistantAgentMetadataLimits
{
    /// <summary>
    /// The largest value of an entry by default, in bytes of its JSON in UTF-8.
    /// </summary>
    public const int DefaultMaxBytes = 4_096;

    /// <summary>
    /// The largest value any entry may hold.
    /// </summary>
    public const int MaxBytes = 65_536;

    /// <summary>
    /// The longest entry name.
    /// </summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// The longest searchable value (<c>IndexBy</c>).
    /// </summary>
    public const int MaxIndexValueLength = 256;

    /// <summary>
    /// The most entries one application registers.
    /// </summary>
    public const int MaxEntries = 50;

    /// <summary>
    /// Entry names that start with this prefix belong to the platform.
    /// </summary>
    public const string ReservedNamePrefix = "nh-";
}

/// <summary>
/// Where the value of an agent's metadata entry comes from.
/// </summary>
public static class NhAssistantAgentMetadataSources
{
    /// <summary>
    /// The value an agent in code sets in <c>ConfigureAgent</c> with <c>Metadata</c>.
    /// </summary>
    public const string Code = "code";

    /// <summary>
    /// An administrator's value for an editable entry of an agent in code, which wins over code.
    /// </summary>
    public const string Override = "override";

    /// <summary>
    /// The value of an agent created at runtime, versioned with the agent.
    /// </summary>
    public const string Data = "data";
}

/// <summary>
/// The application's own data about agents, registered with <c>AddAgentMetadata</c>: read an agent
/// with its metadata and find agents by an entry's searchable value. Administrators and the
/// application change it through <c>INhAssistantAgentConfiguration</c>. Nothing in NewHeap interprets
/// metadata, and it never grants an agent or a person anything: authorize what the application does
/// with it in the application.
/// </summary>
public interface INhAssistantAgentMetadata
{
    /// <summary>
    /// The entries registered with <c>AddAgentMetadata</c>, ordered by name.
    /// </summary>
    IReadOnlyList<NhAssistantAgentMetadataEntryDto> ListEntries();

    /// <summary>
    /// The agent with the metadata in effect, or <see langword="null"/> for an agent that does not
    /// exist. Entries that are no longer registered are left out.
    /// </summary>
    Task<NhAssistantAgentWithMetadata?> GetAgentAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The agents whose entry <paramref name="name"/> has <paramref name="value"/> as its searchable
    /// value (<c>IndexBy</c>), compared trimmed and without regard to case, ordered by id, each with the
    /// metadata in effect. Throws for an entry that is not registered or declares no searchable value.
    /// </summary>
    Task<IReadOnlyList<NhAssistantAgentWithMetadata>> ListAgentsAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default);
}

/// <param name="Name">The entry's dash-case name.</param>
/// <param name="Editable">Administrators may change it on agents in code.</param>
/// <param name="MaxBytes">The largest value in bytes of its JSON.</param>
/// <param name="Indexed">It declares a searchable value with <c>IndexBy</c>.</param>
public sealed record NhAssistantAgentMetadataEntryDto(string Name, bool Editable, int MaxBytes, bool Indexed);

/// <summary>
/// An agent with the metadata in effect.
/// </summary>
/// <param name="AgentId">The agent's id.</param>
/// <param name="Source"><c>code</c> for an agent in code, <c>admin</c> for one created at runtime.</param>
/// <param name="Version">The version of the agent's definition; a change of the metadata of an agent
/// created at runtime makes its next version.</param>
/// <param name="DisplayName">The agent's name as stored: a translation key for an agent in code.</param>
/// <param name="Metadata">The entries that have a value, ordered by name.</param>
public sealed record NhAssistantAgentWithMetadata(
    string AgentId,
    string Source,
    int Version,
    string DisplayName,
    IReadOnlyList<NhAssistantAgentMetadataValue> Metadata)
{
    /// <summary>
    /// The value of entry <paramref name="name"/> as <typeparamref name="TValue"/>, the type it was
    /// registered with; <see langword="default"/> when the agent has none.
    /// </summary>
    public TValue? Get<TValue>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entry = Metadata.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        return entry is null ? default : entry.Value.Deserialize<TValue>(NhAssistantAgentMetadataJson.ReadOptions);
    }
}

/// <summary>
/// The value of one metadata entry of an agent.
/// </summary>
/// <param name="Name">The entry's name.</param>
/// <param name="Value">The value as JSON, in the shape of the entry's type.</param>
/// <param name="Source">A value of <see cref="NhAssistantAgentMetadataSources"/>.</param>
/// <param name="Version">The agent's version that stored it for <c>data</c>, the override's own version
/// for <c>override</c>, <see langword="null"/> for <c>code</c>.</param>
/// <param name="IndexValue">The searchable value as stored: trimmed and in lowercase.</param>
/// <param name="ChangedAt">When an administrator or the application stored it; <see langword="null"/>
/// for code.</param>
/// <param name="ChangedBy">Who stored it; <see langword="null"/> for code.</param>
public sealed record NhAssistantAgentMetadataValue(
    string Name,
    JsonElement Value,
    string Source,
    int? Version,
    string? IndexValue,
    DateTimeOffset? ChangedAt,
    string? ChangedBy);

/// <summary>
/// The JSON of agent metadata: web defaults (camelCase). A value saved must fit its type exactly, so
/// fields the type does not have are refused; a value read ignores them, so a field removed from the
/// type does not break stored values.
/// </summary>
internal static class NhAssistantAgentMetadataJson
{
    public static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// A metadata value of one agent in code, as <c>ConfigureAgent</c> set it, before startup checks it.
/// </summary>
internal sealed record NhAssistantCodeMetadataValue(Type Type, object Value);

/// <summary>
/// A checked metadata value: its JSON as stored, its searchable value and the problems that refuse it.
/// </summary>
internal sealed record NhAssistantAgentMetadataCheck(
    string? Json,
    string? IndexValue,
    IReadOnlyList<(string? Field, string Code)> Errors)
{
    public bool Valid => Errors.Count == 0;

    public static NhAssistantAgentMetadataCheck Refused(string code)
    {
        return new NhAssistantAgentMetadataCheck(null, null, [(null, code)]);
    }

    /// <summary>
    /// Adds the problems as field errors under <paramref name="root"/>, such as <c>value</c> or
    /// <c>metadata.cost-center</c>.
    /// </summary>
    public void AddTo(NhAssistantValidationErrors errors, string root)
    {
        foreach (var (field, code) in Errors)
        {
            errors.Add(field is null ? root : root + "." + field, code);
        }
    }

    /// <summary>
    /// The problems as one line, for a startup failure.
    /// </summary>
    public string Describe()
    {
        return string.Join(", ", Errors.Select(error => (error.Field is null ? "value" : "value." + error.Field) + ": " + error.Code));
    }
}

/// <summary>
/// One entry registered with <c>AddAgentMetadata</c>.
/// </summary>
internal sealed class NhAssistantAgentMetadataEntry(string name, Type valueType)
{
    public string Name { get; } = name;

    public Type ValueType { get; } = valueType;

    public int MaxBytes { get; set; } = NhAssistantAgentMetadataLimits.DefaultMaxBytes;

    public bool Editable { get; set; }

    public Func<object, string?>? IndexSelector { get; set; }

    public List<Action<object, NhAssistantAgentMetadataErrors>> Validators { get; } = [];

    public bool Indexed => IndexSelector is not null;

    public NhAssistantAgentMetadataEntryDto ToDto()
    {
        return new NhAssistantAgentMetadataEntryDto(Name, Editable, MaxBytes, Indexed);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a dash-case entry name of at most
    /// <see cref="NhAssistantAgentMetadataLimits.MaxNameLength"/> characters.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        return name is { Length: <= NhAssistantAgentMetadataLimits.MaxNameLength } && NhAssistantNames.IsSegment(name);
    }

    public static void ValidateName(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        if (!IsValidName(name))
        {
            throw new ArgumentException(
                $"An agent metadata name is lowercase dash-case of at most {NhAssistantAgentMetadataLimits.MaxNameLength} characters.",
                parameterName);
        }
    }

    /// <summary>
    /// Checks a value sent as JSON: it must fit the entry's type exactly, then as
    /// <see cref="CheckValue"/>.
    /// </summary>
    public NhAssistantAgentMetadataCheck Check(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return NhAssistantAgentMetadataCheck.Refused(NhAssistantFieldErrors.Required);
        }

        object? typed;
        try
        {
            typed = value.Deserialize(ValueType, NhAssistantAgentMetadataJson.WriteOptions);
        }
        catch (JsonException)
        {
            return NhAssistantAgentMetadataCheck.Refused(NhAssistantFieldErrors.Invalid);
        }
        catch (NotSupportedException)
        {
            return NhAssistantAgentMetadataCheck.Refused(NhAssistantFieldErrors.Invalid);
        }

        return typed is null
            ? NhAssistantAgentMetadataCheck.Refused(NhAssistantFieldErrors.Required)
            : CheckValue(typed);
    }

    /// <summary>
    /// Checks a typed value as every save and startup do: its JSON fits the size limit, every
    /// validator accepts it and its searchable value fits its column.
    /// </summary>
    public NhAssistantAgentMetadataCheck CheckValue(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ValueType, NhAssistantAgentMetadataJson.WriteOptions);
        if (bytes.Length > MaxBytes)
        {
            return NhAssistantAgentMetadataCheck.Refused(NhAssistantFieldErrors.TooLong);
        }

        var errors = new NhAssistantAgentMetadataErrors();
        foreach (var validate in Validators)
        {
            validate(value, errors);
        }

        var index = IndexSelector?.Invoke(value)?.Trim();
        if (index is { Length: > NhAssistantAgentMetadataLimits.MaxIndexValueLength })
        {
            errors.Add(NhAssistantFieldErrors.TooLong);
        }

        return new NhAssistantAgentMetadataCheck(
            Encoding.UTF8.GetString(bytes),
            NormalizeIndex(index),
            errors.Items.ToArray());
    }

    /// <summary>
    /// A searchable value as it is stored and compared: trimmed and in lowercase, so SQL Server and
    /// PostgreSQL find the same agents whatever their collation.
    /// </summary>
    public static string? NormalizeIndex(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }
}
