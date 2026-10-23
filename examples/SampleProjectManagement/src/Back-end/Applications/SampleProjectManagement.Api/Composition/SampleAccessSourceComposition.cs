using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-315: a deployment whose people sign in at an external identity provider and whose rights come
/// from the application's own claims transformation at every request, so the NewHeap user store cannot
/// rebuild them for work outside a request. The application keeps the rights it saw at each person's
/// last request in <see cref="SampleAccessContexts"/>, and the AI ASP.NET integration builds every
/// principal outside a request from them (<c>UsePrincipalsFromAccessSource</c>): an agent run that
/// delegates from its initiator, its follow-up runs, consultations, the account an agent answers to, MCP
/// connections in the background and the people of the whole application. Rights older than the
/// maximum age lend nothing until the person is back. NewHeap stores none of it.
/// </summary>
/// <remarks>
/// Call <see cref="AddSampleAccessSource"/> after the claims projection of the identity provider and put
/// <see cref="UseSampleAccessContexts"/> after <c>UseAuthentication</c>, where the request's principal
/// carries the claims of the transformation. The sample's own API signs people in with the NewHeap user
/// store, whose built-in principal factory rebuilds them, and does not call this.
/// </remarks>
public static class SampleAccessSourceComposition
{
    /// <summary>
    /// Builds the principals outside a request from the rights the application recorded, valid for
    /// <paramref name="maxAge"/> after the person's last request.
    /// </summary>
    public static IServiceCollection AddSampleAccessSource(this IServiceCollection services, TimeSpan maxAge)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The contexts live in this process; a deployment with more than one server keeps them where it
        // keeps its sessions, such as a distributed cache.
        services.TryAddSingleton<SampleAccessContexts>();
        services.AddNewHeapPlatformAIAspNet(ai => ai.UsePrincipalsFromAccessSource<SampleAccessContexts>(options =>
        {
            options.MaxAge = maxAge;
        }));
        return services;
    }

    /// <summary>
    /// Records the rights of every signed-in request after authentication and the claims transformation.
    /// </summary>
    public static IApplicationBuilder UseSampleAccessContexts(this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return application.Use(async (context, next) =>
        {
            await context.RequestServices.GetRequiredService<SampleAccessContexts>().RememberAsync(context, context.RequestAborted);
            await next(context);
        });
    }
}

/// <summary>
/// The application's record of its people's rights: the claims of each person's last request and when,
/// on the application's clock, by the platform's actor id. The AI integration asks it for work outside a
/// request; a person it does not know, or who signed out, gets no principal.
/// </summary>
public sealed class SampleAccessContexts(TimeProvider? timeProvider = null) : INhAiAccessSource
{
    private readonly ConcurrentDictionary<string, NhAiAccessContext> _contexts = new(StringComparer.Ordinal);
    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Records the claims of the request's person under the platform's actor id, which the registered
    /// resolver resolves for the request (for an identity provider's people an <c>oidc:</c> id). A request
    /// without a person the resolver accepts records nothing.
    /// </summary>
    public async ValueTask RememberAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var resolver = httpContext.RequestServices.GetRequiredService<INhAiAuthenticatedInvocationContextResolver>();
        var resolved = await resolver.ResolveAsync(httpContext, cancellationToken);
        if (!resolved.Success)
        {
            return;
        }

        _contexts[resolved.Data.ActorId] = new NhAiAccessContext(httpContext.User.Claims, _applicationClock.GetUtcNow());
    }

    /// <summary>
    /// Forgets the rights of <paramref name="actorId"/>, for example when the person signs out, so work
    /// outside a request stops using them at once.
    /// </summary>
    public void Forget(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _contexts.TryRemove(actorId, out _);
    }

    public ValueTask<NhAiAccessContext?> GetAsync(
        string actorId,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken = default)
    {
        // The sample keeps one context per person; a host with several tenants or divisions per person
        // could return the one of the subject's tenant or division.
        return ValueTask.FromResult(_contexts.GetValueOrDefault(actorId));
    }
}
