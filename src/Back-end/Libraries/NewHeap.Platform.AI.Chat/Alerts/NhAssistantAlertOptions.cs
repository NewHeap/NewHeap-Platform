namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The delivery of alerts (FB87), bound from <c>NewHeap:AI:Assistant:Alerts</c> and changed in code with
/// <see cref="NhAssistantBuilder.ConfigureAlerts"/>. Read for every delivery, so a reloaded configuration
/// applies from the next one; the host refuses to start with a time zone it does not know.
/// </summary>
public sealed class NhAssistantAlertOptions
{
    /// <summary>The longest <see cref="ListUrl"/>.</summary>
    public const int MaxListUrlLength = 512;

    /// <summary>
    /// The application's time zone for alerts, an IANA or Windows id such as <c>Europe/Amsterdam</c>:
    /// the daily digest and working days count in it for every person whose own time zone the
    /// application does not give through <see cref="INhAssistantPersonTimeZones"/>.
    /// <see langword="null"/> uses the time zone of <see cref="NhAssistantBuilder.UseTimeZone"/>, which is
    /// UTC by default.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// The host's alert list, which an in-app notification of alerts links to and which shows every
    /// alert of a person: a path within the application such as <c>/alerts</c>, or an absolute
    /// <c>https</c> URL, at most <see cref="MaxListUrlLength"/> characters. <see langword="null"/> leaves
    /// the in-app notifications without a link. The push worker opens the list it was given in the
    /// browser (<c>push.alertsUrl</c> of <c>provideNhAssistant</c>).
    /// </summary>
    public string? ListUrl { get; set; }

    /// <summary>
    /// Throws when a configured value is invalid: a time zone the host does not know or a link that is
    /// neither a path within the application nor an <c>https</c> URL.
    /// </summary>
    internal void Validate()
    {
        if (TimeZone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _))
        {
            throw new InvalidOperationException(
                $"NewHeap:AI:Assistant:Alerts:TimeZone '{TimeZone}' is no time zone this host knows. Use an IANA or Windows id such as Europe/Amsterdam.");
        }
        if (ListUrl is not null && !IsValidListUrl(ListUrl))
        {
            throw new InvalidOperationException(
                $"NewHeap:AI:Assistant:Alerts:ListUrl must be a path within the application, such as /alerts, or an https URL of at most {MaxListUrlLength} characters.");
        }
    }

    /// <summary>
    /// The time zone of <see cref="TimeZone"/>, or <paramref name="fallback"/> when it is not set.
    /// </summary>
    internal TimeZoneInfo TimeZoneOr(TimeZoneInfo fallback)
    {
        return TimeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone)
            ? zone
            : fallback;
    }

    private static bool IsValidListUrl(string url)
    {
        if (url.Length is 0 or > MaxListUrlLength || url.Trim() != url || url.Any(char.IsControl))
        {
            return false;
        }
        if (url.StartsWith('/'))
        {
            // A path within the application; "//host" would leave it.
            return !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttps;
    }
}

/// <summary>
/// Gives a person's own time zone, which the application keeps in its profiles, so a person elsewhere
/// receives the daily digest of alerts at its time of day where they are and working days count there
/// (FB87-4). Register it with <see cref="NhAssistantBuilder.UsePersonTimeZones{TTimeZones}"/>; without it,
/// or for a person it does not know, the application's time zone for alerts applies
/// (<see cref="NhAssistantAlertOptions.TimeZone"/>).
/// </summary>
public interface INhAssistantPersonTimeZones
{
    /// <summary>
    /// The IANA or Windows id of the time zone of <paramref name="actorId"/>, such as
    /// <c>America/New_York</c>, or <see langword="null"/> when the application does not know it. An id the
    /// host does not know, or a lookup that throws, counts as unknown and is logged without the person.
    /// </summary>
    ValueTask<string?> FindTimeZoneIdAsync(string actorId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Delivers alerts through a channel of the application, such as e-mail or a chat tool, next to Web Push
/// and the in-app notifications. Register channels with <see cref="NhAssistantBuilder.AddAlertChannel{TChannel}"/>.
/// The assistant calls every channel for every notification of alerts that goes out: one alert on its own,
/// or one digest per person and digest time. It never calls a channel for a person who no longer reads
/// the alert's subject or who muted its kind; a person chooses only Web Push and the in-app notifications.
/// A channel that fails or throws never stops the other channels; when every channel fails, the
/// delivery is tried again later.
/// </summary>
public interface INhAssistantAlertChannel
{
    /// <summary>
    /// The channel's dash-case id of at most 32 characters, such as <c>e-mail</c>, recorded with every
    /// delivery it sent. <c>web-push</c> and <c>in-app</c> are the assistant's own.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Delivers <paramref name="message"/> to its recipient. Return a failed result for a notification
    /// that did not go out; the assistant logs its code only.
    /// </summary>
    Task<NewHeap.Platform.Common.Models.TaskResult> DeliverAsync(
        NhAssistantAlertMessage message,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One notification of alerts to one person: a single alert, or the digest of a person's alerts at one
/// digest time. The texts are written in the person's culture from the alerts' parameters; they are the
/// application's data, so a channel keeps them out of logs.
/// </summary>
/// <param name="RecipientActorId">The person.</param>
/// <param name="DigestId">The digest; <see langword="null"/> for an alert delivered on its own.</param>
/// <param name="Culture">The culture the texts are written in.</param>
/// <param name="Title">The alert's title, or for a digest its counts per urgency, such as "3 alerts, 1 urgent".</param>
/// <param name="Urgency">The highest urgency of the alerts.</param>
/// <param name="Alerts">The alerts, most urgent first, at most <see cref="NhAssistantLimits.MaxAlertDigestLines"/>.</param>
/// <param name="MoreCount">How many alerts of the digest <paramref name="Alerts"/> leaves out.</param>
public sealed record NhAssistantAlertMessage(
    string RecipientActorId,
    Guid? DigestId,
    System.Globalization.CultureInfo Culture,
    string Title,
    NhAssistantAlertUrgency Urgency,
    IReadOnlyList<NhAssistantAlertMessageItem> Alerts,
    int MoreCount)
{
    /// <summary>The host's alert list (<see cref="NhAssistantAlertOptions.ListUrl"/>), or <see langword="null"/>.</summary>
    public string? ListUrl { get; init; }
}

/// <summary>
/// One alert of an <see cref="NhAssistantAlertMessage"/>.
/// </summary>
/// <param name="AlertId">The alert, as the alert list names it.</param>
/// <param name="KindId">Its kind.</param>
/// <param name="Urgency">Its urgency now.</param>
/// <param name="Subject">The object it is about, which its link opens.</param>
/// <param name="SubjectDisplayName">The subject's name as its resolver last described it.</param>
/// <param name="Title">Its title in the message's culture.</param>
/// <param name="Text">Its text in the message's culture; <see langword="null"/> for a kind with a title only.</param>
public sealed record NhAssistantAlertMessageItem(
    Guid AlertId,
    string KindId,
    NhAssistantAlertUrgency Urgency,
    NhAiSubjectRef Subject,
    string? SubjectDisplayName,
    string Title,
    string? Text);
