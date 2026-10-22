using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The off state of agents and skills: it ignores triggers without queueing them, lets runs and
/// proposals finish, keeps people's own work possible and replays nothing when switched on, and
/// <c>StartsOff()</c> sets it only the first time the application sees an agent or skill.
/// </summary>
public sealed class AssistantAgentOffStateTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ProjectChangedTrigger = "project-changed";
    private const string DataTrigger = "data-project-changed";
    private const string WeeklyTrigger = "weekly-review";
    private const string WebhookTrigger = "planning-webhook";
    private const string Review = "review";
    private const string Notes = "notes";
    private const string Admin = "admin-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_and_a_skill_that_start_off_keep_their_stored_state_after_a_restart_and_a_deploy(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new TriggerProbe(), startsOff: true);

        var first = await ControlAsync(host, control => control.GetAsync(AgentId));

        Assert.Equal(NhAssistantAgentRunStates.Off, first!.RunState);
        Assert.False(first.Active);
        Assert.Null(first.ChangedAt);
        Assert.Equal(NhAssistantAgentRunStates.Off, first.Skills.Single(skill => skill.SkillId == Review).RunState);
        Assert.Equal(NhAssistantAgentRunStates.Active, first.Skills.Single(skill => skill.SkillId == Notes).RunState);

        var switchedOn = await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1", "Ready"));
        await ControlAsync(host, control => control.ResumeSkillAsync(AgentId, Review, "operator-1"));
        Assert.Equal(NhAssistantAgentRunStates.Active, switchedOn.Data!.RunState);
        Assert.Equal(0, switchedOn.Data.StartedRuns);

        // A new host start reads the stored state, and so does a deploy that changes the agent's code.
        await RestartAsync(host);
        await using (var context = Factory(host).CreateDbContext())
        {
            await context.Agents.Where(agent => agent.Id == AgentId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(agent => agent.CodeHash, "an-earlier-deploy"));
        }
        await RestartAsync(host);

        var later = await ControlAsync(host, control => control.GetAsync(AgentId));
        Assert.Equal(NhAssistantAgentRunStates.Active, later!.RunState);
        Assert.Equal("Ready", later.Reason);
        Assert.All(later.Skills, skill => Assert.Equal(NhAssistantAgentRunStates.Active, skill.RunState));

        // An agent and a data skill created in the administration start off when asked to.
        await using var scope = host.Services.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
        var created = await configuration.CreateAgentAsync(
            new NhAssistantAgentRequest(
                "project-watcher",
                "Project watcher",
                "Watches projects.",
                "Watch the project.",
                ["projects.*"],
                NhAiAutonomyLevel.Propose,
                StartsOff: true),
            Admin);
        var skill = await configuration.SaveSkillAsync(
            AgentId,
            "data-review",
            new NhAssistantDataSkillRequest("Reviews the project by rule.", RuleHandler: "project-follow-up", StartsOff: true),
            Admin);
        var changed = await configuration.SaveSkillAsync(
            AgentId,
            "data-review",
            new NhAssistantDataSkillRequest("Reviews the project.", RuleHandler: "project-follow-up", ExpectedVersion: 1, StartsOff: true),
            Admin);

        Assert.True(created.Success);
        Assert.True(skill.Success);
        var watcher = await ControlAsync(host, control => control.GetAsync("project-watcher"));
        Assert.Equal((NhAssistantAgentRunStates.Off, Admin), (watcher!.RunState, watcher.ChangedBy));
        var agent = await ControlAsync(host, control => control.GetAsync(AgentId));
        Assert.Equal(NhAssistantAgentRunStates.Off, agent!.Skills.Single(item => item.SkillId == "data-review").RunState);
        Assert.Contains(changed.GetResultItems(), item => item.Name == "field:startsOff");
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSwitchedOff
            && evt.SkillId == "data-review"
            && evt.ResultCode == NhAssistantAgentRunStates.Off);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_that_is_off_ignores_events_schedules_data_triggers_and_webhooks_and_switching_on_replays_nothing(AssistantTestProvider provider)
    {
        var probe = new TriggerProbe();
        await using var host = await CreateHostAsync(provider, probe, startsOff: true);
        var operations = host.Services.GetRequiredService<RecordingOperations>();
        Assert.True((await SaveDataTriggerAsync(host)).Success);
        var auditsBefore = host.Business.Events.Count;

        var published = await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "task added"));
        var ticked = await TickAsync(host);
        var webhook = await RefuseWebhookAsync(host, """{ "kind": "planning", "projectId": "p-1" }""");

        Assert.Equal(new[] { DataTrigger, ProjectChangedTrigger }, published.Select(receipt => receipt.TriggerId).Order().ToArray());
        Assert.All(published.Append(Assert.Single(ticked)).Append(webhook!), receipt =>
        {
            Assert.Equal(NhAssistantTriggerOutcomes.Ignored, receipt.Outcome);
            Assert.Equal(NhAssistantTriggerReasons.AgentOff, receipt.ReasonCode);
            Assert.Null(receipt.ConversationId);
        });
        // No lambda ran, nothing waits, nothing started and nothing was audited.
        Assert.Equal((0, 0, 0), (probe.Filters, probe.Subjects, probe.ScheduledQueries));
        Assert.Equal(0, await WaitingAsync(host));
        Assert.Empty(operations.Enqueued);
        Assert.Equal(auditsBefore, host.Business.Events.Count);

        // Switching on starts no run for the events of the period the agent was off.
        var switchedOn = await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));
        Assert.Equal(0, switchedOn.Data!.StartedRuns);
        Assert.Empty(operations.Enqueued);

        // Events that waited during a pause end when the agent is switched off.
        await ControlAsync(host, control => control.PauseAsync(AgentId, "operator-1"));
        var waiting = await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "task added"));
        Assert.All(waiting, receipt => Assert.Equal(NhAssistantTriggerOutcomes.Waiting, receipt.Outcome));
        await ControlAsync(host, control => control.SwitchOffAsync(AgentId, "operator-1", "Not yet"));
        Assert.Equal(0, await WaitingAsync(host));
        var again = await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));
        Assert.Equal(0, again.Data!.StartedRuns);
        Assert.Empty(operations.Enqueued);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSwitchedOff && evt.ObjectId == AgentId);

        var started = await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "task added"));
        Assert.All(started, receipt => Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Releasing_the_kill_switch_leaves_an_agent_and_skills_that_were_off_off_and_resumes_a_paused_or_stopped_agent(AssistantTestProvider provider)
    {
        var probe = new TriggerProbe();
        await using var host = await CreateHostAsync(provider, probe, startsOff: true);
        var operations = host.Services.GetRequiredService<RecordingOperations>();

        // The agent and its review skill start off; engaging the kill switch stops the agent.
        var engaged = await ControlAsync(host, control => control.SetKillSwitchAsync(AgentId, true, "operator-1"));
        var released = await ControlAsync(host, control => control.SetKillSwitchAsync(AgentId, false, "operator-1"));
        var ignored = await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "task added"), ProjectChangedTrigger);
        // Releasing a kill switch that was never engaged leaves an agent that is off off as well.
        var releasedAgain = await ControlAsync(host, control => control.SetKillSwitchAsync(AgentId, false, "operator-1"));

        Assert.Equal(NhAssistantAgentRunStates.Stopped, engaged.Data!.RunState);
        Assert.Equal(NhAssistantAgentRunStates.Off, released.Data!.RunState);
        Assert.False(released.Data.Active);
        Assert.Equal(NhAssistantAgentRunStates.Off, released.Data.Skills.Single(skill => skill.SkillId == Review).RunState);
        Assert.Equal(NhAssistantAgentRunStates.Active, released.Data.Skills.Single(skill => skill.SkillId == Notes).RunState);
        Assert.Equal(NhAssistantTriggerReasons.AgentOff, Assert.Single(ignored).ReasonCode);
        Assert.Equal(NhAssistantAgentRunStates.Off, releasedAgain.Data!.RunState);
        Assert.Empty(operations.Enqueued);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSwitchedOff
            && evt.ObjectId == AgentId
            && evt.ActorId == "operator-1");

        // An explicit resume still switches the agent on, and a later stop forgets that it was off.
        await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));
        await ControlAsync(host, control => control.StopAsync(AgentId, "operator-1"));
        var resumed = await ControlAsync(host, control => control.SetKillSwitchAsync(AgentId, false, "operator-1"));
        Assert.Equal(NhAssistantAgentRunStates.Active, resumed.Data!.RunState);

        // A paused agent behaves as before: the kill switch stops it and releasing it resumes it.
        await ControlAsync(host, control => control.PauseAsync(AgentId, "operator-1"));
        var pausedEngaged = await ControlAsync(host, control => control.SetKillSwitchAsync(AgentId, true, "operator-1"));
        var pausedReleased = await ControlAsync(host, control => control.SetKillSwitchAsync(AgentId, false, "operator-1"));
        Assert.Equal(NhAssistantAgentRunStates.Stopped, pausedEngaged.Data!.RunState);
        Assert.Equal(NhAssistantAgentRunStates.Active, pausedReleased.Data!.RunState);
        Assert.Equal(NhAssistantAgentRunStates.Off, pausedReleased.Data.Skills.Single(skill => skill.SkillId == Review).RunState);
        await using var context = Factory(host).CreateDbContext();
        Assert.False((await context.Agents.SingleAsync(agent => agent.Id == AgentId)).OffBeforeStop);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Switching_off_during_a_run_lets_it_finish_and_its_proposal_stays_decidable_and_executes(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new TriggerProbe());
        var operations = host.Services.GetRequiredService<RecordingOperations>();
        var rules = host.Services.GetRequiredService<TestRuleScript>();
        rules.Run = async (context, cancellationToken) =>
        {
            await context.ProposeAsync(
                "projects.change-status",
                new ProjectStatusInput(Guid.Parse(context.Subject.Subject.Id), "Closed"),
                null,
                cancellationToken);
            return NhAssistantRuleResult.Done("Proposed to close the project.");
        };
        var started = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "task done"), ProjectChangedTrigger));

        await ControlAsync(host, control => control.SwitchOffAsync(AgentId, "operator-1"));
        await ExecuteAsync(host, started.RunId!.Value);

        var run = await Runs(host).FindRunAsync(started.RunId.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Completed, run!.Status);
        var proposal = Assert.Single(await Runs(host).GetRunApprovalsAsync(run.Id, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, proposal.Status);

        // The proposal stays decidable and executes; the follow-up its policy asks for is ignored.
        Assert.True((await DecideAsync(host, proposal.Id)).Success);
        Assert.Contains(proposal.Id, operations.Executions);
        await ExecuteProposalAsync(host, proposal.Id);
        await using var context = Factory(host).CreateDbContext();
        Assert.Equal(NhAssistantApprovalStatuses.Executed, (await context.Approvals.SingleAsync(item => item.Id == proposal.Id)).Status);
        Assert.Equal(run.Id, Assert.Single(await context.Runs.Where(item => item.ConversationId == run.ConversationId).ToListAsync()).Id);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task People_chat_with_an_agent_that_is_off_and_start_it_by_hand(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("The project is on track.");
        await using var host = await CreateHostAsync(provider, new TriggerProbe(), startsOff: true, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        var turn = await host.SendAsync(conversation.Id, "How is the project doing?");
        NhAssistantTriggerReceipt manual;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            manual = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().StartAsync(
                AgentId,
                new NhAiSubjectRef("project", Guid.NewGuid().ToString("N")),
                "member-1",
                "Look at this project.");
        }
        await ExecuteAsync(host, manual.RunId!.Value);

        Assert.Equal(NhAssistantTurnStatuses.Completed, Assert.IsType<NhAssistantTurnCompletedEvent>(turn.Events[^1]).Status);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, manual.Outcome);
        Assert.Equal(NhAssistantRunStatuses.Completed, (await Runs(host).FindRunAsync(manual.RunId.Value, CancellationToken.None))!.Status);
        Assert.Equal(NhAssistantAgentRunStates.Off, (await ControlAsync(host, control => control.GetAsync(AgentId)))!.RunState);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        TriggerProbe probe,
        bool startsOff = false,
        NhAiScriptedChatClient? model = null)
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
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .StartFollowUpRun())
                .AddRuleHandler<TestSkillRules>("project-follow-up", "Follows up a project by rule.")
                .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                    .Description("A project changed.")
                    .When(change => probe.Filter(change.Change))
                    .Subject(change => probe.Subject(new NhAiSubjectRef("project", change.ProjectId.ToString("N"))))
                    .Message(change => change.Change)
                    .Dedupe(change => change.EventId.ToString("N")))
                .ConfigureAgent(AgentId, agent =>
                {
                    agent
                        .Grant("projects.*")
                        .UseApprovalPolicy("project-review")
                        .UseRuleHandler<TestRules>()
                        .Skill(Review, skill => skill
                            .Description("Reviews the project by rule.")
                            .UseRuleHandler<TestSkillRules>()
                            .StartsOff())
                        .Skill(Notes, skill => skill
                            .Description("Writes notes about the project.")
                            .UseRuleHandler<TestSkillRules>());
                    if (startsOff)
                    {
                        agent.StartsOff();
                    }
                })
                .AddTrigger<ProjectChanged>(ProjectChangedTrigger, trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => probe.Filter(evt.Change))
                    .Subject(evt => probe.Subject(new NhAiSubjectRef("project", evt.ProjectId.ToString("N"))))
                    .Message(evt => "The project changed: " + evt.Change)
                    .Dedupe(evt => evt.EventId.ToString("N"))
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddScheduledTrigger(WeeklyTrigger, schedule => schedule
                    .StartAgent(AgentId)
                    .Cron("0 7 * * 1")
                    .Subjects((_, cancellationToken) => probe.Scheduled(cancellationToken))
                    .Message(tick => "Weekly review of " + tick.Subject.Id + "."))
                .AddWebhookTrigger(WebhookTrigger, webhook => webhook
                    .StartAgent(AgentId)
                    .Secret(_ => new string('s', 40))
                    .When(body => body.GetProperty("kind").GetString() == "planning")
                    .Subject(body => probe.Subject(new NhAiSubjectRef("project", body.GetProperty("projectId").GetString()!)))
                    .Message(_ => "The planning changed.")));
    }

    /// <summary>
    /// What a new host does when it starts: it upserts the agents in code and records their skills.
    /// </summary>
    private static async Task RestartAsync(AssistantTestHost host)
    {
        await ActivatorUtilities.CreateInstance<NhAssistantSeeder>(host.Services).EnsureSeededAsync(CancellationToken.None);
    }

    private static async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantDataTriggerDto>> SaveDataTriggerAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().SaveTriggerAsync(
            AgentId,
            DataTrigger,
            new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, Event: "project.changed", Conflict: NhAssistantTriggerConflictNames.Queue),
            Admin);
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(
        AssistantTestHost host,
        ProjectChanged evt,
        string? triggerId = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
        return triggerId is null ? receipts : receipts.Where(receipt => receipt.TriggerId == triggerId).ToArray();
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> TickAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantScheduledTriggerJob>()
            .RunAsync(WeeklyTrigger, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static async Task<NhAssistantTriggerReceipt?> RefuseWebhookAsync(AssistantTestHost host, string body)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var trigger = scope.ServiceProvider.GetRequiredService<NhAssistantRegistrationState>().Triggers[WebhookTrigger];
        using var document = JsonDocument.Parse(body);
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>().RefuseEarlyAsync(
            trigger,
            new NhAssistantWebhookDelivery("delivery-1", document.RootElement.Clone()),
            null,
            CancellationToken.None);
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid runId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(1);
        await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
    }

    private static async Task<NewHeap.Platform.Common.Models.TaskResult<AssistantApproval>> DecideAsync(AssistantTestHost host, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approvalId,
            "approver-1",
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "approver-1")], "test")),
            approve: true,
            expectedProposalHash: null,
            reason: null,
            CancellationToken.None);
    }

    private static async Task ExecuteProposalAsync(AssistantTestHost host, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var result = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalExecutor>().ExecuteAsync(
            approvalId,
            Substitute.For<INhBackgroundOperationContext>(),
            fenced: false,
            CancellationToken.None);
        Assert.True(result.Success);
    }

    private static async Task<T> ControlAsync<T>(AssistantTestHost host, Func<INhAssistantAgentControl, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>());
    }

    /// <summary>
    /// The inputs that wait in any thread.
    /// </summary>
    private static async Task<int> WaitingAsync(AssistantTestHost host)
    {
        await using var context = Factory(host).CreateDbContext();
        return await context.QueuedInputs.CountAsync(input => input.ConsumedAt == null);
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private static NhAssistantDbContextFactory Factory(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantDbContextFactory>();
    }
}
