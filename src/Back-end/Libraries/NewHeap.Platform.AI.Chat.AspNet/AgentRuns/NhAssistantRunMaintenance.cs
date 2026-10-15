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
/// inline approval run is woken only by the person who just recorded the decision.
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
        var access = await subjects.ResolveAsync(subject, actorId, cancellationToken);
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
/// <c>execution-unknown</c> and cancels runs whose trigger transaction rolled back. Every change is
/// a compare-and-set update, so several instances can run it at the same time.
/// </summary>
internal sealed class NhAssistantRunMaintenance(
    IServiceScopeFactory scopeFactory,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantRunMaintenance> logger) : BackgroundService
{
    private const int BatchSize = 100;

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
        await ExpireAsync(runs, policies, notifier, workUpdates, cancellationToken);
        await RemindAsync(runs, notifier, cancellationToken);
        await QueueMissingExecutionsAsync(runs, decisions, cancellationToken);
        if (operations is not null)
        {
            await DetectInterruptedExecutionsAsync(runs, operations, notifier, events, workUpdates, cancellationToken);
            await RecoverOrphanedRunsAsync(runs, operations, services.GetRequiredService<NhAssistantAgentTriggers>(), cancellationToken);
        }
    }

    private static async Task ExpireAsync(
        NhAssistantRunStore runs,
        NhAssistantApprovalPolicies policies,
        NhAssistantAgentNotifier notifier,
        NhAssistantAgentWorkUpdates workUpdates,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
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
            if (expired && policy?.OnExpiry == NhAiApprovalExpiryAction.FailRun && approval.RunId is { } runId)
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
    /// Sends the due reminders of deferred proposals that still wait, each once.
    /// </summary>
    private static async Task RemindAsync(
        NhAssistantRunStore runs,
        NhAssistantAgentNotifier notifier,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var approval in await runs.GetDueRemindersAsync(now, BatchSize, cancellationToken))
        {
            var policy = approval.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
            var next = policy is null
                ? null
                : NhAssistantApprovalReminders.Next(policy, approval.EscalatedAt ?? approval.CreatedAt, approval.RemindersSent + 1, approval.ExpiresAt);
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

    private static async Task QueueMissingExecutionsAsync(
        NhAssistantRunStore runs,
        NhAssistantProposalDecisions decisions,
        CancellationToken cancellationToken)
    {
        var settled = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var approval in await runs.GetApprovalsInStatusAsync(NhAssistantApprovalStatuses.Approved, BatchSize, cancellationToken))
        {
            if (approval.Mode != NhAssistantApprovalModes.Deferred
                || approval.ExecutionOperationId is not null
                || approval.DecidedAt is not { } decidedAt
                || decidedAt > settled)
            {
                continue;
            }

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
