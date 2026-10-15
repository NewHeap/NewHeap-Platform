namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// What happens when a trigger fires while a run of the same agent on the same subject is
/// active or pending.
/// </summary>
public enum NhAssistantTriggerConflict
{
    /// <summary>
    /// Events within the debounce window merge into one pending run that receives every event
    /// message.
    /// </summary>
    Coalesce = 0,

    /// <summary>
    /// The new run waits until the active run ends.
    /// </summary>
    Queue = 1,

    /// <summary>
    /// The active run is cancelled and a new run starts.
    /// </summary>
    Supersede = 2,

    /// <summary>
    /// The event is dropped and the drop is recorded.
    /// </summary>
    Ignore = 3
}

/// <summary>
/// Run configuration of one agent: who answers for its runs, which tools it may use beyond the
/// rights of a person, how proposals are approved and how runs are retried.
/// </summary>
public sealed class NhAssistantAgentRunOptions
{
    private readonly List<NhAiAgentGrant> _grants = [];
    private readonly Dictionary<string, string> _toolApprovalPolicies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAssistantSkillOptions> _skills = new(StringComparer.Ordinal);

    /// <summary>
    /// The most skills one agent declares.
    /// </summary>
    public const int MaxSkills = 20;
    private readonly List<NhAssistantConsultationOptions> _consultations = [];
    private readonly List<NhAssistantHandOffOptions> _handOffs = [];

    internal NhAssistantAgentRunOptions(string agentId)
    {
        AgentId = agentId;
    }

    public string AgentId { get; }

    public NhAiAgentAccountability Accountability { get; internal set; } = NhAiAgentAccountability.Agent;

    /// <summary>
    /// The actor id of the technical account for <see cref="NhAiAgentAccountability.ServiceAccount"/>.
    /// </summary>
    public string? ServiceAccountActorId { get; internal set; }

    public IReadOnlyList<NhAiAgentGrant> Grants => _grants;

    public bool DelegateFromInitiator { get; internal set; }

    public NhAiElevatedReads ElevatedReads { get; internal set; } = NhAiElevatedReads.Restricted;

    /// <summary>
    /// The approval policy of the agent, used when no tool or trigger policy is more specific.
    /// </summary>
    public string? ApprovalPolicyId { get; internal set; }

    /// <summary>
    /// Approval policies per tool selector, more specific than <see cref="ApprovalPolicyId"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToolApprovalPolicies => _toolApprovalPolicies;

    /// <summary>
    /// Automatic retries of a run after a transient failure.
    /// </summary>
    public int RetryCount { get; internal set; } = 3;

    /// <summary>
    /// The kill switch: a disabled agent rejects new triggers and stops its active runs.
    /// </summary>
    public bool Enabled { get; internal set; } = true;

    /// <summary>
    /// Runs execute normally, but proposals are recorded as shadow proposals that nobody is
    /// notified about and that never execute.
    /// </summary>
    public bool Shadow { get; internal set; }

    public int MaxConcurrentRuns { get; internal set; } = 4;

    public int MaxRunsPerSubjectPerDay { get; internal set; } = 10;

    /// <summary>
    /// The daily model and tool call budget of the agent per scope key.
    /// </summary>
    public NhAssistantAgentBudget? DailyBudget { get; internal set; }

    /// <summary>
    /// The input and output tokens one run may use across its model calls; <see langword="null"/>
    /// for no ceiling. Consultations of the agent use it for their one turn.
    /// </summary>
    public long? MaxTokensPerRun { get; internal set; }

    /// <summary>
    /// The skills of the agent by id; empty for an agent without skills.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantSkillOptions> Skills => _skills;

    /// <summary>
    /// The model profile that chooses the skill of a run no trigger or rule routed;
    /// <see langword="null"/> without model triage.
    /// </summary>
    public string? TriageProfileName { get; internal set; }

    internal NhAssistantSkillOptions AddSkill(string skillId)
    {
        if (_skills.ContainsKey(skillId))
        {
            throw new InvalidOperationException($"Agent '{AgentId}' already has skill '{skillId}'.");
        }
        if (_skills.Count >= MaxSkills)
        {
            throw new InvalidOperationException($"An agent has at most {MaxSkills} skills.");
        }

        var skill = new NhAssistantSkillOptions(AgentId, skillId);
        _skills.Add(skillId, skill);
        return skill;
    }

    /// <summary>
    /// The approval policy of a tool in a skill: the skill's own, then the agent's.
    /// </summary>
    internal string? FindApprovalPolicy(string toolId, string? skillId)
    {
        return skillId is not null && _skills.TryGetValue(skillId, out var skill) && skill.ApprovalPolicyId is { } policy
            ? policy
            : FindApprovalPolicy(toolId);
    }

    /// <summary>
    /// The JSON schema type of the agent's subject memory, or <see langword="null"/> without memory.
    /// </summary>
    public Type? MemoryType { get; internal set; }

    /// <summary>
    /// The rule handler that runs the agent's headless runs without a model, or
    /// <see langword="null"/> when the model runs them.
    /// </summary>
    public Type? RuleHandlerType { get; internal set; }

