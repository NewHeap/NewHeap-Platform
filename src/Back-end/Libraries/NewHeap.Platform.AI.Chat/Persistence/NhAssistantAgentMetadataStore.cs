using System.Text.Json;
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
            .Include(item => item.IndexValues)
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
            .Include(item => item.IndexValues)
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
    /// The agents whose stored value of entry <paramref name="name"/>, declared with
    /// <c>IndexByEach</c>, has <paramref name="indexValue"/>, already normalized, among its searchable
    /// values, ordered by id.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindAgentsByIndexValueAsync(
        string name,
        string indexValue,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentMetadataIndexValues
            .AsNoTracking()
            .Where(item => item.Name == name && item.IndexValue == indexValue)
            .Select(item => item.AgentId)
            .OrderBy(agentId => agentId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gives the stored values of entries declared with <c>IndexByEach</c> the searchable values their
    /// selector computes now, for values stored before the entry declared them or before the selector
    /// changed. A value that no longer reads as the entry's type, or whose values no longer fit, keeps
    /// what it has. Each value is saved on its own, guarded by its version, so a save that happens at
    /// the same time, or another host that does the same, wins.
    /// </summary>
    public async Task ReindexEachAsync(
        IReadOnlyCollection<NhAssistantAgentMetadataEntry> entries,
        CancellationToken cancellationToken)
    {
        var indexed = entries
            .Where(entry => entry.IndexedByEach)
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        if (indexed.Count == 0)
        {
            return;
        }

        var names = indexed.Keys.ToArray();
        IReadOnlyList<AssistantAgentMetadata> rows;
        await using (var context = contextFactory.CreateDbContext())
        {
            rows = await context.AgentMetadata
                .AsNoTracking()
                .Include(item => item.IndexValues)
                .Where(item => names.Contains(item.Name))
                .ToListAsync(cancellationToken);
        }

        foreach (var row in rows)
        {
            var values = IndexValuesOf(indexed[row.Name], row.ValueJson);
            if (values is null || SameIndexValues(row, values))
            {
                continue;
            }

            await ReindexAsync(row, values, cancellationToken);
        }
    }

    private async Task ReindexAsync(AssistantAgentMetadata stored, IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var row = await context.AgentMetadata
            .Include(item => item.IndexValues)
            .SingleOrDefaultAsync(item => item.AgentId == stored.AgentId && item.Name == stored.Name, cancellationToken);
        if (row is null || row.Version != stored.Version || row.ValueJson != stored.ValueJson)
        {
            return;
        }

        ApplyIndexValues(row, values);
        // The version guards the change: a save of the value in the meantime wrote its own values.
        context.Entry(row).Property(item => item.Version).IsModified = true;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Saved again in the meantime, or another host reindexed it first.
        }
    }

    /// <summary>
    /// The searchable values of a stored value as the entry's selector computes them now, or
    /// <see langword="null"/> when the value no longer reads as the entry's type or its values no longer
    /// fit.
    /// </summary>
    private static IReadOnlyList<string>? IndexValuesOf(NhAssistantAgentMetadataEntry entry, string json)
    {
        object? value;
        try
        {
            value = JsonSerializer.Deserialize(json, entry.ValueType, NhAssistantAgentMetadataJson.ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (value is null)
        {
            return null;
        }

        var (values, fits) = entry.IndexValuesOf(value);
        return fits ? values : null;
    }

    private static bool SameIndexValues(AssistantAgentMetadata row, IReadOnlyList<string> values)
    {
        return row.IndexValues.Count == values.Count
            && row.IndexValues.All(item => values.Contains(item.IndexValue, StringComparer.Ordinal));
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
            .Include(item => item.IndexValues)
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
            .Include(item => item.IndexValues)
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
        // The searchable values of IndexByEach go in the same save as the value.
        ApplyIndexValues(row, value.IndexValues);
    }

    /// <summary>
    /// Makes the searchable values of <paramref name="row"/>, whose values are loaded, exactly
    /// <paramref name="values"/>: values that remain stay, others are removed or added.
    /// </summary>
    private static void ApplyIndexValues(AssistantAgentMetadata row, IReadOnlyList<string> values)
    {
        row.IndexValues.RemoveAll(item => !values.Contains(item.IndexValue, StringComparer.Ordinal));
        foreach (var value in values)
        {
            if (row.IndexValues.All(item => !string.Equals(item.IndexValue, value, StringComparison.Ordinal)))
            {
                row.IndexValues.Add(new AssistantAgentMetadataIndexValue
                {
                    AgentId = row.AgentId,
                    Name = row.Name,
                    IndexValue = value
                });
            }
        }
    }
}
