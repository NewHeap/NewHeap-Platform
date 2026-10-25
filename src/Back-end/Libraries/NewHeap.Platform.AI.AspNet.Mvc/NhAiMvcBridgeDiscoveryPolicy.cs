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
/// In a person's turn that does not depend on the request (a turn that outlives it, or one the
/// application starts through <c>INhAssistantConversationTurns</c>) the person captured when it started
/// is the user; a bridge tool then still needs the person's own request, whose credential the bridge
/// forwards, so a turn without one is offered only the inner policy's tools.
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
        ClaimsPrincipal? user;
        string? authenticatedActor;
        if (NhAiPersonInvocationScopes.Current is { } person)
        {
            // A person's turn that does not depend on the request is decided for the person captured
            // when it started, without resolving the request again.
            user = person.User;
            authenticatedActor = person.Context.ActorId;
        }
        else
        {
            if (httpContext is null)
            {
                return false;
            }
            var authenticated = await _contextResolver.ResolveAsync(httpContext, cancellationToken);
            if (!authenticated.Success)
            {
                return false;
            }
            user = httpContext.User;
            authenticatedActor = authenticated.Data.ActorId;
        }

        var accountableActor = context.ActorKind == NhAiActorKind.Human
            ? context.ActorId
            : context.AccountableOwnerId;
        if (!string.Equals(authenticatedActor, accountableActor, StringComparison.Ordinal))
        {
            return false;
        }

        var bridged = _catalog.TryGetAction(descriptor, out _) || _catalog.TryGetGateway(descriptor, out _);
        if (bridged && httpContext is null)
        {
            // The bridge calls the API with the credential of the person's own request; a person's turn
            // that runs without one is offered no bridge tools.
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
