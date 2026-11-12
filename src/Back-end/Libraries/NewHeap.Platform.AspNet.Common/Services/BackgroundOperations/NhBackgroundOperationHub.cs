using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.Common.Actors;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

public interface INhBackgroundOperationHubClient
{
    Task OperationChanged(NhBackgroundOperationChangedMessage message);
}

/// <summary>
/// Live updates of background operations. A connection joins the group of its authenticated user and,
/// when it names an active division the user may use, the group of that user in that division. It
/// lasts as long as its session: it closes when the authentication it was opened with expires, and
/// with <see cref="NhBackgroundOperationsOptions.HubSessionCheckEnabled"/> (on by default) at the next
/// session check once the NewHeap session validator no longer accepts its principal, for example after
/// the user was deleted or their sessions were revoked.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme + ",Identity.Application")]
public sealed class NhBackgroundOperationHub : Hub<INhBackgroundOperationHubClient>
{
    private const string JoinedScopeKey = "nh-background-operation-joined-scope";

    private readonly INhDivisionAccess _divisions;

    public NhBackgroundOperationHub(INhDivisionAccess divisions)
    {
        _divisions = divisions;
    }

    public override async Task OnConnectedAsync()
    {
        var userIdValue = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            Context.Abort();
            throw new HubException("An authenticated user identifier is required.");
        }

        var httpContext = Context.GetHttpContext();
        // The principal the groups are joined for; an authentication refresh replaces it.
        var principal = httpContext?.User;
        var activeDivisionId = httpContext?.GetActiveDivisionId();
        if (activeDivisionId.HasValue
            && (httpContext is null
                || !await _divisions.HasAccessAsync(
                    NhActorRef.ForUser(userId),
                    activeDivisionId.Value,
                    Context.ConnectionAborted)))
        {
            Context.Abort();
            throw new HubException("The active division is not accessible to the authenticated user.");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GetUserGroup(userId, null),
            Context.ConnectionAborted);
        if (activeDivisionId.HasValue)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                GetUserGroup(userId, activeDivisionId),
                Context.ConnectionAborted);
        }

        // The connection lasts as long as its session: it closes when its authentication expires, and
        // when the session validator no longer accepts its principal.
        WatchSession(httpContext);
        // What the groups were joined for, so a refreshed authentication is checked against it.
        Context.Items[JoinedScopeKey] = new JoinedScope(userId, activeDivisionId);
#if NET11_0_OR_GREATER
        // A refresh that completed while the connection opened found nothing to compare with yet.
        if (httpContext is not null
            && !ReferenceEquals(httpContext.User, principal)
            && !await KeepsRefreshedPrincipalAsync(httpContext.User))
        {
            Context.Abort();
            return;
        }
#endif
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Validates the connection's session again every
    /// <see cref="NhBackgroundOperationsOptions.HubSessionCheckInterval"/> while
    /// <see cref="NhBackgroundOperationsOptions.HubSessionCheckEnabled"/> is on.
    /// </summary>
    private void WatchSession(HttpContext? httpContext)
    {
        var services = httpContext?.RequestServices;
        var options = services?.GetService<NhBackgroundOperationsOptions>();
        if (services is null || options is not { HubSessionCheckEnabled: true })
        {
            return;
        }

        services.GetRequiredService<NhHubSessions>().Watch(
            Context,
            nameof(NhBackgroundOperationHub),
            options.HubSessionCheckInterval);
    }

