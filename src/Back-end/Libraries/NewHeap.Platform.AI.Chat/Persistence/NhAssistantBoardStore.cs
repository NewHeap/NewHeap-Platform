using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Persistence of groups and boards (K15, K16): the administrators' choices which agents are in
/// which group, and when each person last looked at a board in a scope. Both tables stay small: one
/// row per agent and group an administrator decided on, and one per person, group and scope.
/// </summary>
internal sealed class NhAssistantBoardStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// The most choices one read returns: every agent in every group is far below it.
    /// </summary>
    internal const int MaxMemberships = 10_000;

    /// <summary>
    /// The administrators' choices, of one group or of all groups, ordered by group and agent.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentGroupMember>> ListMembershipsAsync(
        string? groupId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var query = context.AgentGroupMembers.AsNoTracking();
        if (groupId is not null)
        {
            query = query.Where(item => item.GroupId == groupId);
        }

        return await query
            .OrderBy(item => item.GroupId)
            .ThenBy(item => item.AgentId)
            .Take(MaxMemberships)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The administrators' choices about one agent, ordered by group.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAgentGroupMember>> ListMembershipsOfAgentAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentGroupMembers
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .OrderBy(item => item.GroupId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the administrators' choices about one agent in one transaction: a group with
    /// <see langword="true"/> or <see langword="false"/> keeps that choice, a group with
    /// <see langword="null"/> follows the code again, and groups that are not named keep what they have.
    /// A save that collides with another administrator's save of the same agent is applied again on
    /// top of it once; a second collision, or an agent deleted meanwhile, fails with
    /// <see cref="NhAssistantAdminErrorCodes.VersionConflict"/>.
    /// </summary>
    public async Task<TaskResult> SetMembershipsAsync(
        string agentId,
        IReadOnlyDictionary<string, bool?> choices,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(choices);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await ApplyMembershipsAsync(agentId, choices, actorId, now, cancellationToken);
                return TaskResult.Succeeded();
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Another save of the same agent's choices came first: apply the choices on top of it.
            }
            catch (DbUpdateException)
            {
                break;
            }
        }

        return TaskResult.Failed(
            NhAssistantAdminErrorCodes.VersionConflict,
            "The agent's groups changed at the same time, or the agent was deleted.");
    }

    private async Task ApplyMembershipsAsync(
        string agentId,
        IReadOnlyDictionary<string, bool?> choices,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var groupIds = choices.Keys.ToArray();
            var existing = await context.AgentGroupMembers
                .Where(item => item.AgentId == agentId && groupIds.Contains(item.GroupId))
                .ToDictionaryAsync(item => item.GroupId, StringComparer.Ordinal, cancellationToken);
            foreach (var (groupId, choice) in choices)
            {
                var row = existing.GetValueOrDefault(groupId);
                if (choice is null)
                {
                    if (row is not null)
                    {
                        context.AgentGroupMembers.Remove(row);
                    }

                    continue;
                }

                if (row is null)
                {
                    context.AgentGroupMembers.Add(new AssistantAgentGroupMember
                    {
                        AgentId = agentId,
                        GroupId = groupId,
                        IsMember = choice.Value,
                        ChangedAt = now,
                        ChangedBy = actorId
                    });
                }
                else if (row.IsMember != choice.Value)
                {
                    row.IsMember = choice.Value;
                    row.ChangedAt = now;
                    row.ChangedBy = actorId;
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// Removes the visits made before <paramref name="before"/> and, when the host registers groups,
    /// the visits of every group not among <paramref name="registeredGroupIds"/>; returns how many
    /// were removed. A host that registers no group removes no group's visits, so a host that runs
    /// the maintenance without the groups of another host does not remove theirs. The inbox's visits
    /// (<see cref="NhAssistantAgentGroupDefinition.InboxVisitId"/>) belong to no group and are removed
    /// by age only.
    /// </summary>
    public async Task<int> RemoveStaleVisitsAsync(
        DateTimeOffset before,
        IReadOnlyCollection<string> registeredGroupIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registeredGroupIds);
        var groups = registeredGroupIds.ToArray();
        var anyGroup = groups.Length > 0;
        const string inbox = NhAssistantAgentGroupDefinition.InboxVisitId;
        await using var context = contextFactory.CreateDbContext();
        return await context.BoardVisits
            .Where(item => item.VisitedAt < before
                || (anyGroup && item.GroupId != inbox && !groups.Contains(item.GroupId)))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// When the person last looked at the group's board in the scope; <see langword="null"/> before
    /// the first visit.
    /// </summary>
    public async Task<DateTimeOffset?> FindVisitAsync(
        string actorId,
        string groupId,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.BoardVisits
            .AsNoTracking()
            .Where(item => item.ActorId == actorId && item.GroupId == groupId && item.ScopeKey == scopeKey)
            .Select(item => (DateTimeOffset?)item.VisitedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Records that the person looked at the group's board in the scope at <paramref name="at"/>; a
    /// later visit replaces an earlier one, an earlier one never replaces a later one.
    /// </summary>
    public async Task RecordVisitAsync(
        string actorId,
        string groupId,
        string scopeKey,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using (var context = contextFactory.CreateDbContext())
        {
            if (await MoveForwardAsync(context, actorId, groupId, scopeKey, at, cancellationToken)
                || await Visit(context, actorId, groupId, scopeKey).AnyAsync(cancellationToken))
            {
                return;
            }
        }

        try
        {
            await using var context = contextFactory.CreateDbContext();
            context.BoardVisits.Add(new AssistantBoardVisit
            {
                ActorId = actorId,
                GroupId = groupId,
                ScopeKey = scopeKey,
                VisitedAt = at
            });
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same person visited the same board at the same moment; the key kept one row,
            // which this visit moves forward when it is the later one.
            await using var context = contextFactory.CreateDbContext();
            await MoveForwardAsync(context, actorId, groupId, scopeKey, at, cancellationToken);
        }
    }

    /// <summary>
    /// Moves an earlier visit forward to <paramref name="at"/>; <see langword="false"/> when there is
    /// no earlier visit.
    /// </summary>
    private static async Task<bool> MoveForwardAsync(
        NhAssistantDbContext context,
        string actorId,
        string groupId,
        string scopeKey,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var updated = await Visit(context, actorId, groupId, scopeKey)
            .Where(item => item.VisitedAt < at)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.VisitedAt, at), cancellationToken);
        return updated > 0;
    }

    private static IQueryable<AssistantBoardVisit> Visit(
        NhAssistantDbContext context,
        string actorId,
        string groupId,
        string scopeKey)
    {
        return context.BoardVisits
            .Where(item => item.ActorId == actorId && item.GroupId == groupId && item.ScopeKey == scopeKey);
    }
}
