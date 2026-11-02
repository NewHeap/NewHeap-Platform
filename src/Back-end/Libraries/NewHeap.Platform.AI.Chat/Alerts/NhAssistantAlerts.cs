using System.Text.RegularExpressions;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The bounds of alerts and their kinds.
/// </summary>
public static class NhAssistantAlertLimits
{
    /// <summary>The longest alert kind id.</summary>
    public const int MaxKindIdLength = 64;

    /// <summary>The longest display name key of a kind.</summary>
    public const int MaxDisplayNameKeyLength = 256;

    /// <summary>The longest key of an alert.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>The most parameters one alert carries.</summary>
    public const int MaxParameters = 16;

    /// <summary>The longest parameter name.</summary>
    public const int MaxParameterNameLength = 64;

    /// <summary>The longest parameter value; a longer value is shortened to it.</summary>
    public const int MaxParameterValueLength = 500;

    /// <summary>The most people one alert names itself (<see cref="NhAssistantAlert.Recipients"/>).</summary>
    public const int MaxExplicitRecipients = 50;

    /// <summary>The most people one alert reaches; recipients beyond it are left out.</summary>
    public const int MaxRecipients = 100;

    /// <summary>The longest title of an alert in one culture.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>The longest text of an alert in one culture.</summary>
    public const int MaxTextLength = 2_000;

    /// <summary>The most alerts one rule result raises or resolves together.</summary>
    public const int MaxAlertsPerResult = 200;

    /// <summary>
    /// The most open alerts one <see cref="INhAssistantAlerts.RevalidateAsync"/> checks, the oldest first,
    /// and the most alerts one rule run resolves by omission per kind.
    /// </summary>
    public const int MaxRevalidatedAlerts = 200;
}

/// <summary>
/// The stable codes of alerts, in failed results of <see cref="INhAssistantAlerts"/>, in the log of a
/// rule run and as the failure code of a rule run that raised a kind its agent does not list.
/// </summary>
public static class NhAssistantAlertCodes
{
    /// <summary>No kind with the alert's id is registered with <c>AddAlertKind</c>.</summary>
    public const string KindUnknown = "assistant-alert-kind-unknown";

    /// <summary>
    /// A rule raised or resolved a kind its agent does not list with <c>Alerts(...)</c>. The rule run
    /// fails with this code and none of its alerts are raised or resolved.
    /// </summary>
    public const string KindNotListed = "assistant-alert-kind-not-listed";

    /// <summary>The alert's subject has no resolver or does not exist.</summary>
    public const string SubjectNotFound = "assistant-alert-subject-not-found";

    /// <summary>The alert names recipients itself, but its kind does not select <see cref="NhAssistantAlertRecipients.Explicit"/>.</summary>
    public const string RecipientsNotSelected = "assistant-alert-recipients-not-selected";

    /// <summary>The kind's title could not be written for the alert's parameters, or was empty or too long.</summary>
    public const string TextInvalid = "assistant-alert-text-invalid";

    /// <summary>The alert expires at or before the moment it is raised.</summary>
    public const string Expired = "assistant-alert-expired";

    /// <summary>
    /// A limit of <see cref="NhAssistantLimits"/> refused the alert: <see cref="NhAssistantLimits.MaxAlertsPerRun"/>
    /// or <see cref="NhAssistantLimits.MaxAlertsPerSubjectPerDay"/>.
    /// </summary>
    public const string LimitReached = "assistant-alert-limit-reached";

    /// <summary>The agent of the rule run is paused or stopped, so its rules raise nothing.</summary>
    public const string AgentNotActive = "assistant-alert-agent-not-active";

    /// <summary>
    /// An administrator switched the alert's kind off (<c>PUT admin/alert-kinds/{id}</c>), so nothing of
    /// it is raised: no alert is recorded or updated and nobody is told. Resolving its alerts still works,
    /// and its waiting deliveries are withheld (<see cref="NhAssistantAlertWithholdReasons.KindOff"/>).
    /// </summary>
    public const string KindOff = "assistant-alert-kind-off";

