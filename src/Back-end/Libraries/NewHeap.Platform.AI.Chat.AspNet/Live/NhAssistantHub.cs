using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
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

    /// <summary>
    /// A run, proposal or feedback of an agent thread changed; sent to the thread's participants and
    /// the people on its subject, and for the whole application to the people who watch its agents'
    /// work (<see cref="NhAssistantHub.WatchAgentWork"/>).
    /// </summary>
    Task AgentWorkChanged(NhAssistantLiveAgentWorkDto message);
}

/// <summary>
/// The answer to <see cref="NhAssistantHub.WatchAgentWork"/>: how many agents' work of the whole
/// application the connection now follows, and when the client renews the watch so the server checks
/// the person's rights again.
/// </summary>
/// <param name="Watched">The work of agents for the whole application, per agent and scope, the connection follows.</param>
/// <param name="RefreshAfterSeconds">Renew the watch after this many seconds.</param>
public sealed record NhAssistantLiveWatchDto(int Watched, int RefreshAfterSeconds);

/// <summary>
/// Live updates of conversations. A connection joins the group of its authenticated actor; the
/// server decides per event which actors belong to the conversation, so a removed participant
/// stops receiving updates without any client action. The whole application lists no members, so a
/// client that shows its agents' work, such as the approval inbox or a board, asks to watch it with
/// <see cref="WatchAgentWork"/>: the server joins the connection to the work it decides the person
/// may read and leaves what the person may no longer read whenever the client renews the watch.
/// Clients send nothing else to the hub.
/// </summary>
public sealed class NhAssistantHub : Hub<INhAssistantHubClient>
{
    private const string WatchedGroupsKey = "nh-assistant-agent-work";

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
    /// Follows the work of agents for the whole application that the person may read: their runs and
    /// proposals reach the connection as <c>AgentWorkChanged</c>, which carries no content. The server
    /// decides what the person may read, the agent's view policy included, each time the client calls
    /// it; work the person may no longer read is left. Call it again within
    /// <see cref="NhAssistantLiveWatchDto.RefreshAfterSeconds"/> to keep watching, and after every
    /// reconnect.
    /// </summary>
    public async Task<NhAssistantLiveWatchDto> WatchAgentWork()
    {
        var httpContext = Context.GetHttpContext() ?? throw new HubException("The assistant is not available.");
        var caller = await httpContext.RequestServices
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, Context.ConnectionAborted);
        if (!caller.Success)
        {
            throw new HubException("An authenticated assistant caller is required.");
        }

        // A scope per call: the decisions of the whole application are made fresh, not kept for the
        // lifetime of the connection.
        await using var scope = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<NhAssistantOptions>>().CurrentValue;
        var watchable = options.Enabled
            ? await scope.ServiceProvider.GetRequiredService<NhAssistantAgentWorkWatchers>()
                .WatchableAsync(caller.Data.ActorId, caller.Data.TenantId, Context.ConnectionAborted)
            : [];

        var groups = watchable.Select(work => GroupFor(work)).ToHashSet(StringComparer.Ordinal);
        var joined = WatchedGroups();
        foreach (var group in joined.Except(groups).ToArray())
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
            joined.Remove(group);
        }
        foreach (var group in groups.Except(joined).ToArray())
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
            joined.Add(group);
        }

        return new NhAssistantLiveWatchDto(groups.Count, (int)NhAssistantAgentWorkWatchers.RefreshAfter.TotalSeconds);
    }

    /// <summary>
    /// Stops following the work of agents for the whole application, for example when the screen
    /// that showed it closes.
    /// </summary>
    public async Task UnwatchAgentWork()
    {
        var joined = WatchedGroups();
        foreach (var group in joined.ToArray())
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        }
        joined.Clear();
    }

    private HashSet<string> WatchedGroups()
    {
        if (Context.Items.TryGetValue(WatchedGroupsKey, out var value) && value is HashSet<string> groups)
        {
            return groups;
        }

        var created = new HashSet<string>(StringComparer.Ordinal);
        Context.Items[WatchedGroupsKey] = created;
        return created;
    }

    /// <summary>
    /// The group of one actor. Actor ids can be long or contain any character, so the group name
    /// uses their hash.
    /// </summary>
    internal static string GroupFor(string actorId)
    {
        return "nh-assistant-actor:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actorId)));
    }

    /// <summary>
    /// The group of the people who watch one agent's work for the whole application in one scope.
    /// </summary>
    internal static string GroupFor(NhAssistantWatchedWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return "nh-assistant-agent-work:"
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(work.AgentId + "\n" + work.SubjectId)));
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
            NhAssistantLiveAgentWorkChanged work => clients.AgentWorkChanged(new NhAssistantLiveAgentWorkDto(
                work.ConversationId,
                work.Reason,
                work.ApprovalId,
                work.RunId,
                work.SubjectType,
                work.SubjectId)),
            _ => throw new InvalidOperationException($"Unknown assistant live event '{evt.GetType().Name}'.")
        };
        return send.WaitAsync(cancellationToken);
    }

    public Task SendToWatchersAsync(
        NhAssistantWatchedWork work,
        NhAssistantLiveAgentWorkChanged evt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(evt);
        return hub.Clients.Group(NhAssistantHub.GroupFor(work))
            .AgentWorkChanged(new NhAssistantLiveAgentWorkDto(
                evt.ConversationId,
                evt.Reason,
                evt.ApprovalId,
                evt.RunId,
                evt.SubjectType,
                evt.SubjectId))
            .WaitAsync(cancellationToken);
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
