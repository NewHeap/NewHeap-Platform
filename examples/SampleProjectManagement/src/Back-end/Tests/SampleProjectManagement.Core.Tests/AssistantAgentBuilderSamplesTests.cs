using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Identity.Claims;
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
/// never reaches the builder. The scripted model answers people's chats only, so no run takes its steps. A project
/// manager builds with it too, and only an administrator changes whose rights an agent uses.
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

    /// <summary>
    /// A project manager passes the administration policy and builds with the builder, but only an administrator
    /// changes whose rights an agent uses: in the screens, and in the builder before any card is shown.
    /// </summary>
    [Fact]
    public async Task SPM_394_a_project_manager_builds_agents_and_only_an_administrator_changes_whose_rights_they_use()
    {
        const string Agent = "spm-394-rights";
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_read_catalog", new { input = new { query = "project" } })
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Agent, accountability = "subject-responsible", expectedVersion = 1 } })
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Agent, maxConcurrentRuns = 2, expectedVersion = 1 } })
            .RespondWithText("Only the concurrency changed; an administrator changes whose rights it uses.");
        host.Model.UseForChats(model);

        try
        {
            await AgentWithPlainRunSettingsAsync(Agent);
            using var manager = host.CreateClient("spm-394-manager", admin: true);
            using var administrator = host.CreateClient("spm-394-administrator", admin: true);
            administrator.DefaultRequestHeaders.Add(SampleTestAuthenticationHandler.RolesHeader, SampleAgentBuilderComposition.AgentRightsRole);

            // The administration says so beforehand; the screens refuse it with what it was about.
            Assert.False(await MayChangeRightsAsync(manager));
            Assert.True(await MayChangeRightsAsync(administrator));
            var runSettings = $"/api/assistant/admin/agents/{Agent}/run-settings";
            var body = AssistantSampleHost.Json(new { accountability = "subject-responsible", expectedVersion = 1 });
            using var refused = await manager.PutAsync(runSettings, body, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var failure = await AssistantSampleHost.ReadJsonAsync(refused);
            Assert.Equal(NhAssistantErrorCodes.AgentRightsForbidden, failure.GetProperty("code").GetString());
            Assert.Equal(NhAssistantFieldErrors.RightsRequired, failure.GetProperty("errors").GetProperty("accountability")[0].GetString());

            // The builder tells the model, refuses the change before any card, and makes the other one.
            var chat = await CreateConversationAsync(manager);
            var asked = await host.SendAsync(manager, chat, "Let the responsible person's rights count and run two at a time.");
            Assert.False(Result(model, 0).GetProperty("data").GetProperty("mayChangeAgentRights").GetBoolean());
            Assert.Contains(NhAssistantErrorCodes.AgentRightsForbidden, Result(model, 1).GetRawText(), StringComparison.Ordinal);
            await host.DecideAsync(manager, chat, Assert.Single(asked, evt => evt.Name == "approval.required").Data, "approve");
            var stored = await RunSettingsAsync(Agent);
            Assert.Equal((NhAssistantAccountabilities.Agent, 2), (stored.Accountability, stored.MaxConcurrentRuns));

            // The administrator makes the change in the screen.
            using var changed = await administrator.PutAsync(
                runSettings,
                AssistantSampleHost.Json(new { accountability = "subject-responsible", maxConcurrentRuns = 2, expectedVersion = 2 }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Equal(NhAssistantAccountabilities.SubjectResponsible, (await RunSettingsAsync(Agent)).Accountability);
            Assert.Equal(0, model.RemainingSteps);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    private async Task AgentWithPlainRunSettingsAsync(string agentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
        // Application code, with an actor the platform knows as no person, decides for itself.
        var created = await configuration.CreateAgentAsync(
            new NhAssistantAgentRequest(agentId, "Rights example", "Shows who changes an agent's rights.", "Answer briefly.", ["project.*"], NhAiAutonomyLevel.Propose),
            "sample-setup");
        Assert.True(created.Success, string.Join(", ", created.AllErrorMessages));
        var saved = await configuration.SaveRunSettingsAsync(agentId, new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent), "sample-setup");
        Assert.True(saved.Success, string.Join(", ", saved.AllErrorMessages));
    }

    private async Task<NhAssistantAgentRunSettingsDto> RunSettingsAsync(string agentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().GetAsync(agentId))!.RunSettings!;
    }

    private static async Task<bool> MayChangeRightsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/assistant/admin/run-catalog", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await AssistantSampleHost.ReadJsonAsync(response)).GetProperty("mayChangeAgentRights").GetBoolean();
    }

    private static JsonElement Result(NhAiScriptedChatClient model, int index)
    {
        var result = model.FunctionResults[index].Result;
        return result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
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

/// <summary>
/// SPM-394 with <c>WithoutRightsChanges</c> (FB97): the builder stays on for what it is good at while the settings
/// of whose rights an agent uses stay in the screens, also for an administrator, and it says where to change them.
/// </summary>
public sealed class AssistantAgentBuilderCeilingSamplesTests(AssistantAgentBuilderCeilingSampleHost host) : IClassFixture<AssistantAgentBuilderCeilingSampleHost>
{
    private const string Agent = "spm-394-ceiling";

    [Fact]
    public async Task SPM_394_the_builder_leaves_whose_rights_an_agent_uses_to_the_screens()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_read_catalog", new { input = new { query = "project" } })
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Agent, accountability = "subject-responsible", expectedVersion = 1 } })
            .RespondWithText("You change whose rights it uses on the agent's page.");
        host.Model.UseForChats(model);

        try
        {
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
                var created = await configuration.CreateAgentAsync(
                    new NhAssistantAgentRequest(Agent, "Ceiling example", "Shows the builder's ceiling.", "Answer briefly.", ["project.*"], NhAiAutonomyLevel.Propose),
                    "sample-setup");
                Assert.True(created.Success, string.Join(", ", created.AllErrorMessages));
                Assert.True((await configuration.SaveRunSettingsAsync(Agent, new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent), "sample-setup")).Success);
            }

            using var administrator = host.CreateClient("spm-394-ceiling-administrator", admin: true);
            administrator.DefaultRequestHeaders.Add(SampleTestAuthenticationHandler.RolesHeader, SampleAgentBuilderComposition.AgentRightsRole);
            using var conversation = await administrator.PostAsync(
                "/api/assistant/conversations",
                AssistantSampleHost.Json(new { agentId = SampleAgentBuilderComposition.AgentId }),
                TestContext.Current.CancellationToken);
            var chat = (await AssistantSampleHost.ReadJsonAsync(conversation)).GetProperty("id").GetGuid();

            // The catalog shows the ceiling, the change gets no card and the answer can name the screen.
            var asked = await host.SendAsync(administrator, chat, "Let the responsible person's rights count.");
            Assert.DoesNotContain(asked, evt => evt.Name == "approval.required");
            var catalog = JsonSerializer.SerializeToElement(model.FunctionResults[0].Result).GetProperty("data");
            Assert.False(catalog.GetProperty("mayChangeAgentRights").GetBoolean());
            Assert.True(catalog.GetProperty("agentRightsInScreens").GetBoolean());
            Assert.Contains("screens", JsonSerializer.SerializeToElement(model.FunctionResults[1].Result).GetRawText(), StringComparison.Ordinal);

            // The same administrator changes it in the screen.
            using var changed = await administrator.PutAsync(
                $"/api/assistant/admin/agents/{Agent}/run-settings",
                AssistantSampleHost.Json(new { accountability = "subject-responsible", expectedVersion = 1 }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Equal(0, model.RemainingSteps);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }
}