    /// <summary>
    /// The caller has no delivery of the alert, may no longer read its subject, or the alert's kind is no
    /// longer registered: <c>POST alerts/{id}/read</c> and <c>POST alerts/{id}/dismiss</c> answer it with
    /// <c>404</c>, as for an alert that does not exist.
    /// </summary>
    public const string NotFound = "assistant-alert-not-found";

    /// <summary>
    /// The filter or paging of <c>GET alerts</c> or <c>GET alerts/summary</c> is malformed, such as an
    /// unknown status or urgency, a subject id without a subject type, or more than 20 kinds: <c>400</c>.
    /// </summary>
    public const string FilterInvalid = "assistant-alert-filter-invalid";

    /// <summary>The outcome of an alert raised for the first time, in the audit's <c>ResultCode</c>.</summary>
    public const string Raised = "raised";

    /// <summary>The outcome of an open alert raised again with other content, in the audit's <c>ResultCode</c>.</summary>
    public const string Updated = "updated";

    /// <summary>The outcome of an open alert that was delivered again, in the audit's <c>ResultCode</c>.</summary>
    public const string Redelivered = "redelivered";

    /// <summary>
    /// Why an open alert was resolved, in the audit's <c>ResultCode</c> of
    /// <see cref="NhAssistantAuditEventKind.AgentAlertResolved"/>: a rule run on its subject did not raise
    /// it again, and its kind resolves by omission (<see cref="NhAssistantAlertKind.ResolveWhenNotRaised"/>).
    /// </summary>
    public const string NotRaised = "not-raised";

    /// <summary>
    /// Why an open alert was resolved, in the audit's <c>ResultCode</c> of
    /// <see cref="NhAssistantAuditEventKind.AgentAlertResolved"/>: its kind's check found that it no longer
    /// applies when the application revalidated its subject (<see cref="INhAssistantAlerts.RevalidateAsync"/>).
    /// </summary>
    public const string Revalidated = "revalidated";
}

/// <summary>
/// The statuses of a stored alert.
/// </summary>
public static class NhAssistantAlertStatuses
{
    /// <summary>The alert is open: raising its kind, subject and key again updates it.</summary>
    public const string Open = "open";

    /// <summary>A rule or the application resolved the alert.</summary>
    public const string Resolved = "resolved";

    /// <summary>The alert's expiry passed.</summary>
    public const string Expired = "expired";
}

/// <summary>
/// The statuses of the delivery of one alert to one person.
/// </summary>
public static class NhAssistantAlertDeliveryStatuses
{
    /// <summary>The delivery waits until it is due, then the assistant delivers it.</summary>
    public const string Pending = "pending";

    /// <summary>The delivery reached the person's channels.</summary>
    public const string Sent = "sent";

    /// <summary>The person read the alert.</summary>
    public const string Read = "read";

    /// <summary>The person dismissed the alert; raising it again does not reach them until its key changes.</summary>
    public const string Dismissed = "dismissed";

    /// <summary>The alert was resolved before the delivery went out.</summary>
    public const string Resolved = "resolved";

    /// <summary>The alert expired before the delivery went out.</summary>
    public const string Expired = "expired";

    /// <summary>The person no longer qualified; <see cref="NhAssistantAlertDropReasons"/> says why.</summary>
    public const string Dropped = "dropped";

    /// <summary>
    /// The delivery was due but not sent, for example because the person muted its kind;
    /// <see cref="NhAssistantAlertWithholdReasons"/> says why. The alert stays in the person's alert list,
    /// and raising it again plans the delivery again.
    /// </summary>
    public const string Withheld = "withheld";

    /// <summary>Whether a delivery in <paramref name="status"/> still waits to go out.</summary>
    public static bool IsWaiting(string? status)
    {
        return status == Pending;
    }
}

