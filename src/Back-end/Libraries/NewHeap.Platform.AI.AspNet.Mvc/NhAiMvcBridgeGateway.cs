using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>The gateway configuration built by <see cref="NhAiMvcBridgeGatewayBuilder"/>.</summary>
public sealed class NhAiMvcBridgeGatewayOptions
{
    /// <summary>The gateway tool set id; defaults to <c>&lt;bridge tool set&gt;-gateway</c>.</summary>
    public string? ToolSetId { get; internal set; }

    public Type? ResourceDescriberType { get; internal set; }
}

/// <summary>
/// Configures the bridge gateway: four read-only tools over a searchable catalog of the
/// read-only bridge actions, grouped per resource.
/// </summary>
public sealed class NhAiMvcBridgeGatewayBuilder
{
    private readonly IServiceCollection _services;
    private readonly NhAiMvcBridgeGatewayOptions _options;

    internal NhAiMvcBridgeGatewayBuilder(IServiceCollection services, NhAiMvcBridgeGatewayOptions options)
    {
        _services = services;
        _options = options;
    }

    /// <summary>The dash-case tool set id of the gateway tools, such as <c>sample-api-gateway</c>.</summary>
    public NhAiMvcBridgeGatewayBuilder UseGatewayToolSetId(string toolSetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolSetId);
        NhAiMvcBridgeNames.ValidateSegment(toolSetId, nameof(toolSetId));
        _options.ToolSetId = toolSetId;
        return this;
    }

    /// <summary>
    /// States the fixed rule explicitly: only read-only bridge descriptors are reachable through
    /// the gateway. Mutations stay explicit bridge or curated tools with approval.
    /// </summary>
    public NhAiMvcBridgeGatewayBuilder IncludeReadOnlyOnly()
    {
        return this;
    }

    /// <summary>Adds titles, descriptions or fields per resource.</summary>
    public NhAiMvcBridgeGatewayBuilder UseResourceDescriber<TDescriber>()
        where TDescriber : class, INhAiBridgeResourceDescriber
    {
        _services.TryAddScoped<TDescriber>();
        _options.ResourceDescriberType = typeof(TDescriber);
        return this;
    }
}

/// <summary>Adjusts the model-facing description of one gateway resource.</summary>
public interface INhAiBridgeResourceDescriber
{
    /// <summary>Returns the description to publish, typically a copy with added text or fields.</summary>
    NhAiBridgeResourceDescription Describe(NhAiBridgeResourceDescription description);
}

/// <summary>A filter field the API accepts for a collection action.</summary>
public sealed record NhAiBridgeFilterField(
    string Key,
    string Type,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string>? EnumValues = null);

/// <summary>A field of the items an action returns.</summary>
public sealed record NhAiBridgeResultField(
    string Key,
    string Type,
    string? Description = null);

/// <summary>
/// The query capabilities of a read-only action, from <see cref="INhAiBridgeConventions.DescribeQuery"/>.
/// </summary>
public sealed record NhAiBridgeQueryDescription
{
    public static NhAiBridgeQueryDescription Empty { get; } = new();

    public IReadOnlyList<NhAiBridgeFilterField> FilterFields { get; init; } = [];

    public bool Searchable { get; init; }

    public IReadOnlyList<string> OrderFields { get; init; } = [];

    public IReadOnlyList<NhAiBridgeResultField> ResultFields { get; init; } = [];
}

/// <summary>One entry of the <c>search-resources</c> result.</summary>
public sealed record NhAiBridgeResourceSummary(
    string Resource,
    string Title,
    string Summary,
    IReadOnlyList<string> Operations);

/// <summary>The query shape of a resource in <c>describe-resource</c>.</summary>
public sealed record NhAiBridgeResourceQuery
{
    public IReadOnlyList<NhAiBridgeFilterField> FilterFields { get; init; } = [];

    public bool Searchable { get; init; }

    public IReadOnlyList<string> OrderFields { get; init; } = [];

    /// <summary>JSON schema of the additional parameters the query accepts in <c>parameters</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ExtraParameters { get; init; }
}

/// <summary>The get shape of a resource in <c>describe-resource</c>.</summary>
public sealed record NhAiBridgeResourceGet(string IdParameter)
{
    /// <summary>JSON schema of the additional parameters the get accepts in <c>parameters</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ExtraParameters { get; init; }
}

