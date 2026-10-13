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

/// <param name="Enabled">Whether the agent accepts triggers and runs.</param>
/// <param name="CancelledRuns">The runs the kill switch cancelled.</param>
public sealed record NhAssistantKillSwitchDto(string AgentId, bool Enabled, int CancelledRuns);

/// <param name="Engaged"><see langword="true"/> stops the agent; <see langword="false"/> enables it again.</param>
public sealed record NhAssistantKillSwitchRequest(bool? Engaged);

/// <summary>
/// The kill switch of an agent. Engaging it disables the agent, so triggers are rejected with
/// <c>assistant-agent-disabled</c>, cancels every active run and keeps approved proposals from
/// executing; releasing it enables the agent again, after which the maintenance job queues the
/// executions that waited.
/// </summary>
internal sealed class NhAssistantKillSwitch(
    NhAssistantAdminStore agentsStore,
    NhAssistantAgentCatalog agents,
    NhAssistantRunStore runs,
    NhAssistantRunCancellation cancellation,
    NhAssistantAgentEvents events)
{
    public async Task<TaskResult<NhAssistantKillSwitchDto>> SetAsync(
        string agentId,
        bool engaged,
        string actorId,
        CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<NhAssistantKillSwitchDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        var updated = await agentsStore.UpdateAgentAsync(
            agentId,
            expectedVersion: null,
            row =>
            {
                row.IsEnabled = !engaged;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                row.UpdatedBy = actorId;
            },
            mcpServerIds: null,
            cancellationToken);
        if (!updated.Succeeded)
        {
            return TaskResult<NhAssistantKillSwitchDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent could not be updated.");
        }

        var cancelled = 0;
        if (engaged)
        {
            foreach (var run in await runs.GetActiveRunsAsync(agentId, cancellationToken))
            {
                var thread = await runs.FindThreadByConversationAsync(run.ConversationId, cancellationToken);
                var owner = thread is null
                    ? NhBackgroundOperationActor.ForAgent(agent.Definition.ActorId)
                    : NhAssistantAgentTriggers.OwnerActor(thread, run.AccountableOwnerId, agent.Definition);
                await cancellation.CancelAsync(run, actorId, owner, cancellationToken);
                cancelled++;
            }
        }

        await events.KillSwitchAsync(agentId, actorId, engaged, cancelled, cancellationToken);
        return TaskResult<NhAssistantKillSwitchDto>.Succeeded(new NhAssistantKillSwitchDto(agentId, !engaged, cancelled));
    }
}
