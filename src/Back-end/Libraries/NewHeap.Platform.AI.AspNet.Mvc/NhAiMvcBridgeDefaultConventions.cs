using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Decides how MVC actions become tools: their ids, descriptions, input schema and the
/// mapping from the flat tool input to the self-HTTP request.
/// </summary>
public interface INhAiBridgeConventions
{
    /// <summary>
    /// Tool id for an action: default "&lt;controller-kebab&gt;.&lt;action-kebab&gt;", collisions get
    /// "-by-&lt;route-param-names&gt;" appended (e.g. "order.get" and "order.get-by-id").
    /// </summary>
    string GetToolId(NhAiBridgeActionInfo action);

    /// <summary>Model-facing description when no [NhAiBridgeTool(Description)] and no XML summary exists.</summary>
    string GetDescription(NhAiBridgeActionInfo action);

    /// <summary>
    /// Maps the flat input object to route values, query string and body. Default: route
    /// parameters and [FromQuery] primitives as top-level properties, complex body as "body".
    /// Throw <see cref="NhAiBridgeInputException"/> for input that cannot be mapped.
    /// </summary>
    NhAiBridgeHttpRequest BuildRequest(NhAiBridgeActionInfo action, JsonElement input);

    /// <summary>JSON schema fragment for the input object; default derived via JsonSchemaExporter.</summary>
    JsonElement BuildInputSchema(NhAiBridgeActionInfo action);

    /// <summary>Serializes the body; default System.Text.Json web defaults.</summary>
    string SerializeBody(object body);

    /// <summary>
    /// Describes the filter, search, order and result fields of a read-only action for the
    /// bridge gateway. Described filter and order keys are enforced before the HTTP call;
    /// the interface default describes no fields, so nothing is enforced. The built-in MVC
    /// conventions publish metadata for the canonical NewHeap collection contract.
    /// </summary>
    NhAiBridgeQueryDescription DescribeQuery(NhAiBridgeActionInfo action)
    {
        return NhAiBridgeQueryDescription.Empty;
    }

    /// <summary>
    /// Whether the action is a paged collection that accepts <c>page</c>, <c>itemsPerPage</c>,
    /// <c>search</c>, <c>orderBy</c> and <c>filter</c>. The gateway offers such actions as
    /// <c>query</c>. The interface default recognizes actions that bind a NewHeap collection
    /// request model. The built-in MVC conventions additionally recognize registered collection
    /// contracts, including the canonical NewHeap result contract.
    /// </summary>
    bool IsCollectionAction(NhAiBridgeActionInfo action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Parameters.Any(parameter => parameter.IsCollectionRequest);
    }

    /// <summary>
    /// Maps the flat input of a gateway <c>countOnly</c> query to a request whose response
    /// reports <c>totalCount</c>. The interface default requests one item on the first page
    /// without ordering. The built-in MVC conventions additionally let the recognized
    /// <see cref="INhAiBridgeCollectionContractProvider"/> mark the request as count-only.
    /// Throw <see cref="NhAiBridgeInputException"/> for input that cannot be mapped.
    /// </summary>
    NhAiBridgeHttpRequest BuildCountRequest(NhAiBridgeActionInfo action, JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(action);
        return BuildRequest(action, NhAiBridgeCountInput.SinglePage(input));
    }
}

