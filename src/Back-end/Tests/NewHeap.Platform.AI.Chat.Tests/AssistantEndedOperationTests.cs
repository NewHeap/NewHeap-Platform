using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
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
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A run that waits inline for a decision has its background operation suspended in a signal wait.
/// When that operation ends without the run's handler, because an operator cancelled it,
/// reconciliation failed it as dispatch-stalled or it failed for good in another way, the handler
/// never runs again. The maintenance job ends such a run once, through the run's own end path,
/// closes its inline proposal and frees its thread, so the agent's next run starts. A run whose
/// operation still waits is left alone. The operations live in the application's own database and
/// are driven by the platform's runner, on both relational providers.
/// </summary>
public sealed class AssistantEndedOperationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Member = "member-1";
    private const string InlinePolicy = "project-inline";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    /// <summary>
    /// How long an operation has to be terminal, on the system clock, before the maintenance job
    /// ends its run: the background operations' stale timeout, two minutes by default.
    /// </summary>
    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(2);

    private static readonly RelationalTestPool SqlServerApplications = CreateApplicationPool(AssistantTestProvider.SqlServer);
    private static readonly RelationalTestPool PostgreSqlApplications = CreateApplicationPool(AssistantTestProvider.PostgreSql);

    /// <summary>
    /// How the waiting run's operation ended without its handler.
    /// </summary>
    public enum OperationEnd
    {
        /// <summary>An operator cancelled it in the background-operation administration.</summary>
        OperatorCancelled,

        /// <summary>Reconciliation failed it because no worker started it.</summary>
        DispatchStalled,

        /// <summary>A worker failed it before the handler ran: its payload schema is not the registered one.</summary>
        PayloadSchemaFailed
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer, OperationEnd.OperatorCancelled)]
    [InlineData(AssistantTestProvider.PostgreSql, OperationEnd.OperatorCancelled)]
    [InlineData(AssistantTestProvider.SqlServer, OperationEnd.DispatchStalled)]
    [InlineData(AssistantTestProvider.PostgreSql, OperationEnd.DispatchStalled)]
    [InlineData(AssistantTestProvider.SqlServer, OperationEnd.PayloadSchemaFailed)]
    [InlineData(AssistantTestProvider.PostgreSql, OperationEnd.PayloadSchemaFailed)]
    public async Task A_run_that_waits_inline_ends_once_when_its_operation_ended_without_it_and_frees_its_thread(
        AssistantTestProvider provider,
        OperationEnd end)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        var projectId = Guid.NewGuid();
        var model = new SwitchingChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Closed" } }, "call-close"));
        await using var host = await CreateHostAsync(provider, clock, model);
        var waiting = await StartWaitingRunAsync(host, projectId);

        await EndOperationAsync(host, waiting.OperationId, end);
        var expected = Expected(end);
        Assert.Equal(expected.OperationStatus, (await FindOperationAsync(host, waiting.OperationId)).Status);

        // Ended a moment ago: the job waits for the stale timeout of real time, the liveness of
        // background operations, also when the application's clock jumps ahead.
        clock.Advance(TimeSpan.FromHours(1));
        await RunMaintenanceAsync(host);
        var stillWaiting = await FindRunAsync(host, waiting.RunId);
        Assert.True(
            stillWaiting.Status == NhAssistantRunStatuses.WaitingForApproval,
            $"Run {waiting.RunId} should still wait for its approval right after its operation ended, but is {stillWaiting.Status} ({stillWaiting.FailureCode}).");
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await FindApprovalAsync(host, waiting.ApprovalId)).Status);

        // Once the operation has been terminal for the stale timeout, the job ends the run.
        await BackdateOperationEndAsync(host, waiting.OperationId, Settled + TimeSpan.FromMinutes(1));
        host.Services.GetRequiredService<RecordedLiveUpdates>().Clear();
        await RunMaintenanceAsync(host);

        var run = await FindRunAsync(host, waiting.RunId);
        Assert.True(
            run.Status == expected.Status && run.FailureClass == expected.FailureClass && run.FailureCode == expected.Code,
            $"Run {waiting.RunId} ended {run.Status}/{run.FailureClass}/{run.FailureCode}; expected {expected.Status}/{expected.FailureClass}/{expected.Code}.");
        Assert.NotNull(run.EndedAt);
        Assert.Contains("(" + expected.Code + ")", run.Summary);
        // The inline proposal can no longer be decided; its lifetime had not passed.
        var approval = await FindApprovalAsync(host, waiting.ApprovalId);
        Assert.Equal(NhAssistantApprovalStatuses.Withdrawn, approval.Status);
        Assert.Contains("(" + NhAssistantApprovalStatuses.Withdrawn + ")", run.Summary);
        // The thread no longer waits for the proposal.
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(waiting.ConversationId, "assistant-agent:" + AgentId)).Status);
        Assert.Empty(host.Tools.StatusChanges);

        AssertEndedOnce(host, waiting, expected);
        var updates = host.Services.GetRequiredService<RecordedLiveUpdates>().Sent
            .Select(sent => sent.Event)
            .OfType<NhAssistantLiveAgentWorkChanged>()
            .ToArray();
        Assert.Single(updates, update => update.Reason == NhAssistantAgentWorkReasons.Proposal && update.ApprovalId == waiting.ApprovalId);
        Assert.Contains(updates, update => update.Reason == NhAssistantAgentWorkReasons.Run && update.RunId == waiting.RunId);

        // Further rounds change nothing more.
        await RunMaintenanceAsync(host);
        await RunMaintenanceAsync(host);
        var again = await FindRunAsync(host, waiting.RunId);
        Assert.Equal(run.ConcurrencyStamp, again.ConcurrencyStamp);
        Assert.Equal(run.EndedAt, again.EndedAt);
        Assert.Equal(approval.ConcurrencyStamp, (await FindApprovalAsync(host, waiting.ApprovalId)).ConcurrencyStamp);
        AssertEndedOnce(host, waiting, expected);

        // The agent's next run starts in the freed thread and completes its turn there.
        model.Current = new NhAiScriptedChatClient().RespondWithText("The project needs nothing else today.");
        var next = await StartAsync(host, projectId);
        Assert.True(
            next.Outcome == NhAssistantTriggerOutcomes.Started,
            $"The next trigger should start a run, but its outcome is {next.Outcome} ({next.ReasonCode}).");
        Assert.NotEqual(waiting.RunId, next.RunId);
        var nextOperation = await ExecuteNextAttemptAsync(host, (await FindRunAsync(host, next.RunId!.Value)).BackgroundOperationId!.Value);
        var nextRun = await FindRunAsync(host, next.RunId!.Value);
        Assert.True(
            nextRun.Status == NhAssistantRunStatuses.Completed,
            $"The next run {nextRun.Id} should complete in the freed thread, but is {nextRun.Status} ({nextRun.FailureCode}); its operation is {nextOperation.Status} ({nextOperation.FailureCode}).");
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(waiting.ConversationId, "assistant-agent:" + AgentId)).Status);
    }

    /// <summary>
    /// A participant cancels the run while it waits inline: the run ends cancelled at once, its
    /// proposal is withdrawn and its thread no longer waits, so the agent's next run completes. Once
    /// the run's operation ended too, the maintenance job leaves the cancelled run as it is.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_who_cancels_a_run_that_waits_inline_frees_its_thread(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        var projectId = Guid.NewGuid();
        var model = new SwitchingChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Closed" } }, "call-close"));
        await using var host = await CreateHostAsync(provider, clock, model);
        var waiting = await StartWaitingRunAsync(host, projectId);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var run = await FindRunAsync(host, waiting.RunId);
            var thread = (await host.Services.GetRequiredService<NhAssistantRunStore>().FindThreadByConversationAsync(run.ConversationId, CancellationToken.None))!;
            await scope.ServiceProvider.GetRequiredService<NhAssistantRunCancellation>().CancelAsync(
                run,
                Member,
                NhAssistantAgentTriggers.OwnerActor(thread, run.AccountableOwnerId),
                CancellationToken.None);
        }

        var cancelled = await FindRunAsync(host, waiting.RunId);
        Assert.True(
            cancelled.Status == NhAssistantRunStatuses.Cancelled && cancelled.FailureClass == NhAssistantRunFailureClasses.Cancelled,
            $"Run {waiting.RunId} should be cancelled, but is {cancelled.Status}/{cancelled.FailureClass}/{cancelled.FailureCode}.");
        Assert.Equal(NhAssistantApprovalStatuses.Withdrawn, (await FindApprovalAsync(host, waiting.ApprovalId)).Status);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(waiting.ConversationId, "assistant-agent:" + AgentId)).Status);

        // The operation's cancellation completes when a worker takes the request; long after that,
        // the maintenance job finds the run ended and changes nothing.
        await RunWorkerAsync(host, waiting.OperationId);
        Assert.Equal(NhBackgroundOperationStatus.Cancelled, (await FindOperationAsync(host, waiting.OperationId)).Status);
        await BackdateOperationEndAsync(host, waiting.OperationId, Settled + TimeSpan.FromMinutes(1));
        await RunMaintenanceAsync(host);
        Assert.Equal(cancelled.ConcurrencyStamp, (await FindRunAsync(host, waiting.RunId)).ConcurrencyStamp);

        model.Current = new NhAiScriptedChatClient().RespondWithText("The project needs nothing else today.");
        var next = await StartAsync(host, projectId);
        Assert.True(
            next.Outcome == NhAssistantTriggerOutcomes.Started,
            $"The next trigger should start a run, but its outcome is {next.Outcome} ({next.ReasonCode}).");
        var nextOperation = await ExecuteNextAttemptAsync(host, (await FindRunAsync(host, next.RunId!.Value)).BackgroundOperationId!.Value);
        var nextRun = await FindRunAsync(host, next.RunId!.Value);
        Assert.True(
            nextRun.Status == NhAssistantRunStatuses.Completed,
            $"The next run {nextRun.Id} should complete in the freed thread, but is {nextRun.Status} ({nextRun.FailureCode}); its operation is {nextOperation.Status} ({nextOperation.FailureCode}).");
    }

    /// <summary>
    /// A run whose operation still waits in its signal wait is healthy, however long it waits and
    /// however far the application's clock moves before the proposal expires.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_whose_operation_still_waits_for_the_decision_is_left_alone(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        var projectId = Guid.NewGuid();
        var model = new SwitchingChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Closed" } }, "call-close"));
        await using var host = await CreateHostAsync(provider, clock, model);
        var waiting = await StartWaitingRunAsync(host, projectId);
        var before = await FindRunAsync(host, waiting.RunId);

        // Nothing touched the operation for an hour of real time, and three of the proposal's four
        // hours passed on the application's clock.
        await BackdateOperationEndAsync(host, waiting.OperationId, TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromHours(3));
        for (var round = 0; round < 3; round++)
        {
            await RunMaintenanceAsync(host);
        }

        var run = await FindRunAsync(host, waiting.RunId);
        Assert.True(
            run.Status == NhAssistantRunStatuses.WaitingForApproval,
            $"Run {waiting.RunId} should still wait for its approval, but is {run.Status} ({run.FailureCode}).");
        Assert.Equal(before.ConcurrencyStamp, run.ConcurrencyStamp);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await FindApprovalAsync(host, waiting.ApprovalId)).Status);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, (await host.ReloadAsync(waiting.ConversationId, "assistant-agent:" + AgentId)).Status);
        Assert.Equal(NhBackgroundOperationStatus.WaitingForSignal, (await FindOperationAsync(host, waiting.OperationId)).Status);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentRunEnded && evt.RunId == waiting.RunId);
    }

    /// <summary>
    /// The job reads open runs with an operation a bounded page at a time, oldest first, and
    /// continues after the last run of the previous page, so it comes round to every open run
    /// however many wait; ended runs and runs without an operation are not read.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_job_reads_open_runs_with_an_operation_a_page_at_a_time(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        await using var host = await CreateHostAsync(provider, clock, new NhAiScriptedChatClient());
        var conversation = await host.CreateConversationAsync();
        var store = host.Services.GetRequiredService<NhAssistantRunStore>();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var open = new List<Guid>();
        string[] statuses =
        [
            NhAssistantRunStatuses.Queued,
            NhAssistantRunStatuses.Running,
            NhAssistantRunStatuses.WaitingForApproval,
            NhAssistantRunStatuses.Completed,
            NhAssistantRunStatuses.WaitingForApproval,
            NhAssistantRunStatuses.Failed,
            NhAssistantRunStatuses.Running
        ];
        for (var index = 0; index < statuses.Length; index++)
        {
            var run = new Entities.AssistantRun
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                // The fifth run has no operation yet.
                BackgroundOperationId = index == 4 ? null : Guid.NewGuid(),
                TriggerId = NhAssistantTriggerIds.Manual,
                AccountableOwnerId = Member,
                Status = statuses[index],
                AgentVersion = 1,
                CreatedAt = start.AddMinutes(index),
                ConcurrencyStamp = Guid.NewGuid()
            };
            await store.AddRunAsync(run, CancellationToken.None);
            if (!NhAssistantRunStatuses.IsTerminal(run.Status) && run.BackgroundOperationId is not null)
            {
                open.Add(run.Id);
            }
        }

        var first = await store.GetActiveRunsWithOperationAsync(null, 2, CancellationToken.None);
        var second = await store.GetActiveRunsWithOperationAsync(first[^1].CreatedAt, 2, CancellationToken.None);
        var third = await store.GetActiveRunsWithOperationAsync(second[^1].CreatedAt, 2, CancellationToken.None);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Empty(third);
        Assert.Equal(open, first.Concat(second).Select(run => run.Id).ToArray());
    }

    private sealed record ExpectedEnd(
        NhBackgroundOperationStatus OperationStatus,
        string Status,
        string FailureClass,
        string Code,
        int RunFailedNotifications);

    private static ExpectedEnd Expected(OperationEnd end)
    {
        return end switch
        {
            OperationEnd.OperatorCancelled => new ExpectedEnd(
                NhBackgroundOperationStatus.Cancelled,
                NhAssistantRunStatuses.Cancelled,
                NhAssistantRunFailureClasses.Cancelled,
                NhAssistantErrorCodes.RunOperationCancelled,
                0),
            OperationEnd.DispatchStalled => new ExpectedEnd(
                NhBackgroundOperationStatus.Failed,
                NhAssistantRunStatuses.Failed,
                NhAssistantRunFailureClasses.Internal,
                NhAssistantErrorCodes.RunOperationStalled,
                1),
            _ => new ExpectedEnd(
                NhBackgroundOperationStatus.Failed,
                NhAssistantRunStatuses.Failed,
                NhAssistantRunFailureClasses.Internal,
                NhAssistantErrorCodes.RunOperationEnded,
                1)
        };
    }

    /// <summary>
    /// The run's end was recorded once: one <c>AgentRunEnded</c> event, and a <c>RunFailed</c>
    /// notification only for a failed run.
    /// </summary>
    private static void AssertEndedOnce(AssistantTestHost host, WaitingRun waiting, ExpectedEnd expected)
    {
        var ended = host.Business.Events
            .Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentRunEnded && evt.RunId == waiting.RunId)
            .ToArray();
        Assert.True(
            ended.Length == 1,
            $"Run {waiting.RunId} should have one AgentRunEnded event, but has {ended.Length}: {string.Join(", ", ended.Select(evt => evt.ResultCode))}.");
        Assert.Equal(expected.Status + ":" + expected.Code, ended[0].ResultCode);
        var failures = host.Services.GetRequiredService<RecordingChannel>().Deliveries
            .Where(delivery => delivery.Context.Kind == NhAssistantAgentNotificationKind.RunFailed && delivery.Context.RunId == waiting.RunId)
            .ToArray();
        Assert.Equal(expected.RunFailedNotifications, failures.Length);
        Assert.All(failures, failure => Assert.Equal(expected.Code, failure.Context.FailureCode));
    }

    private sealed record WaitingRun(Guid RunId, Guid ConversationId, Guid ApprovalId, Guid OperationId);

    /// <summary>
    /// A member starts the agent by hand. A worker runs the operation: the turn proposes to close the
    /// project inline, and the operation suspends in its signal wait until the decision or the
    /// proposal's expiry.
    /// </summary>
    private static async Task<WaitingRun> StartWaitingRunAsync(AssistantTestHost host, Guid projectId)
    {
        var started = await StartAsync(host, projectId);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, started.Outcome);
        var runId = started.RunId!.Value;
        var operationId = (await FindRunAsync(host, runId)).BackgroundOperationId!.Value;

        var operation = await ExecuteNextAttemptAsync(host, operationId);
        Assert.True(
            operation.Status == NhBackgroundOperationStatus.WaitingForSignal,
            $"Operation {operationId} should wait for the decision, but is {operation.Status} ({operation.FailureCode}).");
        var run = await FindRunAsync(host, runId);
        Assert.Equal(NhAssistantRunStatuses.WaitingForApproval, run.Status);
        Assert.NotNull(run.TurnId);
        var approval = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(runId, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, approval.Status);
        Assert.Equal(NhAssistantApprovalModes.Inline, approval.Mode);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, (await host.ReloadAsync(started.ConversationId!.Value, "assistant-agent:" + AgentId)).Status);
        return new WaitingRun(runId, started.ConversationId!.Value, approval.Id, operationId);
    }

    private static async Task<NhAssistantTriggerReceipt> StartAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>().StartNowAsync(
            AgentId,
            new NhAiSubjectRef("project", projectId.ToString()),
            Member,
            "Close the project.",
            CancellationToken.None);
    }

    /// <summary>
    /// Ends the waiting run's operation the way the platform does, outside the run's handler.
    /// </summary>
    private static async Task EndOperationAsync(AssistantTestHost host, Guid operationId, OperationEnd end)
    {
        switch (end)
        {
            case OperationEnd.OperatorCancelled:
                // An operator cancels the operation; the worker the dispatcher hands the request to
                // completes the cancellation before it starts anything.
                await using (var scope = host.Services.CreateAsyncScope())
                {
                    var cancelled = await scope.ServiceProvider.GetRequiredService<INhBackgroundOperationAdministrationService>()
                        .RequestCancellationAsync(operationId, Guid.NewGuid(), null, CancellationToken.None);
                    Assert.True(cancelled.Success, string.Join("; ", cancelled.AllErrorMessages.Select(error => error.ToString())));
                }

                await RunWorkerAsync(host, operationId);
                break;

            case OperationEnd.DispatchStalled:
                // Reconciliation recovered the dispatch as often as it may and then failed it.
                await UpdateOperationAsync(host, operationId, operations => operations.ExecuteUpdateAsync(setters => setters
                    .SetProperty(operation => operation.Status, NhBackgroundOperationStatus.Failed)
                    .SetProperty(operation => operation.FailureCode, "dispatch-stalled")
                    .SetProperty(operation => operation.FailureMessageKey, "background-operation.operator-recovery-required")
                    .SetProperty(operation => operation.NextDispatchAt, (DateTimeOffset?)null)
                    .SetProperty(operation => operation.CompletedAt, DateTimeOffset.UtcNow)
                    .SetProperty(operation => operation.LastModifiedDateTime, DateTimeOffset.UtcNow)));
                break;

            default:
                // The operation woke after a deployment changed its payload schema: the worker fails
                // it before the handler runs.
                await UpdateOperationAsync(host, operationId, operations => operations.ExecuteUpdateAsync(setters => setters
                    .SetProperty(operation => operation.PayloadSchemaVersion, 99)
                    .SetProperty(operation => operation.Status, NhBackgroundOperationStatus.Queued)));
                await RunWorkerAsync(host, operationId);
                Assert.Equal("unsupported-payload-schema", (await FindOperationAsync(host, operationId)).FailureCode);
                break;
        }
    }

    /// <summary>
    /// Hands the operation to a worker as the dispatcher does and runs that worker's attempt.
    /// </summary>
    private static async Task<NhBackgroundOperation> ExecuteNextAttemptAsync(AssistantTestHost host, Guid operationId)
    {
        await UpdateOperationAsync(host, operationId, operations => operations
            .Where(operation => operation.Status == NhBackgroundOperationStatus.PendingDispatch)
            .ExecuteUpdateAsync(setters => setters.SetProperty(operation => operation.Status, NhBackgroundOperationStatus.Queued)));
        await RunWorkerAsync(host, operationId);
        return await FindOperationAsync(host, operationId);
    }

    private static async Task RunWorkerAsync(AssistantTestHost host, Guid operationId)
    {
        var generation = (await FindOperationAsync(host, operationId)).DispatchGeneration;
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhBackgroundOperationRunner>().RunAsync(operationId, generation);
    }

    /// <summary>
    /// Moves the time the operation ended, and its last change, back on the system clock.
    /// </summary>
    private static Task BackdateOperationEndAsync(AssistantTestHost host, Guid operationId, TimeSpan by)
    {
        var before = DateTimeOffset.UtcNow - by;
        return UpdateOperationAsync(host, operationId, operations => operations.ExecuteUpdateAsync(setters => setters
            .SetProperty(operation => operation.CompletedAt, operation => operation.CompletedAt == null ? null : (DateTimeOffset?)before)
            .SetProperty(operation => operation.LastModifiedDateTime, before)));
    }

    private static async Task UpdateOperationAsync(
        AssistantTestHost host,
        Guid operationId,
        Func<IQueryable<NhBackgroundOperation>, Task<int>> update)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<OrderDbContext>().Set<NhBackgroundOperation>()
            .Where(operation => operation.Id == operationId);
        await update(operations);
    }

    private static async Task<NhBackgroundOperation> FindOperationAsync(AssistantTestHost host, Guid operationId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Set<NhBackgroundOperation>()
            .AsNoTracking()
            .SingleAsync(operation => operation.Id == operationId);
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

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, AssistantTestClock clock, IChatClient model)
    {
        var pool = provider == AssistantTestProvider.SqlServer ? SqlServerApplications : PostgreSqlApplications;
        var application = await pool.LeaseAsync();
        try
        {
            var host = await AssistantTestHost.CreateAsync(
                database,
                provider,
                model,
                configure: services =>
                {
                    services.AddSingleton(_ => application);
                    services.AddSingleton<TimeProvider>(clock);
                    services.AddSingleton(new TestSubjects());
                    services.AddSingleton<RecordingChannel>();
                    services.AddSingleton<RecordedLiveUpdates>();
                    services.Replace(ServiceDescriptor.Singleton<INhAssistantLiveUpdateTransport>(serviceProvider => serviceProvider.GetRequiredService<RecordedLiveUpdates>()));
                    services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                    services.AddDbContext<OrderDbContext>(options => UseApplicationDatabase(options, provider, application.ConnectionString));
                    services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
                    services.AddNewHeapBackgroundOperations<OrderDbContext>(operations =>
                    {
                        // The test is the dispatcher and the worker; the maintenance job runs by hand.
                        operations.Options.DispatchWorkersEnabled = false;
                        operations.Options.LiveUpdatesEnabled = false;
                        operations.Options.ReconciliationEnabled = false;
                        operations.Options.CleanupEnabled = false;
                        operations.Options.UserNotificationProjectionEnabled = false;
                        operations.AddAssistantAgentRuns();
                    });
                },
                assistantBuilder: assistant => assistant
                    .AddSubjectType<TestSubjectResolver>()
                    .AddAgentNotificationChannel<RecordingChannelAdapter>()
                    .AddApprovalPolicy(InlinePolicy, policy => policy
                        .Mode(NhAiApprovalMode.Inline)
                        .Lifetime(Lifetime)
                        .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                        .OnExpiry(NhAiApprovalExpiryAction.FailRun))
                    .ConfigureAgent(AgentId, agent => agent
                        .Grant("projects.*")
                        .UseApprovalPolicy(InlinePolicy)));

            // The host's services own the lease from here on: disposing the host returns the
            // application's database to its pool.
            host.Services.GetRequiredService<RelationalTestLease>();
            return host;
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    private static RelationalTestPool CreateApplicationPool(AssistantTestProvider provider)
    {
        return new RelationalTestPool(
            AssistantDatabaseFixture.ToRelational(provider),
            "ended_operations",
            RelationalTestPoolLayout.DatabasePerSlot,
            async slot =>
            {
                var options = new DbContextOptionsBuilder<OrderDbContext>();
                UseApplicationDatabase(options, provider, slot.ConnectionString);
                await using var context = new OrderDbContext(options.Options);
                await context.Database.EnsureCreatedAsync();
            });
    }

    private static void UseApplicationDatabase(DbContextOptionsBuilder options, AssistantTestProvider provider, string connectionString)
    {
        if (provider == AssistantTestProvider.SqlServer)
        {
            options.UseNewHeapSqlServer(connectionString);
        }
        else
        {
            options.UseNewHeapPostgreSql(connectionString);
        }
    }

    /// <summary>
    /// The model of the host, which a test replaces between runs: each run gets a script of its own.
    /// </summary>
    private sealed class SwitchingChatClient(IChatClient initial) : IChatClient
    {
        public IChatClient Current { get; set; } = initial;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Current.GetResponseAsync(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Current.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : Current.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoOpScheduler : INhBackgroundOperationScheduler
    {
        public Task<NhBackgroundOperationScheduleResult> EnqueueAsync(
            Guid operationId,
            int dispatchGeneration,
            string queue,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NhBackgroundOperationScheduleResult("noop"));
        }

        public Task<bool> DeleteAsync(string schedulerJobId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<NhBackgroundOperationExecutionState?> GetStateAsync(string schedulerJobId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<NhBackgroundOperationExecutionState?>(null);
        }
    }
}
