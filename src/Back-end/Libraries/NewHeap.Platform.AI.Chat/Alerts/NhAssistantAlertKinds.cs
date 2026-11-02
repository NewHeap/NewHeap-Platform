using System.Globalization;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// How much an alert asks for a person's attention. Urgencies are ordered: a higher urgency of the same
/// alert delivers it again.
/// </summary>
public enum NhAssistantAlertUrgency
{
    /// <summary>Good to know; nothing goes wrong when the person reads it later.</summary>
    Info = 0,

    /// <summary>Something goes wrong unless the person acts soon.</summary>
    Warning = 1,

    /// <summary>Something goes wrong unless the person acts now.</summary>
    Urgent = 2
}

/// <summary>
/// What a Web Push notification of an alert shows on a device, where other people may read it.
/// </summary>
public enum NhAssistantAlertPushContent
{
    /// <summary>Only the alert's title; the text is read in the application. The default.</summary>
    TitleOnly = 0,

    /// <summary>The alert's title and its text.</summary>
    TitleAndText = 1
}

/// <summary>
/// When an alert of a kind reaches people by default (<see cref="NhAssistantAlertDelivery"/>).
/// </summary>
public enum NhAssistantAlertDeliveryMode
{
    /// <summary>As soon as the alert is raised.</summary>
    Immediately = 0,

    /// <summary>In one digest a day, at <see cref="NhAssistantAlertDelivery.DigestTime"/>.</summary>
    DailyDigest = 1
}

/// <summary>
/// What a person may change about the alerts of a kind in their preferences.
/// </summary>
[Flags]
public enum NhAssistantAlertChoices
{
    /// <summary>Nothing: the kind's delivery applies as declared.</summary>
    None = 0,

    /// <summary>The person may stop receiving the kind. Never for a kind that is <see cref="NhAssistantAlertKind.Required"/>.</summary>
    Mute = 1,

    /// <summary>The person may choose the time of the daily digest that delivers the kind.</summary>
    DigestTime = 2,

    /// <summary>The person may choose the channels that deliver the kind, such as Web Push or the in-app notifications.</summary>
    Channels = 4,

    /// <summary>Everything a person may change.</summary>
    All = Mute | DigestTime | Channels
}

/// <summary>
/// What an administrator may change about a kind in the administration (<c>PUT admin/alert-kinds/{id}</c>),
/// within the bounds of its code; see <see cref="NhAssistantAlertKindBuilder.AdministratorMayChange"/>.
/// The kind's recipients, texts, urgency and limits stay code.
/// </summary>
[Flags]
public enum NhAssistantAlertAdminChoices
{
    /// <summary>Nothing: the kind is on and delivered as its code declares.</summary>
    None = 0,

    /// <summary>
    /// The administrator may switch the kind off, so it raises nothing. Never for a kind that is
    /// <see cref="NhAssistantAlertKind.Required"/>.
    /// </summary>
    SwitchOff = 1,

    /// <summary>
    /// The administrator may change how the kind is delivered by default: immediately or in digests,
    /// and the times of day of its digests.
    /// </summary>
    DigestTimes = 2,

    /// <summary>The administrator may change whether the kind's digests go out on working days only.</summary>
    WorkdaysOnly = 4,

    /// <summary>The administrator may change from which urgency an alert of the kind is delivered immediately.</summary>
    ImmediatelyFrom = 8,

    /// <summary>The administrator may give the kind quiet hours, in which nothing of it is delivered.</summary>
    QuietHours = 16,

    /// <summary>Everything about the kind's default delivery.</summary>
    Delivery = DigestTimes | WorkdaysOnly | ImmediatelyFrom | QuietHours,

    /// <summary>Everything an administrator may change.</summary>
    All = SwitchOff | Delivery
}

/// <summary>
/// One way to choose the recipients of an alert; see <see cref="NhAssistantAlertRecipients"/>.
/// </summary>
public enum NhAssistantAlertRecipientSelector
{
    /// <summary>The person responsible for the subject, as its resolver describes it (<see cref="NhAiSubjectInfo.ResponsibleActorId"/>).</summary>
    SubjectResponsible = 0,

