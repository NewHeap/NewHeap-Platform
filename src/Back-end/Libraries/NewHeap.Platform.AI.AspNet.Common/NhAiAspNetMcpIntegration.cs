using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        builder.WithListToolsHandler(NhAiMcpRequestHandlers.ListToolsAsync);
        builder.WithCallToolHandler(NhAiMcpRequestHandlers.CallToolAsync);
        return builder;
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
        return await tool.InvokeAsync(request, cancellationToken);
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
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
            StructuredContent = JsonSerializer.SerializeToElement(
                new NhAiMcpError(code, message),
                SerializerOptions)
        };
    }
}

internal sealed record NhAiMcpError(string Code, string Message);

internal sealed class NhAiMcpAuthorityStartupValidator(
    IEnumerable<McpServerTool> registeredSdkTools,
    IEnumerable<INhAiToolCatalog> newHeapCatalogs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var newHeapExports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var catalog in newHeapCatalogs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (catalog is not INhAiGeneratedToolCatalog
                || catalog.Governance != NhAiToolCatalogGovernance.SharedInvoker)
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalog.Manifest.CatalogId}' is not a generated catalog governed by INhAiToolInvoker.");
            }

            foreach (var descriptor in catalog.Descriptors.Where(descriptor =>
                descriptor.Exposure.HasFlag(NhAiToolExposure.Mcp)))
            {
                if (!newHeapExports.TryAdd(descriptor.ExportName, descriptor.Id))
                {
                    throw new InvalidOperationException(
                        $"NewHeap AI MCP export name '{descriptor.ExportName}' is registered more than once.");
                }
            }
        }

        foreach (var tool in registeredSdkTools)
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

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static bool IsNewHeapGovernedTool(McpServerTool tool)
    {
        return tool.Metadata.Any(metadata => metadata is NhAiToolAttribute or NhAiToolDescriptor);
    }
}
