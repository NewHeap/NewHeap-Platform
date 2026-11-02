using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The delivery of alerts (FB87 phase 1, lane AB) on both relational providers: immediate alerts alone,
/// one digest per person and digest time in the person's time zone and language, the recipient check
/// right before sending, muted and required kinds, the daily limit per person, claims that keep two
/// dispatchers from sending twice, and channels that fail on their own.
/// </summary>
public sealed class AssistantAlertDeliveryTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    internal const string Immediate = "delivery-immediate";
    internal const string Digest = "delivery-digest";
    internal const string UrgentFirst = "delivery-urgent-first";
    internal const string Required = "delivery-required";
    internal const string Fixed = "delivery-fixed";
    internal const string LockScreen = "delivery-lock-screen";

    /// <summary>Monday 2 November 2026, 06:00 in Amsterdam (CET).</summary>
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

    /// <summary>The 07:30 digest of that Monday in Amsterdam.</summary>
    private static readonly DateTimeOffset MondayDigest = new(2026, 11, 2, 6, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_immediate_alert_goes_out_alone_and_a_persons_digest_collects_their_alerts_at_the_digest_time(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock);
        var now = await RaiseAsync(host, Immediate, "a");
        var lockScreen = await RaiseAsync(host, LockScreen, "l");
        var urgentAtOnce = await RaiseAsync(host, UrgentFirst, "e", NhAssistantAlertUrgency.Urgent);
        var warning = await RaiseAsync(host, Digest, "b", NhAssistantAlertUrgency.Warning);
        var urgent = await RaiseAsync(host, Digest, "c", NhAssistantAlertUrgency.Urgent);
        var info = await RaiseAsync(host, Digest, "d", NhAssistantAlertUrgency.Info);

        // The digest waits for 07:30 in Amsterdam; the immediate ones are due now.
        Assert.Equal(MondayDigest, Single(await FindAsync(host, warning.AlertId)).DueAt);
        Assert.Equal(Monday, Single(await FindAsync(host, urgentAtOnce.AlertId)).DueAt);

        var first = await DispatchAsync(host);
        Assert.Equal((3, 3, 3), (first.Claimed, first.Sent, first.Notifications));
        var pushes = Pushes(host).Sent.ToArray();
        Assert.Equal(3, pushes.Length);
        Assert.All(pushes, push => Assert.Null(push.Push.DigestId));
        // The title only, unless the kind shows its text on a lock screen.
        var single = pushes.Single(push => push.Push.AlertId == now.AlertId);
        Assert.Equal(("Task a is due today", string.Empty, false), (single.Title, single.Body, single.Push.Urgent));
        Assert.Equal(("Task l is due today", "2 tasks still open"), Texts(pushes.Single(push => push.Push.AlertId == lockScreen.AlertId)));
        Assert.True(pushes.Single(push => push.Push.AlertId == urgentAtOnce.AlertId).Push.Urgent);
        var inApp = Inbox(host).Requests.Single(request => request.GroupKey == $"assistant-alert:{now.AlertId:N}");
        Assert.Equal(("Task a is due today", "2 tasks still open", "/alerts", "assistant-alert"), (inApp.Title, inApp.Message, inApp.Url, inApp.Category));
        Assert.Equal(Guid.Parse(DeliveryWorld.Owner), inApp.UserId);
        Assert.Equal(NhUserNotificationLevel.Warning, inApp.Level);
        Assert.Equal(3, Mail(host).Messages.Count);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Pending, Single(await FindAsync(host, info.AlertId)).Status);

        // At 07:30 the three digest alerts go out as one digest, most urgent first.
        clock.Advance(MondayDigest - Monday);
        var digest = await DispatchAsync(host);
        Assert.Equal((3, 3, 1), (digest.Claimed, digest.Sent, digest.Notifications));
        var digestPush = Pushes(host).Sent.Single(push => push.Push.DigestId is not null);
        Assert.Null(digestPush.Push.AlertId);
        Assert.True(digestPush.Push.Urgent);
        Assert.Equal(
            ("3 alerts, 1 urgent, 1 warning", "Task c is due today\nTask b is due today\nTask d is due today"),
            (digestPush.Title, digestPush.Body));
        var digestId = digestPush.Push.DigestId!.Value;
        var thread = Inbox(host).Requests.Single(request => request.GroupKey == $"assistant-alert:digest:{digestId:N}");
        Assert.Equal(("3 alerts, 1 urgent, 1 warning", NhUserNotificationLevel.Error), (thread.Title, thread.Level));
        var mail = Mail(host).Messages.Single(message => message.DigestId == digestId);
        Assert.Equal((DeliveryWorld.Owner, 3, 0, "/alerts"), (mail.RecipientActorId, mail.Alerts.Count, mail.MoreCount, mail.ListUrl));
        Assert.Equal([urgent.AlertId, warning.AlertId, info.AlertId], mail.Alerts.Select(item => item.AlertId));
        foreach (var alert in new[] { warning, urgent, info })
        {
            var delivery = Single(await FindAsync(host, alert.AlertId));
            // The chat channel threw; the others carried the digest.
            Assert.Equal((NhAssistantAlertDeliveryStatuses.Sent, digestId, "web-push,in-app,e-mail", 1), (delivery.Status, delivery.DigestId, delivery.Channels, delivery.SentRevision));
        }

        Assert.Equal(NhAssistantAlertDispatchResult.None, await DispatchAsync(host));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_digest_lists_at_most_its_lines_in_the_persons_language_at_0730_in_their_own_time_zone(AssistantTestProvider provider)
    {
        // Friday 30 October, 13:00 in Amsterdam and 08:00 in New York: today's digests went out, and New
        // York leaves daylight saving time on Sunday.
        var friday = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var clock = new AssistantTestClock(friday);
        await using var host = await CreateHostAsync(provider, clock, limits => limits.MaxAlertDigestLines = 2);
        string[] people = [DeliveryWorld.Dutch, DeliveryWorld.Traveller];
        var urgent = await RaiseAsync(host, Digest, "x", NhAssistantAlertUrgency.Urgent, people);
        await RaiseAsync(host, Digest, "y", NhAssistantAlertUrgency.Warning, people);
        await RaiseAsync(host, Digest, "z", NhAssistantAlertUrgency.Info, people);

        // Working days only: Monday 07:30 in each person's zone, in winter time on both sides.
        var planned = (await FindAsync(host, urgent.AlertId))!.Deliveries.ToDictionary(item => item.RecipientActorId, item => item.DueAt);
        Assert.Equal(MondayDigest, planned[DeliveryWorld.Owner]);
        Assert.Equal(MondayDigest, planned[DeliveryWorld.Dutch]);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 30, 0, TimeSpan.Zero), planned[DeliveryWorld.Traveller]);

        clock.Advance(MondayDigest - friday);
        var amsterdam = await DispatchAsync(host);
        Assert.Equal((6, 2), (amsterdam.Sent, amsterdam.Notifications));
        Assert.DoesNotContain(Pushes(host).Sent, push => push.Push.RecipientActorId == DeliveryWorld.Traveller);
        var dutch = Pushes(host).Sent.Single(push => push.Push.RecipientActorId == DeliveryWorld.Dutch);
        // The person's own language wins over the browser's.
        Assert.Equal(("3 attenties, 1 urgent, 1 waarschuwing", "Task x moet vandaag af\nTask y moet vandaag af\nen nog 1"), (dutch.Title, dutch.Body));
        var owner = Pushes(host).Sent.Single(push => push.Push.RecipientActorId == DeliveryWorld.Owner);
        Assert.Equal(("3 alerts, 1 urgent, 1 warning", "Task x is due today\nTask y is due today\nand 1 more"), (owner.Title, owner.Body));
        var mail = Mail(host).Messages.Single(message => message.RecipientActorId == DeliveryWorld.Dutch);
        Assert.Equal(("nl-NL", 2, 1), (mail.Culture.Name, mail.Alerts.Count, mail.MoreCount));

        // 07:30 in New York.
        clock.Advance(TimeSpan.FromHours(6));
        var newYork = await DispatchAsync(host);
        Assert.Equal((3, 1), (newYork.Sent, newYork.Notifications));
        Assert.Single(Pushes(host).Sent, push => push.Push.RecipientActorId == DeliveryWorld.Traveller);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_recipient_who_lost_reader_whom_the_principal_factory_cannot_build_or_who_moved_division_gets_nothing_at_delivery(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock);
        var raised = await RaiseAsync(host, Immediate, "a", recipients: [DeliveryWorld.Reader, DeliveryWorld.Gone, DeliveryWorld.Mover]);
        Assert.Equal(4, raised.RecipientCount);

        // After the alert was raised, the reader lost their role, the account was removed and an
        // approver moved to another division.
        var world = host.Services.GetRequiredService<DeliveryWorld>();
        world.People[DeliveryWorld.Reader] = (DeliveryWorld.Division, NhAiSubjectRole.None);
        world.Removed[DeliveryWorld.Gone] = true;
        world.People[DeliveryWorld.Mover] = (DeliveryWorld.OtherDivision, NhAiSubjectRole.Approver);

        var result = await DispatchAsync(host);
        Assert.Equal((4, 1, 3), (result.Claimed, result.Sent, result.Dropped));
        Assert.Equal([DeliveryWorld.Owner], Pushes(host).Sent.Select(push => push.Push.RecipientActorId));
        Assert.Equal([Guid.Parse(DeliveryWorld.Owner)], Inbox(host).Requests.Select(request => request.UserId));
        Assert.Equal([DeliveryWorld.Owner], Mail(host).Messages.Select(message => message.RecipientActorId));
        var deliveries = (await FindAsync(host, raised.AlertId))!.Deliveries.ToDictionary(item => item.RecipientActorId);
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Dropped, NhAssistantAlertDropReasons.NoAccess), (deliveries[DeliveryWorld.Reader].Status, deliveries[DeliveryWorld.Reader].ReasonCode));
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Dropped, NhAssistantAlertDropReasons.NotResolved), (deliveries[DeliveryWorld.Gone].Status, deliveries[DeliveryWorld.Gone].ReasonCode));
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Dropped, NhAssistantAlertDropReasons.NoAccess), (deliveries[DeliveryWorld.Mover].Status, deliveries[DeliveryWorld.Mover].ReasonCode));

        // Each one is audited without content.
        var dropped = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRecipientDropped).ToArray();
        Assert.Equal(
            new[] { DeliveryWorld.Gone, DeliveryWorld.Mover, DeliveryWorld.Reader }.Order(),
            dropped.Select(evt => evt.ActorId).Order());
        Assert.All(dropped, evt => Assert.Equal((raised.AlertId, Immediate), (evt.AlertId!.Value, evt.AlertKind)));
        Assert.Equal(
            new[] { NhAssistantAlertDropReasons.NoAccess, NhAssistantAlertDropReasons.NoAccess, NhAssistantAlertDropReasons.NotResolved }.Order(),
            dropped.Select(evt => evt.ResultCode).Order());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_muted_kind_is_withheld_a_required_kind_is_never_muted_and_a_forgotten_user_loses_their_choices(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<NhAssistantAlertPreferences>();
            Assert.True((await preferences.UpdateAsync(DeliveryWorld.Owner, [new NhAssistantAlertPreferenceRequest(Digest) { Muted = true }], CancellationToken.None)).Success);
            Assert.Equal(
                NhAssistantErrorCodes.NotificationMuteNotAllowed,
                Code(await preferences.UpdateAsync(DeliveryWorld.Owner, [new NhAssistantAlertPreferenceRequest(Required) { Muted = true }], CancellationToken.None)));
        }

        // A mute stored for a required kind anyway, as by an older version, has no effect.
        await host.Services.GetRequiredService<NhAssistantNotificationStore>().SavePreferencesAsync(
            DeliveryWorld.Owner,
            NhAssistantNotificationPreferenceCategories.Alert,
            [new NhAssistantNotificationPreferenceChange(Required) { Muted = true }],
            clock.GetUtcNow(),
            CancellationToken.None);
        var muted = await RaiseAsync(host, Digest, "m");
        var required = await RaiseAsync(host, Required, "r");

        clock.Advance(MondayDigest - Monday);
        var result = await DispatchAsync(host);
        Assert.Equal((2, 1, 1, 1), (result.Claimed, result.Sent, result.Withheld, result.Notifications));
        var push = Assert.Single(Pushes(host).Sent);
        Assert.Equal(("1 alert, 1 urgent", "Task r is due today"), (push.Title, push.Body));
        var withheld = Single(await FindAsync(host, muted.AlertId));
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Withheld, NhAssistantAlertWithholdReasons.Muted), (withheld.Status, withheld.ReasonCode));
        Assert.Null(withheld.SentAt);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, Single(await FindAsync(host, required.AlertId)).Status);
        // The muted alert stays in the person's alert list.
        var listed = await host.Services.GetRequiredService<NhAssistantAlertStore>().ListForRecipientAsync(DeliveryWorld.Owner, [], 10, CancellationToken.None);
        Assert.Contains(listed, item => item.AlertId == muted.AlertId);

        // Forgetting the user removes their choices.
        var participant = host.Services.GetServices<INhRetentionParticipant>().OfType<NhAssistantRetentionParticipant>().Single();
        Assert.True((await participant.ForgetUserAsync(Guid.Parse(DeliveryWorld.Owner), CancellationToken.None)).Success);
        Assert.Empty(await host.Services.GetRequiredService<NhAssistantNotificationStore>().GetPreferencesAsync(
            DeliveryWorld.Owner,
            NhAssistantNotificationPreferenceCategories.Alert,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Resolved_expired_and_read_alerts_leave_a_pending_digest_also_when_resolved_after_the_claim(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock);
        var resolved = await RaiseAsync(host, Digest, "k1");
        var expired = await RaiseAsync(host, Digest, "k2", expiresAt: Monday.AddHours(1));
        var read = await RaiseAsync(host, Digest, "k3");
        var resolvedAfterClaim = await RaiseAsync(host, Digest, "k4");
        var left = await RaiseAsync(host, Digest, "k5");

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var alerts = scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>();
            Assert.Equal(1, (await alerts.ResolveAsync(new NhAssistantAlertKey(Digest, TaskOf("k1"), key: null))).Data);
        }
        Assert.True(await host.Services.GetRequiredService<NhAssistantAlertStore>().MarkReadAsync(read.AlertId, DeliveryWorld.Owner, clock.GetUtcNow(), CancellationToken.None));

        // The fourth is resolved while the delivery checks its recipient, after it claimed the delivery.
        var world = host.Services.GetRequiredService<DeliveryWorld>();
        var armed = 1;
        world.BeforeAccess = async (subject, _) =>
        {
            if (subject.Id == TaskOf("k4").Id && Interlocked.Exchange(ref armed, 0) == 1)
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().ResolveAsync(new NhAssistantAlertKey(Digest, subject, key: null));
            }
        };

        // The run maintenance expires the alert whose expiry passed, then delivers what is left.
        clock.Advance(MondayDigest - Monday);
        await host.Services.GetServices<IHostedService>().OfType<NhAssistantRunMaintenance>().Single().RunOnceAsync(CancellationToken.None);

        var push = Assert.Single(Pushes(host).Sent);
        Assert.Equal(("1 alert", "Task k5 is due today"), (push.Title, push.Body));
        Assert.Equal([left.AlertId], Assert.Single(Mail(host).Messages).Alerts.Select(item => item.AlertId));
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Resolved, Single(await FindAsync(host, resolved.AlertId)).Status);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Expired, Single(await FindAsync(host, expired.AlertId)).Status);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Read, Single(await FindAsync(host, read.AlertId)).Status);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Resolved, Single(await FindAsync(host, resolvedAfterClaim.AlertId)).Status);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, Single(await FindAsync(host, left.AlertId)).Status);

        // Raising the read alert again the same way does not plan it again.
        await RaiseAsync(host, Digest, "k3");
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Read, Single(await FindAsync(host, read.AlertId)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_daily_limit_per_person_holds_and_a_withheld_alert_raised_again_goes_out_on_a_later_day(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock, limits => limits.MaxAlertsPerPersonPerDay = 2);
        await RaiseAsync(host, Immediate, "a");
        await RaiseAsync(host, Immediate, "b");
        // A minute later, so it is the last of the three that fall due.
        clock.Advance(TimeSpan.FromMinutes(1));
        var third = await RaiseAsync(host, Immediate, "c");

        var result = await DispatchAsync(host);
        Assert.Equal((3, 2, 1), (result.Claimed, result.Sent, result.Withheld));
        var withheld = Single(await FindAsync(host, third.AlertId));
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Withheld, NhAssistantAlertWithholdReasons.LimitReached), (withheld.Status, withheld.ReasonCode));

        // Raised again the same day, it is planned again but the limit still holds.
        clock.Advance(TimeSpan.FromHours(1));
        await RaiseAsync(host, Immediate, "c");
        var again = await DispatchAsync(host);
        Assert.Equal((0, 1), (again.Sent, again.Withheld));
        Assert.DoesNotContain(Pushes(host).Sent, push => push.Push.AlertId == third.AlertId);

        // The next day it goes out.
        clock.Advance(TimeSpan.FromDays(1));
        await RaiseAsync(host, Immediate, "c");
        Assert.Equal(1, (await DispatchAsync(host)).Sent);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, Single(await FindAsync(host, third.AlertId)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Two_dispatchers_at_the_same_time_never_send_the_same_delivery_twice(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock);
        var raised = new List<NhAssistantAlertReceipt>();
        for (var index = 0; index < 30; index++)
        {
            raised.Add(await RaiseAsync(host, Immediate, "t" + index, recipients: [DeliveryWorld.Reader]));
        }

        var results = await Task.WhenAll(DispatchAsync(host), DispatchAsync(host));

        Assert.Equal(60, results.Sum(result => result.Sent));
        Assert.Equal(60, results.Sum(result => result.Claimed));
        var pushes = Pushes(host).Sent.Select(push => (push.Push.RecipientActorId, push.Push.AlertId)).ToArray();
        Assert.Equal(60, pushes.Length);
        Assert.Equal(60, pushes.Distinct().Count());
        Assert.Equal(60, Mail(host).Messages.Select(message => (message.RecipientActorId, message.Alerts[0].AlertId)).Distinct().Count());
        foreach (var alert in raised)
        {
            Assert.All((await FindAsync(host, alert.AlertId))!.Deliveries, delivery => Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, delivery.Status));
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_failing_channel_never_stops_the_others_and_a_delivery_whose_channels_all_failed_is_tried_again(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock);
        Pushes(host).Outcome = NhAssistantAlertChannelOutcome.Failed;
        Inbox(host).Fail = true;
        Mail(host).Fail = true;
        var raised = await RaiseAsync(host, Immediate, "a");

        // Push, in-app and e-mail failed and chat threw: the delivery waits to be tried again.
        var failed = await DispatchAsync(host);
        Assert.Equal((0, 1), (failed.Sent, failed.Released));
        var waiting = Single(await FindAsync(host, raised.AlertId));
        Assert.Equal(
            (NhAssistantAlertDeliveryStatuses.Pending, NhAssistantAlertDispatcher.ChannelsFailedCode, Monday + NhAssistantAlertDispatcher.RetryDelay, 1),
            (waiting.Status, waiting.ReasonCode, waiting.DueAt!.Value, waiting.Attempts));
        Assert.Equal(NhAssistantAlertDispatchResult.None, await DispatchAsync(host));

        // E-mail works again: the delivery goes out through it, whatever the other channels do.
        Mail(host).Fail = false;
        clock.Advance(NhAssistantAlertDispatcher.RetryDelay);
        Assert.Equal(1, (await DispatchAsync(host)).Sent);
        var sent = Single(await FindAsync(host, raised.AlertId));
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Sent, "e-mail", 2), (sent.Status, sent.Channels, sent.Attempts));
    }

    [Theory]
    [InlineData("2026-10-23T06:00:00Z", "07:30", "Europe/Amsterdam", "2026-10-26T06:30:00Z")]
    [InlineData("2026-10-23T05:00:00Z", "07:30", "Europe/Amsterdam", "2026-10-23T05:30:00Z")]
    [InlineData("2026-03-27T07:00:00Z", "07:30", "Europe/Amsterdam", "2026-03-30T05:30:00Z")]
    [InlineData("2026-10-30T12:00:00Z", "07:30", "America/New_York", "2026-11-02T12:30:00Z")]
    [InlineData("2026-03-06T13:00:00Z", "07:30", "America/New_York", "2026-03-09T11:30:00Z")]
    [InlineData("2026-11-02T06:30:00Z", "07:30", "Europe/Amsterdam", "2026-11-02T06:30:00Z")]
    public void A_0730_digest_on_working_days_stays_at_0730_where_the_person_is_across_daylight_saving_time(
        string now,
        string time,
        string zone,
        string expected)
    {
        var due = NhAssistantAlertDigestTimes.Next(
            DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture),
            TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture),
            workdaysOnly: true,
            TimeZoneInfo.FindSystemTimeZoneById(zone));

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), due);
        Assert.Equal(TimeSpan.Zero, due.Offset);
    }

    [Fact]
    public void A_digest_time_the_clock_skips_falls_after_the_gap_and_one_it_shows_twice_on_its_first_pass()
    {
        var amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

        // 29 March 2026 the clock jumps from 02:00 to 03:00; 25 October it goes back from 03:00 to 02:00.
        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero),
            NhAssistantAlertDigestTimes.Next(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), new TimeOnly(2, 30), false, amsterdam));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero),
            NhAssistantAlertDigestTimes.Next(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), new TimeOnly(2, 30), false, amsterdam));
        // Every day without WorkdaysOnly, also on Saturday.
        Assert.Equal(
            new DateTimeOffset(2026, 10, 31, 6, 30, 0, TimeSpan.Zero),
            NhAssistantAlertDigestTimes.Next(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), new TimeOnly(7, 30), false, amsterdam));
    }

    [Fact]
    public void The_digest_title_counts_per_urgency_in_english_and_dutch()
    {
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        var dutch = System.Globalization.CultureInfo.GetCultureInfo("nl-NL");
        NhAssistantAlertUrgency[] three = [NhAssistantAlertUrgency.Urgent, NhAssistantAlertUrgency.Info, NhAssistantAlertUrgency.Info];

        Assert.Equal("3 alerts, 1 urgent", NhAssistantAlertDigestTexts.Title(english, three));
        Assert.Equal("3 attenties, 1 urgent", NhAssistantAlertDigestTexts.Title(dutch, three));
        Assert.Equal("1 alert", NhAssistantAlertDigestTexts.Title(english, [NhAssistantAlertUrgency.Info]));
        Assert.Equal("2 attenties, 2 waarschuwingen", NhAssistantAlertDigestTexts.Title(dutch, [NhAssistantAlertUrgency.Warning, NhAssistantAlertUrgency.Warning]));
        Assert.Equal("and 4 more", NhAssistantAlertDigestTexts.More(english, 4));
        Assert.Equal("en nog 4", NhAssistantAlertDigestTexts.More(dutch, 4));
    }

    [Fact]
    public void The_options_refuse_a_time_zone_or_alert_list_the_host_cannot_use()
    {
        new NhAssistantAlertOptions().Validate();
        new NhAssistantAlertOptions { TimeZone = "Europe/Amsterdam", ListUrl = "/alerts" }.Validate();
        new NhAssistantAlertOptions { ListUrl = "https://app.example.com/alerts" }.Validate();
        Assert.Throws<InvalidOperationException>(() => new NhAssistantAlertOptions { TimeZone = "Europe/Atlantis" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new NhAssistantAlertOptions { ListUrl = "//attacker.example/alerts" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new NhAssistantAlertOptions { ListUrl = "javascript:alert(1)" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new NhAssistantAlertOptions { ListUrl = "http://app.example.com/alerts" }.Validate());
        Assert.False(NhAssistantAlertDispatcher.IsValidChannelId("web-push"));
        Assert.False(NhAssistantAlertDispatcher.IsValidChannelId("E-mail"));
        Assert.True(NhAssistantAlertDispatcher.IsValidChannelId("e-mail"));
    }

    internal static NhAiSubjectRef TaskOf(string id)
    {
        return new NhAiSubjectRef(DeliveryTestSubjectResolver.Type, "task-" + id);
    }

    private static (string Title, string Body) Texts((NhAssistantAlertPush Push, string Title, string Body) push)
    {
        return (push.Title, push.Body);
    }

    private static AssistantAlertDelivery Single(AssistantAlert? alert)
    {
        Assert.NotNull(alert);
        return Assert.Single(alert.Deliveries);
    }

    private static string? Code(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private static DeliveryPushes Pushes(AssistantTestHost host) => host.Services.GetRequiredService<DeliveryPushes>();

    private static DeliveryInbox Inbox(AssistantTestHost host) => host.Services.GetRequiredService<DeliveryInbox>();

    private static DeliveryMail Mail(AssistantTestHost host) => host.Services.GetRequiredService<DeliveryMail>();

    private static Task<AssistantAlert?> FindAsync(AssistantTestHost host, Guid alertId)
    {
        return host.Services.GetRequiredService<NhAssistantAlertStore>().FindAsync(alertId, CancellationToken.None);
    }

    private static async Task<NhAssistantAlertReceipt> RaiseAsync(
        AssistantTestHost host,
        string kindId,
        string task,
        NhAssistantAlertUrgency? urgency = null,
        IReadOnlyList<string>? recipients = null,
        DateTimeOffset? expiresAt = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var raised = await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RaiseAsync(new NhAssistantAlert(kindId, TaskOf(task))
        {
            Urgency = urgency,
            Parameters = new Dictionary<string, string> { ["task"] = "Task " + task, ["open"] = "2" },
            Recipients = recipients ?? [],
            ExpiresAt = expiresAt
        });
        Assert.True(raised.Success);
        return raised.Data!;
    }

    private static async Task<NhAssistantAlertDispatchResult> DispatchAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAlertDispatcher>().DispatchAsync(CancellationToken.None);
    }

    private Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        AssistantTestClock clock,
        Action<NhAssistantLimits>? limits = null)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            limits,
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<DeliveryWorld>();
                services.AddSingleton<DeliveryPushes>();
                services.AddSingleton<DeliveryInbox>();
                services.AddSingleton<DeliveryMail>();
                services.AddSingleton<INhUserNotificationSink>(provider => provider.GetRequiredService<DeliveryInbox>());
                services.AddSingleton<INhActorDirectory, DeliveryActorDirectory>();
                services.Replace(ServiceDescriptor.Scoped<INhAssistantNotifier, DeliveryNotifier>());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, DeliveryPrincipalFactory>());
                services.AddSingleton(Substitute.For<INhBackgroundOperationService>());
            },
            assistantBuilder: assistant => AddDeliveryKinds(assistant)
                .AddSubjectType<DeliveryTestSubjectResolver>()
                .UsePersonTimeZones<DeliveryTimeZones>()
                .AddAlertChannel<DeliveryMailChannel>()
                .AddAlertChannel<DeliveryChatChannel>()
                .ConfigureAlerts(alerts =>
                {
                    alerts.TimeZone = "Europe/Amsterdam";
                    alerts.ListUrl = "/alerts";
                }));
    }

    /// <summary>
    /// The kinds of the delivery tests: immediate, a working-day digest at 07:30, a digest that an urgent
    /// alert skips, a required one, one nobody changes and one that shows its text on a lock screen.
    /// </summary>
    internal static NhAssistantBuilder AddDeliveryKinds(NhAssistantBuilder assistant)
    {
        return assistant
            .AddAlertKind(Immediate, kind => Texts(kind.DisplayName("tests.alerts.immediate").Urgency(NhAssistantAlertUrgency.Warning)))
            .AddAlertKind(LockScreen, kind => Texts(kind.DisplayName("tests.alerts.lock-screen").PushShows(NhAssistantAlertPushContent.TitleAndText)))
            .AddAlertKind(Digest, kind => Texts(kind.DisplayName("tests.alerts.digest"))
                .Delivery(delivery => delivery.DailyDigest(new TimeOnly(7, 30)).WorkdaysOnly()))
            .AddAlertKind(UrgentFirst, kind => Texts(kind.DisplayName("tests.alerts.urgent-first"))
                .Delivery(delivery => delivery.DailyDigest(new TimeOnly(7, 30)).ImmediatelyFrom(NhAssistantAlertUrgency.Urgent)))
            .AddAlertKind(Required, kind => Texts(kind.DisplayName("tests.alerts.required").Urgency(NhAssistantAlertUrgency.Urgent))
                .Delivery(delivery => delivery.DailyDigest(new TimeOnly(7, 30)))
                .Required())
            .AddAlertKind(Fixed, kind => Texts(kind.DisplayName("tests.alerts.fixed"))
                .Delivery(delivery => delivery.DailyDigest(new TimeOnly(8, 0)))
                .PersonMayChange(NhAssistantAlertChoices.None));
    }

    private static NhAssistantAlertKindBuilder Texts(NhAssistantAlertKindBuilder kind)
    {
        return kind
            .Title((culture, parameters) => culture.TwoLetterISOLanguageName == "nl"
                ? $"{parameters["task"]} moet vandaag af"
                : $"{parameters["task"]} is due today")
            .Text((culture, parameters) => culture.TwoLetterISOLanguageName == "nl"
                ? $"Nog {parameters["open"]} taken open"
                : $"{parameters["open"]} tasks still open")
            .Recipients(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit));
    }
}

