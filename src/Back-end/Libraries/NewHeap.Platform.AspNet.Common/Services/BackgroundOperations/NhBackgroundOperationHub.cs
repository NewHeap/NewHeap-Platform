using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.Common.Actors;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

public interface INhBackgroundOperationHubClient
{
    Task OperationChanged(NhBackgroundOperationChangedMessage message);
}

[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme + ",Identity.Application")]
public sealed class NhBackgroundOperationHub : Hub<INhBackgroundOperationHubClient>
{
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

        await base.OnConnectedAsync();
    }

    internal static string GetUserGroup(Guid userId, Guid? divisionId)
    {
        var scope = divisionId.HasValue
            ? $"division:{divisionId.Value:N}"
            : "global";
        return $"nh-background-operation-user:{userId:N}:{scope}";
    }
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
    /// Maps <see cref="NhBackgroundOperationHub"/> at <paramref name="hubPath"/>. A connection closes when
    /// the authentication it was opened with expires, over WebSockets as well, instead of living on
    /// through keep-alives; the client reconnects with its current session.
    /// </summary>
    internal static HubEndpointConventionBuilder MapNhBackgroundOperationHub(
        this IEndpointRouteBuilder endpoints,
        string hubPath)
    {
        return endpoints.MapHub<NhBackgroundOperationHub>(
            hubPath,
            connection => connection.CloseOnAuthenticationExpiration = true);
    }
}
