using System.Text.Json;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The run configuration of agents as data: skills, and the triggers that start them, defined at
/// runtime on agents whose runs are configured in code. Data names the building blocks code
/// registered (trigger events, subject sets, rule handlers, approval policies and model profiles)
/// and is checked against them on every save, as startup checks the code. A new skill and a new
/// trigger start in shadow; an administrator puts a skill live with
/// <c>INhAssistantAgentControl.SetSkillShadowAsync</c> and a trigger with
/// <see cref="SetTriggerShadowAsync"/>. Skills and triggers in code cannot change here. Every change
/// is audited and reaches every host within seconds. The administration endpoints under
/// <c>admin/agents/{id}</c> use it; applications call it as well, for example from a package import.
/// </summary>
public interface INhAssistantAgentConfiguration
{
    /// <summary>
    /// The building blocks data can name.
    /// </summary>
    NhAssistantRunCatalogDto GetCatalog();

    /// <summary>
    /// The agent's skills and triggers, in code and as data, or <see langword="null"/> for an agent
    /// without a run configuration in code.
    /// </summary>
    Task<NhAssistantAgentConfigurationDto?> GetAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every version of a data skill, newest first; empty when the agent has no such data skill.
    /// </summary>
    Task<IReadOnlyList<NhAssistantDataSkillVersionDto>> GetSkillVersionsAsync(
        string agentId,
        string skillId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a data skill, which starts in shadow, or stores the next version of one, which takes
    /// effect at once in the mode the skill has. <c>expectedVersion</c> is the live version the
    /// change was made on, <see langword="null"/> for a new skill; otherwise the call fails with
    /// <c>assistant-version-conflict</c>.
    /// </summary>
    Task<TaskResult<NhAssistantDataSkillDto>> SaveSkillAsync(
        string agentId,
        string skillId,
        NhAssistantDataSkillRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a data skill out of effect; its versions stay as history. Fails with
    /// <c>assistant-skill-in-use</c> while a data trigger names it. Runs that work in it end with
    /// <c>assistant-skill-not-found</c>.
    /// </summary>
    Task<TaskResult> RemoveSkillAsync(
        string agentId,
        string skillId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a data trigger, which starts in shadow, or changes one in the mode it has.
    /// </summary>
    Task<TaskResult<NhAssistantDataTriggerDto>> SaveTriggerAsync(
        string agentId,
        string triggerId,
        NhAssistantDataTriggerRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    Task<TaskResult> RemoveTriggerAsync(
        string agentId,
        string triggerId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a data trigger in shadow (<see langword="true"/>) or live: in shadow its runs work in the
    /// agent's shadow thread of the subject, whatever the agent's and the skill's mode.
    /// </summary>
    Task<TaskResult<NhAssistantDataTriggerDto>> SetTriggerShadowAsync(
        string agentId,
        string triggerId,
        bool shadow,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

/// <param name="TriggerEvents">The events a data trigger can start a skill on.</param>
/// <param name="SubjectSets">The subjects a data schedule can start a skill for.</param>
/// <param name="RuleHandlers">The rule handlers a data skill can work by.</param>
/// <param name="ApprovalPolicies">The ids of the registered approval policies.</param>
/// <param name="Profiles">The model profiles with chat, function calling and streaming.</param>
public sealed record NhAssistantRunCatalogDto(
    IReadOnlyList<NhAssistantRunBlockDto> TriggerEvents,
    IReadOnlyList<NhAssistantRunBlockDto> SubjectSets,
    IReadOnlyList<NhAssistantRunBlockDto> RuleHandlers,
    IReadOnlyList<string> ApprovalPolicies,
    IReadOnlyList<string> Profiles);

public sealed record NhAssistantRunBlockDto(string Name, string Description);

/// <param name="ToolSelectors">The agent's own tool selectors in code; a data skill's tools stay
/// within them.</param>
/// <param name="Problems">Data that is out of effect because it no longer fits the code.</param>
public sealed record NhAssistantAgentConfigurationDto(
    string AgentId,
    IReadOnlyList<string> ToolSelectors,
    IReadOnlyList<NhAssistantCodeSkillDto> CodeSkills,
    IReadOnlyList<NhAssistantDataSkillDto> DataSkills,
    IReadOnlyList<NhAssistantCodeTriggerDto> CodeTriggers,
    IReadOnlyList<NhAssistantDataTriggerDto> DataTriggers,
    IReadOnlyList<NhAssistantRunConfigurationProblem> Problems);

public sealed record NhAssistantCodeSkillDto(string SkillId, string Description);

/// <param name="Source"><c>event</c>, <c>schedule</c> or <c>webhook</c>.</param>
public sealed record NhAssistantCodeTriggerDto(string TriggerId, string Source, string? SkillId);

/// <param name="InEffect">Whether the live version is in effect; <see langword="false"/> with
/// <paramref name="Problem"/> when it no longer fits the code.</param>
public sealed record NhAssistantDataSkillDto(
    string SkillId,
    int Version,
    string Description,
    string? Instructions,
    string? InstructionsHash,
    IReadOnlyList<string> Tools,
    string? Profile,
    string? RuleHandler,
    long? MaxTokensPerRun,
    int? DailyToolCalls,
    long? DailyTokens,
    string? ApprovalPolicy,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    string? Reason,
    bool InEffect,
    string? Problem);

/// <param name="Status"><c>live</c>, <c>candidate</c> or <c>superseded</c>.</param>
public sealed record NhAssistantDataSkillVersionDto(
    int Version,
    string Status,
    string Description,
    string? InstructionsHash,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? Reason);

/// <param name="Source"><c>event</c> or <c>schedule</c>.</param>
/// <param name="Conflict"><c>coalesce</c>, <c>queue</c>, <c>supersede</c> or <c>ignore</c>.</param>
public sealed record NhAssistantDataTriggerDto(
    string TriggerId,
    string? SkillId,
    string Source,
    string? Event,
    string? Cron,
    string? TimeZone,
    string? SubjectSet,
    string? Message,
    string Conflict,
    int DebounceSeconds,
    bool Shadow,
    int Version,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    string? Reason,
    bool InEffect,
    string? Problem);

/// <param name="Description">What the skill is for, at most 300 characters; triage and
/// <c>use_skill</c> choose by it.</param>
/// <param name="Instructions">Read after the agent's own, at most 20,000 characters.</param>
/// <param name="Tools">Tool ids or <c>prefix.*</c> selectors within the agent's own; empty uses the
/// agent's.</param>
/// <param name="Profile">A model profile with chat, function calling and streaming.</param>
/// <param name="RuleHandler">The name of a rule handler registered with <c>AddRuleHandler</c>.</param>
/// <param name="DailyToolCalls">With <paramref name="DailyTokens"/>, a daily budget of the skill.</param>
/// <param name="ExpectedVersion">The live version the change was made on; <see langword="null"/>
/// for a new skill.</param>
/// <param name="Reason">At most 500 characters.</param>
public sealed record NhAssistantDataSkillRequest(
    string? Description,
    string? Instructions = null,
    IReadOnlyList<string>? Tools = null,
    string? Profile = null,
    string? RuleHandler = null,
    long? MaxTokensPerRun = null,
    int? DailyToolCalls = null,
    long? DailyTokens = null,
    string? ApprovalPolicy = null,
    int? ExpectedVersion = null,
    string? Reason = null);

/// <param name="Skill">The skill the runs work in; <see langword="null"/> lets the agent's rules or
/// triage choose.</param>
/// <param name="Source"><c>event</c> or <c>schedule</c>.</param>
/// <param name="Event">For an event trigger: a trigger event registered with
/// <c>AddTriggerEvent</c>.</param>
/// <param name="Cron">For a schedule: a five- or six-field cron expression.</param>
/// <param name="TimeZone">For a schedule: an IANA or Windows time zone id; UTC by default.</param>
/// <param name="SubjectSet">For a schedule: a subject set registered with <c>AddSubjectSet</c>.</param>
/// <param name="Message">For a schedule: what every run receives, at most 2,000 characters.</param>
/// <param name="Conflict">What happens while a run on the subject is active: <c>coalesce</c>,
/// <c>queue</c>, <c>supersede</c> or <c>ignore</c>. Events coalesce and schedules ignore by
/// default.</param>
/// <param name="DebounceSeconds">For an event trigger: the window in which events coalesce, at most
/// 3,600; 120 by default.</param>
/// <param name="ExpectedVersion">The trigger's version the change was made on;
/// <see langword="null"/> for a new trigger.</param>
/// <param name="Reason">At most 500 characters.</param>
public sealed record NhAssistantDataTriggerRequest(
    string? Source,
    string? Skill = null,
    string? Event = null,
    string? Cron = null,
    string? TimeZone = null,
    string? SubjectSet = null,
    string? Message = null,
    string? Conflict = null,
    int? DebounceSeconds = null,
    int? ExpectedVersion = null,
    string? Reason = null);

internal sealed class NhAssistantAgentConfiguration(
    NhAssistantRegistrationState registration,
    NhAssistantRunConfigurations configurations,
    NhAssistantRunConfigurationStore store,
    NhAssistantRunConfigurationValidator validator,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentEvents events,
    IServiceProvider services) : INhAssistantAgentConfiguration
{
    private const int MaxActorIdLength = 256;
    private const int DefaultDebounceSeconds = 120;

    public NhAssistantRunCatalogDto GetCatalog()
    {
        var profiles = services.GetService(typeof(INhAiModelProfileRegistry)) is INhAiModelProfileRegistry registry
            ? registry.Profiles
                .Where(profile => (profile.Capabilities & (NhAiModelCapability.Chat | NhAiModelCapability.FunctionCalling | NhAiModelCapability.Streaming))
                                  == (NhAiModelCapability.Chat | NhAiModelCapability.FunctionCalling | NhAiModelCapability.Streaming))
                .Select(profile => profile.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        return new NhAssistantRunCatalogDto(
            registration.TriggerEvents.Values.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => new NhAssistantRunBlockDto(item.Name, item.Description)).ToArray(),
            registration.SubjectSets.Values.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => new NhAssistantRunBlockDto(item.Name, item.Description)).ToArray(),
            registration.RuleHandlers.Values.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => new NhAssistantRunBlockDto(item.Name, item.Description)).ToArray(),
            registration.ApprovalPolicies.Keys.Order(StringComparer.Ordinal).ToArray(),
            profiles);
    }

    public async Task<NhAssistantAgentConfigurationDto?> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (!registration.CodeAgentRuns.TryGetValue(agentId, out var code) || !registration.TryGetAgent(agentId, out var agent))
        {
            return null;
        }

        await configurations.EnsureCurrentAsync(cancellationToken);
        var problems = configurations.Problems.Where(problem => problem.AgentId == agentId).ToArray();
        var skills = (await store.ListSkillsAsync(agentId, cancellationToken))
            .Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live)
            .Select(item => ToDto(item, problems))
            .ToArray();
        var triggers = (await store.ListTriggersAsync(agentId, cancellationToken))
            .Select(item => ToDto(item, problems))
            .ToArray();
        return new NhAssistantAgentConfigurationDto(
            agentId,
            agent.ToolSelectors,
            code.Skills.Values.Select(skill => new NhAssistantCodeSkillDto(skill.Id, skill.Description)).ToArray(),
            skills,
            registration.CodeTriggers.Values
                .Where(trigger => trigger.AgentId == agentId)
                .OrderBy(trigger => trigger.Id, StringComparer.Ordinal)
                .Select(trigger => new NhAssistantCodeTriggerDto(trigger.Id, SourceName(trigger.Source), trigger.SkillId))
                .ToArray(),
            triggers,
            problems);
    }

    public async Task<IReadOnlyList<NhAssistantDataSkillVersionDto>> GetSkillVersionsAsync(
        string agentId,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        return (await store.ListSkillsAsync(agentId, cancellationToken))
            .Where(item => item.SkillId == skillId)
            .Select(item => new NhAssistantDataSkillVersionDto(item.Version, item.Status, item.Description, item.InstructionsHash, item.CreatedAt, item.CreatedBy, item.Reason))
            .ToArray();
    }

    public async Task<TaskResult<NhAssistantDataSkillDto>> SaveSkillAsync(
        string agentId,
        string skillId,
        NhAssistantDataSkillRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason;
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(invalid);
        }
        if (await RefuseAgentAsync(agentId, cancellationToken) is { } refused)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(refused);
        }

        var code = registration.CodeAgentRuns[agentId];
        if (code.Skills.ContainsKey(skillId))
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(NhAssistantErrorCodes.SkillInCode, "The skill is declared in code.");
        }

        var description = request.Description?.Trim();
        var instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim();
        var tools = (request.Tools ?? []).Select(tool => tool?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        var errors = new NhAssistantValidationErrors()
            .Require(NhAiSubjectRef.IsValidType(skillId), "skillId", NhAssistantFieldErrors.Invalid)
            .Require(!string.IsNullOrEmpty(description), "description", NhAssistantFieldErrors.Required)
            .Require(description is null || description.Length <= NhAssistantSkillBuilder.MaxDescriptionLength, "description", NhAssistantFieldErrors.TooLong)
            .Require(instructions is null || instructions.Length <= NhAssistantAgentConfigurationLimits.MaxSkillInstructionsLength, "instructions", NhAssistantFieldErrors.TooLong)
            .Require(request.MaxTokensPerRun is null or >= 1, "maxTokensPerRun", NhAssistantFieldErrors.Invalid)
            .Require((request.DailyToolCalls is null) == (request.DailyTokens is null) && request.DailyToolCalls is null or >= 1 && request.DailyTokens is null or >= 1, "dailyBudget", NhAssistantFieldErrors.Invalid);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(errors.ToResult());
        }

        var definition = new AssistantAgentSkillDefinition
        {
            AgentId = agentId,
            SkillId = skillId,
            Description = description!,
            Instructions = instructions,
            InstructionsHash = instructions is null ? null : NhAssistantAgentCatalog.ComputeHash(instructions),
            ToolSelectorsJson = JsonSerializer.Serialize(tools),
            ProfileName = Blank(request.Profile),
            RuleHandlerName = Blank(request.RuleHandler),
            MaxTokensPerRun = request.MaxTokensPerRun,
            DailyToolCalls = request.DailyToolCalls,
            DailyTokens = request.DailyTokens,
            ApprovalPolicyId = Blank(request.ApprovalPolicy),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = actorId,
            Reason = reason
        };
        if (validator.ValidateSkill(definition, tools) is { } problem)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(SkillFieldError(problem));
        }

