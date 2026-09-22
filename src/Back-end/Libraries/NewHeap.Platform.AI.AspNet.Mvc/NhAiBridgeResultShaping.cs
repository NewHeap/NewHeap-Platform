using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Structured guidance returned by the gateway <c>query</c> and <c>get</c> tools when a shaped
/// result still does not fit the tool result limit. It replaces the raw <c>bodyText</c> fragment.
/// </summary>
public sealed record NhAiBridgeTruncation
{
    /// <summary>The total number of matching items reported by the API, when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalCount { get; init; }

    /// <summary>The number of items the API returned for this page, when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ResultCount { get; init; }

    /// <summary>The number of whole items kept in <c>body</c>.</summary>
    public int ReturnedCount { get; init; }

    /// <summary>An <c>itemsPerPage</c> value that is expected to fit, when one can be estimated.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SuggestedItemsPerPage { get; init; }

    /// <summary>Result field keys to pass as <c>fields</c> for a smaller result.</summary>
    public IReadOnlyList<string> SuggestedFields { get; init; } = [];
}

/// <summary>
/// How the gateway shapes results of a resource, published by <c>describe-resource</c>.
/// </summary>
public sealed record NhAiBridgeResultShaping
{
    /// <summary>The shaping applied when no <c>fields</c> are passed: <c>compact</c>.</summary>
    public string Default { get; init; } = NhAiBridgeResultShaper.CompactMode;

    /// <summary>Whether <c>fields</c> may select result fields, including one-level dotted paths.</summary>
    public bool Fields { get; init; } = true;

    /// <summary>Whether <c>query</c> accepts <c>countOnly</c> to return only <c>totalCount</c>.</summary>
    public bool CountOnly { get; init; }

    public string Description { get; init; } = NhAiBridgeResultShaper.ShapingDescription;
}

/// <summary>Removes result fields whose names match the gateway's redaction patterns.</summary>
internal sealed class NhAiBridgeResultRedaction
{
    public static NhAiBridgeResultRedaction None { get; } = new([]);

    private readonly Regex[] _patterns;

    public NhAiBridgeResultRedaction(IReadOnlyList<string> patterns)
    {
        Patterns = patterns;
        _patterns = patterns
            .Select(pattern => new Regex(
                "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
    }

    public IReadOnlyList<string> Patterns { get; }

    public bool IsEmpty => _patterns.Length == 0;

    public bool IsRedacted(string name)
    {
        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(name))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether any dotted segment of a field key is redacted.</summary>
    public bool IsRedactedPath(string path)
    {
        return path.Split('.').Any(IsRedacted);
    }

    /// <summary>Removes redacted properties at every depth, in place.</summary>
    public void Apply(JsonNode? node)
    {
        if (IsEmpty)
        {
            return;
        }

        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToArray())
                {
                    if (IsRedacted(property.Key))
                    {
                        jsonObject.Remove(property.Key);
                    }
                    else
                    {
                        Apply(property.Value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Apply(item);
                }
                break;
        }
    }
}

/// <summary>
/// Shapes gateway <c>query</c> and <c>get</c> results after the HTTP call and before the size
/// bound: redaction, then field projection (<c>fields</c>) or default compaction, then whole-item
/// truncation with structured guidance. Count requests return only <c>totalCount</c>.
/// </summary>
internal sealed class NhAiBridgeResultShaper
{
    public const string CompactMode = "compact";

    public const int MaxFields = 30;

    public const string ShapingDescription =
        "Without 'fields', items are compacted: null values are omitted, nested objects keep only identifying fields "
        + "(id, key, code, number, name, displayName, title, label) and deeper nesting is omitted. Pass 'fields' with result "
        + "field keys, including one-level dotted paths such as 'owner.name', to return exactly those fields. "
        + "Use 'countOnly' on query to get only 'totalCount'. When a result is still too large, 'truncation' suggests "
        + "'fields' and 'itemsPerPage'.";

    public const string CompactedHint =
        "Items were compacted. Pass 'fields' to select result fields, including one-level dotted paths.";

    private static readonly JsonSerializerOptions MeasureOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] IdentityNames =
        ["id", "key", "code", "number", "name", "displayName", "title", "label"];

    private static readonly string[] CollectionScalarNames =
        ["page", "itemsPerPage", "totalCount", "resultCount"];

    private const int MaxSuggestedFields = 8;

    // Keeps a margin for serializer differences between this measurement and the invoker's.
    private const int SizeMargin = 256;

    public NhAiBridgeResultShaper(
        IReadOnlyList<string>? fields,
        bool countOnly,
        NhAiBridgeResultRedaction redaction,
        IReadOnlyList<NhAiBridgeResultField> resultFields,
        int maxResponseBytes,
        int? requestedItemsPerPage)
    {
        Fields = fields;
        CountOnly = countOnly;
        Redaction = redaction;
        ResultFields = resultFields;
        MaxResponseBytes = maxResponseBytes;
        RequestedItemsPerPage = requestedItemsPerPage;
    }

    public IReadOnlyList<string>? Fields { get; }

    public bool CountOnly { get; }

    public NhAiBridgeResultRedaction Redaction { get; }

    public IReadOnlyList<NhAiBridgeResultField> ResultFields { get; }

    /// <summary>The number of response bytes read for shaping; larger bodies are not parsed.</summary>
    public int MaxResponseBytes { get; }

    public int? RequestedItemsPerPage { get; }

    /// <summary>
    /// Validates <paramref name="fields"/> against the described result fields and redaction.
    /// Returns the normalized field keys or a validation failure.
    /// </summary>
    public static TaskResult<IReadOnlyList<string>?> ValidateFields(
        JsonElement input,
        IReadOnlyList<NhAiBridgeResultField> resultFields,
        NhAiBridgeResultRedaction redaction)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("fields", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return TaskResult<IReadOnlyList<string>?>.Succeeded((IReadOnlyList<string>?)null);
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return InvalidFields("The input value 'fields' must be an array of result field keys.");
        }

        var fields = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                return InvalidFields("Every 'fields' entry must be a non-empty result field key.");
            }

