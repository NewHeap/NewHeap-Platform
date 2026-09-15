using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace NewHeap.Platform.AI.Mcp;

public interface INhAiMcpToolAdapter
{
    ValueTask<IReadOnlyList<McpServerTool>> CreateToolsAsync(
        IServiceProvider services,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);
}

public static class NhAiMcpServiceCollectionExtensions
{
    public static IServiceCollection AddNewHeapPlatformAIMcp(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNewHeapPlatformAI();
        services.TryAddScoped<INhAiMcpToolAdapter, NhAiMcpToolAdapter>();
        services.TryAddSingleton<INhAiMcpClientToolImporter, NhAiMcpClientToolImporter>();
        return services;
    }
}

internal sealed class NhAiMcpToolAdapter(
    IEnumerable<INhAiToolCatalog> catalogs,
    INhAiToolDiscoveryService discoveryService) : INhAiMcpToolAdapter
{
    public async ValueTask<IReadOnlyList<McpServerTool>> CreateToolsAsync(
        IServiceProvider services,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        var visible = await discoveryService.DiscoverAsync(
            new NhAiToolDiscoveryRequest(context, NhAiToolExposure.Mcp),
            cancellationToken);
        var visibleContracts = visible
            .Select(descriptor => (descriptor.Id, descriptor.Version, descriptor.ContractHash))
            .ToHashSet();
        var result = new List<McpServerTool>(visible.Count);
        var exportNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var catalog in catalogs.OrderBy(item => item.Manifest.CatalogId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (catalog is not INhAiGeneratedToolCatalog
                || catalog.Governance != NhAiToolCatalogGovernance.SharedInvoker)
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalog.Manifest.CatalogId}' is not a generated catalog governed by INhAiToolInvoker.");
            }
            var descriptors = catalog.Descriptors;
            var functions = catalog.CreateFunctions(services);
            if (descriptors.Count != functions.Count)
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalog.Manifest.CatalogId}' returned a descriptor/function count mismatch.");
            }

            for (var index = 0; index < descriptors.Count; index++)
            {
                var descriptor = descriptors[index];
                if (functions[index] is not INhAiGovernedAIFunction governed
                    || !string.Equals(governed.Descriptor.Id, descriptor.Id, StringComparison.Ordinal)
                    || governed.Descriptor.Version != descriptor.Version
                    || !string.Equals(
                        governed.Descriptor.ContractHash,
                        descriptor.ContractHash,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        functions[index].Name,
                        descriptor.ExportName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AI catalog '{catalog.Manifest.CatalogId}' returned an ungoverned function for '{descriptor.Id}'.");
                }
                if (!visibleContracts.Contains(
                    (descriptor.Id, descriptor.Version, descriptor.ContractHash)))
                {
                    continue;
                }
                if (!exportNames.Add(descriptor.ExportName))
                {
                    throw new InvalidOperationException(
                        $"AI MCP export name '{descriptor.ExportName}' is registered more than once.");
                }

                if (descriptor.ExportSchema == NhAiToolExportSchema.Flat)
                {
                    result.Add(new NhAiFlatSchemaMcpServerTool(functions[index], descriptor));
                    continue;
                }

                var inner = McpServerTool.Create(
                    functions[index],
                    new McpServerToolCreateOptions
                    {
                        Name = functions[index].Name,
                        Description = descriptor.Description,
                        UseStructuredContent = true,
                        ReadOnly = descriptor.Effect == NhAiToolEffect.ReadOnly,
                        Destructive = descriptor.Effect == NhAiToolEffect.Destructive,
                        Idempotent = descriptor.Effect is NhAiToolEffect.ReadOnly
                            or NhAiToolEffect.IdempotentMutation,
                        OpenWorld = descriptor.Effect == NhAiToolEffect.ExternalSideEffect
                    });
                result.Add(new NhAiOutcomeAwareMcpServerTool(inner, descriptor));
            }
        }

        return result;
    }
}

