using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
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
/// Pausing, stopping and resuming agents, the early checks of triggers and schedules, the global
/// switch and the held executions. Runs are enqueued on a recording background-operation
/// service, as in the trigger tests.
/// </summary>
public sealed class AssistantAgentControlTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ProjectChangedTrigger = "project-changed";
    private const string WeeklyTrigger = "weekly-review";
    private const string WebhookTrigger = "planning-webhook";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Pausing_lets_events_wait_per_thread_and_resuming_starts_one_run_per_thread(AssistantTestProvider provider)
    {
        var probe = new TriggerProbe();
        await using var host = await CreateHostAsync(provider, probe);
        var operations = host.Services.GetRequiredService<RecordingOperations>();
        var alpha = Guid.NewGuid();
        var bravo = Guid.NewGuid();
        var versionBefore = await VersionAsync(host);

        var paused = await ControlAsync(host, control => control.PauseAsync(AgentId, "operator-1", "  Nightly import  "));
        var first = new ProjectChanged(Guid.NewGuid(), alpha, "task added");
        var receipts = new List<NhAssistantTriggerReceipt>();
        receipts.AddRange(await PublishAsync(host, first));
        receipts.AddRange(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), alpha, "deadline moved")));
        receipts.AddRange(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), bravo, "task added")));
        var redelivered = Assert.Single(await PublishAsync(host, first));
        var state = await ControlAsync(host, control => control.GetAsync(AgentId));

        Assert.True(paused.Success);
        Assert.Equal(NhAssistantAgentRunStates.Paused, paused.Data!.RunState);
        Assert.False(paused.Data.Active);
        Assert.Equal("Nightly import", paused.Data.Reason);
        Assert.Equal("operator-1", paused.Data.ChangedBy);
        Assert.All(receipts, receipt =>
        {
            Assert.Equal(NhAssistantTriggerOutcomes.Waiting, receipt.Outcome);
            Assert.Equal(NhAssistantErrorCodes.AgentPaused, receipt.ReasonCode);
            Assert.Null(receipt.RunId);
            Assert.NotNull(receipt.ConversationId);
        });
        Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, redelivered.Outcome);
        Assert.Empty(operations.Enqueued);
        Assert.Equal(3, state!.WaitingInputs);
        Assert.Equal(versionBefore, await VersionAsync(host));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentPaused
            && evt.ObjectId == AgentId
            && evt.ActorId == "operator-1");
        Assert.DoesNotContain(host.Business.Events, evt => evt.ResultCode?.Contains("import", StringComparison.OrdinalIgnoreCase) == true);

        var resumed = await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));

        Assert.True(resumed.Success);
        Assert.Equal(NhAssistantAgentRunStates.Active, resumed.Data!.RunState);
        Assert.True(resumed.Data.Active);
        Assert.Equal(2, resumed.Data.StartedRuns);
        Assert.Equal(0, resumed.Data.WaitingInputs);
        Assert.Equal(2, operations.Enqueued.Count);
        await using (var context = Factory(host).CreateDbContext())
        {
            var alphaThread = receipts[0].ConversationId!.Value;
            var run = await context.Runs.SingleAsync(item => item.ConversationId == alphaThread);
            Assert.Equal(NhAssistantRunStatuses.Queued, run.Status);
            Assert.Equal(ProjectChangedTrigger, run.TriggerId);
            Assert.Equal(first.EventId.ToString("N"), run.TriggerEventKey);
            // The run claims both waiting inputs of its thread when it starts.
            Assert.Equal(2, await context.QueuedInputs.CountAsync(input => input.ConversationId == alphaThread && input.ConsumedAt == null));
        }
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentRunsResumed && evt.ResultCode == "resumed-2");

        // A second resume finds nothing to start.
        var again = await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));
        Assert.Equal(0, again.Data!.StartedRuns);
        Assert.Equal(2, operations.Enqueued.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_queued_before_a_pause_ends_without_claiming_its_inputs_and_runs_again_after_the_resume(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new TriggerProbe());
        var started = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created")));
        await ControlAsync(host, control => control.PauseAsync(AgentId, "operator-1"));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
            var result = await operation.ExecuteAsync(
                new NhAssistantAgentRunRequest(started.RunId!.Value),
                Substitute.For<INhBackgroundOperationContext>(),
                CancellationToken.None);
            Assert.True(result.Success);
        }

        var run = await host.Services.GetRequiredService<NhAssistantRunStore>().FindRunAsync(started.RunId!.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Cancelled, run!.Status);
        Assert.Equal(NhAssistantErrorCodes.AgentPaused, run.FailureCode);
        Assert.Equal(1, await WaitingAsync(host, started.ConversationId!.Value));

        var resumed = await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));

        Assert.Equal(1, resumed.Data!.StartedRuns);
        Assert.Equal(2, host.Services.GetRequiredService<RecordingOperations>().Enqueued.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Stopping_ends_work_drops_waiting_inputs_and_holds_approved_executions_until_the_agent_resumes(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Closed" } }, "call-close")
            .RespondWithText("Proposed.");
        await using var host = await CreateHostAsync(provider, new TriggerProbe(), model);
        var operations = host.Services.GetRequiredService<RecordingOperations>();
        var started = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), projectId, "created")));
        var approval = await ProposeAsync(host, started);
        var waitingThread = Guid.NewGuid();
        await ControlAsync(host, control => control.PauseAsync(AgentId, "operator-1"));
        var waiting = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), waitingThread, "created")));

        var stopped = await ControlAsync(host, control => control.StopAsync(AgentId, "operator-1", "Wrong proposals"));

        Assert.Equal(NhAssistantAgentRunStates.Stopped, stopped.Data!.RunState);
        Assert.Equal(1, stopped.Data.CancelledRuns);
        Assert.Equal(NhAssistantApprovalStatuses.Withdrawn, (await FindApprovalAsync(host, approval.Id)).Status);
        Assert.Equal(0, await WaitingAsync(host, waiting.ConversationId!.Value));
        var refused = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), projectId, "changed")));
        Assert.Equal(NhAssistantErrorCodes.AgentStopped, refused.ReasonCode);

        // An approved proposal of the run thread, a consultation proposal (no run) and, before them,
        // more than a batch of proposals that are already queued for execution.
        var decidedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var context = Factory(host).CreateDbContext())
        {
            for (var index = 0; index < 101; index++)
            {
                context.Approvals.Add(Clone(approval, NhAssistantApprovalStatuses.Approved, decidedAt.AddMinutes(-10), Guid.NewGuid(), approval.RunId));
            }
            await context.SaveChangesAsync();
        }
        var held = await AddApprovedAsync(host, approval, decidedAt, approval.RunId);
        var consultation = await AddApprovedAsync(host, approval, decidedAt, runId: null);
        var maintenance = host.Services.GetServices<IHostedService>().OfType<NhAssistantRunMaintenance>().Single();

        await maintenance.RunOnceAsync(CancellationToken.None);
        Assert.Empty(operations.Executions);

        // Resuming queues the executions the stop held, the consultation's included.
        await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));
        Assert.Equal(new[] { consultation, held }.Order(), operations.Executions.Order());

        // The maintenance job finds an approved proposal without an execution behind more than a
        // batch of proposals that already have one, but queues nothing while the assistant is off.
        var later = await AddApprovedAsync(host, approval, decidedAt, approval.RunId);
        host.Services.GetRequiredService<AssistantSwitch>().Enabled = false;
        await maintenance.RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(later, operations.Executions);

        host.Services.GetRequiredService<AssistantSwitch>().Enabled = true;
        await maintenance.RunOnceAsync(CancellationToken.None);
        Assert.Contains(later, operations.Executions);
        Assert.Equal(3, operations.Executions.Count);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentRunsStopped && evt.ResultCode == "cancelled-1");
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Editing_and_resetting_never_change_the_state_and_a_state_change_never_conflicts_with_an_editor(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new TriggerProbe());
        var version = await VersionAsync(host);
        await ControlAsync(host, control => control.PauseAsync(AgentId, "operator-1", "Review"));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var administration = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
            var agent = (await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>()
                .FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
            var definition = agent.Definition;
            var edited = await administration.UpdateAsync(
                new NhAssistantAgentInput(
                    AgentId,
                    definition.DisplayNameKey,
                    definition.DescriptionKey,
                    "Work on projects carefully.",
                    definition.ToolSelectors,
                    [],
                    definition.RequiredPolicy,
                    definition.Autonomy),
                version,
                "admin-1",
                CancellationToken.None);
            Assert.True(edited.Success);
            Assert.Equal(NhAssistantAgentRunStates.Paused, edited.Data!.RunState);

            var reset = await administration.ResetAsync(AgentId, "admin-1", CancellationToken.None);
            Assert.True(reset.Success);
            Assert.Equal(NhAssistantAgentRunStates.Paused, reset.Data!.RunState);
            Assert.Equal("Review", reset.Data.StateReason);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_code_disabled_agent_reports_configured_off_and_cannot_be_resumed(AssistantTestProvider provider)
    {
        var probe = new TriggerProbe();
        await using var host = await CreateHostAsync(provider, probe, enabled: false);

        var state = await ControlAsync(host, control => control.GetAsync(AgentId));
        var resumed = await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));
        var stopped = await ControlAsync(host, control => control.StopAsync(AgentId, "operator-1"));
        var receipt = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created")));

        Assert.Equal(NhAssistantAgentRunStates.ConfiguredOff, state!.RunState);
        Assert.False(state.Active);
        Assert.Equal(NhAssistantErrorCodes.AgentConfiguredOff, Code(resumed));
        // Stopping is stored for the next deployment that turns the agent on in code.
        Assert.True(stopped.Success);
        Assert.Equal(NhAssistantAgentRunStates.ConfiguredOff, stopped.Data!.RunState);
        Assert.Equal(NhAssistantErrorCodes.AgentConfiguredOff, receipt.ReasonCode);
        Assert.Equal(0, probe.Subjects);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>()
            .FindAsync(AgentId, includeDisabled: false, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Triggers_read_the_state_before_any_lambda_and_When_filters_without_an_audit(AssistantTestProvider provider)
    {
        var probe = new TriggerProbe();
        await using var host = await CreateHostAsync(provider, probe);
        await ControlAsync(host, control => control.StopAsync(AgentId, "operator-1"));
        var auditsBefore = host.Business.Events.Count;

        var refused = await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created"), all: true);
        var started = await StartAsync(host);
        var scheduled = await TickAsync(host);

        Assert.Equal(2, refused.Count);
        Assert.All(refused, receipt => Assert.Equal(NhAssistantErrorCodes.AgentStopped, receipt.ReasonCode));
        Assert.Equal(NhAssistantErrorCodes.AgentStopped, started.ReasonCode);
        Assert.Empty(scheduled);
        Assert.Equal(0, probe.Filters);
        Assert.Equal(0, probe.Subjects);
        Assert.Equal(0, probe.ScheduledQueries);
        // One audit for the publish call of the agent and one for the manual start.
        Assert.Equal(2, host.Business.Events.Skip(auditsBefore).Count(evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerRejected));

        await ControlAsync(host, control => control.PauseAsync(AgentId, "operator-1"));
        Assert.Empty(await TickAsync(host));
        Assert.Equal(0, probe.ScheduledQueries);

        await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));
        auditsBefore = host.Business.Events.Count;
        var filtered = await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), TriggerProbe.Unrelated), all: true);
        var webhook = await RefuseWebhookAsync(host, """{ "kind": "unrelated", "projectId": "p-1" }""");

        Assert.All(filtered, receipt =>
        {
            Assert.Equal(NhAssistantTriggerOutcomes.Ignored, receipt.Outcome);
            Assert.Equal(NhAssistantTriggerReasons.Filtered, receipt.ReasonCode);
        });
        Assert.Equal(NhAssistantTriggerReasons.Filtered, webhook!.ReasonCode);
        Assert.Equal(0, probe.Subjects);
        Assert.Equal(auditsBefore, host.Business.Events.Count);
        Assert.Null(await RefuseWebhookAsync(host, """{ "kind": "planning", "projectId": "p-1" }"""));

        var fired = await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created"));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, Assert.Single(fired).Outcome);
        Assert.Equal(1, probe.Subjects);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task With_the_assistant_disabled_triggers_schedules_and_runs_do_nothing(AssistantTestProvider provider)
    {
        var probe = new TriggerProbe();
        await using var host = await CreateHostAsync(provider, probe);
        var started = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created")));
        host.Services.GetRequiredService<AssistantSwitch>().Enabled = false;
        var auditsBefore = host.Business.Events.Count;

        var published = await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created"), all: true);
        var manual = await StartAsync(host);
        var scheduled = await TickAsync(host);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
            await operation.ExecuteAsync(
                new NhAssistantAgentRunRequest(started.RunId!.Value),
                Substitute.For<INhBackgroundOperationContext>(),
                CancellationToken.None);
        }

        Assert.Equal(2, published.Count);
        Assert.All(published, receipt => Assert.Equal(NhAssistantErrorCodes.Disabled, receipt.ReasonCode));
        Assert.Equal(NhAssistantErrorCodes.Disabled, manual.ReasonCode);
        Assert.Empty(scheduled);
        Assert.Equal(1, probe.Subjects);
        Assert.Equal(0, probe.ScheduledQueries);
        var run = await host.Services.GetRequiredService<NhAssistantRunStore>().FindRunAsync(started.RunId!.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Cancelled, run!.Status);
        Assert.Equal(NhAssistantErrorCodes.Disabled, run.FailureCode);
        Assert.DoesNotContain(host.Business.Events.Skip(auditsBefore), evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerRejected);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        TriggerProbe probe,
        NhAiScriptedChatClient? model = null,
        bool enabled = true)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model ?? new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.AddSingleton<RecordingChannel>();
                services.AddSingleton<AssistantSwitch>();
                services.Replace(ServiceDescriptor.Singleton<IOptionsMonitor<NhAssistantOptions>>(provider =>
                    provider.GetRequiredService<AssistantSwitch>()));
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddAgentNotificationChannel<RecordingChannelAdapter>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .Enabled(enabled))
                .AddTrigger<ProjectChanged>(ProjectChangedTrigger, trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => probe.Filter(evt.Change))
                    .Subject(evt => probe.Subject(new NhAiSubjectRef("project", evt.ProjectId.ToString("N"))))
                    .Message(evt => "The project changed: " + evt.Change)
                    .Dedupe(evt => evt.EventId.ToString("N"))
                    .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.FromMinutes(1)))
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

    /// <summary>
    /// Publishes the event and returns the receipt of <see cref="ProjectChangedTrigger"/>, or with
    /// <paramref name="all"/> the receipts of both triggers of the agent.
    /// </summary>
    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(
        AssistantTestHost host,
        ProjectChanged evt,
        bool all = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
        return all ? receipts : receipts.Where(receipt => receipt.TriggerId == ProjectChangedTrigger).ToArray();
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

    private static async Task<T> ControlAsync<T>(AssistantTestHost host, Func<INhAssistantAgentControl, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>());
    }

    private static async Task<int> VersionAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var agent = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>()
            .FindAsync(AgentId, includeDisabled: true, CancellationToken.None);
        return agent!.Definition.Version;
    }

    private static async Task<int> WaitingAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = Factory(host).CreateDbContext();
        return await context.QueuedInputs.CountAsync(input => input.ConversationId == conversationId && input.ConsumedAt == null);
    }

    private static async Task<AssistantApproval> FindApprovalAsync(AssistantTestHost host, Guid approvalId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
    }

    private static async Task<Guid> AddApprovedAsync(AssistantTestHost host, AssistantApproval source, DateTimeOffset decidedAt, Guid? runId)
    {
        await using var context = Factory(host).CreateDbContext();
        var approval = Clone(source, NhAssistantApprovalStatuses.Approved, decidedAt, null, runId);
        if (runId is null)
        {
            approval.ConsultationId = Guid.NewGuid();
        }
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval.Id;
    }

    private static AssistantApproval Clone(
        AssistantApproval source,
        string status,
        DateTimeOffset decidedAt,
        Guid? executionOperationId,
        Guid? runId)
    {
        return new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = source.ConversationId,
            TurnId = source.TurnId,
            ToolInvocationId = source.ToolInvocationId,
            ProposalId = Guid.NewGuid(),
            ProposalHash = source.ProposalHash,
            ProposalJson = source.ProposalJson,
            ToolId = source.ToolId,
            Summary = source.Summary,
            ArgumentsPreview = source.ArgumentsPreview,
            TargetsJson = source.TargetsJson,
            Status = status,
            CreatedAt = decidedAt.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            DecidedByActorId = "approver-1",
            DecidedAt = decidedAt,
            RunId = runId,
            PolicyId = source.PolicyId,
            Mode = NhAssistantApprovalModes.Deferred,
            PolicyJson = source.PolicyJson,
            GrantSource = source.GrantSource,
            ExecutionOperationId = executionOperationId,
            ConcurrencyStamp = Guid.NewGuid()
        };
    }

    private static NhAssistantDbContextFactory Factory(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantDbContextFactory>();
    }

    private static string? Code(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    /// <summary>
    /// Runs one headless turn of the started run, in which the scripted model proposes a status change.
    /// </summary>
    private static async Task<AssistantApproval> ProposeAsync(AssistantTestHost host, NhAssistantTriggerReceipt started)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var runs = services.GetRequiredService<NhAssistantRunStore>();
        var thread = (await runs.FindThreadByConversationAsync(started.ConversationId!.Value, CancellationToken.None))!;
        var run = (await runs.FindRunAsync(started.RunId!.Value, CancellationToken.None))!;
        var subject = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), CancellationToken.None))!;
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var agentTurn = new NhAssistantAgentTurnContext(
            thread,
            subject,
            options,
            run.Id,
            run.TriggerId,
            null,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        var result = await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Close the project.")]),
            CancellationToken.None);
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        return Assert.Single(await runs.GetRunApprovalsAsync(run.Id, CancellationToken.None));
    }
}

