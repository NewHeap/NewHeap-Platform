using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-394: an administrator asks the agent builder "warn me when a project has no document three days
/// before its deadline". The builder reads the building blocks and what the document check's rule script may
/// call, and proposes to save the script with the review request opted in; nothing is saved until the
/// administrator approves it in the chat. It dry-runs the new version on a project due within three days,
/// which changes nothing, and proposes to set it live. Under the document check's four eyes the
/// administrator who saved it through the builder cannot set it live, also not with an approval; another
/// administrator does, in their own chat with the builder. A person without the administration policy
/// never reaches the builder. The scripted model answers people's chats only, so no run takes its steps.
/// </summary>
public sealed class AssistantAgentBuilderSamplesTests(AssistantAgentBuilderSampleHost host) : IClassFixture<AssistantAgentBuilderSampleHost>
{
    private const string Builder = SampleAgentBuilderComposition.AgentId;
    private const string AgentId = SampleRuleScriptsComposition.AgentId;
    private const string ReviewTool = ProjectAiReviewTools.RequestReviewToolId;

    [Fact]
    public async Task SPM_394_the_agent_builder_turns_a_wish_into_a_rule_script_after_approvals_and_four_eyes_hold_for_it()
    {
        var project = await host.CreateProjectAsync("BLD", host.OwnerId, ProjectStatus.Active, host.Clock.GetUtcNow().AddDays(2));
        var projectId = ProjectSubjects.IdOf(project);
        var model = new NhAiScriptedChatClient()
            // The author's chat: read what exists and what a script may call, then save it as a candidate.
            .RespondWithFunctionCall("builder_read_catalog", new { input = new { query = "project" } })
            .RespondWithFunctionCall("builder_read_rule_script", new { input = new { agentId = AgentId } })
            .RespondWithFunctionCall("builder_save_rule_script", new
            {
                input = new
                {
                    agentId = AgentId,
                    script = SampleRuleScriptsComposition.DocumentCheckScript,
                    writes = new[] { ReviewTool },
                    expectedRevision = 0,
                    reason = "Warn about projects without a document three days before their deadline."
                }
            })
            // Approved: dry-run it on the project due soon, then propose to set it live.
            .RespondWithFunctionCall("builder_dry_run_rule_script", new { input = new { agentId = AgentId, version = 1, subjects = new[] { new { type = ProjectSubjects.Type, id = projectId } } } })
            .RespondWithFunctionCall("builder_set_live", new { input = new { agentId = AgentId, part = "rule-script", version = 1, expectedRevision = 1 } })
            .RespondWithText("Under four eyes another administrator sets this version live.")
            // The reviewer's chat: set the candidate live.
            .RespondWithFunctionCall("builder_set_live", new { input = new { agentId = AgentId, part = "rule-script", version = 1, expectedRevision = 1, reason = "The dry run warned about the right project." } })
            .RespondWithText("The document check runs the new script from its next round.");
        host.Model.UseForChats(model);

        try
        {
            // A person without the administration policy does not reach the builder.
            using var participant = host.CreateClient(host.ParticipantId.ToString());
            using var refused = await participant.PostAsync("/api/assistant/conversations", AssistantSampleHost.Json(new { agentId = Builder }), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            using var author = host.CreateClient("spm-394-author", admin: true);
            var chat = await CreateConversationAsync(author);

            // The save waits for the author's approval; its card names the agent and the write.
            var asked = await host.SendAsync(author, chat, "Warn me when a project has no document three days before its deadline.");
            var save = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
            Assert.Equal("assistant.agent-builder.save-rule-script", save.GetProperty("toolId").GetString());
            Assert.Contains(save.GetProperty("presentation").GetProperty("fields").EnumerateArray(), field => field.GetProperty("value").GetString() == ReviewTool);
            Assert.Empty((await RuleScriptAsync()).Versions);

            // The builder reads the sample's own guidance after the platform's rules and before the
            // application context, as background that does not change its rules.
            var instructions = model.Requests[0].Options!.Instructions!;
            var guidance = instructions.IndexOf("Projects and their tasks are read through the API gateway", StringComparison.Ordinal);
            Assert.True(
                instructions.IndexOf("# Assistant rules", StringComparison.Ordinal) < guidance
                && guidance < instructions.IndexOf("# Agent instructions", StringComparison.Ordinal),
                "The guidance is not between the rules and the agent's instructions.");

            // Approved: saved as a candidate by the author with the builder acting for them, dry-run without
            // effects, and the step live waits for an approval of its own.
            var approved = await host.DecideAsync(author, chat, save, "approve");
            var version = Assert.Single((await RuleScriptAsync()).Versions);
            Assert.Equal((NhAssistantRuleScriptStatuses.Candidate, "spm-394-author", Builder), (version.Status, version.AuthorActorId, version.AuthorAgentId));
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var dryRun = (await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().GetDryRunAsync(AgentId, null, 1))!;
                var onProject = Assert.Single(dryRun.Subjects);
                Assert.Equal(SampleRuleScriptsComposition.KindId, Assert.Single(onProject.Alerts).Kind);
                Assert.Equal((ReviewTool, NhAssistantRuleScriptWouldOutcomes.Propose), (onProject.Writes.Single().ToolId, onProject.Writes.Single().Outcome));
            }
            Assert.Equal(0, host.Reviews.Attempts(project));

            // Four eyes: the author's own approval does not set the builder's version live.
            var ownLive = Assert.Single(approved, evt => evt.Name == "approval.required").Data;
            var refusedLive = await host.DecideAsync(author, chat, ownLive, "approve");
            Assert.Equal(
                NhAssistantRuleScriptCodes.FourEyes,
                Assert.Single(refusedLive, evt => evt.Name == "tool.completed").Data.GetProperty("resultCode").GetString());
            Assert.Null((await RuleScriptAsync()).LiveVersion);

            // Another administrator sets it live in their own chat with the builder, after their approval.
            using var reviewer = host.CreateClient("spm-394-reviewer", admin: true);
            var review = await CreateConversationAsync(reviewer);
            var reviewed = await host.SendAsync(reviewer, review, "Set the document check's new rule script live.");
            await host.DecideAsync(reviewer, review, Assert.Single(reviewed, evt => evt.Name == "approval.required").Data, "approve");
            var live = await RuleScriptAsync();
            Assert.Equal(1, live.LiveVersion);
            Assert.Equal("spm-394-reviewer", live.Versions.Single().LiveBy);
            Assert.Equal(0, model.RemainingSteps);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    private async Task<NhAssistantRuleScriptDto> RuleScriptAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().GetAsync(AgentId, null))!;
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
/// SPM-394: the document check's host of SPM-393 with the agent builder of Program.cs.
/// </summary>
public sealed class AssistantAgentBuilderSampleHost(SamplePostgreSqlServer postgreSql) : AssistantRuleScriptSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSampleAgentBuilder();
    }
}