/// <summary>The <c>describe-resource</c> result.</summary>
public sealed record NhAiBridgeResourceDescription
{
    public required string Resource { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NhAiBridgeResourceQuery? Query { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NhAiBridgeResourceGet? Get { get; init; }

    public IReadOnlyList<NhAiBridgeResultField> ResultFields { get; init; } = [];
}

/// <summary>One operation of a gateway resource and the bridge descriptor it executes.</summary>
internal sealed record NhAiBridgeGatewayOperation(
    string Kind,
    NhAiToolDescriptor Descriptor,
    NhAiBridgeActionInfo Action,
    NhAiBridgeQueryDescription Query,
    string? IdInputName);

/// <summary>A read-only API resource: its query and/or get operation.</summary>
internal sealed record NhAiBridgeGatewayResource(
    string Resource,
    string Title,
    string Summary,
    IReadOnlyList<NhAiBridgeGatewayOperation> Operations)
{
    public NhAiBridgeGatewayOperation? Find(string kind)
    {
        return Operations.FirstOrDefault(operation => string.Equals(operation.Kind, kind, StringComparison.Ordinal));
    }
}

/// <summary>The gateway tools and their resources, built once with the bridge catalog.</summary>
internal sealed record NhAiMvcBridgeGatewayModel(
    string ToolSetId,
    IReadOnlyDictionary<string, NhAiBridgeGatewayResource> Resources,
    IReadOnlyDictionary<string, string> ToolKinds,
    IReadOnlyList<NhAiToolDescriptor> Descriptors,
    Type? ResourceDescriberType);

internal static class NhAiMvcBridgeGatewayKinds
{
    public const string SearchResources = "search-resources";
    public const string DescribeResource = "describe-resource";
    public const string Query = "query";
    public const string Get = "get";
}

/// <summary>Builds the gateway resources and tool descriptors from the bridge descriptors.</summary>
internal static class NhAiMvcBridgeGatewayBuilderLogic
{
    private const int MaxExportNameLength = 64;
    private static readonly string[] CollectionInputNames = ["page", "itemsPerPage", "search", "orderBy", "filter"];

    public static NhAiMvcBridgeGatewayModel Build(
        NhAiMvcBridgeOptions options,
        IReadOnlyList<NhAiToolDescriptor> bridgeDescriptors,
        IReadOnlyDictionary<string, NhAiBridgeActionInfo> actions,
        INhAiBridgeConventions conventions,
        NhAiToolExposure exposure)
    {
        var gatewayOptions = options.Gateway!;
        var toolSetId = gatewayOptions.ToolSetId ?? options.ToolSetId + "-gateway";
        if (!NhAiMvcBridgeNames.IsSegment(toolSetId))
        {
            throw new InvalidOperationException(
                $"The API bridge gateway tool set id '{toolSetId}' must use lowercase dash-case.");
        }

        var resources = BuildResources(bridgeDescriptors, actions, conventions);
        var resourceMaterial = string.Join(
            "\n",
            resources.Values
                .OrderBy(resource => resource.Resource, StringComparer.Ordinal)
                .Select(resource => resource.Resource + ":" + string.Join(
                    ",",
                    resource.Operations.Select(operation => operation.Kind + "=" + operation.Descriptor.Id + "@" + operation.Descriptor.ContractHash))));

        var descriptors = new List<NhAiToolDescriptor>();
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, description, inputSchema, outputSchema) in ToolDefinitions())
        {
            var id = toolSetId + "." + kind;
            var exportName = toolSetId + "_" + kind + "_v" + options.ContractVersion.ToString(CultureInfo.InvariantCulture);
            if (exportName.Length > MaxExportNameLength)
            {
                throw new InvalidOperationException(
                    $"API bridge gateway export name '{exportName}' exceeds {MaxExportNameLength} characters. Use a shorter gateway tool set id.");
            }

            var defaults = options.ToolDefaults;
            descriptors.Add(new NhAiToolDescriptor(
                id,
                options.ContractVersion,
                description,
                typeof(JsonElement),
                kind is NhAiMvcBridgeGatewayKinds.Query or NhAiMvcBridgeGatewayKinds.Get
                    ? typeof(NhAiBridgeResponse)
                    : typeof(JsonElement),
                NhAiToolEffect.ReadOnly,
                exposure,
                true,
                [])
            {
                ExportName = exportName,
                CatalogId = options.ToolSetId!,
                CatalogVersion = options.ContractVersion,
                DeclaringAssembly = typeof(NhAiMvcBridgeGatewayBuilderLogic).Assembly.GetName().Name ?? string.Empty,
                InputSchemaJson = inputSchema,
                OutputSchemaJson = outputSchema,
                SchemaHash = Hash(inputSchema + "\n" + outputSchema),
                ContractHash = Hash(id + "\n" + inputSchema + "\n" + resourceMaterial),
                Approval = NhAiApprovalRequirement.PolicyControlled,
                Idempotency = NhAiIdempotencySupport.None,
                Timeout = TimeSpan.FromSeconds(defaults.TimeoutSeconds),
                MaxConcurrency = defaults.MaxConcurrency,
                MaxInputBytes = defaults.MaxInputBytes,
                MaxResultBytes = defaults.MaxResultBytes
            });
            kinds.Add(id, kind);
        }

        return new NhAiMvcBridgeGatewayModel(
            toolSetId,
            resources,
            kinds,
            descriptors,
            gatewayOptions.ResourceDescriberType);
    }