/// <summary>
/// The default bridge conventions. Collection actions use the NewHeap query contract:
/// <c>page</c>, <c>itemsPerPage</c> and <c>search</c> as query values and <c>orderBy</c> and
/// <c>filter</c> as JSON arrays. Register an <see cref="INhAiBridgeCollectionContractProvider"/>
/// to add another collection contract and an <see cref="INhAiBridgeBodySerializer"/> to align
/// request bodies with the host MVC serialization settings.
/// </summary>
public class NhAiMvcBridgeDefaultConventions : INhAiBridgeConventions
{
    private static readonly string[] CollectionPropertyNames = ["Page", "ItemsPerPage", "OrderBy", "Filter", "Search"];
    private readonly IReadOnlyList<INhAiBridgeCollectionContractProvider> _collectionContractProviders;
    private static readonly Regex RouteParameterPattern = new(
        @"\{\*{0,2}(?<name>[^}:=?]+)(?<rest>[^}]*)\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Options used to read tool input and to derive input schemas: web naming, string or
    /// numeric enums, and a reflection resolver.
    /// </summary>
    protected static JsonSerializerOptions InputSerializerOptions { get; } = CreateInputSerializerOptions();

    /// <summary>Options used by <see cref="SerializeBody"/>: System.Text.Json web defaults.</summary>
    protected static JsonSerializerOptions BodySerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public NhAiMvcBridgeDefaultConventions()
        : this([new NhAiNewHeapCollectionContractProvider()])
    {
    }

    public NhAiMvcBridgeDefaultConventions(
        IEnumerable<INhAiBridgeCollectionContractProvider> collectionContractProviders)
    {
        ArgumentNullException.ThrowIfNull(collectionContractProviders);
        _collectionContractProviders = collectionContractProviders.ToArray();
    }

    /// <summary>
    /// Returns <see langword="true"/> for a type with the NewHeap collection request properties
    /// <c>Page</c>, <c>ItemsPerPage</c>, <c>OrderBy</c>, <c>Filter</c> and <c>Search</c>.
    /// </summary>
    public static bool IsCollectionRequestType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var names = type.GetProperties().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CollectionPropertyNames.All(names.Contains);
    }

    /// <summary>Returns <see langword="true"/> for one of the collection request properties.</summary>
    public static bool IsCollectionPropertyName(string name)
    {
        return CollectionPropertyNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public virtual string GetToolId(NhAiBridgeActionInfo action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var toolId = NhAiMvcBridgeNames.ToKebabCase(action.ControllerName)
            + "."
            + NhAiMvcBridgeNames.ToKebabCase(action.ActionName);
        var routeParameters = action.RouteParameters.ToArray();
        if (action.ActionNameCollides && routeParameters.Length > 0)
        {
            toolId += "-by-" + string.Join(
                "-and-",
                routeParameters.Select(parameter => NhAiMvcBridgeNames.ToKebabCase(parameter.Name)));
        }
        return toolId;
    }

    public virtual string GetDescription(NhAiBridgeActionInfo action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var metadata = action.ActionDescriptor.EndpointMetadata;
        var summary = metadata.OfType<IEndpointSummaryMetadata>().LastOrDefault()?.Summary;
        var description = metadata.OfType<IEndpointDescriptionMetadata>().LastOrDefault()?.Description;
        var parts = new[] { summary, description }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim().TrimEnd('.') + ".")
            .ToArray();
        if (parts.Length > 0)
        {
            return string.Join(" ", parts);
        }
        return $"Calls {action.HttpMethod} /{action.RouteTemplate} as the signed-in user.";
    }

