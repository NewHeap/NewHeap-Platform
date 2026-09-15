using System.Text.Json;
using System.Text.Json.Nodes;
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

                var hints = NhAiToolAnnotationHints.Resolve(descriptor);
                if (descriptor.ExportSchema == NhAiToolExportSchema.Flat)
                {
                    result.Add(new NhAiFlatSchemaMcpServerTool(functions[index], descriptor, hints));
                    continue;
                }

                var inner = McpServerTool.Create(
                    functions[index],
                    new McpServerToolCreateOptions
                    {
                        Name = functions[index].Name,
                        Description = descriptor.Description,
                        UseStructuredContent = true,
                        ReadOnly = hints.ReadOnly,
                        Destructive = hints.Destructive,
                        Idempotent = hints.Idempotent,
                        OpenWorld = hints.OpenWorld
                    });
                result.Add(new NhAiOutcomeAwareMcpServerTool(inner, descriptor));
            }
        }

        return result;
    }
}

/// <summary>
/// Structured error payload of a failed governed MCP tool call without a typed payload.
/// </summary>
internal sealed record NhAiMcpToolError(
    string Code,
    string Message,
    string? EvidenceReference);

/// <summary>
/// Metadata keys NewHeap adds to the <c>_meta</c> object of a failed MCP tool result. The
/// domain payload stays untouched; the stable failure detail travels as protocol metadata.
/// </summary>
public static class NhAiMcpResultMetadata
{
    /// <summary>The stable failure code of the failed governed invocation.</summary>
    public const string CodeKey = "newheap.com/code";

    /// <summary>The safe failure message of the failed governed invocation.</summary>
    public const string MessageKey = "newheap.com/message";

    /// <summary>The bounded evidence reference attached to the failure, when one exists.</summary>
    public const string EvidenceReferenceKey = "newheap.com/evidence-reference";

    /// <summary>
    /// <c>true</c> when the structured content of the failed result is the tool's typed failure
    /// payload; <c>false</c> when it is the NewHeap <c>{ code, message }</c> error payload.
    /// </summary>
    public const string TypedPayloadKey = "newheap.com/typed-payload";

    private const string DefaultFailureMessage = "The AI tool invocation failed.";

