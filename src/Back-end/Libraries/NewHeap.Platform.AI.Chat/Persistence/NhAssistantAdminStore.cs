using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

internal enum NhAssistantStoreOutcome
{
    Succeeded = 0,
    NotFound = 1,
    Conflict = 2,
    Rejected = 3,

    /// <summary>
    /// Nothing was saved because what is stored wins over the change, such as a newer candidate; the
    /// result's value is what was kept.
    /// </summary>
    Kept = 4
}

/// <summary>
/// The tools of a server after a sync, and the tools reviewed before any sync that this sync saw first.
/// </summary>
internal sealed record NhAssistantMcpSyncResult(
    IReadOnlyList<AssistantMcpTool> Tools,
    IReadOnlyList<AssistantMcpTool> FirstSeen);

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
    /// The agents created in the administration whose instructions name the application's asset
    /// <paramref name="assetId"/>, ordered by id. An agent whose instructions are text has an asset id
    /// of its own and is never one of them. The asset columns have no index.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgent>> ListAgentsNamingAssetAsync(string assetId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var agents = await context.Agents
            .AsNoTracking()
            .Where(agent => agent.Source == NhAssistantAgentSources.Admin
                && agent.InstructionsFromAsset
                && agent.InstructionsAssetId == assetId)
            .ToListAsync(cancellationToken);

        // Ordinal, so both providers list the same order whatever their collation.
        return agents.OrderBy(agent => agent.Id, StringComparer.Ordinal).ToArray();
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

    /// <summary>
    /// Adds an agent with its MCP assignments and, for an agent created at runtime, the values of its
    /// metadata, in one save.
    /// </summary>
    public async Task<bool> AddAgentAsync(
        AssistantAgent agent,
        IReadOnlyCollection<string> mcpServerIds,
        CancellationToken cancellationToken,
        IReadOnlyList<AssistantAgentMetadata>? metadata = null)
    {
        await using var context = contextFactory.CreateDbContext();
        return await AddAgentAsync(context, agent, mcpServerIds, metadata, cancellationToken);
    }

    /// <summary>
    /// The transactional path of <see cref="AddAgentAsync(AssistantAgent, IReadOnlyCollection{string}, CancellationToken, IReadOnlyList{AssistantAgentMetadata}?)"/>:
    /// adds in <paramref name="context"/>, inside the caller's transaction when it has one.
    /// </summary>
    internal static async Task<bool> AddAgentAsync(
        NhAssistantDbContext context,
        AssistantAgent agent,
        IReadOnlyCollection<string> mcpServerIds,
        IReadOnlyList<AssistantAgentMetadata>? metadata,
        CancellationToken cancellationToken)
    {
        if (await context.Agents.AnyAsync(item => item.Id == agent.Id, cancellationToken))
        {
            return false;
        }
        agent.McpServers = mcpServerIds
            .Distinct(StringComparer.Ordinal)
            .Select(serverId => new AssistantAgentMcpServer { AgentId = agent.Id, McpServerId = serverId })
            .ToList();
        context.Agents.Add(agent);
        context.AgentMetadata.AddRange(metadata ?? []);
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
    /// <paramref name="metadata"/> stores or removes values of an agent created at runtime with the
    /// new version, in the same save, and adds what changed to <paramref name="metadataChanges"/>.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantAgent>> UpdateAgentAsync(
        string id,
        int? expectedVersion,
        Action<AssistantAgent> apply,
        IReadOnlyCollection<string>? mcpServerIds,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?>? metadata = null,
        ICollection<NhAssistantAgentMetadataChange>? metadataChanges = null)
    {
        await using var context = contextFactory.CreateDbContext();
        return await UpdateAgentAsync(context, id, expectedVersion, apply, mcpServerIds, metadata, metadataChanges, cancellationToken);
    }

    /// <summary>
    /// The transactional path of <see cref="UpdateAgentAsync(string, int?, Action{AssistantAgent}, IReadOnlyCollection{string}?, CancellationToken, IReadOnlyDictionary{string, NhAssistantAgentMetadataCheck?}?, ICollection{NhAssistantAgentMetadataChange}?)"/>:
    /// changes in <paramref name="context"/>, inside the caller's transaction when it has one.
    /// </summary>
    internal static async Task<NhAssistantStoreResult<AssistantAgent>> UpdateAgentAsync(
        NhAssistantDbContext context,
        string id,
        int? expectedVersion,
        Action<AssistantAgent> apply,
        IReadOnlyCollection<string>? mcpServerIds,
        IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?>? metadata,
        ICollection<NhAssistantAgentMetadataChange>? metadataChanges,
        CancellationToken cancellationToken)
    {
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
        var changes = metadata is { Count: > 0 } && agent.Source == NhAssistantAgentSources.Admin
            ? await NhAssistantAgentMetadataStore.ApplyDataAsync(
                context,
                agent.Id,
                agent.Version,
                metadata,
                agent.UpdatedBy ?? string.Empty,
                agent.UpdatedAt,
                cancellationToken)
            : [];
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NhAssistantStoreResult<AssistantAgent>.Failed(NhAssistantStoreOutcome.Conflict);
        }
        foreach (var change in changes)
        {
            metadataChanges?.Add(change);
        }
        return NhAssistantStoreResult<AssistantAgent>.Ok(agent);
    }

    /// <summary>
    /// The stored states of the agent's skills that are not active, by skill id.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, AssistantAgentSkillState>> GetSkillStatesAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentSkillStates
            .AsNoTracking()
            .Where(skill => skill.AgentId == agentId)
            .ToDictionaryAsync(skill => skill.SkillId, StringComparer.Ordinal, cancellationToken);
    }

    /// <summary>
    /// Records the skills in code that the application sees for the first time: each starts active,
    /// or off when its code says <c>StartsOff()</c>. A skill with a row keeps its stored state, so
    /// the row also marks that the skill was seen.
    /// </summary>
    public async Task RecordCodeSkillsAsync(
        IReadOnlyCollection<(string AgentId, string SkillId, bool StartsOff)> skills,
        CancellationToken cancellationToken)
    {
        if (skills.Count == 0)
        {
            return;
        }

        var agentIds = skills.Select(skill => skill.AgentId).Distinct(StringComparer.Ordinal).ToArray();
        HashSet<(string AgentId, string SkillId)> seen;
        await using (var context = contextFactory.CreateDbContext())
        {
            var rows = await context.AgentSkillStates
                .AsNoTracking()
                .Where(row => agentIds.Contains(row.AgentId))
                .Select(row => new { row.AgentId, row.SkillId })
                .ToListAsync(cancellationToken);
            seen = rows.Select(row => (row.AgentId, row.SkillId)).ToHashSet();
        }

        foreach (var (agentId, skillId, startsOff) in skills)
        {
            if (seen.Contains((agentId, skillId)))
            {
                continue;
            }

            await using var context = contextFactory.CreateDbContext();
            context.AgentSkillStates.Add(new AssistantAgentSkillState
            {
                AgentId = agentId,
                SkillId = skillId,
                RunState = startsOff ? NhAssistantAgentRunStates.Off : NhAssistantAgentRunStates.Active
            });
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another host recorded the same skill concurrently.
            }
        }
    }

    /// <summary>
    /// Sets the state of one skill and returns the state it had before.
    /// </summary>
    public async Task<string> SetSkillStateAsync(
        string agentId,
        string skillId,
        string runState,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var row = await context.AgentSkillStates.SingleOrDefaultAsync(
            skill => skill.AgentId == agentId && skill.SkillId == skillId,
            cancellationToken);
        var previous = row?.RunState ?? NhAssistantAgentRunStates.Active;
        if (row is null)
        {
            row = new AssistantAgentSkillState { AgentId = agentId, SkillId = skillId };
            context.AgentSkillStates.Add(row);
        }

        row.RunState = runState;
        row.StateChangedAt = DateTimeOffset.UtcNow;
        row.StateChangedBy = actorId;
        row.StateReason = reason;
        await context.SaveChangesAsync(cancellationToken);
        return previous;
    }

    /// <summary>
    /// Sets the shadow mode of one skill; <see langword="null"/> follows the code again.
    /// </summary>
    public async Task SetSkillShadowAsync(
        string agentId,
        string skillId,
        bool? shadow,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var row = await context.AgentSkillStates.SingleOrDefaultAsync(
            skill => skill.AgentId == agentId && skill.SkillId == skillId,
            cancellationToken);
        if (row is null)
        {
            row = new AssistantAgentSkillState { AgentId = agentId, SkillId = skillId };
            context.AgentSkillStates.Add(row);
        }

        row.ShadowOverride = shadow;
        row.ShadowChangedAt = DateTimeOffset.UtcNow;
        row.ShadowChangedBy = actorId;
        row.ShadowReason = reason;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Sets the shadow mode of an agent without changing its definition or version;
    /// <see langword="null"/> follows the code again. Returns <see langword="false"/> when the agent
    /// does not exist.
    /// </summary>
    public async Task<bool> SetAgentShadowAsync(
        string id,
        bool? shadow,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        return await context.Agents
            .Where(agent => agent.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(agent => agent.ShadowOverride, shadow)
                    .SetProperty(agent => agent.ShadowChangedAt, now)
                    .SetProperty(agent => agent.ShadowChangedBy, actorId)
                    .SetProperty(agent => agent.ShadowReason, reason),
                cancellationToken) > 0;
    }

    /// <summary>
    /// Sets the technical account of an agent without changing its definition or version;
    /// <see langword="null"/> uses the account in code again. Returns <see langword="false"/> when
    /// the agent does not exist.
    /// </summary>
    public async Task<bool> SetAgentServiceAccountAsync(
        string id,
        string? serviceAccountActorId,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        return await context.Agents
            .Where(agent => agent.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(agent => agent.ServiceAccountActorId, serviceAccountActorId)
                    .SetProperty(agent => agent.ServiceAccountChangedAt, now)
                    .SetProperty(agent => agent.ServiceAccountChangedBy, actorId)
                    .SetProperty(agent => agent.ServiceAccountReason, reason),
                cancellationToken) > 0;
    }

    /// <summary>
    /// Sets the routes an administrator switched for the agent's consultations without changing its
    /// definition or version; a <see langword="null"/> route follows the code again. Returns
    /// <see langword="false"/> when the agent does not exist.
    /// </summary>
    public async Task<bool> SetAgentConsultationRoutesAsync(
        string id,
        NhAssistantConsultationRouteOverrides routes,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routes);
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        return await context.Agents
            .Where(agent => agent.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(agent => agent.ConsultAskerDecides, routes.AskerDecides)
                    .SetProperty(agent => agent.ConsultSubjectAgentRights, routes.SubjectAgentRights)
                    .SetProperty(agent => agent.ConsultSubjectAskerRights, routes.SubjectAskerRights)
                    .SetProperty(agent => agent.ConsultRoutesChangedAt, now)
                    .SetProperty(agent => agent.ConsultRoutesChangedBy, actorId)
                    .SetProperty(agent => agent.ConsultRoutesReason, reason),
                cancellationToken) > 0;
    }

    /// <summary>
    /// Marks the threads of agents that work in shadow by their code as shadow threads: every thread
    /// of a shadow variant, and those of an agent with <c>Shadow()</c> in code whose shadow mode no
    /// administrator ever changed. Threads from before shadow threads existed then stay with
    /// administrators.
    /// </summary>
    public async Task MarkShadowThreadsAsync(
        IReadOnlyCollection<string> variantIds,
        IReadOnlyCollection<string> codeShadowIds,
        CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0 && codeShadowIds.Count == 0)
        {
            return;
        }

        await using var context = contextFactory.CreateDbContext();
        var variants = variantIds.ToArray();
        var codeShadow = codeShadowIds.ToArray();
        await context.Threads
            .Where(thread => !thread.Shadow
                && (variants.Contains(thread.AgentId)
                    || (codeShadow.Contains(thread.AgentId)
                        && context.Agents.Any(agent => agent.Id == thread.AgentId && agent.ShadowChangedAt == null))))
            .ExecuteUpdateAsync(setters => setters.SetProperty(thread => thread.Shadow, true), cancellationToken);
    }

    /// <summary>
    /// The stored run state of an agent, or <see langword="null"/> when it does not exist.
    /// </summary>
    public async Task<string?> FindAgentRunStateAsync(string id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Agents
            .AsNoTracking()
            .Where(agent => agent.Id == id)
            .Select(agent => agent.IsEnabled ? agent.RunState : NhAssistantAgentRunStates.Stopped)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Sets the run state of an agent without changing its definition or version. Returns the state
    /// it had before, or <see langword="null"/> when the agent does not exist.
    /// </summary>
    public async Task<string?> SetAgentStateAsync(
        string id,
        string runState,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var previous = await context.Agents
            .AsNoTracking()
            .Where(agent => agent.Id == id)
            .Select(agent => agent.IsEnabled ? agent.RunState : NhAssistantAgentRunStates.Stopped)
            .SingleOrDefaultAsync(cancellationToken);
        if (previous is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var stopping = runState == NhAssistantAgentRunStates.Stopped;
        await context.Agents
            .Where(agent => agent.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(agent => agent.RunState, runState)
                    .SetProperty(agent => agent.IsEnabled, !stopping)
                    // A stop remembers whether the agent was off, so releasing the kill switch
                    // switches it off again; every other change forgets it.
                    .SetProperty(
                        agent => agent.OffBeforeStop,
                        agent => stopping && agent.IsEnabled && agent.RunState == NhAssistantAgentRunStates.Off)
                    .SetProperty(agent => agent.StateChangedAt, now)
                    .SetProperty(agent => agent.StateChangedBy, actorId)
                    .SetProperty(agent => agent.StateReason, reason),
                cancellationToken);
        return previous;
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
        var servers = await context.McpServers
            .AsNoTracking()
            .Include(server => server.Readers)
            .OrderBy(server => server.Id)
            .ToListAsync(cancellationToken);
        var assignments = await context.AgentMcpServers.AsNoTracking().ToListAsync(cancellationToken);
        return servers
            .Select(server => (server, (IReadOnlyList<string>)assignments
                .Where(assignment => assignment.McpServerId == server.Id)
                .Select(assignment => assignment.AgentId)
                .Order(StringComparer.Ordinal)
                .ToArray()))
            .ToArray();
    }

    /// <summary>
    /// The readers of the enabled servers with their server, in the order in which the assistant asks
    /// them to read a file: priority, highest first, then server id and tool name.
    /// </summary>
    public async Task<IReadOnlyList<(AssistantMcpServer Server, AssistantMcpReader Reader)>> ListMcpReadersAsync(
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var servers = await context.McpServers
            .AsNoTracking()
            .Include(server => server.Readers)
            .Where(server => server.IsEnabled && server.Readers.Any())
            .ToListAsync(cancellationToken);

        // Ordinal, so both providers ask the same reader first whatever their collation; the set of
        // readers is small.
        return servers
            .SelectMany(server => server.Readers.Select(reader => (Server: server, Reader: reader)))
            .OrderByDescending(item => item.Reader.Priority)
            .ThenBy(item => item.Server.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Reader.ToolName, StringComparer.Ordinal)
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

    /// <summary>
    /// The agents a server is assigned to, ordered by id.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListMcpAssignmentsAsync(string serverId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var agentIds = await context.AgentMcpServers
            .AsNoTracking()
            .Where(assignment => assignment.McpServerId == serverId)
            .Select(assignment => assignment.AgentId)
            .ToListAsync(cancellationToken);
        return agentIds.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Assigns a server to an agent (<paramref name="assign"/>) or removes the assignment, as the
    /// assignment's own row: the agent's row, its version and its definition stay as they are. Returns
    /// whether the assignment changed: assigning an assigned server or removing a missing assignment
    /// changes nothing. <see cref="NhAssistantStoreOutcome.NotFound"/> when the agent or the server does
    /// not exist; <see cref="NhAssistantStoreOutcome.Rejected"/> when the agent already has
    /// <paramref name="maxPerAgent"/> servers.
    /// </summary>
    public async Task<NhAssistantStoreResult<bool>> SetMcpAssignmentAsync(
        string agentId,
        string serverId,
        bool assign,
        int maxPerAgent,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        if (!await context.Agents.AnyAsync(agent => agent.Id == agentId, cancellationToken)
            || !await context.McpServers.AnyAsync(server => server.Id == serverId, cancellationToken))
        {
            return NhAssistantStoreResult<bool>.Failed(NhAssistantStoreOutcome.NotFound);
        }

        var existing = await context.AgentMcpServers.SingleOrDefaultAsync(
            assignment => assignment.AgentId == agentId && assignment.McpServerId == serverId,
            cancellationToken);
        if (assign)
        {
            if (existing is not null)
            {
                return NhAssistantStoreResult<bool>.Ok(false);
            }
            if (await context.AgentMcpServers.CountAsync(assignment => assignment.AgentId == agentId, cancellationToken) >= maxPerAgent)
            {
                return NhAssistantStoreResult<bool>.Failed(NhAssistantStoreOutcome.Rejected);
            }

            context.AgentMcpServers.Add(new AssistantAgentMcpServer { AgentId = agentId, McpServerId = serverId });
        }
        else
        {
            if (existing is null)
            {
                return NhAssistantStoreResult<bool>.Ok(false);
            }

            context.AgentMcpServers.Remove(existing);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return NhAssistantStoreResult<bool>.Ok(true);
        }
        catch (DbUpdateConcurrencyException) when (!assign)
        {
            // A concurrent removal removed the assignment first.
            return NhAssistantStoreResult<bool>.Ok(false);
        }
        catch (DbUpdateException) when (assign)
        {
            // A concurrent assignment stored the same row first, or the agent or server went meanwhile.
            await using var check = contextFactory.CreateDbContext();
            return await check.AgentMcpServers.AnyAsync(
                assignment => assignment.AgentId == agentId && assignment.McpServerId == serverId,
                cancellationToken)
                ? NhAssistantStoreResult<bool>.Ok(false)
                : NhAssistantStoreResult<bool>.Failed(NhAssistantStoreOutcome.NotFound);
        }
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

    /// <summary>
    /// Changes a server with its readers loaded, so <paramref name="apply"/> can change them too, and
    /// returns it as stored; <see langword="null"/> when the server does not exist.
    /// </summary>
    public async Task<AssistantMcpServer?> UpdateMcpServerAsync(
        string id,
        Action<AssistantMcpServer> apply,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var server = await context.McpServers
            .Include(item => item.Readers)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (server is null)
        {
            return null;
        }
        apply(server);
        server.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return server;
    }

    /// <summary>
    /// Records the outcome of a connection without changing the server's version, so cached
    /// connections stay valid.
    /// </summary>
    public async Task RecordMcpConnectAsync(
        string id,
        DateTimeOffset connectedAt,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.McpServers
            .Where(server => server.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(server => server.LastConnectAt, connectedAt)
                    .SetProperty(server => server.LastConnectCode, failureCode),
                cancellationToken);
    }

    public async Task<AssistantMcpConnection?> FindMcpConnectionAsync(
        string serverId,
        string ownerKind,
        string ownerActorId,
        string tenantKey,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(
                connection => connection.ServerId == serverId
                    && connection.OwnerKind == ownerKind
                    && connection.OwnerActorId == ownerActorId
                    && connection.TenantKey == tenantKey,
                cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantMcpConnection>> ListMcpConnectionsAsync(
        string ownerKind,
        string ownerActorId,
        string tenantKey,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnections
            .AsNoTracking()
            .Where(connection => connection.OwnerKind == ownerKind
                && connection.OwnerActorId == ownerActorId
                && connection.TenantKey == tenantKey)
            .OrderBy(connection => connection.ServerId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Records a person's consent to background use of a server, or its withdrawal. Creates the
    /// connection record when there is none; a concurrent first consent of the same person wins once
    /// and the other attempt updates the row it created.
    /// </summary>
    public async Task<AssistantMcpConnection> SetMcpBackgroundUseAsync(
        string serverId,
        string ownerActorId,
        string tenantKey,
        bool allow,
        string consentHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var context = contextFactory.CreateDbContext();
            var connection = await context.McpConnections.SingleOrDefaultAsync(
                item => item.ServerId == serverId
                    && item.OwnerKind == NhAssistantMcpConnectionOwners.User
                    && item.OwnerActorId == ownerActorId
                    && item.TenantKey == tenantKey,
                cancellationToken);
            if (connection is null)
            {
                connection = new AssistantMcpConnection
                {
                    Id = Guid.NewGuid(),
                    ServerId = serverId,
                    OwnerKind = NhAssistantMcpConnectionOwners.User,
                    OwnerActorId = ownerActorId,
                    TenantKey = tenantKey,
                    CreatedAt = now
                };
                context.McpConnections.Add(connection);
            }
            connection.AllowBackgroundUse = allow;
            connection.ConsentHash = allow ? consentHash : null;
            connection.ConsentedAt = allow ? now : null;
            connection.UpdatedAt = now;
            connection.Version++;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return connection;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // A concurrent first consent created the row or changed its version: apply on top of it.
            }
        }
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
    /// Whether a tool row holds the reviewed settings of a tool that no sync of its server has seen
    /// yet: it has no input schema hash and the status <c>missing</c>, so no turn offers it.
    /// </summary>
    public static bool AwaitsSync(AssistantMcpTool tool)
    {
        return tool.InputSchemaHash.Length == 0;
    }

    /// <summary>
    /// Records the tools a sync discovered. New tools start disabled as mutations; a changed input
    /// schema disables the tool with status <c>schema-changed</c>; vanished tools become <c>missing</c>.
    /// A tool whose settings were stored before any sync saw it (<see cref="AwaitsSync"/>) takes the
    /// discovered schema and becomes <c>available</c> with those settings, found by its exact name or,
    /// when no discovered tool has that name, by a name that differs only in case, because SQL Server
    /// compares the key without case. A sync that does not see such a tool leaves it as it is.
    /// </summary>
    public async Task<NhAssistantMcpSyncResult> ApplySyncAsync(
        string serverId,
        IReadOnlyList<AssistantMcpTool> discovered,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var existing = await context.McpTools
            .Where(tool => tool.ServerId == serverId)
            .ToDictionaryAsync(tool => tool.RemoteName, StringComparer.Ordinal, cancellationToken);
        var discoveredNames = discovered.Select(tool => tool.RemoteName).ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var added = new List<AssistantMcpTool>();
        var respelled = new List<AssistantMcpTool>();
        var firstSeen = new List<AssistantMcpTool>();
        foreach (var remote in discovered)
        {
            if (existing.Remove(remote.RemoteName, out var stored))
            {
                if (AwaitsSync(stored))
                {
                    TakeFirstSync(stored, remote, now);
                    firstSeen.Add(stored);
                    continue;
                }

                ApplyDiscovered(stored, remote, now);
                continue;
            }

            remote.ServerId = serverId;
            remote.Status = NhAssistantMcpToolStatuses.Available;
            remote.UpdatedAt = now;
            var reviewed = existing.Values.FirstOrDefault(tool => AwaitsSync(tool)
                && !discoveredNames.Contains(tool.RemoteName)
                && string.Equals(tool.RemoteName, remote.RemoteName, StringComparison.OrdinalIgnoreCase));
            if (reviewed is not null)
            {
                // The reviewed settings move to the server's spelling of the name.
                existing.Remove(reviewed.RemoteName);
                respelled.Add(reviewed);
                remote.IsEnabled = reviewed.IsEnabled;
                remote.Effect = reviewed.Effect;
                remote.DescriptionOverride = reviewed.DescriptionOverride;
                firstSeen.Add(remote);
            }
            else
            {
                remote.IsEnabled = false;
                remote.Effect = NhAssistantMcpToolEffects.Mutation;
            }

            added.Add(remote);
        }
        foreach (var vanished in existing.Values)
        {
            if (AwaitsSync(vanished))
            {
                // Still expected: its settings wait for the sync that sees it.
                continue;
            }

            vanished.IsEnabled = false;
            vanished.Status = NhAssistantMcpToolStatuses.Missing;
            vanished.UpdatedAt = now;
        }

        // SQL Server compares the key without case, so a row that takes another spelling goes first.
        await using var transaction = respelled.Count > 0
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (respelled.Count > 0)
        {
            context.McpTools.RemoveRange(respelled);
            await context.SaveChangesAsync(cancellationToken);
        }

        context.McpTools.AddRange(added);
        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        var tools = await context.McpTools
            .AsNoTracking()
            .Where(tool => tool.ServerId == serverId)
            .OrderBy(tool => tool.RemoteName)
            .ToListAsync(cancellationToken);
        return new NhAssistantMcpSyncResult(tools, firstSeen);
    }

    /// <summary>
    /// Stores the reviewed settings of a tool that no sync of the server has seen yet as a row with the
    /// status <c>missing</c> and no input schema hash (<see cref="AwaitsSync"/>), so no turn offers it
    /// until a sync sees it. <see cref="NhAssistantStoreOutcome.NotFound"/> when the server does not
    /// exist, <see cref="NhAssistantStoreOutcome.Rejected"/> when it already has
    /// <paramref name="maxAwaitingSync"/> such rows, and <see cref="NhAssistantStoreOutcome.Conflict"/>
    /// when another save stored a row of that name first.
    /// </summary>
    public async Task<NhAssistantStoreResult<AssistantMcpTool>> AddMcpToolAwaitingSyncAsync(
        AssistantMcpTool tool,
        int maxAwaitingSync,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        if (!await context.McpServers.AnyAsync(server => server.Id == tool.ServerId, cancellationToken))
        {
            return NhAssistantStoreResult<AssistantMcpTool>.Failed(NhAssistantStoreOutcome.NotFound);
        }

        var awaiting = await context.McpTools.CountAsync(
            item => item.ServerId == tool.ServerId && item.InputSchemaHash == string.Empty,
            cancellationToken);
        if (awaiting >= maxAwaitingSync)
        {
            return NhAssistantStoreResult<AssistantMcpTool>.Failed(NhAssistantStoreOutcome.Rejected);
        }

        tool.InputSchemaHash = string.Empty;
        tool.Status = NhAssistantMcpToolStatuses.Missing;
        tool.UpdatedAt = DateTimeOffset.UtcNow;
        context.McpTools.Add(tool);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return NhAssistantStoreResult<AssistantMcpTool>.Ok(tool);
        }
        catch (DbUpdateException)
        {
            // A concurrent save stored the row first, or the server went meanwhile.
            return NhAssistantStoreResult<AssistantMcpTool>.Failed(NhAssistantStoreOutcome.Conflict);
        }
    }

    /// <summary>
    /// A tool the sync saw again: its server's description, hint and local id, and a changed input
    /// schema disables it until an administrator enables it again.
    /// </summary>
    private static void ApplyDiscovered(AssistantMcpTool stored, AssistantMcpTool remote, DateTimeOffset now)
    {
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

    /// <summary>
    /// The first sync that sees a tool reviewed before: it takes the server's description, hint, local
    /// id and schema, and keeps the reviewed settings. No schema was known, so none changed.
    /// </summary>
    private static void TakeFirstSync(AssistantMcpTool stored, AssistantMcpTool remote, DateTimeOffset now)
    {
        stored.Description = remote.Description;
        stored.ReadOnlyHint = remote.ReadOnlyHint;
        stored.LocalId = remote.LocalId;
        stored.InputSchemaHash = remote.InputSchemaHash;
        stored.Status = NhAssistantMcpToolStatuses.Available;
        stored.UpdatedAt = now;
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
