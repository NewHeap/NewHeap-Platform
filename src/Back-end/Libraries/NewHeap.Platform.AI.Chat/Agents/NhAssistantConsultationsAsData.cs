namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The ceiling in code of consultations that administrators define as data: the routes their
/// proposals may take, whether they may generate request tools, whether MCP clients may see them and
/// the highest limits of one consultation. Answering without proposals is always allowed. The
/// application's ceiling (<see cref="NhAssistantBuilder.UseConsultationsAsData"/>) applies to agents
/// created in the administration; an agent in code gets consultations as data only with its own
/// ceiling (<see cref="NhAssistantAgentRunBuilder.ConsultableAsData"/>), which applies together with
/// the application's.
/// </summary>
public sealed class NhAssistantConsultationCeiling
{
    private string[] _routes = [];

    internal NhAssistantConsultationCeiling()
    {
    }

    /// <summary>
    /// The routes of proposals data may use, ordered: values of <see cref="NhAssistantConsultationRoutes"/>.
    /// Empty allows answers only.
    /// </summary>
    public IReadOnlyList<string> Routes => _routes;

    /// <summary>
    /// Data may generate the request tool, which starts a run of the agent with the asker as initiator.
    /// </summary>
    public bool AllowRequests { get; internal set; }

    /// <summary>
    /// MCP clients may see the tools of a consultation defined as data that asks for it.
    /// </summary>
    public bool AllowMcp { get; internal set; }

    /// <summary>
    /// The most tool calls of one consultation; at most 32, the bound of <c>Limits</c> in code.
    /// </summary>
    public int MaxToolCalls { get; internal set; } = NhAssistantConsultationBounds.MaxToolCalls;

    /// <summary>
    /// The longest consultation; at most ten minutes.
    /// </summary>
    public TimeSpan MaxTimeout { get; internal set; } = NhAssistantConsultationBounds.MaxTimeout;

    /// <summary>
    /// The longest answer in characters; at most 32,000.
    /// </summary>
    public int MaxAnswerChars { get; internal set; } = NhAssistantConsultationBounds.MaxAnswerChars;

    /// <summary>
    /// The most messages of the subject thread a consulted agent reads; at most 200.
    /// </summary>
    public int MaxHistoryMessages { get; internal set; } = NhAssistantConsultationBounds.MaxHistoryMessages;

    /// <summary>
    /// Every new consultation defined as data starts off: it is not offered until an administrator
    /// switches it on. Without it a new consultation is offered once saved, unless its administrator
    /// lets it start off. Consultations that exist keep whether they are on.
    /// </summary>
    public bool StartsOff { get; internal set; }

    /// <summary>
    /// Whether the ceiling allows <paramref name="route"/>, one of <see cref="NhAssistantConsultationRoutes"/>.
    /// </summary>
    public bool Allows(string route)
    {
        return _routes.Contains(route, StringComparer.Ordinal);
    }

