using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Triggers, schedules, webhook signatures, the kill switch, agent notifications and reminders.
/// Runs are enqueued on a recording background-operation service: these tests prove what starts,
/// joins, stops and notifies, not how an operation runs.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantAgentTriggerTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ProjectChangedTrigger = "project-changed";
    private const string WeeklyTrigger = "weekly-review";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_burst_of_events_on_one_subject_joins_one_pending_run(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var projectId = Guid.NewGuid();

        var receipts = new List<NhAssistantTriggerReceipt>();
        for (var index = 0; index < 10; index++)
        {
            receipts.AddRange(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), projectId, "change " + index)));
        }

        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipts[0].Outcome);
        Assert.All(receipts.Skip(1), receipt => Assert.Equal(NhAssistantTriggerOutcomes.Coalesced, receipt.Outcome));
        Assert.Single(receipts.Select(receipt => receipt.RunId).Distinct());
        var enqueue = Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Enqueued);
        Assert.NotNull(enqueue.Options.NotBefore);
        Assert.Null(enqueue.Options.OwnerUserId);
        Assert.Equal(10, await CountQueuedInputsAsync(host, receipts[0].ConversationId!.Value));
        Assert.Equal(10, host.Business.Events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerAccepted));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_schedule_tick_starts_each_subject_once_within_its_cap(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var tick = new DateTimeOffset(2026, 10, 12, 7, 0, 30, TimeSpan.Zero);

        await using var scope = host.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<NhAssistantScheduledTriggerJob>();
        var first = await job.RunAsync(WeeklyTrigger, tick, CancellationToken.None);
        var repeated = await job.RunAsync(WeeklyTrigger, tick.AddSeconds(20), CancellationToken.None);

        Assert.Equal(2, first.Count);
        Assert.All(first, receipt => Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome));
        Assert.All(repeated, receipt => Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, receipt.Outcome));
        Assert.Equal(first.Select(receipt => receipt.RunId), repeated.Select(receipt => receipt.RunId));
        Assert.Equal(2, host.Services.GetRequiredService<RecordingOperations>().Enqueued.Count);
    }

    [Fact]
    public void A_webhook_signature_covers_the_timestamp_the_delivery_and_the_body()
    {
        const string secret = "a-shared-webhook-secret-of-enough-length";
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var timestamp = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body = Encoding.UTF8.GetBytes("{\"projectId\":\"alpha\"}");
        var signature = "sha256=" + NhAssistantWebhookEndpoints.Sign(secret, timestamp, "delivery-1", body);

        Assert.True(NhAssistantWebhookEndpoints.Verify(secret, timestamp, "delivery-1", signature, body, now));
        Assert.False(NhAssistantWebhookEndpoints.Verify(secret + "x", timestamp, "delivery-1", signature, body, now));
        Assert.False(NhAssistantWebhookEndpoints.Verify(secret, timestamp, "delivery-2", signature, body, now));
        Assert.False(NhAssistantWebhookEndpoints.Verify(secret, timestamp, "delivery-1", signature, Encoding.UTF8.GetBytes("{}"), now));
        Assert.False(NhAssistantWebhookEndpoints.Verify(secret, timestamp, "delivery-1", signature, body, now.AddMinutes(6)));
        Assert.False(NhAssistantWebhookEndpoints.Verify(secret, timestamp, "delivery 1", signature, body, now));
        Assert.False(NhAssistantWebhookEndpoints.Verify(secret, timestamp, "delivery-1", signature["sha256=".Length..], body, now));
    }

    [Fact]
    public void A_webhook_refuses_a_short_secret_and_a_secret_reference_left_as_text()
    {
        Assert.True(NhAssistantWebhookEndpoints.IsUsableSecret(new string('s', 40)));
        Assert.False(NhAssistantWebhookEndpoints.IsUsableSecret(null));
        Assert.False(NhAssistantWebhookEndpoints.IsUsableSecret(new string('s', 31)));
        // A missing secret leaves its appsettings reference as text, long enough but readable by anyone.
        Assert.False(NhAssistantWebhookEndpoints.IsUsableSecret("${Secrets:Application:ReviewBoard:WebhookSecret}"));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Stopping_an_agent_cancels_active_runs_and_rejects_triggers_until_it_resumes(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var started = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created")));

        NhAssistantAgentState engaged;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>();
            engaged = (await control.StopAsync(AgentId, "admin-1", "Data migration")).Data!;
        }

        Assert.Equal(NhAssistantAgentRunStates.Stopped, engaged.RunState);
        Assert.False(engaged.Active);
        Assert.Equal("Data migration", engaged.Reason);
        Assert.Equal(1, engaged.CancelledRuns);
        var run = await host.Services.GetRequiredService<NhAssistantRunStore>().FindRunAsync(started.RunId!.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Cancelled, run!.Status);
        Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Cancelled);
        var rejected = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created")));
        Assert.Equal(NhAssistantTriggerOutcomes.Rejected, rejected.Outcome);
        Assert.Equal(NhAssistantErrorCodes.AgentStopped, rejected.ReasonCode);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentRunsStopped);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var released = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>()
                .ResumeAsync(AgentId, "admin-1");
            Assert.Equal(NhAssistantAgentRunStates.Active, released.Data!.RunState);
            Assert.True(released.Data.Active);
        }

        var restarted = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created")));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, restarted.Outcome);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Agent_notifications_reach_the_subjects_approvers_and_never_come_from_shadow_work(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var started = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created")));
        var runs = host.Services.GetRequiredService<NhAssistantRunStore>();
        var thread = (await runs.FindThreadByConversationAsync(started.ConversationId!.Value, CancellationToken.None))!;

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var notifier = scope.ServiceProvider.GetRequiredService<NhAssistantAgentNotifier>();
            await notifier.NotifyAsync(
                NhAssistantAgentNotificationKind.RunFailed,
                thread,
                started.RunId,
                null,
                "member-1",
                shadow: false,
                CancellationToken.None,
                failureCode: "assistant-turn-failed");
            await notifier.NotifyAsync(
                NhAssistantAgentNotificationKind.RunEndedWithProposals,
                thread,
                started.RunId,
                null,
                null,
                shadow: true,
                CancellationToken.None,
                proposalCount: 2);
        }

        var delivered = Assert.Single(host.Services.GetRequiredService<RecordingChannel>().Deliveries);
        Assert.Equal(NhAssistantAgentNotificationKind.RunFailed, delivered.Context.Kind);
        Assert.Equal(["approver-1", "member-1"], delivered.Recipients.Order(StringComparer.Ordinal));
        Assert.Equal("assistant-turn-failed", delivered.Context.FailureCode);
        Assert.Contains(delivered.Context.Members, member => member.ActorId == "approver-1" && member.Role == NhAiSubjectRole.Approver);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_due_reminder_of_a_waiting_proposal_is_sent_once(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Closed" } }, "call-close")
            .RespondWithText("Proposed.");
        await using var host = await CreateHostAsync(provider, model);
        var started = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), projectId, "created")));
        var approval = await ProposeAsync(host, started);
        Assert.NotNull(approval.NextReminderAt);
        Assert.True(approval.NextReminderAt > DateTimeOffset.UtcNow.AddHours(23));
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            // A day later: the first reminder is due.
            await context.Approvals
                .Where(item => item.Id == approval.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextReminderAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var maintenance = host.Services.GetServices<IHostedService>().OfType<NhAssistantRunMaintenance>().Single();

        await maintenance.RunOnceAsync(CancellationToken.None);
        await maintenance.RunOnceAsync(CancellationToken.None);

        var reminder = Assert.Single(
            host.Services.GetRequiredService<RecordingChannel>().Deliveries,
            delivery => delivery.Context.Kind == NhAssistantAgentNotificationKind.ProposalExpiring);
        Assert.Equal(approval.Id, reminder.Context.ApprovalId);
        Assert.Equal(["approver-1"], reminder.Recipients);
        var stored = await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approval.Id, CancellationToken.None);
        Assert.Equal(1, stored!.RemindersSent);
        Assert.Null(stored.NextReminderAt);
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
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.AddSingleton<RecordingChannel>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddAgentNotificationChannel<RecordingChannelAdapter>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Reminders(TimeSpan.FromDays(1)))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review"))
                .AddTrigger<ProjectChanged>(ProjectChangedTrigger, trigger => trigger
                    .StartAgent(AgentId)
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => "The project changed: " + evt.Change)
                    .Dedupe(evt => evt.EventId.ToString("N"))
                    .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.FromMinutes(1)))
                .AddScheduledTrigger(WeeklyTrigger, schedule => schedule
                    .StartAgent(AgentId)
                    .Cron("0 7 * * 1")
                    .Subjects((_, cancellationToken) => WeeklySubjects(cancellationToken))
                    .Message(tick => "Weekly review of " + tick.Subject.Id + ".")
                    .MaxSubjectsPerTick(2)));
    }

    private static async IAsyncEnumerable<NhAiSubjectRef> WeeklySubjects([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var id in new[] { "project-alpha", "project-bravo", "project-charlie" })
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new NhAiSubjectRef("project", id);
        }
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, ProjectChanged evt)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
    }

    private static async Task<int> CountQueuedInputsAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return context.QueuedInputs.Count(input => input.ConversationId == conversationId);
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

