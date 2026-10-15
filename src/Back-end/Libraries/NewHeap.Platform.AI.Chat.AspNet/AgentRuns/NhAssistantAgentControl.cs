using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Pauses, stops and resumes agents. The administration, <c>POST admin/agents/{id}/state</c> and
/// the kill switch use it; applications call it from their own code, for example around an import.
/// <list type="bullet">
/// <item><description><b>Paused:</b> no new runs start. Events, webhooks, manual starts and
/// requests wait per thread, deduplicated by their dedupe key; schedules skip their ticks.
/// Running runs finish, approved proposals execute and people can chat.</description></item>
/// <item><description><b>Stopped:</b> active runs are cancelled and their pending proposals
/// withdrawn, waiting inputs end, approved proposals are held and every trigger, request and
/// chat turn is refused with <c>assistant-agent-stopped</c>.</description></item>
/// <item><description><b>Resumed:</b> every thread with waiting inputs gets one run, which claims
/// them all. Leaving the stopped state, by a resume or a pause, queues the executions of the
/// approved proposals the stop held.</description></item>
/// </list>
/// The state never changes the agent's definition or version, and <c>Enabled(false)</c> in code
/// wins: such an agent reports <see cref="NhAssistantAgentRunStates.ConfiguredOff"/> and cannot be
/// resumed.
/// </summary>
public interface INhAssistantAgentControl
{
    /// <summary>
    /// The agent's state, or <see langword="null"/> when the agent does not exist.
    /// </summary>
    Task<NhAssistantAgentState?> GetAsync(string agentId, CancellationToken cancellationToken = default);