/// <summary>
/// The people and tasks of the delivery tests. Every task belongs to one division and its owner is
/// responsible; a person reads it only with a role in that division. People are users, so the in-app
/// notifications and the actor directory know them.
/// </summary>
public sealed class DeliveryWorld
{
    public static readonly Guid Division = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly Guid OtherDivision = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public const string Owner = "0b1c0000-0000-4000-8000-000000000001";
    public const string Reader = "0b1c0000-0000-4000-8000-000000000002";

    /// <summary>Reads Dutch.</summary>
    public const string Dutch = "0b1c0000-0000-4000-8000-000000000003";

    /// <summary>Lives in New York.</summary>
    public const string Traveller = "0b1c0000-0000-4000-8000-000000000004";
    public const string Gone = "0b1c0000-0000-4000-8000-000000000005";
    public const string Mover = "0b1c0000-0000-4000-8000-000000000006";

    public ConcurrentDictionary<string, (Guid Division, NhAiSubjectRole Role)> People { get; } = new(new Dictionary<string, (Guid, NhAiSubjectRole)>
    {
        [Owner] = (Division, NhAiSubjectRole.Participant),
        [Reader] = (Division, NhAiSubjectRole.Reader),
        [Dutch] = (Division, NhAiSubjectRole.Reader),
        [Traveller] = (Division, NhAiSubjectRole.Reader),
        [Gone] = (Division, NhAiSubjectRole.Reader),
        [Mover] = (Division, NhAiSubjectRole.Approver)
    });