    /// <summary>The people the alert names itself (<see cref="NhAssistantAlert.Recipients"/>).</summary>
    Explicit = 1,

    /// <summary>The subject's approvers, as its resolver lists its members now.</summary>
    SubjectApprovers = 2,

    /// <summary>
    /// The subject's members with at least a role, as its resolver lists them now
    /// (<see cref="NhAssistantAlertRecipients.SubjectMembers"/>).
    /// </summary>
    SubjectMembers = 3
}

/// <summary>
/// Who receives the alerts of a kind: one or more selectors combined with <see cref="Or"/>, such as
/// <c>NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit)</c>. Every
/// person a selector chooses is checked again before anything reaches them: they still exist, the
/// application's principal factory still rebuilds them and they have at least
/// <see cref="NhAiSubjectRole.Reader"/> on the alert's subject. An application that registers an
/// <see cref="INhAssistantAlertPolicy"/> decides the recipients itself.
/// </summary>
public sealed class NhAssistantAlertRecipients
{
    private NhAssistantAlertRecipients(
        IReadOnlyList<NhAssistantAlertRecipientSelector> selectors,
        NhAiSubjectRole? minimumMemberRole = null)
    {
        Selectors = selectors;
        MinimumMemberRole = minimumMemberRole;
    }

    /// <summary>The person responsible for the subject.</summary>
    public static NhAssistantAlertRecipients SubjectResponsible { get; } = new([NhAssistantAlertRecipientSelector.SubjectResponsible]);

    /// <summary>The people the alert names itself; an alert of a kind without this selector names nobody.</summary>
    public static NhAssistantAlertRecipients Explicit { get; } = new([NhAssistantAlertRecipientSelector.Explicit]);

    /// <summary>The subject's approvers.</summary>
    public static NhAssistantAlertRecipients SubjectApprovers { get; } = new([NhAssistantAlertRecipientSelector.SubjectApprovers]);

    /// <summary>
    /// The subject's members with at least <paramref name="minimumRole"/>, such as
    /// <see cref="NhAiSubjectRole.Participant"/> for the people who work on it, as its resolver lists them
    /// now. The role is checked live as well: for a kind that selects only members, a person needs it again
    /// when a delivery goes out and when they read the alert, so a member who lost the role or access gets
    /// nothing more. Combined with another selector or replaced by an <see cref="INhAssistantAlertPolicy"/>,
    /// the person may have been chosen otherwise, so the check asks <see cref="NhAiSubjectRole.Reader"/>,
    /// as it does for the other recipients.
    /// </summary>
    public static NhAssistantAlertRecipients SubjectMembers(NhAiSubjectRole minimumRole)
    {
        if (!Enum.IsDefined(minimumRole) || minimumRole < NhAiSubjectRole.Reader)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRole), "A member selector asks for at least the Reader role.");
        }

        return new NhAssistantAlertRecipients([NhAssistantAlertRecipientSelector.SubjectMembers], minimumRole);
    }

    /// <summary>The selectors, each once, in the order they were combined.</summary>
    public IReadOnlyList<NhAssistantAlertRecipientSelector> Selectors { get; }

    /// <summary>
    /// The role a member needs for <see cref="NhAssistantAlertRecipientSelector.SubjectMembers"/>, the lowest
    /// of the combined member selectors; <see langword="null"/> without one.
    /// </summary>
    public NhAiSubjectRole? MinimumMemberRole { get; }

    /// <summary>Whether <paramref name="selector"/> is one of the selectors.</summary>
    public bool Includes(NhAssistantAlertRecipientSelector selector)
    {
        return Selectors.Contains(selector);
    }

    /// <summary>Everyone either selection chooses.</summary>
    public NhAssistantAlertRecipients Or(NhAssistantAlertRecipients other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // Members with one role or members with another are the members with the lower role.
        var minimumMemberRole = MinimumMemberRole is { } mine && other.MinimumMemberRole is { } theirs
            ? (mine < theirs ? mine : theirs)
            : MinimumMemberRole ?? other.MinimumMemberRole;
        return new NhAssistantAlertRecipients(Selectors.Concat(other.Selectors).Distinct().ToArray(), minimumMemberRole);
    }

    public override string ToString()
    {
        return string.Join(" or ", Selectors.Select(selector => selector == NhAssistantAlertRecipientSelector.SubjectMembers
            ? $"{selector}({MinimumMemberRole})"
            : selector.ToString()));
    }
}

