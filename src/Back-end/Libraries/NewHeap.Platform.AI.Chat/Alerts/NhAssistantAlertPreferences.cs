using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// What applies to one person for one alert kind: their stored choices where the kind lets them choose,
/// otherwise the kind's effective delivery (FB87-6): its code merged with an administrator's settings
/// (<see cref="NhAssistantAlertKindSettings"/>). A required kind is never muted.
/// </summary>
/// <param name="Muted">The person does not receive the kind.</param>
/// <param name="DigestTime">The time of day of the person's first digest of the kind; <see langword="null"/> for a kind delivered immediately.</param>
/// <param name="ChosenDigestTime">The first digest time the person chose and the kind lets them choose; <see langword="null"/> for the kind's own.</param>
/// <param name="Push">Web Push delivers the kind to the person, when push is available and on for them.</param>
/// <param name="InApp">The in-app notifications deliver the kind to the person, when the host has them.</param>
internal sealed record NhAssistantAlertChoice(
    bool Muted,
    TimeOnly? DigestTime,
    TimeOnly? ChosenDigestTime,
    bool Push,
    bool InApp)
{
    /// <summary>
    /// The times of day of the person's digests of the kind in ascending order, the first of them
    /// <see cref="DigestTime"/>; empty for a kind delivered immediately.
    /// </summary>
    public IReadOnlyList<TimeOnly> DigestTimes { get; init; } = [];

    /// <summary>The digest times the person chose and the kind lets them choose; <see langword="null"/> for the kind's own.</summary>
    public IReadOnlyList<TimeOnly>? ChosenDigestTimes { get; init; }

    /// <summary>
    /// The quiet hours that apply to the person: their own where the kind lets them choose, otherwise the
    /// kind's effective ones; <see langword="null"/> for none.
    /// </summary>
    public NhAssistantAlertQuietHours? QuietHours { get; init; }

    /// <summary>The quiet hours the person chose and the kind lets them choose; <see langword="null"/> for the kind's own.</summary>
    public NhAssistantAlertQuietHours? ChosenQuietHours { get; init; }

    /// <summary>
    /// What applies to the person under the kind's declared delivery, without an administrator's settings.
    /// </summary>
    public static NhAssistantAlertChoice Of(NhAssistantAlertKind kind, AssistantNotificationPreference? stored)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return Of(kind, NhAssistantAlertEffectiveDelivery.Of(kind.Delivery), stored);
    }

    /// <summary>
    /// What applies to the person under <paramref name="delivery"/>, the kind's effective delivery.
    /// </summary>
    public static NhAssistantAlertChoice Of(
        NhAssistantAlertKind kind,
        NhAssistantAlertEffectiveDelivery delivery,
        AssistantNotificationPreference? stored)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(delivery);
        var chosenDigestTimes = MayChangeDigestTime(kind, delivery) ? DigestTimesOf(stored) : null;
        IReadOnlyList<TimeOnly> digestTimes = delivery.DigestTime is null ? [] : chosenDigestTimes ?? delivery.DigestTimes;
        var chosenQuietHours = MayChangeQuietHours(kind) ? QuietHoursOf(stored) : null;
        var channels = MayChooseChannels(kind);
        return new NhAssistantAlertChoice(
            MayMute(kind) && stored?.Muted == true,
            digestTimes.Count > 0 ? digestTimes[0] : null,
            chosenDigestTimes?[0],
            !channels || stored?.Push != false,
            !channels || stored?.InApp != false)
        {
            DigestTimes = digestTimes,
            ChosenDigestTimes = chosenDigestTimes,
            QuietHours = chosenQuietHours ?? delivery.QuietHours,
            ChosenQuietHours = chosenQuietHours
        };
    }

    /// <summary>A person may choose their own quiet hours for the kind.</summary>
    public static bool MayChangeQuietHours(NhAssistantAlertKind kind)
    {
        return kind.Choices.HasFlag(NhAssistantAlertChoices.QuietHours);
    }

    /// <summary>
    /// The digest times a person stored: their list, or the single time of a row written before lists;
    /// <see langword="null"/> for none, or for a list that cannot be read, which then has no effect.
    /// </summary>
    internal static IReadOnlyList<TimeOnly>? DigestTimesOf(AssistantNotificationPreference? stored)
    {
        if (stored?.DigestTimes is not { Length: > 0 } list)
        {
            return stored?.DigestTime is { } single ? [single] : null;
        }

        var times = new List<TimeOnly>(AssistantAlertKindSetting.MaxDigestTimes);
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!NhAssistantAlertPreferences.TryParseDigestTime(part, out var time))
            {
                return null;
            }

            times.Add(time);
        }

        var distinct = times.Distinct().Order().ToArray();
        return distinct.Length is > 0 and <= AssistantAlertKindSetting.MaxDigestTimes ? distinct : null;
    }

    /// <summary>
    /// The quiet hours a person stored; <see langword="null"/> for none or for a start and end that make no
    /// quiet hours, which then have no effect.
    /// </summary>
    internal static NhAssistantAlertQuietHours? QuietHoursOf(AssistantNotificationPreference? stored)
    {
        return stored?.QuietHoursStart is { } start
            && stored.QuietHoursEnd is { } end
            && NhAssistantAlertQuietHours.IsValid(start, end)
                ? new NhAssistantAlertQuietHours(start, end)
                : null;
    }

    /// <summary>A person may mute the kind: it is not required and lets them.</summary>
    public static bool MayMute(NhAssistantAlertKind kind)
    {
        return !kind.Required && kind.Choices.HasFlag(NhAssistantAlertChoices.Mute);
    }

    /// <summary>A person may choose the times of the kind's digests: its code delivers it in digests and lets them.</summary>
    public static bool MayChangeDigestTime(NhAssistantAlertKind kind)
    {
        return MayChangeDigestTime(kind, NhAssistantAlertEffectiveDelivery.Of(kind.Delivery));
    }

    /// <summary>
    /// A person may choose the times of the kind's digests: <paramref name="delivery"/>, its effective
    /// delivery, delivers it in digests and the kind lets them.
    /// </summary>
    public static bool MayChangeDigestTime(NhAssistantAlertKind kind, NhAssistantAlertEffectiveDelivery delivery)
    {
        return delivery.DigestTime is not null
            && kind.Choices.HasFlag(NhAssistantAlertChoices.DigestTime);
    }

    /// <summary>A person may choose the channels of the kind.</summary>
    public static bool MayChooseChannels(NhAssistantAlertKind kind)
    {
        return kind.Choices.HasFlag(NhAssistantAlertChoices.Channels);
    }
}