/// <summary>
/// Why a recipient was left out of an alert, in the audit's <c>ResultCode</c> of
/// <see cref="NhAssistantAuditEventKind.AgentAlertRecipientDropped"/> and on the delivery.
/// </summary>
public static class NhAssistantAlertDropReasons
{
    /// <summary>The recipient is no person, such as an agent or an empty or too long id.</summary>
    public const string NotAPerson = "not-a-person";

    /// <summary>The application's principal factory no longer rebuilds the person, such as a deleted, blocked or locked-out account.</summary>
    public const string NotResolved = "not-resolved";

    /// <summary>The person has no <see cref="NhAiSubjectRole.Reader"/> role on the alert's subject, in its division or tenant.</summary>
    public const string NoAccess = "no-access";

    /// <summary>The alert's subject no longer exists.</summary>
    public const string SubjectGone = "subject-gone";

    /// <summary>The alert reached <see cref="NhAssistantAlertLimits.MaxRecipients"/> people before this one.</summary>
    public const string RecipientLimit = "recipient-limit";

    /// <summary>
    /// The person reads the subject, but has less than the role the kind's members selector asks for
    /// (<see cref="NhAssistantAlertRecipients.SubjectMembers"/>), on a kind that selects only members.
    /// </summary>
    public const string BelowMinimumRole = "below-minimum-role";
}

/// <summary>
/// Why a delivery that was due went out to none of the person's channels
/// (<see cref="NhAssistantAlertDeliveryStatuses.Withheld"/>), on the delivery.
/// </summary>
public static class NhAssistantAlertWithholdReasons
{
    /// <summary>The person muted the alert's kind in their preferences.</summary>
    public const string Muted = "muted";

    /// <summary>The person received <see cref="NhAssistantLimits.MaxAlertsPerPersonPerDay"/> alerts that day.</summary>
    public const string LimitReached = "limit-reached";

    /// <summary>The alert's kind is no longer registered, so it has no text.</summary>
    public const string KindUnknown = "kind-unknown";

    /// <summary>The kind's title could not be written in the person's culture.</summary>
    public const string TextInvalid = "text-invalid";

    /// <summary>An administrator switched the alert's kind off before the delivery went out.</summary>
    public const string KindOff = "kind-off";
}

/// <summary>
/// An alert to raise: a warning of one kind about one subject for the people its kind chooses. It is
/// no proposal: nothing is approved and nothing changes. The same kind, subject and <see cref="Key"/>
/// identify one open alert, which raising them again updates in place. Raise alerts from a rule with
/// <see cref="NhAssistantRuleResult.WithAlerts"/> or from the application's code with
/// <see cref="INhAssistantAlerts.RaiseAsync"/>. A copy with other values (<c>alert with { Urgency = ... }</c>)
/// is checked as the original is; its text form names the kind and subject only, never the parameters.
/// </summary>
public sealed record NhAssistantAlert
{
    private static readonly IReadOnlyDictionary<string, string> NoParameters = new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly string _key = string.Empty;
    private readonly NhAssistantAlertUrgency? _urgency;
    private readonly IReadOnlyDictionary<string, string> _parameters = NoParameters;
    private readonly IReadOnlyList<string> _recipients = [];

    /// <param name="kindId">A kind registered with <c>AddAlertKind</c>.</param>
    /// <param name="subject">The object the alert is about; the people it reaches need at least
    /// <see cref="NhAiSubjectRole.Reader"/> on it, and its link opens it.</param>
    public NhAssistantAlert(string kindId, NhAiSubjectRef subject)
    {
        NhAssistantAlertKeys.ValidateKindId(kindId, nameof(kindId));
        ArgumentNullException.ThrowIfNull(subject);
        KindId = kindId;
        Subject = subject;
    }

    public string KindId { get; }

    public NhAiSubjectRef Subject { get; }

