using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The one path that ends an agent run. The run's status compare-and-set records the end once, and
/// only the call that wins it writes the run's summary, its <c>AgentRunEnded</c> event and work
/// update, tells the agents that handed it work how it ended and notifies the people who wait for
/// it. The run's own background operation ends its run here, and so does the maintenance job for a
/// run whose operation ended without it (<see cref="EndAfterOperationEndedAsync"/>).
/// </summary>
internal sealed class NhAssistantRunEnding(
    NhAssistantRunStore runs,
    NhAssistantAgentEvents events,
    NhAssistantAgentNotifier notifier,
    NhAssistantRunSummaries summaries,
    NhAssistantAgentWorkUpdates workUpdates,
    NhAssistantHandOffService handOffs,
    NhAssistantLivePublisher live,
    ILogger<NhAssistantRunEnding> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// The failure code the background-operation reconciliation gives an operation that no worker
    /// started within its recovery rounds.
    /// </summary>
    internal const string DispatchStalledCode = "dispatch-stalled";

    /// <summary>
    /// The statuses of a run that has not ended.
    /// </summary>
    internal static readonly string[] OpenStatuses =
    [
        NhAssistantRunStatuses.Queued,
        NhAssistantRunStatuses.Running,
        NhAssistantRunStatuses.WaitingForApproval
    ];

    /// <summary>
    /// The application's clock, which a proposal's lifetime follows.
    /// </summary>
    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Ends the run once and records the end; a run that already ended stays as it was. Returns
    /// whether this call ended it.
    /// </summary>
    public async Task<bool> EndAsync(
        AssistantRun run,
        AssistantThread? thread,
        string status,
        string? failureClass,
        string? failureCode,
        Guid? turnId = null)
    {
        var ended = await runs.TrySetRunStatusAsync(
            run.Id,
            OpenStatuses,
            status,
            CancellationToken.None,
            failureClass,
            failureCode);
        if (ended && thread is not null)
        {
            // The skill may have changed while the run worked.
            run = await runs.FindRunAsync(run.Id, CancellationToken.None) ?? run;
            // The next runs of the thread start from this summary instead of the full history.
            await summaries.WriteAsync(run, failureCode is null ? status : status + " (" + failureCode + ")", turnId, CancellationToken.None);
            await events.RunEndedAsync(thread, run, status, failureCode, CancellationToken.None);
            await workUpdates.PublishAsync(run.ConversationId, NhAssistantAgentWorkReasons.Run, runId: run.Id, cancellationToken: CancellationToken.None);
            await NotifyHandOffSourcesAsync(run, thread);
            if (status == NhAssistantRunStatuses.Completed
                && string.Equals(run.TriggerId, NhAssistantTriggerIds.AgentRequest, StringComparison.Ordinal))
            {
                // The person who asked through another assistant learns that the answer is there.
                await notifier.NotifyAsync(
                    NhAssistantAgentNotificationKind.RequestAnswered,
                    thread,
                    run.Id,
                    null,
                    run.InitiatorActorId,
                    run.Shadow,
                    CancellationToken.None);
            }
        }

        return ended;
    }

    /// <summary>
    /// Fails the run once through <see cref="EndAsync"/>; the call that failed it tells the run's
    /// initiator, and the approvers of the objects the run placed proposals at. Returns whether this
    /// call failed it.
    /// </summary>
    public async Task<bool> FailAsync(AssistantRun run, AssistantThread? thread, string failureClass, string code, Guid? turnId = null)
    {
        var failed = await EndAsync(run, thread, NhAssistantRunStatuses.Failed, failureClass, code, turnId);
        if (failed && thread is not null)
        {
            await notifier.NotifyAsync(
                NhAssistantAgentNotificationKind.RunFailed,
                thread,
                run.Id,
                null,
                run.InitiatorActorId,
                run.Shadow,
                CancellationToken.None,
                failureCode: code);
            await NotifyPlacedProposalsAsync(run);
        }

        return failed;
    }

    /// <summary>
    /// Tells the people who decide the proposals a completed run left open about them: the run's
    /// initiator about those in its own thread, the approvers of other objects about those it placed
    /// there.
    /// </summary>
    public async Task NotifyWaitingProposalsAsync(AssistantRun run, AssistantThread thread)
    {
        var waiting = (await runs.GetRunApprovalsAsync(run.Id, CancellationToken.None))
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Pending)
            .ToArray();
        var own = waiting.Where(approval => approval.ConversationId == run.ConversationId).ToArray();
        if (own.Length > 0)
        {
            await notifier.NotifyAsync(
                NhAssistantAgentNotificationKind.RunEndedWithProposals,
                thread,
                run.Id,
                own[0],
                run.InitiatorActorId,
                run.Shadow,
                CancellationToken.None,
                proposalCount: own.Length);
        }

        await NotifyPlacedProposalsAsync(run, waiting);
    }

    /// <summary>
    /// Ends a run whose background operation ended without ending the run, such as a run that waited
    /// inline for a decision while an operator cancelled its operation, reconciliation failed it as
    /// <c>dispatch-stalled</c> or it failed for good in another way, or whose operation is gone
    /// (<paramref name="operationStatus"/> <see langword="null"/>), removed by the cleanup of ended
    /// operations. The run's handler never runs again, so the maintenance job ends the run through
    /// <see cref="EndAsync"/>, once and as the operation's end says (<see cref="OutcomeOf"/>). The
    /// run's inline proposals can no longer be decided or executed: pending ones close first, expired
    /// when their lifetime passed on the application's clock and withdrawn otherwise, and approved
    /// ones that never executed are withdrawn without executing, so the run's summary names them so.
    /// The thread's turn that waited for them is released afterwards, so the agent's next run can
    /// start. Returns whether this call ended the run.
    /// </summary>
    public async Task<bool> EndAfterOperationEndedAsync(
        AssistantRun run,
        NhBackgroundOperationStatus? operationStatus,
        string? operationFailureCode,
        CancellationToken cancellationToken)
    {
        var outcome = OutcomeOf(operationStatus, operationFailureCode);
        var thread = await runs.FindThreadByConversationAsync(run.ConversationId, cancellationToken);
        await CloseInlineProposalsAsync(run, outcome.Code, cancellationToken);

        var ended = outcome.Status == NhAssistantRunStatuses.Failed
            ? await FailAsync(run, thread, outcome.FailureClass, outcome.Code, run.TurnId)
            : await EndAsync(run, thread, outcome.Status, outcome.FailureClass, outcome.Code, run.TurnId);

        if (run.TurnId is { } turnId && await runs.TryReleaseWaitingTurnAsync(run.ConversationId, turnId, cancellationToken))
        {
            await live.PublishChangedAsync(run.ConversationId, CancellationToken.None);
        }

        if (ended)
        {
            logger.LogWarning(
                "Agent run {RunId} ended {Status} with {FailureCode}: its background operation {OperationId} ended {OperationStatus} without ending the run.",
                run.Id,
                outcome.Status,
                outcome.Code,
                run.BackgroundOperationId,
                operationStatus?.ToString() ?? "and was removed");
        }

        return ended;
    }

    /// <summary>
    /// How a run ends whose background operation ended without it: a cancelled operation cancels the
    /// run (<c>assistant-run-operation-cancelled</c>), an operation that failed as
    /// <c>dispatch-stalled</c> fails it (<c>assistant-run-operation-stalled</c>), and every other end,
    /// failed, timed out, even succeeded, or unknown because the operation was removed
    /// (<see langword="null"/>), fails it (<c>assistant-run-operation-ended</c>). Each failure is
    /// <see cref="NhAssistantRunFailureClasses.Internal"/>: the platform, not the agent, stopped the
    /// run.
    /// </summary>
    internal static NhAssistantRunOutcome OutcomeOf(NhBackgroundOperationStatus? operationStatus, string? operationFailureCode)
    {
        if (operationStatus == NhBackgroundOperationStatus.Cancelled)
        {
            return new NhAssistantRunOutcome(
                NhAssistantRunStatuses.Cancelled,
                NhAssistantRunFailureClasses.Cancelled,
                NhAssistantErrorCodes.RunOperationCancelled);
        }

        if (operationStatus == NhBackgroundOperationStatus.Failed
            && string.Equals(operationFailureCode, DispatchStalledCode, StringComparison.Ordinal))
        {
            return new NhAssistantRunOutcome(
                NhAssistantRunStatuses.Failed,
                NhAssistantRunFailureClasses.Internal,
                NhAssistantErrorCodes.RunOperationStalled);
        }

        return new NhAssistantRunOutcome(
            NhAssistantRunStatuses.Failed,
            NhAssistantRunFailureClasses.Internal,
            NhAssistantErrorCodes.RunOperationEnded);
    }

    /// <summary>
    /// Closes the run's open inline proposals, each once, and tells the people on the subject. A
    /// pending one expires or is withdrawn; one that a person approved after the operation ended, and
    /// that so never executed, is withdrawn with the run's failure code <paramref name="code"/> as
    /// its result and executes nothing. Deferred proposals do not wait for the run and keep their own
    /// lifecycle.
    /// </summary>
    private async Task CloseInlineProposalsAsync(AssistantRun run, string code, CancellationToken cancellationToken)
    {
        var now = _applicationClock.GetUtcNow();
        var open = (await runs.GetRunApprovalsAsync(run.Id, cancellationToken))
            .Where(approval => approval.Mode == NhAssistantApprovalModes.Inline
                && approval.Status is NhAssistantApprovalStatuses.Pending or NhAssistantApprovalStatuses.Approved);
        foreach (var approval in open)
        {
            var approved = approval.Status == NhAssistantApprovalStatuses.Approved;
            var status = !approved && approval.ExpiresAt <= now
                ? NhAssistantApprovalStatuses.Expired
                : NhAssistantApprovalStatuses.Withdrawn;
            var closed = await runs.TryTransitionApprovalAsync(
                approval.Id,
                [approval.Status],
                status,
                approved
                    ? new NhAssistantApprovalTransition { ExecutionResultCode = code }
                    : NhAssistantApprovalTransition.None,
                cancellationToken);
            if (closed)
            {
                await workUpdates.PublishAsync(
                    approval.ConversationId,
                    NhAssistantAgentWorkReasons.Proposal,
                    approval.Id,
                    approval.RunId,
                    CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Tells the agents that handed this run work how it ended. A failed follow-up never changes
    /// how the run ended.
    /// </summary>
    private async Task NotifyHandOffSourcesAsync(AssistantRun run, AssistantThread thread)
    {
        try
        {
            await handOffs.NotifySourcesAsync(run.Id, thread, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "The hand-off follow-ups of run {RunId} were not sent ({ExceptionType}).",
                run.Id,
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Proposals a run placed at other objects reach those objects' approvers, once per object, also
    /// when the run failed afterwards; the run's initiator is told only about its own thread.
    /// </summary>
    private async Task NotifyPlacedProposalsAsync(AssistantRun run, IReadOnlyList<AssistantApproval>? waiting = null)
    {
        waiting ??= (await runs.GetRunApprovalsAsync(run.Id, CancellationToken.None))
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Pending)
            .ToArray();
        foreach (var placed in waiting
            .Where(approval => approval.PlacedFromConversationId is not null && approval.ConversationId != run.ConversationId)
            .GroupBy(approval => approval.ConversationId))
        {
            if (await runs.FindThreadByConversationAsync(placed.Key, CancellationToken.None) is not { } target)
            {
                continue;
            }

            await notifier.NotifyAsync(
                NhAssistantAgentNotificationKind.RunEndedWithProposals,
                target,
                run.Id,
                placed.First(),
                null,
                run.Shadow,
                CancellationToken.None,
                proposalCount: placed.Count());
        }
    }
}

/// <summary>
/// How a run ends: its status, failure class and failure code.
/// </summary>
internal sealed record NhAssistantRunOutcome(string Status, string FailureClass, string Code);
