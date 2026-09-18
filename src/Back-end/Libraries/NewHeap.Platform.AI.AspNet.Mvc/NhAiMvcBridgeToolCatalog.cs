using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.AI;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// The attested runtime catalog of bridged MVC actions. It is built once, on first use, from
/// ApiExplorer and attests that every function runs through <see cref="INhAiToolInvoker"/>.
/// </summary>
public sealed class NhAiMvcBridgeToolCatalog : INhAiAttestedToolCatalog
{
    private readonly Lazy<NhAiMvcBridgeCatalogModel> _model;

    internal NhAiMvcBridgeToolCatalog(
        IApiDescriptionGroupCollectionProvider apiDescriptions,
        NhAiMvcBridgeOptions options,
        NhAiMvcBridgeRuntimeSettings settings,
        INhAiBridgeConventions conventions)
    {
        ArgumentNullException.ThrowIfNull(apiDescriptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(conventions);
        var builder = new NhAiMvcBridgeCatalogBuilder(options, settings, conventions, new NhAiBridgeXmlDocumentation());
        _model = new Lazy<NhAiMvcBridgeCatalogModel>(
            () => builder.Build(apiDescriptions),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors => _model.Value.Descriptors;

    public NhAiToolCatalogManifest Manifest => _model.Value.Manifest;

    public string AttestationHash => _model.Value.AttestationHash;

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var model = _model.Value;
        return model.Descriptors
            .Select(descriptor => model.Gateway is { } gateway && gateway.ToolKinds.ContainsKey(descriptor.Id)
                ? NhAiMvcBridgeGatewayFunctions.Create(descriptor, gateway, services)
                : NhAiMvcBridgeFunctionFactory.Create(descriptor, model.Actions[descriptor.Id], services))
            .ToArray();
    }

    /// <summary>The gateway resource names, or empty when the gateway is not enabled.</summary>
    public IReadOnlyCollection<string> GatewayResources =>
        (IReadOnlyCollection<string>?)_model.Value.Gateway?.Resources.Keys ?? [];

    internal bool TryGetGateway(NhAiToolDescriptor descriptor, out NhAiMvcBridgeGatewayModel gateway)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var model = _model.Value;
        if (model.Gateway is { } candidate
            && candidate.ToolKinds.ContainsKey(descriptor.Id)
            && candidate.Descriptors.Any(item => string.Equals(item.Id, descriptor.Id, StringComparison.Ordinal)
                && string.Equals(item.ContractHash, descriptor.ContractHash, StringComparison.Ordinal)))
        {
            gateway = candidate;
            return true;
        }

        gateway = null!;
        return false;
    }

    /// <summary>
    /// Returns the action a bridge descriptor executes. Descriptors from other catalogs, or
    /// with a different version or contract hash, are not bridge descriptors.
    /// </summary>
    public bool TryGetAction(NhAiToolDescriptor descriptor, out NhAiBridgeActionInfo action)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var model = _model.Value;
        if (model.Actions.TryGetValue(descriptor.Id, out var candidate)
            && descriptor.Version == Manifest.Version
            && model.Descriptors.Any(item => string.Equals(item.Id, descriptor.Id, StringComparison.Ordinal)
                && string.Equals(item.ContractHash, descriptor.ContractHash, StringComparison.Ordinal)))
        {
            action = candidate;
            return true;
        }

        action = null!;
        return false;
    }
}