    /// <summary>
    /// Tells alerts of the same kind and subject apart, such as the due date the alert is about, at
    /// most <see cref="NhAssistantAlertLimits.MaxKeyLength"/> printable characters; empty by default, for
    /// one alert of the kind per subject. A person who dismissed the alert does not get it again until
    /// the key changes.
    /// </summary>
    public string Key
    {
        get => _key;
        init
        {
            NhAssistantAlertKeys.ValidateKey(value, nameof(Key));
            _key = value;
        }
    }

    /// <summary>The alert's urgency; <see langword="null"/> uses the kind's.</summary>
    public NhAssistantAlertUrgency? Urgency
    {
        get => _urgency;
        init
        {
            if (value is { } urgency && !Enum.IsDefined(urgency))
            {
                throw new ArgumentOutOfRangeException(nameof(Urgency));
            }

            _urgency = value;
        }
    }

    /// <summary>
    /// The named values the kind's title and text are written from, at most
    /// <see cref="NhAssistantAlertLimits.MaxParameters"/>, each name a letter followed by letters, digits,
    /// <c>-</c> or <c>_</c>. A value longer than <see cref="NhAssistantAlertLimits.MaxParameterValueLength"/>
    /// is shortened. They are the application's data: the alert's store keeps them until the alert is
    /// purged, and the audit never carries them.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters
    {
        get => _parameters;
        init => _parameters = NhAssistantAlertKeys.NormalizeParameters(value, nameof(Parameters));
    }

    /// <summary>
    /// The people the alert names itself, by actor id, at most
    /// <see cref="NhAssistantAlertLimits.MaxExplicitRecipients"/>. Needs a kind that selects
    /// <see cref="NhAssistantAlertRecipients.Explicit"/>.
    /// </summary>
    public IReadOnlyList<string> Recipients
    {
        get => _recipients;
        init => _recipients = NhAssistantAlertKeys.NormalizeRecipients(value, nameof(Recipients));
    }

    /// <summary>
    /// When the alert expires on the application's clock, after which it is closed as expired and its
    /// deliveries that did not go out are taken back; <see langword="null"/> while it is not resolved.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Writes the kind and subject only: the parameters, key and recipients are the application's data
    /// and stay out of logs.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("KindId = ").Append(KindId).Append(", Subject = ").Append(Subject);
        return true;
    }
}

/// <summary>
/// The open alert or alerts to resolve: those of one kind about one subject with one key, or with any
/// key when <see cref="Key"/> is <see langword="null"/>.
/// </summary>
public sealed record NhAssistantAlertKey
{
    /// <param name="kindId">A kind registered with <c>AddAlertKind</c>.</param>
    /// <param name="subject">The alerts' subject.</param>
    /// <param name="key">The alert's key, empty by default; <see langword="null"/> for any key.</param>
    public NhAssistantAlertKey(string kindId, NhAiSubjectRef subject, string? key = "")
    {
        NhAssistantAlertKeys.ValidateKindId(kindId, nameof(kindId));
        ArgumentNullException.ThrowIfNull(subject);
        if (key is not null)
        {
            NhAssistantAlertKeys.ValidateKey(key, nameof(key));
        }

        KindId = kindId;
        Subject = subject;
        Key = key;
    }

    public string KindId { get; }

    public NhAiSubjectRef Subject { get; }

    /// <summary>The alert's key; <see langword="null"/> resolves the kind's open alerts of the subject with any key.</summary>
    public string? Key { get; }
}

/// <summary>
/// What became of an alert that was raised.
/// </summary>
public enum NhAssistantAlertOutcome
{
    /// <summary>No open alert of the kind, subject and key existed; the alert was recorded and its deliveries planned.</summary>
    Raised = 0,

    /// <summary>The open alert was updated in place with the new content as its next revision, without delivering it again.</summary>
    Updated = 1,

    /// <summary>The open alert was raised again with the same content; nothing changed.</summary>
    Unchanged = 2,

