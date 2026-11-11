using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The MCP tasks of <c>UseTasks</c> in the assistant's store on SQL Server and PostgreSQL: a task reads
/// back as it was created, ends once, so a late cancellation never overwrites its result, and is deleted
/// once it expired, in bounded batches.
/// </summary>
public sealed class AssistantMcpTaskStoreTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_task_ends_once_and_is_deleted_after_it_expired(AssistantTestProvider provider)
    {
        await using var services = await database.CreateStorageAsync(provider, storage =>
        {
            storage.AddSingleton<NhAssistantMcpTaskStore>();
            storage.AddSingleton<NhAssistantMcpTaskRecords>();
        });
        var records = services.GetRequiredService<NhAssistantMcpTaskRecords>();
        var working = Record("0a1b2c3d4e5f60718293a4b5c6d7e8f9", tenantId: "tenant-1", divisionId: Guid.NewGuid().ToString("D"));
        var other = Record("ffeeddccbbaa99887766554433221100", tenantId: null, divisionId: null) with
        {
            ExpiresAt = Now.AddHours(3)
        };
        await records.CreateAsync(working, CancellationToken.None);
        await records.CreateAsync(other, CancellationToken.None);

        Assert.Equal(working, await records.FindAsync(working.TaskId, CancellationToken.None));
        Assert.Equal(other, await records.FindAsync(other.TaskId, CancellationToken.None));
        Assert.Null(await records.FindAsync("00000000000000000000000000000000", CancellationToken.None));

        const string result = "{\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}";
        var finishedAt = Now.AddMinutes(1);
        Assert.True(await records.TryFinishAsync(working.TaskId, McpTaskStatus.Completed, result, null, null, finishedAt, CancellationToken.None));
        Assert.False(await records.TryFinishAsync(working.TaskId, McpTaskStatus.Cancelled, null, null, null, Now.AddMinutes(2), CancellationToken.None));
        Assert.False(await records.TryFinishAsync("00000000000000000000000000000000", McpTaskStatus.Failed, null, "{}", null, Now, CancellationToken.None));

        var completed = await records.FindAsync(working.TaskId, CancellationToken.None);
        Assert.Equal(McpTaskStatus.Completed, completed!.Status);
        Assert.Equal(result, completed.ResultJson);
        Assert.Equal(finishedAt, completed.LastUpdatedAt);

        Assert.Equal(0, await records.DeleteExpiredAsync(working.ExpiresAt.AddTicks(-1), 10, CancellationToken.None));
        Assert.Equal(1, await records.DeleteExpiredAsync(working.ExpiresAt, 10, CancellationToken.None));
        Assert.Null(await records.FindAsync(working.TaskId, CancellationToken.None));
        Assert.NotNull(await records.FindAsync(other.TaskId, CancellationToken.None));
    }

    private static NhAiMcpTaskRecord Record(string taskId, string? tenantId, string? divisionId)
    {
        return new NhAiMcpTaskRecord(
            taskId,
            "projects_analyze_v1",
            "projects.analyze",
            1,
            "oidc:" + new string('a', 64),
            tenantId,
            divisionId,
            McpTaskStatus.Working,
            Now,
            Now,
            Now.AddHours(1),
            Now.AddMinutes(2),
            2_000,
            ResultJson: null,
            ErrorJson: null,
            StatusMessage: null);
    }
}
