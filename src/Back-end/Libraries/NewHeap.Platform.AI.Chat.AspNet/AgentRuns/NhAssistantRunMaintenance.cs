using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Lets the people of a subject see, cancel and retry the agent operations that work on it. An
/// operation of an agent's work for the whole application is for people who pass the agent's view
/// policy as well, as its thread is; one whose agent is not known is left to its owner. An inline
/// approval run is woken only by the person who just recorded the decision.
/// </summary>
internal sealed class NhAssistantBackgroundOperationAccess(
    NhAssistantSubjectAccessService subjects,
    NhAssistantRunStore runs) : INhBackgroundOperationAccess
{
    public async ValueTask<bool> IsAllowedAsync(
        NhBackgroundOperationAccessTarget operation,
        NhBackgroundOperationActor actor,
        NhBackgroundOperationAccessAction action,
        CancellationToken cancellationToken = default)
    {
        if (!NhAssistantAgentRunOperationTypes.IsAgentOperation(operation.OperationType)
            || actor.UserId is not { } userId
            || !NhAiSubjectRef.IsValidType(operation.DomainObjectType)
            || string.IsNullOrWhiteSpace(operation.DomainObjectId)
            || operation.DomainObjectId.Length > NhAiSubjectRef.MaxIdLength)
        {
            return false;
        }

        var actorId = userId.ToString();
        if (action == NhBackgroundOperationAccessAction.Signal)
        {
            return await runs.HasRecentInlineDecisionAsync(operation.OperationId, actorId, cancellationToken);
        }

        var subject = new NhAiSubjectRef(operation.DomainObjectType!, operation.DomainObjectId);
        NhAiSubjectAccess access;
        if (NhAiSystemSubjects.IsSystem(subject))
        {
            var thread = await runs.FindThreadOfOperationAsync(operation.OperationId, cancellationToken);
            if (thread is null
                || !string.Equals(thread.SubjectType, subject.Type, StringComparison.Ordinal)
                || !string.Equals(thread.SubjectId, subject.Id, StringComparison.Ordinal))
            {
                return false;
            }
            access = await subjects.ResolveForAgentAsync(subject, thread.AgentId, actorId, cancellationToken);
        }
        else
        {
            access = await subjects.ResolveAsync(subject, actorId, cancellationToken);
        }
        var required = action switch
        {
            NhBackgroundOperationAccessAction.View => NhAiSubjectRole.Reader,
            NhBackgroundOperationAccessAction.Cancel => NhAiSubjectRole.Participant,
            _ => NhAiSubjectRole.Approver
        };
        return access.Has(required);
    }
}

