namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// How other assistants consult an agent about subjects of one type: who may ask, what the
/// consulted agent may do and what one consultation may cost. See
/// <see cref="NhAssistantAgentRunBuilder.AllowConsultation"/>.
/// </summary>
public sealed class NhAssistantConsultationOptions
{
    private readonly List<string> _policies = [];

    internal NhAssistantConsultationOptions(string agentId, string subjectType)
    {
        AgentId = agentId;
        SubjectType = subjectType;
    }

    public string AgentId { get; }

    public string SubjectType { get; }

    /// <summary>
    /// The id of the generated tool that consults the agent and returns its answer.
    /// </summary>
    public string ConsultToolId => NhAssistantConsultationTools.ConsultToolId(AgentId, SubjectType);

    /// <summary>
    /// The id of the generated tool that starts a run of the agent, when
    /// <see cref="AllowRequests"/> is set.
    /// </summary>
    public string RequestToolId => NhAssistantConsultationTools.RequestToolId(AgentId, SubjectType);

    /// <summary>
    /// Tells the asking model what the agent knows, so it consults the agent at the right moment.
    /// </summary>
    public string? Description { get; internal set; }

    /// <summary>
    /// The authorization policies the asking person must satisfy. Without policies the assistant's
    /// access policy applies.
    /// </summary>
    public IReadOnlyList<string> Policies => _policies;

    /// <summary>
    /// The role the asking person needs on the subject. The default is <see cref="NhAiSubjectRole.Reader"/>.
    /// </summary>
    public NhAiSubjectRole MinimumRole { get; internal set; } = NhAiSubjectRole.Reader;

    /// <summary>
    /// Whether the consulted agent may propose changes at all: <see cref="AskerDecides"/> or a route at
    /// the subject (<see cref="AtSubjectRights"/>) is configured. On by default, with the route at the
    /// subject that executes with the asker's rights.
    /// </summary>
    public bool AllowProposals => AskerDecides || AtSubjectRights is not null;

    /// <summary>
    /// The role the asking person needs before the consulted agent is offered tools that change
    /// anything. The default is <see cref="NhAiSubjectRole.Participant"/>, the role that may type in
    /// the thread.
    /// </summary>
    public NhAiSubjectRole ProposalRole { get; internal set; } = NhAiSubjectRole.Participant;

    /// <summary>
    /// The person who asked decides a proposal of the consulted agent in their own conversation, when
    /// the approval policy lets them decide it and their turn can wait for them; it then executes in
    /// their own request with their own rights. Off by default.
    /// </summary>
    public bool AskerDecides { get; internal set; }

    /// <summary>
    /// Whose rights execute a proposal that waits for the subject's approvers:
    /// <see cref="NhAssistantConsultationRights.Asker"/> (the default, rebuilt for the asker after the
    /// decision), <see cref="NhAssistantConsultationRights.Agent"/> (the consulted agent's own rights,
    /// always decided by an approver other than the asker), or <see langword="null"/> when proposals
    /// never wait at the subject.
    /// </summary>
    public NhAssistantConsultationRights? AtSubjectRights { get; internal set; } = NhAssistantConsultationRights.Asker;

    /// <summary>
    /// The approval policy of the consultation's proposals where the consulted agent has no policy
    /// for the tool itself, instead of the agent's own policy; <see langword="null"/> uses the agent's
    /// policy.
    /// </summary>
    public string? ProposalPolicyId { get; internal set; }

    /// <summary>
    /// The routes of a proposal this registration allows, as the administration may switch them:
    /// one or more of <see cref="NhAssistantConsultationRoutes"/>.
    /// </summary>
    public IReadOnlyList<string> AllowedRoutes
    {
        get
        {
            var routes = new List<string>(2);
            if (AskerDecides)
            {
                routes.Add(NhAssistantConsultationRoutes.AskerDecides);
            }
            if (AtSubjectRights is { } rights)
            {
                routes.Add(rights == NhAssistantConsultationRights.Agent
                    ? NhAssistantConsultationRoutes.SubjectAgentRights
                    : NhAssistantConsultationRoutes.SubjectAskerRights);
            }

            return routes;
        }
    }

    public int MaxToolCalls { get; internal set; } = 4;