    public int MemorySchemaVersion { get; internal set; }

    /// <summary>
    /// The subject types other assistants may consult the agent about, one entry per type.
    /// </summary>
    public IReadOnlyList<NhAssistantConsultationOptions> Consultations => _consultations;

    /// <summary>
    /// The subject types other agents may hand work on to this agent, one entry per type.
    /// </summary>
    public IReadOnlyList<NhAssistantHandOffOptions> HandOffs => _handOffs;

    internal void AddGrants(IEnumerable<NhAiAgentGrant> grants)
    {
        _grants.AddRange(grants);
    }

    internal NhAssistantConsultationOptions AddConsultation(string subjectType)
    {
        if (_consultations.Any(item => string.Equals(item.SubjectType, subjectType, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Agent '{AgentId}' already allows consultation about subject type '{subjectType}'.");
        }

        var consultation = new NhAssistantConsultationOptions(AgentId, subjectType);
        _consultations.Add(consultation);
        return consultation;
    }

    internal NhAssistantHandOffOptions AddHandOff(string subjectType)
    {
        if (_handOffs.Any(item => string.Equals(item.SubjectType, subjectType, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Agent '{AgentId}' already accepts hand-offs about subject type '{subjectType}'.");
        }

        var handOff = new NhAssistantHandOffOptions(AgentId, subjectType);
        _handOffs.Add(handOff);
        return handOff;
    }

    internal void MarkLastGrantsTriggerOnly(int count)
    {
        for (var index = _grants.Count - count; index < _grants.Count; index++)
        {
            _grants[index] = new NhAiAgentGrant(_grants[index].ToolSelector, triggerOnly: true);
        }
    }

    internal void SetToolApprovalPolicy(string toolSelector, string policyId)
    {
        _toolApprovalPolicies[toolSelector] = policyId;
    }

    /// <summary>
    /// The most specific approval policy of a tool: an exact tool id, then the longest prefix
    /// selector, then the agent policy.
    /// </summary>
    internal string? FindApprovalPolicy(string toolId)
    {
        if (_toolApprovalPolicies.TryGetValue(toolId, out var exact))
        {
            return exact;
        }

        var prefix = _toolApprovalPolicies
            .Where(item => item.Key.EndsWith(".*", StringComparison.Ordinal)
                           && toolId.StartsWith(item.Key[..^1], StringComparison.Ordinal))
            .OrderByDescending(item => item.Key.Length)
            .Select(item => item.Value)
            .FirstOrDefault();
        return prefix ?? ApprovalPolicyId;
    }
}

/// <summary>
/// The daily budget of an agent per scope key: tool calls and model tokens of its runs since the
/// start of the UTC day. The budget is checked before a run starts and settled from the run's
/// recorded usage, so the last run of a day may end above it.
/// </summary>
public sealed record NhAssistantAgentBudget(int ToolCalls, long Tokens)
{
    public static NhAssistantAgentBudget PerDay(int toolCalls, long tokens)
    {
        if (toolCalls < 1 || tokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(toolCalls), "An agent budget allows at least one call and one token.");
        }

        return new NhAssistantAgentBudget(toolCalls, tokens);
    }
}

/// <summary>
/// Configures the runs of one agent; see <see cref="NhAssistantBuilder.ConfigureAgent"/>.
/// </summary>
public sealed class NhAssistantAgentRunBuilder
{
    private readonly NhAssistantAgentRunOptions _options;
    private int _lastGrantCount;

    internal NhAssistantAgentRunBuilder(NhAssistantAgentRunOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Sets who answers for the agent's runs. The default is the agent itself.
    /// </summary>
    public NhAssistantAgentRunBuilder Accountability(NhAiAgentAccountability accountability)
    {
        if (accountability == NhAiAgentAccountability.ServiceAccount)
        {
            throw new ArgumentException(
                "Name the service account with ServiceAccount(actorId).",
                nameof(accountability));
        }

        _options.Accountability = accountability;
        _options.ServiceAccountActorId = null;
        return this;
    }

    /// <summary>
    /// Makes a technical user account answer for the agent's runs.
    /// </summary>
    public NhAssistantAgentRunBuilder ServiceAccount(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId));
        }

        _options.Accountability = NhAiAgentAccountability.ServiceAccount;
        _options.ServiceAccountActorId = actorId.Trim();
        return this;
    }

    /// <summary>
    /// Grants tools to the agent by id or <c>prefix.*</c> selector. A grant applies only to tools
    /// that opt in with <see cref="NhAiToolAttribute.AllowAgentGrant"/>.
    /// </summary>
    public NhAssistantAgentRunBuilder Grant(params string[] toolSelectors)
    {
        ArgumentNullException.ThrowIfNull(toolSelectors);
        if (toolSelectors.Length == 0)
        {
            throw new ArgumentException("Grant at least one tool.", nameof(toolSelectors));
        }

        var grants = toolSelectors.Select(selector => new NhAiAgentGrant(selector)).ToArray();
        if (grants.Any(grant => !NhAssistantAgentDefinition.IsValidSelector(grant.ToolSelector)))
        {
            throw new ArgumentException("A tool grant is a tool id or a prefix ending in '.*'.", nameof(toolSelectors));
        }

        _options.AddGrants(grants);
        _lastGrantCount = grants.Length;
        return this;
    }

    /// <summary>
    /// Limits the grants of the previous <see cref="Grant"/> call to runs without a person
    /// chatting.
    /// </summary>
    public NhAssistantAgentRunBuilder TriggerOnly()
    {
        if (_lastGrantCount == 0)
        {
            throw new InvalidOperationException("Call Grant before TriggerOnly.");
        }

        _options.MarkLastGrantsTriggerOnly(_lastGrantCount);
        return this;
    }

    /// <summary>
    /// Lets the agent also use what its initiator may use. Off by default.
    /// </summary>
    public NhAssistantAgentRunBuilder DelegateFromInitiator(bool enabled = true)
    {
        _options.DelegateFromInitiator = enabled;
        return this;
    }

    public NhAssistantAgentRunBuilder ElevatedReads(NhAiElevatedReads elevatedReads)
    {
        _options.ElevatedReads = elevatedReads;
        return this;
    }

    /// <summary>
    /// Uses a registered approval policy for the agent's proposals.
    /// </summary>
    public NhAssistantAgentRunBuilder UseApprovalPolicy(string policyId)
    {
        ValidatePolicyId(policyId);
        _options.ApprovalPolicyId = policyId;
        return this;
    }

    /// <summary>
    /// Uses a registered approval policy for the proposals of the selected tools.
    /// </summary>
    public NhAssistantAgentRunBuilder UseApprovalPolicy(string toolSelector, string policyId)
    {
        if (!NhAssistantAgentDefinition.IsValidSelector(toolSelector))
        {
            throw new ArgumentException("A tool selector is a tool id or a prefix ending in '.*'.", nameof(toolSelector));
        }

        ValidatePolicyId(policyId);
        _options.SetToolApprovalPolicy(toolSelector, policyId);
        return this;
    }

    public NhAssistantAgentRunBuilder Retry(int retryCount)
    {
        if (retryCount is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        }

        _options.RetryCount = retryCount;
        return this;
    }

    public NhAssistantAgentRunBuilder Budget(
        NhAssistantAgentBudget perDay,
        int maxConcurrentRuns = 4,
        int maxRunsPerSubjectPerDay = 10)
    {
        ArgumentNullException.ThrowIfNull(perDay);
        if (maxConcurrentRuns < 1 || maxRunsPerSubjectPerDay < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentRuns));
        }