/// <summary>
/// Keeps agent proposals and runs moving without a person: expires and escalates proposals,
/// queues approved executions that have none, marks interrupted non-idempotent executions as
/// <c>execution-unknown</c> and cancels runs whose trigger transaction rolled back. Once an hour it
/// removes board visits older than <see cref="NhAssistantLimits.BoardVisitRetention"/> and those of
/// groups that are no longer registered. Every change is a compare-and-set update or a conditional
/// delete, so several instances can run it at the same time. A proposal's expiry, escalation,
/// reminders and decision follow the application's clock (a registered <see cref="TimeProvider"/>,
/// otherwise the system clock); runs and board visits keep the system clock.
/// </summary>
internal sealed class NhAssistantRunMaintenance(
    IServiceScopeFactory scopeFactory,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantRunMaintenance> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private const int BatchSize = 100;

    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// How often board visits are cleaned up; they change once per person, group and visit.
    /// </summary>
    internal static readonly TimeSpan BoardVisitCleanupInterval = TimeSpan.FromHours(1);

    private DateTimeOffset _boardVisitsCleanedAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(registration.Limits.MaintenanceInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning(exception, "Assistant run maintenance failed; it runs again at the next interval.");
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (registration.AgentRuns.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var runs = services.GetRequiredService<NhAssistantRunStore>();
        var policies = services.GetRequiredService<NhAssistantApprovalPolicies>();
        var decisions = services.GetRequiredService<NhAssistantProposalDecisions>();
        var operations = services.GetService<IRepository<NhBackgroundOperation>>();

        var notifier = services.GetRequiredService<NhAssistantAgentNotifier>();
        var events = services.GetRequiredService<NhAssistantAgentEvents>();
        var workUpdates = services.GetRequiredService<NhAssistantAgentWorkUpdates>();
        // A proposal's expiry, reminders and decisions follow the application's clock; the board
        // visits below keep the system clock.
        var proposalTime = _applicationClock.GetUtcNow();
        await ExpireAsync(runs, policies, notifier, workUpdates, proposalTime, cancellationToken);
        if (services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<NhAssistantOptions>>().CurrentValue.Enabled)
        {
            // With the assistant disabled nobody is reminded and nothing is queued for execution.
            await RemindAsync(runs, notifier, proposalTime, cancellationToken);
            await QueueMissingExecutionsAsync(runs, decisions, proposalTime, cancellationToken);
        }
        if (operations is not null)
        {
            await DetectInterruptedExecutionsAsync(runs, operations, notifier, events, workUpdates, cancellationToken);
            await RecoverOrphanedRunsAsync(runs, operations, services.GetRequiredService<NhAssistantAgentTriggers>(), cancellationToken);
        }
        var now = DateTimeOffset.UtcNow;
        if (now - _boardVisitsCleanedAt >= BoardVisitCleanupInterval)
        {
            await RemoveStaleBoardVisitsAsync(services.GetRequiredService<NhAssistantBoardStore>(), now, cancellationToken);
            _boardVisitsCleanedAt = now;
        }
    }

    /// <summary>
    /// Removes board visits older than the retention and those of groups no longer registered.
    /// </summary>
    internal async Task<int> RemoveStaleBoardVisitsAsync(
        NhAssistantBoardStore boards,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var removed = await boards.RemoveStaleVisitsAsync(
            now - registration.Limits.BoardVisitRetention,
            registration.AgentGroups.Keys.ToArray(),
            cancellationToken);
        if (removed > 0)
        {
            logger.LogInformation("Assistant run maintenance removed {Count} board visits.", removed);
        }
        return removed;
    }

    /// <summary>
    /// Expires the pending proposals of agent threads whose expiry <paramref name="now"/> of the
    /// application's clock reached, or escalates a deferred one once; an escalation starts the
    /// escalation policy's lifetime and reminders at that time. A deferred proposal whose policy
    /// says <see cref="NhAiApprovalExpiryAction.FailRun"/> fails its run; a run that waits inline
    /// for its proposal is ended by its own operation, by the same policy.
    /// </summary>
    private static async Task ExpireAsync(
        NhAssistantRunStore runs,
        NhAssistantApprovalPolicies policies,
        NhAssistantAgentNotifier notifier,
        NhAssistantAgentWorkUpdates workUpdates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var approval in await runs.GetPendingThreadApprovalsAsync(now, BatchSize, cancellationToken))
        {
            var policy = approval.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
            var escalation = policy is { OnExpiry: NhAiApprovalExpiryAction.Escalate, EscalationPolicyId: { } escalationId }
                ? policies.Find(escalationId)
                : null;
            // An inline run waits on a fixed expiry, so only deferred proposals escalate.
            if (escalation is not null
                && approval.EscalatedAt is null
                && approval.Mode == NhAssistantApprovalModes.Deferred)
            {
                var escalated = await runs.TryTransitionApprovalAsync(
                    approval.Id,
                    [NhAssistantApprovalStatuses.Pending],
                    NhAssistantApprovalStatuses.Pending,
                    new NhAssistantApprovalTransition
                    {
                        ExpiresAt = now.Add(escalation.Lifetime),
                        PolicyJson = NhAssistantApprovalPolicySnapshot.Serialize(escalation),
                        EscalatedAt = now,
                        NextReminderAt = NhAssistantApprovalReminders.Next(escalation, now, 0, now.Add(escalation.Lifetime)),
                        ResetReminders = true
                    },
                    cancellationToken);
                if (escalated && await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken) is { } escalatedThread)
                {
                    await workUpdates.PublishAsync(
                        approval.ConversationId,
                        NhAssistantAgentWorkReasons.Proposal,
                        approval.Id,
                        approval.RunId,
                        cancellationToken);
                    await notifier.NotifyAsync(
                        NhAssistantAgentNotificationKind.ProposalEscalated,
                        escalatedThread,
                        approval.RunId,
                        approval,
                        approval.InitiatorActorId,
                        shadow: false,
                        cancellationToken);
                }
                continue;
            }

            var expired = await runs.TryTransitionApprovalAsync(
                approval.Id,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantApprovalStatuses.Expired,
                NhAssistantApprovalTransition.None,
                cancellationToken);
            if (expired)
            {
                await workUpdates.PublishAsync(
                    approval.ConversationId,
                    NhAssistantAgentWorkReasons.Proposal,
                    approval.Id,
                    approval.RunId,
                    cancellationToken);
            }
            // A run that waits inline for the proposal ends through its own operation, whose wait
            // expires at the same time on the same clock: it applies the policy's OnExpiry there,
            // also when this job expired the proposal first. A deferred proposal's run did not
            // wait for it, so its policy fails that run here.
            if (expired
                && policy?.OnExpiry == NhAiApprovalExpiryAction.FailRun
                && approval.Mode == NhAssistantApprovalModes.Deferred
                && approval.RunId is { } runId)
            {
                await runs.TrySetRunStatusAsync(
                    runId,
                    [NhAssistantRunStatuses.Queued, NhAssistantRunStatuses.Running, NhAssistantRunStatuses.WaitingForApproval, NhAssistantRunStatuses.Completed],
                    NhAssistantRunStatuses.Failed,
                    cancellationToken,
                    NhAssistantRunFailureClasses.Policy,
                    NhAssistantErrorCodes.ApprovalExpired);
            }
        }
    }

    /// <summary>
    /// Sends the reminders of deferred proposals that still wait and are due at <paramref name="now"/>
    /// of the application's clock, each once.
    /// </summary>
    private static async Task RemindAsync(
        NhAssistantRunStore runs,
        NhAssistantAgentNotifier notifier,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var approval in await runs.GetDueRemindersAsync(now, BatchSize, cancellationToken))
        {
            var policy = approval.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
            var next = policy is null
                ? null
                : NhAssistantApprovalReminders.Next(policy, NhAssistantApprovalReminders.Since(approval), approval.RemindersSent + 1, approval.ExpiresAt);
            if (!await runs.TryRecordReminderAsync(approval.Id, approval.RemindersSent, next, cancellationToken))
            {
                continue;
            }

            if (await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken) is { } thread)
            {
                await notifier.NotifyAsync(
                    NhAssistantAgentNotificationKind.ProposalExpiring,
                    thread,
                    approval.RunId,
                    approval,
                    approval.InitiatorActorId,
                    shadow: false,
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Queues approved deferred proposals that have no execution, of runs and consultations alike,
    /// for example after their agent was stopped. The query leaves out the proposals that stopped
    /// and code-disabled agents hold, so they never hide the ones behind them.
    /// </summary>
    /// <remarks>
    /// Decisions are recorded on the application's clock, so a decision settles a minute after it on
    /// that clock. A forward jump may queue an execution while its decider still does; the
    /// execution's idempotency key makes that one operation.
    /// </remarks>
    private async Task QueueMissingExecutionsAsync(
        NhAssistantRunStore runs,
        NhAssistantProposalDecisions decisions,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settled = now.AddMinutes(-1);
        var configuredOff = registration.AgentRuns
            .Where(item => !item.Value.Enabled)
            .Select(item => item.Key)
            .ToArray();
        foreach (var approval in await runs.GetHeldExecutionsAsync(settled, configuredOff, BatchSize, cancellationToken))
        {
            var thread = await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
            if (thread is not null)
            {
                await decisions.EnqueueExecutionAsync(approval, thread, cancellationToken);
            }
        }
    }

    private static async Task DetectInterruptedExecutionsAsync(
        NhAssistantRunStore runs,
        IRepository<NhBackgroundOperation> operations,
        NhAssistantAgentNotifier notifier,
        NhAssistantAgentEvents events,
        NhAssistantAgentWorkUpdates workUpdates,
        CancellationToken cancellationToken)
    {
        var executing = await runs.GetApprovalsInStatusAsync(NhAssistantApprovalStatuses.Executing, BatchSize, cancellationToken);
        if (executing.Count == 0)
        {
            return;
        }

        var operationIds = executing
            .Where(approval => approval.ExecutionOperationId.HasValue)
            .Select(approval => approval.ExecutionOperationId!.Value)
            .ToArray();
        var states = await operations.GetAll()
            .AsNoTracking()
            .Where(operation => operationIds.Contains(operation.Id))
            .Select(operation => new { operation.Id, operation.Status, operation.OperationType })
            .ToDictionaryAsync(operation => operation.Id, cancellationToken);
        foreach (var approval in executing)
        {
            if (approval.ExecutionOperationId is not { } operationId
                || !states.TryGetValue(operationId, out var state)
                || state.Status is not (NhBackgroundOperationStatus.Failed
                    or NhBackgroundOperationStatus.TimedOut
                    or NhBackgroundOperationStatus.Cancelled
                    or NhBackgroundOperationStatus.Succeeded))
            {
                continue;
            }

            // The operation ended while the proposal still says executing: the tool may or may not
            // have written. Only a read or idempotent tool is known to be safe to treat as failed.
            var onceOnly = state.OperationType == NhAssistantAgentRunOperationTypes.ExecuteProposalOnce;
            var status = onceOnly ? NhAssistantApprovalStatuses.ExecutionUnknown : NhAssistantApprovalStatuses.ExecutionFailed;
            var interrupted = await runs.TryTransitionApprovalAsync(
                approval.Id,
                [NhAssistantApprovalStatuses.Executing],
                status,
                new NhAssistantApprovalTransition
                {
                    ExecutionResultCode = onceOnly ? "execution-interrupted" : "execution-failed"
                },
                cancellationToken);
            if (!interrupted || await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken) is not { } thread)
            {
                continue;
            }

            await workUpdates.PublishAsync(
                approval.ConversationId,
                NhAssistantAgentWorkReasons.Proposal,
                approval.Id,
                approval.RunId,
                cancellationToken);

            await events.ProposalAsync(
                NhAssistantAuditEventKind.AgentProposalExecuted,
                thread,
                approval,
                NhAssistantAgentEvents.AgentActorPrefix + thread.AgentId,
                status,
                cancellationToken);
            if (onceOnly)
            {
                await notifier.NotifyAsync(
                    NhAssistantAgentNotificationKind.ExecutionUnknown,
                    thread,
                    approval.RunId,
                    approval,
                    approval.InitiatorActorId,
                    shadow: false,
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Queues a run again whose background operation never committed, because the process stopped
    /// between writing the run and enqueuing its operation; its input is committed. A run whose
    /// thread, agent or subject is gone is cancelled.
    /// </summary>
    private static async Task RecoverOrphanedRunsAsync(
        NhAssistantRunStore runs,
        IRepository<NhBackgroundOperation> operations,
        NhAssistantAgentTriggers triggers,
        CancellationToken cancellationToken)
    {
        var stale = await runs.GetStaleQueuedRunsAsync(DateTimeOffset.UtcNow.AddMinutes(-10), BatchSize, cancellationToken);
        if (stale.Count == 0)
        {
            return;
        }

        var operationIds = stale
            .Where(run => run.BackgroundOperationId.HasValue)
            .Select(run => run.BackgroundOperationId!.Value)
            .ToArray();
        var existing = await operations.GetAll()
            .AsNoTracking()
            .Where(operation => operationIds.Contains(operation.Id))
            .Select(operation => operation.Id)
            .ToListAsync(cancellationToken);
        foreach (var run in stale)
        {
            if (run.BackgroundOperationId is { } operationId && existing.Contains(operationId))
            {
                continue;
            }

            if (await triggers.RequeueOrphanAsync(run, cancellationToken))
            {
                continue;
            }

            await runs.TrySetRunStatusAsync(
                run.Id,
                [NhAssistantRunStatuses.Queued],
                NhAssistantRunStatuses.Cancelled,
                cancellationToken,
                NhAssistantRunFailureClasses.Internal,
                NhAssistantRunStore.OrphanedRunCode);
        }
    }
}
