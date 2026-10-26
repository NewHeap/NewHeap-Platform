using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-345: an agent editor's save that omits <c>mcpServerIds</c> keeps an assignment an administrator
/// made with <c>PUT admin/mcp-servers/{id}/agents/{agentId}</c> while the editor was open, and
/// <c>DELETE</c> removes it, each without a new agent version. SPM-346: the operations code stores the
/// reviewed settings of the milestone lookup before any sync has seen it, and the first sync keeps them.
/// A host of their own, so the audit of SPM-331 stays its own.
/// </summary>
public sealed class AssistantMcpAssignmentSamplesTests(AssistantOperationsSampleHost host)
    : IClassFixture<AssistantOperationsSampleHost>
{
    private const string ApiKey = "sample-milestones-key-345";
    private const string MilestoneDesk = "sample-milestone-desk";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_345_an_editor_save_without_the_server_list_keeps_an_assignment_made_while_it_was_open()
    {
        await using var milestones = await SampleMcpServer.StartAsync(ApiKey);
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        using var user = host.CreateClient("spm-345-user");
        try
        {
            Assert.True((await OperationsAsync(operations => operations.ConnectMilestonesAsync(milestones.Url, ApiKey, host.OwnerId, CancellationToken.None))).Success);
            // A desk created in the administration without servers; an editor opens it at version 1.
            using (var created = await administrator.PostAsJsonAsync("/api/assistant/admin/agents", DeskBody("Answers milestone questions.", version: null), Json))
            {
                Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            }
            var opened = await AgentAsync(administrator);
            Assert.Empty(opened.GetProperty("mcpServerIds").EnumerateArray());

            // Meanwhile an administrator assigns the milestones server to the desk: only the assignment
            // changes, behind the admin policy.
            using var forbidden = await user.PutAsync(AssignmentPath, null);
            using var assigned = await administrator.PutAsync(AssignmentPath, null);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.True(assigned.StatusCode == HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());
            var server = await AssistantSampleHost.ReadJsonAsync(assigned);
            Assert.Equal(
                [MilestoneDesk, SampleStatusAdvisorComposition.AgentId],
                server.GetProperty("assignedAgentIds").EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal));
            Assert.Equal(opened.GetProperty("version").GetInt32(), (await AgentAsync(administrator)).GetProperty("version").GetInt32());

            // The editor saves the version it opened without the list: no conflict, and the server stays.
            using var saved = await administrator.PutAsJsonAsync(
                $"/api/assistant/admin/agents/{MilestoneDesk}",
                DeskBody("Answers milestone questions with the milestones server.", version: opened.GetProperty("version").GetInt32()),
                Json);
            Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
            var kept = await AssistantSampleHost.ReadJsonAsync(saved);
            Assert.Equal([SampleAssistantOperationsService.MilestonesServerId], kept.GetProperty("mcpServerIds").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(opened.GetProperty("version").GetInt32() + 1, kept.GetProperty("version").GetInt32());

            // Removing the assignment again leaves the definition and its version as they are.
            using var removed = await administrator.DeleteAsync(AssignmentPath);
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.Equal(
                [SampleStatusAdvisorComposition.AgentId],
                (await AssistantSampleHost.ReadJsonAsync(removed)).GetProperty("assignedAgentIds").EnumerateArray().Select(item => item.GetString()));
            var after = await AgentAsync(administrator);
            Assert.Equal((kept.GetProperty("version").GetInt32(), 0), (after.GetProperty("version").GetInt32(), after.GetProperty("mcpServerIds").GetArrayLength()));

            var changes = host.Services.GetRequiredService<SampleAssistantAuditLog>().Events
                .Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged && evt.AgentId == MilestoneDesk)
                .Select(evt => evt.ResultCode);
            Assert.Equal(["assigned", "unassigned"], changes);
        }
        finally
        {
            using var deleted = await administrator.DeleteAsync($"/api/assistant/admin/agents/{MilestoneDesk}");
            Assert.True((await OperationsAsync(operations => operations.DisconnectMilestonesAsync(host.OwnerId, CancellationToken.None))).Success);
        }
    }

    [Fact]
    public async Task SPM_346_the_operations_code_reviews_the_milestone_lookup_before_the_first_sync_sees_it()
    {
        await using var milestones = await SampleMcpServer.StartAsync(ApiKey);
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        try
        {
            var prepared = await OperationsAsync(operations => operations.PrepareMilestonesAsync(milestones.Url, ApiKey, host.OwnerId, CancellationToken.None));

            // The lookup waits for its first sync with the reviewed settings; nothing contacted the server.
            Assert.True(prepared.Success, string.Join("; ", prepared.GetResultItems().Select(item => item.Name)));
            var waiting = prepared.Data!;
            Assert.Equal(
                (NhAssistantMcpToolStatuses.Missing, true, true, NhAssistantMcpToolEffects.ReadOnly, SampleAssistantOperationsService.NextMilestoneDescription),
                (waiting.Status, waiting.AwaitingSync, waiting.IsEnabled, waiting.Effect, waiting.DescriptionOverride));
            Assert.Empty(milestones.ApiKeys);
            var listed = (await host.GetJsonAsync(administrator, "/api/assistant/admin/mcp-servers/milestones/tools")).EnumerateArray().Single();
            Assert.Equal(("missing", true), (listed.GetProperty("status").GetString(), listed.GetProperty("awaitingSync").GetBoolean()));

            var synced = await OperationsAsync(operations => operations.SyncMilestonesAsync(host.OwnerId, CancellationToken.None));

            // The first sync takes the lookup's schema and keeps what was reviewed.
            Assert.True(synced.Success, string.Join("; ", synced.GetResultItems().Select(item => item.Name)));
            var available = synced.Data!;
            Assert.Equal(
                (NhAssistantMcpToolStatuses.Available, false, true, NhAssistantMcpToolEffects.ReadOnly, SampleAssistantOperationsService.NextMilestoneDescription),
                (available.Status, available.AwaitingSync, available.IsEnabled, available.Effect, available.DescriptionOverride));
            Assert.All(milestones.ApiKeys, key => Assert.Equal(ApiKey, key));
            var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>().Events;
            var reviewed = Assert.Single(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolReviewedBeforeSync);
            var seen = Assert.Single(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolSeenAfterReview);
            Assert.Equal(("milestones/next_milestone", "enabled"), (reviewed.ObjectId, reviewed.ResultCode));
            Assert.Equal(("milestones/next_milestone", "enabled"), (seen.ObjectId, seen.ResultCode));
        }
        finally
        {
            Assert.True((await OperationsAsync(operations => operations.DisconnectMilestonesAsync(host.OwnerId, CancellationToken.None))).Success);
        }
    }

    private static string AssignmentPath => $"/api/assistant/admin/mcp-servers/{SampleAssistantOperationsService.MilestonesServerId}/agents/{MilestoneDesk}";

    /// <summary>
    /// The desk as the agent editor saves it: without <c>mcpServerIds</c>, so an update keeps the
    /// desk's assignments and a new desk has none.
    /// </summary>
    private static Dictionary<string, object?> DeskBody(string description, int? version)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = MilestoneDesk,
            ["displayName"] = "Milestone desk",
            ["description"] = description,
            ["instructions"] = "Answer questions about the milestones of projects.",
            ["toolSelectors"] = new[] { "projects.*" },
            ["requiredPolicy"] = SampleAssistantComposition.AccessPolicy,
            ["autonomy"] = "observe"
        };
        if (version is { } expected)
        {
            body["expectedVersion"] = expected;
        }

        return body;
    }

    private async Task<JsonElement> AgentAsync(HttpClient administrator)
    {
        var agents = await host.GetJsonAsync(administrator, "/api/assistant/admin/agents");
        return agents.EnumerateArray().Single(item => item.GetProperty("id").GetString() == MilestoneDesk);
    }

    private async Task<TaskResult<T>> OperationsAsync<T>(Func<SampleAssistantOperationsService, Task<TaskResult<T>>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleAssistantOperationsService>());
    }

    private async Task<TaskResult> OperationsAsync(Func<SampleAssistantOperationsService, Task<TaskResult>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleAssistantOperationsService>());
    }
}
