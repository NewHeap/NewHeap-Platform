namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// An administrator's settings of one alert kind (FB87 phase 2), within what the kind's code lets an
/// administrator change (<see cref="NhAssistantAlertKind.AdministratorChoices"/>): whether the kind is
/// on, and the kind's default delivery in place of the code's. Without a row, or with
/// <see cref="DeliveryMode"/> empty, the code's delivery applies. A setting the code no longer allows is
/// kept but has no effect. A person's own choices (<see cref="AssistantNotificationPreference"/>) apply
/// on top of it.
/// </summary>
public sealed class AssistantAlertKindSetting
{
    /// <summary>The most digest times of one day.</summary>
    public const int MaxDigestTimes = 4;

    /// <summary>The most characters of <see cref="Reason"/>.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// The kind, registered with <c>AddAlertKind</c>.
    /// </summary>
    public string KindId { get; set; } = string.Empty;

    /// <summary>
    /// The kind raises alerts. An administrator switches it off only where its code allows it; a
    /// required kind is always on, also when this says otherwise.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The administrator's default delivery: <c>immediately</c> or <c>daily-digest</c>
    /// (<see cref="NhAssistantAlertKindSettingModes"/>); <see langword="null"/> for the code's delivery,
    /// with the other delivery values empty.
    /// </summary>
    public string? DeliveryMode { get; set; }

    /// <summary>
    /// The times of day of the digests, in a person's time zone, as a list of <c>HH:mm</c> separated by
    /// commas in ascending order, at most <see cref="MaxDigestTimes"/>, such as <c>07:30,13:00</c>; for
    /// <c>daily-digest</c> only.
    /// </summary>
    public string? DigestTimes { get; set; }

    /// <summary>
    /// The digests go out on working days only; for <c>daily-digest</c> only.
    /// </summary>
    public bool WorkdaysOnly { get; set; }

    /// <summary>
    /// An alert of this urgency or higher is delivered immediately instead of in a digest; for
    /// <c>daily-digest</c> only, <see langword="null"/> for none.
    /// </summary>
    public NhAssistantAlertUrgency? ImmediatelyFrom { get; set; }

    /// <summary>
    /// The start and end of the quiet hours, in a person's time zone, in which nothing of the kind is
    /// delivered; an end before the start spans midnight. Both or neither are set.
    /// </summary>
    public TimeOnly? QuietHoursStart { get; set; }

    public TimeOnly? QuietHoursEnd { get; set; }

    /// <summary>
    /// 1 for the first settings, one more for every change; a change names the version it was made on.
    /// </summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Why the administrator changed the settings, at most <see cref="MaxReasonLength"/> characters; never
    /// in the audit.
    /// </summary>
    public string? Reason { get; set; }

    public Guid ConcurrencyStamp { get; set; }
}

/// <summary>
/// The values of <see cref="AssistantAlertKindSetting.DeliveryMode"/>, also the delivery modes of the
/// administration's HTTP API.
/// </summary>
public static class NhAssistantAlertKindSettingModes
{
    /// <summary>Every alert of the kind is delivered as soon as it is raised.</summary>
    public const string Immediately = "immediately";

    /// <summary>The kind's alerts are delivered in digests at the digest times.</summary>
    public const string DailyDigest = "daily-digest";

    /// <summary>The name of <paramref name="mode"/>.</summary>
    public static string Of(NhAssistantAlertDeliveryMode mode)
    {
        return mode == NhAssistantAlertDeliveryMode.DailyDigest ? DailyDigest : Immediately;
    }

    /// <summary>The mode named <paramref name="name"/>; <see langword="false"/> for another name.</summary>
    public static bool TryParse(string? name, out NhAssistantAlertDeliveryMode mode)
    {
        switch (name)
        {
            case Immediately:
                mode = NhAssistantAlertDeliveryMode.Immediately;
                return true;
            case DailyDigest:
                mode = NhAssistantAlertDeliveryMode.DailyDigest;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
