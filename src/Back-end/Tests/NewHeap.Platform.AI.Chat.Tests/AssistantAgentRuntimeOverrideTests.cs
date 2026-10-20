using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Runtime overrides of the code: the shadow mode of an agent and of one skill, and the technical
/// account that answers for an agent's runs.
/// </summary>
public sealed class AssistantAgentRuntimeOverrideTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Variant = "project-assistant-next";
    private const string Review = "review";
    private const string Notes = "notes";
    private const string Draft = "draft";
    private const string Admin = "admin-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_in_shadow_works_in_its_shadow_thread_while_the_live_thread_and_its_proposals_stay(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        ProposeHoldInSkill(host);
        var project = Guid.NewGuid();

        var live = Assert.Single(await PublishAsync(host, project, "task one"));
        await ExecuteAsync(host, live.RunId!.Value);
        var liveProposal = Assert.Single(await ApprovalsAsync(host, live.ConversationId!.Value));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, liveProposal.Status);

        var changed = await ControlAsync(host, control => control.SetShadowAsync(AgentId, true, Admin, "Evaluate the new rules"));
        Assert.True(changed.Success);
        Assert.True(changed.Data!.Shadow!.Shadow);
        Assert.True(changed.Data.Shadow!.Override);
        Assert.False(changed.Data.Shadow!.Configured);
        Assert.Equal(Admin, changed.Data.Shadow!.ChangedBy);
        Assert.Equal("Evaluate the new rules", changed.Data.Shadow!.Reason);

        // The next event of the same subject goes to the shadow thread; its proposal never executes.
        var shadow = Assert.Single(await PublishAsync(host, project, "task two"));
        Assert.NotEqual(live.ConversationId, shadow.ConversationId);
        await ExecuteAsync(host, shadow.RunId!.Value);
        var shadowThread = (await Runs(host).FindThreadByConversationAsync(shadow.ConversationId!.Value, CancellationToken.None))!;
        Assert.True(shadowThread.Shadow);
        Assert.Equal(project.ToString("N"), shadowThread.SubjectId);
        var shadowRun = (await Runs(host).FindRunAsync(shadow.RunId.Value, CancellationToken.None))!;
        Assert.True(shadowRun.Shadow);
        Assert.Equal(NhAssistantRunStatuses.Completed, shadowRun.Status);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, Assert.Single(await ApprovalsAsync(host, shadow.ConversationId.Value)).Status);
        Assert.True(Assert.Single(host.Services.GetRequiredService<TestSkillRuleScript>().Contexts, context => context.RunId == shadowRun.Id).Shadow);

        // The live proposal from before stays open and people still decide it.
        Assert.True((await DecideAsync(host, liveProposal.Id, approve: false)).Success);

        // Back to the code: the agent works live again in its live thread.
        var back = await ControlAsync(host, control => control.SetShadowAsync(AgentId, null, Admin));
        Assert.False(back.Data!.Shadow!.Shadow);
        Assert.Null(back.Data.Shadow!.Override);
        var again = Assert.Single(await PublishAsync(host, project, "task three"));
        Assert.Equal(live.ConversationId, again.ConversationId);

        var changes = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentShadowChanged).ToArray();
        Assert.Equal(["shadow", "code"], changes.Select(evt => evt.ResultCode));
        Assert.All(changes, evt => Assert.Equal(Admin, evt.ActorId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_skill_in_shadow_takes_its_trigger_and_the_routing_into_it_to_the_shadow_thread(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        ProposeHoldInSkill(host);
        host.Services.GetRequiredService<TestRuleScript>().Run = (context, _) => Task.FromResult(
            context.Inputs.Any(input => input.Text.Contains("notes", StringComparison.Ordinal))
                ? NhAssistantRuleResult.UseSkill(Notes, "A note is needed.")
                : NhAssistantRuleResult.UseSkill(Review, "The project needs a review."));
        var skill = await ControlAsync(host, control => control.SetSkillShadowAsync(AgentId, Review, true, Admin, "Try the new review"));
        Assert.True(skill.Data!.Shadow.Shadow);
        Assert.True(skill.Data.Shadow.Override);

        // A trigger that names the skill fires in the shadow thread.
        var triggered = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "task check"));
        var triggeredRun = (await Runs(host).FindRunAsync(triggered.RunId!.Value, CancellationToken.None))!;
        Assert.True(triggeredRun.Shadow);
        Assert.Equal(Review, triggeredRun.SkillId);
        Assert.Equal(NhAssistantSkillChoosers.Trigger, triggeredRun.SkillChosenBy);

        // Rules that route a live run to the skill hand its events to the shadow thread, where a run
        // in the skill takes them.
        var project = Guid.NewGuid();
        var routed = Assert.Single(await PublishAsync(host, project, "rule check"));
        await ExecuteAsync(host, routed.RunId!.Value);
        var liveRun = (await Runs(host).FindRunAsync(routed.RunId.Value, CancellationToken.None))!;
        Assert.Equal(NhAssistantRunStatuses.Cancelled, liveRun.Status);
        Assert.Equal(NhAssistantErrorCodes.MovedToShadow, liveRun.FailureCode);
        Assert.False(liveRun.Shadow);
        var shadowThread = (await ThreadAsync(host, project, shadow: true))!;
        var continued = await LatestRunAsync(host, shadowThread.ConversationId);
        Assert.True(continued.Shadow);
        Assert.Equal(Review, continued.SkillId);
        Assert.Equal(NhAssistantSkillChoosers.Rules, continued.SkillChosenBy);
        Assert.Equal(0, await Runs(host).CountWaitingInputsAsync(routed.ConversationId!.Value, CancellationToken.None));
        await ExecuteAsync(host, continued.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, Assert.Single(await ApprovalsAsync(host, shadowThread.ConversationId)).Status);
        var claimed = Assert.Single(host.Services.GetRequiredService<TestSkillRuleScript>().Contexts, context => context.RunId == continued.Id);
        Assert.Contains(claimed.Inputs, input => input.Text == "rule check");

        // The agent's other skills keep working live.
        var other = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "rule notes"));
        await ExecuteAsync(host, other.RunId!.Value);
        var notesRun = (await Runs(host).FindRunAsync(other.RunId.Value, CancellationToken.None))!;
        Assert.False(notesRun.Shadow);
        Assert.Equal(Notes, notesRun.SkillId);
        Assert.Equal(NhAssistantRunStatuses.Completed, notesRun.Status);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentShadowChanged
            && evt.SkillId == Review
            && evt.ResultCode == "shadow");
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Use_skill_in_a_live_thread_does_not_offer_a_skill_in_shadow(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient();
        model.RespondWithText("Nothing to switch.");
        await using var host = await CreateHostAsync(provider, model);
        host.Services.GetRequiredService<TestRuleScript>().Run = (_, _) => Task.FromResult(NhAssistantRuleResult.HandOver("Let the model judge."));
        await ControlAsync(host, control => control.SetSkillShadowAsync(AgentId, Review, true, Admin));

        var receipt = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "rule model"));
        await ExecuteAsync(host, receipt.RunId!.Value);

        var useSkill = Assert.Single(Assert.Single(model.Requests).Options!.Tools!.OfType<AIFunction>(), tool => tool.Name == "use_skill");
        var choices = useSkill.JsonSchema.GetProperty("properties").GetProperty("skill").GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains(Notes, choices);
        Assert.DoesNotContain(Review, choices);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Members_see_the_live_thread_of_an_agent_in_shadow_and_only_administrators_its_shadow_thread(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        ProposeHoldInSkill(host);
        var project = Guid.NewGuid();
        var live = Assert.Single(await PublishAsync(host, project, "task live"));
        await ExecuteAsync(host, live.RunId!.Value);
        await ControlAsync(host, control => control.SetShadowAsync(AgentId, true, Admin));
        var shadow = Assert.Single(await PublishAsync(host, project, "task shadow"));

        var member = (await WorkAsync(host, project, "member-1", administrator: false)).Data!;
        // An administrator who also approves on the subject.
        var administrator = (await WorkAsync(host, project, "approver-1", administrator: true)).Data!;

        var memberAgent = Assert.Single(member.Agents, agent => agent.AgentId == AgentId);
        Assert.Equal(live.ConversationId, memberAgent.Thread!.ConversationId);
        Assert.False(memberAgent.Thread.Shadow);
        Assert.Null(memberAgent.ShadowThread);
        // An agent in shadow is started by hand by administrators only.
        Assert.False(memberAgent.CanStart);
        Assert.DoesNotContain(member.Agents, agent => agent.AgentId == Variant);
        Assert.Contains(member.OpenProposals, proposal => proposal.Proposal.ConversationId == live.ConversationId);

        var adminAgent = Assert.Single(administrator.Agents, agent => agent.AgentId == AgentId);
        Assert.Equal(live.ConversationId, adminAgent.Thread!.ConversationId);
        Assert.Equal(shadow.ConversationId, adminAgent.ShadowThread!.ConversationId);
        Assert.True(adminAgent.ShadowThread.Shadow);
        Assert.Contains(administrator.Agents, agent => agent.AgentId == Variant && agent.Thread!.Shadow);

        // The conversation endpoints answer a member as if the shadow thread did not exist.
        var liveThread = (await Runs(host).FindThreadByConversationAsync(live.ConversationId!.Value, CancellationToken.None))!;
        var shadowThread = (await Runs(host).FindThreadByConversationAsync(shadow.ConversationId!.Value, CancellationToken.None))!;
        Assert.True(await HiddenAsync(host, shadowThread, administrator: false));
        Assert.False(await HiddenAsync(host, shadowThread, administrator: true));
        Assert.False(await HiddenAsync(host, liveThread, administrator: false));

        // The summary counts the queued shadow run for administrators only.
        var memberSummary = Assert.Single((await SummaryAsync(host, project, "member-1", administrator: false)).Data!.Items);
        var adminSummary = Assert.Single((await SummaryAsync(host, project, "approver-1", administrator: true)).Data!.Items);
        Assert.False(memberSummary.RunActive);
        Assert.True(adminSummary.RunActive);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_queued_before_the_agent_went_into_shadow_starts_in_the_shadow_thread(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        ProposeHoldInSkill(host);
        var project = Guid.NewGuid();

        var queued = Assert.Single(await PublishAsync(host, project, "task queued"));
        await ControlAsync(host, control => control.SetShadowAsync(AgentId, true, Admin));
        await ExecuteAsync(host, queued.RunId!.Value);

        var run = (await Runs(host).FindRunAsync(queued.RunId.Value, CancellationToken.None))!;
        Assert.True(run.Shadow);
        Assert.NotEqual(queued.ConversationId, run.ConversationId);
        Assert.Equal(NhAssistantRunStatuses.Completed, run.Status);
        Assert.True((await Runs(host).FindThreadByConversationAsync(run.ConversationId, CancellationToken.None))!.Shadow);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, Assert.Single(await ApprovalsAsync(host, run.ConversationId)).Status);
        Assert.Empty(await ApprovalsAsync(host, queued.ConversationId!.Value));
        Assert.Equal(0, await Runs(host).CountWaitingInputsAsync(queued.ConversationId.Value, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Events_that_waited_during_a_pause_move_to_the_shadow_thread_when_the_agent_resumes_in_shadow(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var project = Guid.NewGuid();
        await ControlAsync(host, control => control.PauseAsync(AgentId, Admin));

        var waiting = Assert.Single(await PublishAsync(host, project, "task waits"));
        Assert.Equal(NhAssistantTriggerOutcomes.Waiting, waiting.Outcome);
        await ControlAsync(host, control => control.SetShadowAsync(AgentId, true, Admin));
        var resumed = await ControlAsync(host, control => control.ResumeAsync(AgentId, Admin));

        Assert.Equal(1, resumed.Data!.StartedRuns);
        Assert.Equal(0, await Runs(host).CountWaitingInputsAsync(waiting.ConversationId!.Value, CancellationToken.None));
        var shadowThread = (await ThreadAsync(host, project, shadow: true))!;
        var run = await LatestRunAsync(host, shadowThread.ConversationId);
        Assert.True(run.Shadow);
        Assert.Equal(Review, run.SkillId);
        Assert.Equal(1, await Runs(host).CountWaitingInputsAsync(shadowThread.ConversationId, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_skill_in_shadow_in_code_goes_live_at_runtime_and_a_variant_stays_in_shadow(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var state = (await ControlAsync(host, control => control.GetAsync(AgentId)))!;
        var draft = Assert.Single(state.Skills, skill => skill.SkillId == Draft);
        Assert.True(draft.Shadow.Shadow);
        Assert.True(draft.Shadow.Configured);
        Assert.Null(draft.Shadow.Override);
        var variantState = (await ControlAsync(host, control => control.GetAsync(Variant)))!;
        Assert.True(variantState.Shadow!.Shadow);
        Assert.True(variantState.Shadow!.Locked);

        var inShadow = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "draft one"));
        Assert.True((await Runs(host).FindRunAsync(inShadow.RunId!.Value, CancellationToken.None))!.Shadow);

        var live = await ControlAsync(host, control => control.SetSkillShadowAsync(AgentId, Draft, false, Admin, "The drafts are good"));
        Assert.False(live.Data!.Shadow.Shadow);
        Assert.False(live.Data.Shadow.Override);
        var nowLive = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "draft two"));
        Assert.False((await Runs(host).FindRunAsync(nowLive.RunId!.Value, CancellationToken.None))!.Shadow);

        var refused = await ControlAsync(host, control => control.SetShadowAsync(Variant, false, Admin));
        var refusedSkill = await ControlAsync(host, control => control.SetSkillShadowAsync(Variant, Review, false, Admin));
        var unknownSkill = await ControlAsync(host, control => control.SetSkillShadowAsync(AgentId, "missing", true, Admin));
        var tooLong = await ControlAsync(host, control => control.SetShadowAsync(AgentId, true, Admin, new string('r', 501)));
        Assert.Equal(NhAssistantErrorCodes.ShadowVariant, Code(refused));
        Assert.Equal(NhAssistantErrorCodes.ShadowVariant, Code(refusedSkill));
        Assert.Equal(NhAssistantErrorCodes.SkillNotFound, Code(unknownSkill));
        Assert.False(tooLong.Success);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Threads_of_agents_in_shadow_by_code_become_shadow_threads_until_an_administrator_changes_the_mode(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        // The seeder writes the code agents before it marks threads; read once so they exist.
        Assert.NotNull(await ControlAsync(host, control => control.GetAsync(AgentId)));
        var factory = host.Services.GetRequiredService<NhAssistantDbContextFactory>();
        var variantThread = await AddLiveThreadAsync(factory, Variant);
        var codeThread = await AddLiveThreadAsync(factory, AgentId);
        var store = new NhAssistantAdminStore(factory);

        await store.MarkShadowThreadsAsync([Variant], [AgentId], CancellationToken.None);
        Assert.True((await Runs(host).FindThreadByConversationAsync(variantThread, CancellationToken.None))!.Shadow);
        Assert.True((await Runs(host).FindThreadByConversationAsync(codeThread, CancellationToken.None))!.Shadow);

        // Once an administrator chose a mode, threads made since stay as they are.
        await ControlAsync(host, control => control.SetShadowAsync(AgentId, false, Admin));
        var later = await AddLiveThreadAsync(factory, AgentId);
        await store.MarkShadowThreadsAsync([Variant], [AgentId], CancellationToken.None);
        Assert.False((await Runs(host).FindThreadByConversationAsync(later, CancellationToken.None))!.Shadow);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_administrator_changes_the_technical_account_for_new_runs_while_earlier_work_keeps_its_account(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        ProposeHoldInSkill(host);
        var project = Guid.NewGuid();
        var first = Assert.Single(await PublishAsync(host, project, "task first"));
        await ExecuteAsync(host, first.RunId!.Value);
        var firstRun = (await Runs(host).FindRunAsync(first.RunId.Value, CancellationToken.None))!;
        Assert.Equal(KnownAccountPrincipalFactory.First, firstRun.AccountableOwnerId);

        var unknown = await ControlAsync(host, control => control.SetServiceAccountAsync(AgentId, "nobody", Admin));
        var notUsed = await ControlAsync(host, control => control.SetServiceAccountAsync(Variant, KnownAccountPrincipalFactory.Second, Admin));
        var changed = await ControlAsync(host, control => control.SetServiceAccountAsync(AgentId, KnownAccountPrincipalFactory.Second, Admin, "Rotate the account"));

        Assert.Equal(NhAssistantErrorCodes.ServiceAccountNotFound, Code(unknown));
        Assert.Equal(NhAssistantErrorCodes.ServiceAccountNotUsed, Code(notUsed));
        Assert.True(changed.Success);
        var account = changed.Data!.ServiceAccount!;
        Assert.Equal(KnownAccountPrincipalFactory.Second, account.ActorId);
        Assert.Equal(KnownAccountPrincipalFactory.First, account.ConfiguredActorId);
        Assert.True(account.Overridden);
        Assert.Equal("Rotate the account", account.Reason);

        // The thread answers to the new account; the earlier run keeps its own.
        var thread = (await Runs(host).FindThreadByConversationAsync(first.ConversationId!.Value, CancellationToken.None))!;
        Assert.Equal(KnownAccountPrincipalFactory.Second, thread.AccountableOwnerId);
        Assert.Equal(KnownAccountPrincipalFactory.First, (await Runs(host).FindRunAsync(firstRun.Id, CancellationToken.None))!.AccountableOwnerId);
        var next = Assert.Single(await PublishAsync(host, project, "task next"));
        Assert.Equal(KnownAccountPrincipalFactory.Second, (await Runs(host).FindRunAsync(next.RunId!.Value, CancellationToken.None))!.AccountableOwnerId);

        var back = await ControlAsync(host, control => control.SetServiceAccountAsync(AgentId, null, Admin));
        Assert.Equal(KnownAccountPrincipalFactory.First, back.Data!.ServiceAccount!.ActorId);
        Assert.False(back.Data.ServiceAccount.Overridden);
        var events = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentServiceAccountChanged).ToArray();
        Assert.Equal(["set", "code"], events.Select(evt => evt.ResultCode));
        Assert.Equal([KnownAccountPrincipalFactory.Second, KnownAccountPrincipalFactory.First], events.Select(evt => evt.ObjectId));
    }

    private static string? Code<T>(TaskResult<T> result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The review skill's rules propose to put the project on hold.
    /// </summary>
    private static void ProposeHoldInSkill(AssistantTestHost host)
    {
        host.Services.GetRequiredService<TestSkillRuleScript>().Run = async (context, cancellationToken) =>
        {
            await context.ProposeAsync(
                "projects.change-status",
                new ProjectStatusInput(Guid.Parse(context.Subject.Subject.Id), "OnHold"),
                null,
                cancellationToken);
            return NhAssistantRuleResult.Done("Proposed to hold the project.");
        };
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient? model = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model ?? new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<TestSkillRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
                services.AddAuthorization(options => options.AddPolicy("app.assistant.admin", policy => policy.RequireClaim("role", "assistant-admin")));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddAgent(AssistantTestData.Agent(Variant))
                .ConfigureAgent(AgentId, agent => agent
                    .ServiceAccount(KnownAccountPrincipalFactory.First)
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>()
                    .Skill(Review, skill => skill
                        .Description("Reviews the project by rule.")
                        .UseRuleHandler<TestSkillRules>()
                        .Tools("projects.change-status"))
                    .Skill(Draft, skill => skill
                        .Description("Drafts plans by rule.")
                        .UseRuleHandler<TestSkillRules>()
                        .Shadow())
                    .Skill(Notes, skill => skill
                        .Description("Writes notes by rule.")
                        .UseRuleHandler<TestSkillRules>()))
                .ConfigureAgent(Variant, agent => agent
                    .ShadowOf(AgentId)
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .Skill(Review, skill => skill
                        .Description("Reviews the project by rule.")
                        .UseRuleHandler<TestSkillRules>()))
                .AddTrigger<ProjectChanged>("task-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => evt.Change.StartsWith("task", StringComparison.Ordinal))
                    .Skill(Review)
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("draft-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => evt.Change.StartsWith("draft", StringComparison.Ordinal))
                    .Skill(Draft)
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("rule-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => evt.Change.StartsWith("rule", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue)));
    }

    /// <summary>
    /// Publishes a change of the project and returns the live agent's receipt of the trigger it is
    /// for; the variant receives the same event.
    /// </summary>
    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, Guid projectId, string change)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, change));
        var triggerId = change.Split(' ')[0] + "-changed";
        return receipts.Where(receipt => receipt.TriggerId == triggerId).ToArray();
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid runId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(1);
        await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
    }

    private static async Task<TaskResult<AssistantApproval>> DecideAsync(AssistantTestHost host, Guid approvalId, bool approve)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approvalId,
            "approver-1",
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "approver-1")], "test")),
            approve,
            expectedProposalHash: null,
            reason: null,
            CancellationToken.None);
    }

    private static async Task<TaskResult<NhAssistantSubjectWorkDto>> WorkAsync(AssistantTestHost host, Guid projectId, string actorId, bool administrator)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantSubjectWork>()
            .GetAsync(new NhAiSubjectRef("project", projectId.ToString("N")), actorId, Principal(actorId, administrator));
    }

    private static async Task<TaskResult<NhAssistantSubjectSummaryListDto>> SummaryAsync(AssistantTestHost host, Guid projectId, string actorId, bool administrator)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantSubjectWork>()
            .SummarizeAsync("project", [projectId.ToString("N")], actorId, Principal(actorId, administrator));
    }

    private static async Task<bool> HiddenAsync(AssistantTestHost host, AssistantThread thread, bool administrator)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = Principal("member-1", administrator)
        };
        return await NhAssistantEndpointRouteBuilderExtensions.IsHiddenShadowThreadAsync(httpContext, thread);
    }

    private static ClaimsPrincipal Principal(string actorId, bool administrator)
    {
        Claim[] claims = administrator
            ? [new Claim(ClaimTypes.NameIdentifier, actorId), new Claim("role", "assistant-admin")]
            : [new Claim(ClaimTypes.NameIdentifier, actorId)];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<IReadOnlyList<AssistantApproval>> ApprovalsAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Approvals.AsNoTracking().Where(approval => approval.ConversationId == conversationId).ToListAsync();
    }

    private static async Task<AssistantThread?> ThreadAsync(AssistantTestHost host, Guid projectId, bool shadow)
    {
        return await Runs(host).FindThreadAsync(
            NhAiSubjectInfo.CreateScopeKey(null, null),
            AgentId,
            new NhAiSubjectRef("project", projectId.ToString("N")),
            CancellationToken.None,
            shadow);
    }

    private static async Task<AssistantRun> LatestRunAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs.Where(run => run.ConversationId == conversationId).OrderByDescending(run => run.CreatedAt).FirstAsync();
    }

    /// <summary>
    /// Writes a live thread of the agent, as one written before shadow threads existed.
    /// </summary>
    private static async Task<Guid> AddLiveThreadAsync(NhAssistantDbContextFactory factory, string agentId)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            Kind = NhAssistantConversationKinds.Thread,
            OwnerActorId = "agent:" + agentId,
            AgentId = agentId,
            AgentVersion = 1,
            Title = "Project",
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
        var thread = new AssistantThread
        {
            ConversationId = conversation.Id,
            ScopeKey = NhAiSubjectInfo.CreateScopeKey(null, null),
            AgentId = agentId,
            SubjectType = "project",
            SubjectId = Guid.NewGuid().ToString("N"),
            Accountability = NhAssistantAccountabilities.Agent,
            AccountableOwnerId = "agent:" + agentId,
            MemoryConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        await new NhAssistantRunStore(factory).AddThreadAsync(conversation, thread, CancellationToken.None);
        return conversation.Id;
    }

    private static async Task<T> ControlAsync<T>(AssistantTestHost host, Func<INhAssistantAgentControl, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>());
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }
}

/// <summary>
/// A principal factory that knows two technical accounts and nobody else.
/// </summary>
public sealed class KnownAccountPrincipalFactory : INhAiPrincipalFactory
{
    public const string First = "service-1";
    public const string Second = "service-2";

    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<ClaimsPrincipal?>(actorId is First or Second or "approver-1" or "member-1"
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"))
            : null);
    }
}
