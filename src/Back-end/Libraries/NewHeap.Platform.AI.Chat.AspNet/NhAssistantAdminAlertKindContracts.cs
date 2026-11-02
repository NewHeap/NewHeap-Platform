namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The administration's view of every alert kind (<c>GET admin/alert-kinds</c>), with the numbers of the
/// alerts raised within the period.
/// </summary>
/// <param name="From">The start of the period, inclusive.</param>
/// <param name="To">The end of the period, exclusive.</param>
/// <param name="Kinds">Every registered kind, ordered by id.</param>
public sealed record NhAssistantAdminAlertKindsDto(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<NhAssistantAdminAlertKindDto> Kinds);

/// <summary>
/// One alert kind in the administration: what its code declares and allows, the administrator's settings,
/// and what applies now.
/// </summary>
/// <param name="Id">The kind's id.</param>
/// <param name="DisplayNameKey">The front-end translation key of the kind's name.</param>
/// <param name="Urgency">The kind's urgency: <c>info</c>, <c>warning</c> or <c>urgent</c>.</param>
/// <param name="Required">Nobody can mute the kind and no administrator can switch it off.</param>
/// <param name="AdministratorMayChange">What the kind's code lets an administrator change.</param>
/// <param name="CodeDelivery">The default delivery the kind's code declares.</param>
/// <param name="Settings">The administrator's settings; version 0 when nobody changed the kind.</param>
/// <param name="Enabled">The kind raises alerts now.</param>
/// <param name="Delivery">The default delivery that applies now: the code's merged with the settings
/// the code allows. A person's own choices apply on top of it.</param>
public sealed record NhAssistantAdminAlertKindDto(
    string Id,
    string DisplayNameKey,
    string Urgency,
    bool Required,
    NhAssistantAdminAlertKindChoicesDto AdministratorMayChange,
    NhAssistantAdminAlertDeliveryDto CodeDelivery,
    NhAssistantAdminAlertKindSettingsDto Settings,
    bool Enabled,
    NhAssistantAdminAlertDeliveryDto Delivery)
{
    /// <summary>
    /// The numbers of the kind's alerts raised within the period; <see langword="null"/> in the answer of
    /// <c>PUT admin/alert-kinds/{id}</c>.
    /// </summary>
    public NhAssistantAdminAlertKindNumbersDto? Numbers { get; init; }

    /// <summary>
    /// Per agent in shadow, the kind's alerts it raised within the period and would have sent, the most
    /// first; empty in the answer of <c>PUT admin/alert-kinds/{id}</c>.
    /// </summary>
    public IReadOnlyList<NhAssistantAdminAlertShadowAgentDto> Shadow { get; init; } = [];
}

/// <summary>
/// What a kind's code lets an administrator change (<c>AdministratorMayChange</c>).
/// </summary>
/// <param name="SwitchOff">Switch the kind off; never for a required kind.</param>
/// <param name="DigestTimes">Deliver it immediately or in digests, and the digest times.</param>
/// <param name="WorkdaysOnly">Whether its digests go out on working days only.</param>
/// <param name="ImmediatelyFrom">From which urgency an alert is delivered immediately.</param>
/// <param name="QuietHours">Its quiet hours.</param>
public sealed record NhAssistantAdminAlertKindChoicesDto(
    bool SwitchOff,
    bool DigestTimes,
    bool WorkdaysOnly,
    bool ImmediatelyFrom,
    bool QuietHours);

/// <summary>
/// A default delivery of a kind.
/// </summary>
/// <param name="Mode"><c>immediately</c> or <c>daily-digest</c>.</param>
/// <param name="DigestTimes">The times of day of the digests as <c>HH:mm</c> in a person's time zone, in
/// ascending order, one to four; empty for <c>immediately</c>.</param>
/// <param name="WorkdaysOnly">The digests go out on working days only.</param>
/// <param name="ImmediatelyFrom">An alert of this urgency or higher is delivered immediately instead of in
/// a digest: <c>info</c>, <c>warning</c> or <c>urgent</c>; <see langword="null"/> for none.</param>
/// <param name="QuietHours">When nothing of the kind is delivered; <see langword="null"/> for none.</param>
public sealed record NhAssistantAdminAlertDeliveryDto(
    string Mode,
    IReadOnlyList<string> DigestTimes,
    bool WorkdaysOnly,
    string? ImmediatelyFrom,
    NhAssistantAdminAlertQuietHoursDto? QuietHours);

