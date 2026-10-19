namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// One skill of an agent: a part of its role with its own description, instructions, tools, model
/// profile or rules, daily budget, token ceiling, approval policy, state and triggers. A skill
/// shares the agent's identity, grants, accountability, thread, memory, briefing and continuity.
/// A run works in one skill at a time and its model sees only that skill's tools.
/// </summary>
public sealed class NhAssistantSkillOptions
{
    private readonly List<string> _toolSelectors = [];

    internal NhAssistantSkillOptions(string agentId, string id)
    {
        AgentId = agentId;
        Id = id;
    }

    public string AgentId { get; }

    public string Id { get; }

    /// <summary>
    /// What the skill is for, at most 300 characters; triage and <c>use_skill</c> show it.
    /// </summary>
    public string Description { get; internal set; } = string.Empty;

    /// <summary>
    /// The rule handler that runs the skill instead of a model.
    /// </summary>
    public Type? RuleHandlerType { get; internal set; }

    /// <summary>
    /// The model profile of the skill; <see langword="null"/> uses the agent's.
    /// </summary>
    public string? ProfileName { get; internal set; }

    /// <summary>
    /// Instructions appended to the agent's own.
    /// </summary>
    public NhAiTextAsset? Instructions { get; internal set; }

    /// <summary>
    /// The tool selectors the skill narrows the agent's to; empty uses the agent's.
    /// </summary>
    public IReadOnlyList<string> ToolSelectors => _toolSelectors;

    public long? MaxTokensPerRun { get; internal set; }

    public NhAssistantAgentBudget? DailyBudget { get; internal set; }

    public string? ApprovalPolicyId { get; internal set; }

    /// <summary>
    /// The skill starts in shadow mode: its work goes to the agent's shadow thread of the subject,
    /// where nobody is notified and nothing executes. Administrators switch it at runtime with
    /// <c>INhAssistantAgentControl.SetSkillShadowAsync</c>.
    /// </summary>
    public bool Shadow { get; internal set; }

    /// <summary>
    /// The version of a skill defined as data in the administration; <see langword="null"/> for a
    /// skill in code.
    /// </summary>
    public int? DataVersion { get; internal set; }

    /// <summary>
    /// The name a skill defined as data gives its rule handler.
    /// </summary>
    public string? RuleHandlerName { get; internal set; }

    internal void SetToolSelectors(IEnumerable<string> selectors)
    {
        _toolSelectors.Clear();
        _toolSelectors.AddRange(selectors);
    }
}

/// <summary>
/// Configures a skill; see <see cref="NhAssistantAgentRunBuilder.Skill"/>.
/// </summary>
public sealed class NhAssistantSkillBuilder
{
    public const int MaxDescriptionLength = 300;

    private readonly NhAssistantSkillOptions _options;

    internal NhAssistantSkillBuilder(NhAssistantSkillOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// What the skill is for, at most 300 characters. Triage and the model's <c>use_skill</c>
    /// choose by it, so say which events and questions it handles.
    /// </summary>
    public NhAssistantSkillBuilder Description(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > MaxDescriptionLength)
        {
            throw new ArgumentOutOfRangeException(nameof(description), $"A skill description has at most {MaxDescriptionLength} characters.");
        }

        _options.Description = description.Trim();
        return this;
    }

    /// <summary>
    /// Runs the skill by rules first. When they hand over with <c>HandOver</c>, the model continues
    /// in this skill: with its tools, instructions, profile, run ceiling, budget and state.
    /// </summary>
    public NhAssistantSkillBuilder UseRuleHandler<THandler>()
        where THandler : class, INhAssistantAgentRuleHandler
    {
        _options.RuleHandlerType = typeof(THandler);
        return this;
    }

    /// <summary>
    /// Runs the skill with another model profile than the agent's, for example a stronger one. A
    /// skill with rules uses it for the model after the rules hand over.
    /// </summary>
    public NhAssistantSkillBuilder UseProfile(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        _options.ProfileName = profileName;
        return this;
    }