        _options.DailyBudget = perDay;
        _options.MaxConcurrentRuns = maxConcurrentRuns;
        _options.MaxRunsPerSubjectPerDay = maxRunsPerSubjectPerDay;
        return this;
    }

    /// <summary>
    /// Declares a skill: a part of the agent's role with its own description, instructions, tools,
    /// model profile or rules, budget, token ceiling, approval policy and state. A run works in one
    /// skill at a time, chosen by its trigger (<c>.Skill(id)</c>), the agent's rules
    /// (<c>NhAssistantRuleResult.UseSkill</c>) or model triage (<see cref="Triage"/>); the model can
    /// switch with <c>use_skill</c>. Agents without skills work as before.
    /// </summary>
    public NhAssistantAgentRunBuilder Skill(string skillId, Action<NhAssistantSkillBuilder> configure)
    {
        if (!NhAiSubjectRef.IsValidType(skillId))
        {
            throw new ArgumentException("A skill id is a dash-case identifier.", nameof(skillId));
        }
        ArgumentNullException.ThrowIfNull(configure);
        var skill = _options.AddSkill(skillId);
        configure(new NhAssistantSkillBuilder(skill));
        if (skill.Description.Length == 0)
        {
            throw new InvalidOperationException($"Skill '{skillId}' of agent '{_options.AgentId}' needs a Description.");
        }
        if (skill.RuleHandlerType is not null && skill.ProfileName is not null)
        {
            throw new InvalidOperationException(
                $"Skill '{skillId}' of agent '{_options.AgentId}' uses rules or a model profile, not both.");
        }

        return this;
    }

    /// <summary>
    /// Lets a cheap model choose the skill of a run that neither its trigger nor the agent's rules
    /// routed, or end it with nothing to do. The triage call reads the briefing, the run's events and
    /// the catalog of active skills and answers with structured output.
    /// </summary>
    public NhAssistantAgentRunBuilder Triage(Action<NhAssistantTriageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new NhAssistantTriageBuilder(_options));
        if (_options.TriageProfileName is null)
        {
            throw new InvalidOperationException($"The triage of agent '{_options.AgentId}' needs UseProfile.");
        }

        return this;
    }

    /// <summary>
    /// Stops a run once it used <paramref name="tokens"/> input and output tokens together, across
    /// every model call of the run, including a rule run's model part. The ceiling is checked before
    /// each next model call, so the last call can cross it by its own size; the run then fails with
    /// <c>assistant-run-token-limit</c> and keeps its proposals, and an approver may resume it with
    /// a new allowance. A consultation of the agent uses the same ceiling for its one turn.
    /// </summary>
    public NhAssistantAgentRunBuilder MaxTokensPerRun(long tokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tokens, 1);
        _options.MaxTokensPerRun = tokens;
        return this;
    }

    /// <summary>
    /// Gives the agent a structured working memory per subject, validated against the JSON schema
    /// of <typeparamref name="TMemory"/>.
    /// </summary>
    public NhAssistantAgentRunBuilder Memory<TMemory>(int schemaVersion)
        where TMemory : class
    {
        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        _options.MemoryType = typeof(TMemory);
        _options.MemorySchemaVersion = schemaVersion;
        return this;
    }

    /// <summary>
    /// Runs the agent's headless runs (events, schedules, webhooks, manual starts, requests and
    /// follow-ups) with application rules instead of a model: the handler proposes through the
    /// governed flow and costs no model calls, or hands the run over to the model. A person's turn
    /// in the thread and consultations always use the agent's model. A trigger opts out with
    /// <c>UseModel()</c>.
    /// </summary>
    public NhAssistantAgentRunBuilder UseRuleHandler<THandler>()
        where THandler : class, INhAssistantAgentRuleHandler
    {
        _options.RuleHandlerType = typeof(THandler);
        return this;
    }

    /// <summary>
    /// Runs the agent in shadow mode: it reads and proposes, but nobody is notified and nothing
    /// executes, so a team can evaluate it on real events.
    /// </summary>
    public NhAssistantAgentRunBuilder Shadow(bool enabled = true)
    {
        _options.Shadow = enabled;
        return this;
    }

    /// <summary>
    /// The configured kill switch. A disabled agent rejects new triggers and stops its active runs.
    /// </summary>
    public NhAssistantAgentRunBuilder Enabled(bool enabled)
    {
        _options.Enabled = enabled;
        return this;
    }

    /// <summary>
    /// Lets other assistants consult the agent about subjects of <paramref name="subjectType"/>.
    /// This generates the governed tool <c>assistant.consult.{agentId}.{subjectType}</c>, and with
    /// <see cref="NhAssistantConsultationBuilder.AllowRequests"/> also
    /// <c>assistant.request.{agentId}.{subjectType}</c>. An asking agent uses them only when its tool
    /// selectors select them. A consultation runs with the asking person's rights, never with this
    /// agent's grants, and anything that would change data becomes a deferred proposal in the
    /// agent's subject thread.
    /// </summary>
    public NhAssistantAgentRunBuilder AllowConsultation(
        string subjectType,
        Action<NhAssistantConsultationBuilder>? configure = null)
    {
        if (!NhAiSubjectRef.IsValidType(subjectType))
        {
            throw new ArgumentException("A subject type is a dash-case identifier.", nameof(subjectType));
        }

        var consultation = _options.AddConsultation(subjectType);
        configure?.Invoke(new NhAssistantConsultationBuilder(consultation));
        return this;
    }

    /// <summary>
    /// Lets the agents named with <c>From</c> hand work on subjects of <paramref name="subjectType"/>
    /// to this agent, from a model turn through the generated tool
    /// <c>assistant.handoff.{agentId}.{subjectType}</c> or from their rules. The work starts a run of
    /// this agent in its own thread, under its own grants, approval policies and budget, with no
    /// initiator; the source's message reaches it as data from another agent. Hand-offs stay within
    /// one scope key, a chain holds at most <see cref="NhAssistantLimits.MaxHandOffDepth"/> of them and
    /// never returns to an agent already in it.
    /// </summary>
    public NhAssistantAgentRunBuilder AcceptHandOffs(
        string subjectType,
        Action<NhAssistantHandOffBuilder> configure)
    {
        if (!NhAiSubjectRef.IsValidType(subjectType))
        {
            throw new ArgumentException("A subject type is a dash-case identifier.", nameof(subjectType));
        }
        ArgumentNullException.ThrowIfNull(configure);

        var handOff = _options.AddHandOff(subjectType);
        configure(new NhAssistantHandOffBuilder(handOff));
        if (handOff.Sources.Count == 0)
        {
            throw new InvalidOperationException(
                $"Agent '{_options.AgentId}' accepts hand-offs about '{subjectType}' from no agent; name them with From.");
        }

        return this;
    }

    private static void ValidatePolicyId(string policyId)
    {
        if (!NhAiSubjectRef.IsValidType(policyId))
        {
            throw new ArgumentException("An approval policy id is a dash-case identifier.", nameof(policyId));
        }
    }
}

