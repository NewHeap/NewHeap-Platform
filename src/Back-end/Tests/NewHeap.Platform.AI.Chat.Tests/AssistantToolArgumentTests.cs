using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Models sometimes send tool arguments without the <c>input</c> envelope, or mix the envelope
/// with other properties. The assistant must execute the former and turn the latter into a
/// recoverable <c>ai-tool-input-invalid</c> result the model can correct.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantToolArgumentTests(AssistantDatabaseFixture database)
{
    private const string SearchFunction = "projects_search_v1";

    [Fact]
    public async Task Flat_tool_arguments_without_the_input_envelope_execute_once()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { query = "roadmap" }, "call-flat")
            .RespondWithText("I found one roadmap project.");
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Which roadmap projects exist?");

        var completed = turn.Single<NhAssistantToolCompletedEvent>();
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, completed.Status);
        Assert.Null(completed.ResultCode);
        Assert.Contains("Roadmap project", completed.ResultPreview);
        Assert.Single(host.Tools.Contexts);
        Assert.Equal(NhAssistantTurnStatuses.Completed, Assert.IsType<NhAssistantTurnCompletedEvent>(turn.Events[^1]).Status);
    }

    [Fact]
    public async Task Envelope_mixed_with_other_properties_returns_input_invalid_and_the_model_can_retry()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "roadmap" }, page = 1 }, "call-mixed")
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "roadmap" } }, "call-retry")
            .RespondWithText("I found one roadmap project.");
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Which roadmap projects exist?");

        var completed = turn.Events.OfType<NhAssistantToolCompletedEvent>().ToArray();
        Assert.Equal(2, completed.Length);
        Assert.Equal(NhAssistantToolCallStatuses.Failed, completed[0].Status);
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, completed[0].ResultCode);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, completed[1].Status);
        Assert.Single(host.Tools.Contexts);
        var firstResult = model.FunctionResults[0];
        Assert.Equal("call-mixed", firstResult.CallId);
        var resultText = System.Text.Json.JsonSerializer.Serialize(firstResult.Result);
        Assert.Contains(NhAiToolFailureCodes.InputInvalid, resultText, StringComparison.Ordinal);
        Assert.Contains("page", resultText, StringComparison.Ordinal);
        Assert.DoesNotContain("roadmap", resultText, StringComparison.Ordinal);
        Assert.Equal("I found one roadmap project.", turn.Text);
    }

    [Fact]
    public async Task Unexpected_tool_exception_is_logged_without_arguments_messages_or_results()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "secret-argument-value" } }, "call-throw")
            .RespondWithText("The search failed.");
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        host.Tools.SearchException = new InvalidOperationException("secret-exception-message");
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Search for the secret.");

        var completed = turn.Single<NhAssistantToolCompletedEvent>();
        Assert.Equal(NhAiToolFailureCodes.Failed, completed.ResultCode);
        var warnings = host.Logs.Messages
            .Where(message => message.Contains("projects.search", StringComparison.Ordinal)
                && message.Contains("System.InvalidOperationException", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(warnings, message => message.StartsWith("Assistant tool projects.search v1 failed unexpectedly in turn", StringComparison.Ordinal));
        Assert.Contains(warnings, message => message.StartsWith("AI tool projects.search v1 failed unexpectedly", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("secret-argument-value", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("secret-exception-message", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("Roadmap project", StringComparison.Ordinal));
    }
}
