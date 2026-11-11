using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Persistence of the MCP tasks of the host's MCP exposure, shared by every instance. A task is written
/// before its id leaves the host, ends once with a conditional update, so a late cancellation never
/// overwrites a result, and expired tasks are deleted in bounded batches.
/// </summary>
internal sealed class NhAssistantMcpTaskStore(NhAssistantDbContextFactory contextFactory)
{
    public async Task CreateAsync(AssistantMcpTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        await using var context = contextFactory.CreateDbContext();
        context.McpTasks.Add(task);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AssistantMcpTask?> FindAsync(string id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(task => task.Id == id, cancellationToken);
    }

    /// <summary>
    /// Ends the working task <paramref name="id"/>; <see langword="false"/> when it does not exist or
    /// ended already.
    /// </summary>
    public async Task<bool> TryFinishAsync(
        string id,
        string status,
        string? resultJson,
        string? errorJson,
        string? statusMessage,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var updated = await context.McpTasks
            .Where(task => task.Id == id && task.Status == NhAssistantMcpTaskStatuses.Working)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(task => task.Status, status)
                    .SetProperty(task => task.ResultJson, resultJson)
                    .SetProperty(task => task.ErrorJson, errorJson)
                    .SetProperty(task => task.StatusMessage, statusMessage)
                    .SetProperty(task => task.LastUpdatedAt, at),
                cancellationToken);
        return updated == 1;
    }

    /// <summary>
    /// Deletes at most <paramref name="maximum"/> tasks that expired at <paramref name="now"/>, the
    /// earliest first.
    /// </summary>
    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, int maximum, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var expired = await context.McpTasks
            .Where(task => task.ExpiresAt <= now)
            .OrderBy(task => task.ExpiresAt)
            .Select(task => task.Id)
            .Take(maximum)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return 0;
        }

        return await context.McpTasks
            .Where(task => expired.Contains(task.Id) && task.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
