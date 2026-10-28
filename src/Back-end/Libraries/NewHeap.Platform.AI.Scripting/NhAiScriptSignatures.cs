using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// A field of a script's tool call that does not match the tool's signature: the path of the field in the
/// call's input object, such as <c>projectId</c> or <c>items[2].name</c>, and what is wrong with it.
/// </summary>
public sealed record NhAiScriptFieldError(string Field, string Error);

/// <summary>
/// The TypeScript-style signatures a script sees of its tools, generated from the tools' JSON schemas, and
/// the check of a call's arguments against the same schema. A call that does not match stops the script
/// with <see cref="NhAiScriptCodes.CallInvalid"/>, the exact signature and the field error, so the model
/// can correct the script in one step.
/// </summary>
public static class NhAiScriptSignatures
{
    private const int MaxDepth = 8;
    private const int MaxEnumValues = 32;
    private const int MaxOutputTypeLength = 600;
    private const int MaxSummaryLength = 240;
    private const int MaxPropertyDescriptionLength = 120;
    private const int MaxCheckedItems = 1_000;

    /// <summary>
    /// The declaration of one tool, for example
    /// <c>projects_search_v1(input: { limit?: number; query?: string | null }): Promise&lt;Result&lt;Array&lt;{ id: string /* uuid */; key: string; name: string }&gt;&gt;&gt;;</c>,
    /// after a comment with its description and, for a tool that changes something, how a call may end.
    /// </summary>
    public static string Declare(NhAiScriptToolOffer tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var builder = new StringBuilder();
        builder.Append("/** ").Append(Summary(tool)).Append(" */\n");
        builder.Append(Signature(tool));
        return builder.ToString();
    }

    /// <summary>
    /// The signature line of one tool without its comment, as a failing call returns it.
    /// </summary>
    public static string Signature(NhAiScriptToolOffer tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        using var input = Parse(tool.Descriptor.InputSchemaJson);
        using var output = Parse(tool.Descriptor.OutputSchemaJson);
        var inputType = new SchemaWriter(input.RootElement).Write(input.RootElement, 0, describe: true);
        var outputType = new SchemaWriter(output.RootElement).Write(output.RootElement, 0, describe: false);
        if (outputType.Length > MaxOutputTypeLength)
        {
            outputType = "unknown";
        }

        var optional = IsObjectSchema(input.RootElement) && !HasRequiredProperties(input.RootElement) ? "?" : string.Empty;
        return MemberName(tool.Name) + "(input" + optional + ": " + inputType + "): Promise<Result<" + outputType + ">>;";
    }

    /// <summary>
    /// The short form of a declaration when the description has no room for every full signature: the
    /// names of the input properties only, required ones without a question mark.
    /// </summary>
    public static string DeclareCompact(NhAiScriptToolOffer tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        using var input = Parse(tool.Descriptor.InputSchemaJson);
        var root = input.RootElement;
        var names = new List<string>();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            var required = RequiredNames(root);
            foreach (var property in properties.EnumerateObject())
            {
                names.Add(MemberName(property.Name) + (required.Contains(property.Name) ? string.Empty : "?"));
            }
        }