/// <summary>
/// When the alerts of a kind reach people unless a person chose otherwise: immediately, or in one
/// daily digest at <see cref="DigestTime"/> in the person's time zone, on every day or on working days
/// only, and immediately anyway from <see cref="ImmediatelyFrom"/>. The assistant's delivery reads it;
/// the alert list shows every alert at once whatever its delivery.
/// </summary>
/// <param name="Mode">Immediately or in the daily digest.</param>
/// <param name="DigestTime">The time of day of the digest, for <see cref="NhAssistantAlertDeliveryMode.DailyDigest"/>.</param>
/// <param name="WorkdaysOnly">The digest goes out on working days only.</param>
/// <param name="ImmediatelyFrom">An alert of this urgency or higher is delivered immediately instead of in the digest.</param>
public sealed record NhAssistantAlertDelivery(
    NhAssistantAlertDeliveryMode Mode,
    TimeOnly? DigestTime,
    bool WorkdaysOnly,
    NhAssistantAlertUrgency? ImmediatelyFrom)
{
    /// <summary>Delivered as soon as an alert is raised. The default of a kind.</summary>
    public static NhAssistantAlertDelivery Immediately { get; } = new(NhAssistantAlertDeliveryMode.Immediately, null, false, null);

    /// <summary>
    /// Whether an alert of <paramref name="urgency"/> is delivered immediately under this delivery.
    /// </summary>
    public bool IsImmediateFor(NhAssistantAlertUrgency urgency)
    {
        return Mode == NhAssistantAlertDeliveryMode.Immediately
            || (ImmediatelyFrom is { } from && urgency >= from);
    }
}

/// <summary>
/// Declares when the alerts of a kind reach people; see <see cref="NhAssistantAlertKindBuilder.Delivery"/>.
/// </summary>
public sealed class NhAssistantAlertDeliveryBuilder
{
    private NhAssistantAlertDeliveryMode _mode = NhAssistantAlertDeliveryMode.Immediately;
    private TimeOnly? _digestTime;
    private bool _workdaysOnly;
    private NhAssistantAlertUrgency? _immediatelyFrom;

    internal NhAssistantAlertDeliveryBuilder()
    {
    }

    /// <summary>Delivers every alert as soon as it is raised.</summary>
    public NhAssistantAlertDeliveryBuilder Immediately()
    {
        _mode = NhAssistantAlertDeliveryMode.Immediately;
        _digestTime = null;
        return this;
    }

    /// <summary>
    /// Delivers the alerts in one digest a day at <paramref name="at"/>, in the person's time zone, whole
    /// minutes only.
    /// </summary>
    public NhAssistantAlertDeliveryBuilder DailyDigest(TimeOnly at)
    {
        if (at.Second != 0 || at.Millisecond != 0 || at.Microsecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(at), "A digest time is a whole minute.");
        }

