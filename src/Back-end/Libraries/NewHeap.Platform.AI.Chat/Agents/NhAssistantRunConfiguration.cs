using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The limits of the run configuration defined as data.
/// </summary>
public static class NhAssistantAgentConfigurationLimits
{
    /// <summary>
    /// The longest instructions of a data skill, in characters.
    /// </summary>
    public const int MaxSkillInstructionsLength = 20_000;

    /// <summary>
    /// The longest message of a data schedule, in characters.
    /// </summary>
    public const int MaxTriggerMessageLength = 2_000;

    /// <summary>
    /// The most data triggers of one agent.
    /// </summary>
    public const int MaxTriggersPerAgent = 20;

    /// <summary>
    /// The longest debounce of a data event trigger, in seconds.
    /// </summary>
    public const int MaxDebounceSeconds = 3_600;

    /// <summary>
    /// The most automatic retries of a run in the run settings of an agent.
    /// </summary>
    public const int MaxRetryCount = 10;

    /// <summary>
    /// The most concurrent runs in the run settings of an agent.
    /// </summary>
    public const int MaxConcurrentRuns = 100;

    /// <summary>
    /// The most runs per subject and day in the run settings of an agent.
    /// </summary>
    public const int MaxRunsPerSubjectPerDay = 1_000;
}

/// <summary>
/// The names of <see cref="NhAiElevatedReads"/> in the run settings of an agent.
/// </summary>
public static class NhAssistantElevatedReadNames
{
    public const string Restricted = "restricted";

    public const string Deny = "deny";

    public static string NameOf(NhAiElevatedReads elevatedReads)
    {
        return elevatedReads == NhAiElevatedReads.Deny ? Deny : Restricted;
    }

    public static bool TryParse(string? name, out NhAiElevatedReads elevatedReads)
    {
        elevatedReads = name == Deny ? NhAiElevatedReads.Deny : NhAiElevatedReads.Restricted;
        return name is Restricted or Deny;
    }
}

/// <summary>
/// A skill or trigger defined as data that is out of effect because it no longer fits the code, for
/// example after a deploy removed the profile, policy, rule handler, event or subject set it names.
/// The administration shows it; startup does not fail on it.
/// </summary>
/// <param name="Kind"><c>agent</c> for run settings, <c>skill</c> or <c>trigger</c>.</param>
/// <param name="Id">The agent id, the skill id or the trigger id.</param>
/// <param name="Code">One of <see cref="NhAssistantRunConfigurationProblems"/>.</param>
public sealed record NhAssistantRunConfigurationProblem(string AgentId, string Kind, string Id, string Code);

/// <summary>
/// Why a skill or trigger defined as data does not fit the code.
/// </summary>
public static class NhAssistantRunConfigurationProblems
{
    /// <summary>The run settings of an agent created in the administration.</summary>
    public const string AgentKind = "agent";

    public const string SkillKind = "skill";

    public const string TriggerKind = "trigger";

    /// <summary>The candidate version of a data skill.</summary>
    public const string CandidateKind = "candidate";

    /// <summary>The agent has no run configuration, in code or as data, or no longer exists.</summary>
    public const string UnknownAgent = "unknown-agent";

    /// <summary>The code declares a skill or trigger with the same id.</summary>
    public const string ConflictsWithCode = "conflicts-with-code";

    /// <summary>The agent would have more than <see cref="NhAssistantAgentRunOptions.MaxSkills"/> skills.</summary>
    public const string TooManySkills = "too-many-skills";

    /// <summary>A tool selector of the skill is invalid or not covered by the agent's own.</summary>
    public const string ToolNotCovered = "tool-not-covered";

    /// <summary>The model profile is not registered with chat, function calling and streaming.</summary>
    public const string UnknownProfile = "unknown-profile";

    /// <summary>The approval policy is not registered with <c>AddApprovalPolicy</c>.</summary>
    public const string UnknownPolicy = "unknown-policy";

    /// <summary>The rule handler is not registered with <c>AddRuleHandler</c>.</summary>
    public const string UnknownRuleHandler = "unknown-rule-handler";

    /// <summary>The trigger event is not registered with <c>AddTriggerEvent</c>.</summary>
    public const string UnknownEvent = "unknown-event";