/// <summary>
/// Quiet hours as <c>HH:mm</c> in a person's time zone; an end before the start spans midnight.
/// </summary>
public sealed record NhAssistantAdminAlertQuietHoursDto(string Start, string End);

/// <summary>
/// The administrator's settings of a kind as stored.
/// </summary>
/// <param name="Enabled">The administrator left the kind on.</param>
/// <param name="Delivery">The administrator's default delivery; <see langword="null"/> for the code's.</param>
/// <param name="Version">The settings' version, which a change names as <c>expectedVersion</c>; 0 when
/// nobody changed the kind.</param>
/// <param name="ChangedBy">Who changed them last.</param>
/// <param name="ChangedAt">When they were changed last.</param>
/// <param name="Reason">The reason given with the last change.</param>
public sealed record NhAssistantAdminAlertKindSettingsDto(
    bool Enabled,
    NhAssistantAdminAlertDeliveryDto? Delivery,
    int Version,
    string? ChangedBy,
    DateTimeOffset? ChangedAt,
    string? Reason);

/// <summary>
/// The numbers of a kind's live alerts raised within a period, counted per delivery to one person except
/// <see cref="Raised"/>. A delivery dropped because its person no longer qualified does not count.
/// </summary>
/// <param name="Raised">Alerts raised.</param>
/// <param name="Delivered">Deliveries that went out to a person's channels.</param>
/// <param name="Read">Deliveries whose person read the alert.</param>
/// <param name="Dismissed">Deliveries whose person dismissed the alert.</param>
/// <param name="ResolvedBeforeReading">Deliveries of alerts that were resolved before their person read them.</param>
/// <param name="Withheld">Deliveries that were due and went out to none of the person's channels, for any reason.</param>
/// <param name="Muted">Of <see cref="Withheld"/>, those of a person who muted the kind.</param>
public sealed record NhAssistantAdminAlertKindNumbersDto(
    int Raised,
    int Delivered,
    int Read,
    int Dismissed,
    int ResolvedBeforeReading,
    int Withheld,
    int Muted);

/// <summary>
/// The shadow alerts of a kind that one agent raised within the period: recorded, delivered to nobody.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="AgentDisplayName">The front-end translation key of the agent's name, when it is known.</param>
/// <param name="WouldHaveSent">The shadow alerts it raised, each of which it would have sent.</param>
public sealed record NhAssistantAdminAlertShadowAgentDto(
    string AgentId,
    string? AgentDisplayName,
    int WouldHaveSent);

/// <summary>
/// A change of a kind's settings (<c>PUT admin/alert-kinds/{id}</c>): the complete settings, within what
/// the kind's code allows.
/// </summary>
/// <param name="Enabled">Whether the kind raises alerts. Required.</param>
/// <param name="Delivery">The kind's default delivery; <see langword="null"/> for the code's.</param>
/// <param name="ExpectedVersion">The settings' current version, 0 or <see langword="null"/> before the first change.</param>
/// <param name="Reason">Why, at most 500 characters; kept with the settings and left out of the audit.</param>
public sealed record NhAssistantAdminAlertKindRequest(
    bool? Enabled,
    NhAssistantAdminAlertDeliveryRequest? Delivery,
    int? ExpectedVersion,
    string? Reason = null);

/// <summary>
/// The default delivery in a change of a kind's settings.
/// </summary>
/// <param name="Mode"><c>immediately</c> or <c>daily-digest</c>. Required.</param>
/// <param name="DigestTimes">For <c>daily-digest</c>: one to four distinct times of day as <c>HH:mm</c>.</param>
/// <param name="WorkdaysOnly">For <c>daily-digest</c>: the digests go out on working days only.</param>
/// <param name="ImmediatelyFrom">For <c>daily-digest</c>: <c>info</c>, <c>warning</c> or <c>urgent</c>, or
/// <see langword="null"/> for none.</param>
/// <param name="QuietHours">Quiet hours, or <see langword="null"/> for none.</param>
public sealed record NhAssistantAdminAlertDeliveryRequest(
    string? Mode,
    IReadOnlyList<string>? DigestTimes = null,
    bool? WorkdaysOnly = null,
    string? ImmediatelyFrom = null,
    NhAssistantAdminAlertQuietHoursRequest? QuietHours = null);

/// <summary>
/// Quiet hours in a change: a start and an end as <c>HH:mm</c> that differ.
/// </summary>
public sealed record NhAssistantAdminAlertQuietHoursRequest(string? Start, string? End);