    internal void SetRoutes(IEnumerable<string> routes)
    {
        _routes = routes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// What both ceilings allow: the routes of both, requests and MCP only when both allow them, and
    /// the lower of each limit. New consultations start off when either ceiling says so.
    /// </summary>
    internal NhAssistantConsultationCeiling Intersect(NhAssistantConsultationCeiling other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var ceiling = new NhAssistantConsultationCeiling
        {
            AllowRequests = AllowRequests && other.AllowRequests,
            AllowMcp = AllowMcp && other.AllowMcp,
            MaxToolCalls = Math.Min(MaxToolCalls, other.MaxToolCalls),
            MaxTimeout = MaxTimeout < other.MaxTimeout ? MaxTimeout : other.MaxTimeout,
            MaxAnswerChars = Math.Min(MaxAnswerChars, other.MaxAnswerChars),
            MaxHistoryMessages = Math.Min(MaxHistoryMessages, other.MaxHistoryMessages),
            StartsOff = StartsOff || other.StartsOff
        };
        ceiling.SetRoutes(_routes.Where(other.Allows));
        return ceiling;
    }

    /// <summary>
    /// Whether this ceiling stays within <paramref name="outer"/>; the reason when it does not.
    /// </summary>
    internal string? OutsideOf(NhAssistantConsultationCeiling outer)
    {
        var route = _routes.FirstOrDefault(route => !outer.Allows(route));
        if (route is not null)
        {
            return $"route '{route}'";
        }
        if (AllowRequests && !outer.AllowRequests)
        {
            return "requests";
        }
        if (AllowMcp && !outer.AllowMcp)
        {
            return "MCP";
        }
        if (MaxToolCalls > outer.MaxToolCalls
            || MaxTimeout > outer.MaxTimeout
            || MaxAnswerChars > outer.MaxAnswerChars
            || MaxHistoryMessages > outer.MaxHistoryMessages)
        {
            return "limits";
        }

        return null;
    }
}

/// <summary>
/// Configures a ceiling of consultations defined as data; see
/// <see cref="NhAssistantBuilder.UseConsultationsAsData"/> and
/// <see cref="NhAssistantAgentRunBuilder.ConsultableAsData"/>.
/// </summary>
public sealed class NhAssistantConsultationCeilingBuilder
{
    private readonly NhAssistantConsultationCeiling _ceiling;

    internal NhAssistantConsultationCeilingBuilder(NhAssistantConsultationCeiling ceiling)
    {
        _ceiling = ceiling;
    }

    /// <summary>
    /// The routes of proposals data may use, values of <see cref="NhAssistantConsultationRoutes"/>:
    /// <see cref="NhAssistantConsultationRoutes.AskerDecides"/> (the person who asked decides in their
    /// own conversation), <see cref="NhAssistantConsultationRoutes.SubjectAskerRights"/> (the subject's
    /// approvers decide and it executes with the asker's rights, which the application's
    /// <c>INhAiPrincipalFactory</c> rebuilds) or <see cref="NhAssistantConsultationRoutes.SubjectAgentRights"/>
    /// (the subject's approvers other than the asker decide and it executes with the agent's own
    /// rights; for an agent created in the administration only with a technical account). Repeated
    /// calls add routes. Without routes data consultations only answer.
    /// </summary>
    public NhAssistantConsultationCeilingBuilder AllowRoutes(params string[] routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Length == 0 || routes.Any(route => !NhAssistantConsultationBounds.IsRoute(route)))
        {
            throw new ArgumentException(
                "Name one or more routes of NhAssistantConsultationRoutes.",
                nameof(routes));
        }

        _ceiling.SetRoutes(_ceiling.Routes.Concat(routes));
        return this;
    }

    /// <summary>
    /// Data may generate the request tool. Off by default.
    /// </summary>
    public NhAssistantConsultationCeilingBuilder AllowRequests(bool enabled = true)
    {
        _ceiling.AllowRequests = enabled;
        return this;
    }

    /// <summary>
    /// MCP clients may see the tools of a consultation defined as data that asks for it, next to chats
    /// and agents. Each change of such a consultation changes the MCP tool list; connected sessions get
    /// <c>notifications/tools/list_changed</c>. Off by default.
    /// </summary>
    public NhAssistantConsultationCeilingBuilder AllowMcp(bool enabled = true)
    {
        _ceiling.AllowMcp = enabled;
        return this;
    }

