using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Mcp;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// Adds the script tool to the MCP export of each request, over exactly the governed tools that caller
/// may see, with the MCP limits. The tool carries the script tool's descriptor, so the exposure lists it
/// only when it allows scripts and the application's switch has them on, and refuses a call otherwise.
/// Its description lists the caller's own tools, so it is made per request.
/// </summary>
internal sealed class NhAiScriptMcpToolContributor(
    NhAiScriptingOptions options,
    NhAiScriptRunner runner) : INhAiMcpToolContributor
{
    public ValueTask<IReadOnlyList<McpServerTool>> CreateToolsAsync(
        NhAiMcpToolContribution contribution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        cancellationToken.ThrowIfCancellationRequested();

        // The step runtime offers an MCP client's script the tools it may discover through the exposure:
        // the tools the export lists for this caller, without compositions.
        var tools = contribution.Tools
            .Select(tool => tool.Descriptor)
            .Where(descriptor => descriptor.CompositionFeature == NhAiCompositionFeature.None)
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Version)
            .ToArray();
        var offers = tools
            .Select(descriptor => new NhAiScriptToolOffer(descriptor.ExportName, descriptor))
            .ToArray();
        if (runner.CallableTools(offers, options.Mcp).Count == 0)
        {
            // Without a tool to call, a script would only cost the caller a round trip.
            return ValueTask.FromResult<IReadOnlyList<McpServerTool>>([]);
        }

        var scope = new NhAiScriptScope(tools, options.Mcp);
        var function = NhAiGovernedAIFunction.Create(
            NhAiScriptTool.McpDescriptor,
            new NhAiScriptFunction(contribution.Services, NhAiScriptTool.McpDescriptor, () => scope),
            contribution.Services);
        return ValueTask.FromResult<IReadOnlyList<McpServerTool>>([new NhAiScriptMcpServerTool(function, options.Mcp)]);
    }
}

/// <summary>
/// The script tool as an MCP client sees it: the argument <c>script</c>, the script tool's result as
/// structured content, and on a stopped script <c>isError</c> with the stable code and message as
/// protocol metadata, as the export's governed tools publish a failure with a typed payload.
/// </summary>
internal sealed class NhAiScriptMcpServerTool : McpServerTool
{
    private readonly AIFunction _function;
    private readonly Tool _protocolTool;
    private readonly IReadOnlyList<object> _metadata = [NhAiScriptTool.McpDescriptor];

    public NhAiScriptMcpServerTool(AIFunction function, NhAiScriptLimits limits)
    {
        _function = function;
        var hints = NhAiToolAnnotationHints.Resolve(NhAiScriptTool.McpDescriptor);
        var mayChange = limits.MaxMutationsPerScript > 0;
        _protocolTool = new Tool
        {
            Name = NhAiScriptTool.FunctionName,
            Description = function.Description,
            InputSchema = NhAiScriptTool.CreateInputSchema(limits),
            OutputSchema = NhAiToolResult.CreateJsonSchema(NhAiScriptTool.McpDescriptor.OutputSchemaJson),
            Annotations = new ToolAnnotations
            {
                // A read-only script changes nothing; one that may run mutations says so.
                ReadOnlyHint = !mayChange,
                DestructiveHint = mayChange && hints.Destructive,
                IdempotentHint = !mayChange,
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
        var arguments = new AIFunctionArguments();
        foreach (var argument in request.Params?.Arguments ?? new Dictionary<string, JsonElement>())
        {
            arguments[argument.Key] = argument.Value;
        }
        if (request.Services is not null)
        {
            arguments.Services = request.Services;
        }

        var output = await _function.InvokeAsync(arguments, cancellationToken);
        var envelope = output is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(output, _function.JsonSerializerOptions);
        var succeeded = envelope.TryGetProperty(NhAiToolResult.SuccessPropertyName, out var success)
            && success.ValueKind == JsonValueKind.True;
        var result = new CallToolResult
        {
            IsError = !succeeded,
            Content = [new TextContentBlock { Text = envelope.GetRawText() }],
            StructuredContent = envelope
        };
        if (!succeeded)
        {
            var meta = new JsonObject
            {
                [NhAiMcpResultMetadata.CodeKey] = Text(envelope, NhAiToolResult.CodePropertyName) ?? NhAiToolFailureCodes.Failed,
                [NhAiMcpResultMetadata.TypedPayloadKey] = envelope.TryGetProperty(NhAiToolResult.DataPropertyName, out _)
            };
            if (Text(envelope, NhAiToolResult.MessagePropertyName) is { } message)
            {
                meta[NhAiMcpResultMetadata.MessageKey] = message;
            }
            result.Meta = meta;
        }

        return result;
    }

    private static string? Text(JsonElement envelope, string name)
    {
        return envelope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
