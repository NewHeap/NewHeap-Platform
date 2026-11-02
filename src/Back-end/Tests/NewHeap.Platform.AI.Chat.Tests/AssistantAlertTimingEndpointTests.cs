using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Time and the person for alerts (FB87 phase 2, lane AT) over HTTP on both relational providers:
/// <c>GET</c> and <c>PUT notifications</c> show and change several digest times and quiet hours next to
/// every field of phase 1, <c>POST alerts/{id}/dismiss</c> mutes the alert's kind about its subject for a
/// bounded number of days, never for a required kind, and a person lists and lifts only their own mutes.
/// </summary>
public sealed class AssistantAlertTimingEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Watch = "mute-watch";
    private const string Digests = "mute-digests";
    private const string Required = "mute-required";
    private const string Owner = "owner-1";
    private const string Reader = "reader-1";
    private const string Outsider = "outsider-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_chooses_several_digest_times_and_quiet_hours_and_every_earlier_field_stays(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(WholeSecond(DateTimeOffset.UtcNow));
        await using var app = await StartAsync(provider, clock);
        using var owner = CreateClient(app, Owner);

        var digests = Kind(await GetSettingsAsync(owner), Digests);
        Assert.Equal(
            ("07:30", JsonValueKind.Null, true, JsonValueKind.Null, true, true),
            (
                digests.GetProperty("defaultDigestTime").GetString(),
                digests.GetProperty("digestTime").ValueKind,
                digests.GetProperty("mayChangeDigestTime").GetBoolean(),
                digests.GetProperty("digestTimes").ValueKind,
                digests.GetProperty("mayChangeQuietHours").GetBoolean(),
                digests.GetProperty("urgentBreaksQuietHours").GetBoolean()));
        Assert.Equal(["07:30", "16:00"], Times(digests.GetProperty("defaultDigestTimes")));
        Assert.Equal(("22:00", "07:00"), Hours(digests.GetProperty("defaultQuietHours")));
        Assert.Equal(JsonValueKind.Null, digests.GetProperty("quietHours").ValueKind);
        var watch = Kind(await GetSettingsAsync(owner), Watch);
        Assert.Empty(Times(watch.GetProperty("defaultDigestTimes")));
        Assert.Equal((JsonValueKind.Null, false), (watch.GetProperty("defaultQuietHours").ValueKind, watch.GetProperty("urgentBreaksQuietHours").GetBoolean()));

        // Several times of their own, and quiet hours; the single digest time shows the first.
        digests = Kind(await PutSettingsAsync(owner, new
        {
            alertKinds = new object[] { new { id = Digests, digestTimes = new[] { "13:00", "08:00" }, quietHours = new { start = "21:00", end = "06:30" } } }
        }), Digests);
        Assert.Equal(["08:00", "13:00"], Times(digests.GetProperty("digestTimes")));
        Assert.Equal("08:00", digests.GetProperty("digestTime").GetString());
        Assert.Equal(("21:00", "06:30"), Hours(digests.GetProperty("quietHours")));

        // The single digest time of phase 1 still sets one time, and null resets the quiet hours.
        digests = Kind(await PutSettingsAsync(owner, new
        {
            alertKinds = new object[] { new { id = Digests, digestTime = "09:15", quietHours = (object?)null } }
        }), Digests);
        Assert.Equal(["09:15"], Times(digests.GetProperty("digestTimes")));
        Assert.Equal(JsonValueKind.Null, digests.GetProperty("quietHours").ValueKind);

        // A change that leaves the times and quiet hours out keeps them.
        await PutSettingsAsync(owner, new { alertKinds = new object[] { new { id = Digests, quietHours = new { start = "23:00", end = "06:00" } } } });
        digests = Kind(await PutSettingsAsync(owner, new { alertKinds = new object[] { new { id = Digests, muted = true } } }), Digests);
        Assert.Equal((true, "09:15"), (digests.GetProperty("muted").GetBoolean(), digests.GetProperty("digestTime").GetString()));
        Assert.Equal(["09:15"], Times(digests.GetProperty("digestTimes")));
        Assert.Equal(("23:00", "06:00"), Hours(digests.GetProperty("quietHours")));

        await RefusedAsync(owner, new { alertKinds = new object[] { new { id = Digests, digestTimes = new[] { "06:00", "07:00", "08:00", "09:00", "10:00" } } } }, NhAssistantErrorCodes.NotificationDigestTimeInvalid);
        await RefusedAsync(owner, new { alertKinds = new object[] { new { id = Digests, quietHours = new { start = "21:00", end = "21:00" } } } }, NhAssistantErrorCodes.NotificationQuietHoursInvalid);
        await RefusedAsync(owner, new { alertKinds = new object[] { new { id = Required, quietHours = new { start = "21:00", end = "06:00" } } } }, NhAssistantErrorCodes.NotificationQuietHoursNotAllowed);
        await RefusedAsync(owner, new { liftSubjectMutes = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray() }, NhAssistantAdminErrorCodes.ValidationFailed);
        Assert.Equal(["09:15"], Times(Kind(await GetSettingsAsync(owner), Digests).GetProperty("digestTimes")));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_mutes_an_object_on_dismissing_and_lists_and_lifts_only_their_own_mutes(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(WholeSecond(DateTimeOffset.UtcNow));
        await using var app = await StartAsync(provider, clock);
        using var owner = CreateClient(app, Owner);
        using var reader = CreateClient(app, Reader);
        using var outsider = CreateClient(app, Outsider);
        var task = new NhAiSubjectRef("task", "p1." + Guid.NewGuid().ToString("N")[..8]);
        var watched = await RaiseAsync(app, Watch, task, "k1", Reader);
        var required = await RaiseAsync(app, Required, task, "k1");

        var list = await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts"));
        Assert.True(Item(list, watched).GetProperty("mayMuteSubject").GetBoolean());
        Assert.False(Item(list, required).GetProperty("mayMuteSubject").GetBoolean());

        // Out of bounds, malformed, a required kind and someone else's alert change nothing.
        foreach (var days in new[] { 0, 91 })
        {
            await DismissRefusedAsync(owner, watched, new { muteSubjectForDays = days }, HttpStatusCode.BadRequest, NhAssistantAlertCodes.SubjectMuteDaysInvalid);
        }
        await DismissRefusedAsync(owner, watched, new { muteSubjectForDays = "seven" }, HttpStatusCode.BadRequest, NhAssistantAdminErrorCodes.ValidationFailed);
        await DismissRefusedAsync(owner, required, new { muteSubjectForDays = 7 }, HttpStatusCode.BadRequest, NhAssistantAlertCodes.SubjectMuteNotAllowed);
        await DismissRefusedAsync(outsider, watched, new { muteSubjectForDays = 7 }, HttpStatusCode.NotFound, NhAssistantAlertCodes.NotFound);
        list = await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts"));
        Assert.Equal((NhAssistantAlertListStatuses.Unread, NhAssistantAlertListStatuses.Unread), (Item(list, watched).GetProperty("status").GetString(), Item(list, required).GetProperty("status").GetString()));
        Assert.Empty((await GetSettingsAsync(owner)).GetProperty("subjectMutes").EnumerateArray());

        // Dismissing with seven days mutes the kind about the task for the owner only.
        var dismissed = await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{watched}/dismiss", Json(new { muteSubjectForDays = 7 })));
        Assert.Equal(NhAssistantAlertListStatuses.Dismissed, dismissed.GetProperty("status").GetString());
        var mute = Assert.Single((await GetSettingsAsync(owner)).GetProperty("subjectMutes").EnumerateArray());
        var muteId = mute.GetProperty("id").GetGuid();
        Assert.Equal(
            (Watch, "tests.alerts.mute-watch", "task", task.Id, "Task " + task.Id, "Task", "/tasks/" + task.Id),
            (
                mute.GetProperty("kindId").GetString(),
                mute.GetProperty("displayNameKey").GetString(),
                mute.GetProperty("subjectType").GetString(),
                mute.GetProperty("subjectId").GetString(),
                mute.GetProperty("subjectDisplayName").GetString(),
                mute.GetProperty("subjectTypeName").GetString(),
                mute.GetProperty("subjectLink").GetString()));
        var now = clock.GetUtcNow();
        Assert.Equal(now.AddDays(7), mute.GetProperty("until").GetDateTimeOffset());

        // Dismissing it again for longer extends the mute; for shorter it stays.
        await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{watched}/dismiss", Json(new { muteSubjectForDays = 30 })));
        await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{watched}/dismiss", Json(new { muteSubjectForDays = 7 })));
        mute = Assert.Single((await GetSettingsAsync(owner)).GetProperty("subjectMutes").EnumerateArray());
        Assert.Equal((muteId, now.AddDays(30)), (mute.GetProperty("id").GetGuid(), mute.GetProperty("until").GetDateTimeOffset()));
        // An audit records the dismissal once.
        Assert.Single(app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertDismissed && evt.AlertId == watched);

        // Another person neither reads nor lifts it.
        Assert.Empty((await GetSettingsAsync(reader)).GetProperty("subjectMutes").EnumerateArray());
        await PutSettingsAsync(reader, new { liftSubjectMutes = new[] { muteId } });
        Assert.Single((await GetSettingsAsync(owner)).GetProperty("subjectMutes").EnumerateArray());

        // The kind about the task is withheld for the owner, listed as such and left out of the badge;
        // the reader still receives it.
        var again = await RaiseAsync(app, Watch, task, "k2", Reader);
        Dictionary<string, AssistantAlertDelivery> deliveries;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            // The host's run maintenance may deliver them first; either way they go out once.
            await scope.ServiceProvider.GetRequiredService<NhAssistantAlertDispatcher>().DispatchAsync(CancellationToken.None);
            var alert = await scope.ServiceProvider.GetRequiredService<NhAssistantAlertStore>().FindAsync(again, CancellationToken.None);
            deliveries = alert!.Deliveries.ToDictionary(item => item.RecipientActorId);
        }
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Withheld, NhAssistantAlertWithholdReasons.SubjectMuted), (deliveries[Owner].Status, deliveries[Owner].ReasonCode));
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, deliveries[Reader].Status);
        var listed = Item(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts")), again);
        Assert.Equal((true, NhAssistantAlertWithholdReasons.SubjectMuted), (listed.GetProperty("isWithheld").GetBoolean(), listed.GetProperty("withheldReason").GetString()));
        // The owner's badge counts only the required alert; the reader's counts both of theirs.
        Assert.Equal(1, (await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts/summary"))).GetProperty("unread").GetInt32());
        Assert.Equal(2, (await ReadJsonAsync(await reader.GetAsync("/api/assistant/alerts/summary"))).GetProperty("unread").GetInt32());

        // The owner lifts the mute.
        var lifted = await PutSettingsAsync(owner, new { liftSubjectMutes = new[] { muteId } });
        Assert.Empty(lifted.GetProperty("subjectMutes").EnumerateArray());
        await PutSettingsAsync(owner, new { liftSubjectMutes = new[] { muteId } });
    }

    /// <summary>A moment both providers store exactly: PostgreSQL keeps microseconds only.</summary>
    private static DateTimeOffset WholeSecond(DateTimeOffset moment)
    {
        return new DateTimeOffset(moment.Ticks - (moment.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, AssistantTestClock clock)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<AlertListSubjects>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, AlertPrincipalFactory>());
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
                // The request's language picks the culture the names are written in.
                services.Configure<RequestLocalizationOptions>(options =>
                {
                    CultureInfo[] cultures = [new CultureInfo("en-GB"), new CultureInfo("nl-NL")];
                    options.SupportedCultures = cultures;
                    options.SupportedUICultures = cultures;
                });
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<AlertListTaskResolver>(type => type
                    .Named(new() { ["en"] = "Task", ["nl"] = "Taak" }, fallbackLanguage: "en")
                    .OpenAt("/tasks/{id}"))
                .AddAlertKind(Watch, kind => Texts(kind.DisplayName("tests.alerts.mute-watch")))
                .AddAlertKind(Digests, kind => Texts(kind.DisplayName("tests.alerts.mute-digests"))
                    .Delivery(delivery => delivery.DailyDigests(new TimeOnly(7, 30), new TimeOnly(16, 0)).QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)))
                    .UrgentBreaksQuietHours())
                .AddAlertKind(Required, kind => Texts(kind.DisplayName("tests.alerts.mute-required"))
                    .PersonMayChange(NhAssistantAlertChoices.Channels)
                    .Required()),
            provider: provider);
    }

    private static NhAssistantAlertKindBuilder Texts(NhAssistantAlertKindBuilder kind)
    {
        return kind
            .Urgency(NhAssistantAlertUrgency.Warning)
            .Title((_, parameters) => $"{parameters["task"]} needs a look")
            .Recipients(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit));
    }

    private static async Task<Guid> RaiseAsync(AssistantWebApplication app, string kindId, NhAiSubjectRef task, string key, params string[] recipients)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var raised = await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RaiseAsync(new NhAssistantAlert(kindId, task)
        {
            Key = key,
            Parameters = new Dictionary<string, string> { ["task"] = "Task " + task.Id },
            Recipients = recipients
        });
        Assert.True(raised.Success, string.Join(", ", raised.GetResultItems().Select(item => item.Name)));
        return raised.Data!.AlertId;
    }

    private static HttpClient CreateClient(AssistantWebApplication app, string user)
    {
        var client = app.CreateClient(user);
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        return client;
    }

    private static JsonElement Item(JsonElement list, Guid alertId)
    {
        return list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == alertId);
    }

    private static JsonElement Kind(JsonElement settings, string id)
    {
        return settings.GetProperty("alertKinds").EnumerateArray().Single(kind => kind.GetProperty("id").GetString() == id);
    }

    private static string?[] Times(JsonElement times)
    {
        return times.EnumerateArray().Select(time => time.GetString()).ToArray();
    }

    private static (string?, string?) Hours(JsonElement hours)
    {
        return (hours.GetProperty("start").GetString(), hours.GetProperty("end").GetString());
    }

    private static async Task<JsonElement> GetSettingsAsync(HttpClient client)
    {
        return await ReadJsonAsync(await client.GetAsync("/api/assistant/notifications"));
    }

    private static async Task<JsonElement> PutSettingsAsync(HttpClient client, object body)
    {
        return await ReadJsonAsync(await client.PutAsync("/api/assistant/notifications", Json(body)));
    }

    private static async Task RefusedAsync(HttpClient client, object body, string code)
    {
        var refused = await ReadJsonAsync(await client.PutAsync("/api/assistant/notifications", Json(body)), HttpStatusCode.BadRequest);
        Assert.Equal(code, refused.GetProperty("code").GetString());
    }

    private static async Task DismissRefusedAsync(HttpClient client, Guid alertId, object body, HttpStatusCode status, string code)
    {
        var refused = await ReadJsonAsync(await client.PostAsync($"/api/assistant/alerts/{alertId}/dismiss", Json(body)), status);
        Assert.Equal(code, refused.GetProperty("code").GetString());
    }

    private static StringContent Json(object body)
    {
        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(expected == response.StatusCode, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
    }
}
