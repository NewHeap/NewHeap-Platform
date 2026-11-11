using ModelContextProtocol.Extensions.Tasks;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The MCP tasks of the host's MCP exposure (<c>UseTasks</c>) in the assistant's store, which every
/// instance of the host shares.
/// </summary>
internal sealed class NhAssistantMcpTaskRecords(NhAssistantMcpTaskStore store) : INhAiMcpTaskRecords
{
    public Task CreateAsync(NhAiMcpTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return store.CreateAsync(
            new AssistantMcpTask
            {
                Id = record.TaskId,
                ToolName = record.ToolName,
                ToolId = record.ToolId,
                ToolVersion = record.ToolVersion,
                ActorId = record.ActorId,
                TenantKey = record.TenantId ?? string.Empty,
                DivisionKey = record.DivisionId ?? string.Empty,
                Status = StatusOf(record.Status),
                CreatedAt = record.CreatedAt,
                LastUpdatedAt = record.LastUpdatedAt,
                ExpiresAt = record.ExpiresAt,
                AbandonAt = record.AbandonAt,
                PollIntervalMs = record.PollIntervalMs,
                ResultJson = record.ResultJson,
                ErrorJson = record.ErrorJson,
                StatusMessage = record.StatusMessage
            },
            cancellationToken);
    }

    public async Task<NhAiMcpTaskRecord?> FindAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = await store.FindAsync(taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        return new NhAiMcpTaskRecord(
            task.Id,
            task.ToolName,
            task.ToolId,
            task.ToolVersion,
            task.ActorId,
            task.TenantKey.Length == 0 ? null : task.TenantKey,
            task.DivisionKey.Length == 0 ? null : task.DivisionKey,
            StatusOf(task.Status),
            task.CreatedAt,
            task.LastUpdatedAt,
            task.ExpiresAt,
            task.AbandonAt,
            task.PollIntervalMs,
            task.ResultJson,
            task.ErrorJson,
            task.StatusMessage);
    }

    public Task<bool> TryFinishAsync(
        string taskId,
        McpTaskStatus status,
        string? resultJson,
        string? errorJson,
        string? statusMessage,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        return store.TryFinishAsync(taskId, StatusOf(status), resultJson, errorJson, statusMessage, at, cancellationToken);
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, int maximum, CancellationToken cancellationToken)
    {
        return store.DeleteExpiredAsync(now, maximum, cancellationToken);
    }

    private static string StatusOf(McpTaskStatus status)
    {
        return status switch
        {
            McpTaskStatus.Working => NhAssistantMcpTaskStatuses.Working,
            McpTaskStatus.Completed => NhAssistantMcpTaskStatuses.Completed,
            McpTaskStatus.Failed => NhAssistantMcpTaskStatuses.Failed,
            McpTaskStatus.Cancelled => NhAssistantMcpTaskStatuses.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A NewHeap MCP task never waits for input.")
        };
    }

    private static McpTaskStatus StatusOf(string status)
    {
        return status switch
        {
            NhAssistantMcpTaskStatuses.Working => McpTaskStatus.Working,
            NhAssistantMcpTaskStatuses.Completed => McpTaskStatus.Completed,
            NhAssistantMcpTaskStatuses.Failed => McpTaskStatus.Failed,
            NhAssistantMcpTaskStatuses.Cancelled => McpTaskStatus.Cancelled,
            _ => throw new InvalidOperationException($"The stored MCP task status '{status}' is unknown.")
        };
    }
}
