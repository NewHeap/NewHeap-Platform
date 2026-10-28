using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet;

public static class NhAiAspNetMcpBuilderExtensions
{
    public static IMcpServerBuilder WithNewHeapPlatformAITools(
        this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddNewHeapPlatformAIMcp();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            NhAiMcpAuthorityStartupValidator>());
        NhAiAspNetStartupChecks.Register(builder.Services);
        // Catalogs that change at runtime and may reach MCP clients announce every change of the tool
        // list to the connected sessions.
        builder.Services.TryAddSingleton<NhAiMcpToolListChanges>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<McpServerOptions>,
            NhAiMcpToolListChangeOptions>());
        builder.WithListToolsHandler(NhAiMcpRequestHandlers.ListToolsAsync);
        builder.WithCallToolHandler(NhAiMcpRequestHandlers.CallToolAsync);
        return builder;
    }
}

/// <summary>
/// Announces <c>notifications/tools/list_changed</c> to every MCP session when a catalog that changes
/// at runtime and may reach MCP clients changes. Each server's options get a tool collection whose
/// change event the MCP SDK turns into that notification for the session; it holds the tools the
/// application registered through the SDK, if any, and lists nothing else.
/// </summary>
internal sealed class NhAiMcpToolListChanges : IDisposable
{
    private readonly INhAiChangingToolCatalog[] _catalogs;
    private readonly object _sync = new();
    private readonly List<WeakReference<NhAiMcpToolListSignal>> _signals = [];

    public NhAiMcpToolListChanges(IEnumerable<INhAiChangingToolCatalog> catalogs)
    {
        _catalogs = catalogs.Where(catalog => catalog.MayExportToMcp).Distinct().ToArray();
        foreach (var catalog in _catalogs)
        {
            catalog.Changed += OnChanged;
        }
    }

    /// <summary>
    /// Whether a catalog may change the tools MCP clients see.
    /// </summary>
    public bool Announces => _catalogs.Length > 0;

    /// <summary>
    /// The tool collection of one server's options: <paramref name="existing"/>'s tools in a collection
    /// that signals every change of a catalog.
    /// </summary>
    public McpServerPrimitiveCollection<McpServerTool> Attach(McpServerPrimitiveCollection<McpServerTool>? existing)
    {
        if (existing is NhAiMcpToolListSignal attached)
        {
            return attached;
        }

        var signal = new NhAiMcpToolListSignal();
        foreach (var tool in existing ?? [])
        {
            signal.Add(tool);
        }

        lock (_sync)
        {
            _signals.RemoveAll(reference => !reference.TryGetTarget(out _));
            _signals.Add(new WeakReference<NhAiMcpToolListSignal>(signal));
        }

        return signal;
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs)
        {
            catalog.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        NhAiMcpToolListSignal[] live;
        lock (_sync)
        {
            live = _signals
                .Select(reference => reference.TryGetTarget(out var signal) ? signal : null)
                .OfType<NhAiMcpToolListSignal>()
                .ToArray();
        }

        foreach (var signal in live)
        {
            signal.Signal();
        }
    }
}

/// <summary>
/// The tool collection whose change event tells a session that its tool list changed.
/// </summary>
internal sealed class NhAiMcpToolListSignal : McpServerPrimitiveCollection<McpServerTool>
{
    public void Signal()
    {
        RaiseChanged();
    }
}

/// <summary>
/// Gives the options of every MCP server the signalling tool collection when a catalog may change the
/// tools MCP clients see; the SDK then declares <c>listChanged</c> and notifies the session.
/// </summary>
internal sealed class NhAiMcpToolListChangeOptions(NhAiMcpToolListChanges changes) : IPostConfigureOptions<McpServerOptions>
{
    public void PostConfigure(string? name, McpServerOptions options)
    {
        if (!changes.Announces)
        {
            return;
        }

        options.ToolCollection = changes.Attach(options.ToolCollection);
    }
}

