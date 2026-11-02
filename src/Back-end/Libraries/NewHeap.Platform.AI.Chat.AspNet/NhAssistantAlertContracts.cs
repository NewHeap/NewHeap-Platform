using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// How a person sees one alert in their list (<c>GET alerts</c>), and the values of its <c>status</c>
/// filter. Every alert has exactly one of <see cref="Unread"/>, <see cref="Read"/>, <see cref="Resolved"/>,
/// <see cref="Expired"/> and <see cref="Dismissed"/>: a dismissal wins, then a closed alert, then whether
/// the person read the open alert. <see cref="Open"/> and <see cref="Closed"/> filter on two of them at once.
/// Without a status the list shows every alert the person did not dismiss.
/// </summary>
public static class NhAssistantAlertListStatuses
{
    /// <summary>The alert is open and the person has not read it; the badge counts these.</summary>
    public const string Unread = "unread";

    /// <summary>The alert is open and the person read it.</summary>
    public const string Read = "read";

    /// <summary>The alert was resolved: its reason is gone.</summary>
    public const string Resolved = "resolved";

    /// <summary>The alert's expiry passed.</summary>
    public const string Expired = "expired";

    /// <summary>The person dismissed the alert.</summary>
    public const string Dismissed = "dismissed";

    /// <summary>Filter only: the open alerts, unread or read.</summary>
    public const string Open = "open";

    /// <summary>Filter only: the closed alerts, resolved or expired.</summary>
    public const string Closed = "closed";

    /// <summary>Whether <paramref name="status"/> is a value of the <c>status</c> filter.</summary>
    public static bool IsValid(string? status)
    {
        return status is Unread or Read or Resolved or Expired or Dismissed or Open or Closed;
    }
}

/// <summary>
/// The names of <see cref="NhAssistantAlertUrgency"/> in the HTTP API: <c>info</c>, <c>warning</c> and
/// <c>urgent</c>, in the <c>urgency</c> filter and on every alert.
/// </summary>
public static class NhAssistantAlertUrgencyNames
{
    public const string Info = "info";

    public const string Warning = "warning";

    public const string Urgent = "urgent";

    /// <summary>The name of <paramref name="urgency"/>.</summary>
    public static string Of(NhAssistantAlertUrgency urgency)
    {
        return urgency switch
        {
            NhAssistantAlertUrgency.Info => Info,
            NhAssistantAlertUrgency.Warning => Warning,
            NhAssistantAlertUrgency.Urgent => Urgent,
            _ => throw new ArgumentOutOfRangeException(nameof(urgency))
        };
    }

    /// <summary>The urgency named <paramref name="name"/>; <see langword="false"/> for another name.</summary>
    public static bool TryParse(string? name, out NhAssistantAlertUrgency urgency)
    {
        switch (name)
        {
            case Info:
                urgency = NhAssistantAlertUrgency.Info;
                return true;
            case Warning:
                urgency = NhAssistantAlertUrgency.Warning;
                return true;
            case Urgent:
                urgency = NhAssistantAlertUrgency.Urgent;
                return true;
            default:
                urgency = default;
                return false;
        }
    }
}

/// <summary>
/// One alert in the caller's list (<c>GET alerts</c>): the caller's own delivery of it, with its title
/// and text written in the caller's language from its parameters. The subject is the object it is about;
/// the host opens <see cref="SubjectLink"/>, or routes the subject itself.
/// </summary>
/// <param name="Id">The alert, as <c>POST alerts/{id}/read</c> and <c>POST alerts/{id}/dismiss</c> name it.</param>
/// <param name="DeliveryId">The caller's delivery of the alert.</param>
/// <param name="KindId">The alert's kind, as <c>AddAlertKind</c> registered it.</param>
/// <param name="DisplayNameKey">The front-end translation key of the kind's name.</param>
/// <param name="Urgency">One of <see cref="NhAssistantAlertUrgencyNames"/>.</param>
/// <param name="Title">The title in the caller's language; <see langword="null"/> when the kind's title cannot be written.</param>
/// <param name="Text">The text in the caller's language; <see langword="null"/> for a kind with a title only.</param>
/// <param name="Status">One of the five states of <see cref="NhAssistantAlertListStatuses"/>.</param>
/// <param name="CreatedAt">When the alert was first raised.</param>
/// <param name="UpdatedAt">When it was last raised again; the list shows the most recent first.</param>
public sealed record NhAssistantAlertDto(
    Guid Id,
    Guid DeliveryId,
    string KindId,
    string DisplayNameKey,
    string Urgency,
    string? Title,
    string? Text,
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The name of the subject's type in the caller's language, when the application named it.</summary>
    public string? SubjectTypeName { get; init; }

    /// <summary>Where the application shows the subject; <see langword="null"/> without a link template.</summary>
    public string? SubjectLink { get; init; }

    /// <summary>The subject the alert's subject belongs to, which the grouped list groups by.</summary>
    public NhAssistantAlertParentDto? Parent { get; init; }

    /// <summary>1 when the alert was raised, one more for every update in place.</summary>
    public int Revision { get; init; } = 1;

    /// <summary>When the alert expires; <see langword="null"/> without an expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the alert was resolved or expired; <see langword="null"/> while it is open.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>The caller read the open alert (<see cref="NhAssistantAlertListStatuses.Read"/>), or read it before it closed.</summary>
    public bool IsRead { get; init; }

    public DateTimeOffset? ReadAt { get; init; }

    /// <summary>The caller dismissed the alert.</summary>
    public bool IsDismissed { get; init; }

    public DateTimeOffset? DismissedAt { get; init; }

    /// <summary>The alert was resolved; the list shows it as such until the caller dismisses it.</summary>
    public bool IsResolved { get; init; }

    /// <summary>The alert's expiry passed; the list shows it as such until the caller dismisses it.</summary>
    public bool IsExpired { get; init; }

    /// <summary>
    /// The delivery was due but reached none of the caller's channels (<c>withheld</c>), for example because
    /// the caller muted the kind; the alert is in the list all the same. A muted one is not counted by
    /// <c>GET alerts/summary</c>.
    /// </summary>
    public bool IsWithheld { get; init; }

    /// <summary>Why the delivery was withheld, one of <see cref="NhAssistantAlertWithholdReasons"/>; <see langword="null"/> otherwise.</summary>
    public string? WithheldReason { get; init; }
}

