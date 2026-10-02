using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Live;

namespace NewHeap.Platform.AI.Chat.AspNet.Live;

/// <summary>
/// Client methods of the assistant hub. Each carries one live update for the receiving actor.
/// </summary>
public interface INhAssistantHubClient
{
    Task ConversationChanged(NhAssistantLiveConversationDto message);

    Task ConversationRead(NhAssistantLiveReadDto message);

    Task ConversationRemoved(NhAssistantLiveRemovedDto message);

    Task ConversationEvent(NhAssistantLiveEventDto message);
}

/// <summary>
/// Live updates of conversations. A connection joins the group of its authenticated actor; the
/// server decides per event which actors belong to the conversation, so a removed participant
/// stops receiving updates without any client action. Clients never send to the hub.
/// </summary>
public sealed class NhAssistantHub : Hub<INhAssistantHubClient>
{
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var options = httpContext?.RequestServices.GetRequiredService<IOptionsMonitor<NhAssistantOptions>>().CurrentValue;
        if (httpContext is null || options is not { Enabled: true })
        {
            Context.Abort();
            throw new HubException("The assistant is not available.");
        }

        var caller = await httpContext.RequestServices
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, Context.ConnectionAborted);
        if (!caller.Success)
        {
            Context.Abort();
            throw new HubException("An authenticated assistant caller is required.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(caller.Data.ActorId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// The group of one actor. Actor ids can be long or contain any character, so the group name
    /// uses their hash.
    /// </summary>
    internal static string GroupFor(string actorId)
    {
        return "nh-assistant-actor:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actorId)));
    }
}

/// <summary>
/// Sends live updates through <see cref="NhAssistantHub"/>. Scale-out uses the host's SignalR
/// backplane like any other hub.
/// </summary>
internal sealed class NhAssistantSignalRLiveUpdateTransport(
    IHubContext<NhAssistantHub, INhAssistantHubClient> hub) : INhAssistantLiveUpdateTransport
{
    public Task SendAsync(
        IReadOnlyCollection<string> actorIds,
        NhAssistantLiveEvent evt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actorIds);
        ArgumentNullException.ThrowIfNull(evt);
        if (actorIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        var clients = hub.Clients.Groups(actorIds.Distinct(StringComparer.Ordinal).Select(NhAssistantHub.GroupFor).ToArray());
        var send = evt switch
        {
            NhAssistantLiveConversationChanged changed => clients.ConversationChanged(new NhAssistantLiveConversationDto(
                changed.State.ConversationId,
                changed.State.Status,
                changed.State.Title,
                changed.State.UpdatedAt,
                changed.State.ActiveActorId,
                changed.State.LastMessageSequence,
                changed.State.ParticipantCount)),
            NhAssistantLiveConversationRead read => clients.ConversationRead(
                new NhAssistantLiveReadDto(read.ConversationId, read.LastReadSequence)),
            NhAssistantLiveConversationRemoved removed => clients.ConversationRemoved(
                new NhAssistantLiveRemovedDto(removed.ConversationId)),
            NhAssistantLiveMessageCreated created => clients.ConversationEvent(new NhAssistantLiveEventDto(
                created.ConversationId,
                created.ActorId,
                "message.created",
                JsonSerializer.SerializeToElement(
                    NhAssistantDtoMapper.ToDto(created.Message),
                    NhAssistantJsonSerializerContext.Default.NhAssistantMessageDto))),
            NhAssistantLiveTurnEvent turn => SendTurnEventAsync(clients, turn),
            _ => throw new InvalidOperationException($"Unknown assistant live event '{evt.GetType().Name}'.")
        };
        return send.WaitAsync(cancellationToken);
    }

    private static Task SendTurnEventAsync(INhAssistantHubClient clients, NhAssistantLiveTurnEvent turn)
    {
        var (name, data) = NhAssistantServerSentEventsResult.Serialize(turn.Event);
        using var document = JsonDocument.Parse(data);
        return clients.ConversationEvent(new NhAssistantLiveEventDto(
            turn.ConversationId,
            turn.ActorId,
            name,
            document.RootElement.Clone()));
    }
}
