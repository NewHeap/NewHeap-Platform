using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// TC1: the agent's activity shows each composition call with the tool calls it ran in order, the model
/// rounds that saved and the size of the result returned to the model, and per tool what the calls
/// saved, from the tool invocations the turns stored. The administration reads it content-free. Both
/// relational providers for the query; the endpoint's JSON names on PostgreSQL.
/// </summary>
public sealed class AssistantCompositionActivityTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_activity_shows_what_each_composition_call_ran_and_saved(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestCompositions.OverviewFunction, new { input = new { projectId } }, "call-overview")
            .RespondWithText("Two risks.")
            .RespondWithFunctionCall(TestCompositions.FlagOpenRisksFunction, new { input = new { projectId } }, "call-flag");
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton<TestRiskToolRecorder>();
                services.AddSingleton(new TestCompositionSwitchState());
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestRiskToolCatalog>());
            },
            assistantBuilder: assistant => assistant
                .ConfigureAgentContext(AgentId, context => context.AllowCompositions())
                .UseCompositionSwitch<TestCompositionSwitch>()
                .AddTestCompositions());
        var conversation = await host.CreateConversationAsync();
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);

        await host.SendAsync(conversation.Id, "Give me the overview.");
        await host.SendAsync(conversation.Id, "Flag the first risk.");

        await using var scope = host.Services.CreateAsyncScope();
        var activity = await scope.ServiceProvider.GetRequiredService<NhAssistantCompositionActivity>()
            .GetAsync(AgentId, from, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(2, activity.Total);
        Assert.Equal([TestCompositions.FlagOpenRisks, TestCompositions.Overview], activity.Calls.Select(call => call.ToolId).ToArray());
        var rows = await AssistantToolStepTests.RowsAsync(host, conversation.Id);
        var overview = activity.Calls[1];
        Assert.Equal(NhAiCompositionFeature.Compositions, overview.Feature);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, overview.Status);
        Assert.Equal(
            [TestRiskToolCatalog.GetProject.Id, TestRiskToolCatalog.ListRisks.Id, TestRiskToolCatalog.GetPeople.Id],
            overview.Steps.Select(step => step.ToolId).ToArray());
        Assert.Equal([0, 1, 2], overview.Steps.Select(step => step.Index).ToArray());
        Assert.Equal(2, overview.RoundsSaved);
        Assert.Equal(rows.Single(row => row.Id == overview.InvocationId).ResultJson!.Length, overview.ResultCharacters);
        Assert.True(overview.ResultCharacters > 0);
        var flag = activity.Calls[0];
        Assert.Equal(
            [NhAssistantToolCallStatuses.Succeeded, NhAssistantToolCallStatuses.AwaitingApproval],
            flag.Steps.Select(step => step.Status).ToArray());
        Assert.Equal(1, flag.RoundsSaved);
        var usage = Assert.Single(activity.Tools, tool => tool.ToolId == TestCompositions.Overview);
        Assert.Equal((1, 3, 2), (usage.Calls, usage.ToolCalls, usage.RoundsSaved));
        Assert.Equal(overview.ResultCharacters, usage.MaxResultCharacters);

        // Another agent's activity holds none of them.
        var other = await scope.ServiceProvider.GetRequiredService<NhAssistantCompositionActivity>()
            .GetAsync("another-agent", from, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(0, other.Total);
        Assert.Empty(other.Calls);
    }

    [Fact]
    public async Task The_administration_reads_the_activity_with_stable_json_names_and_no_content()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TestRiskToolRecorder>();
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestRiskToolCatalog>());
            },
            assistantBuilder: assistant => assistant.AddTestCompositions());
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            context.Conversations.Add(new AssistantConversation
            {
                Id = conversationId,
                OwnerActorId = "user-1",
                AgentId = AgentId,
                AgentVersion = 1,
                Status = NhAssistantConversationStatuses.Idle,
                CreatedAt = startedAt,
                UpdatedAt = startedAt,
                ConcurrencyStamp = Guid.NewGuid()
            });
            context.ToolInvocations.AddRange(
                Row(conversationId, turnId, parentId, "call-1", TestCompositions.Overview, NhAssistantToolCallStatuses.Failed, NhAiToolFailureCodes.AuthorizationDenied, "{\"success\":false,\"secret\":\"value\"}", startedAt),
                Row(conversationId, turnId, Guid.NewGuid(), NhAssistantToolStepCallIds.Of(NhAiToolCallOriginKind.Composition, parentId, 1), TestRiskToolCatalog.ListRisks.Id, NhAssistantToolCallStatuses.Failed, NhAiToolFailureCodes.AuthorizationDenied, null, startedAt),
                Row(conversationId, turnId, Guid.NewGuid(), NhAssistantToolStepCallIds.Of(NhAiToolCallOriginKind.Composition, parentId, 0), TestRiskToolCatalog.GetProject.Id, NhAssistantToolCallStatuses.Succeeded, null, "{\"success\":true}", startedAt));
            await context.SaveChangesAsync();
        }

        using var admin = app.CreateClient(admin: true);
        using var response = await admin.GetAsync($"/api/assistant/admin/agents/{AgentId}/compositions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(["agentId", "from", "to", "total", "truncated", "tools", "calls"], root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal(
            ["toolId", "kind", "calls", "toolCalls", "roundsSaved", "averageResultCharacters", "maxResultCharacters"],
            tool.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("composition", tool.GetProperty("kind").GetString());
        var call = Assert.Single(root.GetProperty("calls").EnumerateArray());
        Assert.Equal(
            ["invocationId", "conversationId", "toolId", "kind", "status", "resultCode", "startedAt", "steps", "roundsSaved", "resultCharacters"],
            call.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(NhAiToolFailureCodes.AuthorizationDenied, call.GetProperty("resultCode").GetString());
        Assert.Equal(1, call.GetProperty("roundsSaved").GetInt32());
        var steps = call.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(["index", "toolId", "status", "resultCode"], steps[0].EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal([TestRiskToolCatalog.GetProject.Id, TestRiskToolCatalog.ListRisks.Id], steps.Select(step => step.GetProperty("toolId").GetString()).ToArray());
        // Content-free: the stored result never reaches the administration.
        Assert.DoesNotContain("secret", body);

        using var unknown = await admin.GetAsync("/api/assistant/admin/agents/unknown-agent/compositions");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var invalid = await admin.GetAsync($"/api/assistant/admin/agents/{AgentId}/compositions?from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var member = app.CreateClient();
        using var forbidden = await member.GetAsync($"/api/assistant/admin/agents/{AgentId}/compositions");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private static AssistantToolInvocation Row(
        Guid conversationId,
        Guid turnId,
        Guid id,
        string callId,
        string toolId,
        string status,
        string? resultCode,
        string? resultJson,
        DateTimeOffset startedAt)
    {
        return new AssistantToolInvocation
        {
            Id = id,
            ConversationId = conversationId,
            TurnId = turnId,
            MessageId = Guid.NewGuid(),
            CallId = callId,
            FunctionName = toolId.Replace('.', '_'),
            ToolId = toolId,
            ToolVersion = 1,
            ContractHash = new string('a', 64),
            DisplayName = toolId,
            Status = status,
            ResultCode = resultCode,
            ResultJson = resultJson,
            StartedAt = startedAt,
            CompletedAt = startedAt
        };
    }
}
