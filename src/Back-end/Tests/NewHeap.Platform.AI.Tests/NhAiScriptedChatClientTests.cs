using System.Text.Json;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiScriptedChatClientTests
{
    [Fact]
    public async Task Scripted_client_plays_text_and_function_calls_deterministically()
    {
        var first = await PlayScriptAsync();
        var second = await PlayScriptAsync();

        Assert.Equal(first, second);
        Assert.Equal(
            ["call:projects_search_v1:scripted-call-1", "Found two projects."],
            first);
    }

    [Fact]
    public async Task Streaming_splits_text_into_word_chunks_and_reports_usage()
    {
        var client = new NhAiScriptedChatClient()
            .RespondWithText("Two projects match the roadmap.");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Find roadmap projects")]))
        {
            updates.Add(update);
        }

        Assert.Equal(
            ["Two ", "projects ", "match ", "the ", "roadmap."],
            updates.Where(update => update.Text.Length > 0).Select(update => update.Text));
        var usage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>());
        Assert.True(usage.Details.InputTokenCount > 0);
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
    }

    [Fact]
    public async Task A_response_after_a_function_call_requires_the_function_result()
    {
        var client = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithText("Done.");
        var user = new ChatMessage(ChatRole.User, "Find roadmap projects");
        var call = await client.GetResponseAsync([user]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([user]));

        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "{\"success\":true}")]);
        var answer = await client.GetResponseAsync([user, call.Messages[0], result]);
        Assert.Equal("Done.", answer.Text);
        Assert.Equal("call-1", Assert.Single(client.FunctionResults).CallId);
        Assert.Equal(0, client.RemainingSteps);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([user]));
    }

    [Fact]
    public async Task A_looping_script_restarts_when_it_is_exhausted()
    {
        var client = new NhAiScriptedChatClient(loop: true).RespondWithText("Hello.");

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")]);
        var second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi again")]);

        Assert.Equal("Hello.", first.Text);
        Assert.Equal("Hello.", second.Text);
        Assert.Equal(2, client.Requests.Count);
    }

    private static async Task<IReadOnlyList<string>> PlayScriptAsync()
    {
        var client = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap", limit = 5 } })
            .RespondWithText("Found two projects.");
        var user = new ChatMessage(ChatRole.User, "Find roadmap projects");
        var played = new List<string>();

        var callResponse = await client.GetResponseAsync([user]);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(callResponse.Messages[0].Contents));
        played.Add($"call:{call.Name}:{call.CallId}");
        var input = Assert.IsType<JsonElement>(call.Arguments!["input"]);
        Assert.Equal("roadmap", input.GetProperty("query").GetString());

        var toolMessage = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "[]")]);
        var answer = await client.GetResponseAsync([user, callResponse.Messages[0], toolMessage]);
        played.Add(answer.Text);
        return played;
    }
}
