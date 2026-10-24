using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Hand-offs between agents as data, within the ceiling in code (<c>UseHandOffsAsData</c>): which
/// agents may hand work on subjects of one type to an agent, with a description, a follow-up to the
/// source and a daily limit. A hand-off defined as data works as one in code (<c>AcceptHandOffs</c>):
/// the source's run gets the generated tool <c>assistant.handoff.{agentId}.{subjectType}</c> when its
/// tool selectors select it, and the depth, scope key, daily limit and shadow rules hold. Every save is
/// checked as every load checks the data and as startup checks code; a stored hand-off that no longer
/// fits is out of effect with a problem. Hand-offs in code cannot change here. Every change is audited
/// and reaches every host within seconds. The administration endpoints under
/// <c>admin/agents/{id}/hand-offs</c> use it; applications call it as well, for example from a
/// package import.
/// </summary>
public interface INhAssistantDataHandOffs
{
    /// <summary>
    /// The hand-offs the agent accepts, in code and as data with their problems, and what the
    /// ceiling lets administrators add; <see langword="null"/> for an agent that does not exist.
    /// </summary>
    Task<NhAssistantAgentHandOffsDto?> GetAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or changes the hand-off defined as data that the agent accepts about one subject type.
    /// It is in effect once saved. An agent the ceiling connects to no agent fails with
    /// <c>assistant-handoffs-not-allowed</c>, a type the agent accepts in code with
    /// <c>assistant-handoff-in-code</c>, an agent without a run configuration with
    /// <c>assistant-agent-not-configured</c>, more than
    /// <see cref="NhAssistantDataHandOffLimits.MaxHandOffsPerAgent"/> with
    /// <c>assistant-configuration-limit</c>, a stale <c>expectedVersion</c> with
    /// <c>assistant-version-conflict</c> and invalid input with field errors, such as
    /// <c>sources: not-allowed</c> for a source outside the ceiling, <c>sources: not-found</c> for a
    /// source without a run configuration, <c>subjectType: not-allowed</c> for a type the ceiling does
    /// not connect the agent about and <c>maxPerSubjectPerDay: not-allowed</c> above the ceiling's limit.
    /// </summary>
    Task<TaskResult<NhAssistantDataHandOffDto>> SaveAsync(
        string agentId,
        string subjectType,
        NhAssistantDataHandOffRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a hand-off defined as data; its tool disappears from the sources' next turns and a
    /// later call fails with <c>assistant-handoff-not-accepted</c>, while runs it started finish. Fails
    /// with <c>assistant-handoff-not-found</c> without one and with <c>assistant-handoff-in-code</c> for
    /// a hand-off in code.
    /// </summary>
    Task<TaskResult> RemoveAsync(
        string agentId,
        string subjectType,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The hand-offs one agent accepts and what the ceiling lets administrators add.
/// </summary>
/// <param name="CodeHandOffs">The hand-offs in code (<c>AcceptHandOffs</c>), read-only, ordered by subject type.</param>
/// <param name="DataHandOffs">The hand-offs defined as data, ordered by subject type.</param>
/// <param name="Ceiling">What the ceiling lets the agent accept as data; <see langword="null"/> when it
/// may accept none.</param>
/// <param name="Configured">Whether the agent has a run configuration in effect, which a hand-off
/// needs: in code, or run settings of an agent created in the administration.</param>
public sealed record NhAssistantAgentHandOffsDto(
    string AgentId,
    IReadOnlyList<NhAssistantCodeHandOffDto> CodeHandOffs,
    IReadOnlyList<NhAssistantDataHandOffDto> DataHandOffs,
    NhAssistantHandOffCeilingDto? Ceiling,
    bool Configured);

/// <param name="ToolId">The generated tool the sources' tool selectors select.</param>
public sealed record NhAssistantCodeHandOffDto(
    string SubjectType,
    IReadOnlyList<string> Sources,
    string? Description,
    bool NotifySource,
    int MaxPerSubjectPerDay,
    string ToolId);

/// <param name="InEffect">Whether the hand-off is in effect; <see langword="false"/> with
/// <paramref name="Problem"/> when it no longer fits the ceiling or the code, as a whole.</param>
/// <param name="Problem">One of <see cref="NhAssistantRunConfigurationProblems"/>.</param>
/// <param name="ToolId">The generated tool the sources' tool selectors select.</param>
public sealed record NhAssistantDataHandOffDto(
    string SubjectType,
    int Version,
    IReadOnlyList<string> Sources,
    string? Description,
    bool NotifySource,
    int MaxPerSubjectPerDay,
    string ToolId,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    string? Reason,
    bool InEffect,
    string? Problem)
{
    public DateTimeOffset CreatedAt { get; init; }

    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>
    /// The link of the hand-off (part <c>handOff:{subjectType}</c>) to the agent package it was
    /// imported from, with its locked fields, which the administration shows read-only; left out when
    /// it is not linked.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NhAssistantPackagePartLinkDto? Package { get; init; }
}

/// <summary>
/// What the ceiling lets one agent accept as data.
/// </summary>
/// <param name="SubjectTypes">Per subject type the agents that may hand off to it now: agents with a run
/// configuration the ceiling connects, never the agent itself.</param>
/// <param name="MaxPerSubjectPerDay">The highest daily limit of one hand-off.</param>
public sealed record NhAssistantHandOffCeilingDto(
    IReadOnlyList<NhAssistantHandOffAllowanceDto> SubjectTypes,
    int MaxPerSubjectPerDay)
{
    public int MaxHandOffs { get; init; } = NhAssistantDataHandOffLimits.MaxHandOffsPerAgent;

    public int MaxSources { get; init; } = NhAssistantDataHandOffLimits.MaxSources;

    public int MaxDescriptionLength { get; init; } = NhAssistantDataHandOffLimits.MaxDescriptionLength;
}

/// <param name="Sources">The agents that may hand off about the type, ordered by id.</param>
/// <param name="SourcesCreatedInAdministration">The ceiling lets every agent created in the
/// administration with run settings hand off as well, also those created later.</param>
public sealed record NhAssistantHandOffAllowanceDto(
    string SubjectType,
    IReadOnlyList<string> Sources,
    bool SourcesCreatedInAdministration);

/// <param name="Sources">The agents that may hand off, 1 to 20, within the ceiling.</param>
/// <param name="Description">What the source's model reads about the agent's work, at most 500 characters.</param>
/// <param name="NotifySource">The source gets a follow-up when the agent's run for the hand-off ends.</param>
/// <param name="MaxPerSubjectPerDay">Hand-offs one source may make about one subject per 24 hours, 1 to
/// the ceiling's limit; 5 by default, as in code.</param>
/// <param name="ExpectedVersion">The hand-off's current version; <see langword="null"/> for a new one.</param>
/// <param name="Reason">At most 500 characters.</param>
public sealed record NhAssistantDataHandOffRequest(
    IReadOnlyList<string>? Sources,
    string? Description = null,
    bool NotifySource = false,
    int? MaxPerSubjectPerDay = null,
    int? ExpectedVersion = null,
    string? Reason = null);

internal sealed class NhAssistantDataHandOffs(
    NhAssistantRegistrationState registration,
    NhAssistantRunConfigurations configurations,
    NhAssistantRunConfigurationStore store,
    NhAssistantRunConfigurationValidator validator,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentEvents events) : INhAssistantDataHandOffs
{
    private const int MaxActorIdLength = 256;

    public async Task<NhAssistantAgentHandOffsDto?> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (await agents.FindAsync(agentId, includeDisabled: true, cancellationToken) is null)
        {
            return null;
        }

        await configurations.EnsureCurrentAsync(cancellationToken);
        var code = registration.CodeAgentRuns.GetValueOrDefault(agentId);
        var problems = configurations.Problems;
        var data = (await store.ListHandOffsAsync(agentId, cancellationToken))
            .Select(item => ToDto(item, problems))
            .ToArray();
        return new NhAssistantAgentHandOffsDto(
            agentId,
            code is null
                ? []
                : code.HandOffs
                    .OrderBy(item => item.SubjectType, StringComparer.Ordinal)
                    .Select(item => new NhAssistantCodeHandOffDto(
                        item.SubjectType,
                        item.Sources,
                        item.Description,
                        item.NotifySource,
                        item.MaxPerSubjectPerDay,
                        item.ToolId))
                    .ToArray(),
            data,
            CeilingOf(agentId),
            registration.AgentRuns.ContainsKey(agentId));
    }

    public async Task<TaskResult<NhAssistantDataHandOffDto>> SaveAsync(
        string agentId,
        string subjectType,
        NhAssistantDataHandOffRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason;
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantDataHandOffDto>.Failed(invalid);
        }

        if (await RefusalOfAsync(agentId, subjectType, cancellationToken) is { } refused)
        {
            return TaskResult<NhAssistantDataHandOffDto>.Failed(refused);
        }

        var sources = (request.Sources ?? []).Select(source => source?.Trim() ?? string.Empty).ToArray();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var maxPerSubjectPerDay = request.MaxPerSubjectPerDay
                                  ?? Math.Min(NhAssistantDataHandOffLimits.DefaultPerSubjectPerDay, registration.HandOffsAsData!.MaxPerSubjectPerDay);
        var errors = new NhAssistantValidationErrors()
            .Require(NhAiSubjectRef.IsValidType(subjectType), "subjectType", NhAssistantFieldErrors.Invalid)
            .Require(sources.Length > 0, "sources", NhAssistantFieldErrors.Required)
            .Require(sources.Length <= NhAssistantDataHandOffLimits.MaxSources, "sources", NhAssistantFieldErrors.TooLong)
            .Require(
                sources.Length == 0
                || (sources.All(NhAssistantNames.IsSegment)
                    && sources.Distinct(StringComparer.Ordinal).Count() == sources.Length
                    && !sources.Contains(agentId, StringComparer.Ordinal)),
                "sources",
                NhAssistantFieldErrors.Invalid)
            .Require(description is null || description.Length <= NhAssistantDataHandOffLimits.MaxDescriptionLength, "description", NhAssistantFieldErrors.TooLong)
            .Require(maxPerSubjectPerDay is >= 1 and <= NhAssistantDataHandOffLimits.MaxPerSubjectPerDay, "maxPerSubjectPerDay", NhAssistantFieldErrors.Invalid);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantDataHandOffDto>.Failed(errors.ToResult());
        }

        var row = new AssistantAgentHandOffDefinition
        {
            AgentId = agentId,
            SubjectType = subjectType,
            SourcesJson = NhAssistantDataHandOffSources.Write(sources),
            Description = description,
            NotifySource = request.NotifySource,
            MaxPerSubjectPerDay = maxPerSubjectPerDay,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = actorId,
            Reason = reason
        };

        // The same check as every load, refused here instead of left out of effect.
        var check = validator.CheckHandOff(row, registration.AgentRuns);
        if (check.Problem is { } problem)
        {
            return TaskResult<NhAssistantDataHandOffDto>.Failed(FieldErrorOf(problem, agentId, subjectType));
        }

        var existing = await store.ListHandOffsAsync(agentId, cancellationToken);
        if (request.ExpectedVersion is null
            && existing.All(item => item.SubjectType != subjectType)
            && existing.Count >= NhAssistantDataHandOffLimits.MaxHandOffsPerAgent)
        {
            return TaskResult<NhAssistantDataHandOffDto>.Failed(
                NhAssistantErrorCodes.ConfigurationLimit,
                "The agent accepts as many hand-offs defined as data as allowed.");
        }

        var saved = await store.SaveHandOffAsync(row, request.ExpectedVersion, cancellationToken);
        if (!saved.Succeeded)
        {
            return TaskResult<NhAssistantDataHandOffDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The hand-off changed in the meantime.");
        }

        await events.HandOffDefinitionChangedAsync(
            agentId,
            subjectType,
            saved.Value!.Version,
            saved.Value.Version == 1 ? "created" : "changed",
            actorId,
            cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult<NhAssistantDataHandOffDto>.Succeeded(ToDto(saved.Value, configurations.Problems));
    }

    public async Task<TaskResult> RemoveAsync(
        string agentId,
        string subjectType,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return invalid;
        }
        if (InCode(agentId, subjectType))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.HandOffInCode, "The agent accepts hand-offs about this subject type in code.");
        }

        var removed = await store.RemoveHandOffAsync(agentId, subjectType, expectedVersion, cancellationToken);
        switch (removed.Outcome)
        {
            case NhAssistantStoreOutcome.NotFound:
                return TaskResult.Failed(NhAssistantErrorCodes.HandOffNotFound, "The agent has no such hand-off defined as data.");
            case NhAssistantStoreOutcome.Conflict or NhAssistantStoreOutcome.Rejected:
                return TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The hand-off changed in the meantime.");
        }

        await events.HandOffDefinitionChangedAsync(agentId, subjectType, removed.Value!.Version, "removed", actorId, cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    /// <summary>
    /// Why the agent may not accept a hand-off as data about <paramref name="subjectType"/> at all, or
    /// <see langword="null"/>: it exists, the ceiling connects some agent to it, it accepts no hand-off
    /// about the type in code and it has a run configuration in effect.
    /// </summary>
    private async Task<TaskResult?> RefusalOfAsync(string agentId, string subjectType, CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (registration.HandOffsAsData is not { } ceiling
            || !ceiling.AllowsTarget(agentId, validator.IsCreatedInAdministration))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.HandOffsNotAllowed, "The agent may not accept hand-offs defined as data.");
        }
        if (InCode(agentId, subjectType))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.HandOffInCode, "The agent accepts hand-offs about this subject type in code.");
        }

        await configurations.EnsureCurrentAsync(cancellationToken);
        if (!registration.AgentRuns.ContainsKey(agentId)
            || (agent.Source == NhAssistantAgentSources.Admin && await store.FindRunSettingsAsync(agentId, cancellationToken) is null))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.AgentNotConfigured, "The agent has no run configuration.");
        }

        return null;
    }

    private bool InCode(string agentId, string subjectType)
    {
        return registration.CodeAgentRuns.TryGetValue(agentId, out var code)
               && code.HandOffs.Any(item => string.Equals(item.SubjectType, subjectType, StringComparison.Ordinal));
    }

    /// <summary>
    /// The failure of a save whose hand-off would be out of effect, by the problem a load would have.
    /// </summary>
    private TaskResult FieldErrorOf(string problem, string agentId, string subjectType)
    {
        switch (problem)
        {
            case NhAssistantRunConfigurationProblems.UnknownAgent:
                return TaskResult.Failed(NhAssistantErrorCodes.AgentNotConfigured, "The agent has no run configuration.");
            case NhAssistantRunConfigurationProblems.ConflictsWithCode:
                return TaskResult.Failed(NhAssistantErrorCodes.HandOffInCode, "The agent accepts hand-offs about this subject type in code.");
        }

        var connected = registration.HandOffsAsData?.Rules.Any(rule => rule.AllowsTarget(agentId, subjectType, validator.IsCreatedInAdministration)) == true;
        return problem switch
        {
            NhAssistantRunConfigurationProblems.OutsideCeiling when !connected
                => NhAssistantValidationErrors.Failed("subjectType", NhAssistantFieldErrors.NotAllowed),
            NhAssistantRunConfigurationProblems.OutsideCeiling => NhAssistantValidationErrors.Failed("sources", NhAssistantFieldErrors.NotAllowed),
            NhAssistantRunConfigurationProblems.UnknownSource => NhAssistantValidationErrors.Failed("sources", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.NotWorkedOn => NhAssistantValidationErrors.Failed("subjectType", NhAssistantFieldErrors.Invalid),
            NhAssistantRunConfigurationProblems.LimitAboveCeiling => NhAssistantValidationErrors.Failed("maxPerSubjectPerDay", NhAssistantFieldErrors.NotAllowed),
            _ => NhAssistantValidationErrors.Failed("sources", NhAssistantFieldErrors.Invalid)
        };
    }

    /// <summary>
    /// What the ceiling lets <paramref name="agentId"/> accept: per subject type the agents with a run
    /// configuration in effect that may hand off to it.
    /// </summary>
    private NhAssistantHandOffCeilingDto? CeilingOf(string agentId)
    {
        if (registration.HandOffsAsData is not { } ceiling
            || !ceiling.AllowsTarget(agentId, validator.IsCreatedInAdministration))
        {
            return null;
        }

        var configured = registration.AgentRuns.Keys
            .Where(candidate => !string.Equals(candidate, agentId, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var allowances = ceiling.Rules
            .Where(rule => rule.AllowsTarget(agentId, rule.SubjectType, validator.IsCreatedInAdministration))
            .GroupBy(rule => rule.SubjectType, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new NhAssistantHandOffAllowanceDto(
                group.Key,
                configured
                    .Where(candidate => ceiling.Allows(agentId, group.Key, candidate, validator.IsCreatedInAdministration))
                    .ToArray(),
                group.Any(rule => rule.SourcesCreatedInAdministration)))
            .ToArray();
        return new NhAssistantHandOffCeilingDto(allowances, ceiling.MaxPerSubjectPerDay);
    }

    private NhAssistantDataHandOffDto ToDto(
        AssistantAgentHandOffDefinition item,
        IReadOnlyList<NhAssistantRunConfigurationProblem> problems)
    {
        var problem = problems
            .Where(entry => entry.AgentId == item.AgentId
                            && entry.Kind == NhAssistantRunConfigurationProblems.HandOffKind
                            && entry.Id == item.SubjectType)
            .Select(entry => entry.Code)
            .FirstOrDefault();
        var inEffect = registration.AgentRuns.TryGetValue(item.AgentId, out var runOptions)
                       && runOptions.HandOffs.Any(handOff => handOff.IsData
                                                             && string.Equals(handOff.SubjectType, item.SubjectType, StringComparison.Ordinal));
        return new NhAssistantDataHandOffDto(
            item.SubjectType,
            item.Version,
            NhAssistantDataHandOffSources.Read(item.SourcesJson) ?? [],
            item.Description,
            item.NotifySource,
            item.MaxPerSubjectPerDay,
            NhAiSubjectRef.IsValidType(item.SubjectType) && NhAssistantNames.IsSegment(item.AgentId)
                ? NhAssistantHandOffTools.ToolId(item.AgentId, item.SubjectType)
                : string.Empty,
            item.UpdatedAt,
            item.UpdatedBy,
            item.Reason,
            inEffect,
            problem)
        {
            CreatedAt = item.CreatedAt,
            CreatedBy = item.CreatedBy
        };
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
}
