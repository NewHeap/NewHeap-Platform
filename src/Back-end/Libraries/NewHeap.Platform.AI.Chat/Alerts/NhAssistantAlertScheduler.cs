using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// The default of <see cref="INhAssistantPersonTimeZones"/>: the application knows nobody's own time
/// zone, so its time zone for alerts applies to everyone.
/// </summary>
internal sealed class NhAssistantNoPersonTimeZones : INhAssistantPersonTimeZones
{
    public ValueTask<string?> FindTimeZoneIdAsync(string actorId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(null);
    }
}

/// <summary>
/// The time zone of each person for alerts (FB87-4): the zone the application's
/// <see cref="INhAssistantPersonTimeZones"/> gives, otherwise the application's time zone for alerts
/// (<see cref="NhAssistantAlertOptions.TimeZone"/>, else the zone of <c>UseTimeZone</c>). A zone the host
/// does not know, or a lookup that throws, falls back to the application's zone and is logged without
/// the person. One scope asks the application once per person.
/// </summary>
internal sealed class NhAssistantAlertTimeZones(
    INhAssistantPersonTimeZones people,
    NhAssistantRegistrationState registration,
    IOptionsMonitor<NhAssistantOptions> options,
    ILogger<NhAssistantAlertTimeZones> logger)
{
    private readonly Dictionary<string, TimeZoneInfo> _known = new(StringComparer.Ordinal);

    /// <summary>The application's time zone for alerts.</summary>
    public TimeZoneInfo Default => options.CurrentValue.Alerts.TimeZoneOr(registration.TimeZone);

    /// <summary>
    /// The time zone of <paramref name="actorId"/>.
    /// </summary>
    public async ValueTask<TimeZoneInfo> OfAsync(string actorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (_known.TryGetValue(actorId, out var known))
        {
            return known;
        }

        string? id;
        try
        {
            id = await people.FindTimeZoneIdAsync(actorId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "The application's person time zones failed ({ExceptionType}); the application's time zone for alerts applies.",
                exception.GetType().Name);
            id = null;
        }

        var zone = Default;
        if (id is not null)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found))
            {
                zone = found;
            }
            else
            {
                logger.LogWarning("A person's time zone is unknown to this host; the application's time zone for alerts applies.");
            }
        }

        _known[actorId] = zone;
        return zone;
    }
}

/// <summary>
/// The moment of a daily digest in a time zone, across the changes to and from daylight saving time.
/// </summary>
internal static class NhAssistantAlertDigestTimes
{
    /// <summary>
    /// The first moment at or after <paramref name="now"/> when the clock in <paramref name="zone"/>
    /// shows <paramref name="time"/>, on a working day (Monday to Friday) when
    /// <paramref name="workdaysOnly"/>, in UTC. A time the clock skips when daylight saving time starts
    /// falls on the first minute after the gap; a time it shows twice when daylight saving time ends falls
    /// on its first pass.
    /// </summary>
    public static DateTimeOffset Next(DateTimeOffset now, TimeOnly time, bool workdaysOnly, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        // Today or one of the next seven days always has a working day at the time.
        for (var step = 0; step <= 7; step++, day = day.AddDays(1))
        {
            if (workdaysOnly && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            var moment = At(day, time, zone);
            if (moment >= now)
            {
                return moment;
            }
        }

        throw new InvalidOperationException("No digest moment was found within a week.");
    }

    /// <summary>
    /// The first moment at or after <paramref name="now"/> of the digests at <paramref name="times"/>, as
    /// <see cref="Next(DateTimeOffset, TimeOnly, bool, TimeZoneInfo)"/> plans one, outside
    /// <paramref name="quietHours"/>: a digest time in the quiet hours is skipped, so the delivery moves to
    /// the first digest time after they end; when every digest time is in them, it goes out at their end.
    /// </summary>
    public static DateTimeOffset Next(
        DateTimeOffset now,
        IReadOnlyList<TimeOnly> times,
        bool workdaysOnly,
        NhAssistantAlertQuietHours? quietHours,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times.Count == 0)
        {
            throw new ArgumentException("A digest delivery has at least one digest time.", nameof(times));
        }

        IReadOnlyList<TimeOnly> candidates = quietHours is null ? times : times.Where(time => !quietHours.Contains(time)).ToArray();
        if (candidates.Count == 0)
        {
            candidates = times;
        }

        // A time the clock skips falls after the gap, which may lie in the quiet hours: that one waits too.
        return candidates
            .Select(time => Next(now, time, workdaysOnly, zone))
            .Select(moment => quietHours is null ? moment : OutsideQuietHours(moment, quietHours, zone))
            .Min();
    }

