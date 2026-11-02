namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// A person's choices about one kind of notification of the assistant (FB87-6): whether they muted it,
/// the time of their daily digest and the channels that deliver it. One model for every notification
/// of the assistant; in phase 1 it holds the alert kinds (<see cref="Category"/> <c>alert</c>). Without
/// a row, or with a value left empty, the kind's declared delivery applies. The global push switch stays
/// <see cref="AssistantNotificationSetting.PushEnabled"/>. A choice the kind does not let a person make
/// is kept but has no effect.
/// </summary>
public sealed class AssistantNotificationPreference
{
    /// <summary>
    /// The person, by actor id.
    /// </summary>
    public string ActorId { get; set; } = string.Empty;

    /// <summary>
    /// What kind of notification <see cref="KindId"/> names: <c>alert</c> for an alert kind.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// The kind, such as an alert kind registered with <c>AddAlertKind</c>.
    /// </summary>
    public string KindId { get; set; } = string.Empty;

    /// <summary>
    /// The person stopped receiving the kind. Never applies to a kind that is required.
    /// </summary>
    public bool Muted { get; set; }

    /// <summary>
    /// The time of day of the person's digest of the kind, in their time zone; <see langword="null"/>
    /// for the kind's own time.
    /// </summary>
    public TimeOnly? DigestTime { get; set; }

    /// <summary>
    /// Whether Web Push and the in-app notifications deliver the kind; <see langword="null"/> for on.
    /// </summary>
    public bool? Push { get; set; }

    public bool? InApp { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
