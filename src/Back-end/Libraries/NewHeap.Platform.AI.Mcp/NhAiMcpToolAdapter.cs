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

/// <summary>
/// Adds tools to the MCP export of one request after the governed tools the caller may see, such as
/// the script tool of <c>NewHeap.Platform.AI.Scripting</c>. A contributor receives exactly the governed
/// tools the export lists for that caller and works only through them, so a contributed tool can never
/// reach a tool the caller is not offered. Its tools run with the budget purpose of an MCP call. Register
/// it with <see cref="NhAiMcpServiceCollectionExtensions.AddNewHeapPlatformAIMcpToolContributor{TContributor}"/>;
/// a contributed tool that takes the name of another tool of the export fails the listing.
/// </summary>
public interface INhAiMcpToolContributor
{
    ValueTask<IReadOnlyList<McpServerTool>> CreateToolsAsync(
        NhAiMcpToolContribution contribution,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What an <see cref="INhAiMcpToolContributor"/> works with: the request's services, the authorized
/// invocation context of the caller and the governed tools the export lists for that caller, in the
/// export's order.
/// </summary>
public sealed record NhAiMcpToolContribution(
    IServiceProvider Services,
    NhAiInvocationContext Context,
    IReadOnlyList<NhAiMcpExportedTool> Tools);

/// <summary>
/// One governed tool of the MCP export: its descriptor and the governed function that runs it through
/// the shared invoker.
/// </summary>
public sealed record NhAiMcpExportedTool(
    NhAiToolDescriptor Descriptor,
    AIFunction Function);

public static class NhAiMcpServiceCollectionExtensions
{
    public static IServiceCollection AddNewHeapPlatformAIMcp(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNewHeapPlatformAI();
        services.TryAddScoped<INhAiMcpToolAdapter, NhAiMcpToolAdapter>();
        services.TryAddSingleton(provider => new NhAiMcpInvocationBinderRegistry(
            provider.GetServices<NhAiMcpInvocationBinderRegistration>()));
        services.TryAddSingleton<INhAiMcpClientToolImporter, NhAiMcpClientToolImporter>();
        return services;
    }

    /// <summary>
    /// Registers a contributor of tools to the MCP export (<see cref="INhAiMcpToolContributor"/>) as a
    /// scoped service; registering the same contributor twice adds it once.
    /// </summary>
    public static IServiceCollection AddNewHeapPlatformAIMcpToolContributor<TContributor>(
        this IServiceCollection services)
        where TContributor : class, INhAiMcpToolContributor
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNewHeapPlatformAIMcp();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAiMcpToolContributor, TContributor>());
        return services;
    }
}

internal sealed class NhAiMcpToolAdapter(
    IEnumerable<INhAiToolCatalog> catalogs,
    INhAiToolDiscoveryService discoveryService,
    IEnumerable<INhAiMcpToolContributor> contributors) : INhAiMcpToolAdapter
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
        var exported = new List<NhAiMcpExportedTool>(visible.Count);
        var exportNames = new HashSet<string>(StringComparer.Ordinal);

        // Static catalogs first: a tool that exists since startup keeps its export name.
        var ordered = catalogs
            .OrderBy(item => item is INhAiChangingToolCatalog ? 1 : 0)
            .ThenBy(item => item.Manifest.CatalogId, StringComparer.Ordinal);
        foreach (var listed in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A catalog that changes at runtime is exported from one view, which must pass its own
            // attestation; a view that does not stays out of the export instead of failing it.
            var changing = listed as INhAiChangingToolCatalog;
            var catalog = changing?.Current ?? listed;
            var descriptors = catalog.Descriptors;
            if (!descriptors.Any(descriptor => descriptor.Exposure.HasFlag(NhAiToolExposure.Mcp)))
            {
                // A catalog without MCP exposure, such as an imported catalog that only serves
                // agents, never enters the export path.
                continue;
            }

            // Generated catalogs are governed by construction; attested runtime catalogs are
            // validated with NhAiToolCatalogAttestation at startup, and every view of a changing
            // catalog once. Every function of both is re-checked against its descriptor below.
            if (catalog is not (INhAiGeneratedToolCatalog or INhAiAttestedToolCatalog)
                || catalog.Governance != NhAiToolCatalogGovernance.SharedInvoker)
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalog.Manifest.CatalogId}' is not a generated or attested catalog governed by INhAiToolInvoker.");
            }
            if (changing is not null && !NhAiMcpViewAttestations.Holds(catalog, services))
            {
                continue;
            }

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
                    if (changing is not null)
                    {
                        // A runtime tool never takes an export name another tool already has.
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"AI MCP export name '{descriptor.ExportName}' is registered more than once.");
                }

                var hints = NhAiToolAnnotationHints.Resolve(descriptor);
                exported.Add(new NhAiMcpExportedTool(descriptor, functions[index]));
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

        await AddContributedToolsAsync(services, context, exported, exportNames, result, cancellationToken);
        return result;
    }

    /// <summary>
    /// Adds the tools of every contributor after the governed tools, each given only the governed tools
    /// this caller sees.
    /// </summary>
    private async ValueTask AddContributedToolsAsync(
        IServiceProvider services,
        NhAiInvocationContext context,
        IReadOnlyList<NhAiMcpExportedTool> exported,
        HashSet<string> exportNames,
        List<McpServerTool> result,
        CancellationToken cancellationToken)
    {
        var contribution = new NhAiMcpToolContribution(services, context, exported);
        foreach (var contributor in contributors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contributed = await contributor.CreateToolsAsync(contribution, cancellationToken);
            foreach (var tool in contributed)
            {
                if (!exportNames.Add(tool.ProtocolTool.Name))
                {
                    throw new InvalidOperationException(
                        $"The MCP tool contributor '{contributor.GetType().Name}' contributed '{tool.ProtocolTool.Name}', a name another tool of the export has.");
                }

                result.Add(new NhAiContributedMcpServerTool(tool));
            }
        }
    }
}