    public virtual JsonElement BuildInputSchema(NhAiBridgeActionInfo action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var properties = new JsonObject();
        var required = new JsonArray();
        var queryStringCollection = UsesQueryStringCollection(action);
        var queryDescription = DescribeQuery(action);
        if (queryStringCollection)
        {
            // The action reads the collection values from the query string itself.
            foreach (var property in CreateCollectionSchemaProperties(queryDescription))
            {
                properties[property.Key] = property.Value;
            }
        }
        foreach (var parameter in action.Parameters)
        {
            if (queryStringCollection && IsCollectionPropertyName(parameter.InputName))
            {
                // The collection fragment owns paging, search, ordering and filters.
                continue;
            }
            if (parameter.IsCollectionRequest)
            {
                foreach (var property in CreateCollectionSchemaProperties(queryDescription))
                {
                    properties[property.Key] = property.Value;
                }
                continue;
            }

            var name = parameter.Source == NhAiBridgeParameterSource.Body ? "body" : parameter.InputName;
            properties[name] = parameter.Source switch
            {
                NhAiBridgeParameterSource.Body when parameter.IsFreeForm => new JsonObject { ["type"] = "object" },
                NhAiBridgeParameterSource.FormFile => CreateFileSchema(parameter.ParameterType),
                _ => CreateTypeSchema(parameter.ParameterType, "#/properties/" + name)
            };
            if (parameter.IsRequired)
            {
                required.Add(name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        schema["additionalProperties"] = false;
        return JsonSerializer.SerializeToElement(schema, BodySerializerOptions);
    }

    public virtual NhAiBridgeHttpRequest BuildRequest(NhAiBridgeActionInfo action, JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (input.ValueKind == JsonValueKind.Undefined || input.ValueKind == JsonValueKind.Null)
        {
            input = JsonSerializer.SerializeToElement(new JsonObject(), BodySerializerOptions);
        }
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new NhAiBridgeInputException("The tool input must be a JSON object.");
        }

        var queryStringCollection = UsesQueryStringCollection(action);
        var known = KnownInputNames(action, queryStringCollection);
        foreach (var property in input.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                throw new NhAiBridgeInputException($"The tool input property '{Bounded(property.Name)}' is not supported.");
            }
        }

        var request = new NhAiBridgeHttpRequest(action.HttpMethod, BuildPath(action, input));
        foreach (var parameter in action.Parameters)
        {
            if (queryStringCollection && IsCollectionPropertyName(parameter.InputName))
            {
                continue;
            }

            switch (parameter.Source)
            {
                case NhAiBridgeParameterSource.Query when parameter.IsCollectionRequest:
                    AppendCollectionQuery(action, request, input);
                    break;
                case NhAiBridgeParameterSource.Query:
                    AppendValues(request.Query, parameter, input);
                    break;
                case NhAiBridgeParameterSource.Form:
                    AppendValues(request.FormFields, parameter, input);
                    break;
                case NhAiBridgeParameterSource.FormFile:
                    AppendFiles(request, parameter, input);
                    break;
                case NhAiBridgeParameterSource.Body:
                    request.Body = ReadBody(parameter, input);
                    break;
            }
        }
        if (queryStringCollection)
        {
            AppendCollectionQuery(action, request, input);
        }
        return request;
    }

    /// <summary>
    /// Returns whether a registered collection-contract provider recognizes the action, or the
    /// action binds a NewHeap collection request model. Register another provider for an API
    /// contract with different request or result shapes.
    /// </summary>
    public virtual bool IsCollectionAction(NhAiBridgeActionInfo action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TryDescribeCollection(action, out _)
            || action.Parameters.Any(parameter => parameter.IsCollectionRequest);
    }

    /// <summary>
    /// Builds the single-page request without ordering and lets the recognized collection
    /// contract provider mark it as count-only through
    /// <see cref="INhAiBridgeCollectionContractProvider.TryEncodeCountQuery"/>.
    /// </summary>
    public virtual NhAiBridgeHttpRequest BuildCountRequest(NhAiBridgeActionInfo action, JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(action);
        var request = BuildRequest(action, NhAiBridgeCountInput.SinglePage(input));
        if (TryGetCollectionProvider(action, out var provider, out _))
        {
            provider.TryEncodeCountQuery(action, request);
        }
        return request;
    }

    /// <summary>
    /// Uses the recognized collection contract to publish filter, order and result fields.
    /// Unrecognized collection actions expose no fields and remain searchable.
    /// </summary>
    public virtual NhAiBridgeQueryDescription DescribeQuery(NhAiBridgeActionInfo action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TryDescribeCollection(action, out var contract))
        {
            return contract.Query;
        }
        return NhAiBridgeQueryDescription.Empty with
        {
            Searchable = IsCollectionAction(action)
        };
    }

    public virtual string SerializeBody(object body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return JsonSerializer.Serialize(body, body.GetType(), BodySerializerOptions);
    }

    /// <summary>
    /// Appends the collection fragment in the NewHeap query contract. Override to encode
    /// paging, search, ordering and filters for a different API contract.
    /// </summary>
    protected virtual void AppendCollectionQuery(NhAiBridgeHttpRequest request, JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TryGetProperty(input, "page", out var page) && page.ValueKind != JsonValueKind.Null)
        {
            request.Query.Add(new("page", ReadInteger(page, "page").ToString(CultureInfo.InvariantCulture)));
        }
        if (TryGetProperty(input, "itemsPerPage", out var itemsPerPage) && itemsPerPage.ValueKind != JsonValueKind.Null)
        {
            request.Query.Add(new("itemsPerPage", ReadInteger(itemsPerPage, "itemsPerPage").ToString(CultureInfo.InvariantCulture)));
        }
        if (TryGetProperty(input, "search", out var search) && search.ValueKind == JsonValueKind.String)
        {
            request.Query.Add(new("search", search.GetString()!));
        }