    /// <summary>
    /// Instructions the model reads after the agent's own while it works in the skill.
    /// </summary>
    public NhAssistantSkillBuilder Instructions(NhAiTextAsset instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        _options.Instructions = instructions;
        return this;
    }

    /// <summary>
    /// Instructions the model reads after the agent's own while it works in the skill.
    /// </summary>
    public NhAssistantSkillBuilder Instructions(string instructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        _options.Instructions = NhAiTextAssetFactory.Create(
            $"skill-{_options.AgentId}-{_options.Id}",
            1,
            instructions.Trim(),
            "assistant-skill:" + _options.AgentId,
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.Chat,
            [],
            "default",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Operational,
            $"{_options.AgentId}-{_options.Id}-v1");
        return this;
    }

    /// <summary>
    /// The tools of the skill, by id or <c>prefix.*</c> selector; each must be covered by the
    /// agent's own selectors. The model sees only these tools while it works in the skill.
    /// </summary>
    public NhAssistantSkillBuilder Tools(params string[] toolSelectors)
    {
        ArgumentNullException.ThrowIfNull(toolSelectors);
        if (toolSelectors.Length == 0 || toolSelectors.Any(selector => !NhAssistantAgentDefinition.IsValidSelector(selector)))
        {
            throw new ArgumentException("A skill names one or more valid tool selectors.", nameof(toolSelectors));
        }

        _options.SetToolSelectors(toolSelectors.Distinct(StringComparer.Ordinal));
        return this;
    }

    /// <summary>
    /// The token ceiling of a run while it works in the skill, instead of the agent's.
    /// </summary>
    public NhAssistantSkillBuilder MaxTokensPerRun(long tokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tokens, 1);
        _options.MaxTokensPerRun = tokens;
        return this;
    }

    /// <summary>
    /// A daily budget of the skill next to the agent's; a run in the skill checks both.
    /// </summary>
    public NhAssistantSkillBuilder Budget(NhAssistantAgentBudget perDay)
    {
        ArgumentNullException.ThrowIfNull(perDay);
        _options.DailyBudget = perDay;
        return this;
    }

    /// <summary>
    /// The approval policy of the skill's proposals, before the agent's.
    /// </summary>
    public NhAssistantSkillBuilder UseApprovalPolicy(string policyId)
    {
        if (!NhAiSubjectRef.IsValidType(policyId))
        {
            throw new ArgumentException("An approval policy id is a dash-case identifier.", nameof(policyId));
        }

        _options.ApprovalPolicyId = policyId;
        return this;
    }

    /// <summary>
    /// Starts the skill in shadow mode, so a team evaluates it on real events while the agent's
    /// other skills work live: the work that reaches the skill goes to the agent's shadow thread of
    /// the subject. An administrator puts it live at runtime without a deploy.
    /// </summary>
    public NhAssistantSkillBuilder Shadow(bool enabled = true)
    {
        _options.Shadow = enabled;
        return this;
    }
}

/// <summary>
/// Configures the model triage of an agent with skills; see <see cref="NhAssistantAgentRunBuilder.Triage"/>.
/// </summary>
public sealed class NhAssistantTriageBuilder
{
    private readonly NhAssistantAgentRunOptions _options;

    internal NhAssistantTriageBuilder(NhAssistantAgentRunOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// The model profile of the triage call, typically a small and cheap one with structured output.
    /// </summary>
    public NhAssistantTriageBuilder UseProfile(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        _options.TriageProfileName = profileName;
        return this;
    }
}

/// <summary>
/// Who chose the skill a run works in.
/// </summary>
public static class NhAssistantSkillChoosers
{
    /// <summary>The trigger names the skill.</summary>
    public const string Trigger = "trigger";

    /// <summary>The agent's rule handler returned <c>UseSkill</c>.</summary>
    public const string Rules = "rules";

    /// <summary>The agent's model triage chose it.</summary>
    public const string Triage = "triage";

    /// <summary>The model switched to it with <c>use_skill</c>.</summary>
    public const string Model = "model";

    /// <summary>
    /// The run follows up a proposal that people decided or that finished executing, and continues
    /// in the skill of the run that made it.
    /// </summary>
    public const string FollowUp = "follow-up";
}
