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
    INhAiCapabilityResolver capabilityResolver,
    IEnumerable<INhAiAgentToolAuthorizer> agentAuthorizers) : INhAiToolDiscoveryService
{
    private readonly INhAiAgentToolAuthorizer? _agentAuthorizer = agentAuthorizers.LastOrDefault();

    public async ValueTask<IReadOnlyList<NhAiToolDescriptor>> DiscoverAsync(
        NhAiToolDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        ValidateExposure(request.Exposure);

        // A catalog that changes at runtime reads its source first, so a listing sees a change that
        // another host made.
        foreach (var changing in catalogs.OfType<INhAiChangingToolCatalog>())
        {
            await changing.RefreshAsync(cancellationToken);
        }

        var descriptors = catalogs
            .SelectMany(catalog => NhAiToolCatalogViews.Of(catalog).Descriptors)
            .Where(descriptor => (descriptor.Exposure & request.Exposure) == request.Exposure)
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Version)
            .ToArray();
        var agentScope = NhAiAgentInvocationScopes.Current;
        if (agentScope is not null
            && request.Exposure == NhAiToolExposure.Agent
            && _agentAuthorizer is not null)
        {
            // An agent turn sees the tools its grants and its initiator's rights allow. The
            // invocation gate builds the capabilities when the tool is called.
            return await DiscoverForAgentAsync(descriptors, agentScope, _agentAuthorizer, cancellationToken);
        }

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

    private static async ValueTask<IReadOnlyList<NhAiToolDescriptor>> DiscoverForAgentAsync(
        IReadOnlyList<NhAiToolDescriptor> descriptors,
        NhAiAgentInvocationScope scope,
        INhAiAgentToolAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        var authorized = new List<NhAiToolDescriptor>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await authorizer.AuthorizeAsync(descriptor, scope, cancellationToken);
            if (source != NhAiGrantSource.None)
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
