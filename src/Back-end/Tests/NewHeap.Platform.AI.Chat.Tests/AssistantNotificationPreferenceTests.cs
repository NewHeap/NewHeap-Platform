using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.AspNet.Notifications;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Actors;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// One preference model for the notifications of the assistant (FB87-6, lane AB) over HTTP on both
/// providers: <c>GET notifications</c> shows the caller's time zone and their choices per alert kind,
/// <c>PUT notifications</c> changes only the caller's own choices, refuses what a kind does not allow
/// with stable codes and keeps the optional push switch working; and an alert reaches a subscribed
/// browser through the assistant's Web Push with its title only.
/// </summary>
public sealed class AssistantNotificationPreferenceTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_reads_and_changes_only_their_own_choices_per_alert_kind(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var owner = app.CreateClient("user-1");
        using var colleague = app.CreateClient("user-2");

        var settings = await GetAsync(owner);
        Assert.True(settings.GetProperty("pushEnabled").GetBoolean());
        Assert.Equal("Europe/Amsterdam", settings.GetProperty("timeZone").GetString());
        Assert.Equal(
            [
                AssistantAlertDeliveryTests.Digest,
                AssistantAlertDeliveryTests.Fixed,
                AssistantAlertDeliveryTests.Immediate,
                AssistantAlertDeliveryTests.LockScreen,
                AssistantAlertDeliveryTests.Required,
                AssistantAlertDeliveryTests.UrgentFirst
            ],
            settings.GetProperty("alertKinds").EnumerateArray().Select(kind => kind.GetProperty("id").GetString()));
        var digest = Kind(settings, AssistantAlertDeliveryTests.Digest);
        Assert.Equal("tests.alerts.digest", digest.GetProperty("displayNameKey").GetString());
        Assert.Equal(
            ("info", false, true, true, true, false, "07:30", null, true),
            (
                digest.GetProperty("urgency").GetString(),
                digest.GetProperty("required").GetBoolean(),
                digest.GetProperty("mayMute").GetBoolean(),
                digest.GetProperty("mayChangeDigestTime").GetBoolean(),
                digest.GetProperty("mayChooseChannels").GetBoolean(),
                digest.GetProperty("muted").GetBoolean(),
                digest.GetProperty("defaultDigestTime").GetString(),
                digest.GetProperty("digestTime").GetString(),
                digest.GetProperty("workdaysOnly").GetBoolean()));
        Assert.Equal((true, true), Channels(digest));
        Assert.Equal(["push", "in-app"], digest.GetProperty("availableChannels").EnumerateArray().Select(channel => channel.GetString()));
        var immediate = Kind(settings, AssistantAlertDeliveryTests.Immediate);
        Assert.Equal(("warning", false), (immediate.GetProperty("urgency").GetString(), immediate.GetProperty("mayChangeDigestTime").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, immediate.GetProperty("defaultDigestTime").ValueKind);
        var required = Kind(settings, AssistantAlertDeliveryTests.Required);
        Assert.Equal(("urgent", true, false), (required.GetProperty("urgency").GetString(), required.GetProperty("required").GetBoolean(), required.GetProperty("mayMute").GetBoolean()));
        var fixedKind = Kind(settings, AssistantAlertDeliveryTests.Fixed);
        Assert.Equal(
            (false, false, false, "08:00"),
            (fixedKind.GetProperty("mayMute").GetBoolean(), fixedKind.GetProperty("mayChangeDigestTime").GetBoolean(), fixedKind.GetProperty("mayChooseChannels").GetBoolean(), fixedKind.GetProperty("defaultDigestTime").GetString()));

        // The owner mutes the digest, chooses 06:45 and turns push off for it; the push switch stays.
        var changed = await PutAsync(owner, new
        {
            alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.Digest, muted = true, digestTime = "06:45", channels = new { push = false } } }
        });
        digest = Kind(changed, AssistantAlertDeliveryTests.Digest);
        Assert.Equal((true, "06:45", "07:30"), (digest.GetProperty("muted").GetBoolean(), digest.GetProperty("digestTime").GetString(), digest.GetProperty("defaultDigestTime").GetString()));
        Assert.Equal((false, true), Channels(digest));
        Assert.True(changed.GetProperty("pushEnabled").GetBoolean());

        // The push switch alone keeps working, and a null digest time resets it to the kind's own.
        Assert.False((await PutAsync(owner, new { pushEnabled = false })).GetProperty("pushEnabled").GetBoolean());
        var reset = Kind(await PutAsync(owner, new { alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.Digest, digestTime = (string?)null } } }), AssistantAlertDeliveryTests.Digest);
        Assert.Equal((true, JsonValueKind.Null), (reset.GetProperty("muted").GetBoolean(), reset.GetProperty("digestTime").ValueKind));

        // What a kind does not allow is refused with its code, and nothing changes, the switch included.
        await RefusedAsync(owner, new { alertKinds = new object[] { new { id = "delivery-unknown", muted = true } } }, NhAssistantErrorCodes.NotificationKindUnknown);
        await RefusedAsync(owner, new { pushEnabled = true, alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.Required, muted = true } } }, NhAssistantErrorCodes.NotificationMuteNotAllowed);
        await RefusedAsync(owner, new { alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.Immediate, digestTime = "07:00" } } }, NhAssistantErrorCodes.NotificationDigestTimeNotAllowed);
        await RefusedAsync(owner, new { alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.Fixed, channels = new { inApp = false } } } }, NhAssistantErrorCodes.NotificationChannelsNotAllowed);
        foreach (var malformed in new[] { "7:30", "24:00", "07:30:00", "half past seven" })
        {
            await RefusedAsync(owner, new { alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.Digest, digestTime = malformed } } }, NhAssistantErrorCodes.NotificationDigestTimeInvalid);
        }
        await RefusedAsync(owner, new { alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.Digest, muted = false }, new { id = AssistantAlertDeliveryTests.Digest, muted = true } } }, NhAssistantAdminErrorCodes.ValidationFailed);
        await RefusedAsync(owner, new { }, NhAssistantAdminErrorCodes.ValidationFailed);
        var unchanged = await GetAsync(owner);
        Assert.False(unchanged.GetProperty("pushEnabled").GetBoolean());
        Assert.True(Kind(unchanged, AssistantAlertDeliveryTests.Digest).GetProperty("muted").GetBoolean());

        // The kind's defaults are always accepted, so a client may send every kind back as it read it.
        await PutAsync(owner, new
        {
            alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.Fixed, muted = false, digestTime = (string?)null, channels = new { push = true, inApp = true } } }
        });

        // Another person sees and changes only their own choices, whatever the request names.
        Assert.False(Kind(await GetAsync(colleague), AssistantAlertDeliveryTests.Digest).GetProperty("muted").GetBoolean());
        using (var foreign = await colleague.PutAsync(
            "/api/assistant/notifications?actorId=user-1",
            Json(new { actorId = "user-1", alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.Digest, muted = false, actorId = "user-1" } } })))
        {
            Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        }
        Assert.True(Kind(await GetAsync(owner), AssistantAlertDeliveryTests.Digest).GetProperty("muted").GetBoolean());
        Assert.True((await GetAsync(colleague)).GetProperty("pushEnabled").GetBoolean());

        // An impersonation session acts as the person, as it does for the push switch.
        using var impersonated = app.CreateClient("user-1", impersonatedBy: "admin-9");
        Assert.True(Kind(await PutAsync(impersonated, new { alertKinds = new object[] { new { id = AssistantAlertDeliveryTests.UrgentFirst, muted = true } } }), AssistantAlertDeliveryTests.UrgentFirst).GetProperty("muted").GetBoolean());
        Assert.True(Kind(await GetAsync(owner), AssistantAlertDeliveryTests.UrgentFirst).GetProperty("muted").GetBoolean());
        Assert.False(Kind(await GetAsync(colleague), AssistantAlertDeliveryTests.UrgentFirst).GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task An_alert_reaches_a_subscribed_browser_with_its_title_only_and_the_push_switch_stops_it()
    {
        var pushService = new AlertPushService();
        await using var app = await StartAsync(AssistantTestProvider.PostgreSql, pushService);
        using var owner = app.CreateClient("user-1");
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var browserKey = NhAssistantWebPushKeys.UncompressedPoint(browser.ExportParameters(false).Q);
        var authSecret = RandomNumberGenerator.GetBytes(16);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsync(
                "/api/assistant/notifications/push-subscription",
                Json(new
                {
                    endpoint = "https://fcm.googleapis.com/fcm/send/alerts",
                    keys = new { p256dh = NhAssistantBase64Url.Encode(browserKey), auth = NhAssistantBase64Url.Encode(authSecret) }
                }))).StatusCode);

        var raised = await RaiseAsync(app, "a");
        await DispatchAsync(app);

        var delivery = Assert.Single(pushService.Requests);
        Assert.Equal("normal", delivery.Urgency);
        var payload = JsonDocument.Parse(AssistantWebPushTests.Decrypt(delivery.Body, browser, browserKey, authSecret)).RootElement;
        Assert.Equal(("nh-assistant", "alert"), (payload.GetProperty("type").GetString(), payload.GetProperty("kind").GetString()));
        Assert.Equal(raised.AlertId, payload.GetProperty("alertId").GetGuid());
        Assert.Equal(("Task a is due today", string.Empty), (payload.GetProperty("title").GetString(), payload.GetProperty("body").GetString()));
        Assert.Equal("nh-assistant-alert:" + raised.AlertId.ToString("D"), payload.GetProperty("tag").GetString());
        Assert.False(payload.TryGetProperty("conversationId", out _));
        Assert.DoesNotContain("still open", payload.GetRawText(), StringComparison.Ordinal);

        // The one push switch of the person stops alerts as it stops proposals.
        await owner.PutAsync("/api/assistant/notifications", Json(new { pushEnabled = false }));
        await RaiseAsync(app, "b");
        await DispatchAsync(app);
        Assert.Single(pushService.Requests);
    }

    private async Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, AlertPushService? pushService = null)
    {
        var (publicKey, privateKey) = NhAssistantWebPushKeys.Generate();
        return await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            provider: provider,
            configure: services =>
            {
                services.AddSingleton<INhUserNotificationSink, DeliveryInbox>();
                if (pushService is not null)
                {
                    services.AddHttpClient(NhAssistantWebPushNotifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => pushService);
                }
            },
            assistantBuilder: assistant => AssistantAlertDeliveryTests.AddDeliveryKinds(assistant)
                .AddSubjectType<WebDeliverySubjectResolver>()
                .ConfigureAlerts(alerts => alerts.TimeZone = "Europe/Amsterdam")
                .ConfigurePush(push =>
                {
                    push.PublicKey = publicKey;
                    push.PrivateKey = privateKey;
                    push.Subject = "mailto:assistant@example.com";
                }));
    }

    private static async Task<NhAssistantAlertReceipt> RaiseAsync(AssistantWebApplication app, string task)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var raised = await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RaiseAsync(
            new NhAssistantAlert(AssistantAlertDeliveryTests.Immediate, new NhAiSubjectRef(WebDeliverySubjectResolver.Type, "task-" + task))
            {
                Parameters = new Dictionary<string, string> { ["task"] = "Task " + task, ["open"] = "2" }
            });
        Assert.True(raised.Success);
        Assert.Equal(1, raised.Data!.RecipientCount);
        return raised.Data;
    }

    private static async Task DispatchAsync(AssistantWebApplication app)
    {
        // The host's run maintenance may deliver it first; either way it goes out once.
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantAlertDispatcher>().DispatchAsync(CancellationToken.None);
    }

    private static JsonElement Kind(JsonElement settings, string id)
    {
        return settings.GetProperty("alertKinds").EnumerateArray().Single(kind => kind.GetProperty("id").GetString() == id);
    }

    private static (bool Push, bool InApp) Channels(JsonElement kind)
    {
        var channels = kind.GetProperty("channels");
        return (channels.GetProperty("push").GetBoolean(), channels.GetProperty("inApp").GetBoolean());
    }

    private static async Task<JsonElement> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/assistant/notifications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> PutAsync(HttpClient client, object body)
    {
        using var response = await client.PutAsync("/api/assistant/notifications", Json(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task RefusedAsync(HttpClient client, object body, string code)
    {
        using var response = await client.PutAsync("/api/assistant/notifications", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    private static StringContent Json(object body)
    {
        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed record AlertPushDelivery(string? Urgency, byte[] Body);

    /// <summary>
    /// The push service of the subscribed browser: accepts every message.
    /// </summary>
    private sealed class AlertPushService : HttpMessageHandler
    {
        private readonly ConcurrentQueue<AlertPushDelivery> _requests = new();

        public IReadOnlyList<AlertPushDelivery> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("Urgency", out var urgency);
            _requests.Enqueue(new AlertPushDelivery(urgency?.SingleOrDefault(), await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}

/// <summary>
/// The tasks of the HTTP tests: <c>user-1</c> is responsible for every task and reads it.
/// </summary>
public sealed class WebDeliverySubjectResolver : INhAiSubjectAccessResolver
{
    public const string Type = "delivery-task";

    public string SubjectType => Type;

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, DeliveryWorld.Division))
        {
            DivisionId = DeliveryWorld.Division,
            DisplayName = "Task " + subject.Id,
            ResponsibleActorId = "user-1"
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(actorId == "user-1" ? new NhAiSubjectAccess(NhAiSubjectRole.Participant) : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new NhAiSubjectMember("user-1", NhAiSubjectRole.Participant);
    }
}
