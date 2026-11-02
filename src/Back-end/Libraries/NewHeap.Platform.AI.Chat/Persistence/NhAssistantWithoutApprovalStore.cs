using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The append-only versions of the switches that let an agent run a tool without approval (FB82). Every
/// read goes to the database, so switching off takes effect for the next call on every host.
/// </summary>
internal sealed class NhAssistantWithoutApprovalStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// The switch in effect for the agent and the tool: its highest version, or <see langword="null"/>
    /// when nobody ever switched it.
    /// </summary>
    public async Task<AssistantAgentToolSwitch?> FindCurrentAsync(
        string agentId,
        string toolId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentToolSwitches
            .AsNoTracking()
            .Where(item => item.AgentId == agentId && item.ToolId == toolId)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The switches in effect for every tool of the agent that was ever switched.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentToolSwitch>> GetCurrentAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentToolSwitches
            .AsNoTracking()
            .Where(item => item.AgentId == agentId
                && !context.AgentToolSwitches.Any(newer => newer.AgentId == item.AgentId
                    && newer.ToolId == item.ToolId
                    && newer.Version > item.Version))
            .OrderBy(item => item.ToolId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Every version of the agent's switch for the tool, newest first.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentToolSwitch>> GetVersionsAsync(
        string agentId,
        string toolId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentToolSwitches
            .AsNoTracking()
            .Where(item => item.AgentId == agentId && item.ToolId == toolId)
            .OrderByDescending(item => item.Version)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Stores the next version; <see langword="false"/> when another change wrote that version first.
    /// </summary>
    public async Task<bool> AddAsync(AssistantAgentToolSwitch version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        await using var context = contextFactory.CreateDbContext();
        context.AgentToolSwitches.Add(version);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique version per agent and tool: someone else switched in between.
            return false;
        }
    }
}
