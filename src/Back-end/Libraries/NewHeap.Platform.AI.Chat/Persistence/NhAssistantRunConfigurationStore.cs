using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The skills and triggers defined as data, read together into a snapshot.
/// </summary>
/// <param name="AdminAgents">The tool selectors, as JSON, what it works on and the data classification
/// of every agent created in the administration, by agent id.</param>
internal sealed record NhAssistantStoredRunConfiguration(
    Guid? Stamp,
    IReadOnlyList<AssistantAgentRunConfiguration> AgentRuns,
    IReadOnlyDictionary<string, NhAssistantStoredAdminAgent> AdminAgents,
    IReadOnlyList<AssistantAgentSkillDefinition> Skills,
    IReadOnlyList<AssistantAgentTriggerDefinition> Triggers)
{
    /// <summary>
    /// The consultations defined as data, ordered by agent and subject type.
    /// </summary>
    public IReadOnlyList<AssistantAgentConsultationDefinition> Consultations { get; init; } = [];
}

/// <summary>
/// What the run configuration reads of an agent created in the administration.
/// </summary>
/// <param name="DataClassification">The classification of its model calls, one of
/// <see cref="NhAssistantDataClassificationNames"/>; <see langword="null"/> for none.</param>
/// <param name="RequiredPolicy">The policy a person needs to use the agent; a consultation defined as
/// data without an asker policy requires it of the asker.</param>
internal sealed record NhAssistantStoredAdminAgent(
    string ToolSelectorsJson,
    NhAssistantAgentWorksOn WorksOn,
    string? DataClassification = null,
    string? RequiredPolicy = null);