    /// <summary>
    /// <paramref name="moment"/> when the clock in <paramref name="zone"/> shows a time outside
    /// <paramref name="quietHours"/>; otherwise the end of the quiet hours it falls in, in UTC.
    /// </summary>
    public static DateTimeOffset OutsideQuietHours(DateTimeOffset moment, NhAssistantAlertQuietHours quietHours, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(quietHours);
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(moment, zone).DateTime;
        var time = TimeOnly.FromDateTime(local);
        if (!quietHours.Contains(time))
        {
            return moment;
        }

        // Quiet hours across midnight that began today end tomorrow; all others end today.
        var day = DateOnly.FromDateTime(local);
        var end = At(time < quietHours.End ? day : day.AddDays(1), quietHours.End, zone);

        // Around a change of daylight saving time the end's first pass may lie before the moment.
        return end > moment ? end : moment;
    }

    /// <summary>
    /// The moment, in UTC, when the clock in <paramref name="zone"/> shows <paramref name="time"/> on
    /// <paramref name="day"/>.
    /// </summary>
    public static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);

        // A gap is at most a few hours; the bound only guards against a broken zone.
        for (var minute = 0; zone.IsInvalidTime(local) && minute < 24 * 60; minute++)
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

/// <summary>
/// Plans the deliveries of alerts (FB87, lanes AB and AT) in place of the immediate default: an alert of
/// a kind delivered immediately, or of an urgency from which its kind delivers immediately, is due at
/// once; any other is due at the next of the day's digest times, the person's own where the kind lets
/// them choose, otherwise the kind's, on working days only when the kind says so, in the person's time
/// zone. Nothing is due in quiet hours, the person's own where the kind lets them choose, otherwise the
/// kind's: a digest moves to the first digest time after they end, and a delivery due at once to their
/// end, unless the alert is urgent and its kind lets urgent alerts through
/// (<see cref="NhAssistantAlertKind.UrgentBreaksQuietHours"/>). The kind's delivery is its effective one
/// (<see cref="NhAssistantAlertKindSettings"/>), with an administrator's settings. A later change of the
/// digest times or quiet hours applies to the deliveries planned after it.
/// </summary>
internal sealed class NhAssistantAlertDigestScheduler(
    NhAssistantNotificationStore notifications,
    NhAssistantAlertTimeZones timeZones,
    NhAssistantAlertKindSettings kindSettings) : INhAssistantAlertScheduler
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, Entities.AssistantNotificationPreference>> _preferences =
        new(StringComparer.Ordinal);

    public async ValueTask<DateTimeOffset> DueAtAsync(NhAssistantAlertScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = request.Kind;
        var delivery = (await kindSettings.GetAsync(kind, cancellationToken)).Delivery;
        var immediate = delivery.IsImmediateFor(request.Urgency) || delivery.DigestTime is null;
        var breaksQuietHours = request.Urgency == NhAssistantAlertUrgency.Urgent && kind.UrgentBreaksQuietHours;
        if (immediate && (breaksQuietHours || (delivery.QuietHours is null && !NhAssistantAlertChoice.MayChangeQuietHours(kind))))
        {
            // Nothing can hold it back, so the person's choices and time zone are not read.
            return request.Now;
        }

        if (!_preferences.TryGetValue(request.RecipientActorId, out var preferences))
        {
            preferences = await notifications.GetPreferencesAsync(
                request.RecipientActorId,
                NhAssistantNotificationPreferenceCategories.Alert,
                cancellationToken);
            _preferences[request.RecipientActorId] = preferences;
        }

        var choice = NhAssistantAlertChoice.Of(kind, delivery, preferences.GetValueOrDefault(kind.Id));
        var quietHours = breaksQuietHours ? null : choice.QuietHours;
        if (immediate && quietHours is null)
        {
            return request.Now;
        }

        var zone = await timeZones.OfAsync(request.RecipientActorId, cancellationToken);
        return immediate
            ? NhAssistantAlertDigestTimes.OutsideQuietHours(request.Now, quietHours!, zone)
            : NhAssistantAlertDigestTimes.Next(request.Now, choice.DigestTimes, delivery.WorkdaysOnly, quietHours, zone);
    }
}
