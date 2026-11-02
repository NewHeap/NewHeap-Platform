using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Time and the person for alerts (FB87 phase 2, lane AT) on both relational providers: several digest
/// times a day in each person's own time zone and the person's own times, quiet hours across midnight
/// from the code, an administrator and the person, urgent alerts through quiet hours only where the kind
/// lets them, and a person's mutes of one kind about one subject; and, without a database, quiet hours
/// across the changes of daylight saving time.
/// </summary>
public sealed class AssistantAlertTimingTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    internal const string Twice = "timing-twice";
    internal const string Night = "timing-night";
    internal const string NightUrgent = "timing-night-urgent";
    internal const string QuietDigest = "timing-quiet-digest";
    internal const string NoQuietChoice = "timing-no-quiet-choice";
    internal const string Required = "timing-required";

    /// <summary>Monday 2 November 2026, 06:00 in Amsterdam (CET) and 00:00 in New York (EST).</summary>
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Several_digest_times_plan_the_next_one_in_each_persons_zone_and_a_person_chooses_their_own(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock);
        string[] people = [DeliveryWorld.Traveller];

        // 06:00 in Amsterdam: the 07:30 digest; midnight in New York: its own 07:30.
        var early = await RaiseAsync(host, Twice, "a", recipients: people);
        var planned = await PlannedAsync(host, early.AlertId);
        Assert.Equal(At(2026, 11, 2, 6, 30), planned[DeliveryWorld.Owner]);
        Assert.Equal(At(2026, 11, 2, 12, 30), planned[DeliveryWorld.Traveller]);

        // Noon in Amsterdam: the 16:00 digest; 06:00 in New York: still its 07:30.
        clock.Advance(TimeSpan.FromHours(6));
        var noon = await RaiseAsync(host, Twice, "b", recipients: people);
        planned = await PlannedAsync(host, noon.AlertId);
        Assert.Equal(At(2026, 11, 2, 15, 0), planned[DeliveryWorld.Owner]);
        Assert.Equal(At(2026, 11, 2, 12, 30), planned[DeliveryWorld.Traveller]);

        // The owner chooses three times of their own, in any order; the next one is 13:00 in Amsterdam.
        Assert.True((await UpdateAsync(host, DeliveryWorld.Owner, new NhAssistantAlertPreferenceRequest(Twice)
        {
            DigestTimesGiven = true,
            DigestTimes = ["17:00", "09:00", "13:00", "09:00"]
        })).Success);
        var own = await RaiseAsync(host, Twice, "c");
        Assert.Equal(At(2026, 11, 2, 12, 0), (await PlannedAsync(host, own.AlertId))[DeliveryWorld.Owner]);
        var view = await PreferencesAsync(host, DeliveryWorld.Owner);
        var choice = view.Kinds.Single(kind => kind.Kind.Id == Twice).Choice;
        Assert.Equal([new TimeOnly(9, 0), new TimeOnly(13, 0), new TimeOnly(17, 0)], choice.ChosenDigestTimes);
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(9, 0)), (choice.DigestTime!.Value, choice.ChosenDigestTime!.Value));
        // The first time also stays where a reader of one digest time finds it.
        var stored = (await host.Services.GetRequiredService<NhAssistantNotificationStore>().GetPreferencesAsync(
            DeliveryWorld.Owner,
            NhAssistantNotificationPreferenceCategories.Alert,
            CancellationToken.None))[Twice];
        Assert.Equal(("09:00,13:00,17:00", new TimeOnly(9, 0)), (stored.DigestTimes, stored.DigestTime!.Value));

        // At 16:00 in Amsterdam every delivery due by then goes out, one digest per moment and person.
        clock.Advance(TimeSpan.FromHours(4));
        var result = await DispatchAsync(host);
        Assert.Equal((5, 5, 4), (result.Claimed, result.Sent, result.Notifications));
        Assert.Single(Pushes(host).Sent, push => push.Push.RecipientActorId == DeliveryWorld.Traveller);
        Assert.Equal(3, Pushes(host).Sent.Count(push => push.Push.RecipientActorId == DeliveryWorld.Owner));

        // A list that is no times of day, empty or too long changes nothing; null returns to the kind's times.
        string?[][] invalidLists = [["07:30", "25:00"], [], ["06:00", "07:00", "08:00", "09:00", "10:00"], ["07:30", null]];
        foreach (var times in invalidLists)
        {
            Assert.Equal(
                NhAssistantErrorCodes.NotificationDigestTimeInvalid,
                Code(await UpdateAsync(host, DeliveryWorld.Owner, new NhAssistantAlertPreferenceRequest(Twice) { DigestTimesGiven = true, DigestTimes = times })));
        }
        Assert.Equal(
            NhAssistantErrorCodes.NotificationDigestTimeNotAllowed,
            Code(await UpdateAsync(host, DeliveryWorld.Owner, new NhAssistantAlertPreferenceRequest(Night) { DigestTimesGiven = true, DigestTimes = ["07:00"] })));
        Assert.True((await UpdateAsync(host, DeliveryWorld.Owner, new NhAssistantAlertPreferenceRequest(Twice) { DigestTimesGiven = true, DigestTimes = null })).Success);
        choice = (await PreferencesAsync(host, DeliveryWorld.Owner)).Kinds.Single(kind => kind.Kind.Id == Twice).Choice;
        Assert.Equal(((IReadOnlyList<TimeOnly>?)null, new TimeOnly(7, 30)), (choice.ChosenDigestTimes, choice.DigestTime!.Value));
        Assert.Equal([new TimeOnly(7, 30), new TimeOnly(16, 0)], choice.DigestTimes);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Quiet_hours_across_midnight_hold_a_delivery_back_and_urgent_alerts_pass_only_where_the_kind_lets_them(AssistantTestProvider provider)
    {
        // Monday 22:30 in Amsterdam, 16:30 in New York.
        var evening = At(2026, 11, 2, 21, 30);
        var tuesdaySeven = At(2026, 11, 3, 6, 0);
        var clock = new AssistantTestClock(evening);
        await using var host = await CreateHostAsync(provider, clock);
        string[] people = [DeliveryWorld.Traveller];

        // The kind's quiet hours, 22:00 to 07:00 in each person's zone: the owner waits, New York does not.
        var warning = await RaiseAsync(host, Night, "w", recipients: people);
        var planned = await PlannedAsync(host, warning.AlertId);
        Assert.Equal((tuesdaySeven, evening), (planned[DeliveryWorld.Owner], planned[DeliveryWorld.Traveller]));

        // An urgent alert waits too, unless its kind lets urgent alerts through.
        var urgent = await RaiseAsync(host, Night, "u", NhAssistantAlertUrgency.Urgent);
        Assert.Equal(tuesdaySeven, (await PlannedAsync(host, urgent.AlertId))[DeliveryWorld.Owner]);
        var through = await RaiseAsync(host, NightUrgent, "t", NhAssistantAlertUrgency.Urgent);
        Assert.Equal(evening, (await PlannedAsync(host, through.AlertId))[DeliveryWorld.Owner]);
        var notThrough = await RaiseAsync(host, NightUrgent, "n", NhAssistantAlertUrgency.Warning);
        Assert.Equal(tuesdaySeven, (await PlannedAsync(host, notThrough.AlertId))[DeliveryWorld.Owner]);

        // A digest at 06:00, in the quiet hours, moves to the first digest time after them: 12:00.
        var digest = await RaiseAsync(host, QuietDigest, "d");
        Assert.Equal(At(2026, 11, 3, 11, 0), (await PlannedAsync(host, digest.AlertId))[DeliveryWorld.Owner]);

        // The owner's own quiet hours replace the kind's: they end at 06:00.
        Assert.True((await UpdateAsync(host, DeliveryWorld.Owner, new NhAssistantAlertPreferenceRequest(Night)
        {
            QuietHoursGiven = true,
            QuietHoursStart = "21:00",
            QuietHoursEnd = "06:00"
        })).Success);
        var own = await RaiseAsync(host, Night, "o");
        Assert.Equal(At(2026, 11, 3, 5, 0), (await PlannedAsync(host, own.AlertId))[DeliveryWorld.Owner]);
        var choice = (await PreferencesAsync(host, DeliveryWorld.Owner)).Kinds.Single(kind => kind.Kind.Id == Night).Choice;
        Assert.Equal(new NhAssistantAlertQuietHours(new TimeOnly(21, 0), new TimeOnly(6, 0)), choice.ChosenQuietHours);

        // An administrator's quiet hours, 07:00 to 08:00, skip the 07:30 digest of the morning.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            context.AlertKindSettings.Add(new AssistantAlertKindSetting
            {
                KindId = Twice,
                DeliveryMode = NhAssistantAlertKindSettingModes.DailyDigest,
                DigestTimes = "07:30,16:00",
                WorkdaysOnly = true,
                QuietHoursStart = new TimeOnly(7, 0),
                QuietHoursEnd = new TimeOnly(8, 0),
                CreatedAt = evening,
                CreatedBy = "admin-1",
                UpdatedAt = evening,
                UpdatedBy = "admin-1",
                ConcurrencyStamp = Guid.NewGuid()
            });
            await context.SaveChangesAsync();
        }
        var administered = await RaiseAsync(host, Twice, "x");
        Assert.Equal(At(2026, 11, 3, 15, 0), (await PlannedAsync(host, administered.AlertId))[DeliveryWorld.Owner]);

        // What a kind does not let a person choose is refused, as are quiet hours that are none.
        Assert.Equal(
            NhAssistantErrorCodes.NotificationQuietHoursNotAllowed,
            Code(await UpdateAsync(host, DeliveryWorld.Owner, new NhAssistantAlertPreferenceRequest(NoQuietChoice) { QuietHoursGiven = true, QuietHoursStart = "22:00", QuietHoursEnd = "07:00" })));
        (string Start, string? End)[] invalidHours = [("22:00", "22:00"), ("22:00", null), ("7:00", "08:00"), ("22:00", "24:00")];
        foreach (var (start, end) in invalidHours)
        {
            Assert.Equal(
                NhAssistantErrorCodes.NotificationQuietHoursInvalid,
                Code(await UpdateAsync(host, DeliveryWorld.Owner, new NhAssistantAlertPreferenceRequest(Night) { QuietHoursGiven = true, QuietHoursStart = start, QuietHoursEnd = end })));
        }
        // Null for both resets them to the kind's own.
        Assert.True((await UpdateAsync(host, DeliveryWorld.Owner, new NhAssistantAlertPreferenceRequest(NoQuietChoice) { QuietHoursGiven = true })).Success);

        // At 07:00 the alerts that waited go out, each on its own as they are delivered immediately.
        clock.Advance(tuesdaySeven - evening);
        await DispatchAsync(host);
        var owner = Pushes(host).Sent.Where(push => push.Push.RecipientActorId == DeliveryWorld.Owner).ToArray();
        Assert.Equal(
            new[] { warning.AlertId, urgent.AlertId, through.AlertId, notThrough.AlertId, own.AlertId }.Order(),
            owner.Select(push => push.Push.AlertId!.Value).Order());
        Assert.All(owner, push => Assert.Null(push.Push.DigestId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_mute_withholds_only_that_kind_about_that_subject_for_that_person_and_ends(AssistantTestProvider provider)
    {
        // 11:00 in Amsterdam, outside the quiet hours of the night.
        var start = Monday.AddHours(5);
        var clock = new AssistantTestClock(start);
        await using var host = await CreateHostAsync(provider, clock);
        var mutes = host.Services.GetRequiredService<NhAssistantAlertSubjectMuteStore>();
        await mutes.MuteAsync(DeliveryWorld.Owner, Night, DeliveryAlertTaskOf("a"), "Task a", start, start.AddDays(7), CancellationToken.None);
        // A required kind is never muted, also when a row says so.
        await mutes.MuteAsync(DeliveryWorld.Owner, Required, DeliveryAlertTaskOf("a"), "Task a", start, start.AddDays(7), CancellationToken.None);
        // Muting again never shortens a mute.
        var kept = await mutes.MuteAsync(DeliveryWorld.Owner, Night, DeliveryAlertTaskOf("a"), "Task a", start, start.AddDays(3), CancellationToken.None);
        Assert.Equal(start.AddDays(7), kept.Until);

        string[] people = [DeliveryWorld.Reader];
        var muted = await RaiseAsync(host, Night, "a", recipients: people);
        var otherSubject = await RaiseAsync(host, Night, "b");
        var otherKind = await RaiseAsync(host, NightUrgent, "a");
        var required = await RaiseAsync(host, Required, "a");

        var result = await DispatchAsync(host);
        Assert.Equal((5, 4, 1), (result.Claimed, result.Sent, result.Withheld));
        var deliveries = (await FindAsync(host, muted.AlertId))!.Deliveries.ToDictionary(item => item.RecipientActorId);
        Assert.Equal(
            (NhAssistantAlertDeliveryStatuses.Withheld, NhAssistantAlertWithholdReasons.SubjectMuted),
            (deliveries[DeliveryWorld.Owner].Status, deliveries[DeliveryWorld.Owner].ReasonCode));
        // Another person's delivery of the same alert goes out.
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, deliveries[DeliveryWorld.Reader].Status);
        foreach (var alert in new[] { otherSubject, otherKind, required })
        {
            Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, (await FindAsync(host, alert.AlertId))!.Deliveries.Single().Status);
        }

        // The person's preferences list the mute of the kind they may mute, not the one of the required kind.
        var listed = Assert.Single((await PreferencesAsync(host, DeliveryWorld.Owner)).SubjectMutes);
        Assert.Equal((Night, DeliveryTestSubjectResolver.Type, "task-a", "Task a"), (listed.Mute.KindId, listed.Mute.SubjectType, listed.Mute.SubjectId, listed.Mute.SubjectDisplayName));
        Assert.Empty((await PreferencesAsync(host, DeliveryWorld.Reader)).SubjectMutes);

        // Another person cannot lift it; the owner can.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<NhAssistantAlertPreferences>();
            Assert.True((await preferences.LiftSubjectMutesAsync(DeliveryWorld.Reader, [listed.Mute.Id], CancellationToken.None)).Success);
        }
        Assert.Single((await PreferencesAsync(host, DeliveryWorld.Owner)).SubjectMutes);

        // A mute ends: after seven days the kind about the subject reaches the person again, and the run
        // maintenance removes the mute.
        clock.Advance(TimeSpan.FromDays(7));
        var again = await RaiseAsync(host, Night, "a");
        Assert.Equal(1, (await DispatchAsync(host)).Sent);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, (await FindAsync(host, again.AlertId))!.Deliveries.Single(item => item.RecipientActorId == DeliveryWorld.Owner).Status);
        Assert.Equal(2, await mutes.DeleteEndedAsync(clock.GetUtcNow(), 100, CancellationToken.None));

        // Lifting is idempotent, and forgetting a user removes their mutes.
        await mutes.MuteAsync(DeliveryWorld.Owner, Night, DeliveryAlertTaskOf("c"), null, clock.GetUtcNow(), clock.GetUtcNow().AddDays(1), CancellationToken.None);
        var participant = host.Services.GetServices<INhRetentionParticipant>().OfType<NhAssistantRetentionParticipant>().Single();
        Assert.True((await participant.ForgetUserAsync(Guid.Parse(DeliveryWorld.Owner), CancellationToken.None)).Success);
        Assert.Empty(await mutes.ListActiveAsync(DeliveryWorld.Owner, clock.GetUtcNow(), 10, CancellationToken.None));
    }

    [Theory]
    [InlineData("2026-10-24T21:00:00Z", "2026-10-25T06:00:00Z")]
    [InlineData("2026-10-25T01:30:00Z", "2026-10-25T06:00:00Z")]
    [InlineData("2026-03-28T22:00:00Z", "2026-03-29T05:00:00Z")]
    [InlineData("2026-03-29T04:30:00Z", "2026-03-29T05:00:00Z")]
    [InlineData("2026-03-29T05:00:00Z", "2026-03-29T05:00:00Z")]
    [InlineData("2026-11-02T20:59:00Z", "2026-11-02T20:59:00Z")]
    public void Quiet_hours_from_2200_to_0700_end_at_0700_where_the_person_is_across_daylight_saving_time(string moment, string expected)
    {
        var quietHours = new NhAssistantAlertQuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0));

        var outside = NhAssistantAlertDigestTimes.OutsideQuietHours(Parse(moment), quietHours, Amsterdam);

        Assert.Equal(Parse(expected), outside);
        Assert.Equal(TimeSpan.Zero, outside.Offset);
    }

    [Fact]
    public void Digest_times_in_quiet_hours_are_skipped_and_when_all_are_quiet_the_digest_goes_at_their_end()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var night = new NhAssistantAlertQuietHours(new TimeOnly(22, 0), new TimeOnly(8, 0));
        var lunch = new NhAssistantAlertQuietHours(new TimeOnly(12, 0), new TimeOnly(14, 0));

        // 06:00 in Amsterdam: 07:00 is quiet, so 13:00 follows.
        Assert.Equal(
            At(2026, 11, 2, 12, 0),
            NhAssistantAlertDigestTimes.Next(Monday, [new TimeOnly(7, 0), new TimeOnly(13, 0)], false, night, Amsterdam));
        // Only 07:00, which is quiet: at the end of the quiet hours, 08:00.
        Assert.Equal(
            At(2026, 11, 2, 7, 0),
            NhAssistantAlertDigestTimes.Next(Monday, [new TimeOnly(7, 0)], false, night, Amsterdam));
        // Quiet hours within the day: 12:30 moves to the first time after 14:00, on a working day.
        Assert.Equal(
            At(2026, 11, 2, 21, 0),
            NhAssistantAlertDigestTimes.Next(Monday, [new TimeOnly(12, 30), new TimeOnly(16, 0)], true, lunch, newYork));
        // Friday 23:00 in New York, working days only: Monday 08:00, the end of Monday's quiet hours.
        Assert.Equal(
            At(2026, 11, 2, 13, 0),
            NhAssistantAlertDigestTimes.Next(At(2026, 10, 31, 3, 0), [new TimeOnly(7, 0)], true, night, newYork));
        Assert.True(new NhAssistantAlertQuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)).Contains(new TimeOnly(6, 59)));
        Assert.False(new NhAssistantAlertQuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)).Contains(new TimeOnly(7, 0)));
        Assert.False(NhAssistantAlertQuietHours.IsValid(new TimeOnly(7, 0), new TimeOnly(7, 0)));
    }

    [Fact]
    public void A_kind_declares_up_to_four_digest_times_and_quiet_hours_in_whole_minutes()
    {
        var kind = KindOf(builder => builder.Delivery(delivery => delivery
            .DailyDigests(new TimeOnly(16, 0), new TimeOnly(7, 30))
            .QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)))
            .UrgentBreaksQuietHours());

        Assert.Equal([new TimeOnly(7, 30), new TimeOnly(16, 0)], kind.Delivery.DigestTimes);
        Assert.Equal(new TimeOnly(7, 30), kind.Delivery.DigestTime);
        Assert.Equal(new NhAssistantAlertQuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)), kind.Delivery.QuietHours);
        Assert.True(kind.UrgentBreaksQuietHours);
        Assert.True(kind.Choices.HasFlag(NhAssistantAlertChoices.QuietHours));
        var effective = NhAssistantAlertKindSettings.Merge(kind, null).Delivery;
        Assert.Equal([new TimeOnly(7, 30), new TimeOnly(16, 0)], effective.DigestTimes);
        Assert.Equal(kind.Delivery.QuietHours, effective.QuietHours);
        Assert.Throws<ArgumentOutOfRangeException>(() => KindOf(builder => builder.Delivery(delivery => delivery
            .DailyDigests(new TimeOnly(6, 0), new TimeOnly(7, 0), new TimeOnly(8, 0), new TimeOnly(9, 0), new TimeOnly(10, 0)))));
        Assert.Throws<ArgumentOutOfRangeException>(() => KindOf(builder => builder.Delivery(delivery => delivery.DailyDigests())));
        Assert.Throws<ArgumentOutOfRangeException>(() => KindOf(builder => builder.Delivery(delivery => delivery.DailyDigests(new TimeOnly(7, 30, 15)))));
        Assert.Throws<ArgumentOutOfRangeException>(() => KindOf(builder => builder.Delivery(delivery => delivery.QuietHours(new TimeOnly(22, 0), new TimeOnly(22, 0)))));
    }

    internal static NhAiSubjectRef DeliveryAlertTaskOf(string id)
    {
        return AssistantAlertDeliveryTests.TaskOf(id);
    }

    private static NhAssistantAlertKind KindOf(Func<NhAssistantAlertKindBuilder, NhAssistantAlertKindBuilder> configure)
    {
        var builder = new NhAssistantAlertKindBuilder("timing-declared")
            .DisplayName("tests.alerts.declared")
            .Title((_, _) => "A declared alert");
        return configure(builder).Build();
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute)
    {
        return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset Parse(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string? Code(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private static DeliveryPushes Pushes(AssistantTestHost host) => host.Services.GetRequiredService<DeliveryPushes>();

    private static Task<AssistantAlert?> FindAsync(AssistantTestHost host, Guid alertId)
    {
        return host.Services.GetRequiredService<NhAssistantAlertStore>().FindAsync(alertId, CancellationToken.None);
    }

    private static async Task<IReadOnlyDictionary<string, DateTimeOffset>> PlannedAsync(AssistantTestHost host, Guid alertId)
    {
        var alert = await FindAsync(host, alertId);
        Assert.NotNull(alert);
        return alert.Deliveries.ToDictionary(item => item.RecipientActorId, item => item.DueAt!.Value);
    }

    private static async Task<TaskResult> UpdateAsync(AssistantTestHost host, string actorId, NhAssistantAlertPreferenceRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAlertPreferences>().UpdateAsync(actorId, [request], CancellationToken.None);
    }

    private static async Task<NhAssistantAlertPreferenceView> PreferencesAsync(AssistantTestHost host, string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAlertPreferences>().GetAsync(actorId, CancellationToken.None);
    }

    private static async Task<NhAssistantAlertReceipt> RaiseAsync(
        AssistantTestHost host,
        string kindId,
        string task,
        NhAssistantAlertUrgency? urgency = null,
        IReadOnlyList<string>? recipients = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var raised = await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RaiseAsync(new NhAssistantAlert(kindId, DeliveryAlertTaskOf(task))
        {
            // Every raise is a new alert, so each one is planned anew.
            Key = Guid.NewGuid().ToString("N"),
            Urgency = urgency,
            Parameters = new Dictionary<string, string> { ["task"] = "Task " + task },
            Recipients = recipients ?? []
        });
        Assert.True(raised.Success, string.Join(", ", raised.GetResultItems().Select(item => item.Name)));
        return raised.Data!;
    }

    private static async Task<NhAssistantAlertDispatchResult> DispatchAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAlertDispatcher>().DispatchAsync(CancellationToken.None);
    }

    private Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, AssistantTestClock clock)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<DeliveryWorld>();
                services.AddSingleton<DeliveryPushes>();
                services.AddSingleton<DeliveryInbox>();
                services.AddSingleton<INhUserNotificationSink>(provider => provider.GetRequiredService<DeliveryInbox>());
                services.AddSingleton<INhActorDirectory, DeliveryActorDirectory>();
                services.Replace(ServiceDescriptor.Scoped<INhAssistantNotifier, DeliveryNotifier>());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, DeliveryPrincipalFactory>());
                services.AddSingleton(Substitute.For<INhBackgroundOperationService>());
            },
            assistantBuilder: assistant => AddTimingKinds(assistant)
                .AddSubjectType<DeliveryTestSubjectResolver>()
                .UsePersonTimeZones<DeliveryTimeZones>()
                .ConfigureAlerts(alerts => alerts.TimeZone = "Europe/Amsterdam"));
    }

    /// <summary>
    /// The kinds of the timing tests: two digests on working days, immediately with quiet hours at night
    /// with and without urgent alerts through them, digests with quiet hours, one whose quiet hours
    /// nobody changes and a required one.
    /// </summary>
    private static NhAssistantBuilder AddTimingKinds(NhAssistantBuilder assistant)
    {
        return assistant
            .AddAlertKind(Twice, kind => Texts(kind.DisplayName("tests.alerts.twice"))
                .Delivery(delivery => delivery.DailyDigests(new TimeOnly(7, 30), new TimeOnly(16, 0)).WorkdaysOnly()))
            .AddAlertKind(Night, kind => Texts(kind.DisplayName("tests.alerts.night").Urgency(NhAssistantAlertUrgency.Warning))
                .Delivery(delivery => delivery.Immediately().QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0))))
            .AddAlertKind(NightUrgent, kind => Texts(kind.DisplayName("tests.alerts.night-urgent"))
                .Delivery(delivery => delivery.QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)))
                .UrgentBreaksQuietHours())
            .AddAlertKind(QuietDigest, kind => Texts(kind.DisplayName("tests.alerts.quiet-digest"))
                .Delivery(delivery => delivery.DailyDigests(new TimeOnly(6, 0), new TimeOnly(12, 0)).QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0))))
            .AddAlertKind(NoQuietChoice, kind => Texts(kind.DisplayName("tests.alerts.no-quiet-choice"))
                .PersonMayChange(NhAssistantAlertChoices.Mute | NhAssistantAlertChoices.DigestTime))
            .AddAlertKind(Required, kind => Texts(kind.DisplayName("tests.alerts.required").Urgency(NhAssistantAlertUrgency.Urgent)).Required());
    }

    private static NhAssistantAlertKindBuilder Texts(NhAssistantAlertKindBuilder kind)
    {
        return kind
            .Title((_, parameters) => $"{parameters["task"]} needs attention")
            .Recipients(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit));
    }
}
