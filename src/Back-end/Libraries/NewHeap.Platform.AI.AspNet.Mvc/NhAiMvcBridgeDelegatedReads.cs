using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// The gateway's reads for delegated read credentials: its four tools, the <c>GET</c> actions of its
/// resources while a gateway tool executes them, their endpoints, and the policies of the resource
/// operation behind a recorded gateway call.
/// </summary>
internal sealed class NhAiMvcBridgeReadSurface(NhAiMvcBridgeToolCatalog catalog) : INhAiDelegatedReadSurface
{
    public bool IsReadTool(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (catalog.Gateway is null)
        {
            return false;
        }
        if (catalog.TryGetGateway(descriptor, out _))
        {
            return true;
        }

        // A resource action is a delegated read only inside the gateway tool that executes it, so a
        // headless run is offered the gateway's tools and never the resource actions themselves.
        return NhAiMvcBridgeGatewayCall.IsActive
            && catalog.TryGetAction(descriptor, out var action)
            && catalog.IsGatewayRead(action.ActionDescriptor);
    }

    public bool IsReadEndpoint(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
            && catalog.IsGatewayRead(action);
    }

    public IReadOnlyList<string>? ResultPolicies(string toolId, string? argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        if (catalog.Gateway is not { } gateway)
        {
            return null;
        }
        if (!gateway.ToolKinds.TryGetValue(toolId, out var kind))
        {
            return gateway.Resources.Values
                .SelectMany(resource => resource.Operations)
                .FirstOrDefault(operation => string.Equals(operation.Descriptor.Id, toolId, StringComparison.Ordinal))
                ?.Descriptor.AuthorizationPolicies;
        }
        if (kind == NhAiMvcBridgeGatewayKinds.SearchResources)
        {
            // The search lists resources the person may use; a reader sees it only with every policy.
            return PoliciesOf(gateway.Resources.Values.SelectMany(resource => resource.Operations));
        }
        if (ResourceOf(argumentsJson) is not { } name || !gateway.Resources.TryGetValue(name, out var target))
        {
            return null;
        }

        return kind == NhAiMvcBridgeGatewayKinds.DescribeResource
            ? PoliciesOf(target.Operations)
            : target.Find(kind)?.Descriptor.AuthorizationPolicies;
    }

    private static IReadOnlyList<string> PoliciesOf(IEnumerable<NhAiBridgeGatewayOperation> operations)
    {
        return operations
            .SelectMany(operation => operation.Descriptor.AuthorizationPolicies)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The <c>resource</c> argument of a gateway call, inside or without the <c>input</c> envelope.</summary>
    private static string? ResourceOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("input", out var input)
                && input.ValueKind == JsonValueKind.Object)
            {
                root = input;
            }
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("resource", out var resource)
                && resource.ValueKind == JsonValueKind.String
                    ? resource.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Marks the execution of a gateway tool's resource operation in the current asynchronous flow.
/// </summary>
internal static class NhAiMvcBridgeGatewayCall
{
    private static readonly AsyncLocal<bool> Active = new();

    public static bool IsActive => Active.Value;

    public static IDisposable Begin()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose()
        {
            Active.Value = previous;
        }
    }
}

/// <summary>
/// The principal the bridge authorizes gateway reads for: in a headless run that allows gateway
/// reads, the accountable person as the host's scheme authenticates their delegated read
/// credential; otherwise the user of the current request.
/// </summary>
internal static class NhAiMvcBridgeUsers
{
    public static async ValueTask<ClaimsPrincipal?> CurrentAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (NhAiAgentInvocationScopes.Current is { AllowGatewayReads: true } scope)
        {
            var reads = services.GetService<NhAiDelegatedReadService>();
            return reads is null ? null : await reads.AuthenticateAsync(scope, cancellationToken);
        }
        return services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
    }

    /// <summary>
    /// Whether a headless run that allows gateway reads got no principal for its accountable person.
    /// </summary>
    public static async ValueTask<bool> IsUnauthenticatedAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        return NhAiAgentInvocationScopes.Current is { AllowGatewayReads: true }
            && await CurrentAsync(services, cancellationToken) is null;
    }
}
