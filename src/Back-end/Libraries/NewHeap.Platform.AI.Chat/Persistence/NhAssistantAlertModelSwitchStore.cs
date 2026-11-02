using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The append-only versions of the switches that let an agent's model raise alerts of a kind (FB87-9).
/// Every read goes to the database, so switching off takes effect for the next run on every host.
/// </summary>
internal sealed class NhAssistantAlertModelSwitchStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// The switch in effect for the agent and the kind: its highest version, or <see langword="null"/>
    /// when nobody ever switched it.
    /// </summary>
    public async Task<AssistantAgentAlertModelSwitch?> FindCurrentAsync(
        string agentId,
        string kindId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AlertModelSwitches
            .AsNoTracking()
            .Where(item => item.AgentId == agentId && item.KindId == kindId)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The switches in effect for every kind of the agent that was ever switched.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentAlertModelSwitch>> GetCurrentAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AlertModelSwitches
            .AsNoTracking()
            .Where(item => item.AgentId == agentId
                && !context.AlertModelSwitches.Any(newer => newer.AgentId == item.AgentId
                    && newer.KindId == item.KindId
                    && newer.Version > item.Version))
            .OrderBy(item => item.KindId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Every version of the agent's switch for the kind, newest first.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentAlertModelSwitch>> GetVersionsAsync(
        string agentId,
        string kindId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AlertModelSwitches
            .AsNoTracking()
            .Where(item => item.AgentId == agentId && item.KindId == kindId)
            .OrderByDescending(item => item.Version)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Stores the next version; <see langword="false"/> when another change wrote that version first.
    /// </summary>
    public async Task<bool> AddAsync(AssistantAgentAlertModelSwitch version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        await using var context = contextFactory.CreateDbContext();
        context.AlertModelSwitches.Add(version);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique version per agent and kind: someone else switched in between. Any other
            // failure, such as an agent that is gone, is no conflict.
            if (await VersionExistsAsync(version, cancellationToken))
            {
                return false;
            }

            throw;
        }
    }

    private async Task<bool> VersionExistsAsync(AssistantAgentAlertModelSwitch version, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AlertModelSwitches.AnyAsync(
            item => item.AgentId == version.AgentId && item.KindId == version.KindId && item.Version == version.Version,
            cancellationToken);
    }
}