/// <summary>
/// The subject an alert's subject belongs to, as its resolver named it when the alert was last raised.
/// </summary>
public sealed record NhAssistantAlertParentDto(
    string SubjectType,
    string SubjectId,
    string? DisplayName)
{
    /// <summary>The name of the parent's type in the caller's language, when the application named the type.</summary>
    public string? SubjectTypeName { get; init; }

    /// <summary>Where the application shows the parent; <see langword="null"/> without a link template.</summary>
    public string? SubjectLink { get; init; }
}

/// <summary>
/// A page of the caller's alerts (<c>GET alerts</c>), the most recently raised first, or with
/// <c>grouped=true</c> per parent subject.
/// </summary>
/// <param name="Total">How many alerts the list shows with the filter, at most <see cref="NhAssistantAlertListDto.MaxListed"/>.</param>
public sealed record NhAssistantAlertListDto(
    IReadOnlyList<NhAssistantAlertDto> Items,
    int Total)
{
    /// <summary>
    /// The most alerts one list reads: the newest that the filter selects. Older ones show once newer
    /// ones are dismissed or purged.
    /// </summary>
    public const int MaxListed = 500;

    /// <summary>The status the list filters on; <see langword="null"/> for every alert the caller did not dismiss.</summary>
    public string? Status { get; init; }

    /// <summary>
    /// With <c>grouped=true</c>, the groups on this page in order, each with its subjects. The items are
    /// contiguous per group and subject: the first <see cref="NhAssistantAlertGroupDto.Count"/> items
    /// belong to the first group, and so on. <see langword="null"/> for the flat list.
    /// </summary>
    public IReadOnlyList<NhAssistantAlertGroupDto>? Groups { get; init; }
}

/// <summary>
/// One group on a page of the grouped alert list: the parent the alerts' subjects belong to, or the
/// subject of alerts without a parent, which holds that subject's own alerts and those of its children.
/// Groups come in the order of their most recent alert; a group's own subject first.
/// </summary>
/// <param name="Count">How many of the page's items belong to the group.</param>
/// <param name="Total">How many alerts of the list belong to the group.</param>
public sealed record NhAssistantAlertGroupDto(
    string SubjectType,
    string SubjectId,
    string? DisplayName,
    int Count,
    int Total,
    bool ContinuesFromPreviousPage,
    bool ContinuesOnNextPage,
    IReadOnlyList<NhAssistantAlertSubjectGroupDto> Subjects)
{
    /// <summary>The name of the group's subject type in the caller's language, when the application named it.</summary>
    public string? SubjectTypeName { get; init; }

    /// <summary>Where the application shows the group's subject; <see langword="null"/> without a link template.</summary>
    public string? SubjectLink { get; init; }
}

/// <summary>
/// One subject within a group on a page of the grouped alert list. Its name and link are those of its items.
/// </summary>
public sealed record NhAssistantAlertSubjectGroupDto(
    string SubjectType,
    string SubjectId,
    int Count,
    int Total,
    bool ContinuesFromPreviousPage,
    bool ContinuesOnNextPage);

/// <summary>
/// The counts of a badge (<c>GET alerts/summary</c>): the caller's unread open alerts, in total and per
/// urgency, with the visibility of the list.
/// </summary>
public sealed record NhAssistantAlertSummaryDto(
    int Unread,
    NhAssistantAlertUrgencyCountsDto UnreadByUrgency);

/// <summary>
/// A count per urgency.
/// </summary>
public sealed record NhAssistantAlertUrgencyCountsDto(int Info, int Warning, int Urgent);

/// <summary>
/// Source-generated JSON contract of the alert endpoints: web (camelCase) names, nulls written.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NhAssistantAlertDto))]
[JsonSerializable(typeof(NhAssistantAlertListDto))]
[JsonSerializable(typeof(NhAssistantAlertSummaryDto))]
public sealed partial class NhAssistantAlertJsonSerializerContext : JsonSerializerContext;
