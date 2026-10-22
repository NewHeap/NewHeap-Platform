using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// Recognizes the person who sent the current request by the platform's actor id: the id the
/// registered <see cref="INhAiAuthenticatedInvocationContextResolver"/> resolves for the request, which
/// is also the id the assistant records as a conversation's owner, a run's initiator and a
/// consultation's asker. An application with its own identity model may use actor ids that differ
/// from the <c>NameIdentifier</c> claim, such as a prefixed hash; comparing with the resolved id
/// recognizes its people as well. The id is resolved at most once per request and kept in
/// <see cref="HttpContext.Items"/>.
/// </summary>
internal static class NhAiRequestActor
{
    private static readonly object ActorKey = new();
    private static readonly object ResolvingKey = new();

    /// <summary>
    /// The value of a failed resolution, and of a resolution in progress whose id is not known yet.
    /// </summary>
    private static readonly object Unknown = new();

    /// <summary>
    /// Whether <paramref name="actorId"/> is the platform's actor id of the person who sent the
    /// request. While a resolver builds the request's own context, only the id it builds for matches.
    /// </summary>
    /// <param name="services">Resolves the resolver when the request has no request services.</param>
    public static async ValueTask<bool> IsAsync(
        HttpContext httpContext,
        string? actorId,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (string.IsNullOrWhiteSpace(actorId) || httpContext.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (httpContext.Items.TryGetValue(ResolvingKey, out var resolving))
        {
            // The request's own context is being built: only the actor it is built for is the
            // request's person. An unknown one, while another resolver resolves, matches nobody.
            return resolving is string resolvingActorId
                   && string.Equals(resolvingActorId, actorId, StringComparison.Ordinal);
        }

        var requestActorId = await GetActorIdAsync(httpContext, services, cancellationToken);
        return requestActorId is not null && string.Equals(requestActorId, actorId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The platform's actor id of the person who sent the request, or <see langword="null"/> when the
    /// request is anonymous or the resolver refuses it.
    /// </summary>
    /// <param name="services">Resolves the resolver when the request has no request services.</param>
    public static async ValueTask<string?> GetActorIdAsync(
        HttpContext httpContext,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }
        if (httpContext.Items.TryGetValue(ActorKey, out var known))
        {
            return known as string;
        }
        if (httpContext.Items.ContainsKey(ResolvingKey)
            || (httpContext.RequestServices ?? services).GetService<INhAiAuthenticatedInvocationContextResolver>() is not { } resolver)
        {
            // A resolver that asks for its own result while it resolves gets none.
            return null;
        }

        httpContext.Items[ResolvingKey] = Unknown;
        try
        {
            var resolved = await resolver.ResolveAsync(httpContext, cancellationToken);
            var actorId = resolved.Success ? resolved.Data.ActorId : null;
            httpContext.Items[ActorKey] = actorId ?? Unknown;
            return actorId;
        }
        finally
        {
            httpContext.Items.Remove(ResolvingKey);
        }
    }

    /// <summary>
    /// Marks that a resolver builds the request's own context for <paramref name="actorId"/>, until the
    /// returned scope is disposed.
    /// </summary>
    public static IDisposable BeginResolving(HttpContext httpContext, string actorId)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var hadPrevious = httpContext.Items.TryGetValue(ResolvingKey, out var previous);
        httpContext.Items[ResolvingKey] = actorId;
        return new ResolvingScope(httpContext, hadPrevious, previous);
    }

    /// <summary>
    /// Keeps the actor id the platform's resolver resolved for the request, so later checks in the same
    /// request do not resolve it again.
    /// </summary>
    public static void Remember(HttpContext httpContext, string actorId)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        httpContext.Items[ActorKey] = actorId;
    }

    private sealed class ResolvingScope(HttpContext httpContext, bool hadPrevious, object? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (hadPrevious)
            {
                httpContext.Items[ResolvingKey] = previous;
            }
            else
            {
                httpContext.Items.Remove(ResolvingKey);
            }
        }
    }
}
