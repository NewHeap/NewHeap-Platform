using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NewHeap.Platform.AspNet.Common;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

public interface INhBackgroundOperationHubClient
{
    Task OperationChanged(NhBackgroundOperationChangedMessage message);
}

[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme + ",Identity.Application")]
public sealed class NhBackgroundOperationHub : Hub<INhBackgroundOperationHubClient>
{
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
                || !await httpContext.HasDivisionAccessAsync(
                    activeDivisionId,
                    cancellationToken: Context.ConnectionAborted)))
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
