using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-396: an administrator asks the agent builder for an application-wide weekly count of the tasks that
/// changed last week, which warns the operations lead above ten. The builder reads the gateway's resources
/// with the administrator's rights, creates the count in the administration, lets it answer to a technical
/// account with gateway reads and lists the alert kind, on a card that says this changes whose rights the
/// agent uses; saves the rule script that counts through the gateway; dry-runs it on the whole application,
/// where it would raise the alert for eleven changed tasks; and asks to set it live. Every step waits for an
/// approval, and under the rule scripts' four eyes another administrator sets it live. The scripted model
/// answers people's chats only, so no run takes its steps.
/// </summary>
public sealed class AssistantWeeklyCountSamplesTests(AssistantWeeklyCountSampleHost host) : IClassFixture<AssistantWeeklyCountSampleHost>
{
    private const string Builder = SampleAgentBuilderComposition.AgentId;
    private const string AgentId = SampleWeeklyCountComposition.AgentId;

    [Fact]
    public async Task SPM_396_the_agent_builder_builds_a_weekly_count_through_the_gateway_after_approvals()
    {
        var lead = await host.CreatePersonAsync("app.project.view");
        var reporting = await host.CreatePersonAsync("app.project.view");
        var project = await host.CreateProjectAsync("WEEK", host.OwnerId, ProjectStatus.Active);
        for (var task = 1; task <= SampleWeeklyCountComposition.Threshold + 1; task++)
        {
            await host.CreateTaskAsync(project, "Changed task " + task);
        }

        var model = new NhAiScriptedChatClient()
            // What the application holds: the gateway's tasks, which can be filtered by date and counted.
            .RespondWithFunctionCall("builder_read_gateway", new { input = new { query = "task" } })
            .RespondWithFunctionCall("builder_read_gateway", new { input = new { resource = "project-task" } })
            .RespondWithFunctionCall("builder_create_agent", new
            {
                input = new
                {
                    agentId = AgentId,
                    displayName = "Weekly task changes",
                    description = "Counts the tasks that changed last week and warns the operations lead above ten.",
                    instructions = "Count the tasks that changed last week.",
                    toolSelectors = new[] { SampleAiBridgeComposition.GatewayToolSetId + ".*" },
                    worksOnApplication = true,
                    offeredInChat = false
                }
            })
            .RespondWithFunctionCall("builder_save_run_settings", new
            {
                input = new
                {
                    agentId = AgentId,
                    accountability = "service-account",
                    serviceAccountActorId = reporting.ToString("D"),
                    allowGatewayReads = true,
                    alertKinds = new[] { SampleWeeklyCountComposition.KindId },
                    reason = "The count covers the whole application, which has no responsible person."
                }
            })
            .RespondWithFunctionCall("builder_save_rule_script", new
            {
                input = new
                {
                    agentId = AgentId,
                    script = SampleWeeklyCountComposition.ScriptFor(lead.ToString("D")),
                    expectedRevision = 0,
                    reason = "Warn the operations lead when more than ten tasks changed in a week."
                }
            })
            .RespondWithFunctionCall("builder_dry_run_rule_script", new { input = new { agentId = AgentId, version = 1, subjects = new[] { new { type = NhAiSubjectRef.SystemType, id = "global" } } } })
            .RespondWithFunctionCall("builder_set_live", new { input = new { agentId = AgentId, part = "rule-script", version = 1, expectedRevision = 1 } })
            .RespondWithText("Under four eyes another administrator sets this version live.")
            // The reviewer's chat.
            .RespondWithFunctionCall("builder_set_live", new { input = new { agentId = AgentId, part = "rule-script", version = 1, expectedRevision = 1, reason = "The dry run counted eleven changes and would warn the lead." } })
            .RespondWithText("The weekly count runs the script from now on.");
        host.Model.UseForChats(model);

        try
        {
            // The administrator may read projects, so the gateway shows them its project resources, and has the
            // role that may change whose rights an agent uses.
            using var author = host.CreateClient("spm-396-author", true, "app.project.view");
            author.DefaultRequestHeaders.Add(SampleTestAuthenticationHandler.RolesHeader, SampleAgentBuilderComposition.AgentRightsRole);
            var chat = await CreateConversationAsync(author);

            // The gateway's resources are read at once, with the administrator's own rights.
            var asked = await host.SendAsync(author, chat, "Every Monday, count the tasks that changed last week and warn the operations lead when there are more than ten.");
            var described = Result(model, 1).GetProperty("data").GetProperty("resource");
            Assert.Equal("project-task", described.GetProperty("resource").GetString());
            Assert.Contains(described.GetProperty("query").GetProperty("filterFields").EnumerateArray(), field => field.GetProperty("key").GetString() == "lastModifiedDateTime");

            // Each step waits for its approval.
            var create = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
            Assert.Equal("assistant.agent-builder.create-agent", create.GetProperty("toolId").GetString());
            var settings = Assert.Single(await host.DecideAsync(author, chat, create, "approve"), evt => evt.Name == "approval.required").Data;
            Assert.Equal(NhAssistantAgentSources.Admin, (await AgentAsync()).Source);

            // Whose rights the count uses and the kind it lists change on a card that says so.
            var card = settings.GetProperty("presentation");
            // The notice names the agent whose rights change, and the rights field names the account.
            Assert.Contains(AgentId, card.GetProperty("notice").GetString(), StringComparison.Ordinal);
            Assert.Contains(card.GetProperty("fields").EnumerateArray(), field => field.GetProperty("value").GetString()!.Contains(reporting.ToString("D"), StringComparison.Ordinal));
            Assert.Contains(card.GetProperty("fields").EnumerateArray(), field => field.GetProperty("value").GetString() == SampleWeeklyCountComposition.KindId);
            var script = Assert.Single(await host.DecideAsync(author, chat, settings, "approve"), evt => evt.Name == "approval.required").Data;
            var runSettings = (await ConfigurationAsync()).RunSettings!;
            Assert.Equal(
                (NhAssistantAccountabilities.ServiceAccount, reporting.ToString("D"), true),
                (runSettings.Accountability, runSettings.ServiceAccountActorId, runSettings.AllowGatewayReads));
            Assert.Equal([SampleWeeklyCountComposition.KindId], runSettings.AlertKinds);

            // The rule script is saved after its approval and dry-run on the whole application: it counts
            // the eleven changed tasks through the gateway and would warn the lead, without effects.
            var live = Assert.Single(await host.DecideAsync(author, chat, script, "approve"), evt => evt.Name == "approval.required").Data;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var rules = scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>();
                var version = Assert.Single((await rules.GetAsync(AgentId, null))!.Versions);
                Assert.Equal((NhAssistantRuleScriptStatuses.Candidate, Builder), (version.Status, version.AuthorAgentId));
                var dryRun = (await rules.GetDryRunAsync(AgentId, null, 1))!;
                var onApplication = Assert.Single(dryRun.Subjects);
                Assert.Equal(SampleWeeklyCountComposition.KindId, Assert.Single(onApplication.Alerts).Kind);
            }

            // Four eyes: the author's own approval does not set it live; another administrator does.
            var refusedLive = await host.DecideAsync(author, chat, live, "approve");
            Assert.Equal(
                NhAssistantRuleScriptCodes.FourEyes,
                Assert.Single(refusedLive, evt => evt.Name == "tool.completed").Data.GetProperty("resultCode").GetString());
            using var reviewer = host.CreateClient("spm-396-reviewer", admin: true);
            var review = await CreateConversationAsync(reviewer);
            var reviewed = await host.SendAsync(reviewer, review, "Set the weekly count's rule script live.");
            await host.DecideAsync(reviewer, review, Assert.Single(reviewed, evt => evt.Name == "approval.required").Data, "approve");
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().GetAsync(AgentId, null))!.LiveVersion);
            }
            Assert.Equal(0, model.RemainingSteps);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    private async Task<NhAssistantAgentInEffectDto> AgentAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentDirectory>().GetAsync(AgentId))!;
    }

    private async Task<NhAssistantAgentConfigurationDto> ConfigurationAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().GetAsync(AgentId))!;
    }

    private static JsonElement Result(NhAiScriptedChatClient model, int index)
    {
        var result = model.FunctionResults[index].Result;
        return result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client)
    {
        using var response = await client.PostAsync(
            "/api/assistant/conversations",
            AssistantSampleHost.Json(new { agentId = Builder }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await AssistantSampleHost.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }
}

/// <summary>
/// SPM-396: the rule scripts' host of SPM-393, with its API gateway and delegated reads, and the agent builder
/// and the weekly count's alert kind of Program.cs.
/// </summary>
public sealed class AssistantWeeklyCountSampleHost(SamplePostgreSqlServer postgreSql) : AssistantRuleScriptSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSampleAgentBuilder();
        services.AddSampleWeeklyCount();
    }
}