    private static readonly JsonSerializerOptions ErrorSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Builds the result of a failed invocation that carries no typed payload: a structured
    /// <c>{ code, message, evidenceReference }</c> error, a <c>code: message</c> text block and
    /// the same detail as protocol metadata.
    /// </summary>
    internal static CallToolResult CreateDatalessFailure(
        string code,
        string? message,
        string? evidenceReference)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message) ? DefaultFailureMessage : message;
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{code}: {safeMessage}" }],
            StructuredContent = JsonSerializer.SerializeToElement(
                new NhAiMcpToolError(code, safeMessage, evidenceReference),
                ErrorSerializerOptions)
        };
        ApplyFailure(result, code, safeMessage, evidenceReference, typedPayload: false);
        return result;
    }

    internal static void ApplyFailure(
        CallToolResult result,
        string code,
        string? message,
        string? evidenceReference,
        bool typedPayload)
    {
        var meta = result.Meta ?? new JsonObject();
        meta[CodeKey] = code;
        meta[TypedPayloadKey] = typedPayload;
        if (!string.IsNullOrWhiteSpace(message))
        {
            meta[MessageKey] = message;
        }
        if (!string.IsNullOrWhiteSpace(evidenceReference))
        {
            meta[EvidenceReferenceKey] = evidenceReference;
        }
        result.Meta = meta;
    }

    internal static bool TryGetEnvelopeProperty(
        JsonElement envelope,
        string name,
        out JsonElement value)
    {
        // A declared serializer context may apply any naming policy to the envelope.
        foreach (var property in envelope.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

/// <summary>
/// Publishes a generated tool with <see cref="NhAiToolExportSchema.Flat"/>: the input
/// type's properties are the top-level MCP arguments and the output type is the
/// structured result. The governed <see cref="AIFunction"/> still runs through the
/// shared invoker; only the wire shape changes. Results and error payloads are written
/// with the function's JSON options: the tool set's declared context, or
/// <see cref="NhAiToolJsonSerializerOptions.FlatExport"/> when none is declared.
/// </summary>
internal sealed class NhAiFlatSchemaMcpServerTool : McpServerTool
{
    private readonly AIFunction _function;
    private readonly NhAiToolDescriptor _descriptor;
    private readonly Tool _protocolTool;
    private readonly IReadOnlyList<object> _metadata;

    public NhAiFlatSchemaMcpServerTool(
        AIFunction function,
        NhAiToolDescriptor descriptor,
        NhAiToolAnnotationHints hints)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(hints);
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
                ReadOnlyHint = hints.ReadOnly,
                DestructiveHint = hints.Destructive,
                IdempotentHint = hints.Idempotent,
                OpenWorldHint = hints.OpenWorld
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

        var capture = NhAiToolOutcomeCapture.Begin();
        var output = await _function.InvokeAsync(arguments, cancellationToken);
        var envelope = output is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(output, _function.JsonSerializerOptions);
        if (envelope.ValueKind != JsonValueKind.Object
            || !NhAiMcpResultMetadata.TryGetEnvelopeProperty(envelope, "success", out var success)
            || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException(
                $"AI tool '{_descriptor.Id}' did not return a TaskResult envelope.");
        }

        var hasData = NhAiMcpResultMetadata.TryGetEnvelopeProperty(envelope, "data", out var data)
            && data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        var dataText = hasData ? WriteJson(data) : null;
        if (success.ValueKind == JsonValueKind.True)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = dataText ?? "null" }],
                StructuredContent = hasData ? data : null
            };
        }

        // A failed result with typed data publishes that domain payload as the structured
        // error payload; a failure without data publishes a structured code and message.
        // The stable failure detail is added as protocol metadata in both cases.
        var code = capture.Recorded && !capture.Succeeded
            ? capture.Code ?? NhAiToolFailureCodes.Failed
            : NhAiToolFailureCodes.Failed;
        var message = capture.Recorded ? capture.Message : null;
        var evidenceReference = capture.Recorded ? capture.EvidenceReference : null;
        if (!hasData)
        {
            return NhAiMcpResultMetadata.CreateDatalessFailure(code, message, evidenceReference);
        }

        var failure = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = dataText! }],
            StructuredContent = data
        };
        NhAiMcpResultMetadata.ApplyFailure(failure, code, message, evidenceReference, typedPayload: true);
        return failure;
    }

    private string WriteJson(JsonElement value)
    {
        var options = _function.JsonSerializerOptions;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = options.Encoder,
            Indented = options.WriteIndented,
            IndentCharacter = options.IndentCharacter,
            IndentSize = options.IndentSize,
            NewLine = options.NewLine,
            MaxDepth = options.MaxDepth
        }))
        {
            value.WriteTo(writer);
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
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
        var capture = NhAiToolOutcomeCapture.Begin();
        var result = await inner.InvokeAsync(request, cancellationToken);
        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } content
            || !NhAiMcpResultMetadata.TryGetEnvelopeProperty(content, "success", out var success)
            || success.ValueKind is not JsonValueKind.False)
        {
            return result;
        }

        var code = capture.Recorded && !capture.Succeeded
            ? capture.Code ?? NhAiToolFailureCodes.Failed
            : NhAiToolFailureCodes.Failed;
        var message = capture.Recorded ? capture.Message : null;
        var evidenceReference = capture.Recorded ? capture.EvidenceReference : null;
        var hasData = NhAiMcpResultMetadata.TryGetEnvelopeProperty(content, "data", out var data)
            && data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (!hasData)
        {
            return NhAiMcpResultMetadata.CreateDatalessFailure(code, message, evidenceReference);
        }

        // A failed envelope with data keeps the TaskResult envelope as structured content.
        result.IsError = true;
        NhAiMcpResultMetadata.ApplyFailure(result, code, message, evidenceReference, typedPayload: true);
        return result;
    }
}
