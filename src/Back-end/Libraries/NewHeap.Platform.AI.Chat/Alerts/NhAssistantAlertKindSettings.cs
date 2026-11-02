using System.Globalization;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// Quiet hours of a kind's delivery, in a person's time zone: from <see cref="Start"/> up to
/// <see cref="End"/>, across midnight when the end comes before the start.
/// </summary>
internal sealed record NhAssistantAlertQuietHours(TimeOnly Start, TimeOnly End)
{
    /// <summary>Whether the clock showing <paramref name="time"/> is within the quiet hours.</summary>
    public bool Contains(TimeOnly time)
    {
        return Start <= End
            ? time >= Start && time < End
            : time >= Start || time < End;
    }
}

/// <summary>
/// The delivery that applies to a kind: the code's (<see cref="NhAssistantAlertKind.Delivery"/>) merged
/// with an administrator's settings where the code lets an administrator change them. Several digest
/// times and quiet hours are carried for the scheduler that plans them; <see cref="DigestTime"/> is the
/// first digest time of the day.
/// </summary>
/// <param name="Mode">Immediately or in digests.</param>
/// <param name="DigestTimes">The times of day of the digests in ascending order, at most
/// <see cref="AssistantAlertKindSetting.MaxDigestTimes"/>; empty for a kind delivered immediately.</param>
/// <param name="WorkdaysOnly">The digests go out on working days only.</param>
/// <param name="ImmediatelyFrom">An alert of this urgency or higher is delivered immediately instead of in a digest.</param>
/// <param name="QuietHours">When nothing of the kind is delivered; <see langword="null"/> for none.</param>
internal sealed record NhAssistantAlertEffectiveDelivery(
    NhAssistantAlertDeliveryMode Mode,
    IReadOnlyList<TimeOnly> DigestTimes,
    bool WorkdaysOnly,
    NhAssistantAlertUrgency? ImmediatelyFrom,
    NhAssistantAlertQuietHours? QuietHours)
{
    /// <summary>The first digest time of the day; <see langword="null"/> for a kind delivered immediately.</summary>
    public TimeOnly? DigestTime => Mode == NhAssistantAlertDeliveryMode.DailyDigest && DigestTimes.Count > 0 ? DigestTimes[0] : null;

    /// <summary>Whether an alert of <paramref name="urgency"/> is delivered immediately under this delivery.</summary>
    public bool IsImmediateFor(NhAssistantAlertUrgency urgency)
    {
        return Mode == NhAssistantAlertDeliveryMode.Immediately
            || DigestTimes.Count == 0
            || (ImmediatelyFrom is { } from && urgency >= from);
    }

    /// <summary>The delivery the code declares.</summary>
    public static NhAssistantAlertEffectiveDelivery Of(NhAssistantAlertDelivery code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.Mode == NhAssistantAlertDeliveryMode.DailyDigest && code.DigestTime is { } time
            ? new NhAssistantAlertEffectiveDelivery(NhAssistantAlertDeliveryMode.DailyDigest, [time], code.WorkdaysOnly, code.ImmediatelyFrom, null)
            : Immediate(null);
    }

    /// <summary>A delivery as soon as an alert is raised, with <paramref name="quietHours"/>.</summary>
    public static NhAssistantAlertEffectiveDelivery Immediate(NhAssistantAlertQuietHours? quietHours)
    {
        return new NhAssistantAlertEffectiveDelivery(NhAssistantAlertDeliveryMode.Immediately, [], false, null, quietHours);
    }
}

/// <summary>
/// One kind as it applies now: whether it is on, its effective delivery and the administrator's settings
/// it came from, if any.
/// </summary>
/// <param name="Kind">The kind as its code declares it.</param>
/// <param name="Enabled">The kind raises alerts: it is not switched off, or its code does not let an
/// administrator switch it off.</param>
/// <param name="Delivery">The kind's effective delivery.</param>
/// <param name="Stored">The administrator's settings; <see langword="null"/> when nobody changed the kind.</param>
internal sealed record NhAssistantAlertKindState(
    NhAssistantAlertKind Kind,
    bool Enabled,
    NhAssistantAlertEffectiveDelivery Delivery,
    AssistantAlertKindSetting? Stored);