    private static Dictionary<string, NhAiBridgeGatewayResource> BuildResources(
        IReadOnlyList<NhAiToolDescriptor> bridgeDescriptors,
        IReadOnlyDictionary<string, NhAiBridgeActionInfo> actions,
        INhAiBridgeConventions conventions)
    {
        // Only reads that need no approval are reachable: the gateway never offers a mutation.
        var candidates = bridgeDescriptors
            .Where(descriptor => descriptor.Effect == NhAiToolEffect.ReadOnly
                && descriptor.Approval != NhAiApprovalRequirement.Required
                && actions.ContainsKey(descriptor.Id))
            .Select(descriptor => (Descriptor: descriptor, Action: actions[descriptor.Id]))
            .ToArray();

        var operations = new List<(string Resource, string Title, NhAiBridgeGatewayOperation Operation)>();
        foreach (var controller in candidates
            .GroupBy(candidate => candidate.Action.ControllerName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var baseName = NhAiMvcBridgeNames.ToKebabCase(controller.Key);
            var baseTitle = Humanize(controller.Key);
            var queries = controller
                .Where(candidate => IsQuery(candidate.Action))
                .OrderBy(candidate => IsPrimaryName(candidate.Action.ActionName) ? 0 : 1)
                .ThenBy(candidate => candidate.Action.ActionName, StringComparer.Ordinal)
                .ToArray();
            var gets = controller
                .Where(candidate => IsGet(candidate.Action))
                .OrderBy(candidate => IsPrimaryName(candidate.Action.ActionName) ? 0 : 1)
                .ThenBy(candidate => candidate.Action.ActionName, StringComparer.Ordinal)
                .ToArray();

            AddOperations(operations, queries, NhAiMvcBridgeGatewayKinds.Query, baseName, baseTitle, conventions);
            AddOperations(operations, gets, NhAiMvcBridgeGatewayKinds.Get, baseName, baseTitle, conventions);
        }

        var resources = new Dictionary<string, NhAiBridgeGatewayResource>(StringComparer.Ordinal);
        foreach (var group in operations.GroupBy(item => item.Resource, StringComparer.Ordinal))
        {
            var ordered = group
                .Select(item => item.Operation)
                .OrderBy(operation => operation.Kind == NhAiMvcBridgeGatewayKinds.Query ? 0 : 1)
                .ToArray();
            resources.Add(group.Key, new NhAiBridgeGatewayResource(
                group.Key,
                group.First().Title,
                ordered[0].Descriptor.Description,
                ordered));
        }
        return resources;
    }

    private static void AddOperations(
        List<(string Resource, string Title, NhAiBridgeGatewayOperation Operation)> operations,
        IReadOnlyList<(NhAiToolDescriptor Descriptor, NhAiBridgeActionInfo Action)> candidates,
        string kind,
        string baseName,
        string baseTitle,
        INhAiBridgeConventions conventions)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            var (descriptor, action) = candidates[index];
            var resource = baseName;
            var title = baseTitle;
            if (index > 0)
            {
                var suffix = NhAiMvcBridgeNames.ToKebabCase(action.ActionName);
                if (suffix.StartsWith("get-", StringComparison.Ordinal))
                {
                    suffix = suffix["get-".Length..];
                }
                resource = baseName + "-" + suffix;
                title = baseTitle + " (" + Humanize(action.ActionName).ToLowerInvariant() + ")";
            }
            while (operations.Any(item => item.Resource == resource && item.Operation.Kind == kind))
            {
                resource += "-" + NhAiMvcBridgeNames.ToKebabCase(action.ActionName);
            }

            var idInputName = kind == NhAiMvcBridgeGatewayKinds.Get
                ? action.RouteParameters.Single().InputName
                : null;
            operations.Add((resource, title, new NhAiBridgeGatewayOperation(
                kind,
                descriptor,
                action,
                conventions.DescribeQuery(action) ?? NhAiBridgeQueryDescription.Empty,
                idInputName)));
        }
    }

    public static JsonElement? ExtraParameters(NhAiBridgeGatewayOperation operation)
    {
        var schema = JsonNode.Parse(operation.Descriptor.InputSchemaJson)!.AsObject();
        if (schema["properties"] is not JsonObject properties)
        {
            return null;
        }

        var excluded = operation.Kind == NhAiMvcBridgeGatewayKinds.Query
            ? CollectionInputNames
            : [operation.IdInputName!];
        var extra = new JsonObject();
        foreach (var property in properties)
        {
            if (!excluded.Contains(property.Key, StringComparer.Ordinal))
            {
                extra[property.Key] = property.Value?.DeepClone();
            }
        }
        if (extra.Count == 0)
        {
            return null;
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = extra,
            ["additionalProperties"] = false
        };
        if (schema["required"] is JsonArray required)
        {
            var extraRequired = new JsonArray(required
                .Select(item => item?.GetValue<string>())
                .Where(name => name is not null && extra.ContainsKey(name))
                .Select(name => (JsonNode?)JsonValue.Create(name))
                .ToArray());
            if (extraRequired.Count > 0)
            {
                result["required"] = extraRequired;
            }
        }
        return JsonSerializer.SerializeToElement(result);
    }

    public static bool IsCollectionInputName(string name)
    {
        return CollectionInputNames.Contains(name, StringComparer.Ordinal);
    }

    private static bool IsQuery(NhAiBridgeActionInfo action)
    {
        return action.Parameters.Any(parameter => parameter.IsCollectionRequest)
            && !action.RouteParameters.Any()
            && action.BodyParameter is null;
    }

    private static bool IsGet(NhAiBridgeActionInfo action)
    {
        return action.RouteParameters.Count() == 1
            && action.BodyParameter is null
            && !action.Parameters.Any(parameter => parameter.IsCollectionRequest);
    }

    private static bool IsPrimaryName(string actionName)
    {
        return actionName is "Get" or "GetById" or "GetAll" or "List";
    }

    private static string Humanize(string pascal)
    {
        var words = NhAiMvcBridgeNames.ToKebabCase(pascal).Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return pascal;
        }
        var text = string.Join(" ", words);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static IEnumerable<(string Kind, string Description, string InputSchema, string OutputSchema)> ToolDefinitions()
    {
        const string filterItems = "{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\"},\"operator\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}},\"required\":[\"key\",\"operator\"],\"additionalProperties\":false}";
        const string orderItems = "{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\"},\"direction\":{\"type\":\"string\",\"enum\":[\"asc\",\"desc\"]}},\"required\":[\"key\"],\"additionalProperties\":false}";
        yield return (
            NhAiMvcBridgeGatewayKinds.SearchResources,
            "Searches the read-only API resources you may use by name, title, summary and field names, and returns their names and operations. Call describe-resource before building a query.",
            "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Words to look for.\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20,\"default\":10}},\"required\":[\"query\"],\"additionalProperties\":false}",
            "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"resource\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"},\"summary\":{\"type\":\"string\"},\"operations\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"query\",\"get\"]}}}}}");
        yield return (
            NhAiMvcBridgeGatewayKinds.DescribeResource,
            "Describes one read-only API resource: its filter, search and order fields, extra parameters, the id parameter for get, and result fields.",
            "{\"type\":\"object\",\"properties\":{\"resource\":{\"type\":\"string\"}},\"required\":[\"resource\"],\"additionalProperties\":false}",
            "{\"type\":\"object\"}");
        yield return (
            NhAiMvcBridgeGatewayKinds.Query,
            "Queries a read-only API resource collection as the signed-in user with paging, search, filters and ordering. Use describe-resource first; unknown filter or order keys are rejected.",
            "{\"type\":\"object\",\"properties\":{"
                + "\"resource\":{\"type\":\"string\"},"
                + "\"page\":{\"type\":\"integer\",\"minimum\":1},"
                + "\"itemsPerPage\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},"
                + "\"search\":{\"type\":[\"string\",\"null\"]},"
                + "\"filter\":{\"type\":\"array\",\"items\":" + filterItems + "},"
                + "\"orderBy\":{\"type\":\"array\",\"items\":" + orderItems + "},"
                + "\"parameters\":{\"type\":\"object\",\"description\":\"Extra parameters from describe-resource.\"}},"
                + "\"required\":[\"resource\"],\"additionalProperties\":false}",
            NhAiMvcBridgeCatalogBuilder.OutputSchemaJson);
        yield return (
            NhAiMvcBridgeGatewayKinds.Get,
            "Gets one item of a read-only API resource by id as the signed-in user.",
            "{\"type\":\"object\",\"properties\":{"
                + "\"resource\":{\"type\":\"string\"},"
                + "\"id\":{\"type\":[\"string\",\"integer\"]},"
                + "\"parameters\":{\"type\":\"object\",\"description\":\"Extra parameters from describe-resource.\"}},"
                + "\"required\":[\"resource\",\"id\"],\"additionalProperties\":false}",
            NhAiMvcBridgeCatalogBuilder.OutputSchemaJson);
    }

    private static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