/// <summary>
/// SPM-394: more tools on an agent that answers to a technical account put that account's rights to more use, so an
/// administrator who may not use a tool that the account may use does not add it, and one who may does.
/// </summary>
public sealed class AssistantAgentMoreToolsSamplesTests(AssistantAgentMoreToolsSampleHost host) : IClassFixture<AssistantAgentMoreToolsSampleHost>
{
    private const string Agent = "spm-394-more-tools";

    [Fact]
    public async Task SPM_394_only_an_administrator_who_holds_the_rights_gives_an_agent_with_a_technical_account_more_tools()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var account = await host.CreatePersonAsync(AssistantAgentMoreToolsSampleHost.CompleteTasksPermission);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
            // Application code, with an actor the platform knows as no person, decides for itself.
            var created = await configuration.CreateAgentAsync(
                new NhAssistantAgentRequest(Agent, "More tools", "Shows who gives an agent more tools.", "Answer briefly.", [ProjectAiReviewTools.DraftIntakeNoteToolId], NhAiAutonomyLevel.Propose),
                "sample-setup");
            Assert.True(created.Success, string.Join(", ", created.AllErrorMessages));
            var saved = await configuration.SaveRunSettingsAsync(
                Agent,
                new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.ServiceAccount, account.ToString("D")),
                "sample-setup");
            Assert.True(saved.Success, string.Join(", ", saved.AllErrorMessages));
        }

        var path = $"/api/assistant/admin/agents/{Agent}";
        StringContent Change()
        {
            return AssistantSampleHost.Json(new
            {
                id = Agent,
                displayName = "More tools",
                description = "Shows who gives an agent more tools.",
                instructions = "Answer briefly.",
                toolSelectors = new[] { ProjectAiReviewTools.DraftIntakeNoteToolId, ProjectTaskAiTools.CompleteToolId },
                autonomy = "propose",
                expectedVersion = 1
            });
        }

        // Both may change an agent's rights; only one may complete tasks, as the account may.
        using var limited = host.CreateClient("spm-394-limited", admin: true);
        limited.DefaultRequestHeaders.Add(SampleTestAuthenticationHandler.RolesHeader, SampleAgentBuilderComposition.AgentRightsRole);
        using var refused = await limited.PutAsync(path, Change(), cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.ServiceAccountNotPermitted, (await AssistantSampleHost.ReadJsonAsync(refused)).GetProperty("code").GetString());

        using var holder = host.CreateClient("spm-394-holder", true, AssistantAgentMoreToolsSampleHost.CompleteTasksPermission);
        holder.DefaultRequestHeaders.Add(SampleTestAuthenticationHandler.RolesHeader, SampleAgentBuilderComposition.AgentRightsRole);
        using var accepted = await holder.PutAsync(path, Change(), cancellation);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }
}