/// <summary>
/// The one source of what applies to an alert kind (FB87 phase 2): its code merged with an administrator's
/// settings within the bounds the code sets (<see cref="NhAssistantAlertKind.AdministratorChoices"/>).
/// Raising, the scheduler, the delivery and a person's preferences read a kind's state and effective
/// delivery here, never the code's delivery directly. A setting the code does not allow (any more) has no
/// effect: a required kind is always on, and a part of the delivery the code keeps is the code's. One
/// scope reads the settings once; a host without alert kinds reads no table.
/// </summary>
internal sealed class NhAssistantAlertKindSettings(
    NhAssistantRegistrationState registration,
    NhAssistantAlertKindSettingStore store)
{
    private IReadOnlyDictionary<string, NhAssistantAlertKindState>? _states;

    /// <summary>
    /// The state of <paramref name="kind"/>, a registered kind.
    /// </summary>
    public async ValueTask<NhAssistantAlertKindState> GetAsync(NhAssistantAlertKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var states = await GetAllAsync(cancellationToken);
        return states.TryGetValue(kind.Id, out var state) && ReferenceEquals(state.Kind, kind)
            ? state
            : Merge(kind, null);
    }

    /// <summary>
    /// The state of every registered kind, by id.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<string, NhAssistantAlertKindState>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (_states is not null)
        {
            return _states;
        }

        var kinds = registration.AlertKinds;
        var stored = kinds.Count == 0
            ? new Dictionary<string, AssistantAlertKindSetting>(StringComparer.Ordinal)
            : await store.GetAsync(kinds.Keys.ToArray(), cancellationToken);
        _states = kinds.Values.ToDictionary(
            kind => kind.Id,
            kind => Merge(kind, stored.GetValueOrDefault(kind.Id)),
            StringComparer.Ordinal);
        return _states;
    }

    /// <summary>
    /// Forgets what this scope read, after a change of the settings.
    /// </summary>
    public void Forget()
    {
        _states = null;
    }

    /// <summary>
    /// What applies to <paramref name="kind"/> with <paramref name="stored"/>, its administrator's settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">The stored digest times are not a list of times of day,
    /// which the administration never writes.</exception>
    public static NhAssistantAlertKindState Merge(NhAssistantAlertKind kind, AssistantAlertKindSetting? stored)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var code = NhAssistantAlertEffectiveDelivery.Of(kind.Delivery);
        var enabled = stored?.Enabled != false || !MaySwitchOff(kind);
        if (stored?.DeliveryMode is null || !NhAssistantAlertKindSettingModes.TryParse(stored.DeliveryMode, out var mode))
        {
            return new NhAssistantAlertKindState(kind, enabled, code, stored);
        }

        var allowed = kind.AdministratorChoices;
        var times = mode == NhAssistantAlertDeliveryMode.DailyDigest ? ParseTimes(kind.Id, stored.DigestTimes) : [];
        var quietHours = stored.QuietHoursStart is { } start && stored.QuietHoursEnd is { } end
            ? new NhAssistantAlertQuietHours(start, end)
            : null;
        if (!allowed.HasFlag(NhAssistantAlertAdminChoices.DigestTimes))
        {
            (mode, times) = (code.Mode, code.DigestTimes);
        }

        var workdaysOnly = allowed.HasFlag(NhAssistantAlertAdminChoices.WorkdaysOnly) ? stored.WorkdaysOnly : code.WorkdaysOnly;
        var immediatelyFrom = allowed.HasFlag(NhAssistantAlertAdminChoices.ImmediatelyFrom) ? stored.ImmediatelyFrom : code.ImmediatelyFrom;
        if (!allowed.HasFlag(NhAssistantAlertAdminChoices.QuietHours))
        {
            quietHours = code.QuietHours;
        }

        // A delivery without digests knows no working days and no urgency from which it is immediate.
        var delivery = mode == NhAssistantAlertDeliveryMode.DailyDigest && times.Count > 0
            ? new NhAssistantAlertEffectiveDelivery(mode, times, workdaysOnly, immediatelyFrom, quietHours)
            : NhAssistantAlertEffectiveDelivery.Immediate(quietHours);
        return new NhAssistantAlertKindState(kind, enabled, delivery, stored);
    }

    /// <summary>An administrator may switch the kind off: it is not required and its code lets them.</summary>
    public static bool MaySwitchOff(NhAssistantAlertKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return !kind.Required && kind.AdministratorChoices.HasFlag(NhAssistantAlertAdminChoices.SwitchOff);
    }

    /// <summary>
    /// Writes digest times as stored: <c>HH:mm</c> separated by commas, in ascending order.
    /// </summary>
    public static string FormatTimes(IEnumerable<TimeOnly> times)
    {
        ArgumentNullException.ThrowIfNull(times);
        return string.Join(',', times.Order().Select(time => time.ToString("HH:mm", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Reads stored digest times; <see cref="FormatTimes"/> wrote them.
    /// </summary>
    /// <exception cref="InvalidOperationException">They are not one to
    /// <see cref="AssistantAlertKindSetting.MaxDigestTimes"/> times of day as <c>HH:mm</c>.</exception>
    public static IReadOnlyList<TimeOnly> ParseTimes(string kindId, string? stored)
    {
        var parts = (stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var times = new List<TimeOnly>(parts.Length);
        foreach (var part in parts)
        {
            if (!NhAssistantAlertPreferences.TryParseDigestTime(part, out var time))
            {
                throw new InvalidOperationException($"The stored digest times of alert kind '{kindId}' are not times of day.");
            }

            times.Add(time);
        }
        if (times.Count is 0 or > AssistantAlertKindSetting.MaxDigestTimes)
        {
            throw new InvalidOperationException($"Alert kind '{kindId}' has no stored digest times or more than {AssistantAlertKindSetting.MaxDigestTimes}.");
        }

        return times.Distinct().Order().ToArray();
    }
}