/// <summary>
/// Where a registered trigger comes from.
/// </summary>
internal enum NhAssistantTriggerSource
{
    Event = 0,
    Schedule = 1,
    Webhook = 2
}

/// <summary>
/// A registered trigger that turns an application event, a schedule tick or a webhook call into
/// an agent run.
/// </summary>
internal sealed class NhAssistantTriggerDefinition
{
    public required string Id { get; init; }

    public NhAssistantTriggerSource Source { get; init; } = NhAssistantTriggerSource.Event;

    /// <summary>
    /// The event type of an event trigger; <see langword="null"/> for schedules and webhooks.
    /// </summary>
    public Type? EventType { get; init; }

    public required string AgentId { get; init; }

    /// <summary>
    /// Whether the event concerns the agent at all, evaluated before every other lambda;
    /// <see langword="null"/> accepts every event.
    /// </summary>
    public Func<object, bool>? When { get; init; }

    public required Func<object, NhAiSubjectRef> Subject { get; init; }

    public Func<object, string?> Initiator { get; init; } = _ => null;

    public required Func<object, string> Message { get; init; }

    public Func<object, string?> Dedupe { get; init; } = _ => null;

    public NhAssistantTriggerConflict Conflict { get; init; } = NhAssistantTriggerConflict.Coalesce;