/// <summary>
/// Executes the gateway tools. <c>search-resources</c> and <c>describe-resource</c> run through the
/// shared invoker as gateway tools; <c>query</c> and <c>get</c> run the underlying bridge descriptor
/// through the shared invoker, so gate, policy, budget, audit and the self-HTTP request are those of
/// the bridge tool itself.
/// </summary>
internal static class NhAiMvcBridgeGatewayFunctions
{
    public const string ResourceUnavailableMessage = "The resource does not exist or is not available to you.";

    public static AIFunction Create(
        NhAiToolDescriptor descriptor,
        NhAiMvcBridgeGatewayModel gateway,
        IServiceProvider services)
    {
        return gateway.ToolKinds[descriptor.Id] switch
        {
            NhAiMvcBridgeGatewayKinds.SearchResources => NhAiMvcBridgeFunctionFactory.CreateGoverned<JsonElement?>(
                descriptor,
                (input, cancellationToken) => InvokeCatalogToolAsync(descriptor, gateway, services, input, SearchAsync, cancellationToken),
                services),
            NhAiMvcBridgeGatewayKinds.DescribeResource => NhAiMvcBridgeFunctionFactory.CreateGoverned<JsonElement?>(
                descriptor,
                (input, cancellationToken) => InvokeCatalogToolAsync(descriptor, gateway, services, input, DescribeAsync, cancellationToken),
                services),
            NhAiMvcBridgeGatewayKinds.Query => NhAiMvcBridgeFunctionFactory.CreateGoverned(
                descriptor,
                (input, cancellationToken) => QueryAsync(descriptor, gateway, services, input, cancellationToken),
                services),
            _ => NhAiMvcBridgeFunctionFactory.CreateGoverned(
                descriptor,
                (input, cancellationToken) => GetAsync(descriptor, gateway, services, input, cancellationToken),
                services)
        };
    }