        var live = await LiveSkillsAsync(agentId, cancellationToken);
        if (request.ExpectedVersion is null
            && !live.ContainsKey(skillId)
            && code.Skills.Count + live.Count >= NhAssistantAgentRunOptions.MaxSkills)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(NhAssistantErrorCodes.ConfigurationLimit, "The agent has as many skills as allowed.");
        }

        var saved = await store.AddSkillVersionAsync(definition, request.ExpectedVersion, cancellationToken);
        if (!saved.Succeeded)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The skill changed in the meantime.");
        }

        await events.SkillDefinitionChangedAsync(
            agentId,
            skillId,
            saved.Value!.Version,
            saved.Value.InstructionsHash,
            saved.Value.Version == 1 || request.ExpectedVersion is null ? "created" : "changed",
            actorId,
            cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult<NhAssistantDataSkillDto>.Succeeded(ToDto(saved.Value, configurations.Problems));
    }

    public async Task<TaskResult> RemoveSkillAsync(
        string agentId,
        string skillId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return invalid;
        }
        if (await RefuseAgentAsync(agentId, cancellationToken) is { } refused)
        {
            return refused;
        }
        if (registration.CodeAgentRuns[agentId].Skills.ContainsKey(skillId))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.SkillInCode, "The skill is declared in code.");
        }

        var removed = await store.RemoveSkillAsync(agentId, skillId, expectedVersion, cancellationToken);
        switch (removed.Outcome)
        {
            case NhAssistantStoreOutcome.NotFound:
                return TaskResult.Failed(NhAssistantErrorCodes.SkillNotFound, "The agent has no such data skill.");
            case NhAssistantStoreOutcome.Conflict:
                return TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The skill changed in the meantime.");
            case NhAssistantStoreOutcome.Rejected:
                return TaskResult.Failed(NhAssistantErrorCodes.SkillInUse, "A data trigger names the skill.");
        }

        await events.SkillDefinitionChangedAsync(agentId, skillId, removed.Value!.Version, removed.Value.InstructionsHash, "removed", actorId, cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    public async Task<TaskResult<NhAssistantDataTriggerDto>> SaveTriggerAsync(
        string agentId,
        string triggerId,
        NhAssistantDataTriggerRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason;
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(invalid);
        }
        if (await RefuseAgentAsync(agentId, cancellationToken) is { } refused)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(refused);
        }
        if (registration.CodeTriggers.ContainsKey(triggerId))
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantErrorCodes.TriggerInCode, "The trigger is registered in code.");
        }

        var schedule = request.Source == NhAssistantDataTriggerSources.Schedule;
        var conflict = Blank(request.Conflict) ?? (schedule ? NhAssistantTriggerConflictNames.Ignore : NhAssistantTriggerConflictNames.Coalesce);
        var errors = new NhAssistantValidationErrors()
            .Require(NhAiSubjectRef.IsValidType(triggerId) && !NhAssistantTriggerIds.IsReserved(triggerId), "triggerId", NhAssistantFieldErrors.Invalid)
            .Require(request.Source is NhAssistantDataTriggerSources.Event or NhAssistantDataTriggerSources.Schedule, "source", request.Source is null ? NhAssistantFieldErrors.Required : NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantTriggerConflictNames.TryParse(conflict, out _), "conflict", NhAssistantFieldErrors.Invalid)
            .Require(request.DebounceSeconds is null or >= 0 and <= NhAssistantAgentConfigurationLimits.MaxDebounceSeconds, "debounceSeconds", NhAssistantFieldErrors.Invalid);
        if (request.Source == NhAssistantDataTriggerSources.Event)
        {
            errors.Require(!string.IsNullOrWhiteSpace(request.Event), "event", NhAssistantFieldErrors.Required);
        }
        if (schedule)
        {
            errors
                .Require(!string.IsNullOrWhiteSpace(request.Cron), "cron", NhAssistantFieldErrors.Required)
                .Require(!string.IsNullOrWhiteSpace(request.SubjectSet), "subjectSet", NhAssistantFieldErrors.Required)
                .Require(!string.IsNullOrWhiteSpace(request.Message), "message", NhAssistantFieldErrors.Required)
                .Require(request.Message is null || request.Message.Trim().Length <= NhAssistantAgentConfigurationLimits.MaxTriggerMessageLength, "message", NhAssistantFieldErrors.TooLong);
        }
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(errors.ToResult());
        }

        var now = DateTimeOffset.UtcNow;
        var row = new AssistantAgentTriggerDefinition
        {
            Id = triggerId,
            AgentId = agentId,
            SkillId = Blank(request.Skill),
            Source = request.Source!,
            EventName = schedule ? null : Blank(request.Event),
            Cron = schedule ? Blank(request.Cron) : null,
            TimeZone = schedule ? Blank(request.TimeZone) : null,
            SubjectSetName = schedule ? Blank(request.SubjectSet) : null,
            Message = schedule ? request.Message!.Trim() : null,
            Conflict = conflict,
            DebounceSeconds = schedule ? 0 : request.DebounceSeconds ?? DefaultDebounceSeconds,
            UpdatedAt = now,
            UpdatedBy = actorId,
            Reason = reason
        };
        await configurations.EnsureCurrentAsync(cancellationToken);
        if (validator.ValidateTrigger(row, registration.AgentRuns) is { } problem)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(TriggerFieldError(problem));
        }

        var existing = await store.ListTriggersAsync(agentId, cancellationToken);
        if (request.ExpectedVersion is null
            && existing.All(item => item.Id != triggerId)
            && existing.Count >= NhAssistantAgentConfigurationLimits.MaxTriggersPerAgent)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantErrorCodes.ConfigurationLimit, "The agent has as many data triggers as allowed.");
        }

        var saved = await store.SaveTriggerAsync(row, request.ExpectedVersion, cancellationToken);
        if (saved.Outcome == NhAssistantStoreOutcome.Rejected)
        {
            // The id belongs to a data trigger of another agent; trigger ids are unique.
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantValidationErrors.Failed("triggerId", NhAssistantFieldErrors.Invalid));
        }
        if (!saved.Succeeded)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The trigger changed in the meantime.");
        }

        await events.TriggerDefinitionChangedAsync(
            agentId,
            triggerId,
            saved.Value!.SkillId,
            saved.Value.Version,
            saved.Value.Version == 1 ? "created" : "changed",
            actorId,
            cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult<NhAssistantDataTriggerDto>.Succeeded(ToDto(saved.Value, configurations.Problems));
    }

    public async Task<TaskResult> RemoveTriggerAsync(
        string agentId,
        string triggerId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return invalid;
        }
        if (registration.CodeTriggers.TryGetValue(triggerId, out var codeTrigger) && codeTrigger.AgentId == agentId)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.TriggerInCode, "The trigger is registered in code.");
        }

        var removed = await store.RemoveTriggerAsync(agentId, triggerId, expectedVersion, cancellationToken);
        switch (removed.Outcome)
        {
            case NhAssistantStoreOutcome.NotFound:
                return TaskResult.Failed(NhAssistantErrorCodes.TriggerNotFound, "The agent has no such data trigger.");
            case NhAssistantStoreOutcome.Conflict or NhAssistantStoreOutcome.Rejected:
                return TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The trigger changed in the meantime.");
        }

        await events.TriggerDefinitionChangedAsync(agentId, triggerId, removed.Value!.SkillId, removed.Value.Version, "removed", actorId, cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    public async Task<TaskResult<NhAssistantDataTriggerDto>> SetTriggerShadowAsync(
        string agentId,
        string triggerId,
        bool shadow,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(invalid);
        }
        if (registration.CodeTriggers.TryGetValue(triggerId, out var codeTrigger) && codeTrigger.AgentId == agentId)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantErrorCodes.TriggerInCode, "The trigger is registered in code.");
        }

        var before = await store.FindTriggerAsync(triggerId, cancellationToken);
        var changed = await store.SetTriggerShadowAsync(agentId, triggerId, shadow, actorId, reason, cancellationToken);
        if (changed.Outcome == NhAssistantStoreOutcome.NotFound)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantErrorCodes.TriggerNotFound, "The agent has no such data trigger.");
        }
        if (!changed.Succeeded)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The trigger changed in the meantime.");
        }

        if (before?.Shadow != shadow)
        {
            await events.TriggerDefinitionChangedAsync(
                agentId,
                triggerId,
                changed.Value!.SkillId,
                changed.Value.Version,
                shadow ? "shadow" : "live",
                actorId,
                cancellationToken);
            await configurations.ReloadAsync(cancellationToken);
        }

        return TaskResult<NhAssistantDataTriggerDto>.Succeeded(ToDto(changed.Value!, configurations.Problems));
    }

    private async Task<TaskResult?> RefuseAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        if (await agents.FindAsync(agentId, includeDisabled: true, cancellationToken) is null)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        return registration.CodeAgentRuns.ContainsKey(agentId)
            ? null
            : TaskResult.Failed(NhAssistantErrorCodes.AgentNotConfigured, "The agent has no run configuration in code.");
    }

    private async Task<IReadOnlyDictionary<string, AssistantAgentSkillDefinition>> LiveSkillsAsync(string agentId, CancellationToken cancellationToken)
    {
        return (await store.ListSkillsAsync(agentId, cancellationToken))
            .Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live)
            .ToDictionary(item => item.SkillId, StringComparer.Ordinal);
    }

    private static TaskResult? ValidateChange(string actorId, ref string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return reason?.Length > NhAssistantLimits.MaxStateReasonCharacters
            ? NhAssistantValidationErrors.Failed("reason", NhAssistantFieldErrors.TooLong)
            : null;
    }

    private static TaskResult SkillFieldError(string problem)
    {
        return problem switch
        {
            NhAssistantRunConfigurationProblems.ToolNotCovered => NhAssistantValidationErrors.Failed("tools", NhAssistantFieldErrors.Invalid),
            NhAssistantRunConfigurationProblems.UnknownProfile => NhAssistantValidationErrors.Failed("profile", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.UnknownPolicy => NhAssistantValidationErrors.Failed("approvalPolicy", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.UnknownRuleHandler => NhAssistantValidationErrors.Failed("ruleHandler", NhAssistantFieldErrors.NotFound),
            _ => NhAssistantValidationErrors.Failed("skillId", NhAssistantFieldErrors.Invalid)
        };
    }

    private static TaskResult TriggerFieldError(string problem)
    {
        return problem switch
        {
            NhAssistantRunConfigurationProblems.UnknownSkill => NhAssistantValidationErrors.Failed("skill", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.UnknownEvent => NhAssistantValidationErrors.Failed("event", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.UnknownSubjectSet => NhAssistantValidationErrors.Failed("subjectSet", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.InvalidSchedule => NhAssistantValidationErrors.Failed("cron", NhAssistantFieldErrors.Invalid),
            _ => NhAssistantValidationErrors.Failed("triggerId", NhAssistantFieldErrors.Invalid)
        };
    }

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string SourceName(NhAssistantTriggerSource source)
    {
        return source switch
        {
            NhAssistantTriggerSource.Schedule => NhAssistantDataTriggerSources.Schedule,
            NhAssistantTriggerSource.Webhook => "webhook",
            _ => NhAssistantDataTriggerSources.Event
        };
    }

    private static NhAssistantDataSkillDto ToDto(
        AssistantAgentSkillDefinition item,
        IReadOnlyList<NhAssistantRunConfigurationProblem> problems)
    {
        var problem = problems.FirstOrDefault(entry => entry.AgentId == item.AgentId
                                                       && entry.Kind == NhAssistantRunConfigurationProblems.SkillKind
                                                       && entry.Id == item.SkillId)?.Code;
        return new NhAssistantDataSkillDto(
            item.SkillId,
            item.Version,
            item.Description,
            item.Instructions,
            item.InstructionsHash,
            NhAssistantRunConfigurations.ReadSelectors(item.ToolSelectorsJson) ?? [],
            item.ProfileName,
            item.RuleHandlerName,
            item.MaxTokensPerRun,
            item.DailyToolCalls,
            item.DailyTokens,
            item.ApprovalPolicyId,
            item.CreatedAt,
            item.CreatedBy,
            item.Reason,
            problem is null,
            problem);
    }

    private static NhAssistantDataTriggerDto ToDto(
        AssistantAgentTriggerDefinition item,
        IReadOnlyList<NhAssistantRunConfigurationProblem> problems)
    {
        var problem = problems.FirstOrDefault(entry => entry.AgentId == item.AgentId
                                                       && entry.Kind == NhAssistantRunConfigurationProblems.TriggerKind
                                                       && entry.Id == item.Id)?.Code;
        return new NhAssistantDataTriggerDto(
            item.Id,
            item.SkillId,
            item.Source,
            item.EventName,
            item.Cron,
            item.TimeZone,
            item.SubjectSetName,
            item.Message,
            item.Conflict,
            item.DebounceSeconds,
            item.Shadow,
            item.Version,
            item.UpdatedAt,
            item.UpdatedBy,
            item.Reason,
            problem is null,
            problem);
    }
}