public sealed record ProjectChanged(Guid EventId, Guid ProjectId, string Change);

/// <summary>
/// A background-operation service that records what the assistant enqueues and cancels.
/// </summary>
public sealed class RecordingOperations
{
    public RecordingOperations()
    {
        Service = Substitute.For<INhBackgroundOperationService>();
        Service.EnqueueAsync(Arg.Any<NhAssistantAgentRunRequest>(), Arg.Any<NhBackgroundOperationEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Enqueued.Enqueue((call.ArgAt<NhAssistantAgentRunRequest>(0).RunId, call.ArgAt<NhBackgroundOperationEnqueueOptions>(1)));
                return Task.FromResult(TaskResult<NhBackgroundOperationViewModel>.Succeeded(new NhBackgroundOperationViewModel { Id = Guid.NewGuid() }));
            });
        Service.EnqueueAsync(Arg.Any<NhAssistantExecuteProposalRequest>(), Arg.Any<NhBackgroundOperationEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Executions.Enqueue(call.ArgAt<NhAssistantExecuteProposalRequest>(0).ApprovalId);
                return Task.FromResult(TaskResult<NhBackgroundOperationViewModel>.Succeeded(new NhBackgroundOperationViewModel { Id = Guid.NewGuid() }));
            });
        Service.EnqueueAsync(Arg.Any<NhAssistantExecuteProposalOnceRequest>(), Arg.Any<NhBackgroundOperationEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Executions.Enqueue(call.ArgAt<NhAssistantExecuteProposalOnceRequest>(0).ApprovalId);
                return Task.FromResult(TaskResult<NhBackgroundOperationViewModel>.Succeeded(new NhBackgroundOperationViewModel { Id = Guid.NewGuid() }));
            });
        Service.EnqueueAsync(Arg.Any<NhAssistantReplayOperationRequest>(), Arg.Any<NhBackgroundOperationEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Replays.Enqueue(call.ArgAt<NhAssistantReplayOperationRequest>(0).ReplayId);
                return Task.FromResult(TaskResult<NhBackgroundOperationViewModel>.Succeeded(new NhBackgroundOperationViewModel { Id = Guid.NewGuid() }));
            });
        Service.RequestCancellationForActorAsync(Arg.Any<Guid>(), Arg.Any<NhBackgroundOperationActor>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Cancelled.Enqueue(call.ArgAt<Guid>(0));
                return Task.FromResult(TaskResult<NhBackgroundOperationViewModel>.Succeeded(new NhBackgroundOperationViewModel()));
            });
    }

    public INhBackgroundOperationService Service { get; }

    public ConcurrentQueue<(Guid RunId, NhBackgroundOperationEnqueueOptions Options)> Enqueued { get; } = new();

    public ConcurrentQueue<Guid> Cancelled { get; } = new();

    /// <summary>
    /// The approvals whose execution was queued.
    /// </summary>
    public ConcurrentQueue<Guid> Executions { get; } = new();

    /// <summary>
    /// The replays whose operation was queued.
    /// </summary>
    public ConcurrentQueue<Guid> Replays { get; } = new();
}

public sealed class RecordingChannel
{
    public ConcurrentQueue<(NhAssistantAgentNotificationContext Context, IReadOnlyList<string> Recipients)> Deliveries { get; } = new();
}

public sealed class RecordingChannelAdapter(RecordingChannel channel) : INhAssistantAgentNotificationChannel
{
    public Task DeliverAsync(
        NhAssistantAgentNotificationContext context,
        IReadOnlyList<string> recipientActorIds,
        CancellationToken cancellationToken = default)
    {
        channel.Deliveries.Enqueue((context, recipientActorIds.ToArray()));
        return Task.CompletedTask;
    }
}
