using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// A change of one metadata entry and how it went: <c>created</c>, <c>changed</c> or <c>removed</c>.
/// </summary>
internal sealed record NhAssistantAgentMetadataChange(string Name, string Change, int Version);

/// <summary>
/// The stored values of agent metadata: those of agents created at runtime, versioned with the agent,
/// and the overrides of editable entries of agents in code, with versions of their own. Every call uses
/// its own short-lived context; versioned changes are optimistic.
/// </summary>
internal sealed class NhAssistantAgentMetadataStore(NhAssistantDbContextFactory contextFactory)
{
    public const string Created = "created";
    public const string Changed = "changed";
    public const string Removed = "removed";

    public async Task<IReadOnlyList<AssistantAgentMetadata>> ListAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentMetadata
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The stored values of several agents in one read.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentMetadata>> ListAsync(
        IReadOnlyCollection<string> agentIds,
        CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0)
        {
            return [];
        }

        var ids = agentIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentMetadata
            .AsNoTracking()
            .Where(item => ids.Contains(item.AgentId))
            .OrderBy(item => item.AgentId)
            .ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The stored values of entry <paramref name="name"/> whose searchable value is
    /// <paramref name="indexValue"/>, already normalized.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentMetadata>> FindByIndexAsync(
        string name,
        string indexValue,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentMetadata
            .AsNoTracking()
            .Where(item => item.Name == name && item.IndexValue == indexValue)
            .OrderBy(item => item.AgentId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Changes values of an agent created at runtime as its next version: when the agent's version is
    /// <paramref name="expectedAgentVersion"/>, the version goes up and each value is stored, or
    /// removed for <see langword="null"/>, with it. Returns the agent's new version and what changed.
    /// </summary>
    public async Task<NhAssistantStoreResult<(int Version, IReadOnlyList<NhAssistantAgentMetadataChange> Changes)>> SaveDataAsync(
        string agentId,
        int expectedAgentVersion,
        IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?> values,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var agent = await context.Agents.SingleOrDefaultAsync(item => item.Id == agentId, cancellationToken);
        if (agent is null)
        {
            return NhAssistantStoreResult<(int, IReadOnlyList<NhAssistantAgentMetadataChange>)>.Failed(NhAssistantStoreOutcome.NotFound);
        }
        if (agent.Source != NhAssistantAgentSources.Admin)
        {
            return NhAssistantStoreResult<(int, IReadOnlyList<NhAssistantAgentMetadataChange>)>.Failed(NhAssistantStoreOutcome.Rejected);
        }
        if (agent.Version != expectedAgentVersion)
        {
            return NhAssistantStoreResult<(int, IReadOnlyList<NhAssistantAgentMetadataChange>)>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        agent.Version++;
        agent.UpdatedAt = now;
        agent.UpdatedBy = actorId;
        var changes = await ApplyDataAsync(context, agentId, agent.Version, values, actorId, now, cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NhAssistantStoreResult<(int, IReadOnlyList<NhAssistantAgentMetadataChange>)>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        return NhAssistantStoreResult<(int, IReadOnlyList<NhAssistantAgentMetadataChange>)>.Ok((agent.Version, changes));
    }

    /// <summary>
    /// Stores or removes values of an agent created at runtime in <paramref name="context"/>, which
    /// saves them together with the agent's version <paramref name="version"/>.
    /// </summary>
    public static async Task<IReadOnlyList<NhAssistantAgentMetadataChange>> ApplyDataAsync(
        NhAssistantDbContext context,
        string agentId,
        int version,
        IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?> values,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var names = values.Keys.ToArray();
        var existing = await context.AgentMetadata
            .Where(item => item.AgentId == agentId && names.Contains(item.Name))
            .ToDictionaryAsync(item => item.Name, StringComparer.Ordinal, cancellationToken);
        var changes = new List<NhAssistantAgentMetadataChange>(values.Count);
        foreach (var (name, value) in values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var row = existing.GetValueOrDefault(name);
            if (value is null)
            {
                if (row is not null)
                {
                    context.AgentMetadata.Remove(row);
                    changes.Add(new NhAssistantAgentMetadataChange(name, Removed, version));
                }
                continue;
            }

            if (row is null)
            {
                context.AgentMetadata.Add(NewRow(agentId, name, value, version, actorId, now));
                changes.Add(new NhAssistantAgentMetadataChange(name, Created, version));
                continue;
            }

            Apply(row, value, version, actorId, now);
            changes.Add(new NhAssistantAgentMetadataChange(name, Changed, version));
        }

        return changes;
    }

    /// <summary>
    /// The rows of a new agent created at runtime with values, at its first version.
    /// </summary>
    public static IReadOnlyList<AssistantAgentMetadata> NewRows(
        string agentId,
        IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?> values,
        string actorId,
        DateTimeOffset now)
    {
        return values
            .Where(item => item.Value is not null)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => NewRow(agentId, item.Key, item.Value!, 1, actorId, now))
            .ToArray();
    }

    /// <summary>
    /// Stores an administrator's override of an editable entry of an agent in code: a new override when
    /// <paramref name="expectedVersion"/> is <see langword="null"/> and none exists, otherwise the next
    /// version of the one whose version is <paramref name="expectedVersion"/>.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentMetadata>> SaveOverrideAsync(
        string agentId,
        string name,
        int? expectedVersion,
        NhAssistantAgentMetadataCheck value,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await SaveOverrideAsync(context, agentId, name, expectedVersion, value, actorId, now, cancellationToken);
    }

    /// <summary>
    /// The transactional path of <see cref="SaveOverrideAsync(string, string, int?, NhAssistantAgentMetadataCheck, string, DateTimeOffset, CancellationToken)"/>:
    /// saves in <paramref name="context"/>, inside the caller's transaction when it has one.
    /// </summary>
    internal static async Task<NhAssistantStoreResult<AssistantAgentMetadata>> SaveOverrideAsync(
        NhAssistantDbContext context,
        string agentId,
        string name,
        int? expectedVersion,
        NhAssistantAgentMetadataCheck value,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = await context.AgentMetadata
            .SingleOrDefaultAsync(item => item.AgentId == agentId && item.Name == name, cancellationToken);
        if (row is null)
        {
            if (expectedVersion is not null)
            {
                return NhAssistantStoreResult<AssistantAgentMetadata>.Failed(NhAssistantStoreOutcome.Conflict);
            }

            row = NewRow(agentId, name, value, 1, actorId, now);
            context.AgentMetadata.Add(row);
        }
        else
        {
            if (expectedVersion != row.Version)
            {
                return NhAssistantStoreResult<AssistantAgentMetadata>.Failed(NhAssistantStoreOutcome.Conflict);
            }

            Apply(row, value, row.Version + 1, actorId, now);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another administrator stored the same override first, or the agent is gone.
            return NhAssistantStoreResult<AssistantAgentMetadata>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        return NhAssistantStoreResult<AssistantAgentMetadata>.Ok(row);
    }

    /// <summary>
    /// Removes the override of an entry of an agent in code, so the value in code applies again.
    /// Succeeds with <see langword="null"/> when there is none.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgentMetadata?>> RemoveOverrideAsync(
        string agentId,
        string name,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var row = await context.AgentMetadata
            .SingleOrDefaultAsync(item => item.AgentId == agentId && item.Name == name, cancellationToken);
        if (row is null)
        {
            return NhAssistantStoreResult<AssistantAgentMetadata?>.Ok(null);
        }
        if (expectedVersion != row.Version)
        {
            return NhAssistantStoreResult<AssistantAgentMetadata?>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        context.AgentMetadata.Remove(row);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NhAssistantStoreResult<AssistantAgentMetadata?>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        return NhAssistantStoreResult<AssistantAgentMetadata?>.Ok(row);
    }

    private static AssistantAgentMetadata NewRow(
        string agentId,
        string name,
        NhAssistantAgentMetadataCheck value,
        int version,
        string actorId,
        DateTimeOffset now)
    {
        var row = new AssistantAgentMetadata { AgentId = agentId, Name = name };
        Apply(row, value, version, actorId, now);
        return row;
    }

    private static void Apply(
        AssistantAgentMetadata row,
        NhAssistantAgentMetadataCheck value,
        int version,
        string actorId,
        DateTimeOffset now)
    {
        row.ValueJson = value.Json!;
        row.IndexValue = value.IndexValue;
        row.Version = version;
        row.ChangedAt = now;
        row.ChangedBy = actorId;
    }
}
