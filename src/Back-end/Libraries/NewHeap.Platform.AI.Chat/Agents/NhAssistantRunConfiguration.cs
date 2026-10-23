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
    /// The most named filters of its trigger event one data trigger applies.
    /// </summary>
    public const int MaxTriggerFilters = 10;

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

    /// <summary>
    /// The most consultations defined as data of one agent, one per subject type.
    /// </summary>
    public const int MaxConsultationsPerAgent = 20;

    /// <summary>
    /// The longest instructions of a consultation defined as data, in characters.
    /// </summary>
    public const int MaxConsultationInstructionsLength = 4_000;

    /// <summary>
    /// The longest description of a consultation, in characters, in code and as data.
    /// </summary>
    public const int MaxConsultationDescriptionLength = 1_000;
}

/// <summary>
/// The names of <see cref="NhAiDataClassification"/> that data skills and agents created in the
/// administration store and the administration sends.
/// </summary>
public static class NhAssistantDataClassificationNames
{
    public const string Public = "public";

    public const string Internal = "internal";

    public const string Confidential = "confidential";

    public const string Restricted = "restricted";

    public static string NameOf(NhAiDataClassification classification)
    {
        return classification switch
        {
            NhAiDataClassification.Public => Public,
            NhAiDataClassification.Confidential => Confidential,
            NhAiDataClassification.Restricted => Restricted,
            _ => Internal
        };
    }

    public static bool TryParse(string? name, out NhAiDataClassification classification)
    {
        classification = name switch
        {
            Public => NhAiDataClassification.Public,
            Confidential => NhAiDataClassification.Confidential,
            Restricted => NhAiDataClassification.Restricted,
            _ => NhAiDataClassification.Internal
        };
        return name is Public or Internal or Confidential or Restricted;
    }
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
/// A skill, trigger or consultation defined as data that is out of effect, or a consultation that
/// works with less than it was saved with, because it no longer fits the code, for example after a
/// deploy removed the profile, policy, rule handler, event or subject set it names. The
/// administration shows it; startup does not fail on it.
/// </summary>
/// <param name="Kind"><c>agent</c> for run settings and for the instructions an agent created in the
/// administration takes from an asset of the application, <c>skill</c>, <c>candidate</c>,
/// <c>trigger</c>, <c>trigger-candidate</c> or <c>consultation</c>.</param>
/// <param name="Id">The agent id, the skill id, the trigger id or, for a consultation, its subject
/// type. A consultation that works with less has a problem per part that no longer fits.</param>
/// <param name="Code">One of <see cref="NhAssistantRunConfigurationProblems"/>.</param>
public sealed record NhAssistantRunConfigurationProblem(string AgentId, string Kind, string Id, string Code);

/// <summary>
/// Why a skill or trigger defined as data does not fit the code.
/// </summary>
public static class NhAssistantRunConfigurationProblems
{
    /// <summary>
    /// The run settings of an agent created in the administration, and the instructions it takes from
    /// an asset of the application.
    /// </summary>
    public const string AgentKind = "agent";

    public const string SkillKind = "skill";

    public const string TriggerKind = "trigger";

    /// <summary>The candidate version of a data skill.</summary>
    public const string CandidateKind = "candidate";

    /// <summary>
    /// The candidate version of a data trigger. It is checked as a live trigger is; any problem, a
    /// filter its event no longer registers included, keeps it from firing, while the live version
    /// keeps working.
    /// </summary>
    public const string TriggerCandidateKind = "trigger-candidate";

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

    /// <summary>
    /// The trigger applies a filter its event no longer registers with <c>Filter</c>. Unlike the
    /// other problems, the trigger stays registered and fails closed: every event it would start a
    /// run on is ignored with the reason <c>filter-missing</c> until the filter is removed from the
    /// trigger or registered again.
    /// </summary>
    public const string UnknownFilter = "unknown-filter";

    /// <summary>The subject set is not registered with <c>AddSubjectSet</c>.</summary>
    public const string UnknownSubjectSet = "unknown-subject-set";

    /// <summary>The trigger names a skill the agent does not have.</summary>
    public const string UnknownSkill = "unknown-skill";

    /// <summary>The schedule's cron expression or time zone is invalid.</summary>
    public const string InvalidSchedule = "invalid-schedule";

    /// <summary>
    /// The trigger event names a subject type up front that the agent does not work on, or a schedule
    /// without a subject set runs for the whole application, which the agent does not work on.
    /// </summary>
    public const string NotWorkedOn = "not-worked-on";

    /// <summary>The stored definition is invalid in another way.</summary>
    public const string Invalid = "invalid";

    /// <summary>A consultation defined as data, by its subject type.</summary>
    public const string ConsultationKind = "consultation";

    /// <summary>The consultation's subject type has no subject access resolver.</summary>
    public const string UnknownSubjectType = "unknown-subject-type";

    /// <summary>
    /// The consultation's asker policy is no longer listed with <c>AddAgentPolicy</c>; the consultation
    /// is out of effect.
    /// </summary>
    public const string UnknownAgentPolicy = "unknown-agent-policy";

    /// <summary>Another tool catalog has a tool with the consultation's tool id or export name.</summary>
    public const string ConflictsWithTool = "conflicts-with-tool";

    /// <summary>
    /// The agent may no longer get consultations as data: the application no longer calls
    /// <c>UseConsultationsAsData</c>, or the agent in code no longer calls <c>ConsultableAsData</c>.
    /// </summary>
    public const string NotConsultable = "not-consultable";