/// <summary>
/// One alert kind as a person's preferences show it: the kind, its effective delivery, what they may
/// change and what applies.
/// </summary>
internal sealed record NhAssistantAlertKindPreference(
    NhAssistantAlertKind Kind,
    NhAssistantAlertEffectiveDelivery Delivery,
    NhAssistantAlertChoice Choice)
{
    public bool MayMute => NhAssistantAlertChoice.MayMute(Kind);

    public bool MayChangeDigestTime => NhAssistantAlertChoice.MayChangeDigestTime(Kind, Delivery);

    public bool MayChooseChannels => NhAssistantAlertChoice.MayChooseChannels(Kind);

    public bool MayChangeQuietHours => NhAssistantAlertChoice.MayChangeQuietHours(Kind);
}

/// <summary>
/// One of a person's mutes of an alert kind about a subject (FB87-11) as their preferences list it.
/// </summary>
internal sealed record NhAssistantAlertSubjectMuteView(AssistantAlertSubjectMute Mute, NhAssistantAlertKind Kind);

/// <summary>
/// A person's preferences about alerts: their time zone for the digest, every registered kind and their
/// mutes of a kind about a subject.
/// </summary>
/// <param name="TimeZoneId">The id of the person's time zone, as the application gives it or the application's own.</param>
/// <param name="Kinds">Every registered kind that is on, ordered by id.</param>
/// <param name="InAppAvailable">The host delivers in-app notifications (<see cref="INhUserNotificationSink"/>).</param>
internal sealed record NhAssistantAlertPreferenceView(
    string TimeZoneId,
    IReadOnlyList<NhAssistantAlertKindPreference> Kinds,
    bool InAppAvailable)
{
    /// <summary>
    /// The person's mutes that have not ended, of kinds they may mute that are on, the most recent first,
    /// at most <see cref="NhAssistantAlertPreferences.MaxListedSubjectMutes"/>.
    /// </summary>
    public IReadOnlyList<NhAssistantAlertSubjectMuteView> SubjectMutes { get; init; } = [];
}

/// <summary>
/// One person's requested change to one alert kind: a value that is <see langword="null"/> stays as it
/// is, and <see cref="DigestTimeGiven"/>, <see cref="DigestTimesGiven"/> and <see cref="QuietHoursGiven"/>
/// say whether a value was given, so a <see langword="null"/> one resets it to the kind's own.
/// </summary>
internal sealed record NhAssistantAlertPreferenceRequest(string? KindId)
{
    public bool? Muted { get; init; }

    public bool DigestTimeGiven { get; init; }

    /// <summary>The digest time as <c>HH:mm</c>; the one time of the person's digests.</summary>
    public string? DigestTime { get; init; }

    /// <summary>Whether <see cref="DigestTimes"/> was given; it replaces <see cref="DigestTime"/> then.</summary>
    public bool DigestTimesGiven { get; init; }