/// <summary>
/// A tool an <see cref="INhAiMcpToolContributor"/> added to the export. The governed calls it makes
/// reserve their budget as MCP calls, as the export's own tools do.
/// </summary>
internal sealed class NhAiContributedMcpServerTool(McpServerTool inner) : McpServerTool
{
    public override Tool ProtocolTool => inner.ProtocolTool;

    public override IReadOnlyList<object> Metadata => inner.Metadata;

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        using var purpose = NhAiBudgetPurposeScope.Begin(NhAiBudgetPurposes.Mcp);
        return await inner.InvokeAsync(request, cancellationToken);
    }
}

/// <summary>
/// Remembers which views of catalogs that change at runtime passed
/// <see cref="NhAiToolCatalogAttestation.Validate"/>, so every view is attested once before it enters
/// the MCP export and a view that fails stays out of it.
/// </summary>
internal static class NhAiMcpViewAttestations
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<INhAiToolCatalog, object> Attested = new();
    private static readonly object Holding = new();

    public static bool Holds(INhAiToolCatalog view, IServiceProvider services)
    {
        if (Attested.TryGetValue(view, out _))
        {
            return true;
        }

        try
        {
            NhAiToolCatalogAttestation.Validate(view, services);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        Attested.AddOrUpdate(view, Holding);
        return true;
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

    /// <summary>
    /// The composite format of the failure message, such as
    /// <c>Project '{0}' has {1} open tasks.</c>, so a client can translate it and fill in
    /// <see cref="MessageArgumentsKey"/>. Published together with the arguments, next to
    /// <see cref="MessageKey"/>, when the message is one message that its format and arguments
    /// render exactly; a fixed message is its own format without arguments. A message that joins
    /// several messages carries neither key.
    /// </summary>
    public const string MessageFormatKey = "newheap.com/message-format";

    /// <summary>
    /// The arguments of <see cref="MessageFormatKey"/> as a JSON array of the values the message
    /// shows, and nothing else: strings, numbers, booleans and <c>null</c>, dates as ISO 8601
    /// strings, and the shown text for an argument with a format specifier or of another type.
    /// </summary>
    public const string MessageArgumentsKey = "newheap.com/message-arguments";

    /// <summary>
    /// The longest format, in characters, that <see cref="MessageFormatKey"/> carries.
    /// </summary>
    public const int MaxMessageFormatLength = 1_024;

    /// <summary>
    /// The most arguments <see cref="MessageArgumentsKey"/> carries.
    /// </summary>
    public const int MaxMessageArguments = 16;

    /// <summary>
    /// The longest argument, in characters as the message shows it, that
    /// <see cref="MessageArgumentsKey"/> carries.
    /// </summary>
    public const int MaxMessageArgumentLength = 256;

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
        string? evidenceReference,
        FormattableString? messageSource)
    {
        var safeMessage = message;
        var safeSource = messageSource;
        if (string.IsNullOrWhiteSpace(safeMessage))
        {
            // NewHeap's own text replaces a missing message, as a fixed message of its own.
            safeMessage = DefaultFailureMessage;
            safeSource = NhAiMcpMessageFormat.Fixed(DefaultFailureMessage);
        }

        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{code}: {safeMessage}" }],
            StructuredContent = JsonSerializer.SerializeToElement(
                new NhAiMcpToolError(code, safeMessage, evidenceReference),
                ErrorSerializerOptions)
        };
        ApplyFailure(result, code, safeMessage, evidenceReference, typedPayload: false, safeSource);
        return result;
    }

    /// <param name="messageSource">
    /// The one message <paramref name="message"/> was rendered from; its format and arguments are
    /// published only when they render <paramref name="message"/> exactly.
    /// </param>
    internal static void ApplyFailure(
        CallToolResult result,
        string code,
        string? message,
        string? evidenceReference,
        bool typedPayload,
        FormattableString? messageSource)
    {
        var meta = result.Meta ?? new JsonObject();
        meta[CodeKey] = code;
        meta[TypedPayloadKey] = typedPayload;
        if (!string.IsNullOrWhiteSpace(message))
        {
            meta[MessageKey] = message;
            ApplyMessageFormat(meta, message, messageSource);
        }
        if (!string.IsNullOrWhiteSpace(evidenceReference))
        {
            meta[EvidenceReferenceKey] = evidenceReference;
        }
        result.Meta = meta;
    }

    /// <summary>
    /// Adds the format of a fixed message, such as a refusal of the MCP export, to its metadata: the
    /// text itself, without arguments. Nothing a resolver or gate reported in its place is published.
    /// </summary>
    internal static void ApplyFixedMessageFormat(JsonObject meta, string message)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ApplyMessageFormat(meta, message, NhAiMcpMessageFormat.Fixed(message));
    }

    private static void ApplyMessageFormat(JsonObject meta, string message, FormattableString? messageSource)
    {
        if (!NhAiMcpMessageFormat.TryCreate(messageSource, message, out var format, out var arguments))
        {
            return;
        }

        meta[MessageFormatKey] = format;
        meta[MessageArgumentsKey] = arguments;
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
        // The budget manager sees the call as an MCP client's.
        using var purpose = NhAiBudgetPurposeScope.Begin(NhAiBudgetPurposes.Mcp);
        var output = await _function.InvokeAsync(arguments, cancellationToken);
        var envelope = output is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(output, _function.JsonSerializerOptions);
        if (envelope.ValueKind != JsonValueKind.Object
            || !NhAiMcpResultMetadata.TryGetEnvelopeProperty(envelope, "success", out var success)
            || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException(
                $"AI tool '{_descriptor.Id}' did not return the shared tool result.");
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
        var messageSource = capture.Recorded ? capture.MessageSource : null;
        var evidenceReference = capture.Recorded ? capture.EvidenceReference : null;
        if (!hasData)
        {
            return NhAiMcpResultMetadata.CreateDatalessFailure(code, message, evidenceReference, messageSource);
        }

        var failure = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = dataText! }],
            StructuredContent = data
        };
        NhAiMcpResultMetadata.ApplyFailure(
            failure,
            code,
            message,
            evidenceReference,
            typedPayload: true,
            messageSource);
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
        // The budget manager sees the call as an MCP client's.
        using var purpose = NhAiBudgetPurposeScope.Begin(NhAiBudgetPurposes.Mcp);
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
        var messageSource = capture.Recorded ? capture.MessageSource : null;
        var evidenceReference = capture.Recorded ? capture.EvidenceReference : null;
        var hasData = NhAiMcpResultMetadata.TryGetEnvelopeProperty(content, "data", out var data)
            && data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (!hasData)
        {
            return NhAiMcpResultMetadata.CreateDatalessFailure(code, message, evidenceReference, messageSource);
        }

        // A failed result with data keeps the shared tool result as structured content.
        result.IsError = true;
        NhAiMcpResultMetadata.ApplyFailure(
            result,
            code,
            message,
            evidenceReference,
            typedPayload: true,
            messageSource);
        return result;
    }
}
