using Microsoft.Extensions.AI;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// The shared hosts' model: a script installed for one agent answers only that agent's turns. A turn of
/// another agent, such as the intake reviewer that every new project starts once the outbox delivers
/// the event, gets the host's own client and leaves the script alone, however late it runs.
/// </summary>
public sealed class SwitchableChatClientTests
{
    [Fact]
    public async Task A_script_for_one_agent_answers_only_that_agents_turns()
    {
        var model = new SwitchableChatClient();
        model.Use(new NhAiScriptedChatClient(loop: true).RespondWithText("host"));
        var script = new NhAiScriptedChatClient().RespondWithText("advisor");
        model.UseFor("status-advisor", script);

        Assert.Equal("host", await AnswerAsync(model, "intake-reviewer"));
        Assert.Equal("host", await AnswerAsync(model, agentId: null));
        Assert.Empty(script.Requests);
        Assert.Equal("advisor", await AnswerAsync(model, "status-advisor"));

        // Installing a client for every turn forgets the script of one agent.
        model.Use(new NhAiScriptedChatClient(loop: true).RespondWithText("again"));
        Assert.Equal("again", await AnswerAsync(model, "status-advisor"));
    }

    private static async Task<string> AnswerAsync(SwitchableChatClient model, string? agentId)
    {
        using var scope = agentId is null ? null : NhAiAgentInvocationScopes.Begin(Scope(agentId));
        return (await model.GetResponseAsync([new ChatMessage(ChatRole.User, "When is the next milestone?")])).Text;
    }

    private static NhAiAgentInvocationScope Scope(string agentId)
    {
        return new NhAiAgentInvocationScope(
            agentId,
            agentId,
            1,
            new NhAiSubjectInfo(new NhAiSubjectRef("project", "project-0001"), NhAiSubjectInfo.CreateScopeKey(null, null)),
            "owner-1",
            "run-1",
            "project-assistance");
    }
}