    /// <summary>
    /// The ceiling in code no longer allows a route of the consultation; the consultation stays in
    /// effect without it, read-only when no route is left.
    /// </summary>
    public const string RouteNotAllowed = "route-not-allowed";

    /// <summary>
    /// The route that executes with the agent's own rights is dropped: the agent answers to the
    /// subject's responsible person.
    /// </summary>
    public const string AgentAnswersToPerson = "agent-answers-to-person";

    /// <summary>
    /// The route that executes with the agent's own rights is dropped: the agent answers for itself
    /// without grants, such as an agent created in the administration that answers for itself.
    /// </summary>
    public const string AgentHasNoRights = "agent-has-no-rights";

    /// <summary>
    /// The route that executes with the agent's own rights is dropped: an approval policy that applies
    /// names only the person who caused the proposal.
    /// </summary>
    public const string NoApprover = "no-approver";

    /// <summary>
    /// A limit of the consultation is above the ceiling's; it works with the ceiling's.
    /// </summary>
    public const string LimitAboveCeiling = "limit-above-ceiling";

    /// <summary>
    /// The ceiling in code no longer allows request tools; the consultation stays without one.
    /// </summary>
    public const string RequestsNotAllowed = "requests-not-allowed";

    /// <summary>
    /// The ceiling in code no longer lets MCP clients see consultations; the consultation stays for
    /// chats and agents only.
    /// </summary>
    public const string McpNotAllowed = "mcp-not-allowed";

    /// <summary>
    /// A tool selector of the consultation is no longer covered by the agent's own selectors; the
    /// consultation stays in effect without it, and offers no tools at all when none is left, never
    /// every tool of the agent.
    /// </summary>
    public const string ToolsNotAllowed = "tools-not-allowed";

    /// <summary>
    /// The skill or agent takes its instructions from an asset of the application, which no longer
    /// registers an <see cref="INhAiTextAssetSource"/> (<c>UseTextAssetSource</c>).
    /// </summary>
    public const string NoAssetSource = "no-asset-source";

    /// <summary>
    /// The application's text asset source has no asset with the id and version the skill or agent
    /// names.
    /// </summary>
    public const string AssetMissing = "asset-missing";

    /// <summary>
    /// The content of the asset the skill or agent names no longer has the hash it was saved with. A
    /// changed text is never followed: an administrator saves the next version with the new hash.
    /// </summary>
    public const string AssetChanged = "asset-changed";

    /// <summary>
    /// The asset the skill or agent names cannot be instructions: it is not the asked id and version,
    /// not trusted system instructions of the application, blank, longer than inline instructions may
    /// be, or its manifest's hash is not the hash of its content.
    /// </summary>
    public const string AssetInvalid = "asset-invalid";

    /// <summary>
    /// The application's text asset source failed for the asset, and no text of it was verified
    /// before; the source is asked again later.
    /// </summary>
    public const string AssetUnavailable = "asset-unavailable";
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
/// The names of the filters a data trigger applies, as its row stores them: a JSON array of distinct
/// filter names, at most <see cref="NhAssistantAgentConfigurationLimits.MaxTriggerFilters"/>, or
/// <see langword="null"/> for none.
/// </summary>
internal static class NhAssistantDataTriggerFilters
{
    /// <summary>
    /// The longest stored array; the most filters with the longest names fit with room to spare.
    /// </summary>
    public const int MaxJsonLength = 1_000;

    /// <summary>
    /// The stored names in their order, empty for none, or <see langword="null"/> when the stored
    /// value is not a valid list of filter names.
    /// </summary>
    public static IReadOnlyList<string>? Read(string? json)
    {
        if (json is null)
        {
            return [];
        }
        if (json.Length > MaxJsonLength)
        {
            return null;
        }

        string[]? names;
        try
        {
            names = JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (names is null
            || names.Length > NhAssistantAgentConfigurationLimits.MaxTriggerFilters
            || names.Any(name => !NhAssistantTriggerFilterNames.IsValid(name))
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            return null;
        }

        return names;
    }

    /// <summary>
    /// The value a row stores for <paramref name="names"/>: <see langword="null"/> for none.
    /// </summary>
    public static string? Write(IReadOnlyList<string> names)
    {
        return names.Count == 0 ? null : JsonSerializer.Serialize(names);
    }
}

/// <summary>
/// The routes a consultation defined as data stores: a JSON array of distinct values of
/// <see cref="NhAssistantConsultationRoutes"/> with at most one route at the subject; an empty array
/// keeps the consultation read-only.
/// </summary>
internal static class NhAssistantDataConsultationRoutes
{
    /// <summary>
    /// The stored routes in order, or <see langword="null"/> when the value is not a valid list.
    /// </summary>
    public static IReadOnlyList<string>? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 200)
        {
            return null;
        }

        string[]? routes;
        try
        {
            routes = JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        return routes is not null && IsValid(routes) ? routes : null;
    }

    public static string Write(IReadOnlyList<string> routes)
    {
        return JsonSerializer.Serialize(routes);
    }

