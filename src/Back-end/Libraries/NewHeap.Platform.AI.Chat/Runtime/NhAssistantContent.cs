using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// One stored message part. Tool-call and approval parts reference their authoritative rows.
/// </summary>
internal sealed record NhAssistantStoredPart(
    string Type,
    string? Text = null,
    Guid? InvocationId = null,
    Guid? ApprovalId = null)
{
    public const string TextType = "text";
    public const string ToolCallType = "tool-call";
    public const string ApprovalType = "approval";
}

/// <summary>
/// Serialization, bounding and redaction of conversation content.
/// </summary>
internal static class NhAssistantContent
{
    public const int PartsVersion = 1;
    public const string Redacted = "[redacted]";

    private static readonly JsonSerializerOptions PartOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SerializeParts(IEnumerable<NhAssistantStoredPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return JsonSerializer.Serialize(parts.ToArray(), PartOptions);
    }

    public static IReadOnlyList<NhAssistantStoredPart> DeserializeParts(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<NhAssistantStoredPart[]>(json, PartOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored assistant message parts are corrupt.", exception);
        }
    }

    public static string TextOf(IEnumerable<NhAssistantStoredPart> parts)
    {
        return string.Concat(parts
            .Where(part => part.Type == NhAssistantStoredPart.TextType)
            .Select(part => part.Text ?? string.Empty));
    }

    /// <summary>
    /// Serializes model-supplied function arguments, bounded to the stored maximum.
    /// </summary>
    public static string? SerializeArguments(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }
        var dictionary = arguments.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(dictionary, JsonOptions);
        return json.Length <= NhAssistantLimits.MaxStoredJsonCharacters ? json : null;
    }

    public static Dictionary<string, object?> DeserializeArguments(string? json)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return arguments;
        }
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }
        return arguments;
    }

    public static string? SerializeResult(object? result)
    {
        if (result is null)
        {
            return null;
        }
        var json = result is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(result, result.GetType(), JsonOptions);
        return Bound(json, NhAssistantLimits.MaxStoredJsonCharacters);
    }

    /// <summary>
    /// Creates a UI preview that respects the tool's data classification.
    /// </summary>
    public static string? Preview(string? json, NhAiDataClassification classification)
    {
        if (json is null)
        {
            return null;
        }
        if (classification >= NhAiDataClassification.Confidential)
        {
            return Redacted;
        }
        return Bound(json, NhAssistantLimits.MaxPreviewCharacters);
    }

    /// <summary>
    /// Previews the data of a serialized <c>TaskResult</c> envelope, or the whole value.
    /// </summary>
    public static string? ResultPreview(string? json, NhAiDataClassification classification)
    {
        if (json is null)
        {
            return null;
        }
        if (classification >= NhAiDataClassification.Confidential)
        {
            return Redacted;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("data", out var data))
            {
                return Bound(data.GetRawText(), NhAssistantLimits.MaxPreviewCharacters);
            }
        }
        catch (JsonException)
        {
            // A bounded, truncated result is no longer valid JSON; preview the raw text.
        }
        return Bound(json, NhAssistantLimits.MaxPreviewCharacters);
    }

    public static string Bound(string value, int maximum)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length <= maximum ? value : value[..maximum];
    }
}