    /// <summary>Accounts the application's principal factory no longer rebuilds.</summary>
    public ConcurrentDictionary<string, bool> Removed { get; } = new();

    /// <summary>Runs whenever the tasks' resolver is asked about a person, before it answers.</summary>
    public Func<NhAiSubjectRef, string, Task>? BeforeAccess { get; set; }
}

public sealed class DeliveryTestSubjectResolver(DeliveryWorld world) : INhAiSubjectAccessResolver
{
    public const string Type = "delivery-task";

    public string SubjectType => Type;

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, DeliveryWorld.Division))
        {
            DivisionId = DeliveryWorld.Division,
            DisplayName = "Task " + subject.Id,
            ResponsibleActorId = DeliveryWorld.Owner
        });
    }

    public async ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        if (world.BeforeAccess is { } before)
        {
            await before(subject, actorId);
        }

        return world.People.TryGetValue(actorId, out var person) && person.Division == DeliveryWorld.Division && person.Role > NhAiSubjectRole.None
            ? new NhAiSubjectAccess(person.Role)
            : NhAiSubjectAccess.None;
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (actorId, person) in world.People.Where(item => item.Value.Division == DeliveryWorld.Division))
        {
            await Task.Yield();
            yield return new NhAiSubjectMember(actorId, person.Role);
        }
    }
}

