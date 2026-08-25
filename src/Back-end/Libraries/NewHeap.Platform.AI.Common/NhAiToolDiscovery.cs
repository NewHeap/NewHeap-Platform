namespace NewHeap.Platform.AI;

public sealed record NhAiToolDiscoveryRequest(
    NhAiInvocationContext Context,
    NhAiToolExposure Exposure);

public interface INhAiToolDiscoveryPolicy
{
    ValueTask<bool> CanDiscoverAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);
}

public interface INhAiToolDiscoveryService
{
    ValueTask<IReadOnlyList<NhAiToolDescriptor>> DiscoverAsync(
        NhAiToolDiscoveryRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiDenyAllToolDiscoveryPolicy : INhAiToolDiscoveryPolicy
{
    public ValueTask<bool> CanDiscoverAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

internal sealed class NhAiToolDiscoveryService(
    IEnumerable<INhAiToolCatalog> catalogs,
    INhAiToolDiscoveryPolicy policy,
    INhAiCapabilityResolver capabilityResolver) : INhAiToolDiscoveryService
{
    public async ValueTask<IReadOnlyList<NhAiToolDescriptor>> DiscoverAsync(
        NhAiToolDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        ValidateExposure(request.Exposure);

        var descriptors = catalogs
            .SelectMany(catalog => catalog.Descriptors)
            .Where(descriptor => (descriptor.Exposure & request.Exposure) == request.Exposure)
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Version)
            .ToArray();
        var authorized = new List<NhAiToolDescriptor>(descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capabilities = await capabilityResolver.ResolveAsync(
                descriptor,
                request.Context,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (capabilities.Succeeded
                && await policy.CanDiscoverAsync(
                descriptor,
                request.Context,
                cancellationToken))
            {
                authorized.Add(descriptor);
            }
        }
        return authorized;
    }

    private static void ValidateExposure(NhAiToolExposure exposure)
    {
        var value = (int)exposure;
        if (value == 0 || (value & (value - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exposure),
                "AI discovery requires exactly one exposure boundary.");
        }
    }
}
