using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The skills and triggers defined as data, read together into a snapshot.
/// </summary>
/// <param name="AdminAgentSelectors">The tool selectors of every agent created in the
/// administration, as JSON, by agent id.</param>
internal sealed record NhAssistantStoredRunConfiguration(
    Guid? Stamp,
    IReadOnlyList<AssistantAgentRunConfiguration> AgentRuns,
    IReadOnlyDictionary<string, string> AdminAgentSelectors,
    IReadOnlyList<AssistantAgentSkillDefinition> Skills,
    IReadOnlyList<AssistantAgentTriggerDefinition> Triggers);

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
        var adminAgents = await context.Agents
            .AsNoTracking()
            .Where(agent => agent.Source == NhAssistantAgentSources.Admin)
            .Select(agent => new { agent.Id, agent.ToolSelectorsJson })
            .ToDictionaryAsync(agent => agent.Id, agent => agent.ToolSelectorsJson, StringComparer.Ordinal, cancellationToken);
        var skills = await context.AgentSkillDefinitions
            .AsNoTracking()
            .Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live)
            .OrderBy(item => item.AgentId)
            .ThenBy(item => item.SkillId)
            .ToListAsync(cancellationToken);
        var triggers = await context.AgentTriggerDefinitions
            .AsNoTracking()
            .OrderBy(item => item.AgentId)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return new NhAssistantStoredRunConfiguration(stamp, agentRuns, adminAgents, skills, triggers);
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
    /// <see cref="NhAssistantStoreOutcome.Rejected"/> while the agent has data skills or data triggers.
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
    /// Adds the next version of a data skill and makes it the live one; the live version before it
    /// becomes history. <paramref name="expectedVersion"/> is the live version the change was made
    /// on, or <see langword="null"/> for a new skill.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentSkillDefinition>> AddSkillVersionAsync(
        AssistantAgentSkillDefinition definition,
        int? expectedVersion,
        CancellationToken cancellationToken)
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

        var latest = await context.AgentSkillDefinitions
            .Where(item => item.AgentId == definition.AgentId && item.SkillId == definition.SkillId)
            .MaxAsync(item => (int?)item.Version, cancellationToken) ?? 0;
        if (live is not null)
        {
            live.Status = NhAssistantSkillDefinitionStatuses.Superseded;
        }

        definition.Id = Guid.NewGuid();
        definition.Version = latest + 1;
        definition.Status = NhAssistantSkillDefinitionStatuses.Live;
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
