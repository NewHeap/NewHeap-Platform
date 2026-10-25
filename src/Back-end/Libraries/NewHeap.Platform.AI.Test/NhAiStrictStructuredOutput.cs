using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NewHeap.Platform.AI.Test;

/// <summary>
/// One rule of strict structured output that a JSON schema breaks, at <see cref="Path"/>, a JSON
/// pointer into the schema such as <c>#/properties/done/items</c>.
/// </summary>
public sealed record NhAiStrictSchemaViolation(
    string Path,
    string Rule,
    string Message);

/// <summary>
/// Checks a structured-output JSON schema, such as the schema of a
/// <see cref="ChatResponseFormatJson"/> in <see cref="ChatOptions.ResponseFormat"/>, against the rules
/// that model providers apply to strict structured output, so a test fails where the provider would
/// refuse the request. The libraries do not normalize schemas; an application that sends one with
/// references or open objects normalizes it itself and proves the result with this check, or with
/// <see cref="NhAiDeterministicChatClient.StrictStructuredOutput"/>.
/// </summary>
/// <remarks>
/// The rules: a <c>$ref</c> names the root (<c>#</c>) or one entry of the root's <c>$defs</c>
/// (<c>#/$defs/name</c>), never a JSON pointer into another part of the schema such as
/// <c>#/properties/open/items</c>; every object schema sets <c>additionalProperties</c> to
/// <see langword="false"/>; and every property of an object schema is listed in its
/// <c>required</c>. A nullable property stays required and allows <c>null</c> in its type instead.
/// </remarks>
public static class NhAiStrictStructuredOutput
{
    /// <summary>A <c>$ref</c> that names neither the root nor an entry of the root's <c>$defs</c>.</summary>
    public const string ReferenceRule = "reference";

    /// <summary>An object schema without <c>additionalProperties: false</c>.</summary>
    public const string AdditionalPropertiesRule = "additional-properties";

    /// <summary>A property that its object schema does not list in <c>required</c>.</summary>
    public const string RequiredRule = "required";

    private const string DefinitionsPrefix = "#/$defs/";

    private static readonly string[] SchemaListKeywords = ["anyOf", "oneOf", "allOf", "prefixItems"];
    private static readonly string[] SchemaKeywords = ["not", "if", "then", "else"];
    private static readonly string[] DefinitionKeywords = ["$defs", "definitions"];

    /// <summary>
    /// Every violation of the strict rules in <paramref name="schema"/>, in document order; empty when a
    /// strict provider accepts it.
    /// </summary>
    public static IReadOnlyList<NhAiStrictSchemaViolation> Validate(JsonElement schema)
    {
        var violations = new List<NhAiStrictSchemaViolation>();
        var definitions = schema.ValueKind == JsonValueKind.Object
                          && schema.TryGetProperty("$defs", out var defs)
                          && defs.ValueKind == JsonValueKind.Object
            ? defs.EnumerateObject().Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal)
            : [];

        Walk(schema, "#", definitions, violations);
        return violations;
    }

    /// <summary>
    /// Refuses <paramref name="options"/> as a strict provider refuses the request: with an
    /// <see cref="HttpRequestException"/> of status 400 that names the first violation.
    /// </summary>
    internal static void ThrowIfRefused(ChatOptions? options)
    {
        if (options?.ResponseFormat is not ChatResponseFormatJson { Schema: { } schema } format)
        {
            return;
        }

        var violation = Validate(schema).FirstOrDefault();
        if (violation is null)
        {
            return;
        }

        throw new HttpRequestException(
            $"Invalid schema for response_format '{format.SchemaName ?? "response"}': {violation.Message} (at '{violation.Path}').",
            null,
            HttpStatusCode.BadRequest);
    }

    private static void Walk(
        JsonElement node,
        string path,
        IReadOnlySet<string> definitions,
        List<NhAiStrictSchemaViolation> violations)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (node.TryGetProperty("$ref", out var reference))
        {
            CheckReference(reference, path, definitions, violations);
        }

        if (IsObjectSchema(node))
        {
            CheckObject(node, path, violations);
        }

        if (node.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                Walk(property.Value, $"{path}/properties/{Escape(property.Name)}", definitions, violations);
            }
        }

        if (node.TryGetProperty("items", out var items))
        {
            if (items.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in items.EnumerateArray())
                {
                    Walk(item, $"{path}/items/{index++}", definitions, violations);
                }
            }
            else
            {
                Walk(items, $"{path}/items", definitions, violations);
            }
        }

        foreach (var keyword in SchemaListKeywords)
        {
            if (node.TryGetProperty(keyword, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in list.EnumerateArray())
                {
                    Walk(item, $"{path}/{keyword}/{index++}", definitions, violations);
                }
            }
        }

        foreach (var keyword in SchemaKeywords)
        {
            if (node.TryGetProperty(keyword, out var subschema))
            {
                Walk(subschema, $"{path}/{keyword}", definitions, violations);
            }
        }

        foreach (var keyword in DefinitionKeywords)
        {
            if (node.TryGetProperty(keyword, out var entries) && entries.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in entries.EnumerateObject())
                {
                    Walk(entry.Value, $"{path}/{keyword}/{Escape(entry.Name)}", definitions, violations);
                }
            }
        }
    }

    private static void CheckReference(
        JsonElement reference,
        string path,
        IReadOnlySet<string> definitions,
        List<NhAiStrictSchemaViolation> violations)
    {
        var target = reference.ValueKind == JsonValueKind.String ? reference.GetString() : null;
        if (target == "#")
        {
            return;
        }

        var name = target is not null && target.StartsWith(DefinitionsPrefix, StringComparison.Ordinal)
            ? target[DefinitionsPrefix.Length..]
            : null;
        if (name is { Length: > 0 } && !name.Contains('/', StringComparison.Ordinal))
        {
            if (!definitions.Contains(Unescape(name)))
            {
                violations.Add(new NhAiStrictSchemaViolation(
                    path,
                    ReferenceRule,
                    $"Reference '{target}' names no entry of the top-level $defs."));
            }
            return;
        }

        // A JSON pointer into another part of the schema, a nested definition or another document.
        violations.Add(new NhAiStrictSchemaViolation(
            path,
            ReferenceRule,
            $"Reference '{target}' must name the root ('#') or an entry of the top-level $defs ('#/$defs/name')."));
    }

    private static void CheckObject(
        JsonElement node,
        string path,
        List<NhAiStrictSchemaViolation> violations)
    {
        if (!node.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False)
        {
            violations.Add(new NhAiStrictSchemaViolation(
                path,
                AdditionalPropertiesRule,
                "An object schema must set 'additionalProperties' to false."));
        }

        if (!node.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var required = node.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var property in properties.EnumerateObject())
        {
            if (!required.Contains(property.Name))
            {
                violations.Add(new NhAiStrictSchemaViolation(
                    $"{path}/properties/{Escape(property.Name)}",
                    RequiredRule,
                    $"Property '{property.Name}' must be listed in 'required'; allow null in its type to make it optional."));
            }
        }
    }

    /// <summary>
    /// An object schema: its type is <c>object</c>, alone or with <c>null</c>, or it has properties
    /// without a type.
    /// </summary>
    private static bool IsObjectSchema(JsonElement node)
    {
        if (!node.TryGetProperty("type", out var type))
        {
            return node.TryGetProperty("properties", out _);
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString() == "object",
            JsonValueKind.Array => type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "object"),
            _ => false
        };
    }

    private static string Escape(string name)
    {
        return name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    private static string Unescape(string segment)
    {
        return segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }
}
