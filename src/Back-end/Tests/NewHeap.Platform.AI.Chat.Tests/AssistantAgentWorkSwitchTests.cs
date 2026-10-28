using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB17: <see cref="NhAssistantOptions.AgentWork"/> switches agent work off on a host while the
/// assistant stays enabled. Every kind of agent work is refused with
/// <see cref="NhAssistantErrorCodes.AgentWorkOff"/> before it costs anything, approved proposals are held
/// and released by the maintenance job once agents work again, and people keep chatting and deciding.
/// The switch is flipped at runtime, as a reloaded configuration does.
/// </summary>
public sealed class AssistantAgentWorkSwitchTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Closer = "project-closer";
    private const string ProjectChangedTrigger = "project-changed";
    private const string WeeklyTrigger = "weekly-review";
    private const string WebhookTrigger = "planning-webhook";
    private const string Notes = "notes";
    private const string Operator = "operator-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Triggers_schedules_webhooks_starts_and_follow_ups_are_refused_before_any_lambda_and_a_queued_run_ends(AssistantTestProvider provider)
    {
        var probe = new TriggerProbe();
        await using var host = await CreateHostAsync(provider, probe);
        var work = host.Services.GetRequiredService<AgentWorkSwitch>();
        var operations = host.Services.GetRequiredService<RecordingOperations>();
        var queued = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created"), ProjectChangedTrigger));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, queued.Outcome);
        var filters = probe.Filters;
        var subjects = probe.Subjects;

        work.AgentWork = false;
        var auditsBefore = host.Business.Events.Count;
        var published = await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created"));
        var manual = await StartAsync(host);
        var scheduled = await TickAsync(host);
        var webhook = await RefuseWebhookAsync(host, """{ "kind": "planning", "projectId": "p-1" }""");

        Assert.Equal(2, published.Count);
        Assert.All(published, receipt =>
        {
            Assert.Equal(NhAssistantTriggerOutcomes.Rejected, receipt.Outcome);
            Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, receipt.ReasonCode);
        });
        Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, manual.ReasonCode);
        Assert.Empty(scheduled);
        Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, webhook!.ReasonCode);
        // No trigger's lambda ran, nothing was queued, and a refusal of the whole host is not audited
        // per event, as with the assistant disabled.
        Assert.Equal(filters, probe.Filters);
        Assert.Equal(subjects, probe.Subjects);
        Assert.Equal(0, probe.ScheduledQueries);
        Assert.Single(operations.Enqueued);
        Assert.DoesNotContain(host.Business.Events.Skip(auditsBefore), evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerRejected);

        // A follow-up of a decided proposal starts no run either.
        var thread = (await Runs(host).FindThreadByConversationAsync(queued.ConversationId!.Value, CancellationToken.None))!;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var followUp = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>().StartFollowUpAsync(
                thread,
                new AssistantApproval
                {
                    Id = Guid.NewGuid(),
                    ConversationId = thread.ConversationId,
                    ProposalId = Guid.NewGuid(),
                    ToolId = "projects.change-status",
                    Status = NhAssistantApprovalStatuses.Rejected
                },
                CancellationToken.None);
            Assert.Equal(NhAssistantTriggerOutcomes.Rejected, followUp.Outcome);
            Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, followUp.ReasonCode);
        }

        // The run queued before the switch ends without working and claims none of its inputs.
        await ExecuteRunAsync(host, queued.RunId!.Value);
        var run = (await Runs(host).FindRunAsync(queued.RunId.Value, CancellationToken.None))!;
        Assert.Equal(NhAssistantRunStatuses.Cancelled, run.Status);
        Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, run.FailureCode);
        Assert.Equal(NhAssistantRunFailureClasses.Policy, run.FailureClass);
        Assert.Equal(1, await WaitingAsync(host, queued.ConversationId.Value));
        Assert.Empty(host.Services.GetRequiredService<TestRuleScript>().Contexts);

        // Once agents work again, the next event starts a run.
        work.AgentWork = true;
        var fired = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created"), ProjectChangedTrigger));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, fired.Outcome);
        Assert.Equal(2, operations.Enqueued.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Approved_proposals_are_held_while_agent_work_is_off_and_released_once_it_is_on(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(DateTimeOffset.UtcNow);
        await using var host = await CreateHostAsync(provider, new TriggerProbe(), clock: clock);
        var work = host.Services.GetRequiredService<AgentWorkSwitch>();
        var operations = host.Services.GetRequiredService<RecordingOperations>();
        host.Services.GetRequiredService<TestRuleScript>().Run = async (context, cancellationToken) =>
        {
            await context.ProposeAsync(
                "projects.change-status",
                new ProjectStatusInput(Guid.Parse(context.Subject.Subject.Id), "Closed"),
                null,
                cancellationToken);
            return NhAssistantRuleResult.Done("Proposed to close the project.");
        };
        var started = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "task done"), ProjectChangedTrigger));
        await ExecuteRunAsync(host, started.RunId!.Value);
        var proposal = Assert.Single(await Runs(host).GetRunApprovalsAsync(started.RunId.Value, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, proposal.Status);

        // People keep deciding while agents do not work; the approved proposal waits. The application's
        // clock moves past the proposal, which the run made on the system clock.
        work.AgentWork = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await DecideAsync(host, proposal.Id)).Success);
        Assert.DoesNotContain(proposal.Id, operations.Executions);

        // An execution that was queued before the switch holds the proposal when it runs.
        await ExecuteProposalAsync(host, proposal.Id);
        var held = await FindApprovalAsync(host, proposal.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Approved, held.Status);
        Assert.Null(held.ExecutionOperationId);

        // The maintenance job queues nothing while agent work is off, and the held proposal once it is on.
        clock.Advance(TimeSpan.FromMinutes(2));
        var maintenance = host.Services.GetServices<IHostedService>().OfType<NhAssistantRunMaintenance>().Single();
        await maintenance.RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(proposal.Id, operations.Executions);

        work.AgentWork = true;
        await maintenance.RunOnceAsync(CancellationToken.None);
        Assert.Contains(proposal.Id, operations.Executions);
        await ExecuteProposalAsync(host, proposal.Id);
        var executed = await FindApprovalAsync(host, proposal.Id);
        Assert.Equal((NhAssistantApprovalStatuses.Executed, (string?)null), (executed.Status, executed.ExecutionResultCode));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task People_keep_chatting_while_hand_offs_consultations_and_application_messages_are_refused(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("The project is on track.");
        await using var host = await CreateHostAsync(provider, new TriggerProbe(), model: model);
        var work = host.Services.GetRequiredService<AgentWorkSwitch>();
        NhAssistantHandOffReceipt? handOff = null;
        host.Services.GetRequiredService<TestRuleScript>().Run = async (context, cancellationToken) =>
        {
            // Agent work goes off while the run works: its hand-off is refused.
            work.AgentWork = false;
            handOff = await context.HandOffAsync(Closer, context.Subject.Subject, "Please close the project.", null, cancellationToken);
            return NhAssistantRuleResult.Done("Tried to hand the project over.");
        };
        var started = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "plan done"), ProjectChangedTrigger));

        await ExecuteRunAsync(host, started.RunId!.Value);

        Assert.Equal(NhAssistantHandOffOutcomes.Refused, handOff!.Outcome);
        Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, handOff.Code);
        Assert.Null(handOff.RunId);
        Assert.DoesNotContain(
            await HandOffsAsync(host),
            item => item.TargetAgentId == Closer);

        // A consultation is refused before it counts toward any limit or records a consultation.
        var options = host.Services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[Closer].Consultations.Single();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var consulted = await scope.ServiceProvider.GetRequiredService<NhAssistantConsultationService>().ConsultAsync(
                new NhAssistantConsultationRequest(
                    options,
                    Guid.NewGuid().ToString("N"),
                    "Can this project close?",
                    "member-1",
                    false,
                    "en",
                    new AllowAllViewer(),
                    new NhAssistantTenantBinding(null)),
                CancellationToken.None);
            Assert.False(consulted.Success);
            Assert.Contains(consulted.GetResultItems(), item => item.Name == NhAssistantErrorCodes.AgentWorkOff);
        }
        await using (var context = Factory(host).CreateDbContext())
        {
            Assert.Empty(await context.Consultations.ToListAsync());
        }

        // A person's chat keeps working.
        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        var turn = await host.SendAsync(conversation.Id, "How is the project doing?");
        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);

        // The application's own messages are refused; the assistant's report of an execution the
        // application made is still posted.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var message = new NhAssistantApplicationMessage("The export is ready.")
            {
                Access = NhAssistantApplicationMessageAccess.WithoutPersonCheck()
            };
            var refused = await scope.ServiceProvider.GetRequiredService<INhAssistantConversationMessages>()
                .PostAsync(conversation.Id, message);
            Assert.False(refused.Success);
            Assert.Contains(refused.GetResultItems(), item => item.Name == NhAssistantErrorCodes.AgentWorkOff);

            var report = await scope.ServiceProvider.GetRequiredService<NhAssistantConversationMessages>()
                .PostAsync(conversation.Id, message, agentWork: false, CancellationToken.None);
            Assert.True(report.Success);
        }

        // Once agents work again, the application posts as before.
        work.AgentWork = true;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var posted = await scope.ServiceProvider.GetRequiredService<INhAssistantConversationMessages>().PostAsync(
                conversation.Id,
                new NhAssistantApplicationMessage("The second export is ready.")
                {
                    Access = NhAssistantApplicationMessageAccess.WithoutPersonCheck()
                });
            Assert.True(posted.Success);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_state_reports_agent_work_and_a_paused_agent_or_skill_resumes_only_once_agents_work(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new TriggerProbe());
        var work = host.Services.GetRequiredService<AgentWorkSwitch>();
        var waiting = Guid.NewGuid();
        await ControlAsync(host, control => control.PauseAsync(AgentId, Operator, "Import"));
        Assert.Equal(
            NhAssistantTriggerOutcomes.Waiting,
            Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), waiting, "imported"), ProjectChangedTrigger)).Outcome);

        work.AgentWork = false;
        var state = (await ControlAsync(host, control => control.GetAsync(AgentId)))!;
        var resumed = await ControlAsync(host, control => control.ResumeAsync(AgentId, Operator));

        Assert.Equal(NhAssistantAgentRunStates.Paused, state.RunState);
        Assert.False(state.AgentWork);
        Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, Code(resumed));
        // The events that waited keep waiting for a resume that can start them.
        Assert.Equal(1, (await ControlAsync(host, control => control.GetAsync(AgentId)))!.WaitingInputs);

        // The other changes serve: a stopped agent resumes, starting nothing, and reports that it does not work.
        Assert.True((await ControlAsync(host, control => control.StopAsync(AgentId, Operator))).Success);
        var active = await ControlAsync(host, control => control.ResumeAsync(AgentId, Operator));
        Assert.True(active.Success);
        Assert.Equal(NhAssistantAgentRunStates.Active, active.Data!.RunState);
        Assert.False(active.Data.Active);
        Assert.False(active.Data.AgentWork);
        Assert.Equal(0, active.Data.StartedRuns);
        Assert.True((await ControlAsync(host, control => control.PauseSkillAsync(AgentId, Notes, Operator))).Success);
        Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, Code(await ControlAsync(host, control => control.ResumeSkillAsync(AgentId, Notes, Operator))));

        // Once agents work again, the agent is active and the skill resumes.
        work.AgentWork = true;
        var working = (await ControlAsync(host, control => control.GetAsync(AgentId)))!;
        Assert.True(working.Active);
        Assert.True(working.AgentWork);
        Assert.True((await ControlAsync(host, control => control.ResumeSkillAsync(AgentId, Notes, Operator))).Success);
    }

    [Fact]
    public async Task Status_and_administration_report_agent_work_and_a_person_chats_and_decides_in_their_chat()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Active" } })
            .RespondWithText("Done: the project is active.");
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services => services.Configure<NhAssistantOptions>(options => options.AgentWork = false));
        using var client = app.CreateClient();
        using var admin = app.CreateClient(admin: true);

        var status = await ReadJsonAsync(await client.GetAsync("/api/assistant/status"));
        var agents = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"));
        var state = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{AgentId}/state"));
        using var paused = await admin.PostAsync($"/api/assistant/admin/agents/{AgentId}/state", ServerSentEventReader.Json(new { state = "paused" }));
        using var resumed = await admin.PostAsync($"/api/assistant/admin/agents/{AgentId}/state", ServerSentEventReader.Json(new { state = "active" }));

        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("agentWork").GetBoolean());
        Assert.NotEmpty(status.GetProperty("agents").EnumerateArray());
        Assert.All(agents.EnumerateArray(), agent => Assert.False(agent.GetProperty("agentWork").GetBoolean()));
        Assert.Equal("active", state.GetProperty("runState").GetString());
        Assert.False(state.GetProperty("active").GetBoolean());
        Assert.False(state.GetProperty("agentWork").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, resumed.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, (await ReadJsonAsync(resumed)).GetProperty("code").GetString());

        // A person chats with the paused agent and decides the proposal of their own chat, which runs in
        // the turn: a person's chat is no agent work.
        using var created = await client.PostAsync("/api/assistant/conversations", ServerSentEventReader.Json(new { agentId = AgentId }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversationId = (await ReadJsonAsync(created)).GetProperty("id").GetString();
        using var turn = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/messages",
            ServerSentEventReader.Json(new { text = "Activate the project.", clientMessageId = "c-1" }));
        var (events, _) = await ServerSentEventReader.ReadAllAsync(turn);
        var approval = events.Single(evt => evt.Name == "approval.required").Data;
        using var decided = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/approvals/{approval.GetProperty("approvalId").GetString()}/decide",
            ServerSentEventReader.Json(new { decision = "approve", expectedProposalHash = approval.GetProperty("proposalHash").GetString() }));
        var (decision, _) = await ServerSentEventReader.ReadAllAsync(decided);

        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        Assert.Contains(decision, evt => evt.Name == "tool.completed" && evt.Data.GetProperty("status").GetString() == "succeeded");
        Assert.Equal("completed", decision.Single(evt => evt.Name == "turn.completed").Data.GetProperty("status").GetString());
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        TriggerProbe probe,
        NhAiScriptedChatClient? model = null,
        AssistantTestClock? clock = null)
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
                services.AddSingleton<AgentWorkSwitch>();
                services.Replace(ServiceDescriptor.Singleton<IOptionsMonitor<NhAssistantOptions>>(provider =>
                    provider.GetRequiredService<AgentWorkSwitch>()));
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                if (clock is not null)
                {
                    services.AddSingleton<TimeProvider>(clock);
                }
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddAgent(AssistantTestData.Agent(Closer) with { ToolSelectors = ["projects.*"] })
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>()
                    .Skill(Notes, skill => skill
                        .Description("Writes notes about the project.")
                        .UseRuleHandler<TestSkillRules>()))
                .ConfigureAgent(Closer, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .AcceptHandOffs("project", handOff => handOff.From(AgentId))
                    .AllowConsultation("project", consult => consult.Description("Knows when a project can close.")))
                .AddTrigger<ProjectChanged>(ProjectChangedTrigger, trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => probe.Filter(evt.Change))
                    .Subject(evt => probe.Subject(new NhAiSubjectRef("project", evt.ProjectId.ToString("N"))))
                    .Message(evt => "The project changed: " + evt.Change)
                    .Dedupe(evt => evt.EventId.ToString("N"))
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("project-changed-audit", trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => probe.Filter(evt.Change) && evt.Change == "audited")
                    .Subject(evt => probe.Subject(new NhAiSubjectRef("project", evt.ProjectId.ToString("N"))))
                    .Message(evt => "Audit: " + evt.Change))
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

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(
        AssistantTestHost host,
        ProjectChanged evt,
        string? triggerId = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
        return triggerId is null ? receipts : receipts.Where(receipt => receipt.TriggerId == triggerId).ToArray();
    }

    private static async Task<NhAssistantTriggerReceipt> StartAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().StartAsync(
            AgentId,
            new NhAiSubjectRef("project", Guid.NewGuid().ToString("N")),
            "member-1",
            "Look at this project.");
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

    private static async Task ExecuteRunAsync(AssistantTestHost host, Guid runId)
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
        var result = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalExecution>().ExecuteAsync(
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

    private static async Task<AssistantApproval> FindApprovalAsync(AssistantTestHost host, Guid approvalId)
    {
        await using var context = Factory(host).CreateDbContext();
        return await context.Approvals.AsNoTracking().SingleAsync(item => item.Id == approvalId);
    }

    private static async Task<int> WaitingAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = Factory(host).CreateDbContext();
        return await context.QueuedInputs.CountAsync(input => input.ConversationId == conversationId && input.ConsumedAt == null);
    }

    private static async Task<List<AssistantHandOff>> HandOffsAsync(AssistantTestHost host)
    {
        await using var context = Factory(host).CreateDbContext();
        return await context.HandOffs.AsNoTracking().ToListAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string? Code(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private static NhAssistantDbContextFactory Factory(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantDbContextFactory>();
    }

    private sealed class AllowAllViewer : INhAssistantConsultationViewer
    {
        public ValueTask<bool> MayViewAsync(NhAiGrantSource grantSource, string toolId, string? readForActorId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> MayDecideAsync(NhAiApprovalPolicy policy, AssistantThread thread, string toolId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> MayUseAgentAsync(NhAssistantAgentDefinition agent, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }
    }
}

/// <summary>
/// The assistant's options with <see cref="NhAssistantOptions.AgentWork"/> as a switch that tests flip,
/// as a reloaded configuration would; the assistant stays enabled.
/// </summary>
public sealed class AgentWorkSwitch(IOptionsFactory<NhAssistantOptions> factory) : IOptionsMonitor<NhAssistantOptions>
{
    public bool AgentWork { get; set; } = true;

    public NhAssistantOptions CurrentValue => Get(Options.DefaultName);

    public NhAssistantOptions Get(string? name)
    {
        var options = factory.Create(name ?? Options.DefaultName);
        options.AgentWork = AgentWork;
        return options;
    }

    public IDisposable? OnChange(Action<NhAssistantOptions, string?> listener)
    {
        return null;
    }
}