    private static async Task<TaskResult<JsonElement?>> InvokeCatalogToolAsync(
        NhAiToolDescriptor descriptor,
        NhAiMvcBridgeGatewayModel gateway,
        IServiceProvider services,
        JsonElement input,
        Func<NhAiMvcBridgeGatewayModel, IServiceProvider, JsonElement, CancellationToken, Task<TaskResult<JsonElement?>>> handler,
        CancellationToken cancellationToken)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        return await invoker.InvokeAsync(
            descriptor,
            input,
            (_, invocationCancellationToken) => handler(gateway, services, input, invocationCancellationToken),
            cancellationToken);
    }

    private static async Task<TaskResult<JsonElement?>> SearchAsync(
        NhAiMvcBridgeGatewayModel gateway,
        IServiceProvider services,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        if (!TryGetString(input, "query", out var query))
        {
            return Invalid<JsonElement?>("The input value 'query' is required.");
        }
        var limit = 10;
        if (TryGetProperty(input, "limit", out var limitValue) && limitValue.ValueKind != JsonValueKind.Null)
        {
            if (!limitValue.TryGetInt32(out limit) || limit < 1 || limit > 20)
            {
                return Invalid<JsonElement?>("The input value 'limit' must be between 1 and 20.");
            }
        }

        var terms = query
            .ToLowerInvariant()
            .Split(Separators(), StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var matches = new List<(int Score, NhAiBridgeResourceSummary Summary)>();
        foreach (var resource in await AvailableResourcesAsync(gateway, services, cancellationToken))
        {
            var description = Describe(resource, services);
            var text = string.Join(
                " ",
                new[] { resource.Resource, description.Title, description.Description }
                    .Concat(description.Query?.FilterFields.Select(field => field.Key) ?? [])
                    .Concat(description.Query?.OrderFields ?? [])
                    .Concat(description.ResultFields.Select(field => field.Key)))
                .ToLowerInvariant();
            var score = terms.Length == 0 ? 1 : terms.Count(term => text.Contains(term, StringComparison.Ordinal));
            if (score > 0)
            {
                matches.Add((score, new NhAiBridgeResourceSummary(
                    resource.Resource,
                    description.Title,
                    description.Description,
                    resource.Operations.Select(operation => operation.Kind).ToArray())));
            }
        }

        var result = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Summary.Resource, StringComparer.Ordinal)
            .Take(limit)
            .Select(match => match.Summary)
            .ToArray();
        return TaskResult<JsonElement?>.Succeeded(JsonSerializer.SerializeToElement(result, AIJsonUtilities.DefaultOptions));
    }

    private static async Task<TaskResult<JsonElement?>> DescribeAsync(
        NhAiMvcBridgeGatewayModel gateway,
        IServiceProvider services,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var resource = await ResolveAsync(gateway, services, input, null, cancellationToken);
        if (resource is null)
        {
            return Unavailable<JsonElement?>();
        }
        return TaskResult<JsonElement?>.Succeeded(
            JsonSerializer.SerializeToElement(Describe(resource, services), AIJsonUtilities.DefaultOptions));
    }

    private static async Task<TaskResult<NhAiBridgeResponse>> QueryAsync(
        NhAiToolDescriptor gatewayDescriptor,
        NhAiMvcBridgeGatewayModel gateway,
        IServiceProvider services,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var resource = await ResolveAsync(gateway, services, input, NhAiMvcBridgeGatewayKinds.Query, cancellationToken);
        var operation = resource?.Find(NhAiMvcBridgeGatewayKinds.Query);
        if (operation is null)
        {
            return await UnavailableAsync(gatewayDescriptor, services, input, cancellationToken);
        }

        var underlying = new JsonObject();
        foreach (var name in new[] { "page", "itemsPerPage", "search", "filter", "orderBy" })
        {
            if (TryGetProperty(input, name, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                underlying[name] = JsonNode.Parse(value.GetRawText());
            }
        }
        var merge = MergeParameters(input, underlying);
        var underlyingInput = JsonSerializer.SerializeToElement(underlying);
        return await NhAiMvcBridgeFunctionFactory.InvokeAsync(
            operation.Descriptor,
            operation.Action,
            services,
            underlyingInput,
            cancellationToken,
            () => merge ?? ValidateQuery(operation.Query, input));
    }

    private static async Task<TaskResult<NhAiBridgeResponse>> GetAsync(
        NhAiToolDescriptor gatewayDescriptor,
        NhAiMvcBridgeGatewayModel gateway,
        IServiceProvider services,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var resource = await ResolveAsync(gateway, services, input, NhAiMvcBridgeGatewayKinds.Get, cancellationToken);
        var operation = resource?.Find(NhAiMvcBridgeGatewayKinds.Get);
        if (operation is null)
        {
            return await UnavailableAsync(gatewayDescriptor, services, input, cancellationToken);
        }

        var underlying = new JsonObject();
        if (TryGetProperty(input, "id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            underlying[operation.IdInputName!] = JsonNode.Parse(id.GetRawText());
        }
        var merge = MergeParameters(input, underlying);
        var underlyingInput = JsonSerializer.SerializeToElement(underlying);
        return await NhAiMvcBridgeFunctionFactory.InvokeAsync(
            operation.Descriptor,
            operation.Action,
            services,
            underlyingInput,
            cancellationToken,
            () => merge ?? (underlying.ContainsKey(operation.IdInputName!)
                ? null
                : Invalid<NhAiBridgeResponse>("The input value 'id' must be a string or number.")));
    }

    /// <summary>
    /// Resolves the requested resource when it exists, offers the operation and the current user
    /// may use it. Unknown and unauthorized resources are indistinguishable to the caller.
    /// </summary>
    private static async Task<NhAiBridgeGatewayResource?> ResolveAsync(
        NhAiMvcBridgeGatewayModel gateway,
        IServiceProvider services,
        JsonElement input,
        string? kind,
        CancellationToken cancellationToken)
    {
        if (!TryGetString(input, "resource", out var name)
            || !gateway.Resources.TryGetValue(name, out var resource))
        {
            return null;
        }

        var user = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
        var authorization = services.GetRequiredService<IAuthorizationService>();
        if (kind is null)
        {
            return await NhAiMvcBridgeUserAuthorization.CanUseAsync(user, resource, authorization, cancellationToken)
                ? resource
                : null;
        }

        var operation = resource.Find(kind);
        return operation is not null
            && await NhAiMvcBridgeUserAuthorization.CanUseAsync(user, operation.Descriptor, authorization, cancellationToken)
                ? resource
                : null;
    }

    private static async Task<IReadOnlyList<NhAiBridgeGatewayResource>> AvailableResourcesAsync(
        NhAiMvcBridgeGatewayModel gateway,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var user = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var available = new List<NhAiBridgeGatewayResource>();
        foreach (var resource in gateway.Resources.Values.OrderBy(item => item.Resource, StringComparer.Ordinal))
        {
            // Only operations the user may call are listed.
            var allowed = new List<NhAiBridgeGatewayOperation>();
            foreach (var operation in resource.Operations)
            {
                if (await NhAiMvcBridgeUserAuthorization.CanUseAsync(user, operation.Descriptor, authorization, cancellationToken))
                {
                    allowed.Add(operation);
                }
            }
            if (allowed.Count > 0)
            {
                available.Add(resource with { Operations = allowed });
            }
        }
        return available;
    }

    private static NhAiBridgeResourceDescription Describe(NhAiBridgeGatewayResource resource, IServiceProvider services)
    {
        var query = resource.Find(NhAiMvcBridgeGatewayKinds.Query);
        var get = resource.Find(NhAiMvcBridgeGatewayKinds.Get);
        var description = new NhAiBridgeResourceDescription
        {
            Resource = resource.Resource,
            Title = resource.Title,
            Description = resource.Summary,
            Query = query is null
                ? null
                : new NhAiBridgeResourceQuery
                {
                    FilterFields = query.Query.FilterFields,
                    Searchable = query.Query.Searchable,
                    OrderFields = query.Query.OrderFields,
                    ExtraParameters = NhAiMvcBridgeGatewayBuilderLogic.ExtraParameters(query)
                },
            Get = get is null
                ? null
                : new NhAiBridgeResourceGet(get.IdInputName!)
                {
                    ExtraParameters = NhAiMvcBridgeGatewayBuilderLogic.ExtraParameters(get)
                },
            ResultFields = (query ?? get)!.Query.ResultFields
        };

        var describerType = services.GetRequiredService<NhAiMvcBridgeOptions>().Gateway?.ResourceDescriberType;
        if (describerType is not null
            && services.GetRequiredService(describerType) is INhAiBridgeResourceDescriber describer)
        {
            description = describer.Describe(description) with { Resource = resource.Resource };
        }
        return description;
    }

    /// <summary>Copies <c>parameters</c> to the top level of the bridge input, never over gateway fields.</summary>
    private static TaskResult<NhAiBridgeResponse>? MergeParameters(JsonElement input, JsonObject underlying)
    {
        if (!TryGetProperty(input, "parameters", out var parameters) || parameters.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return Invalid<NhAiBridgeResponse>("The input value 'parameters' must be an object.");
        }

        foreach (var parameter in parameters.EnumerateObject())
        {
            if (underlying.ContainsKey(parameter.Name) || NhAiMvcBridgeGatewayBuilderLogic.IsCollectionInputName(parameter.Name))
            {
                return Invalid<NhAiBridgeResponse>(
                    $"The parameter '{Bounded(parameter.Name)}' must be passed as a gateway input value, not in 'parameters'.");
            }
            underlying[parameter.Name] = JsonNode.Parse(parameter.Value.GetRawText());
        }
        return null;
    }

    /// <summary>Rejects unknown filter and order keys when the conventions describe fields.</summary>
    private static TaskResult<NhAiBridgeResponse>? ValidateQuery(NhAiBridgeQueryDescription description, JsonElement input)
    {
        if (TryGetProperty(input, "itemsPerPage", out var itemsPerPage)
            && itemsPerPage.ValueKind != JsonValueKind.Null
            && (!itemsPerPage.TryGetInt32(out var size) || size < 1 || size > 100))
        {
            return Invalid<NhAiBridgeResponse>("The input value 'itemsPerPage' must be between 1 and 100.");
        }

        if (description.FilterFields.Count > 0 && TryGetProperty(input, "filter", out var filter) && filter.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in filter.EnumerateArray())
            {
                var key = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String
                    ? keyValue.GetString()!
                    : string.Empty;
                var field = description.FilterFields.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
                if (field is null)
                {
                    return Invalid<NhAiBridgeResponse>($"The filter key '{Bounded(key)}' is not a filter field of this resource.");
                }
                var @operator = item.TryGetProperty("operator", out var operatorValue) && operatorValue.ValueKind == JsonValueKind.String
                    ? operatorValue.GetString()!
                    : string.Empty;
                if (field.Operators.Count > 0 && !field.Operators.Contains(@operator, StringComparer.OrdinalIgnoreCase))
                {
                    return Invalid<NhAiBridgeResponse>($"The operator '{Bounded(@operator)}' is not supported for filter '{field.Key}'.");
                }
            }
        }

        if (description.OrderFields.Count > 0 && TryGetProperty(input, "orderBy", out var orderBy) && orderBy.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in orderBy.EnumerateArray())
            {
                var key = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String
                    ? keyValue.GetString()!
                    : string.Empty;
                if (!description.OrderFields.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    return Invalid<NhAiBridgeResponse>($"The order key '{Bounded(key)}' is not an order field of this resource.");
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Reports an unknown or unavailable resource through the shared invoker under the gateway
    /// tool, so it is audited and carries its stable code, without touching any bridge tool.
    /// </summary>
    private static async Task<TaskResult<NhAiBridgeResponse>> UnavailableAsync(
        NhAiToolDescriptor gatewayDescriptor,
        IServiceProvider services,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        return await services.GetRequiredService<INhAiToolInvoker>().InvokeAsync(
            gatewayDescriptor,
            input,
            (_, _) => Task.FromResult(Unavailable<NhAiBridgeResponse>()),
            cancellationToken);
    }

    private static TaskResult<T> Invalid<T>(string message)
    {
        return TaskResult<T>.Failed(NhAiBridgeFailureCodes.Validation, message);
    }

    private static TaskResult<T> Unavailable<T>()
    {
        return TaskResult<T>.Failed(NhAiBridgeFailureCodes.ResourceNotFound, ResourceUnavailableMessage);
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

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static char[] Separators()
    {
        return [' ', '\t', '\n', '\r', ',', '.', ';', ':', '-', '_', '/', '(', ')'];
    }

    private static string Bounded(string value)
    {
        return value.Length <= 64 ? value : value[..64];
    }
}
