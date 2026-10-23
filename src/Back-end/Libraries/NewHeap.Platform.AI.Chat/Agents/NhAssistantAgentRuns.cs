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
    private Dictionary<string, NhAssistantSkillOptions> _skills = new(StringComparer.Ordinal);
    private Dictionary<string, NhAssistantSkillOptions> _candidates = new(StringComparer.Ordinal);

    /// <summary>
    /// The most skills one agent declares.
    /// </summary>
    public const int MaxSkills = 20;
    private List<NhAssistantConsultationOptions> _consultations = [];
    private List<NhAssistantHandOffOptions> _handOffs = [];

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
    /// Headless runs may read through the API bridge gateway with the accountable person's rights.
    /// The agent's tool selectors must select the gateway's tools as well.
    /// </summary>
    public bool AllowGatewayReads { get; internal set; }

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
    /// The agent starts off (<see cref="NhAssistantAgentRunStates.Off"/>) the first time the
    /// application sees it; afterwards its stored state wins, also after a deploy. Unlike
    /// <see cref="Enabled"/>, an administrator switches it on.
    /// </summary>
    public bool StartsOff { get; internal set; }

    /// <summary>
    /// The agent is not offered in the chat: it is left out of the agent picker and people cannot
    /// start a chat conversation with it. Its runs, subject threads and consultations are unaffected.
    /// </summary>
    public bool HiddenFromChat { get; internal set; }

    /// <summary>
    /// What the agent works on: in code from <c>WorksOn</c>, <c>WorksOnAllObjects</c>,
    /// <c>WorksOnApplication</c>, <c>PerDivision</c>, <c>RunPolicy</c> and <c>ViewPolicy</c>; for an
    /// agent created in the administration as its administrator stored it. Without a statement the
    /// agent works on all object types and not on the application.
    /// </summary>
    public NhAssistantAgentWorksOn WorksOn { get; internal set; } = NhAssistantAgentWorksOn.Undeclared;

    /// <summary>
    /// The groups the agent joins in code with <c>InGroup</c>, ordered by id. An administrator's
    /// choice per group wins; an agent created in the administration joins groups there only.
    /// </summary>
    public IReadOnlyList<string> Groups { get; internal set; } = [];

    /// <summary>
    /// The metadata values the agent sets in code with <c>Metadata</c>, by entry name, unchecked;
    /// startup checks them against the entries registered with <c>AddAgentMetadata</c>.
    /// </summary>
    internal IReadOnlyDictionary<string, NhAssistantCodeMetadataValue> CodeMetadata => _codeMetadata;

    private readonly Dictionary<string, NhAssistantCodeMetadataValue> _codeMetadata = new(StringComparer.Ordinal);

    internal void SetCodeMetadata(string name, NhAssistantCodeMetadataValue value)
    {
        _codeMetadata[name] = value;
    }

    /// <summary>
    /// The agent starts in shadow mode: its runs work in shadow threads, where proposals are recorded
    /// as shadow proposals that nobody is notified about and that never execute. Administrators
    /// switch a live agent at runtime with <c>INhAssistantAgentControl.SetShadowAsync</c>; a shadow
    /// variant (<see cref="ShadowOfAgentId"/>) always works in shadow.
    /// </summary>
    public bool Shadow { get; internal set; }

    /// <summary>
    /// The live agent this agent is a shadow variant of: every trigger of the live agent also fires
    /// for it. <see langword="null"/> for an agent that is no variant.
    /// </summary>
    public string? ShadowOfAgentId { get; internal set; }

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
    /// The classification of the data the model calls of the agent's runs, people's turns in its
    /// threads, its triage, its consultations and people's plain chats with it carry; a skill's own
    /// wins. For an agent created in the administration it is the classification of its definition.
    /// <see langword="null"/> uses <see cref="NhAiDataClassification.Internal"/>.
    /// </summary>
    public NhAiDataClassification? DataClassification { get; internal set; }

    /// <summary>
    /// The classification of a model call in <paramref name="skill"/>: the skill's, then the
    /// agent's, then <see cref="NhAiDataClassification.Internal"/>.
    /// </summary>
    internal NhAiDataClassification ClassificationOf(NhAssistantSkillOptions? skill)
    {
        return skill?.DataClassification ?? DataClassification ?? NhAiDataClassification.Internal;
    }

    /// <summary>
    /// The agent's lower file limits once it accepts files; <see langword="null"/> while it accepts
    /// none, which is where every agent starts. Switch it on with
    /// <see cref="NhAssistantAgentRunBuilder.AcceptsFiles"/>, or in the administration for an agent
    /// created there.
    /// </summary>
    public NhAssistantFileLimits? FileLimits { get; internal set; }

    /// <summary>
    /// Whether people's messages and the agent's tools may give it files.
    /// </summary>
    public bool AcceptsFiles => FileLimits is not null;

    /// <summary>
    /// The skills of the agent by id; empty for an agent without skills.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantSkillOptions> Skills => _skills;

    /// <summary>
    /// The model profile that chooses the skill of a run no trigger or rule routed;
    /// <see langword="null"/> without model triage.
    /// </summary>
    public string? TriageProfileName { get; internal set; }

    /// <summary>
    /// In headless runs the agent may use what the person or technical account it answers to may
    /// use, within its tool selectors (<see cref="NhAiGrantSource.Accountable"/>). Always on for an
    /// agent whose run settings are data.
    /// </summary>
    public bool ActWithAccountableRights { get; internal set; }

    /// <summary>
    /// The version of the run settings of an agent created in the administration;
    /// <see langword="null"/> for an agent configured in code.
    /// </summary>
    public int? DataVersion { get; internal set; }

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
    /// A copy of the options with the skills defined as data next to those in code. The options in
    /// code stay as they are; everything else is shared, because nothing changes it after startup.
    /// </summary>
    internal NhAssistantAgentRunOptions WithDataSkills(
        IEnumerable<NhAssistantSkillOptions> dataSkills,
        IEnumerable<NhAssistantSkillOptions>? candidates = null)
    {
        var copy = (NhAssistantAgentRunOptions)MemberwiseClone();
        copy._skills = new Dictionary<string, NhAssistantSkillOptions>(_skills, StringComparer.Ordinal);
        foreach (var skill in dataSkills)
        {
            copy._skills.Add(skill.Id, skill);
        }

        copy._candidates = new Dictionary<string, NhAssistantSkillOptions>(_candidates, StringComparer.Ordinal);
        foreach (var candidate in candidates ?? [])
        {
            copy._candidates[candidate.Id] = candidate;
        }

        return copy;
    }

    /// <summary>
    /// The candidate versions of data skills, by skill id: each runs in shadow next to the live
    /// version on the triggers that name its skill.
    /// </summary>
    internal IReadOnlyDictionary<string, NhAssistantSkillOptions> SkillCandidates => _candidates;

    /// <summary>
    /// A copy of the options in which <paramref name="skillId"/> is its candidate of
    /// <paramref name="version"/>, for the candidate's run; <see langword="null"/> when that
    /// candidate is no longer there.
    /// </summary>
    internal NhAssistantAgentRunOptions? WithCandidate(string skillId, int? version)
    {
        if (!_candidates.TryGetValue(skillId, out var candidate) || candidate.DataVersion != version)
        {
            return null;
        }

        var copy = (NhAssistantAgentRunOptions)MemberwiseClone();
        copy._skills = new Dictionary<string, NhAssistantSkillOptions>(_skills, StringComparer.Ordinal) { [skillId] = candidate };
        return copy;
    }

    /// <summary>
    /// Whether <paramref name="skill"/> is the candidate version of its skill, as the options of a
    /// candidate's run (<see cref="WithCandidate"/>) hold it in place of the live version.
    /// </summary>
    internal bool IsCandidate(NhAssistantSkillOptions skill)
    {
        return _candidates.TryGetValue(skill.Id, out var candidate) && ReferenceEquals(candidate, skill);
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
    /// The subject types other assistants may consult the agent about, one entry per type: those in
    /// code and, in the configuration in effect, those an administrator defined as data
    /// (<see cref="NhAssistantConsultationOptions.Source"/>).
    /// </summary>
    public IReadOnlyList<NhAssistantConsultationOptions> Consultations => _consultations;

    /// <summary>
    /// The agent's own ceiling of consultations defined as data, from
    /// <see cref="NhAssistantAgentRunBuilder.ConsultableAsData"/>; <see langword="null"/> when
    /// administrators may not make the agent in code consultable. It applies together with the
    /// application's ceiling.
    /// </summary>
    public NhAssistantConsultationCeiling? ConsultationCeiling { get; internal set; }

    /// <summary>
    /// A copy of the options with the consultations defined as data next to those in code. The
    /// options in code stay as they are.
    /// </summary>
    internal NhAssistantAgentRunOptions WithDataConsultations(IEnumerable<NhAssistantConsultationOptions> dataConsultations)
    {
        var copy = (NhAssistantAgentRunOptions)MemberwiseClone();
        copy._consultations = [.. _consultations, .. dataConsultations];
        return copy;
    }

    /// <summary>
    /// The subject types other agents may hand work on to this agent, one entry per type: those in
    /// code and, in the configuration in effect, those an administrator defined as data
    /// (<see cref="NhAssistantHandOffOptions.Source"/>).
    /// </summary>
    public IReadOnlyList<NhAssistantHandOffOptions> HandOffs => _handOffs;

    /// <summary>
    /// A copy of the options with the hand-offs defined as data next to those in code. The options in
    /// code stay as they are.
    /// </summary>
    internal NhAssistantAgentRunOptions WithDataHandOffs(IEnumerable<NhAssistantHandOffOptions> dataHandOffs)
    {
        var copy = (NhAssistantAgentRunOptions)MemberwiseClone();
        copy._handOffs = [.. _handOffs, .. dataHandOffs];
        return copy;
    }

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
        return FindToolApprovalPolicy(toolId) ?? ApprovalPolicyId;
    }

    /// <summary>
    /// The policy of the tool itself, by its exact id or the longest prefix selector, without the
    /// agent's own policy.
    /// </summary>
    internal string? FindToolApprovalPolicy(string toolId)
    {
        if (_toolApprovalPolicies.TryGetValue(toolId, out var exact))
        {
            return exact;
        }

        return _toolApprovalPolicies
            .Where(item => item.Key.EndsWith(".*", StringComparison.Ordinal)
                           && toolId.StartsWith(item.Key[..^1], StringComparison.Ordinal))
            .OrderByDescending(item => item.Key.Length)
            .Select(item => item.Value)
            .FirstOrDefault();
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
    /// Makes a technical user account answer for the agent's runs. An administrator replaces the
    /// account at runtime with <c>INhAssistantAgentControl.SetServiceAccountAsync</c>; this one
    /// applies until then and again after the override is cleared.
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

    /// <summary>
    /// Lets the agent use, in its headless runs, what the person or technical account it answers to
    /// may use: a tool its selectors select is allowed when that account passes the tool's policies
    /// against the subject, recorded as <see cref="NhAiGrantSource.Accountable"/>. The account is
    /// rebuilt with <c>INhAiPrincipalFactory</c> for every decision, so an account that may no longer
    /// sign in lends nothing. Reads are shielded from thread readers without the tool's policies, as
    /// for grants. Needs an accountable person or technical account; a person's turn in the thread
    /// and consultations keep that person's own rights. The consult and request tools never run with
    /// the account's rights: a run uses them only when it delegates from its initiator.
    /// </summary>
    public NhAssistantAgentRunBuilder ActWithAccountableRights(bool enabled = true)
    {
        _options.ActWithAccountableRights = enabled;
        return this;
    }

    public NhAssistantAgentRunBuilder ElevatedReads(NhAiElevatedReads elevatedReads)
    {
        _options.ElevatedReads = elevatedReads;
        return this;
    }

    /// <summary>
    /// Lets headless runs read through the API bridge gateway's four read tools with the rights of
    /// the run's accountable person: each call gets a short-lived delegated read credential that the
    /// host's own bearer scheme validates and the library restricts to <c>GET</c> calls of gateway
    /// resources. The second key is the agent's tool selectors, which must select the gateway's
    /// tools. Requires <c>AddNewHeapPlatformAIDelegatedReads</c> and an accountable person or
    /// technical account; results are shielded from thread readers without the resource's policies.
    /// </summary>
    public NhAssistantAgentRunBuilder AllowGatewayReads(bool enabled = true)
    {
        _options.AllowGatewayReads = enabled;
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
        if (retryCount is < 0 or > NhAssistantAgentConfigurationLimits.MaxRetryCount)
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
    /// Starts the agent in shadow mode: it reads and proposes in shadow threads, but nobody is
    /// notified and nothing executes, so a team can evaluate it on real events. An administrator
    /// puts it live at runtime without a deploy.
    /// </summary>
    public NhAssistantAgentRunBuilder Shadow(bool enabled = true)
    {
        _options.Shadow = enabled;
        return this;
    }

    /// <summary>
    /// Makes the agent a shadow variant of <paramref name="liveAgentId"/> to evaluate other
    /// instructions, a profile, skills or triage before they go live: every event, scheduled and
    /// webhook trigger of the live agent also fires for the variant, with the same subject, message,
    /// dedupe key and payload, and a trigger's skill when the variant declares it. The variant keeps
    /// what shadow mode guarantees: administrators alone see its threads, it notifies nobody, nothing
    /// it proposes executes, and its usage counts against its own budget. It may have triggers of its
    /// own; the states of the live agent and the variant are independent. Implies <see cref="Shadow"/>.
    /// </summary>
    public NhAssistantAgentRunBuilder ShadowOf(string liveAgentId)
    {
        NhAssistantNames.ValidateSegment(liveAgentId, nameof(liveAgentId));
        if (string.Equals(liveAgentId, _options.AgentId, StringComparison.Ordinal))
        {
            throw new ArgumentException("An agent is no shadow variant of itself.", nameof(liveAgentId));
        }

        _options.ShadowOfAgentId = liveAgentId;
        _options.Shadow = true;
        return this;
    }

    /// <summary>
    /// The configured kill switch. A disabled agent rejects new triggers and stops its active runs,
    /// reports <c>configured-off</c> and cannot be switched on at runtime. To deploy an agent that
    /// waits for an administrator instead, use <see cref="StartsOff"/>.
    /// </summary>
    public NhAssistantAgentRunBuilder Enabled(bool enabled)
    {
        _options.Enabled = enabled;
        return this;
    }

    /// <summary>
    /// Starts the agent off the first time the application sees it: its events, schedules,
    /// webhooks, hand-offs and follow-ups are ignored until an administrator switches it on, while
    /// people can already chat with it and start it by hand. Only the first start applies it; after
    /// that the stored state wins, so a later deploy never switches the agent off again. Unlike
    /// <see cref="Enabled"/>(<see langword="false"/>), the administration switches it on.
    /// </summary>
    public NhAssistantAgentRunBuilder StartsOff()
    {
        _options.StartsOff = true;
        return this;
    }

    /// <summary>
    /// The classification of the data the agent's model calls carry: in its runs, people's turns in
    /// its threads, its triage, its consultations and people's plain chats with it. A skill's own
    /// classification wins. The model profile of each call must permit it
    /// (<c>PermitDataClassifications</c>); otherwise its fallbacks are tried and the call is refused
    /// with <c>assistant-classification-not-permitted</c> when none permits it; startup logs a warning
    /// when neither the agent's profile nor its fallbacks permit it. Without it the calls are
    /// <see cref="NhAiDataClassification.Internal"/>.
    /// </summary>
    public NhAssistantAgentRunBuilder DataClassification(NhAiDataClassification classification)
    {
        if (!Enum.IsDefined(classification))
        {
            throw new ArgumentOutOfRangeException(nameof(classification));
        }

        _options.DataClassification = classification;
        return this;
    }

    /// <summary>
    /// Lets the agent read files: those people attach to their messages and those its tools return.
    /// An agent accepts no files until it is switched on here or, for an agent created there, in the
    /// administration, so a deploy never opens a new stream of documents unnoticed. A skill of the
    /// agent accepts files only when it is switched on too. The application's file source opens every
    /// file for the actor of the turn; the turn takes the highest classification of its files, agent
    /// and skill. <paramref name="limits"/> can only lower the application's
    /// <see cref="NhAssistantLimits"/>; the host refuses to start when a limit is higher.
    /// </summary>
    public NhAssistantAgentRunBuilder AcceptsFiles(Action<NhAssistantFileLimits>? limits = null)
    {
        var fileLimits = new NhAssistantFileLimits();
        limits?.Invoke(fileLimits);
        _options.FileLimits = fileLimits;
        return this;
    }

    /// <summary>
    /// Keeps the agent out of the chat: it is left out of the agent picker and people cannot start a
    /// chat conversation with it, while its triggers, runs, subject threads and consultations work as
    /// before. Use it for agents that only work on their own.
    /// </summary>
    public NhAssistantAgentRunBuilder HideFromChat()
    {
        _options.HiddenFromChat = true;
        return this;
    }

    /// <summary>
    /// Says which object types the agent works on, by their registered type ids; repeated calls add
    /// types, at most <see cref="NhAssistantAgentWorksOn.MaxSubjectTypes"/>. Triggers, manual starts,
    /// hand-offs, consultations and requests start the agent only on these types, and startup fails
    /// for a trigger, hand-off or consultation that names another. An agent that says nothing works on
    /// all object types and not on the application. Excludes <see cref="WorksOnAllObjects"/>.
    /// </summary>
    public NhAssistantAgentRunBuilder WorksOn(params string[] subjectTypes)
    {
        ArgumentNullException.ThrowIfNull(subjectTypes);
        if (subjectTypes.Length == 0)
        {
            throw new ArgumentException("Name at least one object type.", nameof(subjectTypes));
        }
        foreach (var subjectType in subjectTypes)
        {
            if (!NhAiSubjectRef.IsValidType(subjectType))
            {
                throw new ArgumentException("An object type is a dash-case identifier.", nameof(subjectTypes));
            }
            if (NhAiSubjectRef.IsReservedType(subjectType))
            {
                throw new ArgumentException(
                    $"'{subjectType}' belongs to the platform; use WorksOnApplication for the whole application.",
                    nameof(subjectTypes));
            }
        }

        var current = Declared();
        if (current.AllSubjects)
        {
            throw new InvalidOperationException(
                $"Agent '{_options.AgentId}' already works on all object types; WorksOn names types instead.");
        }

        var types = NhAssistantAgentWorksOn.Normalize(current.SubjectTypes.Concat(subjectTypes));
        if (types.Count > NhAssistantAgentWorksOn.MaxSubjectTypes)
        {
            throw new InvalidOperationException(
                $"An agent works on at most {NhAssistantAgentWorksOn.MaxSubjectTypes} object types; choose WorksOnAllObjects.");
        }

        _options.WorksOn = current with { SubjectTypes = types };
        return this;
    }

    /// <summary>
    /// Says the agent works on every object type the application registers, now and later. This is
    /// what an agent that says nothing does, chosen on purpose. Excludes <see cref="WorksOn"/>.
    /// </summary>
    public NhAssistantAgentRunBuilder WorksOnAllObjects()
    {
        var current = Declared();
        if (current.SubjectTypes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Agent '{_options.AgentId}' already works on named object types; WorksOnAllObjects replaces them.");
        }

        _options.WorksOn = current with { AllSubjects = true };
        return this;
    }

    /// <summary>
    /// Says the agent also works on the whole application (the platform's
    /// <see cref="NhAiSubjectRef.SystemType"/> subject), for runs that concern no single object, such
    /// as weekly statistics. Combine it with <see cref="WorksOn"/> or <see cref="WorksOnAllObjects"/>
    /// for objects; alone the agent works on the application only.
    /// </summary>
    public NhAssistantAgentRunBuilder WorksOnApplication()
    {
        _options.WorksOn = Declared() with { Application = true };
        return this;
    }

    /// <summary>
    /// Makes the agent work per division instead of app-wide: its runs, threads and pages for the
    /// whole application then exist per division. Use it only when the application lists its
    /// divisions; app-wide is the default.
    /// </summary>
    public NhAssistantAgentRunBuilder PerDivision(bool enabled = true)
    {
        _options.WorksOn = _options.WorksOn with { PerDivision = enabled };
        return this;
    }

    /// <summary>
    /// The authorization policy a person needs to run the agent for the whole application; without
    /// it the assistant's administration policy decides. Starts on an object keep their own rule: a
    /// participant of the object who may use the agent. Startup fails when the policy is not
    /// registered.
    /// </summary>
    public NhAssistantAgentRunBuilder RunPolicy(string policyName)
    {
        _options.WorksOn = _options.WorksOn with { RunPolicy = ValidatePolicyName(policyName) };
        return this;
    }

    /// <summary>
    /// The authorization policy a person needs to see the agent's results for the whole application;
    /// without it the agent's required policy decides. Results on an object follow the object's own
    /// access. Startup fails when the policy is not registered.
    /// </summary>
    public NhAssistantAgentRunBuilder ViewPolicy(string policyName)
    {
        _options.WorksOn = _options.WorksOn with { ViewPolicy = ValidatePolicyName(policyName) };
        return this;
    }

    /// <summary>
    /// Adds the agent to a group of agents registered with <c>AddAgentGroup</c>, so the group's board
    /// shows it with its latest result for the whole application. Administrators may remove it again;
    /// their choice wins over code. Startup fails for a group that is not registered.
    /// </summary>
    public NhAssistantAgentRunBuilder InGroup(string groupId)
    {
        if (!NhAssistantAgentGroupDefinition.IsValidId(groupId))
        {
            throw new ArgumentException(
                $"An agent group id is a dash-case identifier of at most {NhAssistantAgentGroupDefinition.MaxIdLength} characters.",
                nameof(groupId));
        }

        _options.Groups = _options.Groups
            .Append(groupId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return this;
    }

    /// <summary>
    /// Sets the agent's value of an entry of agent metadata registered with <c>AddAgentMetadata</c>,
    /// with the entry's type. Startup checks it as a save checks data: an entry that is not registered,
    /// another type, a value above the entry's size limit or one a validator refuses fails startup.
    /// Administrators may override it only when the entry is <c>Editable()</c>. Setting a name again
    /// replaces the value.
    /// </summary>
    public NhAssistantAgentRunBuilder Metadata<TValue>(string name, TValue value)
        where TValue : notnull
    {
        NhAssistantAgentMetadataEntry.ValidateName(name, nameof(name));
        ArgumentNullException.ThrowIfNull(value);
        _options.SetCodeMetadata(name, new NhAssistantCodeMetadataValue(typeof(TValue), value));
        return this;
    }

    /// <summary>
    /// The statement so far, declared: the first <c>WorksOn*</c> call replaces "all object types" of
    /// an agent that said nothing, and keeps per division and the policies.
    /// </summary>
    private NhAssistantAgentWorksOn Declared()
    {
        var current = _options.WorksOn;
        return current.Declared
            ? current
            : current with { Declared = true, AllSubjects = false, SubjectTypes = [], Application = false };
    }

    private static string ValidatePolicyName(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (policyName.Length > NhAssistantAgentWorksOn.MaxPolicyLength || policyName.Trim() != policyName)
        {
            throw new ArgumentException(
                $"A policy name has at most {NhAssistantAgentWorksOn.MaxPolicyLength} characters without surrounding whitespace.",
                nameof(policyName));
        }

        return policyName;
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
    /// Lets administrators make the agent consultable as data about the object types it works on and
    /// that it has no <see cref="AllowConsultation"/> for, within this ceiling and the application's
    /// (<c>UseConsultationsAsData</c>). Without it the agent gets no consultations as data: the
    /// developer decides whether people reach its memory and tools through other assistants. The
    /// route that executes with the agent's own rights needs an agent that answers for itself with
    /// grants or a technical account; startup fails for an agent that answers to the subject's
    /// responsible person, for a ceiling outside the application's and without
    /// <c>UseConsultationsAsData</c>.
    /// </summary>
    public NhAssistantAgentRunBuilder ConsultableAsData(Action<NhAssistantConsultationCeilingBuilder>? configure = null)
    {
        var ceiling = new NhAssistantConsultationCeiling();
        configure?.Invoke(new NhAssistantConsultationCeilingBuilder(ceiling));
        _options.ConsultationCeiling = ceiling;
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

    /// <summary>
    /// The subject type the trigger names up front, so startup checks it against what the agent works
    /// on; <see langword="null"/> when the type is known only when it fires.
    /// </summary>
    public string? SubjectType { get; init; }

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
    /// The subjects a schedule tick starts the agent for; <see langword="null"/> for a schedule that
    /// runs for the whole application (<see cref="IsSystemSchedule"/>).
    /// </summary>
    public Func<IServiceProvider, CancellationToken, IAsyncEnumerable<NhAiSubjectRef>>? ScheduledSubjects { get; init; }

    /// <summary>
    /// A schedule without subjects: each tick runs once on <c>nh-system/global</c>, or once per
    /// listed division for an agent that works per division.
    /// </summary>
    public bool IsSystemSchedule => Source == NhAssistantTriggerSource.Schedule && ScheduledSubjects is null;

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

    /// <summary>
    /// The version of a trigger defined as data; <see langword="null"/> for a trigger in code.
    /// </summary>
    public int? DataVersion { get; init; }

    /// <summary>
    /// The trigger event or subject set a trigger defined as data names.
    /// </summary>
    public string? BlockName { get; init; }

    /// <summary>
    /// The named filters of its trigger event that a trigger defined as data selected, in the order
    /// it stores them; every one must accept an event, after <see cref="When"/>.
    /// </summary>
    public IReadOnlyList<NhAssistantTriggerFilterDefinition> Filters { get; init; } = [];

    /// <summary>
    /// The filters a trigger defined as data selected that its event no longer registers, for example
    /// after a deploy removed one. Such a trigger fails closed: it ignores every event with the reason
    /// <c>filter-missing</c>.
    /// </summary>
    public IReadOnlyList<string> MissingFilters { get; init; } = [];

    /// <summary>
    /// The trigger's runs work in the agent's shadow thread, whatever the agent's and the skill's
    /// mode. Only a trigger defined as data has a mode of its own; it starts in shadow.
    /// </summary>
    public bool Shadow { get; init; }

    /// <summary>
    /// The candidate version <see cref="DataVersion"/> of a trigger defined as data: it fires next to
    /// the live version of the same id, for the trigger's own agent only, and its runs always work in
    /// the agent's shadow thread.
    /// </summary>
    public bool Candidate { get; init; }
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
    /// deadline this week. Without subjects the schedule runs for the whole application: once per
    /// tick on <c>nh-system/global</c>, or once per listed division for an agent that works per
    /// division, at most <see cref="MaxSubjectsPerTick"/>. Only an agent that works on the
    /// application (<c>WorksOnApplication</c>) may have such a schedule; startup checks it.
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

    /// <summary>
    /// The dedupe key of one tick: a repeated job in the same minute starts no second run.
    /// </summary>
    internal static string TickKey(NhAssistantScheduledTick tick)
    {
        return "tick:" + tick.Tick.UtcDateTime.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal NhAssistantTriggerDefinition Build(string id)
    {
        if (_agentId is null || _cron is null || _message is null)
        {
            throw new InvalidOperationException(
                $"Scheduled trigger '{id}' needs StartAgent, Cron and Message.");
        }

        var message = _message;
        return new NhAssistantTriggerDefinition
        {
            Id = id,
            Source = NhAssistantTriggerSource.Schedule,
            AgentId = _agentId,
            Subject = evt => ((NhAssistantScheduledTick)evt).Subject,
            // Without subjects the schedule runs for the whole application, which startup checks
            // against what the agent works on like every trigger that names its type up front.
            SubjectType = _subjects is null ? NhAiSubjectRef.SystemType : null,
            Message = evt => message((NhAssistantScheduledTick)evt),
            Dedupe = evt => TickKey((NhAssistantScheduledTick)evt),
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
    private string? _subjectType;
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

    /// <summary>
    /// The subject of an event, whose type is known only when the trigger fires; a start on a type
    /// the agent does not work on is refused then with <c>assistant-agent-does-not-work-on</c>. Prefer
    /// <see cref="Subject(string, Func{TEvent, string})"/> when the type is fixed.
    /// </summary>
    public NhAssistantTriggerBuilder<TEvent> Subject(Func<TEvent, NhAiSubjectRef> subject)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _subjectType = null;
        return this;
    }

    /// <summary>
    /// The subject of an event as a fixed type and the id the event names, such as
    /// <c>Subject("project", e =&gt; e.ProjectId.ToString())</c>. The type is known up front, so
    /// startup fails when the agent does not work on it.
    /// </summary>
    public NhAssistantTriggerBuilder<TEvent> Subject(string subjectType, Func<TEvent, string> id)
    {
        NhAssistantTriggerSubjects.ValidateType(subjectType);
        ArgumentNullException.ThrowIfNull(id);
        _subject = evt => new NhAiSubjectRef(subjectType, id(evt));
        _subjectType = subjectType;
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
            SubjectType = _subjectType,
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

    /// <summary>
    /// Whether a person asked for the work of the trigger: a start by hand, a message typed while a
    /// run worked, or a request through another assistant. Its input is that person's message and
    /// the person is the run's initiator.
    /// </summary>
    internal static bool IsAskedByPerson(string? triggerId)
    {
        return triggerId is Manual or QueuedMessage or AgentRequest;
    }
}

/// <summary>
/// What started an agent run, as <see cref="NhAssistantAuditEvent.TriggerKind"/> of
/// <see cref="NhAssistantAuditEventKind.AgentRunEnded"/> names it: a small set of stable codes, so an
/// audit sink counts runs by what started them without knowing the application's trigger ids. A run
/// records its kind when it is created, from its trigger as it was registered then.
/// </summary>
public static class NhAssistantTriggerKinds
{
    /// <summary>
    /// An application event: a trigger added with <c>AddTrigger</c>, or a trigger defined as data on
    /// a trigger event.
    /// </summary>
    public const string Event = "event";

    /// <summary>
    /// A tick of a scheduled trigger, in code (<c>AddScheduledTrigger</c>) or defined as data.
    /// </summary>
    public const string Schedule = "schedule";

    /// <summary>
    /// A signed call of a webhook trigger (<c>AddWebhookTrigger</c>).
    /// </summary>
    public const string Webhook = "webhook";

    /// <summary>
    /// A person started the agent on a subject, or ran it now (<see cref="NhAssistantTriggerIds.Manual"/>).
    /// </summary>
    public const string Manual = "manual";

    /// <summary>
    /// A person's message in the agent's thread that waited for the run
    /// (<see cref="NhAssistantTriggerIds.QueuedMessage"/>).
    /// </summary>
    public const string Message = "message";

    /// <summary>
    /// Another assistant asked the agent to work on a subject through the generated request tool
    /// (<see cref="NhAssistantTriggerIds.AgentRequest"/>).
    /// </summary>
    public const string Request = "request";

    /// <summary>
    /// Another agent handed work on a subject to the agent (<see cref="NhAssistantTriggerIds.HandOff"/>).
    /// </summary>
    public const string HandOff = "hand-off";

    /// <summary>
    /// The agent follows up on its own earlier work: a proposal was decided or executed
    /// (<see cref="NhAssistantTriggerIds.ProposalDecided"/>, <see cref="NhAssistantTriggerIds.ProposalExecuted"/>)
    /// or a run it handed work to ended (<see cref="NhAssistantTriggerIds.HandOffEnded"/>).
    /// </summary>
    public const string FollowUp = "follow-up";

    /// <summary>
    /// An administrator replayed the recorded inputs of another agent's run into a shadow variant.
    /// </summary>
    public const string Replay = "replay";

    /// <summary>
    /// The kind of a run started by <paramref name="triggerId"/>: a replay when it replays another
    /// run, the kind of a reserved trigger id, and the source of a registered trigger otherwise;
    /// <see langword="null"/> for a trigger that is no longer registered.
    /// </summary>
    internal static string? Of(
        string triggerId,
        Guid? replayOfRunId,
        IReadOnlyDictionary<string, NhAssistantTriggerDefinition> triggers)
    {
        if (replayOfRunId is not null)
        {
            return Replay;
        }

        switch (triggerId)
        {
            case NhAssistantTriggerIds.Manual:
                return Manual;
            case NhAssistantTriggerIds.QueuedMessage:
                return Message;
            case NhAssistantTriggerIds.AgentRequest:
                return Request;
            case NhAssistantTriggerIds.HandOff:
                return HandOff;
            case NhAssistantTriggerIds.ProposalDecided:
            case NhAssistantTriggerIds.ProposalExecuted:
            case NhAssistantTriggerIds.HandOffEnded:
                return FollowUp;
        }

        if (!triggers.TryGetValue(triggerId, out var trigger))
        {
            return null;
        }

        return trigger.Source switch
        {
            NhAssistantTriggerSource.Schedule => Schedule,
            NhAssistantTriggerSource.Webhook => Webhook,
            _ => Event
        };
    }
}