    /// <param name="actorId">Who pauses the agent: a person's actor id or the application's own
    /// actor, at most 256 characters.</param>
    /// <param name="reason">Why, at most 500 characters; kept with the state and left out of the
    /// business audit.</param>
    Task<TaskResult<NhAssistantAgentState>> PauseAsync(
        string agentId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<TaskResult<NhAssistantAgentState>> StopAsync(
        string agentId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a paused or stopped agent active again. Fails with
    /// <c>assistant-agent-configured-off</c> for an agent that is disabled in code.
    /// </summary>
    Task<TaskResult<NhAssistantAgentState>> ResumeAsync(
        string agentId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

/// <param name="RunState">One of <see cref="NhAssistantAgentRunStates"/>: <c>active</c>,
/// <c>paused</c>, <c>stopped</c> or <c>configured-off</c>.</param>
/// <param name="Active">Whether the agent works now: active, enabled in code and the assistant
/// enabled.</param>
/// <param name="ChangedAt">When the state last changed; <see langword="null"/> when it never did.</param>
/// <param name="ChangedBy">The actor who changed it last.</param>
/// <param name="Reason">The reason given with the last change.</param>
/// <param name="WaitingInputs">The inputs that wait while the agent is paused.</param>
/// <param name="CancelledRuns">The runs this call cancelled by stopping the agent; 0 otherwise.</param>
/// <param name="StartedRuns">The runs this call started by resuming the agent; 0 otherwise.</param>
public sealed record NhAssistantAgentState(
    string AgentId,
    string RunState,
    bool Active,
    DateTimeOffset? ChangedAt,
    string? ChangedBy,
    string? Reason,
    int WaitingInputs,
    int CancelledRuns,
    int StartedRuns);

/// <param name="State"><c>active</c>, <c>paused</c> or <c>stopped</c>.</param>
/// <param name="Reason">Optional, at most 500 characters.</param>
public sealed record NhAssistantAgentStateRequest(string? State, string? Reason);

internal sealed class NhAssistantAgentControl(
    NhAssistantAdminStore store,
    NhAssistantAgentCatalog agents,
    NhAssistantRegistrationState registration,
    NhAssistantRunStore runs,
    NhAssistantRunCancellation cancellation,
    NhAssistantAgentTriggers triggers,
    NhAssistantAgentEvents events,
    IOptionsMonitor<NhAssistantOptions> options,
    IServiceProvider services) : INhAssistantAgentControl
{
    private const int MaxActorIdLength = 256;

    /// <summary>
    /// The held executions one state change queues; the maintenance job queues any others.
    /// </summary>
    private const int MaxReleasedExecutions = 500;

    public async Task<NhAssistantAgentState?> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        return agent is null ? null : await StateOfAsync(agent, cancellationToken);
    }

    internal Task<NhAssistantAgentState> StateOfAsync(NhAssistantAgent agent, CancellationToken cancellationToken)
    {
        return StateOfAsync(agent, 0, 0, cancellationToken);
    }

    public Task<TaskResult<NhAssistantAgentState>> PauseAsync(
        string agentId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return ChangeAsync(agentId, NhAssistantAgentRunStates.Paused, actorId, reason, cancellationToken);
    }

    public Task<TaskResult<NhAssistantAgentState>> StopAsync(
        string agentId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return ChangeAsync(agentId, NhAssistantAgentRunStates.Stopped, actorId, reason, cancellationToken);
    }

    public Task<TaskResult<NhAssistantAgentState>> ResumeAsync(
        string agentId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return ChangeAsync(agentId, NhAssistantAgentRunStates.Active, actorId, reason, cancellationToken);
    }

    /// <summary>
    /// The configured-off state of an agent, which code decides at startup.
    /// </summary>
    internal bool IsConfiguredOff(string agentId)
    {
        return registration.AgentRuns.TryGetValue(agentId, out var runOptions) && !runOptions.Enabled;
    }

    private async Task<TaskResult<NhAssistantAgentState>> ChangeAsync(
        string agentId,
        string target,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason?.Length > NhAssistantLimits.MaxStateReasonCharacters)
        {
            return TaskResult<NhAssistantAgentState>.Failed(
                NhAssistantValidationErrors.Failed("reason", NhAssistantFieldErrors.TooLong));
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<NhAssistantAgentState>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (target == NhAssistantAgentRunStates.Active && IsConfiguredOff(agentId))
        {
            return TaskResult<NhAssistantAgentState>.Failed(
                NhAssistantErrorCodes.AgentConfiguredOff,
                "The agent is disabled in the application's code and cannot be resumed.");
        }
        if (agent.RunState == target)
        {
            return TaskResult<NhAssistantAgentState>.Succeeded(await StateOfAsync(agent, 0, 0, cancellationToken));
        }

        var previous = await store.SetAgentStateAsync(agentId, target, actorId, reason, cancellationToken);
        if (previous is null)
        {
            return TaskResult<NhAssistantAgentState>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        var cancelled = 0;
        var started = 0;
        if (target == NhAssistantAgentRunStates.Stopped)
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

            await runs.DiscardAgentWaitingInputsAsync(agentId, cancellationToken);
        }
        else
        {
            if (previous == NhAssistantAgentRunStates.Stopped)
            {
                await ReleaseHeldExecutionsAsync(agentId, cancellationToken);
            }
            if (target == NhAssistantAgentRunStates.Active)
            {
                started = await triggers.ResumeWaitingAsync(agentId, cancellationToken);
            }
        }

        await events.AgentStateChangedAsync(agentId, actorId, target, target == NhAssistantAgentRunStates.Stopped ? cancelled : started, cancellationToken);
        var changed = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken) ?? agent;
        return TaskResult<NhAssistantAgentState>.Succeeded(await StateOfAsync(changed, cancelled, started, cancellationToken));
    }

    /// <summary>
    /// Queues the executions of approved proposals that the agent held while it was stopped. An
    /// execution that cannot be queued now is queued by the maintenance job.
    /// </summary>
    private async Task ReleaseHeldExecutionsAsync(string agentId, CancellationToken cancellationToken)
    {
        var held = await runs.GetAgentHeldExecutionsAsync(agentId, MaxReleasedExecutions, cancellationToken);
        if (held.Count == 0)
        {
            return;
        }

        var decisions = services.GetRequiredService<NhAssistantProposalDecisions>();
        foreach (var (approval, thread) in held)
        {
            await decisions.EnqueueExecutionAsync(approval, thread, cancellationToken);
        }
    }

    private async Task<NhAssistantAgentState> StateOfAsync(
        NhAssistantAgent agent,
        int cancelled,
        int started,
        CancellationToken cancellationToken)
    {
        var runState = IsConfiguredOff(agent.Id) ? NhAssistantAgentRunStates.ConfiguredOff : agent.RunState;
        var waiting = agent.RunState == NhAssistantAgentRunStates.Paused
            ? await runs.CountAgentWaitingInputsAsync(agent.Id, cancellationToken)
            : 0;
        return new NhAssistantAgentState(
            agent.Id,
            runState,
            runState == NhAssistantAgentRunStates.Active && options.CurrentValue.Enabled,
            agent.StateChangedAt,
            agent.StateChangedBy,
            agent.StateReason,
            waiting,
            cancelled,
            started);
    }
}
