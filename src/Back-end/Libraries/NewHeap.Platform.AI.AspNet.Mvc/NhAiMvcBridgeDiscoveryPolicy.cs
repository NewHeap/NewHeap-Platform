using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Discovery policy registered by the API bridge. A bridge descriptor is discoverable when the
/// current user satisfies every authorization policy of its action; a gateway tool is
/// discoverable when the user may use at least one of its resources; every other descriptor is
/// decided by the inner policy configured with <c>UseInnerDiscoveryPolicy</c> (default: deny).
/// </summary>
public sealed class NhAiMvcBridgeDiscoveryPolicy : INhAiToolDiscoveryPolicy
{
    private readonly NhAiMvcBridgeToolCatalog _catalog;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuthorizationService _authorizationService;
    private readonly INhAiAuthenticatedInvocationContextResolver _contextResolver;
    private readonly INhAiToolDiscoveryPolicy? _innerPolicy;

    public NhAiMvcBridgeDiscoveryPolicy(
        NhAiMvcBridgeToolCatalog catalog,
        NhAiMvcBridgeOptions options,
        IHttpContextAccessor httpContextAccessor,
        IAuthorizationService authorizationService,
        INhAiAuthenticatedInvocationContextResolver contextResolver,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(contextResolver);
        ArgumentNullException.ThrowIfNull(services);
        _catalog = catalog;
        _httpContextAccessor = httpContextAccessor;
        _authorizationService = authorizationService;
        _contextResolver = contextResolver;
        _innerPolicy = options.InnerDiscoveryPolicyType is null
            ? null
            : (INhAiToolDiscoveryPolicy)services.GetRequiredService(options.InnerDiscoveryPolicyType);
    }

    public async ValueTask<bool> CanDiscoverAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var httpContext = _httpContextAccessor.HttpContext;
        var user = httpContext?.User;
        if (httpContext is null)
        {
            return false;
        }
        var authenticated = await _contextResolver.ResolveAsync(httpContext, cancellationToken);
        var accountableActor = context.ActorKind == NhAiActorKind.Human
            ? context.ActorId
            : context.AccountableOwnerId;
        if (!authenticated.Success
            || !string.Equals(authenticated.Data.ActorId, accountableActor, StringComparison.Ordinal))
        {
            return false;
        }
        if (_catalog.TryGetAction(descriptor, out _))
        {
            return await NhAiMvcBridgeUserAuthorization.CanUseAsync(
                user,
                descriptor,
                _authorizationService,
                cancellationToken);
        }

        if (_catalog.TryGetGateway(descriptor, out var gateway))
        {
            foreach (var resource in gateway.Resources.Values)
            {
                if (await NhAiMvcBridgeUserAuthorization.CanUseAsync(
                    user,
                    resource,
                    _authorizationService,
                    cancellationToken))
                {
                    return true;
                }
            }
            return false;
        }

        return _innerPolicy is not null
            && await _innerPolicy.CanDiscoverAsync(descriptor, context, cancellationToken);
    }
}

/// <summary>
/// The per-request authorization rule shared by bridge discovery and the gateway catalog: an
/// authenticated user who satisfies every policy of the action.
/// </summary>
internal static class NhAiMvcBridgeUserAuthorization
{
    public static async ValueTask<bool> CanUseAsync(
        ClaimsPrincipal? user,
        NhAiToolDescriptor descriptor,
        IAuthorizationService authorizationService,
        CancellationToken cancellationToken)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        foreach (var policy in descriptor.AuthorizationPolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorization = await authorizationService.AuthorizeAsync(user, policy);
            if (!authorization.Succeeded)
            {
                return false;
            }
        }
        return true;
    }

    public static async ValueTask<bool> CanUseAsync(
        ClaimsPrincipal? user,
        NhAiBridgeGatewayResource resource,
        IAuthorizationService authorizationService,
        CancellationToken cancellationToken)
    {
        foreach (var operation in resource.Operations)
        {
            if (await CanUseAsync(user, operation.Descriptor, authorizationService, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }
}