    /// <summary>
    /// The tools the consulted agent is offered in this consultation, by id or <c>prefix.*</c>
    /// selector, each covered by the agent's own selectors; <see langword="null"/> offers every tool
    /// of the agent. The selection only narrows: the rules on effects, routes and depth still apply,
    /// and a consultation tool of another agent (<c>assistant.consult.*</c>, <c>assistant.request.*</c>)
    /// is offered only when a selector names it. An empty selection offers no tools: a consultation
    /// defined as data whose every selector no longer fits the agent's selectors.
    /// </summary>
    public IReadOnlyList<string>? ToolSelectors { get; internal set; }

    /// <summary>
    /// Whether the consultation offers the consulted agent <paramref name="toolId"/>: every tool of the
    /// agent without a selection, otherwise only a tool a selector names.
    /// </summary>
    internal bool Selects(string toolId)
    {
        return ToolSelectors is not { } selectors
               || selectors.Any(selector => NhAssistantRegistrationState.Covers(selector, toolId));
    }

    /// <summary>
    /// How long one consultation may take. The asking turn's own deadline caps it.
    /// </summary>
    public TimeSpan Timeout { get; internal set; } = TimeSpan.FromSeconds(60);

    public int MaxAnswerChars { get; internal set; } = 4_000;

    /// <summary>
    /// How many recent messages of the subject thread the consulted agent reads.
    /// </summary>
    public int MaxHistoryMessages { get; internal set; } = 20;

    /// <summary>
    /// Also generates the tool that asks the agent to work on a subject in a run of its own.
    /// </summary>
    public bool AllowRequests { get; internal set; }

    /// <summary>
    /// Where the generated tools are offered. The default is chats and agents; MCP clients see the
    /// tools only when <see cref="NhAiToolExposure.Mcp"/> is added.
    /// </summary>
    public NhAiToolExposure Exposure { get; internal set; } = NhAiToolExposure.Local | NhAiToolExposure.Agent;

    /// <summary>
    /// Extra instructions the agent follows when it is consulted, next to its own instructions.
    /// </summary>
    public NhAiTextAsset? Instructions { get; internal set; }

    /// <summary>
    /// The classification of answers, which decides how the asking conversation stores and
    /// previews them.
    /// </summary>
    public NhAiDataClassification DataClassification { get; internal set; } = NhAiDataClassification.Internal;

    /// <summary>
    /// Where the consultation comes from: <see cref="NhAssistantConsultationSources.Code"/> for
    /// <c>AllowConsultation</c>, <see cref="NhAssistantConsultationSources.Data"/> for a consultation an
    /// administrator defined.
    /// </summary>
    public string Source { get; internal set; } = NhAssistantConsultationSources.Code;

    /// <summary>
    /// The version of a consultation defined as data; <see langword="null"/> for one in code.
    /// </summary>
    public int? DataVersion { get; internal set; }

    /// <summary>
    /// Whether an administrator defined the consultation as data.
    /// </summary>
    public bool IsData => string.Equals(Source, NhAssistantConsultationSources.Data, StringComparison.Ordinal);

    internal void AddPolicies(IEnumerable<string> policies)
    {
        foreach (var policy in policies)
        {
            if (!_policies.Contains(policy, StringComparer.Ordinal))
            {
                _policies.Add(policy);
            }
        }
    }
}

/// <summary>
/// Configures a consultation of one agent; see <see cref="NhAssistantAgentRunBuilder.AllowConsultation"/>.
/// </summary>
public sealed class NhAssistantConsultationBuilder
{
    private const NhAiToolExposure SupportedExposure = NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp;

    private readonly NhAssistantConsultationOptions _options;

    internal NhAssistantConsultationBuilder(NhAssistantConsultationOptions options)
    {
        _options = options;
    }