    /// <summary>The subject set is not registered with <c>AddSubjectSet</c>.</summary>
    public const string UnknownSubjectSet = "unknown-subject-set";

    /// <summary>The trigger names a skill the agent does not have.</summary>
    public const string UnknownSkill = "unknown-skill";

    /// <summary>The schedule's cron expression or time zone is invalid.</summary>
    public const string InvalidSchedule = "invalid-schedule";

    /// <summary>The trigger event names a subject type up front that the agent does not work on.</summary>
    public const string NotWorkedOn = "not-worked-on";

    /// <summary>The stored definition is invalid in another way.</summary>
    public const string Invalid = "invalid";
}

/// <summary>
/// The names of the conflict behaviors a data trigger stores.
/// </summary>
public static class NhAssistantTriggerConflictNames
{
    public const string Coalesce = "coalesce";

    public const string Queue = "queue";

    public const string Supersede = "supersede";

    public const string Ignore = "ignore";

    public static string NameOf(NhAssistantTriggerConflict conflict)
    {
        return conflict switch
        {
            NhAssistantTriggerConflict.Queue => Queue,
            NhAssistantTriggerConflict.Supersede => Supersede,
            NhAssistantTriggerConflict.Ignore => Ignore,
            _ => Coalesce
        };
    }

    public static bool TryParse(string? name, out NhAssistantTriggerConflict conflict)
    {
        conflict = name switch
        {
            Queue => NhAssistantTriggerConflict.Queue,
            Supersede => NhAssistantTriggerConflict.Supersede,
            Ignore => NhAssistantTriggerConflict.Ignore,
            _ => NhAssistantTriggerConflict.Coalesce
        };
        return name is Coalesce or Queue or Supersede or Ignore;
    }
}

/// <summary>
/// The run configuration in effect: the options and triggers in code with the skills and triggers
/// defined as data that fit them.
/// </summary>
/// <param name="Stamp">The stamp the data was read at; <see langword="null"/> before any change.</param>
/// <param name="Problems">The data that is out of effect, and why.</param>
internal sealed record NhAssistantRunSnapshot(
    IReadOnlyDictionary<string, NhAssistantAgentRunOptions> AgentRuns,
    IReadOnlyDictionary<string, NhAssistantTriggerDefinition> Triggers,
    Guid? Stamp,
    IReadOnlyList<NhAssistantRunConfigurationProblem> Problems);

