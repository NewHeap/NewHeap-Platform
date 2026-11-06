namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// One alert (FB87): a warning of one kind about one subject. One open alert exists per kind, subject
/// and key, shadow apart; raising them again updates it in place as its next revision. Resolving or
/// expiring it closes it, and the run maintenance purges it after <see cref="NhAssistantLimits.AlertRetention"/>.
/// Its parameters are the application's data and stay in this store; the audit never carries them.
/// </summary>
public sealed class AssistantAlert
{
    public Guid Id { get; set; }

    /// <summary>
    /// The kind, registered with <c>AddAlertKind</c>.
    /// </summary>
    public string KindId { get; set; } = string.Empty;

    /// <summary>
    /// The subject's scope key, as its resolver described it when the alert was raised.
    /// </summary>
    public string ScopeKey { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// The subject's name people see, as its resolver last described it.
    /// </summary>
    public string? SubjectDisplayName { get; set; }

    /// <summary>
    /// The subject's division and tenant, as its resolver last described them.
    /// </summary>
    public Guid? DivisionId { get; set; }

    public string? TenantId { get; set; }

    /// <summary>
    /// The subject the alert's subject belongs to (<see cref="NhAiSubjectInfo.Parent"/>), a label the
    /// alert list groups by; never resolved.
    /// </summary>
    public string? ParentSubjectType { get; set; }

    public string? ParentSubjectId { get; set; }

    public string? ParentDisplayName { get; set; }

    /// <summary>
    /// Tells alerts of the same kind and subject apart; empty for one alert of the kind per subject.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The SHA-256 of the alert's identity (schema, scope key, kind, subject, key and shadow mark),
    /// lowercase hex. Unique with <see cref="ClosedMarker"/>, so one open alert exists per identity.
    /// </summary>
    public string IdentityHash { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Guid.Empty"/> while the alert is open and the alert's own id once it closed, so the
    /// unique index over <see cref="IdentityHash"/> and this marker admits one open alert per identity
    /// and any number of closed ones on every provider.
    /// </summary>
    public Guid ClosedMarker { get; set; }

    /// <summary>
    /// One of <see cref="NhAssistantAlertStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantAlertStatuses.Open;

    public NhAssistantAlertUrgency Urgency { get; set; }

    /// <summary>
    /// The named parameters the kind's title and text are written from, as a JSON object of strings.
    /// </summary>
    public string ParametersJson { get; set; } = "{}";

    /// <summary>
    /// 1 when the alert was raised, one more for every update in place.
    /// </summary>
    public int Revision { get; set; } = 1;

    /// <summary>
    /// Raised by a shadow agent or run: recorded for evaluation, with no deliveries to people.
    /// </summary>
    public bool Shadow { get; set; }

    /// <summary>
    /// The agent and run whose rules raised or last updated the alert; <see langword="null"/> for the
    /// application's code.
    /// </summary>
    public string? AgentId { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>
    /// The language model of <see cref="AgentId"/> raised or last updated the alert through the platform
    /// tool <c>assistant.alert.raise</c> (FB87-9), so its parameters are the model's, which are untrusted:
    /// the platform checked them against the kind's template, and the alert list labels the alert.
    /// </summary>
    public bool RaisedByModel { get; set; }

    /// <summary>
    /// When the alert was first raised, on the application's clock.
    /// </summary>
    public DateTimeOffset RaisedAt { get; set; }

    /// <summary>
    /// When the alert was last raised again, on the application's clock.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// When the alert was last planned for delivery to its recipients: when it was raised, or when it was
    /// delivered again because its urgency rose or the kind's <c>RepeatAfter</c> passed.
    /// </summary>
    public DateTimeOffset DeliveryPlannedAt { get; set; }

    /// <summary>
    /// When the alert expires on the application's clock; <see langword="null"/> while it is not resolved.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// When the alert was resolved or expired, on the application's clock.
    /// </summary>
    public DateTimeOffset? ClosedAt { get; set; }

    public Guid ConcurrencyStamp { get; set; }

    public ICollection<AssistantAlertDelivery> Deliveries { get; set; } = [];
}

/// <summary>
/// The delivery of one alert to one person: whether it still waits, went out, was read or dismissed, or
/// was taken back. The assistant's delivery claims a row that is due before it sends it, so several
/// instances never send it twice; a digest groups the rows it sends under one <see cref="DigestId"/>.
/// </summary>
public sealed class AssistantAlertDelivery
{
    public Guid Id { get; set; }

    public Guid AlertId { get; set; }

    public AssistantAlert? Alert { get; set; }

    /// <summary>
    /// The person, by actor id.
    /// </summary>
    public string RecipientActorId { get; set; } = string.Empty;

    /// <summary>
    /// One of <see cref="NhAssistantAlertDeliveryStatuses"/>.
    /// </summary>
    public string Status { get; set; } = NhAssistantAlertDeliveryStatuses.Pending;

    /// <summary>
    /// Why the delivery was dropped, one of <see cref="NhAssistantAlertDropReasons"/>, or why the last
    /// attempt failed, as a code; <see langword="null"/> otherwise.
    /// </summary>
    public string? ReasonCode { get; set; }

    /// <summary>
    /// When a pending delivery is due on the application's clock: now for an immediate one, the digest's
    /// moment for one in a digest.
    /// </summary>
    public DateTimeOffset? DueAt { get; set; }

    /// <summary>
    /// The instance that claimed the delivery to send it, and until when the claim holds; another instance
    /// takes it over after that. A delivery that waits, such as for a fresh context of its person's
    /// rights, has no claimant and is claimed again only after <see cref="ClaimedUntil"/>.
    /// </summary>
    public string? ClaimedBy { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    /// <summary>
    /// The digest the delivery went out in; <see langword="null"/> for one sent on its own.
    /// </summary>
    public Guid? DigestId { get; set; }

    /// <summary>
    /// The channels that delivered it, comma-separated codes such as <c>web-push,in-app</c>.
    /// </summary>
    public string? Channels { get; set; }

    /// <summary>
    /// The attempts to send it since it was last planned.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// The alert's revision the delivery was last planned for, and the revision that last went out.
    /// </summary>
    public int PlannedRevision { get; set; }

    public int? SentRevision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset? DismissedAt { get; set; }

    /// <summary>
    /// When the delivery was taken back: resolved, expired or dropped.
    /// </summary>
    public DateTimeOffset? ClosedAt { get; set; }

    public Guid ConcurrencyStamp { get; set; }
}
