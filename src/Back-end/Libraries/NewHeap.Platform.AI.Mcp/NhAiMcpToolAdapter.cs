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

        foreach (var catalog in catalogs.OrderBy(item => item.Manifest.CatalogId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (catalog.Governance != NhAiToolCatalogGovernance.SharedInvoker)
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalog.Manifest.CatalogId}' is not governed by INhAiToolInvoker.");
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
