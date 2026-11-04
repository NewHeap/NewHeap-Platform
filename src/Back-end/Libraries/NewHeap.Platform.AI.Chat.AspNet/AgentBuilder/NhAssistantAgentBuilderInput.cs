using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// The input of one call of an agent builder tool, as the model sent it. It is untrusted, so it is checked
/// against the tool's schema before the call becomes a proposal: a property the tool does not take, a
/// missing required property, <c>null</c> for a required property or where the schema does not allow it, a
/// value of another JSON type (no number as a string, no string as a flag), a blank text, a value outside its
/// choices and a text or list that is too long are refused before anything is shown or runs. Two forms models
/// often send are read as what they mean, before the check and the same for the card and the handler:
/// <c>null</c> for an optional property that takes no <c>null</c> is left out, and a whole number written as
/// <c>1.0</c> is that number. The approval card and the handler read the same checked input with the same
/// accessors, so what the card says is what runs. A property that is left out reads as not given;
/// <c>null</c>, where the schema allows it, clears a value, and it is the only way to clear one.
/// </summary>
internal sealed class NhAssistantAgentBuilderInput
{
    private const int MaxReportedNameLength = 64;

    private readonly JsonElement _value;

    private NhAssistantAgentBuilderInput(JsonElement value)
    {
        _value = value;
    }

    /// <summary>
    /// The input of <paramref name="value"/> checked against <paramref name="schema"/>, a tool's input
    /// schema, or why it is refused, naming the property but never its value.
    /// </summary>
    public static (NhAssistantAgentBuilderInput? Input, string? Refused) Read(JsonElement value, JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
        {
            value = JsonSerializer.SerializeToElement(new JsonObject());
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return (null, "The input must be an object of the properties the tool's schema names.");
        }

        var node = (JsonObject)JsonNode.Parse(value.GetRawText())!;
        Normalize(node, schema);
        var normalized = JsonSerializer.SerializeToElement(node);
        var refusal = CheckObject(string.Empty, normalized, schema);
        return refusal is null ? (new NhAssistantAgentBuilderInput(normalized), null) : (null, refusal);
    }

    /// <summary>
    /// Reads two forms as what the schema means, in place: <c>null</c> for an optional property whose schema
    /// takes no <c>null</c> is left out, so it keeps what the agent has, and an integral number written with a
    /// fraction or an exponent where the schema wants a whole number is that whole number. Anything else is
    /// left as it is for the check.
    /// </summary>
    private static JsonNode? Normalize(JsonNode? node, JsonObject schema)
    {
        switch (node)
        {
            case JsonObject value when schema["properties"] is JsonObject properties:
            {
                var required = (schema["required"] as JsonArray ?? []).Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                foreach (var (name, child) in value.ToArray())
                {
                    if (properties[name] is not JsonObject propertySchema)
                    {
                        continue;
                    }
                    if (child is null)
                    {
                        if (!TypesOf(propertySchema).Contains("null") && !required.Contains(name))
                        {
                            value.Remove(name);
                        }
                        continue;
                    }

                    var normalized = Normalize(child, propertySchema);
                    if (!ReferenceEquals(normalized, child))
                    {
                        value[name] = normalized;
                    }
                }
                return value;
            }
            case JsonArray items when schema["items"] is JsonObject itemSchema:
            {
                for (var index = 0; index < items.Count; index++)
                {
                    var normalized = Normalize(items[index], itemSchema);
                    if (!ReferenceEquals(normalized, items[index]))
                    {
                        items[index] = normalized;
                    }
                }
                return items;
            }
            case JsonValue number when number.GetValueKind() == JsonValueKind.Number && TypesOf(schema).Contains("integer"):
            {
                var text = number.ToJsonString();
                return !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
                    && exact == decimal.Truncate(exact)
                    && exact >= long.MinValue
                    && exact <= long.MaxValue
                        ? JsonValue.Create((long)exact)
                        : number;
            }
            default:
                return node;
        }
    }

