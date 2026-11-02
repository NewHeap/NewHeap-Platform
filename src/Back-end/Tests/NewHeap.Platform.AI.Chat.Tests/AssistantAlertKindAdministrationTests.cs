using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
/// The administration of alert kinds (FB87 phase 2, lane AD) over HTTP: every kind with the bounds its
/// code sets, an administrator's settings within those bounds with their version, the effective delivery
/// that the scheduler and a person's preferences read, a kind that is off, the numbers per kind and the
/// shadow alerts per agent, on both relational providers; and that only administrators see or change it.
/// </summary>
public sealed class AssistantAlertKindAdministrationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string DueToday = "task-due-today";
    private const string Contract = "contract-expires";
    private const string Fixed = "report-reminder";
    private const string Owner = "owner-1";
    private const string Administrator = "admin-1";
    private const string KindsPath = "/api/assistant/admin/alert-kinds";

    /// <summary>Monday 2 November 2026, 05:00 UTC; the application's time zone for alerts is UTC.</summary>
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_administrators_delivery_within_the_codes_bounds_is_what_the_scheduler_and_a_persons_preferences_read(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var app = await StartAsync(provider, clock);
        using var admin = app.CreateClient(Administrator, admin: true);
        using var owner = app.CreateClient(Owner);

        // What the code declares and allows, before anybody changed the kind.
        var view = await ReadAsync<NhAssistantAdminAlertKindsDto>(await admin.GetAsync(KindsPath));
        Assert.Equal([Contract, Fixed, DueToday], view.Kinds.Select(kind => kind.Id));
        var dueToday = view.Kinds[2];
        Assert.Equal(("tests.alerts.task-due-today", "warning", false, true), (dueToday.DisplayNameKey, dueToday.Urgency, dueToday.Required, dueToday.Enabled));
        Assert.Equal(new NhAssistantAdminAlertKindChoicesDto(true, true, true, true, true), dueToday.AdministratorMayChange);
        Assert.Equal(("daily-digest", "07:30", true, "urgent", null), Shape(dueToday.CodeDelivery));
        Assert.Equal(Shape(dueToday.CodeDelivery), Shape(dueToday.Delivery));
        Assert.Equal((true, null, 0), (dueToday.Settings.Enabled, dueToday.Settings.Delivery, dueToday.Settings.Version));
        // A required kind is never switched off; a kind whose code allows nothing keeps its delivery.
        Assert.Equal((true, false), (view.Kinds[0].Required, view.Kinds[0].AdministratorMayChange.SwitchOff));
        Assert.Equal(new NhAssistantAdminAlertKindChoicesDto(false, false, false, false, false), view.Kinds[1].AdministratorMayChange);

        // Two digests a day, every day, warnings at once and quiet hours at night.
        var changed = await ReadAsync<NhAssistantAdminAlertKindDto>(await admin.PutAsJsonAsync(
            $"{KindsPath}/{DueToday}",
            new NhAssistantAdminAlertKindRequest(
                true,
                new NhAssistantAdminAlertDeliveryRequest("daily-digest", ["12:00", "06:45"], false, "warning", new NhAssistantAdminAlertQuietHoursRequest("22:00", "06:00")),
                0,
                "Earlier for the morning shift"),
            Json));
        Assert.Equal(("daily-digest", "06:45", false, "warning", "22:00-06:00"), Shape(changed.Delivery));
        Assert.Equal(["06:45", "12:00"], changed.Delivery.DigestTimes);
        Assert.Equal((1, Administrator, "Earlier for the morning shift"), (changed.Settings.Version, changed.Settings.ChangedBy, changed.Settings.Reason));
        Assert.Equal(Monday, changed.Settings.ChangedAt);
        Assert.Null(changed.Numbers);
        var audited = Assert.Single(Audit(app), evt => evt.Kind == NhAssistantAuditEventKind.AdminAlertKindChanged);
        Assert.Equal(
            (Administrator, DueToday, DueToday, "created", "digest-times,workdays-only,immediately-from,quiet-hours"),
            (audited.ActorId, audited.AlertKind, audited.ObjectId, audited.ResultCode, audited.ChangedFields));

        // The scheduler plans the first digest time of the administrator's delivery, and a warning at once.
        var info = await RaiseAsync(app, "a", NhAssistantAlertUrgency.Info);
        Assert.True(info.Success);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 45, 0, TimeSpan.Zero), (await DeliveryAsync(app, info.Data!.AlertId)).DueAt);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<INhAssistantAlertScheduler>();
            var kind = scope.ServiceProvider.GetRequiredService<NhAssistantRegistrationState>().AlertKinds[DueToday];
            Assert.Equal(Monday, await scheduler.DueAtAsync(new NhAssistantAlertScheduleRequest(kind, NhAssistantAlertUrgency.Warning, Owner, Monday), CancellationToken.None));
        }

        // A person's preferences show the administrator's default.
        var preferences = await ReadAsync<JsonElement>(await owner.GetAsync("/api/assistant/notifications"));
        var preference = preferences.GetProperty("alertKinds").EnumerateArray().Single(item => item.GetProperty("id").GetString() == DueToday);
        Assert.Equal(("06:45", false), (preference.GetProperty("defaultDigestTime").GetString(), preference.GetProperty("workdaysOnly").GetBoolean()));

        // The same settings again change nothing; a stale version is refused.
        var again = await ReadAsync<NhAssistantAdminAlertKindDto>(await admin.PutAsJsonAsync(
            $"{KindsPath}/{DueToday}",
            new NhAssistantAdminAlertKindRequest(true, new NhAssistantAdminAlertDeliveryRequest("daily-digest", ["06:45", "12:00"], false, "warning", new NhAssistantAdminAlertQuietHoursRequest("22:00", "06:00")), 1),
            Json));
        Assert.Equal(1, again.Settings.Version);
        await ErrorAsync(
            await admin.PutAsJsonAsync($"{KindsPath}/{DueToday}", new NhAssistantAdminAlertKindRequest(true, null, 0), Json),
            HttpStatusCode.Conflict,
            NhAssistantAdminErrorCodes.VersionConflict);

        // Back to the code's delivery.
        var reset = await ReadAsync<NhAssistantAdminAlertKindDto>(await admin.PutAsJsonAsync(
            $"{KindsPath}/{DueToday}",
            new NhAssistantAdminAlertKindRequest(true, null, 1),
            Json));
        Assert.Equal((2, null), (reset.Settings.Version, reset.Settings.Delivery));
        Assert.Equal(("daily-digest", "07:30", true, "urgent", null), Shape(reset.Delivery));
        Assert.Equal(2, Audit(app).Count(evt => evt.Kind == NhAssistantAuditEventKind.AdminAlertKindChanged));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_kind_that_is_off_raises_nothing_withholds_what_waits_and_leaves_the_preferences_and_the_badge(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var app = await StartAsync(provider, clock);
        using var admin = app.CreateClient(Administrator, admin: true);
        using var owner = app.CreateClient(Owner);
        var waiting = await RaiseAsync(app, "a", NhAssistantAlertUrgency.Info);
        Assert.True(waiting.Success);

        var off = await ReadAsync<NhAssistantAdminAlertKindDto>(await admin.PutAsJsonAsync(
            $"{KindsPath}/{DueToday}",
            new NhAssistantAdminAlertKindRequest(false, null, 0),
            Json));
        Assert.Equal((false, false, 1), (off.Enabled, off.Settings.Enabled, off.Settings.Version));
        Assert.Equal("enabled", Assert.Single(Audit(app), evt => evt.Kind == NhAssistantAuditEventKind.AdminAlertKindChanged).ChangedFields);

        // Nothing of the kind is raised, not even an update of the open alert.
        var refused = await RaiseAsync(app, "b", NhAssistantAlertUrgency.Urgent);
        Assert.False(refused.Success);
        Assert.Equal(NhAssistantAlertCodes.KindOff, NhAssistantResults.CodeOf(refused));
        Assert.Equal(NhAssistantAlertCodes.KindOff, NhAssistantResults.CodeOf(await RaiseAsync(app, "a", NhAssistantAlertUrgency.Urgent)));
        Assert.Equal(1, await CountAlertsAsync(app));

        // A person has nothing to choose about it.
        var preferences = await ReadAsync<JsonElement>(await owner.GetAsync("/api/assistant/notifications"));
        Assert.DoesNotContain(preferences.GetProperty("alertKinds").EnumerateArray(), item => item.GetProperty("id").GetString() == DueToday);

        // What waited for the 07:30 digest is withheld; the list keeps it, the badge leaves it out.
        clock.Advance(TimeSpan.FromHours(2.5));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NhAssistantAlertDispatcher>().DispatchAsync(CancellationToken.None);
        }
        var withheld = await DeliveryAsync(app, waiting.Data!.AlertId);
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Withheld, NhAssistantAlertWithholdReasons.KindOff), (withheld.Status, withheld.ReasonCode));
        var summary = await ReadAsync<JsonElement>(await owner.GetAsync("/api/assistant/alerts/summary"));
        Assert.Equal(0, summary.GetProperty("unread").GetInt32());
        var listed = await ReadAsync<JsonElement>(await owner.GetAsync("/api/assistant/alerts"));
        Assert.Equal("kind-off", Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("withheldReason").GetString());

        // Resolving still works.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var resolved = await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().ResolveAsync(new NhAssistantAlertKey(DueToday, TaskOf("a"), null));
            Assert.Equal(1, resolved.Data);
        }

        // On again, the kind raises as before.
        await ReadAsync<NhAssistantAdminAlertKindDto>(await admin.PutAsJsonAsync($"{KindsPath}/{DueToday}", new NhAssistantAdminAlertKindRequest(true, null, 1), Json));
        Assert.True((await RaiseAsync(app, "b", NhAssistantAlertUrgency.Info)).Success);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_numbers_per_kind_count_the_deliveries_of_the_period_and_the_shadow_alerts_per_agent(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new AssistantTestClock(Monday));
        using var admin = app.CreateClient(Administrator, admin: true);
        var start = Monday.AddDays(-10);
        await SeedNumbersAsync(app, start);

        var view = await ReadAsync<NhAssistantAdminAlertKindsDto>(await admin.GetAsync(
            $"{KindsPath}?from={Uri.EscapeDataString(start.ToString("O"))}&to={Uri.EscapeDataString(Monday.ToString("O"))}"));

        Assert.Equal((start, Monday), (view.From, view.To));
        var dueToday = view.Kinds.Single(kind => kind.Id == DueToday);
        Assert.Equal(new NhAssistantAdminAlertKindNumbersDto(Raised: 2, Delivered: 6, Read: 3, Dismissed: 1, ResolvedBeforeReading: 3, Withheld: 2, Muted: 1), dueToday.Numbers);
        Assert.Equal(
            [new NhAssistantAdminAlertShadowAgentDto("agent-a", null, 2), new NhAssistantAdminAlertShadowAgentDto("agent-b", null, 1)],
            dueToday.Shadow);
        Assert.Equal(new NhAssistantAdminAlertKindNumbersDto(1, 1, 0, 0, 0, 0, 0), view.Kinds.Single(kind => kind.Id == Contract).Numbers);
        Assert.Equal(new NhAssistantAdminAlertKindNumbersDto(0, 0, 0, 0, 0, 0, 0), view.Kinds.Single(kind => kind.Id == Fixed).Numbers);
        Assert.Empty(view.Kinds.Single(kind => kind.Id == Fixed).Shadow);

        // The default period ends now on the application's clock; a period before the alerts counts nothing.
        var recent = await ReadAsync<NhAssistantAdminAlertKindsDto>(await admin.GetAsync(KindsPath));
        Assert.Equal((Monday.AddDays(-30), Monday), (recent.From, recent.To));
        var before = await ReadAsync<NhAssistantAdminAlertKindsDto>(await admin.GetAsync(
            $"{KindsPath}?from={Uri.EscapeDataString(start.AddDays(-30).ToString("O"))}&to={Uri.EscapeDataString(start.AddDays(-2).ToString("O"))}"));
        Assert.All(before.Kinds, kind => Assert.Equal(new NhAssistantAdminAlertKindNumbersDto(0, 0, 0, 0, 0, 0, 0), kind.Numbers));
        await ErrorAsync(await admin.GetAsync($"{KindsPath}?from={Uri.EscapeDataString(Monday.ToString("O"))}&to={Uri.EscapeDataString(start.ToString("O"))}"), HttpStatusCode.BadRequest, NhAssistantErrorCodes.MessageInvalid);
    }

    [Fact]
    public async Task A_change_outside_the_codes_bounds_or_malformed_is_refused_and_saves_nothing()
    {
        await using var app = await StartAsync(AssistantTestProvider.PostgreSql, new AssistantTestClock(Monday));
        using var admin = app.CreateClient(Administrator, admin: true);

        await ErrorAsync(
            await admin.PutAsJsonAsync($"{KindsPath}/{Contract}", new NhAssistantAdminAlertKindRequest(false, null, 0), Json),
            HttpStatusCode.BadRequest,
            NhAssistantAdminErrorCodes.AlertKindSwitchOffNotAllowed);
        await ErrorAsync(
            await admin.PutAsJsonAsync($"{KindsPath}/{Fixed}", new NhAssistantAdminAlertKindRequest(false, null, 0), Json),
            HttpStatusCode.BadRequest,
            NhAssistantAdminErrorCodes.AlertKindSwitchOffNotAllowed);
        var notAllowed = await ErrorAsync(
            await admin.PutAsJsonAsync(
                $"{KindsPath}/{Fixed}",
                new NhAssistantAdminAlertKindRequest(true, new NhAssistantAdminAlertDeliveryRequest("daily-digest", ["09:00"], true, null, new NhAssistantAdminAlertQuietHoursRequest("20:00", "08:00")), 0),
                Json),
            HttpStatusCode.BadRequest,
            NhAssistantAdminErrorCodes.AlertKindDeliveryNotAllowed);
        Assert.Equal(["delivery.digestTimes", "delivery.quietHours"], FieldErrors(notAllowed, NhAssistantFieldErrors.NotAllowed));

        // A delivery that repeats the code's is accepted and stays the code's.
        var repeated = await ReadAsync<NhAssistantAdminAlertKindDto>(await admin.PutAsJsonAsync(
            $"{KindsPath}/{Fixed}",
            new NhAssistantAdminAlertKindRequest(true, new NhAssistantAdminAlertDeliveryRequest("daily-digest", ["08:00"], true), 0),
            Json));
        Assert.Equal((0, null), (repeated.Settings.Version, repeated.Settings.Delivery));

        var malformed = new (NhAssistantAdminAlertDeliveryRequest Delivery, string Field)[]
        {
            (new("weekly"), "delivery.mode"),
            (new(null), "delivery.mode"),
            (new("daily-digest", []), "delivery.digestTimes"),
            (new("daily-digest", ["06:00", "08:00", "10:00", "12:00", "14:00"]), "delivery.digestTimes"),
            (new("daily-digest", ["07:30", "07:30"]), "delivery.digestTimes"),
            (new("daily-digest", ["7.30"]), "delivery.digestTimes"),
            (new("daily-digest", ["07:30"], ImmediatelyFrom: "critical"), "delivery.immediatelyFrom"),
            (new("immediately", ["07:30"]), "delivery.digestTimes"),
            (new("immediately", WorkdaysOnly: true), "delivery.workdaysOnly"),
            (new("immediately", QuietHours: new("22:00", "22:00")), "delivery.quietHours"),
            (new("immediately", QuietHours: new("22:00", null)), "delivery.quietHours")
        };
        foreach (var (delivery, field) in malformed)
        {
            var refused = await ErrorAsync(
                await admin.PutAsJsonAsync($"{KindsPath}/{DueToday}", new NhAssistantAdminAlertKindRequest(true, delivery, 0), Json),
                HttpStatusCode.BadRequest,
                NhAssistantAdminErrorCodes.AlertKindDeliveryInvalid);
            Assert.Contains(field, refused.GetProperty("errors").EnumerateObject().Select(item => item.Name));
        }

        var missing = await ErrorAsync(
            await admin.PutAsync($"{KindsPath}/{DueToday}", new StringContent("{\"expectedVersion\":0}", Encoding.UTF8, "application/json")),
            HttpStatusCode.BadRequest,
            NhAssistantAdminErrorCodes.ValidationFailed);
        Assert.Equal(["enabled"], FieldErrors(missing, NhAssistantFieldErrors.Required));
        await ErrorAsync(
            await admin.PutAsJsonAsync($"{KindsPath}/{DueToday}", new NhAssistantAdminAlertKindRequest(true, null, 0, new string('x', 501)), Json),
            HttpStatusCode.BadRequest,
            NhAssistantAdminErrorCodes.ValidationFailed);
        await ErrorAsync(
            await admin.PutAsJsonAsync($"{KindsPath}/unknown-kind", new NhAssistantAdminAlertKindRequest(true, null, 0), Json),
            HttpStatusCode.NotFound,
            NhAssistantAlertCodes.KindUnknown);

        // Immediately, a kind delivered in a digest by its code may become so where the code allows it.
        var immediate = await ReadAsync<NhAssistantAdminAlertKindDto>(await admin.PutAsJsonAsync(
            $"{KindsPath}/{DueToday}",
            new NhAssistantAdminAlertKindRequest(true, new NhAssistantAdminAlertDeliveryRequest("immediately"), 0),
            Json));
        Assert.Equal(("immediately", null, false, null, null), Shape(immediate.Delivery));
        Assert.Equal("delivery-mode,digest-times,workdays-only,immediately-from", Assert.Single(Audit(app), evt => evt.Kind == NhAssistantAuditEventKind.AdminAlertKindChanged).ChangedFields);
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Equal([DueToday], await context.AlertKindSettings.Select(setting => setting.KindId).ToListAsync());
    }

    [Fact]
    public async Task Only_administrators_see_and_change_alert_kinds()
    {
        await using var app = await StartAsync(AssistantTestProvider.PostgreSql, new AssistantTestClock(Monday));
        using var anonymous = app.CreateClient(user: null, access: false);
        using var member = app.CreateClient(Owner);
        using var admin = app.CreateClient(Administrator, admin: true);
        var change = new NhAssistantAdminAlertKindRequest(false, null, 0);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(KindsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"{KindsPath}/{DueToday}", change, Json)).StatusCode);
        await ErrorAsync(await member.GetAsync(KindsPath), HttpStatusCode.Forbidden, NhAssistantAdminErrorCodes.Forbidden);
        await ErrorAsync(await member.PutAsJsonAsync($"{KindsPath}/{DueToday}", change, Json), HttpStatusCode.Forbidden, NhAssistantAdminErrorCodes.Forbidden);
        Assert.DoesNotContain(Audit(app), evt => evt.Kind == NhAssistantAuditEventKind.AdminAlertKindChanged);
        await using (var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            Assert.False(await context.AlertKindSettings.AnyAsync());
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(KindsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{KindsPath}/{DueToday}", change, Json)).StatusCode);
    }

    [Fact]
    public void The_code_declares_what_an_administrator_may_change_and_a_required_kind_is_never_switched_off()
    {
        NhAssistantAlertKindBuilder Kind(string id) => new NhAssistantAlertKindBuilder(id)
            .DisplayName("tests.alerts." + id)
            .Title((_, _) => "Title");

        Assert.Equal(NhAssistantAlertAdminChoices.All, Kind("a").Build().AdministratorChoices);
        Assert.Equal(NhAssistantAlertAdminChoices.Delivery, Kind("b").Required().Build().AdministratorChoices);
        Assert.Equal(NhAssistantAlertAdminChoices.QuietHours, Kind("c").AdministratorMayChange(NhAssistantAlertAdminChoices.QuietHours).Build().AdministratorChoices);
        Assert.Throws<InvalidOperationException>(() => Kind("d").Required().AdministratorMayChange(NhAssistantAlertAdminChoices.All).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => Kind("e").AdministratorMayChange((NhAssistantAlertAdminChoices)64));
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
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<AlertListTaskResolver>()
                .AddAlertKind(DueToday, kind => kind
                    .DisplayName("tests.alerts.task-due-today")
                    .Urgency(NhAssistantAlertUrgency.Warning)
                    .Title((_, parameters) => $"{parameters["task"]} is due today")
                    .Delivery(delivery => delivery.DailyDigest(new TimeOnly(7, 30)).WorkdaysOnly().ImmediatelyFrom(NhAssistantAlertUrgency.Urgent)))
                .AddAlertKind(Contract, kind => kind
                    .DisplayName("tests.alerts.contract-expires")
                    .Urgency(NhAssistantAlertUrgency.Urgent)
                    .Title((_, _) => "The contract expires")
                    .Required())
                .AddAlertKind(Fixed, kind => kind
                    .DisplayName("tests.alerts.report-reminder")
                    .Title((_, _) => "Write the report")
                    .Delivery(delivery => delivery.DailyDigest(new TimeOnly(8, 0)).WorkdaysOnly())
                    .AdministratorMayChange(NhAssistantAlertAdminChoices.None)),
            provider: provider);
    }

    private static NhAiSubjectRef TaskOf(string id)
    {
        return new NhAiSubjectRef("task", "p1." + id);
    }

    private static async Task<Common.Models.TaskResult<NhAssistantAlertReceipt>> RaiseAsync(AssistantWebApplication app, string task, NhAssistantAlertUrgency urgency)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RaiseAsync(new NhAssistantAlert(DueToday, TaskOf(task))
        {
            Urgency = urgency,
            Parameters = new Dictionary<string, string> { ["task"] = "Task " + task }
        });
    }

    private static async Task<AssistantAlertDelivery> DeliveryAsync(AssistantWebApplication app, Guid alertId)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.AlertDeliveries.AsNoTracking().SingleAsync(delivery => delivery.AlertId == alertId && delivery.RecipientActorId == Owner);
    }

    private static async Task<int> CountAlertsAsync(AssistantWebApplication app)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Alerts.CountAsync();
    }

    /// <summary>
    /// Two live alerts of the kind in the period with deliveries in every state, one before it, three
    /// shadow alerts of two agents, and one alert of the required kind.
    /// </summary>
    private static async Task SeedNumbersAsync(AssistantWebApplication app, DateTimeOffset start)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var open = Alert(DueToday, start.AddDays(1));
        open.Deliveries =
        [
            Delivery(open, NhAssistantAlertDeliveryStatuses.Read, sent: true, read: start.AddDays(1).AddHours(2)),
            Delivery(open, NhAssistantAlertDeliveryStatuses.Sent, sent: true),
            Delivery(open, NhAssistantAlertDeliveryStatuses.Withheld, reason: NhAssistantAlertWithholdReasons.Muted),
            Delivery(open, NhAssistantAlertDeliveryStatuses.Withheld, reason: NhAssistantAlertWithholdReasons.LimitReached),
            Delivery(open, NhAssistantAlertDeliveryStatuses.Dropped, reason: NhAssistantAlertDropReasons.NoAccess),
            Delivery(open, NhAssistantAlertDeliveryStatuses.Pending, due: Monday.AddDays(7))
        ];
        var resolved = Alert(DueToday, start.AddDays(2));
        resolved.Status = NhAssistantAlertStatuses.Resolved;
        resolved.ClosedAt = start.AddDays(3);
        resolved.ClosedMarker = resolved.Id;
        resolved.Deliveries =
        [
            // Sent and never read, read before it was resolved, read after it, dismissed unread.
            Delivery(resolved, NhAssistantAlertDeliveryStatuses.Sent, sent: true),
            Delivery(resolved, NhAssistantAlertDeliveryStatuses.Read, sent: true, read: start.AddDays(2).AddHours(1)),
            Delivery(resolved, NhAssistantAlertDeliveryStatuses.Read, sent: true, read: start.AddDays(4)),
            Delivery(resolved, NhAssistantAlertDeliveryStatuses.Dismissed, sent: true, dismissed: start.AddDays(4))
        ];
        var earlier = Alert(DueToday, start.AddDays(-1));
        earlier.Deliveries = [Delivery(earlier, NhAssistantAlertDeliveryStatuses.Sent, sent: true)];
        var contract = Alert(Contract, start.AddDays(5));
        contract.Deliveries = [Delivery(contract, NhAssistantAlertDeliveryStatuses.Sent, sent: true)];
        context.Alerts.AddRange(
            open,
            resolved,
            earlier,
            contract,
            Shadow(DueToday, "agent-a", start.AddDays(1)),
            Shadow(DueToday, "agent-a", start.AddDays(6)),
            Shadow(DueToday, "agent-b", start.AddDays(7)));
        await context.SaveChangesAsync();
    }

    private static AssistantAlert Alert(string kindId, DateTimeOffset raisedAt)
    {
        var id = Guid.NewGuid();
        return new AssistantAlert
        {
            Id = id,
            KindId = kindId,
            ScopeKey = NhAiSubjectInfo.CreateScopeKey(null, null),
            SubjectType = "task",
            SubjectId = "p1." + id.ToString("N")[..8],
            IdentityHash = id.ToString("N") + id.ToString("N"),
            Status = NhAssistantAlertStatuses.Open,
            Urgency = NhAssistantAlertUrgency.Warning,
            RaisedAt = raisedAt,
            UpdatedAt = raisedAt,
            DeliveryPlannedAt = raisedAt,
            ConcurrencyStamp = Guid.NewGuid()
        };
    }

    private static AssistantAlert Shadow(string kindId, string agentId, DateTimeOffset raisedAt)
    {
        var alert = Alert(kindId, raisedAt);
        alert.Shadow = true;
        alert.AgentId = agentId;
        return alert;
    }

    private static AssistantAlertDelivery Delivery(
        AssistantAlert alert,
        string status,
        bool sent = false,
        DateTimeOffset? read = null,
        DateTimeOffset? dismissed = null,
        string? reason = null,
        DateTimeOffset? due = null)
    {
        return new AssistantAlertDelivery
        {
            Id = Guid.NewGuid(),
            AlertId = alert.Id,
            RecipientActorId = "person-" + Guid.NewGuid().ToString("N")[..8],
            Status = status,
            ReasonCode = reason,
            DueAt = due,
            SentAt = sent ? alert.RaisedAt.AddMinutes(5) : null,
            ReadAt = read,
            DismissedAt = dismissed,
            PlannedRevision = 1,
            CreatedAt = alert.RaisedAt,
            UpdatedAt = alert.RaisedAt,
            ConcurrencyStamp = Guid.NewGuid()
        };
    }

    private static NhAssistantAuditEvent[] Audit(AssistantWebApplication app)
    {
        return app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events.ToArray();
    }

    private static (string Mode, string? FirstTime, bool WorkdaysOnly, string? ImmediatelyFrom, string? QuietHours) Shape(NhAssistantAdminAlertDeliveryDto delivery)
    {
        return (
            delivery.Mode,
            delivery.DigestTimes.FirstOrDefault(),
            delivery.WorkdaysOnly,
            delivery.ImmediatelyFrom,
            delivery.QuietHours is { } quiet ? $"{quiet.Start}-{quiet.End}" : null);
    }

    private static string[] FieldErrors(JsonElement error, string value)
    {
        return error.GetProperty("errors").EnumerateObject()
            .Where(field => field.Value.EnumerateArray().Any(item => item.GetString() == value))
            .Select(field => field.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {body}");
            return JsonSerializer.Deserialize<T>(body, Json)!;
        }
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.Clone();
            Assert.Equal(code, error.GetProperty("code").GetString());
            return error;
        }
    }
}
