using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

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
            return (await AuthenticateAsync(services, scope, cancellationToken)).Principal;
        }
        return services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
    }

    /// <summary>
    /// The failure code of a headless run that allows gateway reads but got no principal for the
    /// person it reads for: the cause of <see cref="NhAiDelegatedReadFailureCodes"/>, or
    /// <see cref="NhAiBridgeFailureCodes.Unauthenticated"/> when the person may not lend rights now.
    /// <see langword="null"/> when the read may go ahead.
    /// </summary>
    public static async ValueTask<string?> RefusalAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (NhAiAgentInvocationScopes.Current is not { AllowGatewayReads: true } scope)
        {
            return null;
        }

        var authenticated = await AuthenticateAsync(services, scope, cancellationToken);
        return authenticated.Principal is null
            ? NhAiMvcBridgeDelegatedReadFailures.CodeOf(authenticated.FailureCode)
            : null;
    }

    private static async ValueTask<NhAiDelegatedReadAuthentication> AuthenticateAsync(
        IServiceProvider services,
        NhAiAgentInvocationScope scope,
        CancellationToken cancellationToken)
    {
        var reads = services.GetService<NhAiDelegatedReadService>();
        if (reads is null)
        {
            // AddNewHeapPlatformAIDelegatedReads is missing, so nobody issues credentials.
            services.GetService<NhAiDelegatedReadDiagnostics>()?.Report(NhAiDelegatedReadFailureCodes.NoIssuer, scope.AgentId);
            return new NhAiDelegatedReadAuthentication(null, NhAiDelegatedReadFailureCodes.NoIssuer);
        }
        return await reads.AuthenticateAsync(scope, cancellationToken);
    }
}

/// <summary>
/// The failures of a gateway read in a headless run that got no usable delegated read credential:
/// its cause as the code (<see cref="NhAiDelegatedReadFailureCodes"/>), with a message that is safe
/// for the model and says what to fix in a few words.
/// </summary>
internal static class NhAiMvcBridgeDelegatedReadFailures
{
    /// <summary>
    /// The bridge failure code of a delegated read refusal: its cause, or
    /// <see cref="NhAiBridgeFailureCodes.Unauthenticated"/> when it names none.
    /// </summary>
    public static string CodeOf(string? refusal)
    {
        return refusal is not null && NhAiDelegatedReadFailureCodes.All.Contains(refusal, StringComparer.Ordinal)
            ? refusal
            : NhAiBridgeFailureCodes.Unauthenticated;
    }

    public static TaskResult<T> Failed<T>(string code)
    {
        return TaskResult<T>.Failed(code, Message(code));
    }

    public static string Message(string code)
    {
        return code switch
        {
            NhAiDelegatedReadFailureCodes.TargetUnknown =>
                "The API checked the delegated read credential before it knew the action it was for, so it refused it. The application routes before it authenticates.",
            NhAiDelegatedReadFailureCodes.NoIssuer =>
                "The application issues no delegated read credentials, so the agent cannot read through the gateway. The application registers an issuer.",
            NhAiDelegatedReadFailureCodes.IssuerRefused =>
                "No delegated read credential was issued for the person the agent reads for, for example because they may no longer sign in.",
            NhAiDelegatedReadFailureCodes.Rejected =>
                "The API did not accept the delegated read credential. The application's bearer scheme must validate it.",
            _ => "No read credential could be issued for the person the agent works for."
        };
    }
}