    public NhAssistantConsultationBuilder Description(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Trim().Length > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(description), "A consultation description has at most 1,000 characters.");
        }

        _options.Description = description.Trim();
        return this;
    }

    /// <summary>
    /// Requires the asking person to satisfy these authorization policies.
    /// </summary>
    public NhAssistantConsultationBuilder Policy(params string[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Length == 0 || policies.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Name at least one authorization policy.", nameof(policies));
        }

        _options.AddPolicies(policies.Select(policy => policy.Trim()));
        return this;
    }

    /// <summary>
    /// The role the asking person needs on the subject to consult the agent.
    /// </summary>
    public NhAssistantConsultationBuilder MinimumRole(NhAiSubjectRole role)
    {
        if (role == NhAiSubjectRole.None)
        {
            throw new ArgumentOutOfRangeException(nameof(role), "A consultation needs a subject role.");
        }

        _options.MinimumRole = role;
        return this;
    }

    /// <summary>
    /// Whether the consulted agent may leave deferred proposals for the subject's approvers, which
    /// execute with the asker's rights, and the role the asking person needs for that. This is
    /// <c>Proposals(p =&gt; p.MinimumRole(minimumRole).AtSubject(NhAssistantConsultationRights.Asker))</c>;
    /// <c>Proposals(false)</c> lets the consulted agent only describe a change in its answer.
    /// </summary>
    public NhAssistantConsultationBuilder Proposals(bool allowed, NhAiSubjectRole minimumRole = NhAiSubjectRole.Participant)
    {
        if (allowed && minimumRole == NhAiSubjectRole.None)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRole), "Proposals need a subject role.");
        }

        _options.AskerDecides = false;
        _options.AtSubjectRights = allowed ? NhAssistantConsultationRights.Asker : null;
        _options.ProposalPolicyId = null;
        _options.ProposalRole = minimumRole;
        return this;
    }

    /// <summary>
    /// How the consulted agent's proposals are decided and executed: by the person who asked, in
    /// their own conversation (<see cref="NhAssistantConsultationProposalBuilder.AskerDecides"/>),
    /// and otherwise at the subject (<see cref="NhAssistantConsultationProposalBuilder.AtSubject"/>).
    /// For each call that changes something the first route that applies is taken; when none applies
    /// the call is refused and the consulted agent describes the change in its answer instead.
    /// </summary>
    public NhAssistantConsultationBuilder Proposals(Action<NhAssistantConsultationProposalBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _options.AskerDecides = false;
        _options.AtSubjectRights = null;
        _options.ProposalPolicyId = null;
        _options.ProposalRole = NhAiSubjectRole.Participant;
        configure(new NhAssistantConsultationProposalBuilder(_options));
        if (!_options.AllowProposals)
        {
            throw new InvalidOperationException(
                $"The consultation of agent '{_options.AgentId}' about '{_options.SubjectType}' configures proposals without a route. Choose AskerDecides, AtSubject or both, or use Proposals(false).");
        }

        return this;
    }

    /// <summary>
    /// The bounds of one consultation: tool calls of the consulted agent, its time and the length
    /// of its answer.
    /// </summary>
    public NhAssistantConsultationBuilder Limits(int maxToolCalls = 4, TimeSpan? timeout = null, int maxAnswerChars = 4_000)
    {
        if (maxToolCalls is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolCalls), "A consultation makes 0 to 32 tool calls.");
        }
        if (timeout is { } value && (value < TimeSpan.FromSeconds(1) || value > TimeSpan.FromMinutes(10)))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "A consultation takes one second to ten minutes.");
        }
        if (maxAnswerChars is < 200 or > 32_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAnswerChars), "An answer has 200 to 32,000 characters.");
        }

        _options.MaxToolCalls = maxToolCalls;
        _options.Timeout = timeout ?? _options.Timeout;
        _options.MaxAnswerChars = maxAnswerChars;
        return this;
    }

    /// <summary>
    /// The tools the consulted agent is offered in this consultation, by id or <c>prefix.*</c>
    /// selector; each must be covered by the agent's own selectors, or startup fails, as for a skill's
    /// tools. The consulted model sees only these tools, and a call of another tool runs nothing; the
    /// rules on effects, routes and depth still apply. A consultation tool of another agent
    /// (<c>assistant.consult.*</c>, <c>assistant.request.*</c>) is offered only when a selector names
    /// it. Without <c>Tools</c> the consultation offers every tool of the agent.
    /// </summary>
    public NhAssistantConsultationBuilder Tools(params string[] toolSelectors)
    {
        ArgumentNullException.ThrowIfNull(toolSelectors);
        if (toolSelectors.Length == 0 || toolSelectors.Any(selector => !NhAssistantAgentDefinition.IsValidSelector(selector)))
        {
            throw new ArgumentException("A consultation names one or more valid tool selectors.", nameof(toolSelectors));
        }

        _options.ToolSelectors = toolSelectors.Distinct(StringComparer.Ordinal).ToArray();
        return this;
    }

    /// <summary>
    /// How many recent messages of the subject thread the consulted agent reads; zero reads none.
    /// </summary>
    public NhAssistantConsultationBuilder History(int messages)
    {
        if (messages is < 0 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(messages));
        }

        _options.MaxHistoryMessages = messages;
        return this;
    }

    /// <summary>
    /// Also generates the tool that starts a run of the agent on a subject, with the asking person
    /// as initiator. The person needs <see cref="NhAiSubjectRole.Participant"/>, as for starting the
    /// agent in its thread.
    /// </summary>
    public NhAssistantConsultationBuilder AllowRequests(bool enabled = true)
    {
        _options.AllowRequests = enabled;
        return this;
    }

    /// <summary>
    /// Where the generated tools are offered. Add <see cref="NhAiToolExposure.Mcp"/> to let MCP
    /// clients consult the agent.
    /// </summary>
    public NhAssistantConsultationBuilder Exposure(NhAiToolExposure exposure)
    {
        if (exposure == NhAiToolExposure.None || (exposure & ~SupportedExposure) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exposure), "Combine Local, Agent and Mcp.");
        }

        _options.Exposure = exposure;
        return this;
    }

    public NhAssistantConsultationBuilder Instructions(NhAiTextAsset instructions)
    {
        _options.Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        return this;
    }

    public NhAssistantConsultationBuilder DataClassification(NhAiDataClassification classification)
    {
        _options.DataClassification = classification;
        return this;
    }
}