#if NET11_0_OR_GREATER
    /// <summary>
    /// Checks a connection whose authentication was refreshed
    /// (<see cref="NhBackgroundOperationsOptions.HubAuthenticationRefresh"/>) again, as when it opened:
    /// the refreshed principal must name the user the connection joined its groups for, and that user
    /// must still have access to the active division the connection joined. Then the connection keeps its
    /// groups, and the session check validates the refreshed principal from now on. Otherwise the
    /// connection closes, so no group outlives the authorization that joined it; the client reconnects
    /// with its current session and resynchronizes. A refresh that completes while the connection still
    /// opens is checked once the connection joined its groups.
    /// </summary>
    public override async Task OnAuthenticationRefreshedAsync()
    {
        if (!Context.Items.ContainsKey(JoinedScopeKey))
        {
            // Still opening: OnConnectedAsync checks the refreshed principal after joining.
            return;
        }

        if (Context.User is not { } user || !await KeepsRefreshedPrincipalAsync(user))
        {
            Context.Abort();
            return;
        }

        await base.OnAuthenticationRefreshedAsync();
    }

    /// <summary>
    /// Whether the connection keeps its groups for <paramref name="user"/>, its refreshed principal; the
    /// session check then validates that principal.
    /// </summary>
    private async Task<bool> KeepsRefreshedPrincipalAsync(ClaimsPrincipal user)
    {
        if (!await IsStillAuthorizedAsync(user))
        {
            return false;
        }

        Context.GetHttpContext()?.RequestServices.GetService<NhHubSessions>()?.Refreshed(Context, user);
        return true;
    }

    private async Task<bool> IsStillAuthorizedAsync(ClaimsPrincipal user)
    {
        if (!Context.Items.TryGetValue(JoinedScopeKey, out var value) || value is not JoinedScope joined)
        {
            return false;
        }

        var userIdValue = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId) || userId != joined.UserId)
        {
            return false;
        }

        if (!joined.DivisionId.HasValue)
        {
            return true;
        }

        try
        {
            return await _divisions.HasAccessAsync(
                NhActorRef.ForUser(userId),
                joined.DivisionId.Value,
                Context.ConnectionAborted);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A check that cannot be completed does not keep the division's group: the connection
            // closes and the client reconnects, when the hub checks the division again.
            return false;
        }
    }
#endif

    internal static string GetUserGroup(Guid userId, Guid? divisionId)
    {
        var scope = divisionId.HasValue
            ? $"division:{divisionId.Value:N}"
            : "global";
        return $"nh-background-operation-user:{userId:N}:{scope}";
    }

    /// <summary>The user and the active division a connection joined its groups for.</summary>
    private sealed record JoinedScope(Guid UserId, Guid? DivisionId);
}

internal sealed class NhSignalRBackgroundOperationLiveUpdatePublisher : INhBackgroundOperationLiveUpdatePublisher
{
    private readonly IHubContext<NhBackgroundOperationHub, INhBackgroundOperationHubClient> _hubContext;

    public NhSignalRBackgroundOperationLiveUpdatePublisher(
        IHubContext<NhBackgroundOperationHub, INhBackgroundOperationHubClient> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishChangedAsync(
        Guid ownerUserId,
        NhBackgroundOperationChangedMessage message,
        CancellationToken cancellationToken = default)
    {
        return _hubContext.Clients
            .Group(NhBackgroundOperationHub.GetUserGroup(ownerUserId, message.DivisionId))
            .OperationChanged(message)
            .WaitAsync(cancellationToken);
    }
}

internal sealed class NhBackgroundOperationSignalRMarker
{
}

internal static class NhBackgroundOperationHubEndpoints
{
    /// <summary>
    /// Maps <see cref="NhBackgroundOperationHub"/> at <see cref="NhBackgroundOperationsOptions.HubPath"/>.
    /// A connection closes when the authentication it was opened with expires, over WebSockets as well,
    /// instead of living on through keep-alives, and with
    /// <see cref="NhBackgroundOperationsOptions.HubSessionCheckEnabled"/> once its session ended
    /// (<see cref="NhHubSessions"/>); the client reconnects with its current session. With
    /// <see cref="NhBackgroundOperationsOptions.HubAuthenticationRefresh"/> on ASP.NET Core 11 the client
    /// may refresh that authentication without reconnecting (<see cref="NhHubAuthenticationRefresh"/>).
    /// </summary>
    internal static HubEndpointConventionBuilder MapNhBackgroundOperationHub(
        this IEndpointRouteBuilder endpoints,
        NhBackgroundOperationsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return endpoints.MapHub<NhBackgroundOperationHub>(
            options.HubPath,
            connection => NhHubAuthenticationRefresh.Configure(connection, options.HubAuthenticationRefresh));
    }
}
