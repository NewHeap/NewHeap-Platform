using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-245: when a turn reaches the sample's limit of four tool calls,
/// the assistant still answers from the results it gathered in one tool-free model call, and
/// the next turn sees a compact summary of those calls instead of repeating them blindly.
/// </summary>
public sealed partial class AssistantSamplesTests
{
    [Fact]
    public async Task SPM_245_the_tool_call_limit_ends_with_an_answer_and_a_follow_up_sees_the_calls()
    {
        var model = new NhAiScriptedChatClient();
        foreach (var query in new[] { "roadmap", "backlog", "design", "launch", "archive" })
        {
            model.RespondWithFunctionCall("projects_search_v1", new { input = new { query, limit = 5 } });
        }
        model
            .RespondWithText("I searched four topics; the archive search did not run within this answer.")
            .RespondWithText("Earlier I searched roadmap, backlog, design and launch.");
        host.Model.Use(model);
        using var client = host.CreateClient("spm-245-limit-user");
        var conversationId = await host.CreateConversationAsync(client);

        var limited = await host.SendAsync(client, conversationId, "Search every project topic.");

        Assert.Equal(4, limited.Count(evt => evt.Name == "tool.completed"));
        Assert.Contains(limited, evt => evt.Name == "message.delta");
        var end = limited[^1];
        Assert.Equal("turn.completed", end.Name);
        Assert.Equal("completed", end.Data.GetProperty("status").GetString());
        Assert.Equal("assistant-tool-call-limit-reached", end.Data.GetProperty("errorCode").GetString());
        Assert.Equal(ChatToolMode.None, model.Requests[5].Options!.ToolMode);

        var followUp = await host.SendAsync(client, conversationId, "I see no answer, what did you check?");

        Assert.Equal("completed", followUp[^1].Data.GetProperty("status").GetString());
        var replayed = model.Requests[^1].Messages.Single(message => message.Role == ChatRole.Assistant).Text;
        Assert.StartsWith("<tool-call-summary>", replayed, StringComparison.Ordinal);
        Assert.Contains("- projects.search v1 ", replayed, StringComparison.Ordinal);
        Assert.Contains("launch", replayed, StringComparison.Ordinal);
        Assert.DoesNotContain("archive\"", replayed.Split("</tool-call-summary>")[0], StringComparison.Ordinal);
    }
}