/// <summary>
/// Hears every snapshot that went into effect, for example to keep the recurring jobs of data
/// schedules in line.
/// </summary>
internal interface INhAssistantRunSnapshotListener
{
    Task AppliedAsync(NhAssistantRunSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>
/// What a data skill is checked against: the skills the agent declares in code and its own tool
/// selectors, from code or from the administration.
/// </summary>
internal sealed record NhAssistantRunAgent(IReadOnlyCollection<string> CodeSkillIds, IReadOnlyList<string> ToolSelectors);

/// <summary>
/// Checks data skills and triggers against the code, the same way startup checks those in code.
/// Saving and loading both use it, so data that is saved fits and data that no longer fits is left
/// out.
/// </summary>
internal sealed class NhAssistantRunConfigurationValidator(
    NhAssistantRegistrationState registration,
    IServiceProvider services)
{
    private const NhAiModelCapability ChatProfileCapabilities =
        NhAiModelCapability.Chat | NhAiModelCapability.FunctionCalling | NhAiModelCapability.Streaming;

    /// <summary>
    /// The agent a data skill of <paramref name="agentId"/> is checked against when its runs are
    /// configured in code, or <see langword="null"/>.
    /// </summary>
    public NhAssistantRunAgent? CodeAgent(string agentId)
    {
        return registration.CodeAgentRuns.TryGetValue(agentId, out var options) && registration.TryGetAgent(agentId, out var agent)
            ? new NhAssistantRunAgent(options.Skills.Keys.ToArray(), agent.ToolSelectors)
            : null;
    }

    /// <summary>
    /// The problem of the run settings of an agent created in the administration, or
    /// <see langword="null"/> when they fit the code.
    /// </summary>
    public string? ValidateRunSettings(AssistantAgentRunConfiguration settings)
    {
        if (!NhAssistantAccountabilities.TryParse(settings.Accountability, out var accountability)
            || (accountability == NhAiAgentAccountability.ServiceAccount) == string.IsNullOrWhiteSpace(settings.ServiceAccountActorId)
            || (settings.AllowGatewayReads && accountability == NhAiAgentAccountability.Agent)
            || !NhAssistantElevatedReadNames.TryParse(settings.ElevatedReads, out _)
            || settings.RetryCount is < 0 or > NhAssistantAgentConfigurationLimits.MaxRetryCount
            || settings.MaxConcurrentRuns is < 1 or > NhAssistantAgentConfigurationLimits.MaxConcurrentRuns
            || settings.MaxRunsPerSubjectPerDay is < 1 or > NhAssistantAgentConfigurationLimits.MaxRunsPerSubjectPerDay
            || settings.MaxTokensPerRun < 1
            || (settings.DailyToolCalls is null) != (settings.DailyTokens is null)
            || settings.DailyToolCalls < 1
            || settings.DailyTokens < 1)
        {
            return NhAssistantRunConfigurationProblems.Invalid;
        }
        if (settings.ApprovalPolicyId is { } policyId && !registration.ApprovalPolicies.ContainsKey(policyId))
        {
            return NhAssistantRunConfigurationProblems.UnknownPolicy;
        }
        if (settings.TriageProfileName is { } triage
            && (services.GetService<INhAiModelProfileRegistry>() is not { } profiles
                || !profiles.TryGet(triage, out var profile)
                || (profile.Capabilities & NhAiModelCapability.Chat) == 0))
        {
            return NhAssistantRunConfigurationProblems.UnknownProfile;
        }
        if (settings.RuleHandlerName is { } handlerName && !registration.RuleHandlers.ContainsKey(handlerName))
        {
            return NhAssistantRunConfigurationProblems.UnknownRuleHandler;
        }

        return null;
    }

    /// <summary>
    /// The problem of a data skill on its agent, or <see langword="null"/> when it fits.
    /// </summary>
    public string? ValidateSkill(AssistantAgentSkillDefinition definition, IReadOnlyList<string> toolSelectors, NhAssistantRunAgent? agent)
    {
        if (agent is null)
        {
            return NhAssistantRunConfigurationProblems.UnknownAgent;
        }
        if (!NhAiSubjectRef.IsValidType(definition.SkillId)
            || string.IsNullOrWhiteSpace(definition.Description)
            || definition.Description.Length > NhAssistantSkillBuilder.MaxDescriptionLength
            || definition.Instructions?.Length > NhAssistantAgentConfigurationLimits.MaxSkillInstructionsLength
            || definition.MaxTokensPerRun < 1
            || (definition.DailyToolCalls is null) != (definition.DailyTokens is null)
            || definition.DailyToolCalls < 1
            || definition.DailyTokens < 1)
        {
            return NhAssistantRunConfigurationProblems.Invalid;
        }
        if (agent.CodeSkillIds.Contains(definition.SkillId, StringComparer.Ordinal))
        {
            return NhAssistantRunConfigurationProblems.ConflictsWithCode;
        }
        if (toolSelectors.Any(selector => !NhAssistantAgentDefinition.IsValidSelector(selector)
                                          || !agent.ToolSelectors.Any(agentSelector => NhAssistantRegistrationState.Covers(agentSelector, selector))))
        {
            return NhAssistantRunConfigurationProblems.ToolNotCovered;
        }
        if (definition.ProfileName is { } profileName
            && (services.GetService<INhAiModelProfileRegistry>() is not { } profiles
                || !profiles.TryGet(profileName, out var profile)
                || (profile.Capabilities & ChatProfileCapabilities) != ChatProfileCapabilities))
        {
            return NhAssistantRunConfigurationProblems.UnknownProfile;
        }
        if (definition.ApprovalPolicyId is { } policyId && !registration.ApprovalPolicies.ContainsKey(policyId))
        {
            return NhAssistantRunConfigurationProblems.UnknownPolicy;
        }
        if (definition.RuleHandlerName is { } handlerName && !registration.RuleHandlers.ContainsKey(handlerName))
        {
            return NhAssistantRunConfigurationProblems.UnknownRuleHandler;
        }

        return null;
    }

    /// <summary>
    /// The problem of a data trigger on the agent's effective options, or <see langword="null"/>
    /// when it fits.
    /// </summary>
    public string? ValidateTrigger(
        AssistantAgentTriggerDefinition trigger,
        IReadOnlyDictionary<string, NhAssistantAgentRunOptions> agentRuns)
    {
        if (!agentRuns.TryGetValue(trigger.AgentId, out var options))
        {
            return NhAssistantRunConfigurationProblems.UnknownAgent;
        }
        if (!NhAiSubjectRef.IsValidType(trigger.Id)
            || NhAssistantTriggerIds.IsReserved(trigger.Id)
            || !NhAssistantTriggerConflictNames.TryParse(trigger.Conflict, out _)
            || trigger.DebounceSeconds is < 0 or > NhAssistantAgentConfigurationLimits.MaxDebounceSeconds)
        {
            return NhAssistantRunConfigurationProblems.Invalid;
        }
        if (registration.CodeTriggers.ContainsKey(trigger.Id))
        {
            return NhAssistantRunConfigurationProblems.ConflictsWithCode;
        }
        if (trigger.SkillId is { } skillId && !options.Skills.ContainsKey(skillId))
        {
            return NhAssistantRunConfigurationProblems.UnknownSkill;
        }

        switch (trigger.Source)
        {
            case NhAssistantDataTriggerSources.Event:
                if (trigger.EventName is not { } eventName || !registration.TriggerEvents.TryGetValue(eventName, out var triggerEvent))
                {
                    return NhAssistantRunConfigurationProblems.UnknownEvent;
                }

                // An event that names its subject type up front fits only an agent that works on it.
                return triggerEvent.SubjectType is { } subjectType && !options.WorksOn.Covers(subjectType)
                    ? NhAssistantRunConfigurationProblems.NotWorkedOn
                    : null;
            case NhAssistantDataTriggerSources.Schedule:
                if (trigger.SubjectSetName is not { } setName || !registration.SubjectSets.ContainsKey(setName))
                {
                    return NhAssistantRunConfigurationProblems.UnknownSubjectSet;
                }
                if (string.IsNullOrWhiteSpace(trigger.Message)
                    || trigger.Message.Length > NhAssistantAgentConfigurationLimits.MaxTriggerMessageLength)
                {
                    return NhAssistantRunConfigurationProblems.Invalid;
                }

                return IsValidCron(trigger.Cron) && TryFindTimeZone(trigger.TimeZone, out _)
                    ? null
                    : NhAssistantRunConfigurationProblems.InvalidSchedule;
            default:
                return NhAssistantRunConfigurationProblems.Invalid;
        }
    }

    /// <summary>
    /// A five- or six-field cron expression of plain cron characters; Hangfire checks the rest when
    /// it registers the job.
    /// </summary>
    internal static bool IsValidCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron) || cron.Length > 128)
        {
            return false;
        }

        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length is 5 or 6
               && fields.All(field => field.All(character => char.IsAsciiLetterOrDigit(character) || character is '*' or ',' or '-' or '/' or '?' or 'L' or 'W' or '#'));
    }

    internal static bool TryFindTimeZone(string? id, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id))
        {
            return true;
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found))
        {
            timeZone = found;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Builds the run configuration in effect from code and data into one immutable snapshot that the
/// registration returns to every reader, so readers keep their synchronous access. It loads on first
/// use, after every change in this process and whenever the stamp changed, which every fire and every
/// run start checks at most once per second; a background refresh checks it as well.
/// </summary>
internal sealed class NhAssistantRunConfigurations(
    NhAssistantRegistrationState registration,
    NhAssistantRunConfigurationStore store,
    NhAssistantRunConfigurationValidator validator,
    IServiceProvider services,
    ILogger<NhAssistantRunConfigurations> logger)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _checkedAt = long.MinValue;

    /// <summary>
    /// Whether the host loads the run configuration defined as data: it has agent runs in code or
    /// runs agents whose run settings are data.
    /// </summary>
    public bool Enabled => registration.CodeAgentRuns.Count > 0 || registration.DataAgentRunsEnabled;

    /// <summary>
    /// Makes sure the snapshot in effect has the latest data: reads the stamp when the last check
    /// is more than a second ago and reloads when it changed. A host that loads no data reads
    /// nothing. When the store cannot be read, the configuration in effect stays.
    /// </summary>
    public async Task EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || !IsDue())
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsDue())
            {
                return;
            }

            var stamp = await store.ReadStampAsync(cancellationToken);
            Volatile.Write(ref _checkedAt, Environment.TickCount64);
            if (registration.Snapshot is { } current && current.Stamp == stamp)
            {
                return;
            }

            await LoadCoreAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            Volatile.Write(ref _checkedAt, Environment.TickCount64);
            logger.LogWarning(
                "The assistant run configuration was not reloaded ({ExceptionType}); the configuration in effect stays.",
                exception.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reloads now, after a change in this process.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadCoreAsync(cancellationToken);
            Volatile.Write(ref _checkedAt, Environment.TickCount64);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The data that is out of effect in the snapshot, and why.
    /// </summary>
    public IReadOnlyList<NhAssistantRunConfigurationProblem> Problems => registration.Snapshot?.Problems ?? [];

    private bool IsDue()
    {
        var checkedAt = Volatile.Read(ref _checkedAt);
        return registration.Snapshot is null
               || checkedAt == long.MinValue
               || Environment.TickCount64 - checkedAt >= (long)CheckInterval.TotalMilliseconds;
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        var stored = await store.LoadAsync(cancellationToken);
        var snapshot = Build(stored);
        registration.ApplySnapshot(snapshot);
        foreach (var problem in snapshot.Problems)
        {
            logger.LogWarning(
                "The {Kind} '{Id}' of agent '{AgentId}' defined as data is out of effect ({Code}).",
                problem.Kind,
                problem.Id,
                problem.AgentId,
                problem.Code);
        }

        foreach (var listener in services.GetServices<INhAssistantRunSnapshotListener>())
        {
            try
            {
                await listener.AppliedAsync(snapshot, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // The snapshot is in effect; a listener that fails tries again with the next one.
                logger.LogWarning(
                    "A listener of the assistant run configuration failed ({Listener}, {ExceptionType}).",
                    listener.GetType().Name,
                    exception.GetType().Name);
            }
        }
    }

    internal NhAssistantRunSnapshot Build(NhAssistantStoredRunConfiguration stored)
    {
        var problems = new List<NhAssistantRunConfigurationProblem>();
        var agentRuns = new Dictionary<string, NhAssistantAgentRunOptions>(registration.CodeAgentRuns, StringComparer.Ordinal);
        var runAgents = new Dictionary<string, NhAssistantRunAgent>(StringComparer.Ordinal);
        foreach (var agentId in registration.CodeAgentRuns.Keys)
        {
            if (validator.CodeAgent(agentId) is { } codeAgent)
            {
                runAgents[agentId] = codeAgent;
            }
        }

        // Agents created in the administration whose run settings are data.
        foreach (var settings in stored.AgentRuns)
        {
            IReadOnlyList<string>? agentSelectors = null;
            var problem = registration.CodeAgentRuns.ContainsKey(settings.AgentId) || registration.TryGetAgent(settings.AgentId, out _)
                ? NhAssistantRunConfigurationProblems.ConflictsWithCode
                : !stored.AdminAgents.TryGetValue(settings.AgentId, out var adminAgent)
                    ? NhAssistantRunConfigurationProblems.UnknownAgent
                    : (agentSelectors = ReadSelectors(adminAgent.ToolSelectorsJson)) is null
                        ? NhAssistantRunConfigurationProblems.Invalid
                        : validator.ValidateRunSettings(settings);
            if (problem is not null)
            {
                problems.Add(new NhAssistantRunConfigurationProblem(settings.AgentId, NhAssistantRunConfigurationProblems.AgentKind, settings.AgentId, problem));
                continue;
            }

            // What the agent works on is part of its definition; its runs follow it.
            agentRuns[settings.AgentId] = ToOptions(settings, stored.AdminAgents[settings.AgentId].WorksOn);
            runAgents[settings.AgentId] = new NhAssistantRunAgent([], agentSelectors!);
        }

        foreach (var group in stored.Skills.GroupBy(definition => definition.AgentId, StringComparer.Ordinal))
        {
            var dataSkills = new List<NhAssistantSkillOptions>();
            var runAgent = runAgents.GetValueOrDefault(group.Key);
            var candidates = group.Where(definition => definition.Status == NhAssistantSkillDefinitionStatuses.Candidate).ToArray();
            foreach (var definition in group.Where(definition => definition.Status == NhAssistantSkillDefinitionStatuses.Live))
            {
                var selectors = ReadSelectors(definition.ToolSelectorsJson);
                var problem = selectors is null
                    ? NhAssistantRunConfigurationProblems.ToolNotCovered
                    : validator.ValidateSkill(definition, selectors, runAgent);
                if (problem is null && runAgent!.CodeSkillIds.Count + dataSkills.Count >= NhAssistantAgentRunOptions.MaxSkills)
                {
                    problem = NhAssistantRunConfigurationProblems.TooManySkills;
                }
                if (problem is not null)
                {
                    problems.Add(new NhAssistantRunConfigurationProblem(definition.AgentId, NhAssistantRunConfigurationProblems.SkillKind, definition.SkillId, problem));
                    continue;
                }

                dataSkills.Add(ToSkill(definition, selectors!));
            }

            // A candidate runs next to a live version in effect, checked as that version is.
            var candidateSkills = new List<NhAssistantSkillOptions>();
            foreach (var definition in candidates)
            {
                var selectors = ReadSelectors(definition.ToolSelectorsJson);
                var problem = dataSkills.All(skill => skill.Id != definition.SkillId)
                    ? NhAssistantRunConfigurationProblems.UnknownSkill
                    : selectors is null
                        ? NhAssistantRunConfigurationProblems.ToolNotCovered
                        : validator.ValidateSkill(definition, selectors, runAgent);
                if (problem is not null)
                {
                    problems.Add(new NhAssistantRunConfigurationProblem(definition.AgentId, NhAssistantRunConfigurationProblems.CandidateKind, definition.SkillId, problem));
                    continue;
                }

                candidateSkills.Add(ToSkill(definition, selectors!));
            }

            if (dataSkills.Count > 0)
            {
                agentRuns[group.Key] = agentRuns[group.Key].WithDataSkills(dataSkills, candidateSkills);
            }
        }

        var triggers = new Dictionary<string, NhAssistantTriggerDefinition>(registration.CodeTriggers, StringComparer.Ordinal);
        foreach (var row in stored.Triggers)
        {
            var problem = validator.ValidateTrigger(row, agentRuns);
            if (problem is null && triggers.ContainsKey(row.Id))
            {
                problem = NhAssistantRunConfigurationProblems.ConflictsWithCode;
            }
            if (problem is not null)
            {
                problems.Add(new NhAssistantRunConfigurationProblem(row.AgentId, NhAssistantRunConfigurationProblems.TriggerKind, row.Id, problem));
                continue;
            }

            triggers.Add(row.Id, ToTrigger(row));
        }

        return new NhAssistantRunSnapshot(agentRuns, triggers, stored.Stamp, problems);
    }

    internal static IReadOnlyList<string>? ReadSelectors(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The run options of an agent created in the administration. It has no grants: it acts with the
    /// rights of the account it answers to.
    /// </summary>
    private NhAssistantAgentRunOptions ToOptions(AssistantAgentRunConfiguration settings, NhAssistantAgentWorksOn worksOn)
    {
        NhAssistantAccountabilities.TryParse(settings.Accountability, out var accountability);
        NhAssistantElevatedReadNames.TryParse(settings.ElevatedReads, out var elevatedReads);
        return new NhAssistantAgentRunOptions(settings.AgentId)
        {
            Accountability = accountability,
            ServiceAccountActorId = accountability == NhAiAgentAccountability.ServiceAccount ? settings.ServiceAccountActorId : null,
            DelegateFromInitiator = settings.DelegateFromInitiator,
            AllowGatewayReads = settings.AllowGatewayReads,
            ElevatedReads = elevatedReads,
            ApprovalPolicyId = settings.ApprovalPolicyId,
            RetryCount = settings.RetryCount,
            DailyBudget = settings is { DailyToolCalls: { } calls, DailyTokens: { } tokens } ? new NhAssistantAgentBudget(calls, tokens) : null,
            MaxConcurrentRuns = settings.MaxConcurrentRuns,
            MaxRunsPerSubjectPerDay = settings.MaxRunsPerSubjectPerDay,
            MaxTokensPerRun = settings.MaxTokensPerRun,
            TriageProfileName = settings.TriageProfileName,
            RuleHandlerType = settings.RuleHandlerName is { } handler ? registration.RuleHandlers[handler].HandlerType : null,
            ActWithAccountableRights = true,
            DataVersion = settings.Version,
            WorksOn = worksOn
        };
    }

    private NhAssistantSkillOptions ToSkill(AssistantAgentSkillDefinition definition, IReadOnlyList<string> selectors)
    {
        var skill = new NhAssistantSkillOptions(definition.AgentId, definition.SkillId)
        {
            Description = definition.Description,
            ProfileName = definition.ProfileName,
            RuleHandlerName = definition.RuleHandlerName,
            RuleHandlerType = definition.RuleHandlerName is { } handler ? registration.RuleHandlers[handler].HandlerType : null,
            Instructions = string.IsNullOrWhiteSpace(definition.Instructions) ? null : CreateInstructions(definition),
            MaxTokensPerRun = definition.MaxTokensPerRun,
            DailyBudget = definition is { DailyToolCalls: { } calls, DailyTokens: { } tokens } ? new NhAssistantAgentBudget(calls, tokens) : null,
            ApprovalPolicyId = definition.ApprovalPolicyId,
            // A data skill starts in shadow; an administrator puts it live like any skill.
            Shadow = true,
            DataVersion = definition.Version
        };
        skill.SetToolSelectors(selectors);
        return skill;
    }

    private static NhAiTextAsset CreateInstructions(AssistantAgentSkillDefinition definition)
    {
        var version = definition.Version.ToString(CultureInfo.InvariantCulture);
        return NhAiTextAssetFactory.Create(
            $"skill-{definition.AgentId}-{definition.SkillId}",
            definition.Version,
            definition.Instructions!,
            "assistant-skill-data:" + definition.AgentId,
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.Chat,
            [],
            "default",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Operational,
            $"{definition.AgentId}-{definition.SkillId}-v{version}");
    }

    private NhAssistantTriggerDefinition ToTrigger(AssistantAgentTriggerDefinition row)
    {
        NhAssistantTriggerConflictNames.TryParse(row.Conflict, out var conflict);
        if (row.Source == NhAssistantDataTriggerSources.Event)
        {
            var source = registration.TriggerEvents[row.EventName!];
            return new NhAssistantTriggerDefinition
            {
                Id = row.Id,
                Source = NhAssistantTriggerSource.Event,
                EventType = source.EventType,
                AgentId = row.AgentId,
                When = source.When,
                Subject = source.Subject,
                SubjectType = source.SubjectType,
                Initiator = source.Initiator,
                Message = source.Message,
                Dedupe = source.Dedupe,
                Payload = source.Payload,
                Conflict = conflict,
                Debounce = TimeSpan.FromSeconds(row.DebounceSeconds),
                SkillId = row.SkillId,
                DataVersion = row.Version,
                BlockName = row.EventName,
                Shadow = row.Shadow
            };
        }

        var subjects = registration.SubjectSets[row.SubjectSetName!];
        NhAssistantRunConfigurationValidator.TryFindTimeZone(row.TimeZone, out var timeZone);
        var message = row.Message!;
        return new NhAssistantTriggerDefinition
        {
            Id = row.Id,
            Source = NhAssistantTriggerSource.Schedule,
            AgentId = row.AgentId,
            Subject = evt => ((NhAssistantScheduledTick)evt).Subject,
            Message = _ => message,
            Dedupe = evt => NhAssistantScheduleBuilder.TickKey((NhAssistantScheduledTick)evt),
            Conflict = conflict,
            Debounce = TimeSpan.Zero,
            SkillId = row.SkillId,
            Cron = string.Join(' ', row.Cron!.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            TimeZone = timeZone,
            ScheduledSubjects = subjects.Subjects,
            DataVersion = row.Version,
            BlockName = row.SubjectSetName,
            Shadow = row.Shadow
        };
    }
}