    /// <summary>
    /// Why <paramref name="value"/> does not fit the object schema <paramref name="schema"/>, or
    /// <see langword="null"/> when it does.
    /// </summary>
    private static string? CheckObject(string path, JsonElement value, JsonObject schema)
    {
        var properties = schema["properties"] as JsonObject ?? [];
        foreach (var property in value.EnumerateObject())
        {
            var name = path + property.Name;
            if (properties[property.Name] is not JsonObject propertySchema)
            {
                return $"The tool does not take '{Bounded(name)}'. Use only the properties its schema names; the agent builder never takes or handles secrets.";
            }
            if (Check(name, property.Value, propertySchema) is { } refusal)
            {
                return refusal;
            }
        }
        foreach (var required in (schema["required"] as JsonArray ?? []).Select(item => item!.GetValue<string>()))
        {
            if (!value.TryGetProperty(required, out _))
            {
                return $"Give {path + required}.";
            }
        }

        return schema["maxProperties"] is JsonValue maximum && value.EnumerateObject().Count() > NumberOf(maximum)
            ? $"'{Bounded(path.TrimEnd('.'))}' has more properties than its schema allows."
            : null;
    }

    /// <summary>
    /// Why one value does not fit its schema: its JSON type, <c>null</c>, its choices, its length, the size
    /// of a list and its items; <see langword="null"/> when it fits. A schema without a type takes any value.
    /// </summary>
    private static string? Check(string name, JsonElement value, JsonObject schema)
    {
        var types = TypesOf(schema);
        if (types.Count == 0)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return types.Contains("null")
                ? null
                : $"'{Bounded(name)}' may not be null. Leave it out to keep what the agent has.";
        }
        if (schema["anyOf"] is JsonArray branches)
        {
            // A value fits the first branch it fits; null was checked above.
            string? first = null;
            foreach (var branch in branches.OfType<JsonObject>().Where(branch => !TypesOf(branch).SetEquals(["null"])))
            {
                var refusal = Check(name, value, branch);
                if (refusal is null)
                {
                    return null;
                }
                first ??= refusal;
            }
            return first ?? $"'{Bounded(name)}' does not fit its schema.";
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String when types.Contains("string"):
            {
                var text = value.GetString()!;
                if (schema["enum"] is JsonArray choices
                    && !choices.Any(choice => choice is JsonValue item && item.TryGetValue<string>(out var allowed) && string.Equals(allowed, text, StringComparison.Ordinal)))
                {
                    return $"'{Bounded(name)}' is not one of the values its schema names.";
                }
                if (schema["maxLength"] is JsonValue maxLength && text.Length > NumberOf(maxLength))
                {
                    return $"'{Bounded(name)}' is longer than its schema allows.";
                }
                // A blank text says nothing, and the card could not show it: null is the only way to clear.
                return string.IsNullOrWhiteSpace(text)
                    ? types.Contains("null")
                        ? $"'{Bounded(name)}' may not be blank. Send null to clear it, or leave it out to keep what the agent has."
                        : $"'{Bounded(name)}' may not be blank. Leave it out to keep what the agent has."
                    : null;
            }
            case JsonValueKind.Number when types.Contains("integer"):
            {
                if (!value.TryGetInt64(out var number))
                {
                    return $"'{Bounded(name)}' must be a whole number.";
                }
                return schema["minimum"] is JsonValue minimum && number < NumberOf(minimum)
                    || schema["maximum"] is JsonValue maximum && number > NumberOf(maximum)
                    ? $"'{Bounded(name)}' is outside the range its schema allows."
                    : null;
            }
            case JsonValueKind.True or JsonValueKind.False when types.Contains("boolean"):
                return null;
            case JsonValueKind.Array when types.Contains("array"):
            {
                var count = value.GetArrayLength();
                if (schema["maxItems"] is JsonValue maxItems && count > NumberOf(maxItems)
                    || schema["minItems"] is JsonValue minItems && count < NumberOf(minItems))
                {
                    return $"'{Bounded(name)}' has another number of items than its schema allows.";
                }
                if (schema["items"] is JsonObject itemSchema)
                {
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (Check($"{name}[{index}]", item, itemSchema) is { } refusal)
                        {
                            return refusal;
                        }
                        index++;
                    }
                }
                return null;
            }
            case JsonValueKind.Object when types.Contains("object"):
                return schema["properties"] is JsonObject
                    ? CheckObject(name + ".", value, schema)
                    : schema["maxProperties"] is JsonValue maxProperties && value.EnumerateObject().Count() > NumberOf(maxProperties)
                        ? $"'{Bounded(name)}' has more properties than its schema allows."
                        : null;
            default:
                return $"'{Bounded(name)}' has the wrong type: send it as {string.Join(" or ", types.Where(type => type != "null"))}, as its schema says.";
        }
    }

    /// <summary>The JSON types a schema takes, also those of the branches of its <c>anyOf</c>.</summary>
    private static HashSet<string> TypesOf(JsonObject schema)
    {
        if (schema["anyOf"] is JsonArray branches)
        {
            return branches.OfType<JsonObject>().SelectMany(TypesOf).ToHashSet(StringComparer.Ordinal);
        }

        return schema["type"] switch
        {
            JsonValue single => [single.GetValue<string>()],
            JsonArray several => several.Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal),
            _ => []
        };
    }

    /// <summary>A bound of the schema as a number, whatever numeric type it was written with.</summary>
    private static long NumberOf(JsonValue bound)
    {
        return long.Parse(bound.ToJsonString(), CultureInfo.InvariantCulture);
    }

    private static string Bounded(string name)
    {
        return NhAssistantContent.Bound(name, MaxReportedNameLength);
    }
    /// <summary>
    /// Whether the input gives <paramref name="name"/> a value other than <c>null</c>.
    /// </summary>
    public bool Has(string name)
    {
        return _value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null;
    }

    /// <summary>
    /// Whether the input names <paramref name="name"/> at all, also with <c>null</c>, which clears a value
    /// that a change otherwise keeps.
    /// </summary>
    public bool Given(string name)
    {
        return _value.TryGetProperty(name, out _);
    }

    /// <summary>The text of <paramref name="name"/>, trimmed; <see langword="null"/> when it is not given or empty.</summary>
    public string? Text(string name)
    {
        if (!_value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new NhAssistantAgentBuilderInputException(name);
        }

        var text = property.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>The text of <paramref name="name"/> as it was sent, untrimmed, such as a script.</summary>
    public string? RawText(string name)
    {
        if (!_value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new NhAssistantAgentBuilderInputException(name);
        }

        return property.GetString();
    }

    public int? Int(string name)
    {
        if (!_value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        // A number only: the card reads the same value, so a number sent as text is refused, never parsed.
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        throw new NhAssistantAgentBuilderInputException(name);
    }

    public long? Long(string name)
    {
        if (!_value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        throw new NhAssistantAgentBuilderInputException(name);
    }

    public bool? Bool(string name)
    {
        if (!_value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new NhAssistantAgentBuilderInputException(name)
        };
    }

    /// <summary>The texts of the array <paramref name="name"/>, trimmed and without empty ones.</summary>
    public IReadOnlyList<string>? Texts(string name)
    {
        if (!_value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new NhAssistantAgentBuilderInputException(name);
        }

        var texts = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new NhAssistantAgentBuilderInputException(name);
            }
            if (item.GetString()?.Trim() is { Length: > 0 } text)
            {
                texts.Add(text);
            }
        }

        return texts;
    }

    /// <summary>
    /// The value of <paramref name="name"/> as it was sent, of any JSON type, such as a metadata value;
    /// <see langword="null"/> when it is left out. A JSON <c>null</c> is returned as such.
    /// </summary>
    public JsonElement? Element(string name)
    {
        return _value.TryGetProperty(name, out var property) ? property.Clone() : null;
    }

    /// <summary>The properties of the object <paramref name="name"/>, by name, such as metadata values.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Map(string name)
    {
        if (!_value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.Object)
        {
            throw new NhAssistantAgentBuilderInputException(name);
        }

        return property.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>The objects of the array <paramref name="name"/>.</summary>
    public IReadOnlyList<JsonElement>? Objects(string name)
    {
        if (!_value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.Array || property.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object))
        {
            throw new NhAssistantAgentBuilderInputException(name);
        }

        return property.EnumerateArray().ToArray();
    }
}

/// <summary>
/// A value of the input that has the wrong type; the call is refused with the property's name.
/// </summary>
internal sealed class NhAssistantAgentBuilderInputException(string property)
    : Exception("The input property '" + property + "' has the wrong type.")
{
    public string Property { get; } = property;
}
