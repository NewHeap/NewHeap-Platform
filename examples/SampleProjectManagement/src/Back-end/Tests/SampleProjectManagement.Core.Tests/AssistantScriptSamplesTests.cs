using System.Text.Json;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Scripting;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-373 and SPM-374: the project assistant answers a chained question with
/// one script that runs the turn's tools on the server and returns only what the answer needs, and a
/// status change inside a script gets the sample's approval exactly as the same call on its own does.
/// The sample switches scripts on with <c>NewHeap:AI:Assistant:Scripts</c>.
/// </summary>
public sealed class AssistantScriptSamplesTests(AssistantScriptSampleHost host) : IClassFixture<AssistantScriptSampleHost>
{
    [Fact]
    public async Task SPM_373_the_assistant_answers_a_chained_question_with_one_script()
    {
        const string script = """
            const roadmap = await tools.projects_search_v1({ query: "roadmap", limit: 5 });
            const launch = await tools.projects_search_v1({ query: "launch", limit: 5 });
            if (!roadmap.success || !launch.success) { return { failed: true }; }
            const keys = [...new Set([...roadmap.data, ...launch.data].map(project => project.key))];
            return { keys, searched: 2 };
            """;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script })
            .RespondWithText("Your roadmap and launch work is in PRJ-1.");
        host.Model.Use(model);
        var searchesBefore = host.Projects.SearchedDivisions.Count;
        using var client = host.CreateClient("spm-373-user");
        var conversationId = await host.CreateConversationAsync(client);

        var events = await host.SendAsync(client, conversationId, "Which projects cover the roadmap and the launch?");

        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        // The script tool is offered next to the individual tools.
        var offered = model.Requests[0].Options!.Tools!.Select(tool => tool.Name).ToArray();
        Assert.Contains(NhAiScriptTool.FunctionName, offered);
        Assert.Contains("projects_search_v1", offered);
        // Both searches ran in the person's division, as their own calls would.
        Assert.Equal(2, host.Projects.SearchedDivisions.Count - searchesBefore);
        Assert.All(host.Projects.SearchedDivisions.Skip(searchesBefore), division => Assert.Equal(host.DivisionId, division));
        // Only the program's value reached the model, with the calls it ran in order.
        var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        var data = result.GetProperty("data");
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal("""{"keys":["PRJ-1"],"searched":2}""", data.GetProperty("value").GetRawText());
        Assert.Equal(["done", "done"], data.GetProperty("calls").EnumerateArray().Select(call => call.GetProperty("outcome").GetString()!).ToArray());
        Assert.Equal(1, data.GetProperty("roundsSaved").GetInt32());
        // One model round for the chain: the script call, then the answer.
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task SPM_374_a_status_change_in_a_script_waits_for_the_persons_approval_as_it_does_on_its_own()
    {
        var projectId = Guid.NewGuid();
        var script = $$"""
            const found = await tools.projects_search_v1({ query: "roadmap", limit: 1 });
            const changed = await tools.projects_change_status_v1({ projectId: "{{projectId}}", status: "Active" });
            return { key: found.data[0].key, changed: changed.success };
            """;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script })
            .RespondWithText("The roadmap project is active now.");
        host.Model.Use(model);
        using var client = host.CreateClient("spm-374-user");
        var conversationId = await host.CreateConversationAsync(client);

        var paused = await host.SendAsync(client, conversationId, "Activate the roadmap project.");

        // The change inside the script is the same proposal the call gets on its own; the script stopped
        // there and the turn waits for the person.
        var approval = paused.Single(evt => evt.Name == "approval.required").Data;
        Assert.Equal("projects.change-status", approval.GetProperty("toolId").GetString());
        Assert.Equal("waiting-for-approval", paused[^1].Data.GetProperty("status").GetString());
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == projectId);

        var resumed = await host.DecideAsync(client, conversationId, approval, "approve");

        Assert.Equal("completed", resumed[^1].Data.GetProperty("status").GetString());
        Assert.Equal(ProjectStatus.Active, host.Projects.StatusChanges.Single(change => change.ProjectId == projectId).Status);
        Assert.Contains(projectId, host.Projects.VerifiedProjects);
        var stopped = model.Requests[^1].Messages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Select(content => JsonSerializer.SerializeToElement(content.Result))
            .First(content => content.TryGetProperty("code", out var code) && code.GetString() == NhAiScriptCodes.WaitingForApproval);
        Assert.Equal(
            ["done", "waiting-for-approval"],
            stopped.GetProperty("data").GetProperty("calls").EnumerateArray().Select(call => call.GetProperty("outcome").GetString()!).ToArray());
    }
}

/// <summary>
/// The assistant sample with scripts switched on through the default switch of the assistant.
/// </summary>
public sealed class AssistantScriptSampleHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
{
    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        base.ConfigureSettings(settings);
        settings["NewHeap:AI:Assistant:Scripts"] = "true";
    }
}
