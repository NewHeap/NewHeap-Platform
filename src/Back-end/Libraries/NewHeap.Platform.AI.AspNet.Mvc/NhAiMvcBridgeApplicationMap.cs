namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// The application map of the bridge gateway: the gateway resources the acting person may use,
/// with their localized titles and summaries, operations and relations, read by the gateway's
/// <c>query</c> and <c>get</c> tools. Registered by <c>EnableGateway</c>.
/// </summary>
internal sealed class NhAiMvcBridgeApplicationMapSource(
    NhAiMvcBridgeToolCatalog catalog,
    IServiceProvider services) : INhAiApplicationMapSource
{
    public async ValueTask<IReadOnlyList<NhAiApplicationMapEntry>> GetEntriesAsync(
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return catalog.Gateway is { } gateway
            ? await NhAiMvcBridgeGatewayFunctions.MapAsync(gateway, services, cancellationToken)
            : [];
    }
}