    /// <summary>The digest times as <c>HH:mm</c>, one to <see cref="AssistantAlertKindSetting.MaxDigestTimes"/>.</summary>
    public IReadOnlyList<string?>? DigestTimes { get; init; }

    public bool QuietHoursGiven { get; init; }

    /// <summary>The start and end of the person's quiet hours as <c>HH:mm</c>; both <see langword="null"/> resets them.</summary>
    public string? QuietHoursStart { get; init; }

    public string? QuietHoursEnd { get; init; }

    public bool? Push { get; init; }

    public bool? InApp { get; init; }
}

/// <summary>
/// Reads and changes one person's preferences about alerts (FB87-6), for the person's own
/// <c>GET</c> and <c>PUT notifications</c>. A change the kind does not allow is refused with a stable
/// code and nothing is saved; a value that only repeats the kind's default is accepted, so a client may
/// send every kind back as it read it.
/// </summary>
internal sealed class NhAssistantAlertPreferences(
    NhAssistantRegistrationState registration,
    NhAssistantNotificationStore notifications,
    NhAssistantAlertTimeZones timeZones,
    NhAssistantAlertKindSettings kindSettings,
    NhAssistantAlertSubjectMuteStore subjectMutes,
    IServiceProvider services,
    TimeProvider? timeProvider = null)
{
    /// <summary>The most kinds one change names.</summary>
    internal const int MaxKindsPerChange = 200;

    /// <summary>The most mutes of a kind about a subject the preferences list, and one change lifts.</summary>
    internal const int MaxListedSubjectMutes = 200;

    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    public async Task<NhAssistantAlertPreferenceView> GetAsync(string actorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var zone = await timeZones.OfAsync(actorId, cancellationToken);
        var inApp = services.GetService<INhUserNotificationSink>() is not null;
        if (registration.AlertKinds.Count == 0)
        {
            // A host without alert kinds has nothing to choose and may not have the alert tables.
            return new NhAssistantAlertPreferenceView(zone.Id, [], inApp);
        }

        var stored = await notifications.GetPreferencesAsync(actorId, NhAssistantNotificationPreferenceCategories.Alert, cancellationToken);
        // A kind an administrator switched off reaches nobody, so a person has nothing to choose about it.
        var states = await kindSettings.GetAllAsync(cancellationToken);
        var kinds = states.Values
            .Where(state => state.Enabled)
            .OrderBy(state => state.Kind.Id, StringComparer.Ordinal)
            .Select(state => new NhAssistantAlertKindPreference(
                state.Kind,
                state.Delivery,
                NhAssistantAlertChoice.Of(state.Kind, state.Delivery, stored.GetValueOrDefault(state.Kind.Id))))
            .ToArray();

        // A mute has effect only for a kind that is on and that the person may mute.
        var mutes = await subjectMutes.ListActiveAsync(actorId, _applicationClock.GetUtcNow(), MaxListedSubjectMutes, cancellationToken);
        var listed = mutes
            .Where(mute => states.TryGetValue(mute.KindId, out var state) && state.Enabled && NhAssistantAlertChoice.MayMute(state.Kind))
            .Select(mute => new NhAssistantAlertSubjectMuteView(mute, states[mute.KindId].Kind))
            .ToArray();
        return new NhAssistantAlertPreferenceView(zone.Id, kinds, inApp) { SubjectMutes = listed };
    }

    /// <summary>
    /// Lifts the person's own mutes of a kind about a subject among <paramref name="muteIds"/>, at most
    /// <see cref="MaxListedSubjectMutes"/>; an id of another person's mute, or of one that ended, changes
    /// nothing, so lifting is idempotent.
    /// </summary>
    public async Task<TaskResult> LiftSubjectMutesAsync(string actorId, IReadOnlyCollection<Guid> muteIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(muteIds);
        if (muteIds.Count > MaxListedSubjectMutes)
        {
            return TaskResult.Failed(NhAssistantAdminErrorCodes.ValidationFailed, NhAssistantErrorCodes.MessageKey(NhAssistantAdminErrorCodes.ValidationFailed));
        }
        if (muteIds.Count > 0 && registration.AlertKinds.Count > 0)
        {
            await subjectMutes.LiftAsync(actorId, muteIds, cancellationToken);
        }

        return TaskResult.Succeeded();
    }

    /// <summary>
    /// Checks every change against its kind and saves them all, or refuses them all with the code of the
    /// first one the kind does not allow.
    /// </summary>
    public async Task<TaskResult> UpdateAsync(
        string actorId,
        IReadOnlyList<NhAssistantAlertPreferenceRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count > MaxKindsPerChange
            || requests.Select(request => request.KindId).Distinct(StringComparer.Ordinal).Count() != requests.Count)
        {
            return TaskResult.Failed(NhAssistantAdminErrorCodes.ValidationFailed, NhAssistantErrorCodes.MessageKey(NhAssistantAdminErrorCodes.ValidationFailed));
        }

        var states = await kindSettings.GetAllAsync(cancellationToken);
        var changes = new List<NhAssistantNotificationPreferenceChange>(requests.Count);
        foreach (var request in requests)
        {
            var checkedChange = Check(request, states);
            if (!checkedChange.Success)
            {
                return checkedChange;
            }

            changes.Add(checkedChange.Data!);
        }

        await notifications.SavePreferencesAsync(
            actorId,
            NhAssistantNotificationPreferenceCategories.Alert,
            changes,
            _applicationClock.GetUtcNow(),
            cancellationToken);
        return TaskResult.Succeeded();
    }

    private static TaskResult<NhAssistantNotificationPreferenceChange> Check(
        NhAssistantAlertPreferenceRequest request,
        IReadOnlyDictionary<string, NhAssistantAlertKindState> states)
    {
        if (request.KindId is null || !states.TryGetValue(request.KindId, out var state))
        {
            return Refused(NhAssistantErrorCodes.NotificationKindUnknown);
        }

        var kind = state.Kind;
        if (request.Muted == true && !NhAssistantAlertChoice.MayMute(kind))
        {
            return Refused(NhAssistantErrorCodes.NotificationMuteNotAllowed);
        }

        // A list of digest times replaces the single time of phase 1; either resets with null.
        IReadOnlyList<string?>? givenTimes = request.DigestTimesGiven
            ? request.DigestTimes
            : request.DigestTimeGiven && request.DigestTime is not null ? [request.DigestTime] : null;
        IReadOnlyList<TimeOnly>? digestTimes = null;
        if (givenTimes is not null)
        {
            var parsed = new List<TimeOnly>(givenTimes.Count);
            foreach (var given in givenTimes)
            {
                if (given is null || !TryParseDigestTime(given, out var time))
                {
                    return Refused(NhAssistantErrorCodes.NotificationDigestTimeInvalid);
                }

                parsed.Add(time);
            }

            digestTimes = parsed.Distinct().Order().ToArray();
            if (digestTimes.Count is 0 or > AssistantAlertKindSetting.MaxDigestTimes)
            {
                return Refused(NhAssistantErrorCodes.NotificationDigestTimeInvalid);
            }
            if (!NhAssistantAlertChoice.MayChangeDigestTime(kind, state.Delivery))
            {
                return Refused(NhAssistantErrorCodes.NotificationDigestTimeNotAllowed);
            }
        }

        NhAssistantAlertQuietHours? quietHours = null;
        if (request.QuietHoursGiven && (request.QuietHoursStart is not null || request.QuietHoursEnd is not null))
        {
            if (request.QuietHoursStart is null
                || request.QuietHoursEnd is null
                || !TryParseDigestTime(request.QuietHoursStart, out var start)
                || !TryParseDigestTime(request.QuietHoursEnd, out var end)
                || !NhAssistantAlertQuietHours.IsValid(start, end))
            {
                return Refused(NhAssistantErrorCodes.NotificationQuietHoursInvalid);
            }
            if (!NhAssistantAlertChoice.MayChangeQuietHours(kind))
            {
                return Refused(NhAssistantErrorCodes.NotificationQuietHoursNotAllowed);
            }

            quietHours = new NhAssistantAlertQuietHours(start, end);
        }
        if ((request.Push == false || request.InApp == false) && !NhAssistantAlertChoice.MayChooseChannels(kind))
        {
            return Refused(NhAssistantErrorCodes.NotificationChannelsNotAllowed);
        }

        var digestTimesChanged = request.DigestTimesGiven || request.DigestTimeGiven;
        return TaskResult<NhAssistantNotificationPreferenceChange>.Succeeded(new NhAssistantNotificationPreferenceChange(kind.Id)
        {
            Muted = request.Muted,
            DigestTimeChanged = digestTimesChanged,
            DigestTime = digestTimes?[0],
            DigestTimesChanged = digestTimesChanged,
            DigestTimes = digestTimes,
            QuietHoursChanged = request.QuietHoursGiven,
            QuietHours = quietHours,
            Push = request.Push,
            InApp = request.InApp
        });
    }

    /// <summary>
    /// Reads a digest time as <c>HH:mm</c>, such as <c>07:30</c>.
    /// </summary>
    internal static bool TryParseDigestTime(string value, out TimeOnly time)
    {
        return TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>
    /// Writes a digest time as <c>HH:mm</c>.
    /// </summary>
    internal static string? FormatDigestTime(TimeOnly? time)
    {
        return time?.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static TaskResult<NhAssistantNotificationPreferenceChange> Refused(string code)
    {
        return TaskResult<NhAssistantNotificationPreferenceChange>.Failed(code, NhAssistantErrorCodes.MessageKey(code));
    }
}
