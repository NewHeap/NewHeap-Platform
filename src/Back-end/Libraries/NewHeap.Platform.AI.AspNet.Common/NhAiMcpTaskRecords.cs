using System.Collections.Concurrent;
using ModelContextProtocol.Extensions.Tasks;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// One MCP task as its store keeps it: its owner, the tool it runs, its state and its bounded outcome.
/// It never holds the call's arguments.
/// </summary>
/// <param name="TaskId">The task's id: 32 lowercase hexadecimal characters of a random value.</param>
/// <param name="ToolName">The name the MCP export gives the tool.</param>
/// <param name="ToolId">The tool's id, for the audit.</param>
/// <param name="ToolVersion">The tool's version, for the audit.</param>
/// <param name="ActorId">The actor that created the task.</param>
/// <param name="TenantId">The actor's tenant, or <see langword="null"/>.</param>
/// <param name="DivisionId">The active division of the call, or <see langword="null"/>.</param>
/// <param name="Status">Working, completed, failed or cancelled; never waiting for input.</param>
/// <param name="CreatedAt">When the task was created, on the application's clock.</param>
/// <param name="LastUpdatedAt">When the task last changed, on the application's clock.</param>
/// <param name="ExpiresAt">From when the task does not exist any more.</param>
/// <param name="AbandonAt">From when a working task counts as abandoned.</param>
/// <param name="PollIntervalMs">How often the client should poll.</param>
/// <param name="ResultJson">The tool's result of a completed task.</param>
/// <param name="ErrorJson">The JSON-RPC error of a failed task.</param>
/// <param name="StatusMessage">A stable code that explains the status, such as an abandoned task's.</param>
internal sealed record NhAiMcpTaskRecord(
    string TaskId,
    string ToolName,
    string ToolId,
    int ToolVersion,
    string ActorId,
    string? TenantId,
    string? DivisionId,
    McpTaskStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset AbandonAt,
    long PollIntervalMs,
    string? ResultJson,
    string? ErrorJson,
    string? StatusMessage);

/// <summary>
/// Where <see cref="NhAiMcpTaskStore"/> keeps its tasks. The assistant registers its store, which every
/// instance of the host shares; <see cref="NhAiInMemoryMcpTaskRecords"/> is for tests and development.
/// A task is created durably before the call returns it, so every instance finds it at once.
/// </summary>
internal interface INhAiMcpTaskRecords
{
    Task CreateAsync(NhAiMcpTaskRecord record, CancellationToken cancellationToken);

    Task<NhAiMcpTaskRecord?> FindAsync(string taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a working task with <paramref name="status"/> and its outcome. Returns <see langword="false"/>
    /// when the task does not exist or ended already, which it leaves unchanged.
    /// </summary>
    Task<bool> TryFinishAsync(
        string taskId,
        McpTaskStatus status,
        string? resultJson,
        string? errorJson,
        string? statusMessage,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Deletes at most <paramref name="maximum"/> tasks that expired at <paramref name="now"/>.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, int maximum, CancellationToken cancellationToken);
}

/// <summary>
/// Tasks in this process only, for tests and development (<see cref="NhAiMcpTaskOptions.UseInMemoryStore"/>).
/// </summary>
internal sealed class NhAiInMemoryMcpTaskRecords : INhAiMcpTaskRecords
{
    private readonly ConcurrentDictionary<string, NhAiMcpTaskRecord> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task CreateAsync(NhAiMcpTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_records.TryAdd(record.TaskId, record))
        {
            throw new InvalidOperationException("An MCP task with this id exists already.");
        }

        return Task.CompletedTask;
    }

    public Task<NhAiMcpTaskRecord?> FindAsync(string taskId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_records.TryGetValue(taskId, out var record) ? record : null);
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
        lock (_gate)
        {
            if (!_records.TryGetValue(taskId, out var record) || record.Status != McpTaskStatus.Working)
            {
                return Task.FromResult(false);
            }

            _records[taskId] = record with
            {
                Status = status,
                ResultJson = resultJson,
                ErrorJson = errorJson,
                StatusMessage = statusMessage,
                LastUpdatedAt = at
            };
            return Task.FromResult(true);
        }
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, int maximum, CancellationToken cancellationToken)
    {
        var expired = _records.Values
            .Where(record => record.ExpiresAt <= now)
            .Take(maximum)
            .ToList();
        foreach (var record in expired)
        {
            _records.TryRemove(record.TaskId, out _);
        }

        return Task.FromResult(expired.Count);
    }
}