    public TimeSpan Debounce { get; init; } = TimeSpan.FromMinutes(2);

    public string? ApprovalPolicyId { get; init; }

    /// <summary>
    /// The skill of the agent the trigger's runs work in; <see langword="null"/> lets the agent's
    /// rules or triage choose.
    /// </summary>
    public string? SkillId { get; init; }

    /// <summary>
    /// The cron expression of a schedule, in <see cref="TimeZone"/>.
    /// </summary>
    public string? Cron { get; init; }

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>
    /// The subjects a schedule tick starts the agent for.
    /// </summary>
    public Func<IServiceProvider, CancellationToken, IAsyncEnumerable<NhAiSubjectRef>>? ScheduledSubjects { get; init; }

    public int MaxSubjectsPerTick { get; init; } = 100;

    /// <summary>
    /// The shared secret a webhook call is signed with.
    /// </summary>
    public Func<IServiceProvider, string?>? WebhookSecret { get; init; }

    /// <summary>
    /// The subject of a webhook call, or <see langword="null"/> when the call names none.
    /// </summary>
    public Func<System.Text.Json.JsonElement, NhAiSubjectRef?>? WebhookSubject { get; init; }

    /// <summary>
    /// The trigger's runs use the agent's model even when the agent has a rule handler.
    /// </summary>
    public bool UseModel { get; init; }

    /// <summary>
    /// The small typed payload stored with each event for the agent's rule handler.
    /// </summary>
    public Func<object, object?>? Payload { get; init; }
}

/// <summary>
/// One tick of a scheduled trigger for one subject, as the trigger's event.
/// </summary>
/// <param name="Tick">The scheduled time, truncated to the minute.</param>
public sealed record NhAssistantScheduledTick(NhAiSubjectRef Subject, DateTimeOffset Tick);

/// <summary>
/// One signed webhook call, as the trigger's event.
/// </summary>
/// <param name="DeliveryId">The sender's id of the delivery; a repeated delivery starts no second run.</param>
/// <param name="Body">The JSON body.</param>
public sealed record NhAssistantWebhookDelivery(string DeliveryId, System.Text.Json.JsonElement Body);

/// <summary>
/// Configures a scheduled trigger; see <see cref="NhAssistantBuilder.AddScheduledTrigger"/>.
/// </summary>
public sealed class NhAssistantScheduleBuilder
{
    private string? _agentId;
    private string? _cron;
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    private Func<IServiceProvider, CancellationToken, IAsyncEnumerable<NhAiSubjectRef>>? _subjects;
    private Func<NhAssistantScheduledTick, string>? _message;
    private int _maxSubjectsPerTick = 100;
    private NhAssistantTriggerConflict _conflict = NhAssistantTriggerConflict.Ignore;
    private string? _approvalPolicyId;
    private string? _skillId;
    private bool _useModel;

    internal NhAssistantScheduleBuilder()
    {
    }

    public NhAssistantScheduleBuilder StartAgent(string agentId)
    {
        NhAssistantNames.ValidateSegment(agentId, nameof(agentId));
        _agentId = agentId;
        return this;
    }

    /// <summary>
    /// The schedule as a five-field cron expression, for example <c>0 7 * * 1</c> for Mondays at
    /// seven, in <paramref name="timeZone"/> (UTC by default).
    /// </summary>
    public NhAssistantScheduleBuilder Cron(string expression, TimeZoneInfo? timeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is not (5 or 6))
        {
            throw new ArgumentException("A cron expression has five or six fields.", nameof(expression));
        }

