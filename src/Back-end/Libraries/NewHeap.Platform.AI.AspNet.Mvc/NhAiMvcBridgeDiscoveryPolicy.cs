using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Discovery policy registered by the API bridge. A bridge descriptor is discoverable when the
/// current user satisfies every authorization policy of its action; every other descriptor is
/// decided by the inner policy configured with <c>UseInnerDiscoveryPolicy</c> (default: deny).
/// </summary>
public sealed class NhAiMvcBridgeDiscoveryPolicy : INhAiToolDiscoveryPolicy
{
    private readonly NhAiMvcBridgeToolCatalog _catalog;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuthorizationService _authorizationService;
    private readonly INhAiToolDiscoveryPolicy? _innerPolicy;

    public NhAiMvcBridgeDiscoveryPolicy(
        NhAiMvcBridgeToolCatalog catalog,
        NhAiMvcBridgeOptions options,
        IHttpContextAccessor httpContextAccessor,
        IAuthorizationService authorizationService,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(services);
        _catalog = catalog;
        _httpContextAccessor = httpContextAccessor;
        _authorizationService = authorizationService;
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

        if (!_catalog.TryGetAction(descriptor, out _))
        {
            return _innerPolicy is not null
                && await _innerPolicy.CanDiscoverAsync(descriptor, context, cancellationToken);
        }

        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        foreach (var policy in descriptor.AuthorizationPolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorization = await _authorizationService.AuthorizeAsync(user, policy);
            if (!authorization.Succeeded)
            {
                return false;
            }
        }
        return true;
    }
}
