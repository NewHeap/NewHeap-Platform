using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The HTTP contract of client tools: the browser declares the tools it can run with its message, the
/// <c>client-tool.requested</c> event of its own stream carries the bound arguments, the conversation shows
/// the waiting call without them, and only the person whose turn called the tool reports its result with
/// <c>POST conversations/{id}/client-tools/{invocationId}/result</c>, which streams the resumed turn.
/// </summary>
public sealed class AssistantClientToolEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string OpenToolId = "projects.open-in-app";

    [Fact]
    public async Task The_browser_runs_the_tool_and_only_its_person_reports_the_result_over_http()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_open_in_app_v1", new { input = new { projectId } })
            .RespondWithText("The project is open.");
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            model,
            assistantBuilder: assistant => assistant.AddClientTool<OpenProjectInput>(OpenToolId, tool => tool
                .Describe("Opens the page of a project in the application.")
                .RequirePolicy(TestProjectToolCatalog.ReadPolicy)));
        using var client = app.CreateClient();
        using var created = await client.PostAsync("/api/assistant/conversations", Json(new { agentId = "project-assistant" }));
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetString()!;
        await AddParticipantAsync(app, Guid.Parse(id), "user-2");

        using var turn = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new { text = "Open the launch project.", clientMessageId = "c-1", clientTools = new[] { OpenToolId } }));

        var (events, _) = await ReadAllAsync(turn);
        Assert.Equal(
            ["turn.started", "tool.started", "client-tool.requested", "turn.completed"],
            events.Select(evt => evt.Name));
        var requested = events[2].Data;
        Assert.Equal(
            ["invocationId", "toolId", "actorId", "expiresAt", "status", "arguments"],
            requested.EnumerateObject().Select(property => property.Name));
        Assert.Equal(OpenToolId, requested.GetProperty("toolId").GetString());
        Assert.Equal("user-1", requested.GetProperty("actorId").GetString());
        Assert.Equal("pending", requested.GetProperty("status").GetString());
        Assert.Equal(projectId, requested.GetProperty("arguments").GetProperty("projectId").GetGuid());
        Assert.Equal("waiting-for-approval", events[3].Data.GetProperty("status").GetString());
        var invocationId = requested.GetProperty("invocationId").GetString();

        // The conversation shows the waiting call without its arguments, to everyone in it.
        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        var pending = conversation.GetProperty("pendingClientTool");
        Assert.Equal(invocationId, pending.GetProperty("invocationId").GetString());
        Assert.False(pending.TryGetProperty("arguments", out _));
        using var participant = app.CreateClient("user-2");
        var shared = await ReadJsonAsync(await participant.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("user-1", shared.GetProperty("pendingClientTool").GetProperty("actorId").GetString());

        var result = $"/api/assistant/conversations/{id}/client-tools/{invocationId}/result";
        using var forbidden = await participant.PostAsync(result, Json(new { status = "completed", outcome = "navigated" }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("assistant-client-tool-forbidden", (await ReadJsonAsync(forbidden)).GetProperty("code").GetString());
        using var stranger = app.CreateClient("user-3");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync(result, Json(new { status = "completed" }))).StatusCode);
        using var invalid = await client.PostAsync(result, Json(new { status = "done" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("assistant-client-tool-result-invalid", (await ReadJsonAsync(invalid)).GetProperty("code").GetString());

        using var reported = await client.PostAsync(result, Json(new { status = "completed", outcome = "navigated", clientTools = new[] { OpenToolId } }));
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        var (resumed, _) = await ReadAllAsync(reported);
        Assert.Equal("turn.started", resumed[0].Name);
        Assert.Contains(resumed, evt => evt.Name == "tool.completed" && evt.Data.GetProperty("status").GetString() == "succeeded");
        Assert.Equal("completed", resumed[^1].Data.GetProperty("status").GetString());
        var after = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("idle", after.GetProperty("status").GetString());
        Assert.False(after.TryGetProperty("pendingClientTool", out _));

        using var late = await client.PostAsync(result, Json(new { status = "completed" }));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("assistant-client-tool-not-pending", (await ReadJsonAsync(late)).GetProperty("code").GetString());
    }

    private static async Task AddParticipantAsync(AssistantWebApplication app, Guid conversationId, string actorId)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Participants.Add(new AssistantConversationParticipant
        {
            ConversationId = conversationId,
            ActorId = actorId,
            DisplayName = actorId,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public sealed record OpenProjectInput(Guid ProjectId);
}