/// <summary>
/// The host of <see cref="AssistantAgentMoreToolsSamplesTests"/>: the document check's host with the agent builder,
/// where completing tasks takes a permission of its own instead of being open to every signed-in person.
/// </summary>
public sealed class AssistantAgentMoreToolsSampleHost(SamplePostgreSqlServer postgreSql) : AssistantRuleScriptSampleHost(postgreSql)
{
    public const string CompleteTasksPermission = "app.project.complete-tasks";

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSampleAgentBuilder();
    }

    protected override void ConfigureAuthorization(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        base.ConfigureAuthorization(options);
        // Accounts of the NewHeap identity carry it as NhPlatformClaimTypes.Permission, the test sign-in as "permission".
        options.AddPolicy(ProjectAiTools.ManagePolicy, policy => policy.RequireAssertion(context =>
            context.User.HasClaim(NhPlatformClaimTypes.Permission, CompleteTasksPermission)
            || context.User.HasClaim("permission", CompleteTasksPermission)));
    }
}

/// <summary>
/// The host of <see cref="AssistantAgentBuilderCeilingSamplesTests"/>: the document check's host with the agent
/// builder under its ceiling.
/// </summary>
public sealed class AssistantAgentBuilderCeilingSampleHost(SamplePostgreSqlServer postgreSql) : AssistantRuleScriptSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSampleAgentBuilder(rightsInScreensOnly: true);
    }
}
