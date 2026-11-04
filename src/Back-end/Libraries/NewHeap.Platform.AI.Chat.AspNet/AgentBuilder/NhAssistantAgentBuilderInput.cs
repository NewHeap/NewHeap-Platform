using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// The input of one call of an agent builder tool, as the model sent it. It is untrusted: a property the
/// tool does not take and a value of the wrong type are refused before anything runs, and a property that
/// is left out or <c>null</c> reads as not given.
/// </summary>
internal sealed class NhAssistantAgentBuilderInput
{
    private readonly JsonElement _value;

    private NhAssistantAgentBuilderInput(JsonElement value)
    {
        _value = value;
    }

    /// <summary>
    /// The input of <paramref name="value"/> for a tool that takes <paramref name="properties"/>, or the name
    /// of the first property it does not take; <c>input</c> when the value is no object.
    /// </summary>
    public static (NhAssistantAgentBuilderInput? Input, string? Refused) Read(JsonElement value, IReadOnlyCollection<string> properties)
    {
        if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
        {
            value = JsonSerializer.SerializeToElement(new JsonObject());
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return (null, "input");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!properties.Contains(property.Name))
            {
                return (null, NhAssistantContent.Bound(property.Name, 64));
            }
        }

        return (new NhAssistantAgentBuilderInput(value.Clone()), null);
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
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }
        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
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