        return MemberName(tool.Name) + "(input: { " + string.Join(", ", names) + " }): Promise<Result<unknown>>;";
    }

    /// <summary>
    /// Checks the arguments of a call against the tool's input schema: <see langword="null"/> when they
    /// match, otherwise the first field that does not. Only names and types are reported, never values.
    /// The tool still validates its own input; this check only catches what the schema says.
    /// </summary>
    public static NhAiScriptFieldError? Validate(NhAiScriptToolOffer tool, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        using var input = Parse(tool.Descriptor.InputSchemaJson);
        return new SchemaValidator(input.RootElement).Validate(input.RootElement, arguments, string.Empty, 0);
    }

    internal static string MemberName(string name)
    {
        return IsIdentifier(name) ? name : JsonSerializer.Serialize(name);
    }

    internal static string Accessor(string name)
    {
        return IsIdentifier(name) ? "tools." + name : "tools[" + JsonSerializer.Serialize(name) + "]";
    }

    private static string Summary(NhAiScriptToolOffer tool)
    {
        var description = Bound(OneLine(tool.Descriptor.Description), MaxSummaryLength);
        var effect = tool.Descriptor.Effect switch
        {
            NhAiToolEffect.ReadOnly => string.Empty,
            NhAiToolEffect.Destructive => "Destructive; it may be recorded as a proposal that waits for approval. ",
            _ => "Changes data; it may be recorded as a proposal that waits for approval. "
        };
        return (effect + description).Replace("*/", "* /", StringComparison.Ordinal);
    }

    private static JsonDocument Parse(string? schemaJson)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(schemaJson) ? "{}" : schemaJson);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    /// <summary>
    /// Whether a tool's input is an object of properties, as almost every tool's is, rather than one
    /// value such as a string.
    /// </summary>
    internal static bool IsObjectSchema(string? schemaJson)
    {
        using var document = Parse(schemaJson);
        return IsObjectSchema(document.RootElement);
    }

    private static bool IsObjectSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        var types = Types(schema);
        return types.Count == 0
            ? !schema.TryGetProperty("$ref", out _) || schema.TryGetProperty("properties", out _)
            : types.Contains("object");
    }

    private static bool HasRequiredProperties(JsonElement schema)
    {
        return RequiredNames(schema).Count > 0;
    }

    private static HashSet<string> RequiredNames(JsonElement schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String)
                {
                    names.Add(name.GetString()!);
                }
            }
        }

        return names;
    }

    private static bool IsIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] is '_' or '$'))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '$'))
            {
                return false;
            }
        }

        return true;
    }

    private static string OneLine(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Bound(string text, int length)
    {
        return text.Length <= length ? text : text[..(length - 3)] + "...";
    }

    private static string Literal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => "unknown"
        };
    }

    private static bool TryResolve(JsonElement root, JsonElement schema, out JsonElement resolved, out string? reference)
    {
        resolved = schema;
        reference = null;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("$ref", out var refValue)
            || refValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        reference = refValue.GetString()!;
        if (string.Equals(reference, "#", StringComparison.Ordinal))
        {
            resolved = root;
            return true;
        }
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        var current = root;
        foreach (var segment in reference[2..].Split('/'))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return false;
            }
        }

        resolved = current;
        return true;
    }

    private static IReadOnlyList<string> Types(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
        {
            return [];
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return [type.GetString()!];
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
        }

        return [];
    }

    private static string? Format(JsonElement schema)
    {
        return schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("format", out var format)
            && format.ValueKind == JsonValueKind.String
                ? format.GetString()
                : null;
    }

    private static IReadOnlyList<JsonElement> Branches(JsonElement schema, string keyword)
    {
        return schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty(keyword, out var branches)
            && branches.ValueKind == JsonValueKind.Array
                ? branches.EnumerateArray().ToArray()
                : [];
    }

    private static bool IsNullSchema(JsonElement schema)
    {
        var types = Types(schema);
        return types.Count == 1 && string.Equals(types[0], "null", StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes TypeScript-style types for the subset of JSON Schema that tool contracts use.
    /// </summary>
    private sealed class SchemaWriter(JsonElement root)
    {
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);

        public string Write(JsonElement schema, int depth, bool describe)
        {
            if (depth > MaxDepth)
            {
                return "unknown";
            }
            if (schema.ValueKind == JsonValueKind.True)
            {
                return "unknown";
            }
            if (schema.ValueKind == JsonValueKind.False)
            {
                return "never";
            }
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return "unknown";
            }

            if (TryResolve(root, schema, out var resolved, out var reference))
            {
                if (!_visiting.Add(reference!))
                {
                    return "object";
                }

                try
                {
                    return Write(resolved, depth + 1, describe);
                }
                finally
                {
                    _visiting.Remove(reference!);
                }
            }
            if (reference is not null)
            {
                return "unknown";
            }

            if (schema.TryGetProperty("const", out var constant))
            {
                return Literal(constant);
            }
            if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                var literals = values.EnumerateArray().Select(Literal).Distinct(StringComparer.Ordinal).ToArray();
                if (literals.Length is > 0 and <= MaxEnumValues)
                {
                    return string.Join(" | ", literals);
                }
            }

            var union = Branches(schema, "anyOf").Concat(Branches(schema, "oneOf")).ToArray();
            if (union.Length > 0)
            {
                return Union(union.Select(branch => Write(branch, depth + 1, describe)));
            }

            var all = Branches(schema, "allOf");
            if (all.Count > 0)
            {
                return string.Join(" & ", all.Select(branch => Wrap(Write(branch, depth + 1, describe))));
            }

            var types = Types(schema);
            if (types.Count == 0)
            {
                return schema.TryGetProperty("properties", out _) ? WriteObject(schema, depth, describe) : "unknown";
            }

            return Union(types.Select(type => WriteType(schema, type, depth, describe)));
        }

        private string WriteType(JsonElement schema, string type, int depth, bool describe)
        {
            switch (type)
            {
                case "string":
                    return Format(schema) is { Length: > 0 and <= 32 } format
                        ? "string /* " + format.Replace("*/", string.Empty, StringComparison.Ordinal) + " */"
                        : "string";
                case "integer":
                case "number":
                    return "number";
                case "boolean":
                    return "boolean";
                case "null":
                    return "null";
                case "array":
                    return schema.TryGetProperty("items", out var items)
                        ? "Array<" + Write(items, depth + 1, false) + ">"
                        : "unknown[]";
                case "object":
                    return WriteObject(schema, depth, describe);
                default:
                    return "unknown";
            }
        }

        private string WriteObject(JsonElement schema, int depth, bool describe)
        {
            var hasProperties = schema.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                && properties.EnumerateObject().Any();
            if (!hasProperties)
            {
                if (schema.TryGetProperty("additionalProperties", out var additional))
                {
                    if (additional.ValueKind == JsonValueKind.False)
                    {
                        return "{}";
                    }
                    if (additional.ValueKind == JsonValueKind.Object)
                    {
                        return "Record<string, " + Write(additional, depth + 1, false) + ">";
                    }
                }

                return "Record<string, unknown>";
            }

            var required = RequiredNames(schema);
            var members = new List<string>();
            foreach (var property in properties.EnumerateObject())
            {
                var member = new StringBuilder();
                if (describe
                    && property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("description", out var description)
                    && description.ValueKind == JsonValueKind.String
                    && OneLine(description.GetString()) is { Length: > 0 } text)
                {
                    member.Append("/** ").Append(Bound(text, MaxPropertyDescriptionLength).Replace("*/", "* /", StringComparison.Ordinal)).Append(" */ ");
                }

                member.Append(MemberName(property.Name));
                if (!required.Contains(property.Name))
                {
                    member.Append('?');
                }
                member.Append(": ").Append(Write(property.Value, depth + 1, false));
                members.Add(member.ToString());
            }

            return "{ " + string.Join("; ", members) + " }";
        }

        private static string Union(IEnumerable<string> types)
        {
            var distinct = types.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Contains("unknown"))
            {
                return "unknown";
            }

            // null last, as TypeScript code usually writes it.
            var ordered = distinct.Where(type => type != "null").Concat(distinct.Where(type => type == "null"));
            return string.Join(" | ", ordered);
        }

        private static string Wrap(string type)
        {
            return type.Contains(" | ", StringComparison.Ordinal) ? "(" + type + ")" : type;
        }
    }

    /// <summary>
    /// Checks a value against the subset of JSON Schema the writer renders.
    /// </summary>
    private sealed class SchemaValidator(JsonElement root)
    {
        public NhAiScriptFieldError? Validate(JsonElement schema, JsonElement value, string path, int depth)
        {
            if (depth > MaxDepth || schema.ValueKind == JsonValueKind.True)
            {
                return null;
            }
            if (schema.ValueKind == JsonValueKind.False)
            {
                return Error(path, "not allowed");
            }
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryResolve(root, schema, out var resolved, out var reference))
            {
                return Validate(resolved, value, path, depth + 1);
            }
            if (reference is not null)
            {
                return null;
            }

            if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, value))
            {
                return Error(path, "expected " + Literal(constant));
            }
            if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                if (!values.EnumerateArray().Any(item => JsonEquals(item, value)))
                {
                    var literals = values.EnumerateArray().Take(MaxEnumValues).Select(Literal);
                    return Error(path, "expected one of " + string.Join(", ", literals));
                }
            }

            var union = Branches(schema, "anyOf").Concat(Branches(schema, "oneOf")).ToArray();
            if (union.Length > 0)
            {
                var error = ValidateUnion(schema, union, value, path, depth);
                if (error is not null)
                {
                    return error;
                }
            }
            foreach (var branch in Branches(schema, "allOf"))
            {
                var error = Validate(branch, value, path, depth + 1);
                if (error is not null)
                {
                    return error;
                }
            }

            var types = Types(schema);
            if (types.Count == 0)
            {
                return schema.TryGetProperty("properties", out _) && value.ValueKind == JsonValueKind.Object
                    ? ValidateObject(schema, value, path, depth)
                    : null;
            }

            var matching = types.FirstOrDefault(type => Matches(type, value));
            if (matching is null && types.Contains("integer") && value.ValueKind == JsonValueKind.Number)
            {
                return Error(path, "expected an integer, got a number with a fraction");
            }
            if (matching is null)
            {
                var expected = new SchemaWriter(root).Write(schema, 0, describe: false);
                return Error(path, "expected " + expected + ", got " + Describe(value));
            }

            return matching switch
            {
                "string" => ValidateFormat(schema, value, path),
                "array" => ValidateArray(schema, value, path, depth),
                "object" => ValidateObject(schema, value, path, depth),
                _ => null
            };
        }

        private NhAiScriptFieldError? ValidateUnion(
            JsonElement schema,
            IReadOnlyList<JsonElement> union,
            JsonElement value,
            string path,
            int depth)
        {
            var nonNull = union.Where(branch => !IsNullSchema(branch)).ToArray();
            if (value.ValueKind == JsonValueKind.Null && nonNull.Length < union.Count)
            {
                return null;
            }
            if (nonNull.Length == 1)
            {
                // A nullable type: report the field error of the one real type.
                return Validate(nonNull[0], value, path, depth + 1);
            }
            if (union.Any(branch => Validate(branch, value, path, depth + 1) is null))
            {
                return null;
            }

            var expected = new SchemaWriter(root).Write(schema, 0, describe: false);
            return Error(path, "expected " + expected + ", got " + Describe(value));
        }

        private NhAiScriptFieldError? ValidateObject(JsonElement schema, JsonElement value, string path, int depth)
        {
            var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object
                ? declared
                : default;
            var names = properties.ValueKind == JsonValueKind.Object
                ? properties.EnumerateObject().Select(property => property.Name).ToArray()
                : [];

            foreach (var required in RequiredNames(schema).OrderBy(name => name, StringComparer.Ordinal))
            {
                if (!value.TryGetProperty(required, out _))
                {
                    return Error(Join(path, required), "required");
                }
            }

            var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    var error = Validate(propertySchema, property.Value, Join(path, property.Name), depth + 1);
                    if (error is not null)
                    {
                        return error;
                    }
                    continue;
                }

                if (closed)
                {
                    var suggestion = Suggest(property.Name, names);
                    return Error(
                        Join(path, property.Name),
                        suggestion is null
                            ? "not a property of this input"
                            : "not a property of this input; did you mean '" + suggestion + "'?");
                }
                if (additional.ValueKind == JsonValueKind.Object)
                {
                    var error = Validate(additional, property.Value, Join(path, property.Name), depth + 1);
                    if (error is not null)
                    {
                        return error;
                    }
                }
            }

            return null;
        }

        private NhAiScriptFieldError? ValidateArray(JsonElement schema, JsonElement value, string path, int depth)
        {
            if (!schema.TryGetProperty("items", out var items))
            {
                return null;
            }

            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (index >= MaxCheckedItems)
                {
                    break;
                }

                var error = Validate(items, item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", depth + 1);
                if (error is not null)
                {
                    return error;
                }
                index++;
            }

            return null;
        }

        private static NhAiScriptFieldError? ValidateFormat(JsonElement schema, JsonElement value, string path)
        {
            var text = value.GetString() ?? string.Empty;
            return Format(schema) switch
            {
                "uuid" when !Guid.TryParse(text, out _) => Error(path, "expected a uuid string"),
                "date-time" when !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) =>
                    Error(path, "expected an ISO 8601 date-time string"),
                _ => null
            };
        }

        private static bool Matches(string type, JsonElement value)
        {
            return type switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                "array" => value.ValueKind == JsonValueKind.Array,
                "object" => value.ValueKind == JsonValueKind.Object,
                _ => true
            };
        }

        private static bool IsInteger(JsonElement value)
        {
            return value.TryGetInt64(out _)
                || (value.TryGetDecimal(out var number) && decimal.Truncate(number) == number);
        }

        private static string Describe(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => IsInteger(value) ? "integer" : "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                JsonValueKind.Array => "array",
                JsonValueKind.Object => "object",
                _ => "undefined"
            };
        }

        private static bool JsonEquals(JsonElement left, JsonElement right)
        {
            return JsonElement.DeepEquals(left, right);
        }

        private static string Join(string path, string name)
        {
            var member = IsIdentifier(name) ? name : "[" + JsonSerializer.Serialize(name) + "]";
            if (path.Length == 0)
            {
                return member;
            }

            return member.StartsWith('[') ? path + member : path + "." + member;
        }

        private static string? Suggest(string name, IReadOnlyList<string> candidates)
        {
            string? best = null;
            var bestDistance = int.MaxValue;
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                var distance = Distance(name.ToLowerInvariant(), candidate.ToLowerInvariant());
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return bestDistance <= Math.Max(2, name.Length / 3) ? best : null;
        }

        private static int Distance(string left, string right)
        {
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (var column = 0; column <= right.Length; column++)
            {
                previous[column] = column;
            }

            for (var row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (var column = 1; column <= right.Length; column++)
                {
                    var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                    current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[right.Length];
        }

        private static NhAiScriptFieldError Error(string path, string error)
        {
            return new NhAiScriptFieldError(path.Length == 0 ? "input" : path, error);
        }
    }
}
