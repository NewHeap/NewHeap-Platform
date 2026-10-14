using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Wakes the run that waits for an inline proposal the platform closed itself. Nobody decided, so
/// no person can signal the run; the platform signals its own wait on behalf of the operation's
/// owner. The background operation records the signal, and the closing records the invalidation.
/// </summary>
internal sealed class NhAssistantInlineRunWaker(
    NhAssistantRunStore runs,
    IServiceProvider services,
    ILogger<NhAssistantInlineRunWaker> logger) : INhAssistantInlineRunWaker
{
    public async Task WakeAsync(AssistantApproval approval, CancellationToken cancellationToken)
    {
        var run = approval.RunId is { } runId ? await runs.FindRunAsync(runId, cancellationToken) : null;
        var signals = services.GetService<NhBackgroundOperationOwnerSignals>();
        if (run?.BackgroundOperationId is not { } operationId || signals is null)
        {
            logger.LogWarning("The run of stale proposal {ApprovalId} cannot be signaled.", approval.Id);
            return;
        }

        var signaled = await signals.SignalAsync(
            operationId,
            NhAssistantAgentRunOperation.WaitKey(approval.Id),
            new NhAiBackgroundApprovalSignal(
                approval.Id,
                approval.ProposalId,
                approval.ProposalHash,
                false,
                NhAssistantApprovalStatuses.Stale),
            cancellationToken: cancellationToken);
        if (!signaled.Success)
        {
            // A run that no longer waits, such as one whose wait expired, needs no signal.
            logger.LogInformation("The run waiting for stale proposal {ApprovalId} was not woken.", approval.Id);
        }
    }
}
