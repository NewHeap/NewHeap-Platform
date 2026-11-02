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
    /// The time of day of the person's first digest of the kind, in their time zone; <see langword="null"/>
    /// for the kind's own times. <see cref="DigestTimes"/> holds all of them; a row written before it
    /// existed has this one only.
    /// </summary>
    public TimeOnly? DigestTime { get; set; }

    /// <summary>
    /// The times of day of the person's digests of the kind (FB87 phase 2), as a list of <c>HH:mm</c>
    /// separated by commas in ascending order, at most <see cref="AssistantAlertKindSetting.MaxDigestTimes"/>,
    /// such as <c>07:30,16:00</c>; <see langword="null"/> for the kind's own times, or for the single
    /// <see cref="DigestTime"/> of an older row.
    /// </summary>
    public string? DigestTimes { get; set; }

    /// <summary>
    /// The start and end of the person's own quiet hours for the kind, in their time zone, in place of
    /// the kind's; an end before the start spans midnight. Both or neither are set; neither for the
    /// kind's own quiet hours.
    /// </summary>
    public TimeOnly? QuietHoursStart { get; set; }

    public TimeOnly? QuietHoursEnd { get; set; }

    /// <summary>
    /// Whether Web Push and the in-app notifications deliver the kind; <see langword="null"/> for on.
    /// </summary>
    public bool? Push { get; set; }

    public bool? InApp { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A person's "do not tell me about this object for a while" (FB87-11): until <see cref="Until"/> the
/// alerts of one kind about one subject are withheld from the person
/// (<see cref="NhAssistantAlertWithholdReasons.SubjectMuted"/>). They stay in the person's alert list
/// and leave the badge; other kinds about the subject and the kind about other subjects still reach
/// them. A kind that is required is never muted, also when a row says so. The run maintenance removes
/// a mute once it ended, and lifting it removes it at once.
/// </summary>
public sealed class AssistantAlertSubjectMute
{
    public Guid Id { get; set; }

    /// <summary>
    /// The person, by actor id.
    /// </summary>
    public string ActorId { get; set; } = string.Empty;

    /// <summary>
    /// The alert kind, registered with <c>AddAlertKind</c>.
    /// </summary>
    public string KindId { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// The subject's name as the alert the person dismissed showed it, for the person's list of mutes.
    /// </summary>
    public string? SubjectDisplayName { get; set; }

    /// <summary>
    /// When the person muted the subject, on the application's clock.
    /// </summary>
    public DateTimeOffset MutedAt { get; set; }

    /// <summary>
    /// When the mute ends, on the application's clock.
    /// </summary>
    public DateTimeOffset Until { get; set; }
}
