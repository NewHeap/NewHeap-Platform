using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// An inline proposal that nobody decides expires on the application's clock, and its run ends as
/// the proposal's approval policy says, whichever comes first: the run's own background operation,
/// whose wait expires on that clock, or the maintenance job. Both orders end the run once, with the
/// same status, audit event, notification and live updates, on both relational providers.
/// </summary>
public sealed class AssistantInlineExpiryTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string AgentActorId = "assistant-agent:project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Member = "member-1";
    private const string InlinePolicy = "project-inline";
    private const string EscalationPolicy = "project-escalation";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    /// <summary>
    /// The run's operation wakes when its wait expires and expires the proposal itself; the
    /// maintenance job then finds nothing left to do.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer, NhAiApprovalExpiryAction.FailRun)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiApprovalExpiryAction.FailRun)]
    [InlineData(AssistantTestProvider.SqlServer, NhAiApprovalExpiryAction.Withdraw)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiApprovalExpiryAction.Withdraw)]
    [InlineData(AssistantTestProvider.SqlServer, NhAiApprovalExpiryAction.Escalate)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiApprovalExpiryAction.Escalate)]
    public async Task An_expired_inline_proposal_ends_its_run_by_its_policy_when_the_run_wakes_first(
        AssistantTestProvider provider,
        NhAiApprovalExpiryAction onExpiry)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, clock, onExpiry, projectId);
        var waiting = await StartWaitingRunAsync(host, projectId);
        clock.Advance(Lifetime);
        host.Services.GetRequiredService<RecordedLiveUpdates>().Clear();

        var result = await ExecuteAttemptAsync(host, waiting);
        Assert.Equal(NhAssistantApprovalStatuses.Expired, (await FindApprovalAsync(host, waiting.ApprovalId)).Status);
        await RunMaintenanceAsync(host);

        AssertOperationResult(result, onExpiry);
        await AssertRunEndedByPolicyAsync(host, waiting, onExpiry);
    }

    /// <summary>
    /// The maintenance job expires the proposal before the run's operation wakes. It leaves the run
    /// to that operation, which ends it by the same policy when its wait expires.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer, NhAiApprovalExpiryAction.FailRun)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiApprovalExpiryAction.FailRun)]
    [InlineData(AssistantTestProvider.SqlServer, NhAiApprovalExpiryAction.Withdraw)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiApprovalExpiryAction.Withdraw)]
    [InlineData(AssistantTestProvider.SqlServer, NhAiApprovalExpiryAction.Escalate)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiApprovalExpiryAction.Escalate)]
    public async Task An_expired_inline_proposal_ends_its_run_by_its_policy_when_the_maintenance_job_comes_first(
        AssistantTestProvider provider,
        NhAiApprovalExpiryAction onExpiry)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, clock, onExpiry, projectId);
        var waiting = await StartWaitingRunAsync(host, projectId);
        clock.Advance(Lifetime);
        host.Services.GetRequiredService<RecordedLiveUpdates>().Clear();

        await RunMaintenanceAsync(host);
        // An inline proposal never escalates, and the job does not end the run that waits for it.
        var expired = await FindApprovalAsync(host, waiting.ApprovalId);
        Assert.Equal(NhAssistantApprovalStatuses.Expired, expired.Status);
        Assert.Null(expired.EscalatedAt);
        Assert.Equal(NhAssistantRunStatuses.WaitingForApproval, (await FindRunAsync(host, waiting.RunId)).Status);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentRunEnded && evt.RunId == waiting.RunId);

        var result = await ExecuteAttemptAsync(host, waiting);

        AssertOperationResult(result, onExpiry);
        await AssertRunEndedByPolicyAsync(host, waiting, onExpiry);
    }

    private static void AssertOperationResult(TaskResult? result, NhAiApprovalExpiryAction onExpiry)
    {
        Assert.NotNull(result);
        if (onExpiry == NhAiApprovalExpiryAction.FailRun)
        {
            // The run failed by its policy: an expected outcome that ends the operation without a retry.
            Assert.False(result.Success);
            Assert.Contains(result.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ApprovalExpired);
        }
        else
        {
            Assert.True(result.Success);
        }
    }

    /// <summary>
    /// The run ended once, by its policy: FailRun fails it with the policy failure class, every other
    /// expiry action completes it without the proposal. Either way the waiting turn ended, nothing
    /// changed, and another round of the job or a late attempt of the operation changes nothing.
    /// </summary>
    private static async Task AssertRunEndedByPolicyAsync(AssistantTestHost host, WaitingRun waiting, NhAiApprovalExpiryAction onExpiry)
    {
        var failsRun = onExpiry == NhAiApprovalExpiryAction.FailRun;
        var expectedStatus = failsRun ? NhAssistantRunStatuses.Failed : NhAssistantRunStatuses.Completed;

        var run = await FindRunAsync(host, waiting.RunId);
        Assert.Equal(expectedStatus, run.Status);
        Assert.Equal(NhAssistantErrorCodes.ApprovalExpired, run.FailureCode);
        Assert.Equal(failsRun ? NhAssistantRunFailureClasses.Policy : null, run.FailureClass);
        var approval = await FindApprovalAsync(host, waiting.ApprovalId);
        Assert.Equal(NhAssistantApprovalStatuses.Expired, approval.Status);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(waiting.ConversationId, AgentActorId)).Status);
        Assert.Empty(host.Tools.StatusChanges);

        // Whatever comes later finds the run ended and leaves it as it is.
        await RunMaintenanceAsync(host);
        var late = await ExecuteAttemptAsync(host, waiting);
        Assert.NotNull(late);
        Assert.True(late.Success);
        var again = await FindRunAsync(host, waiting.RunId);
        Assert.Equal(expectedStatus, again.Status);
        Assert.Equal(run.EndedAt, again.EndedAt);
        Assert.Equal(run.ConcurrencyStamp, again.ConcurrencyStamp);

        var ended = Assert.Single(
            host.Business.Events,
            evt => evt.Kind == NhAssistantAuditEventKind.AgentRunEnded && evt.RunId == waiting.RunId);
        Assert.Equal(expectedStatus + ":" + NhAssistantErrorCodes.ApprovalExpired, ended.ResultCode);
        var failures = host.Services.GetRequiredService<RecordingChannel>().Deliveries
            .Where(delivery => delivery.Context.Kind == NhAssistantAgentNotificationKind.RunFailed && delivery.Context.RunId == waiting.RunId)
            .ToArray();
        Assert.Equal(failsRun ? 1 : 0, failures.Length);
        Assert.All(failures, failure => Assert.Equal(NhAssistantErrorCodes.ApprovalExpired, failure.Context.FailureCode));

        // The people on the subject hear once that the proposal expired, and that the run ended.
        var updates = host.Services.GetRequiredService<RecordedLiveUpdates>().Sent
            .Select(sent => sent.Event)
            .OfType<NhAssistantLiveAgentWorkChanged>()
            .ToArray();
        Assert.Single(updates, update => update.Reason == NhAssistantAgentWorkReasons.Proposal && update.ApprovalId == waiting.ApprovalId);
        Assert.Contains(updates, update => update.Reason == NhAssistantAgentWorkReasons.Run && update.RunId == waiting.RunId);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        AssistantTestClock clock,
        NhAiApprovalExpiryAction onExpiry,
        Guid projectId)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Closed" } }, "call-close");
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<RecordingOperations>().Service);
                services.AddSingleton<RecordingChannel>();
                services.AddSingleton<RecordedLiveUpdates>();
                services.Replace(ServiceDescriptor.Singleton<INhAssistantLiveUpdateTransport>(serviceProvider => serviceProvider.GetRequiredService<RecordedLiveUpdates>()));
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddAgentNotificationChannel<RecordingChannelAdapter>()
                .AddApprovalPolicy(EscalationPolicy, policy => policy
                    .Lifetime(TimeSpan.FromDays(1))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddApprovalPolicy(InlinePolicy, policy => policy
                    .Mode(NhAiApprovalMode.Inline)
                    .Lifetime(Lifetime)
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .OnExpiry(onExpiry, onExpiry == NhAiApprovalExpiryAction.Escalate ? EscalationPolicy : null))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy(InlinePolicy)));
    }

    private sealed record WaitingRun(Guid RunId, Guid ConversationId, Guid ApprovalId, AssistantTestOperation Operation);

    /// <summary>
    /// A member starts the agent by hand. Its operation's first attempt proposes to close the project
    /// inline and suspends until the decision or the proposal's expiry.
    /// </summary>
    private static async Task<WaitingRun> StartWaitingRunAsync(AssistantTestHost host, Guid projectId)
    {
        NhAssistantTriggerReceipt started;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            started = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>().StartNowAsync(
                new NhAssistantStartClearance(AgentId, new NhAiSubjectRef("project", projectId.ToString()), Member),
                "Close the project.",
                CancellationToken.None);
        }
        Assert.Equal(NhAssistantTriggerOutcomes.Started, started.Outcome);

        var operation = new AssistantTestOperation(host.Services.GetRequiredService<TimeProvider>());
        var runId = started.RunId!.Value;
        var suspended = await ExecuteAsync(host, runId, operation);
        Assert.Null(suspended);
        Assert.Equal(NhAssistantRunStatuses.WaitingForApproval, (await FindRunAsync(host, runId)).Status);
        var approval = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(runId, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, approval.Status);
        Assert.Equal(NhAssistantApprovalModes.Inline, approval.Mode);
        Assert.Equal(host.Services.GetRequiredService<TimeProvider>().GetUtcNow().Add(Lifetime), approval.ExpiresAt);
        return new WaitingRun(runId, started.ConversationId!.Value, approval.Id, operation);
    }

    /// <summary>
    /// Runs the next attempt of the run's operation, as the runner does when the operation wakes.
    /// </summary>
    private static Task<TaskResult?> ExecuteAttemptAsync(AssistantTestHost host, WaitingRun waiting)
    {
        waiting.Operation.NextAttempt();
        return ExecuteAsync(host, waiting.RunId, waiting.Operation);
    }

    /// <summary>
    /// Executes one attempt of the run's operation; <see langword="null"/> when it suspended.
    /// </summary>
    private static async Task<TaskResult?> ExecuteAsync(AssistantTestHost host, Guid runId, AssistantTestOperation operation)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        try
        {
            return await handler.ExecuteAsync(new NhAssistantAgentRunRequest(runId), operation, CancellationToken.None);
        }
        catch (AssistantTestOperation.Suspended)
        {
            return null;
        }
    }

    private static async Task RunMaintenanceAsync(AssistantTestHost host)
    {
        await host.Services.GetServices<IHostedService>()
            .OfType<NhAssistantRunMaintenance>()
            .Single()
            .RunOnceAsync(CancellationToken.None);
    }

    private static async Task<Entities.AssistantRun> FindRunAsync(AssistantTestHost host, Guid runId)
    {
        return (await host.Services.GetRequiredService<NhAssistantRunStore>().FindRunAsync(runId, CancellationToken.None))!;
    }

    private static async Task<Entities.AssistantApproval> FindApprovalAsync(AssistantTestHost host, Guid approvalId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
    }
}