        _cron = string.Join(' ', fields);
        _timeZone = timeZone ?? TimeZoneInfo.Utc;
        return this;
    }

    /// <summary>
    /// The subjects of one tick, read in a scope of their own, for example the projects with a
    /// deadline this week.
    /// </summary>
    public NhAssistantScheduleBuilder Subjects(Func<IServiceProvider, CancellationToken, IAsyncEnumerable<NhAiSubjectRef>> subjects)
    {
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        return this;
    }

    public NhAssistantScheduleBuilder Message(Func<NhAssistantScheduledTick, string> message)
    {
        _message = message ?? throw new ArgumentNullException(nameof(message));
        return this;
    }

    /// <summary>
    /// Caps the subjects of one tick; the rest wait for the next tick. The default is 100.
    /// </summary>
    public NhAssistantScheduleBuilder MaxSubjectsPerTick(int count)
    {
        if (count is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _maxSubjectsPerTick = count;
        return this;
    }

    /// <summary>
    /// What a tick does while a run on the subject is active. The default skips the tick.
    /// </summary>
    public NhAssistantScheduleBuilder OnBusy(NhAssistantTriggerConflict conflict)
    {
        _conflict = conflict;
        return this;
    }

    /// <summary>
    /// Runs this trigger's runs with the agent's model even when the agent has a rule handler.
    /// </summary>
    public NhAssistantScheduleBuilder UseModel()
    {
        _useModel = true;
        return this;
    }

    /// <summary>
    /// Runs the trigger's runs in one skill of the agent.
    /// </summary>
    public NhAssistantScheduleBuilder Skill(string skillId)
    {
        if (!NhAiSubjectRef.IsValidType(skillId))
        {
            throw new ArgumentException("A skill id is a dash-case identifier.", nameof(skillId));
        }

        _skillId = skillId;
        return this;
    }

    public NhAssistantScheduleBuilder UseApprovalPolicy(string policyId)
    {
        if (!NhAiSubjectRef.IsValidType(policyId))
        {
            throw new ArgumentException("An approval policy id is a dash-case identifier.", nameof(policyId));
        }

        _approvalPolicyId = policyId;
        return this;
    }

    internal NhAssistantTriggerDefinition Build(string id)
    {
        if (_agentId is null || _cron is null || _subjects is null || _message is null)
        {
            throw new InvalidOperationException(
                $"Scheduled trigger '{id}' needs StartAgent, Cron, Subjects and Message.");
        }

        var message = _message;
        return new NhAssistantTriggerDefinition
        {
            Id = id,
            Source = NhAssistantTriggerSource.Schedule,
            AgentId = _agentId,
            Subject = evt => ((NhAssistantScheduledTick)evt).Subject,
            Message = evt => message((NhAssistantScheduledTick)evt),
            Dedupe = evt => "tick:" + ((NhAssistantScheduledTick)evt).Tick.UtcDateTime.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture),
            Conflict = _conflict,
            Debounce = TimeSpan.Zero,
            ApprovalPolicyId = _approvalPolicyId,
            SkillId = _skillId,
            Cron = _cron,
            TimeZone = _timeZone,
            ScheduledSubjects = _subjects,
            MaxSubjectsPerTick = _maxSubjectsPerTick,
            UseModel = _useModel
        };
    }
}

/// <summary>
/// Configures a webhook trigger; see <see cref="NhAssistantBuilder.AddWebhookTrigger"/>. The
/// caller signs each call with HMAC-SHA256 over <c>{timestamp}.{deliveryId}.{body}</c> using the
/// shared secret, and sends <c>X-NH-Timestamp</c> (Unix seconds), <c>X-NH-Delivery</c> (a unique
/// delivery id of at most 100 letters, digits, <c>-</c>, <c>_</c>, <c>.</c> or <c>:</c>) and
/// <c>X-NH-Signature</c> (<c>sha256=</c> plus the lowercase hex digest). Calls more than five
/// minutes off are refused.
/// </summary>
public sealed class NhAssistantWebhookBuilder
{
    private string? _agentId;
    private Func<IServiceProvider, string?>? _secret;
    private Func<System.Text.Json.JsonElement, NhAiSubjectRef?>? _subject;
    private Func<System.Text.Json.JsonElement, string>? _message;
    private Func<System.Text.Json.JsonElement, bool>? _when;
    private NhAssistantTriggerConflict _conflict = NhAssistantTriggerConflict.Coalesce;
    private TimeSpan _debounce = TimeSpan.FromMinutes(2);
    private string? _approvalPolicyId;
    private string? _skillId;
    private bool _useModel;

    internal NhAssistantWebhookBuilder()
    {
    }

    /// <summary>
    /// Whether a verified call concerns the agent. It is evaluated before <see cref="Subject"/> and
    /// <see cref="Message"/>; a call it refuses is answered with outcome <c>ignored</c> and reason
    /// <c>filtered</c>, starts nothing and is not audited. Treat the body as untrusted.
    /// </summary>
    public NhAssistantWebhookBuilder When(Func<System.Text.Json.JsonElement, bool> filter)
    {
        _when = filter ?? throw new ArgumentNullException(nameof(filter));
        return this;
    }

    public NhAssistantWebhookBuilder StartAgent(string agentId)
    {
        NhAssistantNames.ValidateSegment(agentId, nameof(agentId));
        _agentId = agentId;
        return this;
    }

    /// <summary>
    /// The shared secret, read for every call so that it can be rotated, for example from
    /// configuration. Calls are refused while it is missing or shorter than 32 characters.
    /// </summary>
    public NhAssistantWebhookBuilder Secret(Func<IServiceProvider, string?> secret)
    {
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        return this;
    }

