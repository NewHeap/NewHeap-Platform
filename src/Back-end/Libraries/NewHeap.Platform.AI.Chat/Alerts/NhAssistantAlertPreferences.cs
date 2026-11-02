using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// What applies to one person for one alert kind: their stored choices where the kind lets them choose,
/// otherwise the kind's declared delivery (FB87-6). A required kind is never muted.
/// </summary>
/// <param name="Muted">The person does not receive the kind.</param>
/// <param name="DigestTime">The time of day of the person's digest of the kind; <see langword="null"/> for a kind delivered immediately.</param>
/// <param name="ChosenDigestTime">The digest time the person chose and the kind lets them choose; <see langword="null"/> for the kind's own.</param>
/// <param name="Push">Web Push delivers the kind to the person, when push is available and on for them.</param>
/// <param name="InApp">The in-app notifications deliver the kind to the person, when the host has them.</param>
internal sealed record NhAssistantAlertChoice(
    bool Muted,
    TimeOnly? DigestTime,
    TimeOnly? ChosenDigestTime,
    bool Push,
    bool InApp)
{
    public static NhAssistantAlertChoice Of(NhAssistantAlertKind kind, AssistantNotificationPreference? stored)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var chosenDigestTime = MayChangeDigestTime(kind) ? stored?.DigestTime : null;
        var channels = MayChooseChannels(kind);
        return new NhAssistantAlertChoice(
            MayMute(kind) && stored?.Muted == true,
            kind.Delivery.Mode == NhAssistantAlertDeliveryMode.DailyDigest ? chosenDigestTime ?? kind.Delivery.DigestTime : null,
            chosenDigestTime,
            !channels || stored?.Push != false,
            !channels || stored?.InApp != false);
    }

    /// <summary>A person may mute the kind: it is not required and lets them.</summary>
    public static bool MayMute(NhAssistantAlertKind kind)
    {
        return !kind.Required && kind.Choices.HasFlag(NhAssistantAlertChoices.Mute);
    }

    /// <summary>A person may choose the time of the kind's digest: it is delivered in one and lets them.</summary>
    public static bool MayChangeDigestTime(NhAssistantAlertKind kind)
    {
        return kind.Delivery.Mode == NhAssistantAlertDeliveryMode.DailyDigest
            && kind.Choices.HasFlag(NhAssistantAlertChoices.DigestTime);
    }

    /// <summary>A person may choose the channels of the kind.</summary>
    public static bool MayChooseChannels(NhAssistantAlertKind kind)
    {
        return kind.Choices.HasFlag(NhAssistantAlertChoices.Channels);
    }
}

/// <summary>
/// One alert kind as a person's preferences show it: the kind, what they may change and what applies.
/// </summary>
internal sealed record NhAssistantAlertKindPreference(NhAssistantAlertKind Kind, NhAssistantAlertChoice Choice)
{
    public bool MayMute => NhAssistantAlertChoice.MayMute(Kind);

    public bool MayChangeDigestTime => NhAssistantAlertChoice.MayChangeDigestTime(Kind);

    public bool MayChooseChannels => NhAssistantAlertChoice.MayChooseChannels(Kind);
}

/// <summary>
/// A person's preferences about alerts: their time zone for the digest and every registered kind.
/// </summary>
/// <param name="TimeZoneId">The id of the person's time zone, as the application gives it or the application's own.</param>
/// <param name="Kinds">Every registered kind, ordered by id.</param>
/// <param name="InAppAvailable">The host delivers in-app notifications (<see cref="INhUserNotificationSink"/>).</param>
internal sealed record NhAssistantAlertPreferenceView(
    string TimeZoneId,
    IReadOnlyList<NhAssistantAlertKindPreference> Kinds,
    bool InAppAvailable);

/// <summary>
/// One person's requested change to one alert kind: a value that is <see langword="null"/> stays as it
/// is, and <see cref="DigestTimeGiven"/> says whether <see cref="DigestTime"/> was given, so a
/// <see langword="null"/> digest time resets it to the kind's own.
/// </summary>
internal sealed record NhAssistantAlertPreferenceRequest(string? KindId)
{
    public bool? Muted { get; init; }

    public bool DigestTimeGiven { get; init; }

    /// <summary>The digest time as <c>HH:mm</c>.</summary>
    public string? DigestTime { get; init; }

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
    IServiceProvider services,
    TimeProvider? timeProvider = null)
{
    /// <summary>The most kinds one change names.</summary>
    internal const int MaxKindsPerChange = 200;

    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    public async Task<NhAssistantAlertPreferenceView> GetAsync(string actorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var zone = await timeZones.OfAsync(actorId, cancellationToken);
        var stored = registration.AlertKinds.Count == 0
            ? new Dictionary<string, AssistantNotificationPreference>(StringComparer.Ordinal)
            : await notifications.GetPreferencesAsync(actorId, NhAssistantNotificationPreferenceCategories.Alert, cancellationToken);
        var kinds = registration.AlertKinds.Values
            .OrderBy(kind => kind.Id, StringComparer.Ordinal)
            .Select(kind => new NhAssistantAlertKindPreference(kind, NhAssistantAlertChoice.Of(kind, stored.GetValueOrDefault(kind.Id))))
            .ToArray();
        return new NhAssistantAlertPreferenceView(zone.Id, kinds, services.GetService<INhUserNotificationSink>() is not null);
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

        var changes = new List<NhAssistantNotificationPreferenceChange>(requests.Count);
        foreach (var request in requests)
        {
            var checkedChange = Check(request);
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

    private TaskResult<NhAssistantNotificationPreferenceChange> Check(NhAssistantAlertPreferenceRequest request)
    {
        if (request.KindId is null || !registration.AlertKinds.TryGetValue(request.KindId, out var kind))
        {
            return Refused(NhAssistantErrorCodes.NotificationKindUnknown);
        }
        if (request.Muted == true && !NhAssistantAlertChoice.MayMute(kind))
        {
            return Refused(NhAssistantErrorCodes.NotificationMuteNotAllowed);
        }

        TimeOnly? digestTime = null;
        if (request.DigestTimeGiven && request.DigestTime is not null)
        {
            if (!TryParseDigestTime(request.DigestTime, out var parsed))
            {
                return Refused(NhAssistantErrorCodes.NotificationDigestTimeInvalid);
            }
            if (!NhAssistantAlertChoice.MayChangeDigestTime(kind))
            {
                return Refused(NhAssistantErrorCodes.NotificationDigestTimeNotAllowed);
            }

            digestTime = parsed;
        }
        if ((request.Push == false || request.InApp == false) && !NhAssistantAlertChoice.MayChooseChannels(kind))
        {
            return Refused(NhAssistantErrorCodes.NotificationChannelsNotAllowed);
        }

        return TaskResult<NhAssistantNotificationPreferenceChange>.Succeeded(new NhAssistantNotificationPreferenceChange(kind.Id)
        {
            Muted = request.Muted,
            DigestTimeChanged = request.DigestTimeGiven,
            DigestTime = digestTime,
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
