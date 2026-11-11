using System.Net;
using System.Text.Json;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Trusted result cards over HTTP (<see cref="INhAssistantToolResultPresenter"/>): <c>tool.completed</c>
/// carries the card of a call the application presents, the conversation keeps it on the call after a
/// reload, also for an approved call whose card the resumed turn stored, and a failed call has none.
/// </summary>
public sealed class AssistantToolResultCardTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Tool_completed_and_the_reloaded_conversation_carry_the_result_card(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Active" } })
            .RespondWithText("Done: the project is active.");
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            model,
            assistantBuilder: assistant => assistant.AddToolResultPresenter<ProjectResultPresenter>(),
            provider: provider);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);

        using var turn = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new { text = "Activate the roadmap project.", clientMessageId = "c-1" }));
        var (events, _) = await ReadAllAsync(turn);

        var searched = Assert.Single(events, evt => evt.Name == "tool.completed").Data;
        var card = searched.GetProperty("resultPresentation");
        Assert.Equal("Roadmap project", card.GetProperty("title").GetString());
        Assert.Equal("1 project found", card.GetProperty("summary").GetString());
        Assert.Equal("/projects?query=roadmap", card.GetProperty("link").GetProperty("url").GetString());
        // The raw result stays in the preview of the technical details; the card holds only what the presenter wrote.
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", card.GetRawText(), StringComparison.Ordinal);
        var approval = Assert.Single(events, evt => evt.Name == "approval.required").Data;

        using var decided = await client.PostAsync(
            $"/api/assistant/conversations/{id}/approvals/{approval.GetProperty("approvalId").GetString()}/decide",
            Json(new { decision = "approve", expectedProposalHash = approval.GetProperty("proposalHash").GetString() }));
        var (resumed, _) = await ReadAllAsync(decided);
        var executed = Assert.Single(resumed, evt => evt.Name == "tool.completed").Data;
        var executedCard = executed.GetProperty("resultPresentation");
        Assert.Equal("Status changed", executedCard.GetProperty("title").GetString());
        var status = Assert.Single(executedCard.GetProperty("fields").EnumerateArray());
        Assert.Equal("Active", status.GetProperty("value").GetString());

        using var reloaded = await client.GetAsync($"/api/assistant/conversations/{id}");
        Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
        using var conversation = JsonDocument.Parse(await reloaded.Content.ReadAsStringAsync());
        var parts = conversation.RootElement.GetProperty("messages")
            .EnumerateArray()
            .SelectMany(message => message.GetProperty("parts").EnumerateArray())
            .ToArray();
        // The card is shown on its call, never as a part of its own.
        Assert.DoesNotContain(parts, part => part.GetProperty("type").GetString() == "tool-result");
        var calls = parts.Where(part => part.GetProperty("type").GetString() == "tool-call").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal("Roadmap project", calls[0].GetProperty("resultPresentation").GetProperty("title").GetString());
        // The approved call's part is in the paused message; the resumed turn stored its card.
        Assert.Equal("Status changed", calls[1].GetProperty("resultPresentation").GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_failed_call_and_a_tool_without_a_presenter_have_no_card()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("Nothing found.");
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            model,
            assistantBuilder: assistant => assistant.AddToolResultPresenter<ProjectResultPresenter>());
        app.Tools.SearchException = new InvalidOperationException("search failed");
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);

        using var turn = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new { text = "Find the roadmap project.", clientMessageId = "c-1" }));
        var (events, _) = await ReadAllAsync(turn);

        var completed = Assert.Single(events, evt => evt.Name == "tool.completed").Data;
        Assert.Equal("failed", completed.GetProperty("status").GetString());
        Assert.False(completed.TryGetProperty("resultPresentation", out _));
        using var reloaded = await client.GetAsync($"/api/assistant/conversations/{id}");
        using var conversation = JsonDocument.Parse(await reloaded.Content.ReadAsStringAsync());
        var call = conversation.RootElement.GetProperty("messages")
            .EnumerateArray()
            .SelectMany(message => message.GetProperty("parts").EnumerateArray())
            .Single(part => part.GetProperty("type").GetString() == "tool-call");
        Assert.False(call.TryGetProperty("resultPresentation", out _));
    }

    private static async Task<string> CreateConversationAsync(HttpClient client)
    {
        using var created = await client.PostAsync(
            "/api/assistant/conversations",
            Json(new { agentId = "project-assistant" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Presents the test catalog's search and status change from the data the tools returned.
    /// </summary>
    private sealed class ProjectResultPresenter : INhAssistantToolResultPresenter
    {
        public ValueTask<NhAssistantToolResultPresentation?> PresentResultAsync(
            NhAssistantToolResultPresentationContext context,
            CancellationToken cancellationToken)
        {
            if (context.Descriptor.Id == TestProjectToolCatalog.Search.Id
                && context.ReadData<ProjectSearchItem[]>() is [var first, ..] found)
            {
                return ValueTask.FromResult<NhAssistantToolResultPresentation?>(new NhAssistantToolResultPresentation(
                    first.Name,
                    [new NhAssistantPresentationField("Project", first.Name)],
                    $"{found.Length} project found",
                    new NhAssistantPresentationLink("/projects?query=roadmap", "Open projects")));
            }
            if (context.Descriptor.Id == TestProjectToolCatalog.ChangeStatus.Id
                && context.ReadData<ProjectStatusReport>() is { } report)
            {
                return ValueTask.FromResult<NhAssistantToolResultPresentation?>(new NhAssistantToolResultPresentation(
                    "Status changed",
                    [new NhAssistantPresentationField("Status", report.Status)]));
            }

            return ValueTask.FromResult<NhAssistantToolResultPresentation?>(null);
        }
    }
}
