using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// One data shadow variant of a live agent, as the administration lists it.
/// </summary>
internal sealed record NhAssistantStoredShadowVariant(string AgentId, string DisplayName, DateTimeOffset UpdatedAt);

/// <summary>
/// The hand-offs defined as data and the data shadow variants of live agents.
/// </summary>
internal sealed partial class NhAssistantRunConfigurationStore
{
    public async Task<IReadOnlyList<AssistantAgentHandOffDefinition>> ListHandOffsAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentHandOffDefinitions
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .OrderBy(item => item.SubjectType)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Adds a hand-off defined as data, or changes one when <paramref name="expectedVersion"/> is its
    /// current version; <see langword="null"/> for a new hand-off. A change is its next version.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentHandOffDefinition>> SaveHandOffAsync(
        AssistantAgentHandOffDefinition handOff,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentHandOffDefinitions.SingleOrDefaultAsync(
            item => item.AgentId == handOff.AgentId && item.SubjectType == handOff.SubjectType,
            cancellationToken);
        if (existing?.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentHandOffDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        if (existing is null)
        {
            handOff.Version = 1;
            handOff.CreatedAt = handOff.UpdatedAt;
            handOff.CreatedBy = handOff.UpdatedBy;
            handOff.ConcurrencyStamp = Guid.NewGuid();
            context.AgentHandOffDefinitions.Add(handOff);
            existing = handOff;
        }
        else
        {
            existing.SourcesJson = handOff.SourcesJson;
            existing.Description = handOff.Description;
            existing.NotifySource = handOff.NotifySource;
            existing.MaxPerSubjectPerDay = handOff.MaxPerSubjectPerDay;
            existing.Version++;
            existing.UpdatedAt = handOff.UpdatedAt;
            existing.UpdatedBy = handOff.UpdatedBy;
            existing.Reason = handOff.Reason;
            existing.ConcurrencyStamp = Guid.NewGuid();
        }

        if (!await TrySaveAsync(context, cancellationToken) || !await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentHandOffDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentHandOffDefinition>.Ok(existing);
    }

    /// <summary>
    /// Removes a hand-off defined as data. <paramref name="expectedVersion"/>, when given, must be its
    /// current version.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentHandOffDefinition>> RemoveHandOffAsync(
        string agentId,
        string subjectType,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AgentHandOffDefinitions.SingleOrDefaultAsync(
            item => item.AgentId == agentId && item.SubjectType == subjectType,
            cancellationToken);
        if (existing is null)
        {
            return NhAssistantStoreResult<AssistantAgentHandOffDefinition>.Failed(NhAssistantStoreOutcome.NotFound);
        }
        if (expectedVersion is not null && existing.Version != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantAgentHandOffDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        context.AgentHandOffDefinitions.Remove(existing);
        if (!await TrySaveAsync(context, cancellationToken) || !await ChangeStampAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantAgentHandOffDefinition>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return NhAssistantStoreResult<AssistantAgentHandOffDefinition>.Ok(existing);
    }

    /// <summary>
    /// The agents created in the administration as shadow variants of <paramref name="liveAgentId"/>,
    /// ordered by id.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantStoredShadowVariant>> ListShadowVariantsAsync(
        string liveAgentId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Agents
            .AsNoTracking()
            .Where(agent => agent.Source == NhAssistantAgentSources.Admin && agent.ShadowOfAgentId == liveAgentId)
            .OrderBy(agent => agent.Id)
            .Select(agent => new NhAssistantStoredShadowVariant(agent.Id, agent.DisplayName, agent.UpdatedAt))
            .ToListAsync(cancellationToken);
    }
}
