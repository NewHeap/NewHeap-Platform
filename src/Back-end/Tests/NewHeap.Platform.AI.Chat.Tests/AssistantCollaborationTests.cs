using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Notifications;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantCollaborationTests(AssistantDatabaseFixture database)
{
    private const string OwnerName = "Olivia Owner";
    private const string ParticipantName = "Pat Participant";

    [Fact]
    public async Task An_invited_participant_continues_the_conversation_with_names_read_state_and_live_events()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("First answer.")
            .RespondWithText("Second answer.");
        var live = new CapturingLiveUpdates();
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAssistantLiveUpdateTransport>(live)));
        using var owner = app.CreateClient("user-1", name: OwnerName);
        using var participant = app.CreateClient("user-2", name: ParticipantName);
        var id = await CreateConversationAsync(owner);
        await SendAsync(owner, id, "Plan the roadmap.");

        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        using var wrongToken = await participant.PostAsync($"/api/assistant/conversations/{id}/join", Json(new { token = "guess" }));
        Assert.Equal(HttpStatusCode.NotFound, wrongToken.StatusCode);
        Assert.Equal("assistant-share-link-invalid", (await ReadJsonAsync(wrongToken)).GetProperty("code").GetString());

        var token = (await ReadJsonAsync(await owner.PostAsync($"/api/assistant/conversations/{id}/share-link", null)))
            .GetProperty("token").GetString()!;
        Assert.Equal(token, (await GetConversationAsync(owner, id)).GetProperty("shareToken").GetString());

        using var joined = await participant.PostAsync($"/api/assistant/conversations/{id}/join", Json(new { token }));
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var joinedConversation = await ReadJsonAsync(joined);
        Assert.Equal("participant", joinedConversation.GetProperty("role").GetString());
        Assert.Equal("user-2", joinedConversation.GetProperty("currentActorId").GetString());
        Assert.Equal(JsonValueKind.Null, joinedConversation.GetProperty("shareToken").ValueKind);
        Assert.Equal(
            [(OwnerName, "owner"), (ParticipantName, "participant")],
            joinedConversation.GetProperty("members").EnumerateArray()
                .Select(member => (member.GetProperty("displayName").GetString(), member.GetProperty("role").GetString())));
        // History from before joining counts as read.
        Assert.Equal(
            joinedConversation.GetProperty("lastMessageSequence").GetInt32(),
            joinedConversation.GetProperty("lastReadSequence").GetInt32());

        using var participantShares = await participant.PostAsync($"/api/assistant/conversations/{id}/share-link", null);
        Assert.Equal(HttpStatusCode.Forbidden, participantShares.StatusCode);
        Assert.Equal("assistant-owner-required", (await ReadJsonAsync(participantShares)).GetProperty("code").GetString());

        live.Clear();
        await SendAsync(participant, id, "What about Q3?");

        // The model knows who says what; the newest message comes from the participant.
        var request = model.Requests[^1];
        var userTexts = request.Messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray();
        Assert.Equal([$"[{OwnerName}] Plan the roadmap.", $"[{ParticipantName}] What about Q3?"], userTexts);
        var instructions = request.Options!.Instructions!;
        Assert.Contains("# Shared conversation", instructions, StringComparison.Ordinal);
        Assert.Contains($"- People: {OwnerName}, {ParticipantName}", instructions, StringComparison.Ordinal);
        Assert.Contains($"- Latest message from: {ParticipantName}", instructions, StringComparison.Ordinal);

        // The owner's other sessions saw the participant's message and the streamed answer.
        var ownerEvents = live.For("user-1");
        Assert.Contains(ownerEvents, evt => evt is NhAssistantLiveMessageCreated created && created.Message.AuthorActorId == "user-2");
        Assert.Contains(ownerEvents, evt => evt is NhAssistantLiveTurnEvent { Event: Runtime.NhAssistantMessageDeltaEvent });
        Assert.Contains(ownerEvents, evt => evt is NhAssistantLiveConversationChanged { State.Status: "running", State.ActiveActorId: "user-2" });
        Assert.Contains(ownerEvents, evt => evt is NhAssistantLiveConversationChanged { State.Status: "idle" });

        var conversation = await GetConversationAsync(owner, id);
        var messages = conversation.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["user-1", null, "user-2", null], messages.Select(message => message.GetProperty("authorActorId").GetString()));
        Assert.Equal([1, 2, 3, 4], messages.Select(message => message.GetProperty("sequence").GetInt32()));

        var ownerItem = await SingleListItemAsync(owner);
        Assert.Equal("owner", ownerItem.GetProperty("role").GetString());
        Assert.Equal(1, ownerItem.GetProperty("participantCount").GetInt32());
        Assert.Equal(4, ownerItem.GetProperty("lastMessageSequence").GetInt32());
        Assert.Equal(1, ownerItem.GetProperty("lastReadSequence").GetInt32());
        var participantItem = await SingleListItemAsync(participant);
        Assert.Equal("participant", participantItem.GetProperty("role").GetString());
        Assert.Equal(3, participantItem.GetProperty("lastReadSequence").GetInt32());

        live.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/assistant/conversations/{id}/read", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/assistant/conversations/{id}/read", Json(new { sequence = 2 }))).StatusCode);
        Assert.Equal(4, (await SingleListItemAsync(owner)).GetProperty("lastReadSequence").GetInt32());
        var read = Assert.Single(live.For("user-1").OfType<NhAssistantLiveConversationRead>());
        Assert.Equal(4, read.LastReadSequence);
        Assert.Empty(live.For("user-2"));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/assistant/conversations/{id}/share-link")).StatusCode);
        using var outsider = app.CreateClient("user-3");
        using var revoked = await outsider.PostAsync($"/api/assistant/conversations/{id}/join", Json(new { token }));
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);

        live.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/assistant/conversations/{id}/participants/user-2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Contains(live.For("user-2"), evt => evt is NhAssistantLiveConversationRemoved);
        Assert.Equal(0, (await ReadJsonAsync(await participant.GetAsync("/api/assistant/conversations"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Only_the_participant_who_started_a_turn_decides_its_approval()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Active" } })
            .RespondWithText("Done.");
        await using var app = await AssistantWebApplication.StartAsync(database, model);
        using var owner = app.CreateClient("user-1", name: OwnerName);
        using var participant = app.CreateClient("user-2", name: ParticipantName);
        var id = await CreateConversationAsync(owner);
        await JoinAsync(owner, participant, id);

        var events = await SendAsync(participant, id, "Activate the project.");
        var approval = events.Single(evt => evt.Name == "approval.required").Data;
        var decision = Json(new { decision = "approve", expectedProposalHash = approval.GetProperty("proposalHash").GetString() });
        var decideUrl = $"/api/assistant/conversations/{id}/approvals/{approval.GetProperty("approvalId").GetString()}/decide";
        Assert.Equal("user-2", (await GetConversationAsync(owner, id)).GetProperty("activeActorId").GetString());

        using var ownerDecides = await owner.PostAsync(decideUrl, decision);
        Assert.Equal(HttpStatusCode.Forbidden, ownerDecides.StatusCode);
        Assert.Equal("assistant-approval-forbidden", (await ReadJsonAsync(ownerDecides)).GetProperty("code").GetString());
        Assert.Empty(app.Tools.StatusChanges);

        using var participantDecides = await participant.PostAsync(decideUrl, decision);
        var (resumed, _) = await ReadAllAsync(participantDecides);
        Assert.Equal("completed", resumed[^1].Data.GetProperty("status").GetString());
        Assert.Single(app.Tools.StatusChanges);
        Assert.Equal("user-2", app.Tools.Contexts.Last().AccountableOwnerId);
        Assert.Equal(JsonValueKind.Null, (await GetConversationAsync(owner, id)).GetProperty("activeActorId").ValueKind);
    }

    [Fact]
    public async Task Only_the_owner_or_the_turn_starter_can_cancel_and_a_participant_can_leave()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Active" } });
        await using var app = await AssistantWebApplication.StartAsync(database, model);
        using var owner = app.CreateClient("user-1", name: OwnerName);
        using var participant = app.CreateClient("user-2", name: ParticipantName);
        using var other = app.CreateClient("user-3", name: "Quinn");
        var id = await CreateConversationAsync(owner);
        await JoinAsync(owner, participant, id);
        await JoinAsync(owner, other, id);

        await SendAsync(participant, id, "Activate the project.");

        using var otherCancels = await other.PostAsync($"/api/assistant/conversations/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, otherCancels.StatusCode);
        Assert.Equal("assistant-turn-forbidden", (await ReadJsonAsync(otherCancels)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync($"/api/assistant/conversations/{id}/cancel", null)).StatusCode);
        Assert.Equal("idle", (await GetConversationAsync(owner, id)).GetProperty("status").GetString());

        using var participantRemovesOther = await participant.DeleteAsync($"/api/assistant/conversations/{id}/participants/user-3");
        Assert.Equal(HttpStatusCode.Forbidden, participantRemovesOther.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await participant.DeleteAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Equal(1, (await SingleListItemAsync(owner)).GetProperty("participantCount").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
    }

    [Fact]
    public async Task The_owner_invites_through_the_application_directory()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient().RespondWithText("Answer."),
            assistantBuilder: assistant => assistant.UseParticipantDirectory<TestParticipantDirectory>());
        using var owner = app.CreateClient("user-1", name: OwnerName);
        using var invitee = app.CreateClient("user-2");
        var id = await CreateConversationAsync(owner);
        await SendAsync(owner, id, "Plan the roadmap.");

        var status = await ReadJsonAsync(await owner.GetAsync("/api/assistant/status"));
        Assert.True(status.GetProperty("collaboration").GetProperty("directory").GetBoolean());
        Assert.Equal("/hub/assistant", status.GetProperty("collaboration").GetProperty("hubPath").GetString());

        var candidates = await ReadJsonAsync(await owner.GetAsync($"/api/assistant/conversations/{id}/participant-candidates?query=pa"));
        Assert.Equal(["user-2"], candidates.EnumerateArray().Select(entry => entry.GetProperty("actorId").GetString()));
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await owner.GetAsync($"/api/assistant/conversations/{id}/participant-candidates?query=p")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await owner.PostAsync($"/api/assistant/conversations/{id}/participants", Json(new { actorId = "user-9" }))).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PostAsync($"/api/assistant/conversations/{id}/participants", Json(new { actorId = "user-2" }))).StatusCode);

        // An invitation shows as unread until the invitee opens it.
        var item = await SingleListItemAsync(invitee);
        Assert.Equal("participant", item.GetProperty("role").GetString());
        Assert.Equal(0, item.GetProperty("lastReadSequence").GetInt32());
        Assert.Equal(2, item.GetProperty("lastMessageSequence").GetInt32());
        var members = (await GetConversationAsync(invitee, id)).GetProperty("members");
        Assert.Equal("Pat from Planning", members[1].GetProperty("displayName").GetString());
        Assert.Empty((await ReadJsonAsync(await owner.GetAsync($"/api/assistant/conversations/{id}/participant-candidates?query=pa"))).EnumerateArray());
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await invitee.GetAsync($"/api/assistant/conversations/{id}/participant-candidates?query=pa")).StatusCode);
    }

    [Fact]
    public async Task Without_a_directory_direct_invitations_are_unavailable()
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient());
        using var owner = app.CreateClient();
        var id = await CreateConversationAsync(owner);

        using var search = await owner.GetAsync($"/api/assistant/conversations/{id}/participant-candidates?query=pa");
        Assert.Equal(HttpStatusCode.NotFound, search.StatusCode);
        Assert.Equal("assistant-directory-unavailable", (await ReadJsonAsync(search)).GetProperty("code").GetString());
        Assert.False((await ReadJsonAsync(await owner.GetAsync("/api/assistant/status"))).GetProperty("collaboration").GetProperty("directory").GetBoolean());
    }

    [Fact]
    public async Task The_hub_delivers_live_updates_to_a_participant_and_rejects_anonymous_connections()
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient().RespondWithText("Live answer."));
        using var owner = app.CreateClient("user-1", name: OwnerName);
        using var participant = app.CreateClient("user-2", name: ParticipantName);
        var id = await CreateConversationAsync(owner);
        await JoinAsync(owner, participant, id);

        var received = new ConcurrentQueue<NhAssistantLiveEventDto>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = Connect(app, "user-2");
        connection.On<NhAssistantLiveEventDto>("ConversationEvent", message =>
        {
            received.Enqueue(message);
            if (message.Type == "turn.completed")
            {
                completed.TrySetResult();
            }
        });
        await connection.StartAsync();

        await SendAsync(owner, id, "Tell everyone.");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("message.created", received.First().Type);
        Assert.Equal("user-1", received.First().Data.GetProperty("authorActorId").GetString());
        Assert.Equal(
            "Live answer.",
            string.Concat(received.Where(message => message.Type == "message.delta").Select(message => message.Data.GetProperty("text").GetString())));
        Assert.All(received, message => Assert.Equal(Guid.Parse(id), message.ConversationId));

        await using var anonymous = Connect(app, null);
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }

    [Fact]
    public async Task Push_notifications_reach_subscribed_browsers_encrypted_and_respect_the_opt_out()
    {
        var (publicKey, privateKey) = NhAssistantWebPushKeys.Generate();
        var pushService = new FakePushService();
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(loop: true).RespondWithText("Ready."),
            configure: services => services
                .AddHttpClient(NhAssistantWebPushNotifier.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => pushService),
            assistantBuilder: assistant => assistant.ConfigurePush(push =>
            {
                push.PublicKey = publicKey;
                push.PrivateKey = privateKey;
                push.Subject = "mailto:assistant@example.com";
                push.MinimumTurnDuration = TimeSpan.Zero;
            }));
        using var owner = app.CreateClient();
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var browserKey = NhAssistantWebPushKeys.UncompressedPoint(browser.ExportParameters(false).Q);
        var authSecret = RandomNumberGenerator.GetBytes(16);
        const string endpoint = "https://fcm.googleapis.com/fcm/send/browser-1";

        var settings = await ReadJsonAsync(await owner.GetAsync("/api/assistant/notifications"));
        Assert.True(settings.GetProperty("pushEnabled").GetBoolean());
        Assert.True(settings.GetProperty("pushAvailable").GetBoolean());
        Assert.Equal(publicKey, settings.GetProperty("publicKey").GetString());
        using var blocked = await owner.PutAsync(
            "/api/assistant/notifications/push-subscription",
            Json(new { endpoint = "https://attacker.example/push", keys = new { p256dh = NhAssistantBase64Url.Encode(browserKey), auth = NhAssistantBase64Url.Encode(authSecret) } }));
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsync(
                "/api/assistant/notifications/push-subscription",
                Json(new { endpoint, keys = new { p256dh = NhAssistantBase64Url.Encode(browserKey), auth = NhAssistantBase64Url.Encode(authSecret) }, language = "nl" }))).StatusCode);

        var id = await CreateConversationAsync(owner);
        await SendAsync(owner, id, "Draft the quarterly plan.\nWith details.");

        var delivery = Assert.Single(pushService.Requests);
        Assert.Equal(new Uri(endpoint), delivery.Uri);
        Assert.Equal("aes128gcm", delivery.ContentEncoding);
        Assert.Equal("normal", delivery.Headers["Urgency"]);
        Assert.Equal("14400", delivery.Headers["TTL"]);
        Assert.StartsWith("vapid t=", delivery.Headers["Authorization"], StringComparison.Ordinal);
        var payload = JsonDocument.Parse(AssistantWebPushTests.Decrypt(delivery.Body, browser, browserKey, authSecret)).RootElement;
        Assert.Equal("turn-completed", payload.GetProperty("kind").GetString());
        Assert.Equal(id, payload.GetProperty("conversationId").GetString());
        Assert.Equal("Draft the quarterly plan.", payload.GetProperty("title").GetString());
        Assert.Equal("Het antwoord staat klaar.", payload.GetProperty("body").GetString());
        Assert.DoesNotContain("Ready.", payload.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PutAsync("/api/assistant/notifications", Json(new { pushEnabled = false }))).StatusCode);
        await SendAsync(owner, id, "And the next quarter?");
        Assert.Single(pushService.Requests);

        await owner.PutAsync("/api/assistant/notifications", Json(new { pushEnabled = true }));
        pushService.Status = HttpStatusCode.Gone;
        await SendAsync(owner, id, "One more?");
        await SendAsync(owner, id, "And another?");
        // The push service reported the subscription as gone, so it was removed after one attempt.
        Assert.Equal(2, pushService.Requests.Count);
    }

    private static HubConnection Connect(AssistantWebApplication app, string? user)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hub/assistant"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                if (user is not null)
                {
                    options.Headers[AssistantWebApplication.UserHeader] = user;
                    options.Headers[AssistantWebApplication.AccessHeader] = "true";
                }
            })
            .Build();
    }

    private static async Task JoinAsync(HttpClient owner, HttpClient participant, string id)
    {
        using var link = await owner.PostAsync($"/api/assistant/conversations/{id}/share-link", null);
        var token = (await ReadJsonAsync(link)).GetProperty("token").GetString();
        using var joined = await participant.PostAsync($"/api/assistant/conversations/{id}/join", Json(new { token }));
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
    }

    private static async Task<IReadOnlyList<ServerSentEvent>> SendAsync(HttpClient client, string id, string text)
    {
        using var response = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new { text, clientMessageId = Guid.NewGuid().ToString("N") }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (events, _) = await ReadAllAsync(response);
        return events;
    }

    private static async Task<string> CreateConversationAsync(HttpClient client)
    {
        using var created = await client.PostAsync("/api/assistant/conversations", Json(new { agentId = "project-assistant" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ReadJsonAsync(created)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> GetConversationAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync($"/api/assistant/conversations/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> SingleListItemAsync(HttpClient client)
    {
        var list = await ReadJsonAsync(await client.GetAsync("/api/assistant/conversations"));
        return Assert.Single(list.GetProperty("items").EnumerateArray());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Records live updates per actor instead of sending them.
    /// </summary>
    private sealed class CapturingLiveUpdates : INhAssistantLiveUpdateTransport
    {
        private readonly ConcurrentQueue<(string ActorId, NhAssistantLiveEvent Event)> _events = new();

        public Task SendAsync(IReadOnlyCollection<string> actorIds, NhAssistantLiveEvent evt, CancellationToken cancellationToken)
        {
            foreach (var actorId in actorIds)
            {
                _events.Enqueue((actorId, evt));
            }
            return Task.CompletedTask;
        }

        public IReadOnlyList<NhAssistantLiveEvent> For(string actorId)
        {
            return _events.Where(item => item.ActorId == actorId).Select(item => item.Event).ToArray();
        }

        public void Clear()
        {
            _events.Clear();
        }
    }

    private sealed record PushDelivery(Uri Uri, string? ContentEncoding, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private sealed class FakePushService : HttpMessageHandler
    {
        private readonly ConcurrentQueue<PushDelivery> _requests = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

        public IReadOnlyList<PushDelivery> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value));
            _requests.Enqueue(new PushDelivery(
                request.RequestUri!,
                request.Content?.Headers.ContentEncoding.SingleOrDefault(),
                headers,
                await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            return new HttpResponseMessage(Status);
        }
    }
}

internal sealed class TestParticipantDirectory : INhAssistantParticipantDirectory
{
    private static readonly NhAssistantDirectoryEntry[] People =
    [
        new("user-1", "Olivia Owner"),
        new("user-2", "Pat from Planning", "Planning")
    ];

    public ValueTask<IReadOnlyList<NhAssistantDirectoryEntry>> SearchAsync(
        NhAssistantDirectorySearch search,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NhAssistantDirectoryEntry> matches = People
            .Where(person => person.DisplayName.Contains(search.Query, StringComparison.OrdinalIgnoreCase))
            .Take(search.Limit)
            .ToArray();
        return ValueTask.FromResult(matches);
    }

    public ValueTask<NhAssistantDirectoryEntry?> FindAsync(
        NhAssistantDirectoryLookup lookup,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(People.FirstOrDefault(person => person.ActorId == lookup.ActorId));
    }
}