internal static class NhAiMcpRequestHandlers
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken)
    {
        var services = RequiredServices(request.Services);
        var httpContext = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (httpContext is null)
        {
            return new ListToolsResult { Tools = [] };
        }
        var resolved = await services
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, cancellationToken);
        if (!resolved.Success)
        {
            return new ListToolsResult { Tools = [] };
        }
        var tools = await services
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(services, resolved.Data, cancellationToken);
        return new ListToolsResult
        {
            Tools = tools.Select(tool => tool.ProtocolTool).ToList()
        };
    }

    public static async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        var services = RequiredServices(request.Services);
        var httpContext = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (httpContext is null)
        {
            return Error("ai-tool-authentication-required", "AI tool authentication is required.");
        }
        var resolved = await services
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, cancellationToken);
        if (!resolved.Success)
        {
            return Error(ResultCode(resolved), "AI tool invocation context was rejected.");
        }
        var tools = await services
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(services, resolved.Data, cancellationToken);
        var tool = tools.SingleOrDefault(item => string.Equals(
            item.ProtocolTool.Name,
            request.Params?.Name,
            StringComparison.Ordinal));
        if (tool is null)
        {
            return Error("ai-tool-not-authorized", "The requested AI tool is not available.");
        }

        // The tool reads the call's request metadata as data the caller sent, never as identity.
        using (NhAiMcpInboundCall.Enter(request.Params?.Meta))
        {
            return await tool.InvokeAsync(request, cancellationToken);
        }
    }

    private static IServiceProvider RequiredServices(IServiceProvider? services)
    {
        return services ?? throw new InvalidOperationException(
            "The MCP request service provider is unavailable.");
    }

    private static string ResultCode(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? "ai-tool-authorization-denied";
    }

    private static CallToolResult Error(string code, string message)
    {
        var meta = new System.Text.Json.Nodes.JsonObject
        {
            [NhAiMcpResultMetadata.CodeKey] = code,
            [NhAiMcpResultMetadata.MessageKey] = message
        };
        // A refusal of the export publishes its fixed text as its format, without arguments; what
        // the resolver reported stays shielded.
        NhAiMcpResultMetadata.ApplyFixedMessageFormat(meta, message);

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{code}: {message}" }],
            StructuredContent = JsonSerializer.SerializeToElement(
                new NhAiMcpError(code, message),
                SerializerOptions),
            Meta = meta
        };
    }
}

internal sealed record NhAiMcpError(string Code, string Message);

/// <summary>
/// The request metadata (<c>_meta</c>) of the MCP tool call that the NewHeap export runs in this
/// asynchronous flow, after the caller was authenticated and the tool was found. It is what the caller
/// sent: never identity or authorization. A reader decides for itself whether it believes it, as the
/// assistant does for the consultation chain of a reviewed caller.
/// </summary>
internal static class NhAiMcpInboundCall
{
    private static readonly AsyncLocal<JsonObject?> CurrentMeta = new();

    /// <summary>
    /// The metadata of the call in progress, or <see langword="null"/> outside an exported MCP tool call
    /// or when the caller sent none.
    /// </summary>
    public static JsonObject? Meta => CurrentMeta.Value;

    public static IDisposable Enter(JsonObject? meta)
    {
        var previous = CurrentMeta.Value;
        CurrentMeta.Value = meta;
        return new Scope(previous);
    }

    private sealed class Scope(JsonObject? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentMeta.Value = previous;
        }
    }
}

internal sealed class NhAiMcpAuthorityStartupValidator(
    IServiceProvider hostServices,
    IServiceScopeFactory serviceScopeFactory) : NhAiStartupCheck(hostServices)
{
    protected override async Task ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Catalogs and SDK tools may be scoped services, so they are resolved in a validation scope.
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var newHeapExports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var catalog in services.GetServices<INhAiToolCatalog>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (catalog is INhAiChangingToolCatalog)
            {
                // A catalog that changes at runtime attests every view when it is exported, and its
                // tools never take an export name another tool has; its data never stops startup.
                if (catalog.Governance != NhAiToolCatalogGovernance.SharedInvoker)
                {
                    throw new InvalidOperationException(
                        $"AI catalog '{catalog.Manifest.CatalogId}' is not governed by INhAiToolInvoker.");
                }

                continue;
            }

            var exportedDescriptors = catalog.Descriptors
                .Where(descriptor => descriptor.Exposure.HasFlag(NhAiToolExposure.Mcp))
                .ToArray();
            if (exportedDescriptors.Length == 0)
            {
                // A catalog without MCP exposure, such as an imported catalog that only serves
                // agents, never enters the export path.
                continue;
            }

            if (catalog is not (INhAiGeneratedToolCatalog or INhAiAttestedToolCatalog)
                || catalog.Governance != NhAiToolCatalogGovernance.SharedInvoker)
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalog.Manifest.CatalogId}' is not a generated or attested catalog governed by INhAiToolInvoker.");
            }
            if (catalog is INhAiAttestedToolCatalog)
            {
                // A runtime catalog only enters the MCP export path after its attestation holds.
                NhAiToolCatalogAttestation.Validate(catalog, services);
            }

            foreach (var descriptor in exportedDescriptors)
            {
                if (!newHeapExports.TryAdd(descriptor.ExportName, descriptor.Id))
                {
                    throw new InvalidOperationException(
                        $"NewHeap AI MCP export name '{descriptor.ExportName}' is registered more than once.");
                }
            }
        }

        foreach (var tool in services.GetServices<McpServerTool>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsNewHeapGovernedTool(tool))
            {
                throw new InvalidOperationException(
                    $"NewHeap AI tool '{tool.ProtocolTool.Name}' is also registered through the MCP SDK. Publish each NewHeap tool only through WithNewHeapPlatformAITools.");
            }
            if (newHeapExports.ContainsKey(tool.ProtocolTool.Name))
            {
                throw new InvalidOperationException(
                    $"MCP tool name '{tool.ProtocolTool.Name}' conflicts with a NewHeap AI export name. Give the external tool a distinct name.");
            }
        }
    }

    private static bool IsNewHeapGovernedTool(McpServerTool tool)
    {
        return tool.Metadata.Any(metadata => metadata is NhAiToolAttribute or NhAiToolDescriptor);
    }
}