        _mode = NhAssistantAlertDeliveryMode.DailyDigest;
        _digestTime = at;
        return this;
    }

    /// <summary>Sends the daily digest on working days only. Needs <see cref="DailyDigest"/>.</summary>
    public NhAssistantAlertDeliveryBuilder WorkdaysOnly(bool enabled = true)
    {
        _workdaysOnly = enabled;
        return this;
    }

    /// <summary>
    /// Delivers an alert of <paramref name="urgency"/> or higher immediately instead of in the digest.
    /// Needs <see cref="DailyDigest"/>.
    /// </summary>
    public NhAssistantAlertDeliveryBuilder ImmediatelyFrom(NhAssistantAlertUrgency urgency)
    {
        if (!Enum.IsDefined(urgency))
        {
            throw new ArgumentOutOfRangeException(nameof(urgency));
        }

        _immediatelyFrom = urgency;
        return this;
    }

    internal NhAssistantAlertDelivery Build(string kindId)
    {
        if (_mode == NhAssistantAlertDeliveryMode.Immediately && (_workdaysOnly || _immediatelyFrom is not null))
        {
            throw new InvalidOperationException(
                $"Alert kind '{kindId}' is delivered immediately, so WorkdaysOnly and ImmediatelyFrom do not apply. Call DailyDigest first.");
        }

        return new NhAssistantAlertDelivery(_mode, _digestTime, _workdaysOnly, _immediatelyFrom);
    }
}

/// <summary>
/// One kind of alert, as <see cref="NhAssistantBuilder.AddAlertKind"/> declared it in code: how people
/// see it, whom it reaches, how it repeats and when it is delivered. The assistant reads it for every
/// alert of the kind; only delivery choices are data: a person's own, and an administrator's within
/// <see cref="AdministratorChoices"/>.
/// </summary>
public sealed class NhAssistantAlertKind
{
    internal NhAssistantAlertKind(
        string id,
        string displayNameKey,
        NhAssistantAlertUrgency urgency,
        Func<CultureInfo, IReadOnlyDictionary<string, string>, string> title,
        Func<CultureInfo, IReadOnlyDictionary<string, string>, string>? text,
        NhAssistantAlertRecipients recipients,
        TimeSpan? repeatAfter,
        NhAssistantAlertDelivery delivery,
        NhAssistantAlertChoices choices,
        bool required,
        NhAssistantAlertPushContent pushShows)
    {
        Id = id;
        DisplayNameKey = displayNameKey;
        Urgency = urgency;
        Title = title;
        Text = text;
        Recipients = recipients;
        RepeatAfter = repeatAfter;
        Delivery = delivery;
        Choices = choices;
        Required = required;
        PushShows = pushShows;
    }

    /// <summary>The kind's dash-case id, such as <c>task-due-today</c>.</summary>
    public string Id { get; }

    /// <summary>The front-end translation key of the kind's name, as people see it in their preferences.</summary>
    public string DisplayNameKey { get; }

    /// <summary>The urgency of an alert that names none itself.</summary>
    public NhAssistantAlertUrgency Urgency { get; }

    /// <summary>Who receives the kind's alerts, unless an <see cref="INhAssistantAlertPolicy"/> decides.</summary>
    public NhAssistantAlertRecipients Recipients { get; }

    /// <summary>
    /// After how long the same open alert, raised again, is delivered again; <see langword="null"/>
    /// delivers it again only when its urgency rises.
    /// </summary>
    public TimeSpan? RepeatAfter { get; }

    /// <summary>When the kind's alerts reach people unless a person chose otherwise.</summary>
    public NhAssistantAlertDelivery Delivery { get; }

    /// <summary>What a person may change about the kind in their preferences.</summary>
    public NhAssistantAlertChoices Choices { get; }

    /// <summary>People cannot mute the kind; <see cref="Choices"/> never includes <see cref="NhAssistantAlertChoices.Mute"/>.</summary>
    public bool Required { get; }

    /// <summary>What a Web Push notification of the kind shows.</summary>
    public NhAssistantAlertPushContent PushShows { get; }

    /// <summary>
    /// What an administrator may change about the kind; never <see cref="NhAssistantAlertAdminChoices.SwitchOff"/>
    /// for a kind that is <see cref="Required"/>.
    /// </summary>
    public NhAssistantAlertAdminChoices AdministratorChoices { get; internal init; } = NhAssistantAlertAdminChoices.All;