/// <summary>
/// Publishes a generated tool with <see cref="NhAiToolExportSchema.Flat"/>: the input
/// type's properties are the top-level MCP arguments and the output type is the
/// structured result. The governed <see cref="AIFunction"/> still runs through the
/// shared invoker; only the wire shape changes.
/// </summary>
internal sealed class NhAiFlatSchemaMcpServerTool : McpServerTool
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private readonly AIFunction _function;
    private readonly NhAiToolDescriptor _descriptor;
    private readonly Tool _protocolTool;
    private readonly IReadOnlyList<object> _metadata;

    public NhAiFlatSchemaMcpServerTool(
        AIFunction function,
        NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(descriptor);
        _function = function;
        _descriptor = descriptor;
        _metadata = [descriptor];

        var inputSchema = ParseSchema(descriptor.InputSchemaJson);
        if (!IsObjectSchema(inputSchema))
        {
            throw new InvalidOperationException(
                $"AI tool '{descriptor.Id}' declares a flat export schema but its input schema is not a JSON object.");
        }
        var outputSchema = ParseSchema(descriptor.OutputSchemaJson);
        _protocolTool = new Tool
        {
            Name = descriptor.ExportName,
            Description = descriptor.Description,
            InputSchema = inputSchema,
            OutputSchema = IsObjectSchema(outputSchema) ? outputSchema : null,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = descriptor.Effect == NhAiToolEffect.ReadOnly,
                DestructiveHint = descriptor.Effect == NhAiToolEffect.Destructive,
                IdempotentHint = descriptor.Effect is NhAiToolEffect.ReadOnly
                    or NhAiToolEffect.IdempotentMutation,
                OpenWorldHint = descriptor.Effect == NhAiToolEffect.ExternalSideEffect
            }
        };
    }

    public override Tool ProtocolTool => _protocolTool;

    public override IReadOnlyList<object> Metadata => _metadata;

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new AIFunctionArguments
        {
            ["input"] = BuildInputElement(request.Params?.Arguments)
        };
        if (request.Services is not null)
        {
            arguments.Services = request.Services;
        }

        var output = await _function.InvokeAsync(arguments, cancellationToken);
        var envelope = output is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(output, SerializerOptions);
        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("success", out var success)
            || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException(
                $"AI tool '{_descriptor.Id}' did not return a TaskResult envelope.");
        }

        var hasData = envelope.TryGetProperty("data", out var data)
            && data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (success.ValueKind == JsonValueKind.True)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = hasData ? data.GetRawText() : "null" }],
                StructuredContent = hasData ? data : null
            };
        }

        // A failed result with typed data publishes that domain payload as the structured
        // error payload; a failure without data becomes a plain MCP tool error.
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = hasData ? data.GetRawText() : FailureText(envelope) }],
            StructuredContent = hasData ? data : null
        };
    }

    private static JsonElement BuildInputElement(
        IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (arguments is not null)
            {
                foreach (var argument in arguments)
                {
                    writer.WritePropertyName(argument.Key);
                    argument.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string FailureText(JsonElement envelope)
    {
        var messages = new List<string>();
        if (envelope.TryGetProperty("allErrorMessages", out var errorMessages)
            && errorMessages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in errorMessages.EnumerateArray())
            {
                if (message.ValueKind == JsonValueKind.String)
                {
                    messages.Add(message.GetString()!);
                }
                else if (message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("format", out var format)
                    && format.ValueKind == JsonValueKind.String)
                {
                    messages.Add(format.GetString()!);
                }
            }
        }
        return messages.Count == 0
            ? "The AI tool returned a failed result."
            : string.Join(" ", messages);
    }

    private static JsonElement ParseSchema(string schemaJson)
    {
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(schemaJson) ? "{}" : schemaJson);
        return document.RootElement.Clone();
    }

    private static bool IsObjectSchema(JsonElement schema)
    {
        return schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "object", StringComparison.Ordinal);
    }
}

internal sealed class NhAiOutcomeAwareMcpServerTool(
    McpServerTool inner,
    NhAiToolDescriptor descriptor) : McpServerTool
{
    private readonly IReadOnlyList<object> _metadata = [descriptor];

    public override Tool ProtocolTool => inner.ProtocolTool;

    public override IReadOnlyList<object> Metadata => _metadata;

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.InvokeAsync(request, cancellationToken);
        if (result.StructuredContent is { ValueKind: System.Text.Json.JsonValueKind.Object } content
            && content.TryGetProperty("success", out var success)
            && success.ValueKind is System.Text.Json.JsonValueKind.False)
        {
            result.IsError = true;
        }
        return result;
    }
}
