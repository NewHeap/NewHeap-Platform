using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-245: models that send tool arguments without the <c>input</c>
/// envelope still run the curated tool once, and a mixed shape becomes the recoverable
/// <c>ai-tool-input-invalid</c> result the model corrects in the same turn.
/// </summary>
public sealed partial class AssistantSamplesTests
{
    [Fact]
    public async Task SPM_245_flat_tool_arguments_run_and_a_mixed_shape_is_recoverable()
    {
        host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { query = "roadmap", limit = 5 })
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" }, limit = 5 })
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap", limit = 5 } })
            .RespondWithText("One roadmap project is visible in your division."));
        using var client = host.CreateClient("spm-245-arguments-user");
        var conversationId = await host.CreateConversationAsync(client);

        var events = await host.SendAsync(client, conversationId, "Which roadmap projects exist?");

        var completed = events.Where(evt => evt.Name == "tool.completed").Select(evt => evt.Data).ToArray();
        Assert.Equal(3, completed.Length);
        Assert.Equal("succeeded", completed[0].GetProperty("status").GetString());
        Assert.Equal("failed", completed[1].GetProperty("status").GetString());
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, completed[1].GetProperty("resultCode").GetString());
        Assert.Equal("succeeded", completed[2].GetProperty("status").GetString());
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
    }
}