    /// <summary>
    /// A rule run that its rules finished on a subject resolves the agent's open alerts of the kind about
    /// that subject that the run did not raise again; see <see cref="NhAssistantAlertKindBuilder.ResolveWhenNotRaised"/>.
    /// </summary>
    public bool ResolveWhenNotRaised { get; internal init; }

    /// <summary>
    /// The application's check that says whether an open alert of the kind still applies when the
    /// application revalidates its subject (<see cref="INhAssistantAlerts.RevalidateAsync"/>);
    /// <see langword="null"/> leaves the kind's alerts alone.
    /// </summary>
    internal Type? CheckType { get; init; }

    /// <summary>
    /// What an agent's model fills when it raises an alert of the kind through the platform tool
    /// <c>assistant.alert.raise</c> (FB87-9); <see langword="null"/> for a kind no model may raise. See
    /// <see cref="NhAssistantAlertKindBuilder.RaisedByModel"/>.
    /// </summary>
    public NhAssistantAlertModelTemplate? ModelTemplate { get; internal init; }

    /// <summary>Writes the title of an alert in a culture from its parameters.</summary>
    internal Func<CultureInfo, IReadOnlyDictionary<string, string>, string> Title { get; }

    /// <summary>Writes the text of an alert in a culture from its parameters; <see langword="null"/> for a title only.</summary>
    internal Func<CultureInfo, IReadOnlyDictionary<string, string>, string>? Text { get; }
}

/// <summary>
/// Declares one kind of alert; see <see cref="NhAssistantBuilder.AddAlertKind"/>.
/// </summary>
public sealed class NhAssistantAlertKindBuilder
{
    private readonly string _id;
    private string? _displayNameKey;
    private NhAssistantAlertUrgency _urgency = NhAssistantAlertUrgency.Info;
    private Func<CultureInfo, IReadOnlyDictionary<string, string>, string>? _title;
    private Func<CultureInfo, IReadOnlyDictionary<string, string>, string>? _text;
    private NhAssistantAlertRecipients _recipients = NhAssistantAlertRecipients.SubjectResponsible;
    private TimeSpan? _repeatAfter;
    private NhAssistantAlertDelivery _delivery = NhAssistantAlertDelivery.Immediately;
    private NhAssistantAlertChoices _choices = NhAssistantAlertChoices.All;
    private bool _required;
    private NhAssistantAlertPushContent _pushShows = NhAssistantAlertPushContent.TitleOnly;
    private NhAssistantAlertAdminChoices _adminChoices = NhAssistantAlertAdminChoices.All;
    private bool _resolveWhenNotRaised;
    private Type? _checkType;
    private NhAssistantAlertModelTemplate? _modelTemplate;

    internal NhAssistantAlertKindBuilder(string id)
    {
        _id = id;
    }

    /// <summary>
    /// The front-end translation key of the kind's name, at most 256 characters, such as
    /// <c>app.alerts.task-due-today</c>. Required.
    /// </summary>
    public NhAssistantAlertKindBuilder DisplayName(string displayNameKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayNameKey);
        if (displayNameKey.Length > NhAssistantAlertLimits.MaxDisplayNameKeyLength || displayNameKey.Trim() != displayNameKey)
        {
            throw new ArgumentException(
                $"A display name key has at most {NhAssistantAlertLimits.MaxDisplayNameKeyLength} characters without surrounding whitespace.",
                nameof(displayNameKey));
        }