    /// <summary>
    /// The open alert was updated and delivered again, because its urgency rose or the kind's
    /// <c>RepeatAfter</c> passed. People who dismissed it do not get it again.
    /// </summary>
    Redelivered = 3
}

/// <summary>
/// The receipt of an alert that was raised.
/// </summary>
/// <param name="AlertId">The open alert of the kind, subject and key.</param>
/// <param name="Revision">The alert's revision: 1 when it was raised, one more for every update.</param>
/// <param name="Shadow">The alert was raised by a shadow agent or run: recorded for evaluation, delivered to nobody.</param>
/// <param name="RecipientCount">The people the alert reaches now, after every check: those whose delivery waits, went out or was read.</param>
public sealed record NhAssistantAlertReceipt(
    Guid AlertId,
    NhAssistantAlertOutcome Outcome,
    int Revision,
    bool Shadow,
    int RecipientCount);

/// <summary>
/// Raises and resolves alerts from the application's own code, without an agent, for example when a
/// job finds work that is late, and revalidates a subject's open alerts after the application changed it.
/// The same checks, deduplication, limits and audit apply as for an alert a rule raises. Expected
/// refusals are failed results with a code of <see cref="NhAssistantAlertCodes"/>.
/// </summary>
public interface INhAssistantAlerts
{
    /// <summary>
    /// Raises <paramref name="alert"/>: records it, or updates the open alert of its kind, subject and
    /// key in place, and plans its deliveries to the people its kind chooses who still qualify.
    /// </summary>
    Task<TaskResult<NhAssistantAlertReceipt>> RaiseAsync(NhAssistantAlert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the open alerts <paramref name="key"/> names: they close, and their deliveries that did
    /// not go out are taken back. Returns how many closed; resolving what is not open succeeds with 0.
    /// </summary>
    Task<TaskResult<int>> ResolveAsync(NhAssistantAlertKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revalidates the open alerts about <paramref name="subject"/> (FB87-10): for each one of a kind that
    /// declares a check (<see cref="NhAssistantAlertKindBuilder.Revalidate{TCheck}"/>), the check says
    /// whether it still applies, and one that no longer does is resolved, as <see cref="ResolveAsync"/>
    /// resolves it. Alerts of kinds without a check stay as they are. Call it after the application's
    /// change committed, as proposals are revalidated; nothing revalidates by itself. One call checks at
    /// most <see cref="NhAssistantAlertLimits.MaxRevalidatedAlerts"/> alerts, the oldest first. A check that
    /// fails is logged without content and leaves its alert open; the others still run. Repeating a call
    /// changes nothing more.
    /// </summary>
    Task<TaskResult<NhAssistantAlertRevalidationResult>> RevalidateAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The facts the recipients of one alert are chosen from. It carries the alert's identity, its subject
/// as its resolver describes it now and the people the kind's selectors chose, never its parameters.
/// </summary>
/// <param name="Kind">The alert's kind, with its selectors.</param>
/// <param name="Subject">The subject as its resolver describes it now.</param>
/// <param name="Key">The alert's key.</param>
/// <param name="Urgency">The alert's urgency.</param>
/// <param name="ExplicitRecipients">The people the alert names itself.</param>
/// <param name="AgentId">The agent whose rules raised the alert; <see langword="null"/> for the application's code.</param>
public sealed record NhAssistantAlertRecipientContext(
    NhAssistantAlertKind Kind,
    NhAiSubjectInfo Subject,
    string Key,
    NhAssistantAlertUrgency Urgency,
    IReadOnlyList<string> ExplicitRecipients,
    string? AgentId)
{
    /// <summary>The subject's members with at least <see cref="NhAiSubjectRole.Approver"/>, as its resolver lists them now.</summary>
    public IReadOnlyList<NhAiSubjectMember> Approvers { get; init; } = [];

    /// <summary>The people the kind's selectors choose, each once, in the order of the selectors.</summary>
    public IReadOnlyList<string> Selected { get; init; } = [];
}

/// <summary>
/// Decides who receives an alert, in place of the kind's selectors, as
/// <see cref="INhAssistantAgentNotificationPolicy"/> does for agent notifications. Register it with
/// <see cref="NhAssistantBuilder.UseAlertPolicy{TPolicy}"/>; wrap <see cref="NhAssistantDefaultAlertPolicy"/>
/// to refine the selectors. Every person it returns is checked again, as the selectors' are: they still
/// exist, the principal factory rebuilds them and they have at least <see cref="NhAiSubjectRole.Reader"/>
/// on the subject. Others are left out with a content-free audit.
/// </summary>
public interface INhAssistantAlertPolicy
{
    ValueTask<IReadOnlyList<string>> SelectRecipientsAsync(
        NhAssistantAlertRecipientContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The default recipients of an alert: the people its kind's selectors choose
/// (<see cref="NhAssistantAlertRecipientContext.Selected"/>).
/// </summary>
public sealed class NhAssistantDefaultAlertPolicy : INhAssistantAlertPolicy
{
    public ValueTask<IReadOnlyList<string>> SelectRecipientsAsync(
        NhAssistantAlertRecipientContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(context.Selected);
    }
}

/// <summary>
/// The validation of alert identities, shared by alerts, keys and kinds.
/// </summary>
internal static partial class NhAssistantAlertKeys
{
    public static void ValidateKindId(string kindId, string parameterName)
    {
        if (!NhAiSubjectRef.IsValidType(kindId) || kindId.Length > NhAssistantAlertLimits.MaxKindIdLength)
        {
            throw new ArgumentException(
                $"An alert kind id is a dash-case identifier of at most {NhAssistantAlertLimits.MaxKindIdLength} characters.",
                parameterName);
        }
    }

    public static void ValidateKey(string key, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (key.Length > NhAssistantAlertLimits.MaxKeyLength
            || key.Trim() != key
            || key.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                $"An alert key has at most {NhAssistantAlertLimits.MaxKeyLength} characters, without control characters or surrounding whitespace.",
                parameterName);
        }
    }

    public static IReadOnlyDictionary<string, string> NormalizeParameters(
        IReadOnlyDictionary<string, string>? parameters,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(parameters, parameterName);
        if (parameters.Count > NhAssistantAlertLimits.MaxParameters)
        {
            throw new ArgumentException(
                $"An alert carries at most {NhAssistantAlertLimits.MaxParameters} parameters.",
                parameterName);
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            if (name is null
                || name.Length > NhAssistantAlertLimits.MaxParameterNameLength
                || !ParameterName().IsMatch(name))
            {
                throw new ArgumentException(
                    $"An alert parameter name is a letter followed by letters, digits, '-' or '_', at most {NhAssistantAlertLimits.MaxParameterNameLength} characters.",
                    parameterName);
            }
            if (value is null)
            {
                throw new ArgumentException($"Alert parameter '{name}' has no value.", parameterName);
            }

            normalized[name] = NhAssistantContent.Bound(value, NhAssistantAlertLimits.MaxParameterValueLength);
        }

        return normalized;
    }

    public static IReadOnlyList<string> NormalizeRecipients(IReadOnlyList<string>? recipients, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(recipients, parameterName);
        var normalized = new List<string>();
        foreach (var recipient in recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient) || recipient.Trim().Length > 256)
            {
                throw new ArgumentException("An alert recipient is an actor id of at most 256 characters.", parameterName);
            }

            var actorId = recipient.Trim();
            if (!normalized.Contains(actorId, StringComparer.Ordinal))
            {
                normalized.Add(actorId);
            }
        }
        if (normalized.Count > NhAssistantAlertLimits.MaxExplicitRecipients)
        {
            throw new ArgumentException(
                $"An alert names at most {NhAssistantAlertLimits.MaxExplicitRecipients} recipients itself.",
                parameterName);
        }

        return normalized;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterName();
}
