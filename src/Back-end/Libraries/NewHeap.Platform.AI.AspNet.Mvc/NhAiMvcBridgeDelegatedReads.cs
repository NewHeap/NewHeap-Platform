using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// The gateway's reads for delegated read credentials: its four tools, the <c>GET</c> actions of its
/// resources while a gateway tool executes them, and the endpoint of each such action.
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
            && catalog.IsGatewayRead(action.ActionDescriptor, descriptor.Id);
    }

    public bool IsReadEndpoint(Endpoint endpoint, string actionToolId)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionToolId);
        return endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
            && catalog.IsGatewayRead(action, actionToolId);
    }
}

/// <summary>
/// Identifies the target of a delegated read by the MVC action, which MVC selects in both routing
/// modes before its authorization filters run. Under the old <c>UseMvc</c> routing a controller
/// request has no endpoint, so the restriction alone refuses every delegated read there. As the first
/// authorization filter this filter authenticates the delegated read scheme with the executing action
/// as the target; MVC's own authorization then uses that cached result. It also refuses a delegated
/// read principal that was authenticated earlier against a routed endpoint when MVC executes another
/// action than the gateway read that its token names.
/// </summary>
/// <remarks>
/// The filter only acts on <c>GET</c> requests with the invocation header, the only requests that a
/// delegated read token can authenticate. Middleware before MVC still sees no target and refuses the
/// token, so the scheme stays refused when such middleware authenticated it first, for example when
/// it is the default authentication scheme under <c>UseMvc</c>.
/// </remarks>
internal sealed class NhAiMvcBridgeDelegatedReadFilter : IAsyncAuthorizationFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;
        if (!HttpMethods.IsGet(httpContext.Request.Method)
            || !httpContext.Request.Headers.ContainsKey(NhAiDelegatedReadDefaults.InvocationHeaderName)
            || context.ActionDescriptor is not ControllerActionDescriptor action
            || httpContext.RequestServices.GetService<NhAiDelegatedReadOptions>() is not { } options)
        {
            return;
        }

        // The action as an endpoint carries the same metadata as its endpoint under endpoint routing.
        var target = new Endpoint(
            null,
            new EndpointMetadataCollection([action, .. action.EndpointMetadata]),
            action.DisplayName);
        var authenticated = await NhAiDelegatedReadService.AuthenticateForTargetAsync(httpContext, target, options.Scheme);

        var services = httpContext.RequestServices;
        if ((authenticated.Succeeded && !NhAiDelegatedReadRestriction.Reaches(authenticated.Principal!, target, services))
            || !NhAiDelegatedReadRestriction.Reaches(httpContext.User, target, services))
        {
            context.Result = new UnauthorizedResult();
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