        var orderBy = ReadCollectionItems(input, "orderBy", ["key", "direction"])
            .Select(item => new NhAiBridgeOrderBy(
                item["key"] ?? string.Empty,
                string.Equals(item["direction"], "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC"))
            .ToArray();
        if (orderBy.Length > 0)
        {
            request.Query.Add(new("orderBy", JsonSerializer.Serialize(orderBy, BodySerializerOptions)));
        }

        var filter = ReadCollectionItems(input, "filter", ["key", "operator", "value"])
            .Select(item => new NhAiBridgeFilter(
                item["key"] ?? string.Empty,
                item["operator"] ?? string.Empty,
                item["value"]))
            .ToArray();
        if (filter.Length > 0)
        {
            request.Query.Add(new("filter", JsonSerializer.Serialize(filter, BodySerializerOptions)));
        }
    }

    private void AppendCollectionQuery(
        NhAiBridgeActionInfo action,
        NhAiBridgeHttpRequest request,
        JsonElement input)
    {
        if (TryGetCollectionProvider(action, out var provider, out _)
            && provider.TryEncodeQuery(action, input, request))
        {
            return;
        }
        AppendCollectionQuery(request, input);
    }

    /// <summary>Creates the JSON schema of one parameter or body type with <see cref="JsonSchemaExporter"/>.</summary>
    protected virtual JsonNode CreateTypeSchema(Type type, string pointer)
    {
        ArgumentNullException.ThrowIfNull(type);
        var schema = InputSerializerOptions.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = ApplyAnnotations
        });
        RebaseReferences(schema, pointer);
        return schema;
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> CreateCollectionSchemaProperties(
        NhAiBridgeQueryDescription query)
    {
        yield return new("page", new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = 1,
            ["default"] = 1,
            ["description"] = "One-based page number."
        });
        yield return new("itemsPerPage", new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = 1,
            ["default"] = 20,
            ["description"] = "Number of items per page."
        });
        yield return new("search", new JsonObject
        {
            ["type"] = new JsonArray("string", "null"),
            ["description"] = "Free-text search."
        });
        yield return new("orderBy", new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["key"] = CreateBoundedStringSchema(query.OrderFields),
                    ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("asc", "desc") }
                },
                ["required"] = new JsonArray("key"),
                ["additionalProperties"] = false
            }
        });
        yield return new("filter", new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["key"] = CreateBoundedStringSchema(query.FilterFields.Select(field => field.Key)),
                    ["operator"] = CreateBoundedStringSchema(
                        query.FilterFields.SelectMany(field => field.Operators).Distinct(StringComparer.OrdinalIgnoreCase)),
                    ["value"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("key", "operator"),
                ["additionalProperties"] = false
            }
        });
    }

    private static JsonObject CreateBoundedStringSchema(IEnumerable<string> values)
    {
        var schema = new JsonObject { ["type"] = "string" };
        var items = values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (items.Length > 0)
        {
            schema["enum"] = new JsonArray(items.Select(item => JsonValue.Create(item)).ToArray());
        }
        return schema;
    }

    private static JsonObject CreateFileSchema(Type parameterType)
    {
        var file = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["fileName"] = new JsonObject { ["type"] = "string" },
                ["contentType"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                ["contentBase64"] = new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" }
            },
            ["required"] = new JsonArray("fileName", "contentBase64"),
            ["additionalProperties"] = false
        };
        return IsFileCollection(parameterType)
            ? new JsonObject { ["type"] = "array", ["items"] = file }
            : file;
    }

    /// <summary>A collection action without a bound collection model.</summary>
    private bool UsesQueryStringCollection(NhAiBridgeActionInfo action)
    {
        return !action.Parameters.Any(parameter => parameter.IsCollectionRequest)
            && IsCollectionAction(action);
    }

    private bool TryDescribeCollection(
        NhAiBridgeActionInfo action,
        out NhAiBridgeCollectionContract contract)
    {
        return TryGetCollectionProvider(action, out _, out contract);
    }

    private bool TryGetCollectionProvider(
        NhAiBridgeActionInfo action,
        out INhAiBridgeCollectionContractProvider provider,
        out NhAiBridgeCollectionContract contract)
    {
        foreach (var candidate in _collectionContractProviders)
        {
            if (candidate.TryDescribe(action, out contract))
            {
                provider = candidate;
                return true;
            }
        }
        provider = null!;
        contract = null!;
        return false;
    }

    private static HashSet<string> KnownInputNames(NhAiBridgeActionInfo action, bool queryStringCollection)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (queryStringCollection)
        {
            names.UnionWith(["page", "itemsPerPage", "search", "orderBy", "filter"]);
        }
        foreach (var parameter in action.Parameters)
        {
            if (parameter.IsCollectionRequest)
            {
                names.UnionWith(["page", "itemsPerPage", "search", "orderBy", "filter"]);
            }
            else if (parameter.Source == NhAiBridgeParameterSource.Body)
            {
                names.Add("body");
            }
            else
            {
                names.Add(parameter.InputName);
            }
        }
        return names;
    }

    private static string BuildPath(NhAiBridgeActionInfo action, JsonElement input)
    {
        var routeParameters = action.RouteParameters.ToDictionary(
            parameter => parameter.Name,
            StringComparer.OrdinalIgnoreCase);
        var path = RouteParameterPattern.Replace(action.RouteTemplate, match =>
        {
            var name = match.Groups["name"].Value;
            var catchAll = match.Value.StartsWith("{*", StringComparison.Ordinal);
            if (!routeParameters.TryGetValue(name, out var parameter)
                || !TryGetProperty(input, parameter.InputName, out var value)
                || value.ValueKind == JsonValueKind.Null)
            {
                if (match.Groups["rest"].Value.Contains('?', StringComparison.Ordinal) || catchAll)
                {
                    return string.Empty;
                }
                throw new NhAiBridgeInputException($"The route value '{Bounded(parameter?.InputName ?? name)}' is required.");
            }

            var text = ReadScalar(value, parameter.InputName);
            return catchAll
                ? string.Join("/", text.Split('/').Select(Uri.EscapeDataString))
                : Uri.EscapeDataString(text);
        });

        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }
        return path.Trim('/');
    }

    private static void AppendValues(
        IList<KeyValuePair<string, string>> target,
        NhAiBridgeParameterInfo parameter,
        JsonElement input)
    {
        if (!TryGetProperty(input, parameter.InputName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (parameter.IsRequired)
            {
                throw new NhAiBridgeInputException($"The input value '{Bounded(parameter.InputName)}' is required.");
            }
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                target.Add(new(parameter.Name, ReadScalar(item, parameter.InputName)));
            }
            return;
        }
        target.Add(new(parameter.Name, ReadScalar(value, parameter.InputName)));
    }

    private static void AppendFiles(NhAiBridgeHttpRequest request, NhAiBridgeParameterInfo parameter, JsonElement input)
    {
        if (!TryGetProperty(input, parameter.InputName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (parameter.IsRequired)
            {
                throw new NhAiBridgeInputException($"The file '{Bounded(parameter.InputName)}' is required.");
            }
            return;
        }

        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryGetProperty(item, "fileName", out var fileName)
                || fileName.ValueKind != JsonValueKind.String
                || !TryGetProperty(item, "contentBase64", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                throw new NhAiBridgeInputException($"The file '{Bounded(parameter.InputName)}' needs a fileName and contentBase64.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(content.GetString()!);
            }
            catch (FormatException)
            {
                throw new NhAiBridgeInputException($"The file '{Bounded(parameter.InputName)}' is not valid base64.");
            }

            var contentType = TryGetProperty(item, "contentType", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
            request.FormFiles.Add(new NhAiBridgeFormFile(parameter.Name, fileName.GetString()!, contentType, bytes));
        }
    }

    private static object? ReadBody(NhAiBridgeParameterInfo parameter, JsonElement input)
    {
        if (!TryGetProperty(input, "body", out var body) || body.ValueKind == JsonValueKind.Null)
        {
            if (parameter.IsRequired)
            {
                throw new NhAiBridgeInputException("The input value 'body' is required.");
            }
            return null;
        }

        if (parameter.IsFreeForm)
        {
            return body.Clone();
        }

        try
        {
            return body.Deserialize(parameter.ParameterType, InputSerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new NhAiBridgeInputException("The input value 'body' does not match the expected shape.");
        }
    }

    private static List<Dictionary<string, string?>> ReadCollectionItems(
        JsonElement input,
        string propertyName,
        string[] allowedKeys)
    {
        var result = new List<Dictionary<string, string?>>();
        if (!TryGetProperty(input, propertyName, out var items) || items.ValueKind == JsonValueKind.Null)
        {
            return result;
        }
        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new NhAiBridgeInputException($"The input value '{propertyName}' must be an array.");
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new NhAiBridgeInputException($"Every '{propertyName}' entry must be an object.");
            }

            var entry = allowedKeys.ToDictionary(key => key, _ => (string?)null, StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!entry.ContainsKey(property.Name))
                {
                    throw new NhAiBridgeInputException($"The '{propertyName}' property '{Bounded(property.Name)}' is not supported.");
                }
                entry[property.Name] = property.Value.ValueKind == JsonValueKind.Null
                    ? null
                    : ReadScalar(property.Value, propertyName);
            }
            if (string.IsNullOrWhiteSpace(entry["key"]))
            {
                throw new NhAiBridgeInputException($"Every '{propertyName}' entry needs a key.");
            }
            result.Add(entry);
        }
        return result;
    }

    private static int ReadInteger(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0)
        {
            return number;
        }
        throw new NhAiBridgeInputException($"The input value '{name}' must be a positive integer.");
    }

    private static string ReadScalar(JsonElement value, string name)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new NhAiBridgeInputException($"The input value '{Bounded(name)}' must be a string, number or boolean.")
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    private static bool IsFileCollection(Type type)
    {
        return type != typeof(Microsoft.AspNetCore.Http.IFormFile)
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    private static string Bounded(string value)
    {
        return value.Length <= 64 ? value : value[..64];
    }

    private static void RebaseReferences(JsonNode? node, string pointer)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToArray())
                {
                    if (string.Equals(property.Key, "$ref", StringComparison.Ordinal)
                        && property.Value is JsonValue reference
                        && reference.TryGetValue<string>(out var target)
                        && target.StartsWith('#'))
                    {
                        jsonObject["$ref"] = pointer + target[1..];
                    }
                    else
                    {
                        RebaseReferences(property.Value, pointer);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    RebaseReferences(item, pointer);
                }
                break;
        }
    }

    /// <summary>
    /// Adds <c>[Required]</c> properties to the object's <c>required</c> list and
    /// <c>[Description]</c> text to the property schema, so the model sees the same contract as
    /// the API's own validation.
    /// </summary>
    private static JsonNode ApplyAnnotations(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject schemaObject)
        {
            return schema;
        }

        var description = context.PropertyInfo?.AttributeProvider?
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: true)
            .OfType<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault()?.Description;
        if (!string.IsNullOrWhiteSpace(description) && !schemaObject.ContainsKey("description"))
        {
            schemaObject["description"] = description;
        }

        if (context.PropertyInfo is null && context.TypeInfo.Kind == JsonTypeInfoKind.Object)
        {
            var required = context.TypeInfo.Properties
                .Where(property => property.IsRequired
                    || (property.AttributeProvider?.IsDefined(
                        typeof(System.ComponentModel.DataAnnotations.RequiredAttribute),
                        inherit: true) ?? false))
                .Select(property => property.Name)
                .ToArray();
            if (required.Length > 0)
            {
                var list = schemaObject["required"] as JsonArray ?? new JsonArray();
                foreach (var name in required)
                {
                    if (!list.Any(item => item?.GetValue<string>() == name))
                    {
                        list.Add(name);
                    }
                }
                schemaObject["required"] = list;
            }
        }
        return schemaObject;
    }

    private static JsonSerializerOptions CreateInputSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            NumberHandling = JsonNumberHandling.Strict
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed record NhAiBridgeOrderBy(string Key, string Direction);

    private sealed record NhAiBridgeFilter(string Key, string Operator, string? Value);
}