    /// <summary>
    /// The subject of a call, or <see langword="null"/> to refuse it. Treat the body as untrusted.
    /// </summary>
    public NhAssistantWebhookBuilder Subject(Func<System.Text.Json.JsonElement, NhAiSubjectRef?> subject)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        return this;
    }

    public NhAssistantWebhookBuilder Message(Func<System.Text.Json.JsonElement, string> message)
    {
        _message = message ?? throw new ArgumentNullException(nameof(message));
        return this;
    }

    public NhAssistantWebhookBuilder OnBusy(NhAssistantTriggerConflict conflict, TimeSpan? debounce = null)
    {
        if (debounce is { } window && (window < TimeSpan.Zero || window > TimeSpan.FromHours(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        _conflict = conflict;
        _debounce = debounce ?? _debounce;
        return this;
    }

    /// <summary>
    /// Runs this trigger's runs with the agent's model even when the agent has a rule handler.
    /// </summary>
    public NhAssistantWebhookBuilder UseModel()
    {
        _useModel = true;
        return this;
    }

    /// <summary>
    /// Runs the trigger's runs in one skill of the agent.
    /// </summary>
    public NhAssistantWebhookBuilder Skill(string skillId)
    {
        if (!NhAiSubjectRef.IsValidType(skillId))
        {
            throw new ArgumentException("A skill id is a dash-case identifier.", nameof(skillId));
        }

        _skillId = skillId;
        return this;
    }

    public NhAssistantWebhookBuilder UseApprovalPolicy(string policyId)
    {
        if (!NhAiSubjectRef.IsValidType(policyId))
        {
            throw new ArgumentException("An approval policy id is a dash-case identifier.", nameof(policyId));
        }

        _approvalPolicyId = policyId;
        return this;
    }

    internal NhAssistantTriggerDefinition Build(string id)
    {
        if (_agentId is null || _secret is null || _subject is null || _message is null)
        {
            throw new InvalidOperationException(
                $"Webhook trigger '{id}' needs StartAgent, Secret, Subject and Message.");
        }

        var subject = _subject;
        var message = _message;
        var when = _when;
        return new NhAssistantTriggerDefinition
        {
            Id = id,
            Source = NhAssistantTriggerSource.Webhook,
            AgentId = _agentId,
            When = when is null ? null : evt => when(((NhAssistantWebhookDelivery)evt).Body),
            Subject = evt => subject(((NhAssistantWebhookDelivery)evt).Body)
                ?? throw new InvalidOperationException("The webhook call names no subject."),
            Message = evt => message(((NhAssistantWebhookDelivery)evt).Body),
            Dedupe = evt => "delivery:" + ((NhAssistantWebhookDelivery)evt).DeliveryId,
            Conflict = _conflict,
            Debounce = _debounce,
            ApprovalPolicyId = _approvalPolicyId,
            SkillId = _skillId,
            WebhookSecret = _secret,
            WebhookSubject = evt => subject(evt),
            UseModel = _useModel
        };
    }
}

/// <summary>
/// Configures a trigger; see <see cref="NhAssistantBuilder.AddTrigger{TEvent}"/>.
/// </summary>
public sealed class NhAssistantTriggerBuilder<TEvent>
    where TEvent : class
{
    private string? _agentId;
    private Func<TEvent, NhAiSubjectRef>? _subject;
    private Func<TEvent, string?> _initiator = _ => null;
    private Func<TEvent, string>? _message;
    private Func<TEvent, string?> _dedupe = _ => null;
    private Func<TEvent, bool>? _when;
    private NhAssistantTriggerConflict _conflict = NhAssistantTriggerConflict.Coalesce;
    private TimeSpan _debounce = TimeSpan.FromMinutes(2);
    private string? _approvalPolicyId;
    private string? _skillId;
    private bool _useModel;
    private Func<TEvent, object?>? _payload;

    internal NhAssistantTriggerBuilder()
    {
    }

    /// <summary>
    /// Whether the event concerns the agent at all. It is evaluated before every other lambda of
    /// the trigger, and only while the agent is active or paused; an event it refuses starts
    /// nothing, is not audited and gets a receipt with outcome <c>ignored</c> and reason
    /// <c>filtered</c>. Keep it cheap and free of I/O: it runs for every published event.
    /// </summary>
    public NhAssistantTriggerBuilder<TEvent> When(Func<TEvent, bool> filter)
    {
        _when = filter ?? throw new ArgumentNullException(nameof(filter));
        return this;
    }

    public NhAssistantTriggerBuilder<TEvent> StartAgent(string agentId)
    {
        NhAssistantNames.ValidateSegment(agentId, nameof(agentId));
        _agentId = agentId;
        return this;
    }

    public NhAssistantTriggerBuilder<TEvent> Subject(Func<TEvent, NhAiSubjectRef> subject)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        return this;
    }

    /// <summary>
    /// The person who caused the event, as an actor id, or <see langword="null"/> for an event
    /// from outside, which runs with the agent's grants only.
    /// </summary>
    public NhAssistantTriggerBuilder<TEvent> Initiator(Func<TEvent, string?> initiator)
    {
        _initiator = initiator ?? throw new ArgumentNullException(nameof(initiator));
        return this;
    }

    /// <summary>
    /// The event message the run receives as its input.
    /// </summary>
    public NhAssistantTriggerBuilder<TEvent> Message(Func<TEvent, string> message)
    {
        _message = message ?? throw new ArgumentNullException(nameof(message));
        return this;
    }

    /// <summary>
    /// A key that identifies a repeated delivery of the same event, so it starts at most one run.
    /// </summary>
    public NhAssistantTriggerBuilder<TEvent> Dedupe(Func<TEvent, string?> key)
    {
        _dedupe = key ?? throw new ArgumentNullException(nameof(key));
        return this;
    }

    public NhAssistantTriggerBuilder<TEvent> OnBusy(NhAssistantTriggerConflict conflict, TimeSpan? debounce = null)
    {
        if (debounce is { } window && (window < TimeSpan.Zero || window > TimeSpan.FromHours(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        _conflict = conflict;
        _debounce = debounce ?? _debounce;
        return this;
    }

    /// <summary>
    /// Runs this trigger's runs with the agent's model even when the agent has a rule handler.
    /// </summary>
    public NhAssistantTriggerBuilder<TEvent> UseModel()
    {
        _useModel = true;
        return this;
    }

    /// <summary>
    /// Stores a small typed payload with each event, at most 4,000 characters of JSON, for the agent's
    /// rule handler (<c>NhAssistantRuleInput.GetPayload</c>). Rules should still read the subject's
    /// current state: events coalesce, and retries and schedules carry no event.
    /// </summary>
    public NhAssistantTriggerBuilder<TEvent> Payload(Func<TEvent, object?> payload)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        return this;
    }

    /// <summary>
    /// Runs the trigger's runs in one skill of the agent.
    /// </summary>
    public NhAssistantTriggerBuilder<TEvent> Skill(string skillId)
    {
        if (!NhAiSubjectRef.IsValidType(skillId))
        {
            throw new ArgumentException("A skill id is a dash-case identifier.", nameof(skillId));
        }

        _skillId = skillId;
        return this;
    }

    public NhAssistantTriggerBuilder<TEvent> UseApprovalPolicy(string policyId)
    {
        if (!NhAiSubjectRef.IsValidType(policyId))
        {
            throw new ArgumentException("An approval policy id is a dash-case identifier.", nameof(policyId));
        }

        _approvalPolicyId = policyId;
        return this;
    }

    internal NhAssistantTriggerDefinition Build(string id)
    {
        if (_agentId is null || _subject is null || _message is null)
        {
            throw new InvalidOperationException(
                $"Trigger '{id}' needs StartAgent, Subject and Message.");
        }

        var subject = _subject;
        var initiator = _initiator;
        var message = _message;
        var dedupe = _dedupe;
        var payload = _payload;
        var when = _when;
        return new NhAssistantTriggerDefinition
        {
            Id = id,
            EventType = typeof(TEvent),
            AgentId = _agentId,
            When = when is null ? null : evt => when((TEvent)evt),
            Subject = evt => subject((TEvent)evt),
            Initiator = evt => initiator((TEvent)evt),
            Message = evt => message((TEvent)evt),
            Dedupe = evt => dedupe((TEvent)evt),
            Conflict = _conflict,
            Debounce = _debounce,
            ApprovalPolicyId = _approvalPolicyId,
            SkillId = _skillId,
            UseModel = _useModel,
            Payload = payload is null ? null : evt => payload((TEvent)evt)
        };
    }
}

/// <summary>
/// Built-in trigger ids.
/// </summary>
public static class NhAssistantTriggerIds
{
    /// <summary>
    /// A person started the agent on a subject.
    /// </summary>
    public const string Manual = "manual";

    /// <summary>
    /// A proposal of the agent was decided and the policy asks for a follow-up run.
    /// </summary>
    public const string ProposalDecided = "proposal-decided";

    /// <summary>
    /// An approved proposal of the agent finished executing and the policy asks for a follow-up run.
    /// </summary>
    public const string ProposalExecuted = "proposal-executed";

    /// <summary>
    /// A person typed in a thread while a run was active; the message waits for the next run.
    /// </summary>
    public const string QueuedMessage = "queued-message";

    /// <summary>
    /// Another assistant asked the agent to work on a subject through the generated request tool;
    /// the asking person is the run's initiator.
    /// </summary>
    public const string AgentRequest = "agent-request";

    /// <summary>
    /// Another assistant consulted the agent. A consultation is no run; the id marks its proposals.
    /// </summary>
    public const string Consultation = "consultation";

    /// <summary>
    /// Another agent handed work on a subject to the agent; the run has no initiator.
    /// </summary>
    public const string HandOff = "agent-handoff";

    /// <summary>
    /// A run of another agent that this agent handed work to ended; the input's payload is an
    /// <see cref="NhAssistantHandOffResult"/>.
    /// </summary>
    public const string HandOffEnded = "agent-handoff-ended";

    internal static bool IsReserved(string triggerId)
    {
        return triggerId is Manual or ProposalDecided or ProposalExecuted or QueuedMessage or AgentRequest or Consultation
            or HandOff or HandOffEnded;
    }
}