/// <summary>
/// Whose rights execute a consultation proposal that waits for the subject's approvers.
/// </summary>
public enum NhAssistantConsultationRights
{
    /// <summary>
    /// The asking person's rights. The proposal executes in the background after the decision, so the
    /// application's <c>INhAiPrincipalFactory</c> rebuilds the asker's principal for it.
    /// </summary>
    Asker = 0,

    /// <summary>
    /// The consulted agent's own rights: its grants and, for an agent that acts with the rights of the
    /// technical account it answers to, that account's rights. The call must also be one the asker
    /// may make when it is proposed; an approver other than the asker always decides it, and no
    /// person's principal is rebuilt.
    /// </summary>
    Agent = 1
}

/// <summary>
/// Configures how the proposals of one consultation are decided; see
/// <see cref="NhAssistantConsultationBuilder.Proposals(Action{NhAssistantConsultationProposalBuilder})"/>.
/// </summary>
public sealed class NhAssistantConsultationProposalBuilder
{
    private readonly NhAssistantConsultationOptions _options;

    internal NhAssistantConsultationProposalBuilder(NhAssistantConsultationOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// The role the asking person needs on the subject before the consulted agent is offered tools
    /// that change anything. The default is <see cref="NhAiSubjectRole.Participant"/>.
    /// </summary>
    public NhAssistantConsultationProposalBuilder MinimumRole(NhAiSubjectRole role)
    {
        if (role == NhAiSubjectRole.None)
        {
            throw new ArgumentOutOfRangeException(nameof(role), "Proposals need a subject role.");
        }

        _options.ProposalRole = role;
        return this;
    }

    /// <summary>
    /// The person who asked decides the proposal in their own conversation, right away: the approval
    /// card shows in the asking chat, the asking turn waits, and an approved proposal executes in the
    /// asker's own request with the asker's own rights. It applies to a call when the approval policy
    /// lets the asker decide it (an approver rule the asker meets, no four-eyes, the tool's approve
    /// policies) and the asking turn is the asker's own turn in their own request.
    /// </summary>
    public NhAssistantConsultationProposalBuilder AskerDecides(bool enabled = true)
    {
        _options.AskerDecides = enabled;
        return this;
    }

    /// <summary>
    /// The proposal waits at the subject for its approvers, in the consulted agent's thread, when the
    /// asker does not decide it: with <see cref="NhAssistantConsultationRights.Asker"/> it executes
    /// with the asker's rights, with <see cref="NhAssistantConsultationRights.Agent"/> with the agent's
    /// own rights after an approver other than the asker decided it.
    /// </summary>
    public NhAssistantConsultationProposalBuilder AtSubject(NhAssistantConsultationRights rights)
    {
        if (!Enum.IsDefined(rights))
        {
            throw new ArgumentOutOfRangeException(nameof(rights));
        }

        _options.AtSubjectRights = rights;
        return this;
    }

    /// <summary>
    /// The approval policy of the consultation's proposals, registered with <c>AddApprovalPolicy</c>.
    /// It applies where the consulted agent has no policy for the tool itself, instead of the agent's
    /// own policy, which was written for its runs.
    /// </summary>
    public NhAssistantConsultationProposalBuilder Policy(string approvalPolicyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalPolicyId);
        _options.ProposalPolicyId = approvalPolicyId.Trim();
        return this;
    }
}