        _displayNameKey = displayNameKey;
        return this;
    }

    /// <summary>The urgency of an alert that names none itself; <see cref="NhAssistantAlertUrgency.Info"/> by default.</summary>
    public NhAssistantAlertKindBuilder Urgency(NhAssistantAlertUrgency urgency)
    {
        if (!Enum.IsDefined(urgency))
        {
            throw new ArgumentOutOfRangeException(nameof(urgency));
        }

        _urgency = urgency;
        return this;
    }

    /// <summary>
    /// Writes an alert's title in a culture from its named parameters, at most
    /// <see cref="NhAssistantAlertLimits.MaxTitleLength"/> characters. The platform asks for it with that
    /// culture current whenever it shows or delivers the alert, so each person reads it in their own
    /// language, and once when the alert is raised, which a title that fails or is empty refuses.
    /// Required.
    /// </summary>
    public NhAssistantAlertKindBuilder Title(Func<CultureInfo, IReadOnlyDictionary<string, string>, string> title)
    {
        ArgumentNullException.ThrowIfNull(title);
        _title = title;
        return this;
    }

    /// <summary>
    /// Writes an alert's text in a culture from its named parameters, at most
    /// <see cref="NhAssistantAlertLimits.MaxTextLength"/> characters, as <see cref="Title"/> does. Without
    /// it an alert has a title only.
    /// </summary>
    public NhAssistantAlertKindBuilder Text(Func<CultureInfo, IReadOnlyDictionary<string, string>, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        return this;
    }

    /// <summary>
    /// Who receives the kind's alerts; <see cref="NhAssistantAlertRecipients.SubjectResponsible"/> by
    /// default. An alert that names recipients itself needs <see cref="NhAssistantAlertRecipients.Explicit"/>.
    /// </summary>
    public NhAssistantAlertKindBuilder Recipients(NhAssistantAlertRecipients recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        _recipients = recipients;
        return this;
    }

    /// <summary>
    /// Delivers the same open alert again when it is raised again at least <paramref name="interval"/>
    /// after it was last delivered, from one minute to 365 days. Without it an alert raised again is
    /// delivered again only when its urgency rises.
    /// </summary>
    public NhAssistantAlertKindBuilder RepeatAfter(TimeSpan interval)
    {
        if (interval < TimeSpan.FromMinutes(1) || interval > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "An alert repeats after one minute to 365 days.");
        }

        _repeatAfter = interval;
        return this;
    }

    /// <summary>
    /// When the kind's alerts reach people unless a person chose otherwise; immediately by default.
    /// </summary>
    public NhAssistantAlertKindBuilder Delivery(Action<NhAssistantAlertDeliveryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NhAssistantAlertDeliveryBuilder();
        configure(builder);
        _delivery = builder.Build(_id);
        return this;
    }

    /// <summary>
    /// What a person may change about the kind; <see cref="NhAssistantAlertChoices.All"/> by default.
    /// </summary>
    public NhAssistantAlertKindBuilder PersonMayChange(NhAssistantAlertChoices choices)
    {
        if ((choices & ~NhAssistantAlertChoices.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(choices));
        }

        _choices = choices;
        return this;
    }

    /// <summary>
    /// People cannot mute the kind and administrators cannot switch it off. They keep the other choices
    /// of <see cref="PersonMayChange"/> and <see cref="AdministratorMayChange"/>.
    /// </summary>
    public NhAssistantAlertKindBuilder Required()
    {
        _required = true;
        _choices &= ~NhAssistantAlertChoices.Mute;
        _adminChoices &= ~NhAssistantAlertAdminChoices.SwitchOff;
        return this;
    }

    /// <summary>
    /// What an administrator may change about the kind in the administration; by default
    /// <see cref="NhAssistantAlertAdminChoices.All"/>: switch it off, unless it is <see cref="Required"/>,
    /// and change its default delivery. <see cref="NhAssistantAlertAdminChoices.None"/> keeps the kind as
    /// its code declares it.
    /// </summary>
    public NhAssistantAlertKindBuilder AdministratorMayChange(NhAssistantAlertAdminChoices choices)
    {
        if ((choices & ~NhAssistantAlertAdminChoices.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(choices));
        }

        _adminChoices = choices;
        return this;
    }

    /// <summary>
    /// What a Web Push notification of the kind shows; <see cref="NhAssistantAlertPushContent.TitleOnly"/>
    /// by default, because a device's lock screen may show it to others.
    /// </summary>
    public NhAssistantAlertKindBuilder PushShows(NhAssistantAlertPushContent content)
    {
        if (!Enum.IsDefined(content))
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        _pushShows = content;
        return this;
    }

    /// <summary>
    /// Resolves by omission: when an agent's rules finish a run on a subject, the agent's open alerts of
    /// the kind about that subject whose key the run did not raise again are resolved, as
    /// <see cref="NhAssistantRuleResult.ResolveAlerts"/> resolves them, so rules that raise what applies
    /// now need not resolve what no longer does. Only the run's own subject: alerts about other subjects
    /// and the whole application (<c>nh-system</c>) are resolved explicitly. A run whose rules fail, hand
    /// over to the model or a skill, or are cancelled resolves nothing by omission, and a shadow run
    /// touches only shadow alerts.
    /// </summary>
    public NhAssistantAlertKindBuilder ResolveWhenNotRaised()
    {
        _resolveWhenNotRaised = true;
        return this;
    }

    /// <summary>
    /// Declares the application's check of the kind's open alerts (FB87-10): when the application calls
    /// <see cref="INhAssistantAlerts.RevalidateAsync"/> for a subject, <typeparamref name="TCheck"/> says
    /// for each open alert of the kind about it whether it still applies or is resolved. The check is
    /// resolved from the application's services, in a scope of its own per alert. Nothing revalidates
    /// by itself: the application calls it after its change committed, as it revalidates proposals.
    /// </summary>
    public NhAssistantAlertKindBuilder Revalidate<TCheck>()
        where TCheck : class, INhAssistantAlertCheck
    {
        _checkType = typeof(TCheck);
        return this;
    }

    /// <summary>
    /// Lets the language model of an agent that lists the kind (<c>Alerts(...)</c>) raise it through the
    /// platform tool <c>assistant.alert.raise</c> (FB87-2, FB87-9), as a template with typed parameters:
    /// <paramref name="template"/> describes when the kind applies and declares each parameter as a choice,
    /// a whole number with bounds, a date or a short plain text, and which of them form the key. The model
    /// fills only those parameters: the subject is its run's own and the recipients are the kind's, and the
    /// platform derives the key from the kind, the subject and the key parameters. Write the kind's
    /// <see cref="Title"/> and <see cref="Text"/> from these parameters only. A model raises the kind only
    /// once an administrator switched it on for the agent, first in shadow; a kind without this call is
    /// never raised by a model.
    /// </summary>
    public NhAssistantAlertKindBuilder RaisedByModel(Action<NhAssistantAlertModelTemplateBuilder> template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var builder = new NhAssistantAlertModelTemplateBuilder(_id);
        template(builder);
        _modelTemplate = builder.Build();
        return this;
    }

    internal NhAssistantAlertKind Build()
    {
        if (_displayNameKey is null)
        {
            throw new InvalidOperationException($"Alert kind '{_id}' needs a DisplayName.");
        }
        if (_title is null)
        {
            throw new InvalidOperationException($"Alert kind '{_id}' needs a Title.");
        }
        if (_required && _choices.HasFlag(NhAssistantAlertChoices.Mute))
        {
            throw new InvalidOperationException(
                $"Alert kind '{_id}' is required, so people cannot mute it. Leave Mute out of PersonMayChange.");
        }
        if (_required && _adminChoices.HasFlag(NhAssistantAlertAdminChoices.SwitchOff))
        {
            throw new InvalidOperationException(
                $"Alert kind '{_id}' is required, so administrators cannot switch it off. Leave SwitchOff out of AdministratorMayChange.");
        }
        return new NhAssistantAlertKind(
            _id,
            _displayNameKey,
            _urgency,
            _title,
            _text,
            _recipients,
            _repeatAfter,
            _delivery,
            _choices,
            _required,
            _pushShows)
        {
            AdministratorChoices = _adminChoices,
            ResolveWhenNotRaised = _resolveWhenNotRaised,
            CheckType = _checkType,
            ModelTemplate = _modelTemplate
        };
    }
}