/// <summary>
/// Counts the trigger lambdas that run, so tests can prove that a check came first.
/// </summary>
public sealed class TriggerProbe
{
    public const string Unrelated = "unrelated";

    private int _filters;
    private int _subjects;
    private int _scheduledQueries;

    public int Filters => _filters;

    public int Subjects => _subjects;

    public int ScheduledQueries => _scheduledQueries;

    public bool Filter(string change)
    {
        Interlocked.Increment(ref _filters);
        return change != Unrelated;
    }

    public NhAiSubjectRef Subject(NhAiSubjectRef subject)
    {
        Interlocked.Increment(ref _subjects);
        return subject;
    }

    public async IAsyncEnumerable<NhAiSubjectRef> Scheduled([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _scheduledQueries);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new NhAiSubjectRef("project", "project-alpha");
    }
}

/// <summary>
/// The assistant's options with a switch for <see cref="NhAssistantOptions.Enabled"/> that tests flip.
/// </summary>
public sealed class AssistantSwitch(IOptionsFactory<NhAssistantOptions> factory) : IOptionsMonitor<NhAssistantOptions>
{
    public bool Enabled { get; set; } = true;

    public NhAssistantOptions CurrentValue => Get(Options.DefaultName);

    public NhAssistantOptions Get(string? name)
    {
        var options = factory.Create(name ?? Options.DefaultName);
        options.Enabled = Enabled;
        return options;
    }

    public IDisposable? OnChange(Action<NhAssistantOptions, string?> listener)
    {
        return null;
    }
}
