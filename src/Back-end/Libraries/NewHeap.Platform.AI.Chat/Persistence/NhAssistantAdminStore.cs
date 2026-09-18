using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

internal enum NhAssistantStoreOutcome
{
    Succeeded = 0,
    NotFound = 1,
    Conflict = 2,
    Rejected = 3
}

internal sealed record NhAssistantStoreResult<T>(NhAssistantStoreOutcome Outcome, T? Value)
{
    public bool Succeeded => Outcome == NhAssistantStoreOutcome.Succeeded;

    public static NhAssistantStoreResult<T> Ok(T value)
    {
        return new NhAssistantStoreResult<T>(NhAssistantStoreOutcome.Succeeded, value);
    }

    public static NhAssistantStoreResult<T> Failed(NhAssistantStoreOutcome outcome)
    {
        return new NhAssistantStoreResult<T>(outcome, default);
    }
}

/// <summary>
/// Persistence of agents, the application context, MCP servers and tools, and user preferences.
/// Every call uses its own short-lived context; versioned updates are optimistic.
/// </summary>
internal sealed class NhAssistantAdminStore(NhAssistantDbContextFactory contextFactory)
{
    // ----- Agents -----

    public async Task<IReadOnlyList<AssistantAgent>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Agents
            .AsNoTracking()
            .Include(agent => agent.McpServers)
            .OrderBy(agent => agent.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<AssistantAgent?> FindAgentAsync(string id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Agents
            .AsNoTracking()
            .Include(agent => agent.McpServers)
            .SingleOrDefaultAsync(agent => agent.Id == id, cancellationToken);
    }

    /// <summary>
    /// Inserts missing code agents and refreshes code agents that are not overridden when their
    /// code definition changed. Overridden agents keep the administrator's values.
    /// </summary>
    public async Task UpsertCodeAgentsAsync(IEnumerable<AssistantAgent> codeAgents, CancellationToken cancellationToken)
    {
        foreach (var code in codeAgents)
        {
            await using var context = contextFactory.CreateDbContext();
            var existing = await context.Agents.SingleOrDefaultAsync(agent => agent.Id == code.Id, cancellationToken);
            if (existing is null)
            {
                context.Agents.Add(code);
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // Another host inserted the same code agent concurrently.
                }
                continue;
            }
            if (existing.Source != NhAssistantAgentSources.Code
                || existing.IsOverridden
                || string.Equals(existing.CodeHash, code.CodeHash, StringComparison.Ordinal))
            {
                continue;
            }

            ApplyCodeDefinition(existing, code);
            existing.Version++;
            existing.UpdatedAt = code.UpdatedAt;
            existing.UpdatedBy = null;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another host refreshed the agent first.
            }
        }
    }

    public async Task<bool> AddAgentAsync(
        AssistantAgent agent,
        IReadOnlyCollection<string> mcpServerIds,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        if (await context.Agents.AnyAsync(item => item.Id == agent.Id, cancellationToken))
        {
            return false;
        }
        agent.McpServers = mcpServerIds
            .Distinct(StringComparer.Ordinal)
            .Select(serverId => new AssistantAgentMcpServer { AgentId = agent.Id, McpServerId = serverId })
            .ToList();
        context.Agents.Add(agent);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies a change when the stored version equals <paramref name="expectedVersion"/>, then
    /// increments the version. <paramref name="mcpServerIds"/> replaces the assignments when given.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgent>> UpdateAgentAsync(
        string id,
        int? expectedVersion,
        Action<AssistantAgent> apply,
        IReadOnlyCollection<string>? mcpServerIds,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var agent = await context.Agents
            .Include(item => item.McpServers)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (agent is null)
        {
            return NhAssistantStoreResult<AssistantAgent>.Failed(NhAssistantStoreOutcome.NotFound);
        }
        if (expectedVersion is { } expected && agent.Version != expected)
        {
            return NhAssistantStoreResult<AssistantAgent>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        apply(agent);
        agent.Version++;
        if (mcpServerIds is not null)
        {
            agent.McpServers.Clear();
            foreach (var serverId in mcpServerIds.Distinct(StringComparer.Ordinal))
            {
                agent.McpServers.Add(new AssistantAgentMcpServer { AgentId = agent.Id, McpServerId = serverId });
            }
        }
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NhAssistantStoreResult<AssistantAgent>.Failed(NhAssistantStoreOutcome.Conflict);
        }
        return NhAssistantStoreResult<AssistantAgent>.Ok(agent);
    }

    public async Task<NhAssistantStoreOutcome> DeleteAdminAgentAsync(string id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var agent = await context.Agents.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (agent is null)
        {
            return NhAssistantStoreOutcome.NotFound;
        }
        if (agent.Source == NhAssistantAgentSources.Code)
        {
            return NhAssistantStoreOutcome.Rejected;
        }
        context.Agents.Remove(agent);
        await context.SaveChangesAsync(cancellationToken);
        return NhAssistantStoreOutcome.Succeeded;
    }

    public static void ApplyCodeDefinition(AssistantAgent target, AssistantAgent code)
    {
        target.DisplayName = code.DisplayName;
        target.Description = code.Description;
        target.ProfileName = code.ProfileName;
        target.Instructions = code.Instructions;
        target.InstructionsAssetId = code.InstructionsAssetId;
        target.InstructionsAssetVersion = code.InstructionsAssetVersion;
        target.InstructionsHash = code.InstructionsHash;
        target.ToolSelectorsJson = code.ToolSelectorsJson;
        target.RequiredPolicy = code.RequiredPolicy;
        target.Autonomy = code.Autonomy;
        target.CodeHash = code.CodeHash;
    }

    // ----- Application context -----

    public async Task<AssistantApplicationContext?> GetApplicationContextAsync(CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.ApplicationContexts
            .AsNoTracking()
            .Where(item => item.Id == NhAssistantApplicationContexts.DefaultId)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantApplicationContext>> GetApplicationContextVersionsAsync(
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.ApplicationContexts
            .AsNoTracking()
            .Where(item => item.Id == NhAssistantApplicationContexts.DefaultId)
            .OrderByDescending(item => item.Version)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Stores version 1 when no context exists yet. An existing context is never replaced.
    /// </summary>
    public async Task SeedApplicationContextAsync(string text, string hash, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        if (await context.ApplicationContexts.AnyAsync(
            item => item.Id == NhAssistantApplicationContexts.DefaultId,
            cancellationToken))
        {
            return;
        }
        context.ApplicationContexts.Add(new AssistantApplicationContext
        {
            Id = NhAssistantApplicationContexts.DefaultId,
            Version = 1,
            Text = text,
            Hash = hash,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another host seeded first.
        }
    }

    /// <summary>
    /// Adds the next version when the current version equals <paramref name="expectedVersion"/>
    /// (0 when no context exists).
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantApplicationContext>> AddApplicationContextVersionAsync(
        int expectedVersion,
        string text,
        string hash,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var current = await context.ApplicationContexts
            .Where(item => item.Id == NhAssistantApplicationContexts.DefaultId)
            .Select(item => (int?)item.Version)
            .MaxAsync(cancellationToken) ?? 0;
        if (current != expectedVersion)
        {
            return NhAssistantStoreResult<AssistantApplicationContext>.Failed(NhAssistantStoreOutcome.Conflict);
        }
        var next = new AssistantApplicationContext
        {
            Id = NhAssistantApplicationContexts.DefaultId,
            Version = expectedVersion + 1,
            Text = text,
            Hash = hash,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = updatedBy
        };
        context.ApplicationContexts.Add(next);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The version key is unique: a concurrent writer stored the same version first.
            return NhAssistantStoreResult<AssistantApplicationContext>.Failed(NhAssistantStoreOutcome.Conflict);
        }
        return NhAssistantStoreResult<AssistantApplicationContext>.Ok(next);
    }

    // ----- MCP servers and tools -----

    public async Task<IReadOnlyList<(AssistantMcpServer Server, IReadOnlyList<string> AgentIds)>> ListMcpServersAsync(
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var servers = await context.McpServers.AsNoTracking().OrderBy(server => server.Id).ToListAsync(cancellationToken);
        var assignments = await context.AgentMcpServers.AsNoTracking().ToListAsync(cancellationToken);
        return servers
            .Select(server => (server, (IReadOnlyList<string>)assignments
                .Where(assignment => assignment.McpServerId == server.Id)
                .Select(assignment => assignment.AgentId)
                .Order(StringComparer.Ordinal)
                .ToArray()))
            .ToArray();
    }

    public async Task<AssistantMcpServer?> FindMcpServerAsync(string id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpServers.AsNoTracking().SingleOrDefaultAsync(server => server.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> FindExistingMcpServerIdsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var candidates = ids.ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.McpServers
            .Where(server => candidates.Contains(server.Id))
            .Select(server => server.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AddMcpServerAsync(AssistantMcpServer server, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        if (await context.McpServers.AnyAsync(item => item.Id == server.Id, cancellationToken))
        {
            return false;
        }
        context.McpServers.Add(server);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    public async Task<AssistantMcpServer?> UpdateMcpServerAsync(
        string id,
        Action<AssistantMcpServer> apply,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var server = await context.McpServers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (server is null)
        {
            return null;
        }
        apply(server);
        server.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return server;
    }

    public async Task<bool> DeleteMcpServerAsync(string id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var server = await context.McpServers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (server is null)
        {
            return false;
        }
        context.McpServers.Remove(server);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AssistantMcpTool>> ListMcpToolsAsync(string serverId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpTools
            .AsNoTracking()
            .Where(tool => tool.ServerId == serverId)
            .OrderBy(tool => tool.RemoteName)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantMcpTool>> ListEnabledMcpToolsAsync(
        IReadOnlyCollection<string> serverIds,
        CancellationToken cancellationToken)
    {
        if (serverIds.Count == 0)
        {
            return [];
        }
        var ids = serverIds.ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.McpTools
            .AsNoTracking()
            .Where(tool => ids.Contains(tool.ServerId)
                && tool.IsEnabled
                && tool.Status == NhAssistantMcpToolStatuses.Available)
            .OrderBy(tool => tool.ServerId)
            .ThenBy(tool => tool.RemoteName)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Records the tools a sync discovered. New tools start disabled as mutations; a changed input
    /// schema disables the tool with status <c>schema-changed</c>; vanished tools become <c>missing</c>.
    /// </summary>
    public async Task<IReadOnlyList<AssistantMcpTool>> ApplySyncAsync(
        string serverId,
        IReadOnlyList<AssistantMcpTool> discovered,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var existing = await context.McpTools
            .Where(tool => tool.ServerId == serverId)
            .ToDictionaryAsync(tool => tool.RemoteName, StringComparer.Ordinal, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var remote in discovered)
        {
            if (!existing.Remove(remote.RemoteName, out var stored))
            {
                remote.ServerId = serverId;
                remote.IsEnabled = false;
                remote.Effect = NhAssistantMcpToolEffects.Mutation;
                remote.Status = NhAssistantMcpToolStatuses.Available;
                remote.UpdatedAt = now;
                context.McpTools.Add(remote);
                continue;
            }

            stored.Description = remote.Description;
            stored.ReadOnlyHint = remote.ReadOnlyHint;
            stored.LocalId = remote.LocalId;
            stored.UpdatedAt = now;
            if (!string.Equals(stored.InputSchemaHash, remote.InputSchemaHash, StringComparison.Ordinal))
            {
                stored.InputSchemaHash = remote.InputSchemaHash;
                stored.IsEnabled = false;
                stored.Status = NhAssistantMcpToolStatuses.SchemaChanged;
            }
            else if (stored.Status == NhAssistantMcpToolStatuses.Missing)
            {
                stored.Status = NhAssistantMcpToolStatuses.Available;
            }
        }
        foreach (var vanished in existing.Values)
        {
            vanished.IsEnabled = false;
            vanished.Status = NhAssistantMcpToolStatuses.Missing;
            vanished.UpdatedAt = now;
        }
        await context.SaveChangesAsync(cancellationToken);
        return await context.McpTools
            .AsNoTracking()
            .Where(tool => tool.ServerId == serverId)
            .OrderBy(tool => tool.RemoteName)
            .ToListAsync(cancellationToken);
    }

    public async Task<AssistantMcpTool?> UpdateMcpToolAsync(
        string serverId,
        string remoteName,
        Action<AssistantMcpTool> apply,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var tool = await context.McpTools.SingleOrDefaultAsync(
            item => item.ServerId == serverId && item.RemoteName == remoteName,
            cancellationToken);
        if (tool is null)
        {
            return null;
        }
        apply(tool);
        tool.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return tool;
    }

    // ----- User preferences -----

    public async Task<AssistantUserPreference?> FindPreferenceAsync(string actorId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.UserPreferences.AsNoTracking().SingleOrDefaultAsync(item => item.ActorId == actorId, cancellationToken);
    }

    public async Task<AssistantUserPreference> SavePreferenceAsync(
        AssistantUserPreference preference,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var context = contextFactory.CreateDbContext();
            var stored = await context.UserPreferences.SingleOrDefaultAsync(
                item => item.ActorId == preference.ActorId,
                cancellationToken);
            if (stored is null)
            {
                context.UserPreferences.Add(preference);
            }
            else
            {
                stored.Style = preference.Style;
                stored.AddressForm = preference.AddressForm;
                stored.ResponseLength = preference.ResponseLength;
                stored.CustomInstructions = preference.CustomInstructions;
                stored.UpdatedAt = preference.UpdatedAt;
            }
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return preference;
            }
            catch (DbUpdateException) when (stored is null && attempt == 0)
            {
                // A concurrent first save created the row; update it instead.
            }
        }
        throw new InvalidOperationException("The assistant preferences could not be saved.");
    }
}