/// <summary>
/// Persistence of the run configuration defined as data: the versions of data skills and the data
/// triggers. Every change also changes the stamp in the same transaction, so every host reloads.
/// Every call uses its own short-lived context.
/// </summary>
internal sealed class NhAssistantRunConfigurationStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// The id of the one stamp row.
    /// </summary>
    internal const int StampId = 1;

    public async Task<Guid?> ReadStampAsync(CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var stamp = await context.RunConfigurationStamps
            .AsNoTracking()
            .Where(item => item.Id == StampId)
            .Select(item => (Guid?)item.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return stamp;
    }

    /// <summary>
    /// The stamp first, then the live skill versions and every trigger: a change between the reads
    /// shows as a newer stamp at the next check.
    /// </summary>
    public async Task<NhAssistantStoredRunConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        var stamp = await ReadStampAsync(cancellationToken);
        await using var context = contextFactory.CreateDbContext();
        var agentRuns = await context.AgentRunConfigurations
            .AsNoTracking()
            .OrderBy(item => item.AgentId)
            .ToListAsync(cancellationToken);
        var adminAgents = (await context.Agents
                .AsNoTracking()
                .Where(agent => agent.Source == NhAssistantAgentSources.Admin)
                .Select(agent => new AssistantAgent
                {
                    Id = agent.Id,
                    ToolSelectorsJson = agent.ToolSelectorsJson,
                    WorksOnSubjectTypesJson = agent.WorksOnSubjectTypesJson,
                    WorksOnAllSubjects = agent.WorksOnAllSubjects,
                    WorksOnApplication = agent.WorksOnApplication,
                    PerDivision = agent.PerDivision,
                    RunPolicy = agent.RunPolicy,
                    ViewPolicy = agent.ViewPolicy,
                    DataClassification = agent.DataClassification,
                    RequiredPolicy = agent.RequiredPolicy
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(
                agent => agent.Id,
                agent => new NhAssistantStoredAdminAgent(
                    agent.ToolSelectorsJson,
                    NhAssistantAgentWorksOn.FromRow(agent),
                    agent.DataClassification,
                    agent.RequiredPolicy),
                StringComparer.Ordinal);
        var skills = await context.AgentSkillDefinitions
            .AsNoTracking()
            .Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live
                           || item.Status == NhAssistantSkillDefinitionStatuses.Candidate)
            .OrderBy(item => item.AgentId)
            .ThenBy(item => item.SkillId)
            .ThenBy(item => item.Version)
            .ToListAsync(cancellationToken);
        var triggers = await context.AgentTriggerDefinitions
            .AsNoTracking()
            .OrderBy(item => item.AgentId)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var consultations = await context.AgentConsultationDefinitions
            .AsNoTracking()
            .OrderBy(item => item.AgentId)
            .ThenBy(item => item.SubjectType)
            .ToListAsync(cancellationToken);
        return new NhAssistantStoredRunConfiguration(stamp, agentRuns, adminAgents, skills, triggers)
        {
            Consultations = consultations
        };
    }

    public async Task<AssistantAgentRunConfiguration?> FindRunSettingsAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentRunConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.AgentId == agentId, cancellationToken);
    }

    /// <summary>
    /// Adds the run settings of an agent, or changes them when <paramref name="expectedVersion"/> is
    /// their current version.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentRunConfiguration>> SaveRunSettingsAsync(
        AssistantAgentRunConfiguration settings,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentRunConfigurations.SingleOrDefaultAsync(item => item.AgentId == settings.AgentId, cancellationToken);
        if (existing?.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentRunConfiguration>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (existing is null)
        {
            settings.Version = 1;
            settings.CreatedAt = settings.UpdatedAt;
            settings.CreatedBy = settings.UpdatedBy;
            settings.ConcurrencyStamp = Guid.NewGuid();
            context.AgentRunConfigurations.Add(settings);
            existing = settings;
        }
        else
        {
            existing.Accountability = settings.Accountability;
            existing.ServiceAccountActorId = settings.ServiceAccountActorId;
            existing.DelegateFromInitiator = settings.DelegateFromInitiator;
            existing.AllowGatewayReads = settings.AllowGatewayReads;
            existing.ElevatedReads = settings.ElevatedReads;
            existing.ApprovalPolicyId = settings.ApprovalPolicyId;
            existing.RetryCount = settings.RetryCount;
            existing.DailyToolCalls = settings.DailyToolCalls;
            existing.DailyTokens = settings.DailyTokens;
            existing.MaxConcurrentRuns = settings.MaxConcurrentRuns;
            existing.MaxRunsPerSubjectPerDay = settings.MaxRunsPerSubjectPerDay;
            existing.MaxTokensPerRun = settings.MaxTokensPerRun;
            existing.TriageProfileName = settings.TriageProfileName;
            existing.RuleHandlerName = settings.RuleHandlerName;
            existing.AcceptsFiles = settings.AcceptsFiles;
            existing.MaxFileBytes = settings.MaxFileBytes;
            existing.MaxFilePages = settings.MaxFilePages;
            existing.MaxFilesPerTurn = settings.MaxFilesPerTurn;
            existing.MaxFileTokensPerTurn = settings.MaxFileTokensPerTurn;
            existing.Version++;
            existing.UpdatedAt = settings.UpdatedAt;
            existing.UpdatedBy = settings.UpdatedBy;
            existing.Reason = settings.Reason;
            existing.ConcurrencyStamp = Guid.NewGuid();
        }

        if (!await TrySaveAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentRunConfiguration>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (!await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentRunConfiguration>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentRunConfiguration>.Ok(existing);
    }

    /// <summary>
    /// Removes the run settings of an agent, which then only chats. Fails with
    /// <see cref="NhAssistantStoreOutcome.Rejected"/> while the agent has data skills, data triggers or
    /// data consultations.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentRunConfiguration>> RemoveRunSettingsAsync(
        string agentId,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentRunConfigurations.SingleOrDefaultAsync(item => item.AgentId == agentId, cancellationToken);
        if (existing is null)
        {
            return NhAssistantStoreResult<AssistantAgentRunConfiguration>.Failed(NhAssistantStoreOutcome.NotFound);
        }
        if (expectedVersion is not null && existing.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentRunConfiguration>.Failed(NhAssistantStoreOutcome.Conflict);
        }
        if (await context.AgentTriggerDefinitions.AnyAsync(item => item.AgentId == agentId, cancellationToken)
            || await context.AgentConsultationDefinitions.AnyAsync(item => item.AgentId == agentId, cancellationToken)
            || await context.AgentSkillDefinitions.AnyAsync(
                item => item.AgentId == agentId && item.Status != NhAssistantSkillDefinitionStatuses.Superseded,
                cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentRunConfiguration>.Failed(NhAssistantStoreOutcome.Rejected);
        }

        context.AgentRunConfigurations.Remove(existing);
        if (!await TrySaveAsync(context, cancellationToken) || !await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentRunConfiguration>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentRunConfiguration>.Ok(existing);
    }

    /// <summary>
    /// Changes the stamp after a change outside the run configuration that it depends on, such as an
    /// administration agent that was edited or deleted.
    /// </summary>
    public async Task TouchAsync(CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await ChangeStampAsync(context, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AssistantAgentSkillDefinition>> ListSkillsAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentSkillDefinitions
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .OrderBy(item => item.SkillId)
            .ThenByDescending(item => item.Version)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantAgentTriggerDefinition>> ListTriggersAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentTriggerDefinitions
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<AssistantAgentTriggerDefinition?> FindTriggerAsync(string triggerId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentTriggerDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == triggerId, cancellationToken);
    }

    /// <summary>
    /// Adds the next version of a data skill. Live, it replaces the live version, which becomes
    /// history; as a <paramref name="candidate"/>, it runs in shadow next to the live version until it
    /// is promoted or discarded. Either way an earlier candidate is discarded.
    /// <paramref name="expectedVersion"/> is the live version the change was made on, or
    /// <see langword="null"/> for a new skill, which has no candidate.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentSkillDefinition>> AddSkillVersionAsync(
        AssistantAgentSkillDefinition definition,
        int? expectedVersion,
        CancellationToken cancellationToken,
        bool candidate = false)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var live = await context.AgentSkillDefinitions
            .Where(item => item.AgentId == definition.AgentId
                           && item.SkillId == definition.SkillId
                           && item.Status == NhAssistantSkillDefinitionStatuses.Live)
            .SingleOrDefaultAsync(cancellationToken);
        if (live?.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (candidate && live is null)
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.Rejected);
        }

        var latest = await context.AgentSkillDefinitions
            .Where(item => item.AgentId == definition.AgentId && item.SkillId == definition.SkillId)
            .MaxAsync(item => (int?)item.Version, cancellationToken) ?? 0;
        if (live is not null && !candidate)
        {
            live.Status = NhAssistantSkillDefinitionStatuses.Superseded;
        }

        await DiscardCandidatesAsync(context, definition.AgentId, definition.SkillId, cancellationToken);
        definition.Id = Guid.NewGuid();
        definition.Version = latest + 1;
        definition.Status = candidate ? NhAssistantSkillDefinitionStatuses.Candidate : NhAssistantSkillDefinitionStatuses.Live;
        context.AgentSkillDefinitions.Add(definition);
        if (!await TrySaveAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (!await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Ok(definition);
    }

    /// <summary>
    /// Takes a data skill out of effect: its live version becomes history and its runtime state is
    /// removed. Fails with
    /// <see cref="NhAssistantStoreOutcome.Rejected"/> while a data trigger names the skill.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentSkillDefinition>> RemoveSkillAsync(
        string agentId,
        string skillId,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var live = await context.AgentSkillDefinitions
            .Where(item => item.AgentId == agentId
                           && item.SkillId == skillId
                           && item.Status == NhAssistantSkillDefinitionStatuses.Live)
            .SingleOrDefaultAsync(cancellationToken);
        if (live is null)
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.NotFound);
        }
        if (expectedVersion is not null && live.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }
        if (await context.AgentTriggerDefinitions.AnyAsync(item => item.AgentId == agentId && item.SkillId == skillId, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.Rejected);
        }

        live.Status = NhAssistantSkillDefinitionStatuses.Superseded;
        await DiscardCandidatesAsync(context, agentId, skillId, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // A skill added again under the same id starts over: in shadow and active.
        await context.AgentSkillStates
            .Where(item => item.AgentId == agentId && item.SkillId == skillId)
            .ExecuteDeleteAsync(cancellationToken);
        if (!await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Ok(live);
    }

    /// <summary>
    /// Makes the candidate of <paramref name="candidateVersion"/> the live version of its skill; the
    /// live version becomes history. Fails with <see cref="NhAssistantStoreOutcome.NotFound"/> without
    /// a candidate and with <see cref="NhAssistantStoreOutcome.Conflict"/> for another version.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentSkillDefinition>> PromoteCandidateAsync(
        string agentId,
        string skillId,
        int candidateVersion,
        CancellationToken cancellationToken)
    {
        return await ChangeCandidateAsync(agentId, skillId, candidateVersion, promote: true, cancellationToken);
    }

    /// <summary>
    /// Discards the candidate of <paramref name="candidateVersion"/>; it stays as history.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentSkillDefinition>> DiscardCandidateAsync(
        string agentId,
        string skillId,
        int candidateVersion,
        CancellationToken cancellationToken)
    {
        return await ChangeCandidateAsync(agentId, skillId, candidateVersion, promote: false, cancellationToken);
    }

    private async Task<NhAssistantStoreResult<AssistantAgentSkillDefinition>> ChangeCandidateAsync(
        string agentId,
        string skillId,
        int candidateVersion,
        bool promote,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var versions = await context.AgentSkillDefinitions
            .Where(item => item.AgentId == agentId
                           && item.SkillId == skillId
                           && (item.Status == NhAssistantSkillDefinitionStatuses.Live
                               || item.Status == NhAssistantSkillDefinitionStatuses.Candidate))
            .ToListAsync(cancellationToken);
        var candidate = versions.SingleOrDefault(item => item.Status == NhAssistantSkillDefinitionStatuses.Candidate);
        if (candidate is null)
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.NotFound);
        }
        if (candidate.Version != candidateVersion)
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (promote)
        {
            foreach (var live in versions.Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live))
            {
                live.Status = NhAssistantSkillDefinitionStatuses.Superseded;
            }

            candidate.Status = NhAssistantSkillDefinitionStatuses.Live;
        }
        else
        {
            candidate.Status = NhAssistantSkillDefinitionStatuses.Discarded;
        }

        if (!await TrySaveAsync(context, cancellationToken) || !await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentSkillDefinition>.Ok(candidate);
    }

    private static async Task DiscardCandidatesAsync(
        NhAssistantDbContext context,
        string agentId,
        string skillId,
        CancellationToken cancellationToken)
    {
        var candidates = await context.AgentSkillDefinitions
            .Where(item => item.AgentId == agentId
                           && item.SkillId == skillId
                           && item.Status == NhAssistantSkillDefinitionStatuses.Candidate)
            .ToListAsync(cancellationToken);
        foreach (var earlier in candidates)
        {
            earlier.Status = NhAssistantSkillDefinitionStatuses.Discarded;
        }
    }

    /// <summary>
    /// Adds a data trigger, or changes one when <paramref name="expectedVersion"/> is its current
    /// version. A new trigger starts in shadow; a change keeps the trigger's mode.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentTriggerDefinition>> SaveTriggerAsync(
        AssistantAgentTriggerDefinition trigger,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentTriggerDefinitions.SingleOrDefaultAsync(item => item.Id == trigger.Id, cancellationToken);
        if (existing is not null && existing.AgentId != trigger.AgentId)
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.Rejected);
        }
        if (existing?.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (existing is null)
        {
            trigger.Version = 1;
            trigger.Shadow = true;
            trigger.CreatedAt = trigger.UpdatedAt;
            trigger.CreatedBy = trigger.UpdatedBy;
            trigger.ConcurrencyStamp = Guid.NewGuid();
            context.AgentTriggerDefinitions.Add(trigger);
            existing = trigger;
        }
        else
        {
            existing.SkillId = trigger.SkillId;
            existing.Source = trigger.Source;
            existing.EventName = trigger.EventName;
            existing.FiltersJson = trigger.FiltersJson;
            existing.Cron = trigger.Cron;
            existing.TimeZone = trigger.TimeZone;
            existing.SubjectSetName = trigger.SubjectSetName;
            existing.Message = trigger.Message;
            existing.Conflict = trigger.Conflict;
            existing.DebounceSeconds = trigger.DebounceSeconds;
            existing.Version++;
            existing.UpdatedAt = trigger.UpdatedAt;
            existing.UpdatedBy = trigger.UpdatedBy;
            existing.Reason = trigger.Reason;
            existing.ConcurrencyStamp = Guid.NewGuid();
        }

        if (!await TrySaveAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (!await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Ok(existing);
    }

    /// <summary>
    /// Puts a data trigger in shadow or live.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentTriggerDefinition>> SetTriggerShadowAsync(
        string agentId,
        string triggerId,
        bool shadow,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentTriggerDefinitions.SingleOrDefaultAsync(
            item => item.Id == triggerId && item.AgentId == agentId,
            cancellationToken);
        if (existing is null)
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.NotFound);
        }
        if (existing.Shadow == shadow)
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Ok(existing);
        }

        existing.Shadow = shadow;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.UpdatedBy = actorId;
        existing.Reason = reason;
        existing.ConcurrencyStamp = Guid.NewGuid();
        if (!await TrySaveAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (!await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Ok(existing);
    }

    public async Task<NhAssistantStoreResult<AssistantAgentTriggerDefinition>> RemoveTriggerAsync(
        string agentId,
        string triggerId,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentTriggerDefinitions.SingleOrDefaultAsync(
            item => item.Id == triggerId && item.AgentId == agentId,
            cancellationToken);
        if (existing is null)
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.NotFound);
        }
        if (expectedVersion is not null && existing.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        context.AgentTriggerDefinitions.Remove(existing);
        if (!await TrySaveAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (!await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentTriggerDefinition>.Ok(existing);
    }

    public async Task<IReadOnlyList<AssistantAgentConsultationDefinition>> ListConsultationsAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentConsultationDefinitions
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .OrderBy(item => item.SubjectType)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Adds a consultation defined as data, or changes one when <paramref name="expectedVersion"/> is
    /// its current version; <see langword="null"/> for a new consultation. A change is its next
    /// version.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentConsultationDefinition>> SaveConsultationAsync(
        AssistantAgentConsultationDefinition consultation,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentConsultationDefinitions.SingleOrDefaultAsync(
            item => item.AgentId == consultation.AgentId && item.SubjectType == consultation.SubjectType,
            cancellationToken);
        if (existing?.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentConsultationDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (existing is null)
        {
            consultation.Version = 1;
            consultation.CreatedAt = consultation.UpdatedAt;
            consultation.CreatedBy = consultation.UpdatedBy;
            consultation.ConcurrencyStamp = Guid.NewGuid();
            context.AgentConsultationDefinitions.Add(consultation);
            existing = consultation;
        }
        else
        {
            existing.Description = consultation.Description;
            existing.Instructions = consultation.Instructions;
            existing.InstructionsHash = consultation.InstructionsHash;
            existing.MinimumRole = consultation.MinimumRole;
            existing.AskerPolicy = consultation.AskerPolicy;
            existing.RoutesJson = consultation.RoutesJson;
            existing.ProposalRole = consultation.ProposalRole;
            existing.ProposalPolicyId = consultation.ProposalPolicyId;
            existing.MaxToolCalls = consultation.MaxToolCalls;
            existing.TimeoutSeconds = consultation.TimeoutSeconds;
            existing.MaxAnswerChars = consultation.MaxAnswerChars;
            existing.MaxHistoryMessages = consultation.MaxHistoryMessages;
            existing.AllowRequests = consultation.AllowRequests;
            existing.ExposeToMcp = consultation.ExposeToMcp;
            existing.DataClassification = consultation.DataClassification;
            existing.Version++;
            existing.UpdatedAt = consultation.UpdatedAt;
            existing.UpdatedBy = consultation.UpdatedBy;
            existing.Reason = consultation.Reason;
            existing.ConcurrencyStamp = Guid.NewGuid();
        }

        if (!await TrySaveAsync(context, cancellationToken) || !await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentConsultationDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentConsultationDefinition>.Ok(existing);
    }

    /// <summary>
    /// Removes a consultation defined as data. <paramref name="expectedVersion"/>, when given, must be
    /// its current version.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentConsultationDefinition>> RemoveConsultationAsync(
        string agentId,
        string subjectType,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentConsultationDefinitions.SingleOrDefaultAsync(
            item => item.AgentId == agentId && item.SubjectType == subjectType,
            cancellationToken);
        if (existing is null)
        {
            return NhAssistantStoreResult<AssistantAgentConsultationDefinition>.Failed(NhAssistantStoreOutcome.NotFound);
        }
        if (expectedVersion is not null && existing.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentConsultationDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        context.AgentConsultationDefinitions.Remove(existing);
        if (!await TrySaveAsync(context, cancellationToken) || !await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentConsultationDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentConsultationDefinition>.Ok(existing);
    }

    private static async Task<bool> TrySaveAsync(NhAssistantDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // A concurrent change took the version or the id first.
            return false;
        }
    }

    /// <summary>
    /// Gives the stamp a new value in the change's transaction. The first change of a database adds
    /// the row; when two first changes race, the later one fails as a conflict and is tried again.
    /// </summary>
    private static async Task<bool> ChangeStampAsync(NhAssistantDbContext context, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var value = Guid.NewGuid();
        var changed = await context.RunConfigurationStamps
            .Where(item => item.Id == StampId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Value, value)
                    .SetProperty(item => item.ChangedAt, now),
                cancellationToken);
        if (changed > 0)
        {
            return true;
        }

        context.RunConfigurationStamps.Add(new AssistantRunConfigurationStamp { Id = StampId, Value = value, ChangedAt = now });
        return await TrySaveAsync(context, cancellationToken);
    }
}
