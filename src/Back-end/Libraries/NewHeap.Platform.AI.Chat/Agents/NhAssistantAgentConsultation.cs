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
    /// Whether the consulted agent may leave deferred proposals in its subject thread. On by default.
    /// </summary>
    public bool AllowProposals { get; internal set; } = true;

    /// <summary>
    /// The role the asking person needs before the consulted agent is offered tools that change
    /// anything. The default is <see cref="NhAiSubjectRole.Participant"/>, the role that may type in
    /// the thread.
    /// </summary>
    public NhAiSubjectRole ProposalRole { get; internal set; } = NhAiSubjectRole.Participant;

    public int MaxToolCalls { get; internal set; } = 4;

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
    /// Whether the consulted agent may leave deferred proposals, and the role the asking person
    /// needs for that.
    /// </summary>
    public NhAssistantConsultationBuilder Proposals(bool allowed, NhAiSubjectRole minimumRole = NhAiSubjectRole.Participant)
    {
        if (allowed && minimumRole == NhAiSubjectRole.None)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRole), "Proposals need a subject role.");
        }

        _options.AllowProposals = allowed;
        _options.ProposalRole = minimumRole;
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
/// <param name="ProposalCount">Proposals the agent left in its thread for the subject's approvers.</param>
public sealed record NhAssistantConsultationAnswer(Guid ConsultationId, string Answer, int ProposalCount);

/// <summary>
/// The receipt of a request that started, queued or was refused for an agent run.
/// </summary>
/// <param name="Outcome">One of the trigger outcomes, for example <c>started</c> or <c>queued</c>.</param>
public sealed record NhAssistantAgentRequestReceipt(string Outcome, Guid? RunId, Guid? ThreadId);