public sealed class DeliveryPrincipalFactory(DeliveryWorld world) : INhAiPrincipalFactory
{
    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<ClaimsPrincipal?>(world.Removed.ContainsKey(actorId)
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test")));
    }
}

/// <summary>
/// The application's actor directory: one person reads Dutch, everyone else English.
/// </summary>
public sealed class DeliveryActorDirectory : INhActorDirectory
{
    public Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(
        IReadOnlyCollection<NhActorRef> actors,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<NhActorRef, NhActorInfo> known = actors
            .Where(actor => actor.TryGetUserId(out _))
            .Distinct()
            .ToDictionary(actor => actor, actor => new NhActorInfo(actor, "Person " + actor.Id[^1])
            {
                Culture = actor.Id == DeliveryWorld.Dutch ? "nl-NL" : "en-GB"
            });
        return Task.FromResult(known);
    }
}

/// <summary>
/// The application's profiles: one person lives in New York, one has a zone this host does not know.
/// </summary>
public sealed class DeliveryTimeZones : INhAssistantPersonTimeZones
{
    public ValueTask<string?> FindTimeZoneIdAsync(string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(actorId switch
        {
            DeliveryWorld.Traveller => "America/New_York",
            DeliveryWorld.Reader => "Europe/Atlantis",
            _ => null
        });
    }
}