/// <summary>
/// The ids of the generated consultation tools. Select them in the asking agent's tool selectors,
/// for example <c>assistant.consult.work-preparation.*</c>.
/// </summary>
public static class NhAssistantConsultationTools
{
    public const string ConsultPrefix = "assistant.consult.";

    public const string RequestPrefix = "assistant.request.";

    public static string ConsultToolId(string agentId, string subjectType)
    {
        return ConsultPrefix + Validate(agentId, subjectType);
    }

    public static string RequestToolId(string agentId, string subjectType)
    {
        return RequestPrefix + Validate(agentId, subjectType);
    }

    internal static bool IsConsultationTool(string toolId)
    {
        return toolId.StartsWith(ConsultPrefix, StringComparison.Ordinal)
               || toolId.StartsWith(RequestPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The version of the generated tools' contract.
    /// </summary>
    internal const int ContractVersion = 1;

    private const int MaxExportNameLength = 64;

    /// <summary>
    /// The name a model or MCP client calls a generated tool by, for example
    /// <c>consult_project-reviewer_project_v1</c>, within the 64 characters model providers accept, with a
    /// stable hash suffix when it has to be shortened.
    /// </summary>
    internal static string ExportNameOf(bool request, string agentId, string subjectType)
    {
        var name = ((request ? "request_" : "consult_") + agentId + "_" + subjectType + "_v"
                    + ContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace('.', '_');
        if (name.Length <= MaxExportNameLength)
        {
            return name;
        }

        var suffix = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..12];
        return name[..(MaxExportNameLength - suffix.Length - 1)].TrimEnd('_', '-') + "_" + suffix;
    }

    /// <summary>
    /// The agent a consultation tool asks, or <see langword="null"/> for any other tool. Agent ids
    /// and subject types contain no dots, so the id splits unambiguously.
    /// </summary>
    internal static string? TargetAgentOf(string toolId)
    {
        var prefix = toolId.StartsWith(ConsultPrefix, StringComparison.Ordinal)
            ? ConsultPrefix
            : toolId.StartsWith(RequestPrefix, StringComparison.Ordinal) ? RequestPrefix : null;
        if (prefix is null)
        {
            return null;
        }

        var rest = toolId[prefix.Length..];
        var separator = rest.IndexOf('.', StringComparison.Ordinal);
        return separator > 0 ? rest[..separator] : null;
    }

    private static string Validate(string agentId, string subjectType)
    {
        NhAssistantNames.ValidateSegment(agentId, nameof(agentId));
        if (!NhAiSubjectRef.IsValidType(subjectType))
        {
            throw new ArgumentException("A subject type is a dash-case identifier.", nameof(subjectType));
        }

        return agentId + "." + subjectType;
    }
}

/// <summary>
/// What a consulted agent answered.
/// </summary>
/// <param name="ConsultationId">The consultation record in the agent's subject thread.</param>
/// <param name="Answer">The agent's answer, bounded by the consultation's answer length.</param>
/// <param name="ProposalCount">Proposals the agent left in its thread.</param>
public sealed record NhAssistantConsultationAnswer(Guid ConsultationId, string Answer, int ProposalCount)
{
    /// <summary>
    /// Who decides what the agent proposed: <c>asker</c> when the person who asked decides it now in
    /// their own conversation, <c>subject</c> when it waits for the subject's approvers, or
    /// <see langword="null"/> when the agent proposed nothing. See
    /// <see cref="NhAssistantConsultationDecisions"/>.
    /// </summary>
    public string? Decision { get; init; }

    /// <summary>
    /// The proposals the agent left, the one the asker decides first.
    /// </summary>
    public IReadOnlyList<Guid> ApprovalIds { get; init; } = [];
}

/// <summary>
/// The values of <see cref="NhAssistantConsultationAnswer.Decision"/>.
/// </summary>
public static class NhAssistantConsultationDecisions
{
    /// <summary>The person who asked decides the proposal now, in their own conversation.</summary>
    public const string Asker = "asker";

    /// <summary>The proposals wait for the subject's approvers.</summary>
    public const string Subject = "subject";
}

/// <summary>
/// The receipt of a request that started, queued or was refused for an agent run.
/// </summary>
/// <param name="Outcome">One of the trigger outcomes, for example <c>started</c> or <c>queued</c>.</param>
public sealed record NhAssistantAgentRequestReceipt(string Outcome, Guid? RunId, Guid? ThreadId);

/// <summary>
/// The routes of an agent's consultations an administrator switched at runtime. Each
/// <see langword="null"/> route follows the code; the code's consultation registrations are the
/// ceiling, so switching on a route that no registration allows changes nothing.
/// </summary>
/// <param name="AskerDecides">The person who asked decides in their own conversation.</param>
/// <param name="SubjectAgentRights">The subject's approvers decide; it executes with the agent's own rights.</param>
/// <param name="SubjectAskerRights">The subject's approvers decide; it executes with the asker's rights.</param>
public sealed record NhAssistantConsultationRouteOverrides(
    bool? AskerDecides,
    bool? SubjectAgentRights,
    bool? SubjectAskerRights)
{
    /// <summary>Every route follows the code.</summary>
    public static NhAssistantConsultationRouteOverrides None { get; } = new(null, null, null);

    /// <summary>
    /// Whether no route is switched at runtime.
    /// </summary>
    public bool FollowsCode => AskerDecides is null && SubjectAgentRights is null && SubjectAskerRights is null;

    /// <summary>
    /// The switch of one of <see cref="NhAssistantConsultationRoutes"/>; <see langword="null"/> follows
    /// the code.
    /// </summary>
    public bool? Of(string route)
    {
        return route switch
        {
            NhAssistantConsultationRoutes.AskerDecides => AskerDecides,
            NhAssistantConsultationRoutes.SubjectAgentRights => SubjectAgentRights,
            NhAssistantConsultationRoutes.SubjectAskerRights => SubjectAskerRights,
            _ => null
        };
    }
}

/// <summary>
/// The routes of one consultation as they apply now: the code's registration within the switches
/// an administrator set for the agent.
/// </summary>
internal sealed record NhAssistantConsultationRouting(bool AskerDecides, NhAssistantConsultationRights? AtSubjectRights)
{
    public static NhAssistantConsultationRouting Of(
        NhAssistantConsultationOptions options,
        NhAssistantConsultationRouteOverrides? overrides)
    {
        ArgumentNullException.ThrowIfNull(options);
        overrides ??= NhAssistantConsultationRouteOverrides.None;
        var askerDecides = options.AskerDecides && overrides.AskerDecides != false;
        NhAssistantConsultationRights? atSubject = options.AtSubjectRights switch
        {
            NhAssistantConsultationRights.Agent when overrides.SubjectAgentRights != false => NhAssistantConsultationRights.Agent,
            NhAssistantConsultationRights.Asker when overrides.SubjectAskerRights != false => NhAssistantConsultationRights.Asker,
            _ => null
        };
        return new NhAssistantConsultationRouting(askerDecides, atSubject);
    }

    /// <summary>
    /// Whether the consulted agent may propose at all.
    /// </summary>
    public bool AllowsProposals => AskerDecides || AtSubjectRights is not null;

    /// <summary>
    /// The route at the subject, one of <see cref="NhAssistantConsultationRoutes"/>, or
    /// <see langword="null"/>.
    /// </summary>
    public string? AtSubjectRoute => AtSubjectRights switch
    {
        NhAssistantConsultationRights.Agent => NhAssistantConsultationRoutes.SubjectAgentRights,
        NhAssistantConsultationRights.Asker => NhAssistantConsultationRoutes.SubjectAskerRights,
        _ => null
    };
}
