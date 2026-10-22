using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Cancels one run: open proposals are withdrawn, a queued or waiting run ends at once and a
/// running run ends at its next safe point through its background operation. The thread's turn a
/// cancelled run waited in is released.
/// </summary>
internal sealed class NhAssistantRunCancellation(
    NhAssistantRunStore runs,
    NhAssistantAgentWorkUpdates workUpdates,
    NhAssistantLivePublisher live,
    IServiceProvider services)
{
    private INhBackgroundOperationService Operations => services.GetRequiredService<INhBackgroundOperationService>();

    /// <param name="operationActor">Who cancels the background operation: the requesting person,
    /// checked against the operation's access policies, or the operation's owner.</param>
    public async Task CancelAsync(
        AssistantRun run,
        string actorId,
        NhBackgroundOperationActor operationActor,
        CancellationToken cancellationToken)
    {
        if (NhAssistantRunStatuses.IsTerminal(run.Status))
        {
            return;
        }

        await runs.MarkRunCancelRequestedAsync(run.Id, actorId, cancellationToken);
        // A proposal's decisions, its withdrawal included, are recorded on the application's clock.
        var withdrawnAt = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        foreach (var approval in await runs.GetRunApprovalsAsync(run.Id, cancellationToken))
        {
            await runs.TryTransitionApprovalAsync(
                approval.Id,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantApprovalStatuses.Withdrawn,
                new NhAssistantApprovalTransition { DecidedByActorId = actorId, DecidedAt = withdrawnAt },
                cancellationToken);
        }

        if (run.BackgroundOperationId is { } operationId)
        {
            await Operations.RequestCancellationForActorAsync(operationId, operationActor, cancellationToken);
        }
        if (run.Status == NhAssistantRunStatuses.Queued || run.Status == NhAssistantRunStatuses.WaitingForApproval)
        {
            // Nothing is executing; the run ends now. A running run ends at its next safe point.
            var cancelled = await runs.TrySetRunStatusAsync(
                run.Id,
                [NhAssistantRunStatuses.Queued, NhAssistantRunStatuses.WaitingForApproval],
                NhAssistantRunStatuses.Cancelled,
                cancellationToken,
                NhAssistantRunFailureClasses.Cancelled);

            // A run that waited inline never resumes its turn, so the thread no longer waits for it
            // and the agent's next run can start.
            if (cancelled
                && run.Status == NhAssistantRunStatuses.WaitingForApproval
                && run.TurnId is { } turnId
                && await runs.TryReleaseWaitingTurnAsync(run.ConversationId, turnId, cancellationToken))
            {
                await live.PublishChangedAsync(run.ConversationId, CancellationToken.None);
            }
        }

        await workUpdates.PublishAsync(run.ConversationId, NhAssistantAgentWorkReasons.Run, runId: run.Id, cancellationToken: cancellationToken);
    }
}

/// <param name="Enabled">Whether the agent accepts triggers and runs: <see langword="false"/> while it
/// is stopped.</param>
/// <param name="CancelledRuns">The runs the kill switch cancelled.</param>
public sealed record NhAssistantKillSwitchDto(string AgentId, bool Enabled, int CancelledRuns)
{
    /// <summary>
    /// The agent's state after the change: <c>stopped</c> while the kill switch is engaged; after it
    /// is released <c>off</c> for an agent that was off, otherwise <c>active</c>.
    /// </summary>
    public string RunState { get; init; } = NhAssistantAgentRunStates.Active;
}

/// <param name="Engaged"><see langword="true"/> stops the agent; <see langword="false"/> releases the
/// kill switch, which switches an agent that was off when it stopped off again and resumes every
/// other agent. The kill switch is <see cref="INhAssistantAgentControl.SetKillSwitchAsync"/>.</param>
public sealed record NhAssistantKillSwitchRequest(bool? Engaged);
