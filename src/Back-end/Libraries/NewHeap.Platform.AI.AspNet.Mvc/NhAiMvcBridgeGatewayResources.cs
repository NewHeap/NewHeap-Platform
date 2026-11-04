using System.Text.Json;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// The gateway's resources for NewHeap's own code that helps a person build agents: the gateway's own
/// <c>search-resources</c> and <c>describe-resource</c>, with the rights of the person of the current
/// request. The caller's governed call is the one that is authorized and audited, so these never run
/// through the invoker a second time. Registered by <c>EnableGateway</c>.
/// </summary>
internal sealed class NhAiMvcBridgeGatewayResources(
    NhAiMvcBridgeToolCatalog catalog,
    IServiceProvider services) : INhAiGatewayResources
{
    private const string DisabledMessage = "The application's API bridge gateway publishes no resources.";

    public IReadOnlyCollection<string> ToolIds =>
        (IReadOnlyCollection<string>?)catalog.Gateway?.ToolKinds.Keys.ToArray() ?? [];

    public async Task<TaskResult<JsonElement>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var input = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["query"] = query, ["limit"] = limit });
        return await RunAsync(input, NhAiMvcBridgeGatewayFunctions.SearchAsync, cancellationToken);
    }

    public async Task<TaskResult<JsonElement>> DescribeAsync(string resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var input = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["resource"] = resource });
        return await RunAsync(input, NhAiMvcBridgeGatewayFunctions.DescribeAsync, cancellationToken);
    }

    private async Task<TaskResult<JsonElement>> RunAsync(
        JsonElement input,
        Func<NhAiMvcBridgeGatewayModel, IServiceProvider, JsonElement, CancellationToken, Task<TaskResult<JsonElement?>>> handler,
        CancellationToken cancellationToken)
    {
        if (catalog.Gateway is not { } gateway)
        {
            return TaskResult<JsonElement>.Failed(NhAiBridgeFailureCodes.ResourceNotFound, DisabledMessage);
        }

        var result = await handler(gateway, services, input, cancellationToken);
        if (!result.Success)
        {
            return TaskResult<JsonElement>.Failed(result);
        }

        return result.Data is { } data
            ? TaskResult<JsonElement>.Succeeded(data)
            : TaskResult<JsonElement>.Failed(NhAiBridgeFailureCodes.ResourceNotFound, NhAiMvcBridgeGatewayFunctions.ResourceUnavailableMessage);
    }
}
