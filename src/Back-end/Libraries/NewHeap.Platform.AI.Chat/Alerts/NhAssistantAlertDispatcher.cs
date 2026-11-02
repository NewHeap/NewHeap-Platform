using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Actors;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// One delivery about to go out, with what applies to its person and its texts in their culture.
/// </summary>
internal sealed record NhAssistantAlertDispatchItem(
    AssistantAlertDelivery Delivery,
    NhAssistantAlertKind Kind,
    NhAssistantAlertChoice Choice,
    NhAssistantAlertText Text)
{
    public AssistantAlert Alert => Delivery.Alert!;
}

/// <summary>
/// What one pass of the delivery did, by delivery: claimed, sent, withheld (muted, over the daily limit
/// or without a text), dropped because the person no longer qualified, and released to be tried again;
/// and how many notifications went out.
/// </summary>
internal sealed record NhAssistantAlertDispatchResult(
    int Claimed,
    int Sent,
    int Withheld,
    int Dropped,
    int Released,
    int Notifications)
{
    public static NhAssistantAlertDispatchResult None { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The delivery of alerts (FB87 phase 1, lane AB), run by the run maintenance in a host with alert kinds.
/// It claims the deliveries that are due in bounded batches, so several instances never send one twice,
/// and for each person checks every recipient again right before sending: a person who no longer
/// qualifies is dropped with the content-free audit <c>AgentAlertRecipientDropped</c>. A person who
/// muted a kind that is not required, or who reached <see cref="NhAssistantLimits.MaxAlertsPerPersonPerDay"/>,
/// gets nothing and the delivery is withheld; the alert stays in their list. An alert delivered
/// immediately goes out on its own; every other delivery of a person due at the same moment goes out
/// as one digest, whose title counts its alerts per urgency and which lists at most
/// <see cref="NhAssistantLimits.MaxAlertDigestLines"/> of them. A notification goes out through Web Push
/// (the title only, unless the kind shows its text), the in-app notifications as one thread per digest,
/// and the application's <see cref="INhAssistantAlertChannel"/>s; a person chooses Web Push and in-app
/// per kind where the kind allows it. A channel that fails never stops the others; when every channel
/// that tried failed, the deliveries are tried again later, at most <see cref="MaxAttempts"/> times.
/// Right before a notification goes out the claims are read again, so a delivery read, resolved or
/// expired meanwhile never goes out. Every time is on the application's clock.
/// </summary>
internal sealed class NhAssistantAlertDispatcher(
    NhAssistantRegistrationState registration,
    NhAssistantAlertStore store,
    NhAssistantAlertRecipientCheck recipientCheck,
    NhAssistantNotificationStore notifications,
    NhAssistantAlertTexts texts,
    NhAssistantAgentEvents events,
    INhAssistantNotifier notifier,
    IEnumerable<INhAssistantAlertChannel> channels,
    IOptionsMonitor<NhAssistantOptions> options,
    IServiceProvider services,
    ILogger<NhAssistantAlertDispatcher> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>The most deliveries one claim takes.</summary>
    internal const int BatchSize = 100;

    /// <summary>The most claims one pass makes, so a backlog spreads over passes.</summary>
    internal const int MaxBatches = 10;

    /// <summary>How often a delivery is tried when every channel fails, before it counts as sent to the alert list only.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>The in-app category of notifications of alerts.</summary>
    internal const string InAppCategory = "assistant-alert";

    /// <summary>The channel code of Web Push on a delivery.</summary>
    internal const string WebPushChannel = "web-push";

    /// <summary>The channel code of the in-app notifications on a delivery.</summary>
    internal const string InAppChannel = "in-app";

    /// <summary>The code of a delivery released because every channel that tried failed.</summary>
    internal const string ChannelsFailedCode = "assistant-alert-channels-failed";

    /// <summary>How long a claim holds; another instance takes the delivery over after it.</summary>
    internal static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(5);

    /// <summary>How long a delivery whose channels all failed waits before it is tried again.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);

    private const int MaxChannelsLength = 256;

    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Sends what is due now. Returns what the pass did.
    /// </summary>
    public async Task<NhAssistantAlertDispatchResult> DispatchAsync(CancellationToken cancellationToken)
    {
        var now = _applicationClock.GetUtcNow();
        var claimant = NewClaimant();
        var claimed = new List<AssistantAlertDelivery>();
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            var next = await store.ClaimDueAsync(now, claimant, now + ClaimDuration, BatchSize, cancellationToken);
            claimed.AddRange(next);
            if (next.Count < BatchSize)
            {
                break;
            }
        }
        if (claimed.Count == 0)
        {
            return NhAssistantAlertDispatchResult.None;
        }

        var recipients = claimed.Select(delivery => delivery.RecipientActorId).Distinct(StringComparer.Ordinal).ToArray();
        var preferences = await notifications.GetPreferencesAsync(recipients, NhAssistantNotificationPreferenceCategories.Alert, cancellationToken);
        var sentToday = await store.CountSentSinceAsync(recipients, new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), cancellationToken);
        var cultures = await CulturesAsync(recipients, cancellationToken);
        var pass = new Pass(claimant, now) { Claimed = claimed.Count };

        foreach (var person in claimed.GroupBy(delivery => delivery.RecipientActorId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            try
            {
                await DispatchPersonAsync(
                    pass,
                    person.Key,
                    person.OrderBy(delivery => delivery.DueAt).ThenBy(delivery => delivery.Id).ToArray(),
                    preferences,
                    sentToday.GetValueOrDefault(person.Key),
                    cultures.GetValueOrDefault(person.Key),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // The person's claims run out and another pass tries them again; the others go on.
                logger.LogWarning(
                    "The delivery of {Count} alerts to one person failed ({ExceptionType}); they are tried again once their claims run out.",
                    person.Count(),
                    exception.GetType().Name);
            }
        }

        var result = new NhAssistantAlertDispatchResult(pass.Claimed, pass.Sent, pass.Withheld, pass.Dropped, pass.Released, pass.Notifications);
        logger.LogInformation(
            "Assistant alert delivery claimed {Claimed}, sent {Sent} in {Notifications} notifications, withheld {Withheld}, dropped {Dropped} and released {Released} deliveries.",
            result.Claimed,
            result.Sent,
            result.Notifications,
            result.Withheld,
            result.Dropped,
            result.Released);
        return result;
    }

    private async Task DispatchPersonAsync(
        Pass pass,
        string actorId,
        IReadOnlyList<AssistantAlertDelivery> deliveries,
        IReadOnlyDictionary<(string ActorId, string KindId), AssistantNotificationPreference> preferences,
        int sentToday,
        CultureInfo? knownCulture,
        CancellationToken cancellationToken)
    {
        var culture = knownCulture ?? CultureInfo.CurrentUICulture;
        var limit = registration.Limits.MaxAlertsPerPersonPerDay;
        var items = new List<NhAssistantAlertDispatchItem>();
        foreach (var delivery in deliveries)
        {
            var alert = delivery.Alert!;
            if (alert.ExpiresAt is { } expiresAt && expiresAt <= pass.Now)
            {
                // Expired before the maintenance closed it: never sent; closing it takes the delivery back.
                continue;
            }
            if (!registration.AlertKinds.TryGetValue(alert.KindId, out var kind))
            {
                await WithholdAsync(pass, delivery, NhAssistantAlertWithholdReasons.KindUnknown, cancellationToken);
                continue;
            }

            // The one check of a person against an alert, right before anything reaches them.
            var verdict = await recipientCheck.CheckAsync(actorId, alert, cancellationToken);
            if (!verdict.Qualifies)
            {
                if (await store.DropAsync(delivery.Id, verdict.DropReason!, pass.Now, cancellationToken))
                {
                    pass.Dropped++;
                    await events.AlertRecipientDroppedAsync(alert, actorId, verdict.DropReason!, cancellationToken);
                }
                continue;
            }

            var choice = NhAssistantAlertChoice.Of(kind, preferences.GetValueOrDefault((actorId, kind.Id)));
            if (choice.Muted)
            {
                await WithholdAsync(pass, delivery, NhAssistantAlertWithholdReasons.Muted, cancellationToken);
                continue;
            }
            if (sentToday + items.Count >= limit)
            {
                await WithholdAsync(pass, delivery, NhAssistantAlertWithholdReasons.LimitReached, cancellationToken);
                continue;
            }

            var text = texts.Render(kind, alert.ParametersJson, culture);
            if (text is null)
            {
                await WithholdAsync(pass, delivery, NhAssistantAlertWithholdReasons.TextInvalid, cancellationToken);
                continue;
            }

            items.Add(new NhAssistantAlertDispatchItem(delivery, kind, choice, text));
        }

        // An alert delivered immediately goes out on its own; the rest go out as one digest per moment.
        foreach (var item in items.Where(item => item.Kind.Delivery.IsImmediateFor(item.Alert.Urgency)))
        {
            await SendAsync(pass, actorId, culture, knownCulture is not null, [item], null, cancellationToken);
        }
        foreach (var digest in items
                     .Where(item => !item.Kind.Delivery.IsImmediateFor(item.Alert.Urgency))
                     .GroupBy(item => item.Delivery.DueAt)
                     .OrderBy(group => group.Key))
        {
            await SendAsync(pass, actorId, culture, knownCulture is not null, digest.ToArray(), Guid.NewGuid(), cancellationToken);
        }
    }

    /// <summary>
    /// Sends one notification of <paramref name="items"/>: a single alert, or the digest
    /// <paramref name="digestId"/>. Records every delivery sent with the channels that carried it, or
    /// releases them all when every channel that tried failed.
    /// </summary>
    private async Task SendAsync(
        Pass pass,
        string actorId,
        CultureInfo culture,
        bool cultureKnown,
        IReadOnlyList<NhAssistantAlertDispatchItem> items,
        Guid? digestId,
        CancellationToken cancellationToken)
    {
        // Read, resolved or expired since the claim: never sent.
        var held = await store.FindStillClaimedAsync(items.Select(item => item.Delivery.Id).ToArray(), pass.Claimant, cancellationToken);
        var ordered = items
            .Where(item => held.Contains(item.Delivery.Id))
            .OrderByDescending(item => item.Alert.Urgency)
            .ThenBy(item => item.Alert.RaisedAt)
            .ThenBy(item => item.Alert.Id)
            .ToArray();
        if (ordered.Length == 0)
        {
            return;
        }

        var carriedBy = ordered.ToDictionary(item => item.Delivery.Id, _ => new List<string>());
        var tried = 0;
        var delivered = 0;
        void Record(NhAssistantAlertChannelOutcome outcome, string channel, IEnumerable<NhAssistantAlertDispatchItem> carried)
        {
            if (outcome == NhAssistantAlertChannelOutcome.Skipped)
            {
                return;
            }

            tried++;
            if (outcome != NhAssistantAlertChannelOutcome.Delivered)
            {
                return;
            }

            delivered++;
            foreach (var item in carried)
            {
                carriedBy[item.Delivery.Id].Add(channel);
            }
        }

        var pushed = ordered.Where(item => item.Choice.Push).ToArray();
        if (pushed.Length > 0)
        {
            Record(await PushAsync(actorId, culture, cultureKnown, pushed, digestId, cancellationToken), WebPushChannel, pushed);
        }

        var inApp = ordered.Where(item => item.Choice.InApp).ToArray();
        if (inApp.Length > 0)
        {
            Record(await InAppAsync(actorId, culture, inApp, digestId, cancellationToken), InAppChannel, inApp);
        }

        var message = MessageOf(actorId, culture, ordered, digestId);
        foreach (var channel in channels)
        {
            Record(await ApplicationChannelAsync(channel, message, cancellationToken), channel.Id, ordered);
        }

        if (tried > 0 && delivered == 0 && ordered.Max(item => item.Delivery.Attempts) < MaxAttempts)
        {
            var retryAt = pass.Now + RetryDelay;
            foreach (var item in ordered)
            {
                if (await store.ReleaseAsync(item.Delivery.Id, pass.Claimant, retryAt, ChannelsFailedCode, pass.Now, cancellationToken))
                {
                    pass.Released++;
                }
            }
            return;
        }

        var sent = 0;
        foreach (var item in ordered)
        {
            if (await store.MarkSentAsync(
                    item.Delivery.Id,
                    pass.Claimant,
                    item.Alert.Revision,
                    ChannelsOf(carriedBy[item.Delivery.Id]),
                    digestId,
                    pass.Now,
                    cancellationToken))
            {
                sent++;
            }
        }

        pass.Sent += sent;
        pass.Notifications++;
    }

    private async Task<NhAssistantAlertChannelOutcome> PushAsync(
        string actorId,
        CultureInfo culture,
        bool cultureKnown,
        IReadOnlyList<NhAssistantAlertDispatchItem> items,
        Guid? digestId,
        CancellationToken cancellationToken)
    {
        // A browser reads the person's culture when the application knows it, otherwise its own language.
        var written = new ConcurrentDictionary<string, (string Title, string Body)>(StringComparer.OrdinalIgnoreCase);
        (string Title, string Body) TextsFor(string language)
        {
            var target = cultureKnown ? culture : CultureOf(language) ?? culture;
            return written.GetOrAdd(target.Name, _ =>
            {
                var inTarget = items.Select(item => target.Name == culture.Name
                        ? item.Text
                        : texts.Render(item.Kind, item.Alert.ParametersJson, target) ?? item.Text)
                    .ToArray();
                return PushTexts(target, items, inTarget, digestId);
            });
        }

        return await notifier.AlertAsync(
            new NhAssistantAlertPush(
                actorId,
                digestId is null ? items[0].Alert.Id : null,
                digestId,
                items.Any(item => item.Alert.Urgency == NhAssistantAlertUrgency.Urgent),
                TextsFor),
            cancellationToken);
    }

    /// <summary>
    /// The title and body of a push: a single alert's title, with its text only when its kind shows it
    /// on a lock screen; or a digest's counts with the titles of its alerts.
    /// </summary>
    private (string Title, string Body) PushTexts(
        CultureInfo culture,
        IReadOnlyList<NhAssistantAlertDispatchItem> items,
        IReadOnlyList<NhAssistantAlertText> written,
        Guid? digestId)
    {
        if (digestId is null)
        {
            var single = written[0];
            var body = items[0].Kind.PushShows == NhAssistantAlertPushContent.TitleAndText ? single.Text ?? string.Empty : string.Empty;
            return (single.Title, body);
        }

        var listed = Math.Min(items.Count, registration.Limits.MaxAlertDigestLines);
        return (
            NhAssistantAlertDigestTexts.Title(culture, items.Select(item => item.Alert.Urgency).ToArray()),
            NhAssistantAlertDigestTexts.Lines(culture, written.Take(listed).Select(text => text.Title), items.Count - listed));
    }

    private async Task<NhAssistantAlertChannelOutcome> InAppAsync(
        string actorId,
        CultureInfo culture,
        IReadOnlyList<NhAssistantAlertDispatchItem> items,
        Guid? digestId,
        CancellationToken cancellationToken)
    {
        var sink = services.GetService<INhUserNotificationSink>();
        if (sink is null || !Guid.TryParse(actorId, out var userId))
        {
            return NhAssistantAlertChannelOutcome.Skipped;
        }

        var message = MessageOf(actorId, culture, items, digestId);
        var body = digestId is null
            ? items[0].Text.Text ?? items[0].Text.Title
            : NhAssistantAlertDigestTexts.Lines(culture, message.Alerts.Select(item => item.Title), message.MoreCount);
        try
        {
            var result = await sink.SendAsync(
                new NhUserNotificationRequest(userId, message.Title, body)
                {
                    Url = message.ListUrl,
                    Category = InAppCategory,
                    Level = LevelOf(message.Urgency),
                    // One thread per digest; an alert on its own keeps its thread when it is delivered again.
                    GroupKey = digestId is { } digest
                        ? $"{InAppCategory}:digest:{digest:N}"
                        : $"{InAppCategory}:{items[0].Alert.Id:N}"
                },
                cancellationToken);
            if (!result.Success)
            {
                logger.LogWarning(
                    "An in-app notification of alerts was refused by the notification sink ({Code}).",
                    NhAssistantResults.CodeOf(result) ?? "unknown");
                return NhAssistantAlertChannelOutcome.Failed;
            }

            return result.Data is null ? NhAssistantAlertChannelOutcome.Skipped : NhAssistantAlertChannelOutcome.Delivered;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "An in-app notification of alerts was not delivered ({ExceptionType}).",
                exception.GetType().Name);
            return NhAssistantAlertChannelOutcome.Failed;
        }
    }

    private async Task<NhAssistantAlertChannelOutcome> ApplicationChannelAsync(
        INhAssistantAlertChannel channel,
        NhAssistantAlertMessage message,
        CancellationToken cancellationToken)
    {
        string id;
        try
        {
            id = channel.Id;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("An alert channel {ChannelType} has no id ({ExceptionType}).", channel.GetType().Name, exception.GetType().Name);
            return NhAssistantAlertChannelOutcome.Skipped;
        }
        if (!IsValidChannelId(id))
        {
            logger.LogWarning("Alert channel {ChannelType} has an invalid id and delivers nothing.", channel.GetType().Name);
            return NhAssistantAlertChannelOutcome.Skipped;
        }

        try
        {
            var result = await channel.DeliverAsync(message, cancellationToken);
            if (result.Success)
            {
                return NhAssistantAlertChannelOutcome.Delivered;
            }

            logger.LogWarning(
                "Alert channel {ChannelId} did not deliver a notification of alerts ({Code}).",
                id,
                NhAssistantResults.CodeOf(result) ?? "unknown");
            return NhAssistantAlertChannelOutcome.Failed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // The application's channel may put content into its exception messages; log the type only.
            logger.LogWarning(
                "Alert channel {ChannelId} failed ({ExceptionType}).",
                id,
                exception.GetType().Name);
            return NhAssistantAlertChannelOutcome.Failed;
        }
    }

    /// <summary>
    /// The notification of <paramref name="items"/> as the application's channels receive it.
    /// </summary>
    private NhAssistantAlertMessage MessageOf(
        string actorId,
        CultureInfo culture,
        IReadOnlyList<NhAssistantAlertDispatchItem> items,
        Guid? digestId)
    {
        var listed = digestId is null ? 1 : Math.Min(items.Count, registration.Limits.MaxAlertDigestLines);
        var title = digestId is null
            ? items[0].Text.Title
            : NhAssistantAlertDigestTexts.Title(culture, items.Select(item => item.Alert.Urgency).ToArray());
        return new NhAssistantAlertMessage(
            actorId,
            digestId,
            culture,
            title,
            items.Max(item => item.Alert.Urgency),
            items.Take(listed)
                .Select(item => new NhAssistantAlertMessageItem(
                    item.Alert.Id,
                    item.Alert.KindId,
                    item.Alert.Urgency,
                    new NhAiSubjectRef(item.Alert.SubjectType, item.Alert.SubjectId),
                    item.Alert.SubjectDisplayName,
                    item.Text.Title,
                    item.Text.Text))
                .ToArray(),
            items.Count - listed)
        {
            ListUrl = options.CurrentValue.Alerts.ListUrl
        };
    }

    private async Task WithholdAsync(Pass pass, AssistantAlertDelivery delivery, string reason, CancellationToken cancellationToken)
    {
        if (await store.WithholdAsync(delivery.Id, pass.Claimant, reason, pass.Now, cancellationToken))
        {
            pass.Withheld++;
        }
    }

    /// <summary>
    /// The cultures the application's actor directory knows of the people, by actor id.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, CultureInfo>> CulturesAsync(
        IReadOnlyCollection<string> actorIds,
        CancellationToken cancellationToken)
    {
        var users = new Dictionary<NhActorRef, string>();
        foreach (var actorId in actorIds)
        {
            if (Guid.TryParse(actorId, out var userId))
            {
                users[NhActorRef.ForUser(userId)] = actorId;
            }
        }

        var cultures = new Dictionary<string, CultureInfo>(StringComparer.Ordinal);
        if (users.Count == 0)
        {
            return cultures;
        }

        foreach (var (actor, name) in await NhAssistantNotificationCultures.GetAsync(services, users.Keys, cancellationToken))
        {
            if (name is not null && CultureOf(name) is { } culture && users.TryGetValue(actor, out var actorId))
            {
                cultures[actorId] = culture;
            }
        }

        return cultures;
    }

    private static CultureInfo? CultureOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="id"/> can name an application's channel on a delivery.
    /// </summary>
    internal static bool IsValidChannelId(string? id)
    {
        return id is { Length: > 0 and <= 32 }
            && NhAiSubjectRef.IsValidType(id)
            && id is not (WebPushChannel or InAppChannel);
    }

    private static string? ChannelsOf(IReadOnlyCollection<string> carried)
    {
        if (carried.Count == 0)
        {
            return null;
        }

        var joined = new List<string>();
        var length = 0;
        foreach (var channel in carried.Distinct(StringComparer.Ordinal))
        {
            length += channel.Length + (joined.Count > 0 ? 1 : 0);
            if (length > MaxChannelsLength)
            {
                break;
            }

            joined.Add(channel);
        }

        return string.Join(',', joined);
    }

    private static NhUserNotificationLevel LevelOf(NhAssistantAlertUrgency urgency)
    {
        return urgency switch
        {
            NhAssistantAlertUrgency.Urgent => NhUserNotificationLevel.Error,
            NhAssistantAlertUrgency.Warning => NhUserNotificationLevel.Warning,
            _ => NhUserNotificationLevel.Information
        };
    }

    /// <summary>
    /// The claimant of one pass: the instance and the pass, at most 128 characters.
    /// </summary>
    private static string NewClaimant()
    {
        var machine = Environment.MachineName.Length > 64 ? Environment.MachineName[..64] : Environment.MachineName;
        return $"{machine}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }

    /// <summary>
    /// The state of one pass.
    /// </summary>
    private sealed class Pass(string claimant, DateTimeOffset now)
    {
        public string Claimant { get; } = claimant;

        public DateTimeOffset Now { get; } = now;

        public int Claimed { get; init; }

        public int Sent { get; set; }

        public int Withheld { get; set; }

        public int Dropped { get; set; }

        public int Released { get; set; }

        public int Notifications { get; set; }
    }
}
