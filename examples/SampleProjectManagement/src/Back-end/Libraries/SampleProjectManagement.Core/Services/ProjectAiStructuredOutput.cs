using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// SPM-335: the sample's own normalization of structured-output schemas for providers in strict mode.
/// NewHeap's libraries pass a schema on as the application gives it; the application decides what its
/// providers accept. Microsoft.Extensions.AI writes a repeated object type out in place and lets a
/// nullable object allow <c>null</c> in its type, both accepted, but it refers to a second use of a
/// list's element type, and to a recursive type below the root, with a JSON pointer such as
/// <c>#/properties/onTrack/items</c>, which strict providers refuse. This helper generates the schema
/// with closed objects and every property required, then moves each such reference target into
/// <c>$defs</c>. Tests prove the result with the strict check of
/// <c>NhAiDeterministicChatClient.StrictStructuredOutput</c>.
/// </summary>
public static class ProjectAiStructuredOutput
{
    private const string DefinitionsPrefix = "#/$defs/";

    private static readonly AIJsonSchemaCreateOptions StrictOptions = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            DisallowAdditionalProperties = true,
            RequireAllProperties = true
        }
    };

    /// <summary>
    /// The response format of <typeparamref name="T"/> for a strict provider.
    /// </summary>
    public static ChatResponseFormatJson ForJsonSchema<T>(string schemaName, string? schemaDescription = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        return ChatResponseFormat.ForJsonSchema(
            CreateJsonSchema(typeof(T)),
            schemaName,
            schemaDescription);
    }

    /// <summary>
    /// The schema of <paramref name="type"/> as Microsoft.Extensions.AI generates it for strict output,
    /// with its references into the schema moved into <c>$defs</c>.
    /// </summary>
    public static JsonElement CreateJsonSchema(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var generated = AIJsonUtilities.CreateJsonSchema(type, inferenceOptions: StrictOptions);
        return MoveReferencesToDefinitions(generated);
    }

    /// <summary>
    /// Moves every schema that a <c>$ref</c> points to with a JSON pointer into the root's <c>$defs</c>
    /// and points every such reference, and the place the schema came from, to that entry. The deepest
    /// target moves first, so the pointers of the others stay valid.
    /// </summary>
    public static JsonElement MoveReferencesToDefinitions(JsonElement schema)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root)
        {
            return schema.Clone();
        }

        var targets = References(root)
            .Select(reference => reference["$ref"]!.GetValue<string>())
            .Where(target => target.StartsWith("#/", StringComparison.Ordinal)
                             && !target.StartsWith(DefinitionsPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(target => target.Count(character => character == '/'))
            .ThenBy(target => target, StringComparer.Ordinal)
            .ToArray();
        if (targets.Length == 0)
        {
            return schema.Clone();
        }

        var definitions = root["$defs"] as JsonObject ?? new JsonObject();
        root["$defs"] = definitions;
        foreach (var target in targets)
        {
            var name = DefinitionName(target, definitions);
            var reference = DefinitionsPrefix + name;
            if (!TryReplace(root, target, new JsonObject { ["$ref"] = reference }, out var moved))
            {
                continue;
            }

            definitions[name] = moved;
            foreach (var node in References(root).Where(node => node["$ref"]!.GetValue<string>() == target).ToArray())
            {
                // Only the reference stays: strict providers take no keywords next to $ref.
                foreach (var key in node.Select(property => property.Key).Where(key => key != "$ref").ToArray())
                {
                    node.Remove(key);
                }
                node["$ref"] = reference;
            }
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static IEnumerable<JsonObject> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                if (value["$ref"] is JsonValue reference && reference.TryGetValue<string>(out _))
                {
                    yield return value;
                }
                foreach (var property in value.ToArray())
                {
                    foreach (var nested in References(property.Value))
                    {
                        yield return nested;
                    }
                }
                break;
            case JsonArray list:
                foreach (var item in list.ToArray())
                {
                    foreach (var nested in References(item))
                    {
                        yield return nested;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Puts <paramref name="replacement"/> where <paramref name="pointer"/> points and returns the
    /// schema that was there.
    /// </summary>
    private static bool TryReplace(JsonObject root, string pointer, JsonNode replacement, out JsonNode? moved)
    {
        moved = null;
        var segments = pointer[2..].Split('/').Select(Unescape).ToArray();
        JsonNode? parent = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            parent = Child(parent, segments[index]);
            if (parent is null)
            {
                return false;
            }
        }

        var last = segments[^1];
        switch (parent)
        {
            case JsonObject value when value.ContainsKey(last):
                moved = value[last];
                value[last] = replacement;
                return moved is not null;
            case JsonArray list when int.TryParse(last, out var position) && position >= 0 && position < list.Count:
                moved = list[position];
                list[position] = replacement;
                return moved is not null;
            default:
                return false;
        }
    }

    private static JsonNode? Child(JsonNode? node, string segment)
    {
        return node switch
        {
            JsonObject value => value[segment],
            JsonArray list when int.TryParse(segment, out var position) && position >= 0 && position < list.Count => list[position],
            _ => null
        };
    }

    /// <summary>
    /// A readable, unique name from the pointer's property names, such as <c>onTrack</c> for
    /// <c>#/properties/onTrack/items</c>.
    /// </summary>
    private static string DefinitionName(string pointer, JsonObject definitions)
    {
        var names = pointer[2..]
            .Split('/')
            .Select(Unescape)
            .Where(segment => segment is not ("properties" or "items" or "anyOf" or "oneOf" or "allOf" or "prefixItems"))
            .Select(segment => new string(segment.Where(char.IsAsciiLetterOrDigit).ToArray()))
            .Where(segment => segment.Length > 0)
            .ToArray();
        var name = names.Length == 0 ? "definition" : string.Join("_", names);
        var unique = name;
        for (var suffix = 2; definitions.ContainsKey(unique); suffix++)
        {
            unique = name + "_" + suffix;
        }
        return unique;
    }

    private static string Unescape(string segment)
    {
        return segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }
}
