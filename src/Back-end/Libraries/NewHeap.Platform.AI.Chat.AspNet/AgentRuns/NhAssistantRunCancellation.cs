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
/// running run ends at its next safe point through its background operation.
/// </summary>
internal sealed class NhAssistantRunCancellation(
    NhAssistantRunStore runs,
    NhAssistantAgentWorkUpdates workUpdates,
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
        foreach (var approval in await runs.GetRunApprovalsAsync(run.Id, cancellationToken))
        {
            await runs.TryTransitionApprovalAsync(
                approval.Id,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantApprovalStatuses.Withdrawn,
                new NhAssistantApprovalTransition { DecidedByActorId = actorId, DecidedAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }

        if (run.BackgroundOperationId is { } operationId)
        {
            await Operations.RequestCancellationForActorAsync(operationId, operationActor, cancellationToken);
        }
        if (run.Status == NhAssistantRunStatuses.Queued || run.Status == NhAssistantRunStatuses.WaitingForApproval)
        {
            // Nothing is executing; the run ends now. A running run ends at its next safe point.
            await runs.TrySetRunStatusAsync(
                run.Id,
                [NhAssistantRunStatuses.Queued, NhAssistantRunStatuses.WaitingForApproval],
                NhAssistantRunStatuses.Cancelled,
                cancellationToken,
                NhAssistantRunFailureClasses.Cancelled);
        }

        await workUpdates.PublishAsync(run.ConversationId, NhAssistantAgentWorkReasons.Run, runId: run.Id, cancellationToken: cancellationToken);
    }
}

/// <param name="Enabled">Whether the agent accepts triggers and runs: <see langword="false"/> while it
/// is stopped.</param>
/// <param name="CancelledRuns">The runs the kill switch cancelled.</param>
public sealed record NhAssistantKillSwitchDto(string AgentId, bool Enabled, int CancelledRuns);

/// <param name="Engaged"><see langword="true"/> stops the agent; <see langword="false"/> resumes it.
/// The kill switch is <see cref="INhAssistantAgentControl.StopAsync"/> and
/// <see cref="INhAssistantAgentControl.ResumeAsync"/> without a reason.</param>
public sealed record NhAssistantKillSwitchRequest(bool? Engaged);