    /// <summary>
    /// Known routes, each once, and at most one of the two routes at the subject.
    /// </summary>
    public static bool IsValid(IReadOnlyList<string> routes)
    {
        return routes.All(NhAssistantConsultationBounds.IsRoute)
               && routes.Distinct(StringComparer.Ordinal).Count() == routes.Count
               && !(routes.Contains(NhAssistantConsultationRoutes.SubjectAgentRights)
                    && routes.Contains(NhAssistantConsultationRoutes.SubjectAskerRights));
    }
}

/// <summary>
/// The tool selection a consultation defined as data stores: a JSON array of distinct valid tool
/// selectors, or <see langword="null"/> for none, which offers every tool of the agent.
/// </summary>
internal static class NhAssistantDataConsultationToolSelectors
{
    /// <summary>
    /// Reads the stored selectors in order into <paramref name="selectors"/>, <see langword="null"/>
    /// for no selection; <see langword="false"/> when the stored value is not a valid list of selectors.
    /// </summary>
    public static bool TryRead(string? json, out IReadOnlyList<string>? selectors)
    {
        selectors = null;
        if (json is null)
        {
            return true;
        }
        if (json.Length > NhAssistantLimits.MaxStoredToolSelectorCharacters)
        {
            return false;
        }

        string[]? stored;
        try
        {
            stored = JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (stored is null || !IsValid(stored))
        {
            return false;
        }

        selectors = stored;
        return true;
    }

    /// <summary>
    /// The value a row stores for <paramref name="selectors"/>: <see langword="null"/> for no selection.
    /// </summary>
    public static string? Write(IReadOnlyList<string>? selectors)
    {
        return selectors is null || selectors.Count == 0 ? null : JsonSerializer.Serialize(selectors);
    }

    /// <summary>
    /// One or more valid selectors, each once, that fit the stored column.
    /// </summary>
    public static bool IsValid(IReadOnlyList<string> selectors)
    {
        return selectors.Count > 0
               && selectors.All(NhAssistantAgentDefinition.IsValidSelector)
               && selectors.Distinct(StringComparer.Ordinal).Count() == selectors.Count
               && NhAssistantAgentDefinition.FitsSelectorStorage(selectors);
    }
}

/// <summary>
/// The outcome of checking a consultation defined as data: its options in effect, or
/// <see langword="null"/> when it is out of effect, and every problem, the reason it is out of effect
/// first.
/// </summary>
internal sealed record NhAssistantDataConsultationCheck(
    NhAssistantConsultationOptions? Options,
    IReadOnlyList<string> Problems)
{
    public static NhAssistantDataConsultationCheck Out(string problem)
    {
        return new NhAssistantDataConsultationCheck(null, [problem]);
    }
}

/// <summary>
/// The tool ids and export names other tool catalogs already use, which the generated tools of a
/// consultation defined as data may not take; the ASP.NET integration reads them from the catalogs.
/// </summary>
internal interface INhAssistantReservedToolNames
{
    bool IsReserved(string toolId, string exportName);
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
    IReadOnlyList<NhAssistantRunConfigurationProblem> Problems)
{
    /// <summary>
    /// The candidate versions of data triggers that fit the code, by trigger id: each fires in the
    /// agent's shadow thread next to the live version of the same id.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantTriggerDefinition> TriggerCandidates { get; init; } =
        new Dictionary<string, NhAssistantTriggerDefinition>(StringComparer.Ordinal);

    /// <summary>
    /// The application's text asset source failed for an asset the data names while the snapshot
    /// loaded, so a later check loads again even when the stamp did not change.
    /// </summary>
    public bool AssetsUnavailable { get; init; }
}

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
    /// Whether <paramref name="profileName"/> is a profile the administration may choose for a data
    /// skill or an agent created there: registered by the application with chat, function calling
    /// and streaming, as the run catalog lists them.
    /// </summary>
    public bool IsChoosableProfile(string profileName)
    {
        return services.GetService<INhAiModelProfileRegistry>() is { } profiles
               && profiles.TryGet(profileName, out var profile)
               && (profile.Capabilities & ChatProfileCapabilities) == ChatProfileCapabilities;
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
            // A version names an asset or has text of its own, never both.
            || (NhAssistantInstructionAssets.ReferenceOf(definition) is { } reference
                && (definition.Instructions is not null || !reference.IsWellFormed))
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
        if (definition.ProfileName is { } profileName && !IsChoosableProfile(profileName))
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
    /// when it fits. A filter the trigger's event does not register is checked last, so
    /// <see cref="NhAssistantRunConfigurationProblems.UnknownFilter"/> means that everything else
    /// fits.
    /// </summary>
    public string? ValidateTrigger(
        AssistantAgentTriggerDefinition trigger,
        IReadOnlyDictionary<string, NhAssistantAgentRunOptions> agentRuns)
    {
        if (!agentRuns.TryGetValue(trigger.AgentId, out var options))
        {
            return NhAssistantRunConfigurationProblems.UnknownAgent;
        }
        var filters = NhAssistantDataTriggerFilters.Read(trigger.FiltersJson);
        if (!NhAiSubjectRef.IsValidType(trigger.Id)
            || NhAssistantTriggerIds.IsReserved(trigger.Id)
            || !NhAssistantTriggerConflictNames.TryParse(trigger.Conflict, out _)
            || trigger.DebounceSeconds is < 0 or > NhAssistantAgentConfigurationLimits.MaxDebounceSeconds
            || filters is null
            // Filters belong to trigger events; a schedule has none.
            || (filters.Count > 0 && trigger.Source != NhAssistantDataTriggerSources.Event))
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
                if (triggerEvent.SubjectType is { } subjectType && !options.WorksOn.Covers(subjectType))
                {
                    return NhAssistantRunConfigurationProblems.NotWorkedOn;
                }

                return filters.All(name => triggerEvent.Filters.Any(filter => filter.Name == name))
                    ? null
                    : NhAssistantRunConfigurationProblems.UnknownFilter;
            case NhAssistantDataTriggerSources.Schedule:
                if (trigger.SubjectSetName is { } setName && !registration.SubjectSets.ContainsKey(setName))
                {
                    return NhAssistantRunConfigurationProblems.UnknownSubjectSet;
                }
                // Without a subject set the schedule runs for the whole application, which only an
                // agent that works on it may do.
                if (trigger.SubjectSetName is null && !options.WorksOn.Application)
                {
                    return NhAssistantRunConfigurationProblems.NotWorkedOn;
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
    /// Checks a consultation defined as data against the code, on save and on every load (AC11): a
    /// consultation whose agent, subject type, what the agent works on or asker policy no longer fits
    /// is out of effect; a route that no longer fits is dropped (read-only when none is left), a
    /// request tool or MCP exposure the ceiling no longer allows is dropped and limits above the
    /// ceiling are capped, each with a problem. A tool selector that <paramref name="agentToolSelectors"/>,
    /// the agent's own selectors, no longer cover is dropped (CT4); when none is left the consultation
    /// offers no tools, never every tool of the agent. <paramref name="runOptions"/> are the agent's
    /// options in effect, <see langword="null"/> for an agent without runs. Without an asker policy the
    /// asker needs <paramref name="agentRequiredPolicy"/>, the agent's own required policy, and without
    /// that the assistant's access policy (AC14).
    /// </summary>
    public NhAssistantDataConsultationCheck CheckConsultation(
        AssistantAgentConsultationDefinition row,
        NhAssistantAgentRunOptions? runOptions,
        string? agentRequiredPolicy,
        IReadOnlyList<string> agentToolSelectors)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(agentToolSelectors);
        if (runOptions is null)
        {
            return NhAssistantDataConsultationCheck.Out(NhAssistantRunConfigurationProblems.UnknownAgent);
        }
        if (registration.ConsultationCeilingOf(row.AgentId) is not { } ceiling)
        {
            return NhAssistantDataConsultationCheck.Out(NhAssistantRunConfigurationProblems.NotConsultable);
        }

        var routes = NhAssistantDataConsultationRoutes.Read(row.RoutesJson);
        if (routes is null
            || !NhAssistantDataConsultationToolSelectors.TryRead(row.ToolSelectorsJson, out var toolSelectors)
            || !NhAiSubjectRef.IsValidType(row.SubjectType)
            || string.IsNullOrWhiteSpace(row.Description)
            || row.Description.Length > NhAssistantAgentConfigurationLimits.MaxConsultationDescriptionLength
            || row.Instructions?.Length > NhAssistantAgentConfigurationLimits.MaxConsultationInstructionsLength
            || !NhAssistantSubjectRoleNames.TryParse(row.MinimumRole, out var minimumRole)
            || !NhAssistantSubjectRoleNames.TryParse(row.ProposalRole, out var proposalRole)
            || row.MaxToolCalls is < 0 or > NhAssistantConsultationBounds.MaxToolCalls
            || row.TimeoutSeconds < NhAssistantConsultationBounds.MinTimeout.TotalSeconds
            || row.TimeoutSeconds > NhAssistantConsultationBounds.MaxTimeout.TotalSeconds
            || row.MaxAnswerChars is < NhAssistantConsultationBounds.MinAnswerChars or > NhAssistantConsultationBounds.MaxAnswerChars
            || row.MaxHistoryMessages is < 0 or > NhAssistantConsultationBounds.MaxHistoryMessages
            || (row.DataClassification is not null && !NhAssistantDataClassificationNames.TryParse(row.DataClassification, out _)))
        {
            return NhAssistantDataConsultationCheck.Out(NhAssistantRunConfigurationProblems.Invalid);
        }
        if (!KnownSubjectTypes.Contains(row.SubjectType))
        {
            return NhAssistantDataConsultationCheck.Out(NhAssistantRunConfigurationProblems.UnknownSubjectType);
        }
        if (!runOptions.WorksOn.Covers(row.SubjectType))
        {
            return NhAssistantDataConsultationCheck.Out(NhAssistantRunConfigurationProblems.NotWorkedOn);
        }
        if (registration.CodeAgentRuns.TryGetValue(row.AgentId, out var code)
            && code.Consultations.Any(item => string.Equals(item.SubjectType, row.SubjectType, StringComparison.Ordinal)))
        {
            return NhAssistantDataConsultationCheck.Out(NhAssistantRunConfigurationProblems.ConflictsWithCode);
        }
        if (row.AskerPolicy is { } askerPolicy && !registration.AgentPolicies.ContainsKey(askerPolicy))
        {
            return NhAssistantDataConsultationCheck.Out(NhAssistantRunConfigurationProblems.UnknownAgentPolicy);
        }

        var problems = new List<string>();
        var kept = new List<string>();
        foreach (var route in routes)
        {
            var agentRights = route == NhAssistantConsultationRoutes.SubjectAgentRights;
            if (!ceiling.Allows(route)
                || (agentRights && string.Equals(row.SubjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal)))
            {
                AddOnce(problems, NhAssistantRunConfigurationProblems.RouteNotAllowed);
                continue;
            }
            if (agentRights && AgentRightsProblemOf(runOptions) is { } rightsProblem)
            {
                AddOnce(problems, rightsProblem);
                continue;
            }

            kept.Add(route);
        }

        var proposalPolicyId = row.ProposalPolicyId;
        if (proposalPolicyId is not null && !registration.ApprovalPolicies.ContainsKey(proposalPolicyId))
        {
            // Without its policy nobody knows who decides a proposal: the consultation only answers.
            AddOnce(problems, NhAssistantRunConfigurationProblems.UnknownPolicy);
            kept.Clear();
            proposalPolicyId = null;
        }
        if (kept.Contains(NhAssistantConsultationRoutes.SubjectAgentRights) && NamesOnlyTheInitiator(runOptions, proposalPolicyId))
        {
            AddOnce(problems, NhAssistantRunConfigurationProblems.NoApprover);
            kept.Remove(NhAssistantConsultationRoutes.SubjectAgentRights);
        }

        var allowRequests = row.AllowRequests;
        if (allowRequests && !ceiling.AllowRequests)
        {
            problems.Add(NhAssistantRunConfigurationProblems.RequestsNotAllowed);
            allowRequests = false;
        }
        var exposeToMcp = row.ExposeToMcp;
        if (exposeToMcp && !ceiling.AllowMcp)
        {
            problems.Add(NhAssistantRunConfigurationProblems.McpNotAllowed);
            exposeToMcp = false;
        }

        var timeout = TimeSpan.FromSeconds(row.TimeoutSeconds);
        if (row.MaxToolCalls > ceiling.MaxToolCalls
            || timeout > ceiling.MaxTimeout
            || row.MaxAnswerChars > ceiling.MaxAnswerChars
            || row.MaxHistoryMessages > ceiling.MaxHistoryMessages)
        {
            problems.Add(NhAssistantRunConfigurationProblems.LimitAboveCeiling);
        }

        // The selection only narrows the agent's own tools; a selector they no longer cover drops,
        // and a selection with none left offers no tools (CT4).
        IReadOnlyList<string>? keptTools = null;
        if (toolSelectors is not null)
        {
            keptTools = toolSelectors
                .Where(selector => agentToolSelectors.Any(agentSelector => NhAssistantRegistrationState.Covers(agentSelector, selector)))
                .ToArray();
            if (keptTools.Count < toolSelectors.Count)
            {
                problems.Add(NhAssistantRunConfigurationProblems.ToolsNotAllowed);
            }
        }

        NhAssistantDataClassificationNames.TryParse(row.DataClassification, out var classification);
        var options = new NhAssistantConsultationOptions(row.AgentId, row.SubjectType)
        {
            Description = row.Description,
            MinimumRole = minimumRole,
            ProposalRole = proposalRole,
            AskerDecides = kept.Contains(NhAssistantConsultationRoutes.AskerDecides),
            AtSubjectRights = kept.Contains(NhAssistantConsultationRoutes.SubjectAgentRights)
                ? NhAssistantConsultationRights.Agent
                : kept.Contains(NhAssistantConsultationRoutes.SubjectAskerRights)
                    ? NhAssistantConsultationRights.Asker
                    : null,
            ProposalPolicyId = proposalPolicyId,
            ToolSelectors = keptTools,
            MaxToolCalls = Math.Min(row.MaxToolCalls, ceiling.MaxToolCalls),
            Timeout = timeout < ceiling.MaxTimeout ? timeout : ceiling.MaxTimeout,
            MaxAnswerChars = Math.Min(row.MaxAnswerChars, ceiling.MaxAnswerChars),
            MaxHistoryMessages = Math.Min(row.MaxHistoryMessages, ceiling.MaxHistoryMessages),
            AllowRequests = allowRequests,
            Exposure = exposeToMcp
                ? NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp
                : NhAiToolExposure.Local | NhAiToolExposure.Agent,
            Instructions = string.IsNullOrWhiteSpace(row.Instructions) ? null : CreateConsultationInstructions(row),
            // Free, as in code (AC13); without a value the agent's own classification.
            DataClassification = row.DataClassification is null
                ? runOptions.DataClassification ?? NhAiDataClassification.Internal
                : classification,
            Source = NhAssistantConsultationSources.Data,
            DataVersion = row.Version
        };
        if ((row.AskerPolicy ?? agentRequiredPolicy) is { } policy)
        {
            options.AddPolicies([policy]);
        }

        return new NhAssistantDataConsultationCheck(options, problems);
    }

    /// <summary>
    /// The subject types that have an access resolver, read once from the registered resolvers.
    /// </summary>
    internal IReadOnlySet<string> KnownSubjectTypes
    {
        get
        {
            var known = Volatile.Read(ref _knownSubjectTypes);
            if (known is not null)
            {
                return known;
            }

            using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            known = scope.ServiceProvider.GetServices<INhAiSubjectAccessResolver>()
                .Select(resolver => resolver.SubjectType)
                .ToHashSet(StringComparer.Ordinal);
            Volatile.Write(ref _knownSubjectTypes, known);
            return known;
        }
    }

    private HashSet<string>? _knownSubjectTypes;

    /// <summary>
    /// Why the route that executes with the agent's own rights does not fit the agent, or
    /// <see langword="null"/>: it needs grants of its own or a technical account it acts for.
    /// </summary>
    internal static string? AgentRightsProblemOf(NhAssistantAgentRunOptions runOptions)
    {
        if (runOptions.Accountability == NhAiAgentAccountability.SubjectResponsible)
        {
            return NhAssistantRunConfigurationProblems.AgentAnswersToPerson;
        }

        return runOptions.Accountability == NhAiAgentAccountability.Agent && runOptions.Grants.Count == 0
            ? NhAssistantRunConfigurationProblems.AgentHasNoRights
            : null;
    }

    /// <summary>
    /// Whether an approval policy that applies to the consultation's proposals names only the person
    /// who caused them, so no approver other than the asker could decide one.
    /// </summary>
    internal bool NamesOnlyTheInitiator(NhAssistantAgentRunOptions runOptions, string? proposalPolicyId)
    {
        return runOptions.ToolApprovalPolicies.Values
            .Append(proposalPolicyId)
            .Append(runOptions.ApprovalPolicyId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Any(policyId => registration.ApprovalPolicies.TryGetValue(policyId, out var policy)
                             && policy.Approvers.All(rule => rule is NhAiInitiatorApproverRule));
    }

    private static void AddOnce(List<string> problems, string problem)
    {
        if (!problems.Contains(problem, StringComparer.Ordinal))
        {
            problems.Add(problem);
        }
    }

    private static NhAiTextAsset CreateConsultationInstructions(AssistantAgentConsultationDefinition row)
    {
        var version = row.Version.ToString(CultureInfo.InvariantCulture);
        return NhAiTextAssetFactory.Create(
            $"consultation-{row.AgentId}-{row.SubjectType}",
            row.Version,
            row.Instructions!,
            "assistant-consultation-data:" + row.AgentId,
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.Chat,
            [],
            "default",
            NhAssistantDataClassificationNames.TryParse(row.DataClassification, out var classification)
                ? classification
                : NhAiDataClassification.Internal,
            NhAiRetentionCategory.Operational,
            $"{row.AgentId}-{row.SubjectType}-v{version}");
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
    NhAssistantInstructionAssets instructionAssets,
    IServiceProvider services,
    ILogger<NhAssistantRunConfigurations> logger)
{
    private readonly TimeSpan _checkInterval =
        (services.GetService<NhAssistantTiming>() ?? NhAssistantTiming.Default).RunConfigurationCheckInterval;

    private readonly TimeSpan _assetRetryInterval =
        (services.GetService<NhAssistantTiming>() ?? NhAssistantTiming.Default).InstructionAssetRetryInterval;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _checkedAt = long.MinValue;
    private long _loadedAt = long.MinValue;

    /// <summary>
    /// Whether the host loads the run configuration defined as data: it has agent runs in code, runs
    /// agents whose run settings are data, or lets administrators define consultations as data.
    /// </summary>
    public bool Enabled => registration.CodeAgentRuns.Count > 0
                           || registration.DataAgentRunsEnabled
                           || registration.ConsultationsAsData is not null;

    /// <summary>
    /// Makes sure the snapshot in effect has the latest data: reads the stamp when the last check
    /// is more than a second ago and reloads when it changed, or when the application's text asset
    /// source failed at the last load and the retry interval passed. A host that loads no data reads
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
            if (registration.Snapshot is { } current && current.Stamp == stamp && !AssetRetryDue(current))
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
               || Environment.TickCount64 - checkedAt >= (long)_checkInterval.TotalMilliseconds;
    }

    /// <summary>
    /// Whether the snapshot loaded while the application's text asset source failed and the retry
    /// interval passed since, so the source is asked again without a change of the stamp.
    /// </summary>
    private bool AssetRetryDue(NhAssistantRunSnapshot snapshot)
    {
        return snapshot.AssetsUnavailable
               && Environment.TickCount64 - Volatile.Read(ref _loadedAt) >= (long)_assetRetryInterval.TotalMilliseconds;
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        var stored = await store.LoadAsync(cancellationToken);
        // Every load asks the application's source again for every asset the data names (IA6).
        var unavailable = await instructionAssets.RefreshAsync(AssetKeysOf(stored), cancellationToken);
        var snapshot = Build(stored) with { AssetsUnavailable = unavailable };
        Volatile.Write(ref _loadedAt, Environment.TickCount64);
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

        // An agent created in the administration that takes its instructions from an asset of the
        // application is not runnable while the asset does not resolve to the hash it was saved with:
        // the problem is the agent's, and its run settings are out of effect with it.
        var agentsWithoutInstructions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (agentId, adminAgent) in stored.AdminAgents.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (adminAgent.InstructionsAsset is not { } reference
                || registration.CodeAgentRuns.ContainsKey(agentId)
                || registration.TryGetAgent(agentId, out _))
            {
                continue;
            }

            if (InstructionsProblemOf(reference, NhAssistantAgentAdministration.MaxInstructionsLength) is { } instructionsProblem)
            {
                agentsWithoutInstructions.Add(agentId);
                problems.Add(new NhAssistantRunConfigurationProblem(agentId, NhAssistantRunConfigurationProblems.AgentKind, agentId, instructionsProblem));
            }
        }

        // Agents created in the administration whose run settings are data.
        foreach (var settings in stored.AgentRuns)
        {
            if (agentsWithoutInstructions.Contains(settings.AgentId))
            {
                continue;
            }

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

            // What the agent works on and its classification are part of its definition; its runs
            // follow them.
            agentRuns[settings.AgentId] = ToOptions(settings, stored.AdminAgents[settings.AgentId]);
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
                    : validator.ValidateSkill(definition, selectors, runAgent) ?? SkillInstructionsProblemOf(definition);
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
                        : validator.ValidateSkill(definition, selectors, runAgent) ?? SkillInstructionsProblemOf(definition);
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

        AddDataConsultations(stored, agentRuns, runAgents, problems);

        var triggers = new Dictionary<string, NhAssistantTriggerDefinition>(registration.CodeTriggers, StringComparer.Ordinal);
        foreach (var row in stored.Triggers.Where(row => row.Status == NhAssistantSkillDefinitionStatuses.Live))
        {
            var problem = validator.ValidateTrigger(row, agentRuns);
            if ((problem is null or NhAssistantRunConfigurationProblems.UnknownFilter) && triggers.ContainsKey(row.Id))
            {
                problem = NhAssistantRunConfigurationProblems.ConflictsWithCode;
            }
            if (problem == NhAssistantRunConfigurationProblems.UnknownFilter)
            {
                // A filter that is gone fails closed: the trigger stays registered, so a published
                // event gets a receipt that says why nothing started, and ignores every event.
                problems.Add(new NhAssistantRunConfigurationProblem(row.AgentId, NhAssistantRunConfigurationProblems.TriggerKind, row.Id, problem));
                triggers.Add(row.Id, ToTrigger(row));
                continue;
            }
            if (problem is not null)
            {
                problems.Add(new NhAssistantRunConfigurationProblem(row.AgentId, NhAssistantRunConfigurationProblems.TriggerKind, row.Id, problem));
                continue;
            }

            triggers.Add(row.Id, ToTrigger(row));
        }

        // A candidate is checked as its live version is and fires next to it; it needs nothing of the
        // live version, which keeps working whatever becomes of the candidate.
        var triggerCandidates = new Dictionary<string, NhAssistantTriggerDefinition>(StringComparer.Ordinal);
        foreach (var row in stored.Triggers.Where(row => row.Status == NhAssistantSkillDefinitionStatuses.Candidate))
        {
            var problem = registration.CodeTriggers.ContainsKey(row.Id) || triggerCandidates.ContainsKey(row.Id)
                ? NhAssistantRunConfigurationProblems.ConflictsWithCode
                : validator.ValidateTrigger(row, agentRuns);
            if (problem is not null)
            {
                problems.Add(new NhAssistantRunConfigurationProblem(row.AgentId, NhAssistantRunConfigurationProblems.TriggerCandidateKind, row.Id, problem));
                continue;
            }

            triggerCandidates.Add(row.Id, ToTrigger(row, candidate: true));
        }

        return new NhAssistantRunSnapshot(agentRuns, triggers, stored.Stamp, problems)
        {
            TriggerCandidates = triggerCandidates
        };
    }

    /// <summary>
    /// Adds the consultations defined as data that fit the code and are on to their agents' options,
    /// checked after the run settings and skills against the agents' own tool selectors in
    /// <paramref name="runAgents"/>, and lists every problem.
    /// </summary>
    private void AddDataConsultations(
        NhAssistantStoredRunConfiguration stored,
        Dictionary<string, NhAssistantAgentRunOptions> agentRuns,
        IReadOnlyDictionary<string, NhAssistantRunAgent> runAgents,
        List<NhAssistantRunConfigurationProblem> problems)
    {
        var reserved = services.GetService<INhAssistantReservedToolNames>();
        foreach (var group in stored.Consultations.GroupBy(row => row.AgentId, StringComparer.Ordinal))
        {
            var runOptions = agentRuns.GetValueOrDefault(group.Key);
            var dataConsultations = new List<NhAssistantConsultationOptions>();
            var requiredPolicy = registration.TryGetAgent(group.Key, out var codeAgent)
                ? codeAgent.RequiredPolicy
                : stored.AdminAgents.GetValueOrDefault(group.Key)?.RequiredPolicy;
            var agentToolSelectors = runAgents.GetValueOrDefault(group.Key)?.ToolSelectors ?? [];
            foreach (var row in group)
            {
                var check = validator.CheckConsultation(row, runOptions, requiredPolicy, agentToolSelectors);
                var codes = check.Problems;
                if (check.Options is { } checkedOptions && reserved is not null && IsReserved(reserved, checkedOptions))
                {
                    codes = [NhAssistantRunConfigurationProblems.ConflictsWithTool];
                    check = check with { Options = null };
                }

                foreach (var code in codes)
                {
                    problems.Add(new NhAssistantRunConfigurationProblem(row.AgentId, NhAssistantRunConfigurationProblems.ConsultationKind, row.SubjectType, code));
                }
                // A consultation that is off is checked as every load checks it, so the administration
                // shows what would no longer fit, and offered only once it is switched on.
                if (check.Options is { } options && !row.Off)
                {
                    dataConsultations.Add(options);
                }
            }

            if (dataConsultations.Count > 0)
            {
                agentRuns[group.Key] = runOptions!.WithDataConsultations(dataConsultations);
            }
        }
    }

    private static bool IsReserved(INhAssistantReservedToolNames reserved, NhAssistantConsultationOptions options)
    {
        return reserved.IsReserved(options.ConsultToolId, NhAssistantConsultationTools.ExportNameOf(false, options.AgentId, options.SubjectType))
               || (options.AllowRequests
                   && reserved.IsReserved(options.RequestToolId, NhAssistantConsultationTools.ExportNameOf(true, options.AgentId, options.SubjectType)));
    }

    /// <summary>
    /// The assets the data names: of live and candidate skill versions and of agents created in the
    /// administration, each asset id and version once.
    /// </summary>
    private static IReadOnlyList<(string Id, int Version)> AssetKeysOf(NhAssistantStoredRunConfiguration stored)
    {
        return stored.Skills
            .Select(NhAssistantInstructionAssets.ReferenceOf)
            .Concat(stored.AdminAgents.Values.Select(agent => agent.InstructionsAsset))
            .OfType<NhAssistantInstructionAssetReference>()
            .Select(reference => (reference.AssetId, reference.Version))
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Why the asset a data skill version takes its instructions from keeps it out of effect, or
    /// <see langword="null"/> for a version that resolves or has inline instructions.
    /// </summary>
    private string? SkillInstructionsProblemOf(AssistantAgentSkillDefinition definition)
    {
        return NhAssistantInstructionAssets.ReferenceOf(definition) is { } reference
            ? InstructionsProblemOf(reference, NhAssistantAgentConfigurationLimits.MaxSkillInstructionsLength)
            : null;
    }

    /// <summary>
    /// Why <paramref name="reference"/> does not resolve to its hash after this load refreshed it.
    /// </summary>
    private string? InstructionsProblemOf(NhAssistantInstructionAssetReference reference, int maxLength)
    {
        return (instructionAssets.Find(reference, maxLength)
                ?? NhAssistantResolvedInstructions.Of(NhAssistantRunConfigurationProblems.AssetUnavailable)).Problem;
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
    private NhAssistantAgentRunOptions ToOptions(AssistantAgentRunConfiguration settings, NhAssistantStoredAdminAgent agent)
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
            WorksOn = agent.WorksOn,
            DataClassification = NhAssistantDataClassificationNames.TryParse(agent.DataClassification, out var classification)
                ? classification
                : null,
            // Switched on in the administration, the agent reads files within the application's limits
            // and the lower ones stored with it; a turn takes the lowest of them.
            FileLimits = settings.AcceptsFiles
                ? FileLimitsOf(settings.MaxFileBytes, settings.MaxFilePages, settings.MaxFilesPerTurn, settings.MaxFileTokensPerTurn)
                : null
        };
    }

    /// <summary>
    /// The lower file limits stored for an agent or a skill (D11). A stored value above the
    /// application's, after the application lowered its own, has no effect: a turn takes the lowest.
    /// </summary>
    private static NhAssistantFileLimits FileLimitsOf(int? maxFileBytes, int? maxFilePages, int? maxFilesPerTurn, int? maxFileTokensPerTurn)
    {
        return new NhAssistantFileLimits
        {
            MaxFileBytes = maxFileBytes,
            MaxFilePages = maxFilePages,
            MaxFilesPerTurn = maxFilesPerTurn,
            MaxFileTokensPerTurn = maxFileTokensPerTurn
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
            Instructions = InstructionsOf(definition),
            MaxTokensPerRun = definition.MaxTokensPerRun,
            DailyBudget = definition is { DailyToolCalls: { } calls, DailyTokens: { } tokens } ? new NhAssistantAgentBudget(calls, tokens) : null,
            ApprovalPolicyId = definition.ApprovalPolicyId,
            DataClassification = NhAssistantDataClassificationNames.TryParse(definition.DataClassification, out var classification)
                ? classification
                : null,
            FileLimits = definition.AcceptsFiles
                ? FileLimitsOf(definition.MaxFileBytes, definition.MaxFilePages, definition.MaxFilesPerTurn, definition.MaxFileTokensPerTurn)
                : null,
            // A data skill starts in shadow; an administrator puts it live like any skill.
            Shadow = true,
            DataVersion = definition.Version
        };
        skill.SetToolSelectors(selectors);
        return skill;
    }

    /// <summary>
    /// The instructions of a data skill version in effect: the application's verified asset for a
    /// version that names one, otherwise its own text, or none.
    /// </summary>
    private NhAiTextAsset? InstructionsOf(AssistantAgentSkillDefinition definition)
    {
        if (NhAssistantInstructionAssets.ReferenceOf(definition) is { } reference)
        {
            return instructionAssets.Find(reference, NhAssistantAgentConfigurationLimits.MaxSkillInstructionsLength)?.Asset;
        }

        return string.IsNullOrWhiteSpace(definition.Instructions) ? null : CreateInstructions(definition);
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
            NhAssistantDataClassificationNames.TryParse(definition.DataClassification, out var classification)
                ? classification
                : NhAiDataClassification.Internal,
            NhAiRetentionCategory.Operational,
            $"{definition.AgentId}-{definition.SkillId}-v{version}");
    }

    /// <summary>
    /// The trigger of a stored version. A <paramref name="candidate"/> always works in shadow.
    /// </summary>
    private NhAssistantTriggerDefinition ToTrigger(AssistantAgentTriggerDefinition row, bool candidate = false)
    {
        NhAssistantTriggerConflictNames.TryParse(row.Conflict, out var conflict);
        var shadow = candidate || row.Shadow;
        if (row.Source == NhAssistantDataTriggerSources.Event)
        {
            var source = registration.TriggerEvents[row.EventName!];
            var names = NhAssistantDataTriggerFilters.Read(row.FiltersJson)!;
            var filters = names
                .Select(name => source.Filters.FirstOrDefault(filter => filter.Name == name))
                .OfType<NhAssistantTriggerFilterDefinition>()
                .ToArray();
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
                Shadow = shadow,
                Candidate = candidate,
                Filters = filters,
                MissingFilters = names.Where(name => filters.All(filter => filter.Name != name)).ToArray()
            };
        }

        // A schedule without a subject set runs for the whole application.
        var subjects = row.SubjectSetName is { } setName ? registration.SubjectSets[setName] : null;
        NhAssistantRunConfigurationValidator.TryFindTimeZone(row.TimeZone, out var timeZone);
        var message = row.Message!;
        return new NhAssistantTriggerDefinition
        {
            Id = row.Id,
            Source = NhAssistantTriggerSource.Schedule,
            AgentId = row.AgentId,
            Subject = evt => ((NhAssistantScheduledTick)evt).Subject,
            SubjectType = subjects is null ? NhAiSubjectRef.SystemType : null,
            Message = _ => message,
            Dedupe = evt => NhAssistantScheduleBuilder.TickKey((NhAssistantScheduledTick)evt),
            Conflict = conflict,
            Debounce = TimeSpan.Zero,
            SkillId = row.SkillId,
            Cron = string.Join(' ', row.Cron!.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            TimeZone = timeZone,
            ScheduledSubjects = subjects?.Subjects,
            DataVersion = row.Version,
            BlockName = row.SubjectSetName,
            Shadow = shadow,
            Candidate = candidate
        };
    }
}