/// <summary>
/// The Web Push notifications of alerts, with their texts as an English browser receives them.
/// </summary>
internal sealed class DeliveryPushes
{
    public ConcurrentQueue<(NhAssistantAlertPush Push, string Title, string Body)> Sent { get; } = new();

    public NhAssistantAlertChannelOutcome Outcome { get; set; } = NhAssistantAlertChannelOutcome.Delivered;
}

internal sealed class DeliveryNotifier(DeliveryPushes pushes) : INhAssistantNotifier
{
    public Task<NhAssistantAlertChannelOutcome> AlertAsync(NhAssistantAlertPush alert, CancellationToken cancellationToken)
    {
        var (title, body) = alert.Texts("en");
        pushes.Sent.Enqueue((alert, title, body));
        return Task.FromResult(pushes.Outcome);
    }

    public Task TurnEndedAsync(NhAssistantTurnOutcome outcome, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task InvitedAsync(Guid conversationId, string inviteeActorId, string inviterDisplayName, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task AgentNotificationAsync(NhAssistantAgentNotificationContext context, IReadOnlyList<string> recipientActorIds, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task ApplicationMessageAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// The application's inbox: records every in-app notification, or refuses them all.
/// </summary>
public sealed class DeliveryInbox : INhUserNotificationSink
{
    public ConcurrentQueue<NhUserNotificationRequest> Requests { get; } = new();

    public bool Fail { get; set; }

    public Task<TaskResult<NhUserNotificationReceipt>> SendAsync(NhUserNotificationRequest request, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            return Task.FromResult(TaskResult<NhUserNotificationReceipt>.Failed("inbox-unavailable", "The inbox is unavailable."));
        }

        Requests.Enqueue(request);
        return Task.FromResult(TaskResult<NhUserNotificationReceipt>.Succeeded(new NhUserNotificationReceipt(Guid.NewGuid())));
    }
}

public sealed class DeliveryMail
{
    public ConcurrentQueue<NhAssistantAlertMessage> Messages { get; } = new();

    public bool Fail { get; set; }
}

/// <summary>
/// The application's e-mail channel of alerts.
/// </summary>
public sealed class DeliveryMailChannel(DeliveryMail mail) : INhAssistantAlertChannel
{
    public string Id => "e-mail";

    public Task<TaskResult> DeliverAsync(NhAssistantAlertMessage message, CancellationToken cancellationToken = default)
    {
        if (mail.Fail)
        {
            return Task.FromResult(TaskResult.Failed("mail-unavailable", "The mail server is unavailable."));
        }

        mail.Messages.Enqueue(message);
        return Task.FromResult(TaskResult.Succeeded());
    }
}

/// <summary>
/// An application channel that always throws.
/// </summary>
public sealed class DeliveryChatChannel : INhAssistantAlertChannel
{
    public string Id => "chat";

    public Task<TaskResult> DeliverAsync(NhAssistantAlertMessage message, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("The chat tool is unavailable.");
    }
}
