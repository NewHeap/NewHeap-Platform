using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Policy;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// The authorization resource of a person's tool call in a turn that does not depend on the person's
/// request: the scope the request gate would name for the person's own request, with the person's
/// active division, which division policies read instead of the request header.
/// </summary>
internal sealed record NhAiPersonScopeAuthorizationResource(
    string ScopeType,
    string ScopeId,
    string Purpose,
    Guid? DivisionId) : NhAiAspNetScopeAuthorizationResource(ScopeType, ScopeId, Purpose), INhDivisionScopedResource;

/// <summary>
/// Builds the <see cref="NhAiPersonInvocation"/> of a person's turn: captured from the person's own
/// request when the turn may outlive it, or resolved for a person the application authenticated through
/// its own channel, without a request of that person.
/// </summary>
internal static class NhAiPersonInvocations
{
    /// <summary>
    /// The person of the current request with the context the request resolved, and the resource the
    /// request gate would authorize their tool calls against, so the turn keeps authorizing them after
    /// the request ended.
    /// </summary>
    public static NhAiPersonInvocation FromRequest(HttpContext httpContext, NhAiInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(context);
        var state = httpContext.RequestServices.GetRequiredService<NhAiAspNetRegistrationState>();

        return new NhAiPersonInvocation(
            httpContext.User,
            context,
            ResourceOf(state, context, httpContext.GetActiveDivisionId()));
    }

    /// <summary>
    /// Resolves the invocation context of <paramref name="user"/> with the application's resolver, over
    /// a request that carries only that principal, and adds <paramref name="activeDivisionId"/> as a
    /// request header would: only when the host maps the active division and the person passes its
    /// policy for that division. Fails with the resolver's failure.
    /// </summary>
    /// <param name="services">The services of the turn, which the resolver reads as its request
    /// services.</param>
    public static async Task<TaskResult<NhAiPersonInvocation>> ResolveAsync(
        IServiceProvider services,
        ClaimsPrincipal user,
        Guid? activeDivisionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(user);
        var request = new DefaultHttpContext
        {
            User = user,
            RequestServices = services
        };
        var resolved = await services
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(request, cancellationToken);
        if (!resolved.Success)
        {
            return TaskResult<NhAiPersonInvocation>.Failed(resolved);
        }

        var state = services.GetRequiredService<NhAiAspNetRegistrationState>();
        var (context, divisionId) = await WithActiveDivisionAsync(
            services,
            state,
            user,
            resolved.Data,
            activeDivisionId,
            cancellationToken);

        return TaskResult<NhAiPersonInvocation>.Succeeded(
            new NhAiPersonInvocation(user, context, ResourceOf(state, context, divisionId)));
    }

    /// <summary>
    /// The context with the active division, its scope value, its execution scope and the capabilities
    /// granted in it, exactly as the context contributor adds them for a request with that division; the
    /// context unchanged when the host maps no active division, none was given or the person may not use
    /// it. Returns the division that was added.
    /// </summary>
    private static async Task<(NhAiInvocationContext Context, Guid? DivisionId)> WithActiveDivisionAsync(
        IServiceProvider services,
        NhAiAspNetRegistrationState state,
        ClaimsPrincipal user,
        NhAiInvocationContext context,
        Guid? activeDivisionId,
        CancellationToken cancellationToken)
    {
        if (state.ActiveDivision is not { } activeDivision || activeDivisionId is not { } divisionId)
        {
            return (context, null);
        }

        var authorizationService = services.GetRequiredService<IAuthorizationService>();
        var division = divisionId.ToString();
        var resource = new NhAiPersonScopeAuthorizationResource(activeDivision.ScopeType, division, context.Purpose, divisionId);
        var scopeAuthorization = await authorizationService.AuthorizeAsync(user, resource, activeDivision.AuthorizationPolicy);
        if (!scopeAuthorization.Succeeded)
        {
            return (context, null);
        }

        var scope = new Dictionary<string, string>(context.Scope, StringComparer.Ordinal)
        {
            [activeDivision.ScopeKey] = division
        };
        var executionScopes = context.ExecutionScopes.ToList();
        executionScopes.Add(new NhAiExecutionScopeEntry(activeDivision.ScopeType, division));
        var capabilities = context.CapabilityGrants.ToHashSet(StringComparer.Ordinal);
        foreach (var capability in state.Capabilities.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capabilityAuthorization = await authorizationService.AuthorizeAsync(user, resource, capability.Value);
            if (capabilityAuthorization.Succeeded)
            {
                capabilities.Add(capability.Key);
            }
        }

        return (context with
        {
            Scope = scope,
            ExecutionScopes = executionScopes,
            CapabilityGrants = capabilities
        }, divisionId);
    }

    /// <summary>
    /// The resource of the request gate: the tenant when the person's issuer projects one, otherwise the
    /// active division, otherwise none. It names the active division for division policies either way.
    /// </summary>
    private static NhAiPersonScopeAuthorizationResource? ResourceOf(
        NhAiAspNetRegistrationState state,
        NhAiInvocationContext context,
        Guid? divisionId)
    {
        if (state.TryGetIdentityProjection(context.Issuer, out var projection)
            && !string.IsNullOrWhiteSpace(context.TenantId))
        {
            return new NhAiPersonScopeAuthorizationResource(
                projection.TenantScopeType,
                context.TenantId,
                state.ToolInvocationPurpose,
                divisionId);
        }

        if (state.ActiveDivision is { } activeDivision && divisionId is { } id)
        {
            return new NhAiPersonScopeAuthorizationResource(
                activeDivision.ScopeType,
                id.ToString(),
                state.ToolInvocationPurpose,
                id);
        }

        return null;
    }
}