            var field = item.GetString()!.Trim();
            var segments = field.Split('.');
            if (segments.Length > 2 || segments.Any(string.IsNullOrWhiteSpace))
            {
                return InvalidFields(
                    $"The field '{Bounded(field)}' is not supported; use a result field key or one dotted level such as 'owner.name'.");
            }
            if (redaction.IsRedactedPath(field))
            {
                return InvalidFields($"The field '{Bounded(field)}' is not available.");
            }
            if (resultFields.Count > 0
                && !resultFields.Any(candidate => string.Equals(candidate.Key, segments[0], StringComparison.OrdinalIgnoreCase)))
            {
                return InvalidFields($"The field '{Bounded(field)}' is not a result field of this resource.");
            }
            if (!fields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                fields.Add(field);
            }
        }

        if (fields.Count == 0)
        {
            return InvalidFields("The input value 'fields' must contain at least one result field key.");
        }
        if (fields.Count > MaxFields)
        {
            return InvalidFields($"The input value 'fields' accepts at most {MaxFields} keys.");
        }
        return TaskResult<IReadOnlyList<string>?>.Succeeded((IReadOnlyList<string>?)fields);
    }

    /// <summary>Shapes a successful response body read by the executor.</summary>
    public TaskResult<NhAiBridgeResponse> Shape(
        int status,
        string? contentType,
        byte[] kept,
        long totalBytes,
        int maxResultBytes)
    {
        var complete = kept.LongLength == totalBytes;
        if (totalBytes == 0)
        {
            return CountOnly
                ? CountUnavailable()
                : Succeeded(new NhAiBridgeResponse { Status = status, ContentType = contentType, BodyBytes = 0 });
        }

        if (!complete)
        {
            return CountOnly
                ? CountUnavailable()
                : Succeeded(Unreadable(status, contentType, totalBytes));
        }

        JsonNode? body;
        try
        {
            body = JsonNode.Parse(kept);
        }
        catch (JsonException)
        {
            body = null;
        }
        if (body is null)
        {
            if (CountOnly)
            {
                return CountUnavailable();
            }
            // Text that is not JSON cannot be redacted or projected; it is returned unshaped only
            // when no redaction applies and it fits.
            return Succeeded(Redaction.IsEmpty
                ? WithoutBodyText(NhAiMvcBridgeExecutor.CreateEnvelope(status, contentType, kept, totalBytes, maxResultBytes))
                : Unreadable(status, contentType, totalBytes));
        }

        if (CountOnly)
        {
            return ReadCount(status, contentType, body, totalBytes);
        }

        Redaction.Apply(body);
        var collection = ReadCollection(body, out var items);
        var compacted = false;
        if (items is not null)
        {
            for (var index = 0; index < items.Count; index++)
            {
                items[index] = ShapeItem(items[index], ref compacted);
            }
        }
        else
        {
            body = ShapeItem(body, ref compacted);
        }

        var hint = Fields is null && compacted ? CompactedHint : null;
        JsonNode? shaped = body;
        if (collection is not null)
        {
            collection["items"] = new JsonArray(items!.ToArray());
            shaped = collection;
        }
        else if (items is not null)
        {
            shaped = new JsonArray(items.ToArray());
        }
        var envelope = new NhAiBridgeResponse
        {
            Status = status,
            ContentType = contentType,
            Body = ToElement(shaped),
            BodyBytes = totalBytes,
            Hint = hint
        };
        if (Measure(envelope) <= Limit(maxResultBytes))
        {
            return Succeeded(envelope);
        }
        return Succeeded(Truncate(envelope, collection, items, shaped, maxResultBytes));
    }

    /// <summary>Shapes an envelope produced by a replaced executor.</summary>
    public TaskResult<NhAiBridgeResponse> Shape(NhAiBridgeResponse response, int maxResultBytes)
    {
        if (response.Truncated || response.Body is null)
        {
            if (CountOnly)
            {
                return CountUnavailable();
            }
            return Succeeded(response.Truncated
                ? Unreadable(response.Status, response.ContentType, response.BodyBytes)
                : response);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(response.Body.Value, MeasureOptions);
        var result = Shape(response.Status, "application/json", bytes, bytes.LongLength, maxResultBytes);
        if (!result.Success || result.Data is null)
        {
            return result;
        }
        return Succeeded(result.Data with
        {
            ContentType = response.ContentType,
            BodyBytes = response.BodyBytes
        });
    }

    private TaskResult<NhAiBridgeResponse> ReadCount(int status, string? contentType, JsonNode body, long totalBytes)
    {
        if (body is JsonObject jsonObject
            && TryGetProperty(jsonObject, "totalCount", out var value)
            && value is JsonValue jsonValue
            && jsonValue.TryGetValue<long>(out var totalCount))
        {
            var result = new JsonObject { ["totalCount"] = totalCount };
            return Succeeded(new NhAiBridgeResponse
            {
                Status = status,
                ContentType = contentType,
                Body = ToElement(result),
                BodyBytes = totalBytes
            });
        }
        return CountUnavailable();
    }

    /// <summary>
    /// Recognizes a collection result: a JSON array of items, or an object with an <c>items</c>
    /// array. The returned wrapper keeps only the paging scalars and the items.
    /// </summary>
    private static JsonObject? ReadCollection(JsonNode body, out List<JsonNode?>? items)
    {
        if (body is JsonArray array)
        {
            items = array.Select(item => item?.DeepClone()).ToList();
            return null;
        }
        if (body is JsonObject jsonObject
            && TryGetProperty(jsonObject, "items", out var itemsNode)
            && itemsNode is JsonArray itemsArray)
        {
            items = itemsArray.Select(item => item?.DeepClone()).ToList();
            var wrapper = new JsonObject();
            foreach (var property in jsonObject)
            {
                if (CollectionScalarNames.Contains(property.Key, StringComparer.OrdinalIgnoreCase)
                    && property.Value is JsonValue)
                {
                    wrapper[property.Key] = property.Value.DeepClone();
                }
            }
            wrapper["items"] = new JsonArray();
            return wrapper;
        }
        items = null;
        return null;
    }

    private JsonNode? ShapeItem(JsonNode? item, ref bool compacted)
    {
        if (item is not JsonObject jsonObject)
        {
            return item?.DeepClone();
        }
        return Fields is null
            ? Compact(jsonObject, ref compacted)
            : Project(jsonObject, Fields);
    }

    private static JsonObject Project(JsonObject item, IReadOnlyList<string> fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            var segments = field.Split('.');
            if (!TryGetProperty(item, segments[0], out var value, out var name))
            {
                continue;
            }
            if (segments.Length == 1)
            {
                result[name] = value?.DeepClone();
                continue;
            }

            var nested = ProjectNested(value, segments[1]);
            if (nested is null)
            {
                continue;
            }
            if (result[name] is JsonObject existing && nested is JsonObject addition)
            {
                foreach (var property in addition.ToArray())
                {
                    addition.Remove(property.Key);
                    existing[property.Key] = property.Value;
                }
            }
            else if (result[name] is JsonArray existingArray && nested is JsonArray additionArray)
            {
                for (var index = 0; index < existingArray.Count && index < additionArray.Count; index++)
                {
                    if (existingArray[index] is JsonObject target && additionArray[index] is JsonObject source)
                    {
                        foreach (var property in source.ToArray())
                        {
                            source.Remove(property.Key);
                            target[property.Key] = property.Value;
                        }
                    }
                }
            }
            else
            {
                result[name] = nested;
            }
        }
        return result;
    }

    private static JsonNode? ProjectNested(JsonNode? value, string field)
    {
        switch (value)
        {
            case JsonObject jsonObject:
                var projected = new JsonObject();
                if (TryGetProperty(jsonObject, field, out var nested, out var name))
                {
                    projected[name] = nested?.DeepClone();
                }
                return projected;
            case JsonArray array:
                return new JsonArray(array
                    .Select(item => item is JsonObject element ? ProjectNested(element, field) : item?.DeepClone())
                    .ToArray());
            default:
                return null;
        }
    }

    /// <summary>
    /// Keeps scalar fields, reduces nested objects (and arrays of objects) to their identifying
    /// scalar fields, and omits nulls, empty nested values and deeper nesting.
    /// </summary>
    private static JsonObject Compact(JsonObject item, ref bool compacted)
    {
        var result = new JsonObject();
        foreach (var property in item)
        {
            switch (property.Value)
            {
                case null:
                    compacted = true;
                    break;
                case JsonValue scalar:
                    result[property.Key] = scalar.DeepClone();
                    break;
                case JsonObject nested:
                    var identity = Identity(nested, ref compacted);
                    if (identity.Count > 0)
                    {
                        result[property.Key] = identity;
                    }
                    else
                    {
                        compacted = true;
                    }
                    break;
                case JsonArray array:
                    var elements = new JsonArray();
                    foreach (var element in array)
                    {
                        switch (element)
                        {
                            case JsonValue elementScalar:
                                elements.Add(elementScalar.DeepClone());
                                break;
                            case JsonObject elementObject:
                                var elementIdentity = Identity(elementObject, ref compacted);
                                if (elementIdentity.Count > 0)
                                {
                                    elements.Add(elementIdentity);
                                }
                                else
                                {
                                    compacted = true;
                                }
                                break;
                            default:
                                compacted = true;
                                break;
                        }
                    }
                    if (elements.Count > 0)
                    {
                        result[property.Key] = elements;
                    }
                    break;
            }
        }
        return result;
    }

    private static JsonObject Identity(JsonObject nested, ref bool compacted)
    {
        var identity = new JsonObject();
        foreach (var property in nested)
        {
            if (property.Value is JsonValue scalar
                && IdentityNames.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                identity[property.Key] = scalar.DeepClone();
            }
            else
            {
                compacted = true;
            }
        }
        return identity;
    }

    private NhAiBridgeResponse Truncate(
        NhAiBridgeResponse envelope,
        JsonObject? collection,
        List<JsonNode?>? items,
        JsonNode? shaped,
        int maxResultBytes)
    {
        var totalCount = collection is not null && TryReadLong(collection, "totalCount", out var total) ? total : (long?)null;
        var resultCount = items?.Count;
        var suggestedFields = SuggestFields(items?.FirstOrDefault() as JsonObject ?? shaped as JsonObject);
        var limit = Limit(maxResultBytes);

        if (items is null)
        {
            return envelope with
            {
                Body = null,
                Truncated = true,
                Hint = "The item is too large to return. Pass 'fields' to select result fields.",
                Truncation = new NhAiBridgeTruncation
                {
                    ReturnedCount = 0,
                    SuggestedFields = suggestedFields
                }
            };
        }

        // Keep as many whole items as fit, so the body remains valid JSON.
        var kept = items.Count;
        NhAiBridgeResponse candidate;
        while (true)
        {
            kept = Math.Max(0, kept);
            var subset = new JsonArray(items.Take(kept).Select(item => item?.DeepClone()).ToArray());
            JsonNode body = subset;
            if (collection is not null)
            {
                var wrapper = (JsonObject)collection.DeepClone();
                wrapper["items"] = subset;
                body = wrapper;
            }

            candidate = envelope with
            {
                Body = ToElement(body),
                Truncated = true,
                Hint = $"Returned {kept} of {items.Count} items. Request fewer items per page or pass 'fields'.",
                Truncation = new NhAiBridgeTruncation
                {
                    TotalCount = totalCount,
                    ResultCount = resultCount,
                    ReturnedCount = kept,
                    SuggestedItemsPerPage = Math.Max(1, kept),
                    SuggestedFields = suggestedFields
                }
            };
            var size = Measure(candidate);
            if (size <= limit || kept == 0)
            {
                break;
            }

            // Shrink proportionally to the overshoot, at least one item per round.
            var perItem = Math.Max(1, size / Math.Max(1, kept));
            kept -= Math.Max(1, (int)Math.Ceiling((double)(size - limit) / perItem));
        }

        if (Measure(candidate) > limit)
        {
            candidate = candidate with { Body = null };
        }
        return candidate;
    }

    private NhAiBridgeResponse Unreadable(int status, string? contentType, long totalBytes)
    {
        int? suggestedItemsPerPage = null;
        if (RequestedItemsPerPage is { } requested && totalBytes > 0)
        {
            var perItem = Math.Max(1, totalBytes / Math.Max(1, requested));
            suggestedItemsPerPage = (int)Math.Clamp(MaxResponseBytes / 2 / perItem, 1, Math.Max(1, requested - 1));
        }
        return new NhAiBridgeResponse
        {
            Status = status,
            ContentType = contentType,
            BodyBytes = totalBytes,
            Truncated = true,
            Hint = "The response is too large to shape. Request fewer items per page or pass 'fields'.",
            Truncation = new NhAiBridgeTruncation
            {
                ReturnedCount = 0,
                SuggestedItemsPerPage = suggestedItemsPerPage,
                SuggestedFields = SuggestFields(null)
            }
        };
    }

    private static NhAiBridgeResponse WithoutBodyText(NhAiBridgeResponse response)
    {
        return response.Truncated
            ? response with { BodyText = null, Hint = "The response is too large and is not JSON." }
            : response;
    }

    /// <summary>
    /// Suggests identifying fields first, then other scalar result fields, never redacted ones.
    /// Falls back to the scalar properties of the first returned item.
    /// </summary>
    private IReadOnlyList<string> SuggestFields(JsonObject? sample)
    {
        var candidates = ResultFields.Count > 0
            ? ResultFields
                .Where(field => field.Type != "object")
                .Select(field => field.Key)
            : sample?
                .Where(property => property.Value is JsonValue)
                .Select(property => property.Key)
                ?? [];
        var fields = candidates
            .Where(field => !Redaction.IsRedacted(field))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return fields
            .OrderBy(field => IdentityNames.Contains(field, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(field => Array.IndexOf(fields, field))
            .Take(MaxSuggestedFields)
            .ToArray();
    }

    private static int Limit(int maxResultBytes)
    {
        return Math.Max(1, maxResultBytes - SizeMargin);
    }

    private static int Measure(NhAiBridgeResponse envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(envelope, MeasureOptions).Length;
    }

    private static JsonElement? ToElement(JsonNode? node)
    {
        return node is null ? null : JsonSerializer.SerializeToElement(node, MeasureOptions);
    }

    private static bool TryGetProperty(JsonObject jsonObject, string name, out JsonNode? value)
    {
        return TryGetProperty(jsonObject, name, out value, out _);
    }

    private static bool TryGetProperty(JsonObject jsonObject, string name, out JsonNode? value, out string actualName)
    {
        foreach (var property in jsonObject)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                actualName = property.Key;
                return true;
            }
        }
        value = null;
        actualName = name;
        return false;
    }

    private static bool TryReadLong(JsonObject jsonObject, string name, out long value)
    {
        if (TryGetProperty(jsonObject, name, out var node)
            && node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value))
        {
            return true;
        }
        value = 0;
        return false;
    }

    private static TaskResult<NhAiBridgeResponse> Succeeded(NhAiBridgeResponse response)
    {
        return TaskResult<NhAiBridgeResponse>.Succeeded(response);
    }

    private static TaskResult<NhAiBridgeResponse> CountUnavailable()
    {
        return TaskResult<NhAiBridgeResponse>.Failed(
            NhAiBridgeFailureCodes.Validation,
            "The resource did not report a total count. Query without 'countOnly' instead.");
    }

    private static TaskResult<IReadOnlyList<string>?> InvalidFields(string message)
    {
        return TaskResult<IReadOnlyList<string>?>.Failed(NhAiBridgeFailureCodes.Validation, message);
    }

    private static string Bounded(string value)
    {
        return value.Length <= 64 ? value : value[..64];
    }
}

/// <summary>The optional executor seam that reads a larger body for gateway shaping.</summary>
internal interface INhAiMvcBridgeShapingExecutor
{
    Task<TaskResult<NhAiBridgeResponse>> ExecuteShapedAsync(
        NhAiBridgeActionInfo action,
        NhAiToolDescriptor descriptor,
        JsonElement input,
        NhAiInvocationContext context,
        NhAiBridgeResultShaper shaper,
        CancellationToken cancellationToken);
}

/// <summary>Builds the single-page input of the default count request.</summary>
internal static class NhAiBridgeCountInput
{
    public static JsonElement SinglePage(JsonElement input)
    {
        var result = input.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(input.GetRawText())!.AsObject()
            : new JsonObject();
        result["page"] = 1;
        result["itemsPerPage"] = 1;
        result.Remove("orderBy");
        return JsonSerializer.SerializeToElement(result);
    }
}