    /// <summary>
    /// The highest limits data may set, within the bounds of <c>Limits</c> and <c>History</c> in code:
    /// 0 to 32 tool calls, one second to ten minutes, 200 to 32,000 answer characters and 0 to 200
    /// history messages. Without it data may use those bounds.
    /// </summary>
    public NhAssistantConsultationCeilingBuilder MaxLimits(
        int maxToolCalls = NhAssistantConsultationBounds.MaxToolCalls,
        TimeSpan? timeout = null,
        int maxAnswerChars = NhAssistantConsultationBounds.MaxAnswerChars,
        int history = NhAssistantConsultationBounds.MaxHistoryMessages)
    {
        var maxTimeout = timeout ?? NhAssistantConsultationBounds.MaxTimeout;
        if (maxToolCalls is < 0 or > NhAssistantConsultationBounds.MaxToolCalls)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolCalls), "A consultation makes 0 to 32 tool calls.");
        }
        if (maxTimeout < NhAssistantConsultationBounds.MinTimeout || maxTimeout > NhAssistantConsultationBounds.MaxTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "A consultation takes one second to ten minutes.");
        }
        if (maxAnswerChars is < NhAssistantConsultationBounds.MinAnswerChars or > NhAssistantConsultationBounds.MaxAnswerChars)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAnswerChars), "An answer has 200 to 32,000 characters.");
        }
        if (history is < 0 or > NhAssistantConsultationBounds.MaxHistoryMessages)
        {
            throw new ArgumentOutOfRangeException(nameof(history), "A consultation reads 0 to 200 messages.");
        }

        _ceiling.MaxToolCalls = maxToolCalls;
        _ceiling.MaxTimeout = maxTimeout;
        _ceiling.MaxAnswerChars = maxAnswerChars;
        _ceiling.MaxHistoryMessages = history;
        return this;
    }

    /// <summary>
    /// Every new consultation defined as data starts off, so an administrator reviews it before it is
    /// offered and switches it on (<c>POST admin/agents/{id}/consultations/{subjectType}/state</c>).
    /// Off by default: a new consultation is offered once saved, as before, unless its request asks it
    /// to start off. Consultations that exist keep whether they are on.
    /// </summary>
    public NhAssistantConsultationCeilingBuilder StartOff(bool enabled = true)
    {
        _ceiling.StartsOff = enabled;
        return this;
    }
}

/// <summary>
/// Where a consultation comes from: <see cref="NhAssistantConsultationOptions.Source"/>, the audit's
/// <see cref="NhAssistantAuditEvent.ConsultationSource"/> and the administration.
/// </summary>
public static class NhAssistantConsultationSources
{
    /// <summary>Registered in code with <c>AllowConsultation</c>.</summary>
    public const string Code = "code";

    /// <summary>Defined as data in the administration.</summary>
    public const string Data = "data";
}

/// <summary>
/// The names of the subject roles a consultation defined as data stores and the administration sends.
/// </summary>
public static class NhAssistantSubjectRoleNames
{
    public const string Reader = "reader";

    public const string Participant = "participant";

    public const string Approver = "approver";

    public static string NameOf(NhAiSubjectRole role)
    {
        return role switch
        {
            NhAiSubjectRole.Approver => Approver,
            NhAiSubjectRole.Participant => Participant,
            _ => Reader
        };
    }

    /// <summary>
    /// Reads a role a consultation may require; <c>none</c> is no such role.
    /// </summary>
    public static bool TryParse(string? name, out NhAiSubjectRole role)
    {
        role = name switch
        {
            Approver => NhAiSubjectRole.Approver,
            Participant => NhAiSubjectRole.Participant,
            _ => NhAiSubjectRole.Reader
        };
        return name is Reader or Participant or Approver;
    }
}

/// <summary>
/// The bounds of one consultation, the same in code (<see cref="NhAssistantConsultationBuilder"/>) and
/// as data.
/// </summary>
internal static class NhAssistantConsultationBounds
{
    public const int MaxToolCalls = 32;

    public const int MinAnswerChars = 200;

    public const int MaxAnswerChars = 32_000;

    public const int MaxHistoryMessages = 200;

    public const int DefaultToolCalls = 4;

    public const int DefaultAnswerChars = 4_000;

    public const int DefaultHistoryMessages = 20;

    public static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static bool IsRoute(string? route)
    {
        return route is NhAssistantConsultationRoutes.AskerDecides
            or NhAssistantConsultationRoutes.SubjectAskerRights
            or NhAssistantConsultationRoutes.SubjectAgentRights;
    }
}
