using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
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
/// <para>Next to the state, the shadow mode of an agent and of each skill and the technical account
/// of an agent that answers to one are runtime overrides of the code: <see cref="SetShadowAsync"/>,
/// <see cref="SetSkillShadowAsync"/> and <see cref="SetServiceAccountAsync"/> change them without
/// a deploy, and <see langword="null"/> lets the code decide again.</para>
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

    /// <summary>
    /// Pauses one skill of an agent: triggers that name it let their events wait, triage and
    /// <c>use_skill</c> no longer offer it, and its running runs finish.
    /// </summary>
    Task<TaskResult<NhAssistantSkillState>> PauseSkillAsync(
        string agentId,
        string skillId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops one skill of an agent: triggers that name it are refused, its active runs cancelled and
    /// its waiting events dropped. The agent's other skills keep working.
    /// </summary>
    Task<TaskResult<NhAssistantSkillState>> StopSkillAsync(
        string agentId,
        string skillId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes one skill; every thread with events waiting for it gets one run in it.
    /// </summary>
    Task<TaskResult<NhAssistantSkillState>> ResumeSkillAsync(
        string agentId,
        string skillId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the agent in shadow (<see langword="true"/>), live (<see langword="false"/>) or back to
    /// the mode in code (<see langword="null"/>). In shadow, its runs work in the agent's shadow
    /// thread of each subject, which only administrators see: nobody is notified, nothing proposed
    /// there executes, and consultations and requests are refused. Runs that started finish in the
    /// mode they started in; a run that waited in the queue starts in the new mode, and waiting
    /// events move along when they resume. The live thread, its open proposals and people's chat
    /// stay as they are. Fails with <c>assistant-shadow-variant</c> for a shadow variant, which
    /// always works in shadow.
    /// </summary>
    Task<TaskResult<NhAssistantAgentState>> SetShadowAsync(
        string agentId,
        bool? shadow,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts one skill in shadow, live or back to the mode in code, while the agent's other skills
    /// keep their mode. Work that reaches a skill in shadow goes to the agent's shadow thread: a
    /// trigger that names it fires there, a run that rules or triage route to it hands its events
    /// there, and <c>use_skill</c> in a live thread does not offer it. A skill of an agent in shadow
    /// works in shadow whatever its own mode.
    /// </summary>
    Task<TaskResult<NhAssistantSkillState>> SetSkillShadowAsync(
        string agentId,
        string skillId,
        bool? shadow,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="serviceAccountActorId"/> the technical account that answers for the
    /// agent's new runs, or with <see langword="null"/> the account in code again. The account must
    /// exist and be allowed to sign in. Runs that started and the proposals they made keep the
    /// account they were made with; the agent's threads answer to the new one. Fails with
    /// <c>assistant-service-account-not-used</c> for an agent that does not answer to a technical
    /// account in code.
    /// </summary>
    /// <param name="serviceAccountActorId">The account's actor id, at most 256 characters.</param>
    Task<TaskResult<NhAssistantAgentState>> SetServiceAccountAsync(
        string agentId,
        string? serviceAccountActorId,
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
    int StartedRuns)
{
    /// <summary>
    /// The agent's skills with their own states; empty for an agent without skills. The agent's
    /// state covers all of them.
    /// </summary>
    public IReadOnlyList<NhAssistantSkillState> Skills { get; init; } = [];

    /// <summary>
    /// The live agent this agent is a shadow variant of (<c>ShadowOf</c>); <see langword="null"/>
    /// for an agent that is no variant. The two agents' states are independent.
    /// </summary>
    public string? ShadowOf { get; init; }

    /// <summary>
    /// Whether the agent works in shadow, and how the code and an administrator decided it;
    /// <see langword="null"/> for an agent without a run configuration, which only chats.
    /// </summary>
    public NhAssistantShadowState? Shadow { get; init; }

    /// <summary>
    /// The technical account that answers for the agent's new runs; <see langword="null"/> for an
    /// agent that answers to itself or to the subject's responsible person.
    /// </summary>
    public NhAssistantServiceAccountState? ServiceAccount { get; init; }
}

/// <param name="Shadow">Whether the work goes to shadow threads now. For a skill this includes the
/// agent's own shadow mode.</param>
/// <param name="Configured">The mode in code: <c>Shadow()</c> on the agent or the skill.</param>
/// <param name="Override">The mode an administrator set at runtime; <see langword="null"/> follows
/// the code.</param>
/// <param name="Locked">A shadow variant always works in shadow; its mode cannot change.</param>
/// <param name="ChangedAt">When the mode last changed at runtime.</param>
/// <param name="ChangedBy">Who changed it last.</param>
/// <param name="Reason">The reason given with the last change.</param>
public sealed record NhAssistantShadowState(
    bool Shadow,
    bool Configured,
    bool? Override,
    bool Locked,
    DateTimeOffset? ChangedAt,
    string? ChangedBy,
    string? Reason)
{
    internal static NhAssistantShadowState Live { get; } = new(false, false, null, false, null, null, null);
}

/// <param name="ActorId">The account that answers for the agent's new runs.</param>
/// <param name="ConfiguredActorId">The account in code (<c>ServiceAccount(actorId)</c>).</param>
/// <param name="Overridden">Whether an administrator set <paramref name="ActorId"/> at runtime.</param>
/// <param name="ChangedAt">When the account last changed at runtime.</param>
/// <param name="ChangedBy">Who changed it last.</param>
/// <param name="Reason">The reason given with the last change.</param>
public sealed record NhAssistantServiceAccountState(
    string ActorId,
    string ConfiguredActorId,
    bool Overridden,
    DateTimeOffset? ChangedAt,
    string? ChangedBy,
    string? Reason);

/// <param name="RunState"><c>active</c>, <c>paused</c> or <c>stopped</c>.</param>
/// <param name="WaitingInputs">The events that wait for the skill while it is paused.</param>
/// <param name="ToolCallsToday">The tool calls of the skill's runs today, in every scope.</param>
/// <param name="TokensToday">The model tokens of the skill's runs today, in every scope.</param>
/// <param name="DailyToolCalls">The skill's daily tool-call budget per scope, if it has one.</param>
/// <param name="DailyTokens">The skill's daily token budget per scope, if it has one.</param>
/// <param name="CancelledRuns">The runs this call cancelled by stopping the skill; 0 otherwise.</param>
/// <param name="StartedRuns">The runs this call started by resuming the skill; 0 otherwise.</param>
public sealed record NhAssistantSkillState(
    string SkillId,
    string Description,
    string RunState,
    DateTimeOffset? ChangedAt,
    string? ChangedBy,
    string? Reason,
    int WaitingInputs,
    int ToolCallsToday,
    long TokensToday,
    int? DailyToolCalls,
    long? DailyTokens,
    int CancelledRuns,
    int StartedRuns)
{
    /// <summary>
    /// Whether the skill's work goes to the agent's shadow threads, and how the code and an
    /// administrator decided it.
    /// </summary>
    public NhAssistantShadowState Shadow { get; init; } = NhAssistantShadowState.Live;
}

/// <param name="State"><c>active</c>, <c>paused</c> or <c>stopped</c>.</param>
/// <param name="Reason">Optional, at most 500 characters.</param>
public sealed record NhAssistantAgentStateRequest(string? State, string? Reason);

/// <summary>
/// The shadow modes an administrator chooses for an agent or a skill.
/// </summary>
public static class NhAssistantShadowChoices
{
    /// <summary>The work goes to shadow threads.</summary>
    public const string Shadow = "shadow";

    /// <summary>The work is live.</summary>
    public const string Live = "live";

    /// <summary>The mode in code decides again.</summary>
    public const string Code = "code";

    internal static bool TryParse(string? mode, out bool? shadow)
    {
        shadow = mode switch
        {
            Shadow => true,
            Live => false,
            _ => null
        };
        return mode is Shadow or Live or Code;
    }
}

/// <param name="Mode"><c>shadow</c>, <c>live</c> or <c>code</c>.</param>
/// <param name="Reason">Optional, at most 500 characters.</param>
public sealed record NhAssistantShadowRequest(string? Mode, string? Reason);

/// <param name="ActorId">The technical account, at most 256 characters; <see langword="null"/>
/// uses the account in code again.</param>
/// <param name="Reason">Optional, at most 500 characters.</param>
public sealed record NhAssistantServiceAccountRequest(string? ActorId, string? Reason);

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

    public Task<TaskResult<NhAssistantSkillState>> PauseSkillAsync(
        string agentId,
        string skillId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return ChangeSkillAsync(agentId, skillId, NhAssistantAgentRunStates.Paused, actorId, reason, cancellationToken);
    }

    public Task<TaskResult<NhAssistantSkillState>> StopSkillAsync(
        string agentId,
        string skillId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return ChangeSkillAsync(agentId, skillId, NhAssistantAgentRunStates.Stopped, actorId, reason, cancellationToken);
    }

    public Task<TaskResult<NhAssistantSkillState>> ResumeSkillAsync(
        string agentId,
        string skillId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return ChangeSkillAsync(agentId, skillId, NhAssistantAgentRunStates.Active, actorId, reason, cancellationToken);
    }

    public async Task<TaskResult<NhAssistantAgentState>> SetShadowAsync(
        string agentId,
        bool? shadow,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantAgentState>.Failed(invalid);
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null || !registration.AgentRuns.TryGetValue(agentId, out var options))
        {
            return TaskResult<NhAssistantAgentState>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (options.ShadowOfAgentId is not null)
        {
            return TaskResult<NhAssistantAgentState>.Failed(NhAssistantErrorCodes.ShadowVariant, "A shadow variant always works in shadow.");
        }

        if (agent.ShadowOverride != shadow)
        {
            await store.SetAgentShadowAsync(agentId, shadow, actorId, reason, cancellationToken);
            await events.AgentShadowChangedAsync(agentId, actorId, shadow, cancellationToken);
            agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken) ?? agent;
        }

        return TaskResult<NhAssistantAgentState>.Succeeded(await StateOfAsync(agent, 0, 0, cancellationToken));
    }

    public async Task<TaskResult<NhAssistantSkillState>> SetSkillShadowAsync(
        string agentId,
        string skillId,
        bool? shadow,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantSkillState>.Failed(invalid);
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<NhAssistantSkillState>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (!registration.AgentRuns.TryGetValue(agentId, out var options) || !options.Skills.ContainsKey(skillId))
        {
            return TaskResult<NhAssistantSkillState>.Failed(NhAssistantErrorCodes.SkillNotFound, "The agent declares no such skill.");
        }
        if (options.ShadowOfAgentId is not null)
        {
            return TaskResult<NhAssistantSkillState>.Failed(NhAssistantErrorCodes.ShadowVariant, "A shadow variant always works in shadow.");
        }

        var stored = await store.GetSkillStatesAsync(agentId, cancellationToken);
        var row = stored.GetValueOrDefault(skillId);
        if (row?.ShadowOverride != shadow)
        {
            await store.SetSkillShadowAsync(agentId, skillId, shadow, actorId, reason, cancellationToken);
            await events.AgentShadowChangedAsync(agentId, actorId, shadow, cancellationToken, skillId);
        }

        var states = await SkillStatesAsync(agent, options, cancellationToken);
        return TaskResult<NhAssistantSkillState>.Succeeded(states.Single(state => state.SkillId == skillId));
    }

    public async Task<TaskResult<NhAssistantAgentState>> SetServiceAccountAsync(
        string agentId,
        string? serviceAccountActorId,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantAgentState>.Failed(invalid);
        }

        serviceAccountActorId = string.IsNullOrWhiteSpace(serviceAccountActorId) ? null : serviceAccountActorId.Trim();
        if (serviceAccountActorId?.Length > MaxActorIdLength)
        {
            return TaskResult<NhAssistantAgentState>.Failed(
                NhAssistantValidationErrors.Failed("actorId", NhAssistantFieldErrors.TooLong));
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null || !registration.AgentRuns.TryGetValue(agentId, out var options))
        {
            return TaskResult<NhAssistantAgentState>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (options.Accountability != NhAiAgentAccountability.ServiceAccount)
        {
            return TaskResult<NhAssistantAgentState>.Failed(
                NhAssistantErrorCodes.ServiceAccountNotUsed,
                "The agent does not answer to a technical account.");
        }
        if (serviceAccountActorId is not null && !await AccountExistsAsync(agentId, serviceAccountActorId, cancellationToken))
        {
            return TaskResult<NhAssistantAgentState>.Failed(
                NhAssistantErrorCodes.ServiceAccountNotFound,
                "No account that may sign in exists with that id.");
        }

        if (!string.Equals(agent.ServiceAccountOverride, serviceAccountActorId, StringComparison.Ordinal))
        {
            await store.SetAgentServiceAccountAsync(agentId, serviceAccountActorId, actorId, reason, cancellationToken);
            var answering = serviceAccountActorId ?? options.ServiceAccountActorId!;
            // The agent's threads answer to the new account; runs and proposals keep theirs.
            await runs.SetServiceAccountOfThreadsAsync(agentId, answering, cancellationToken);
            await events.AgentServiceAccountChangedAsync(agentId, actorId, answering, serviceAccountActorId is not null, cancellationToken);
            agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken) ?? agent;
        }

        return TaskResult<NhAssistantAgentState>.Succeeded(await StateOfAsync(agent, 0, 0, cancellationToken));
    }

    /// <summary>
    /// Whether the account exists and may sign in, as the principal factory that rebuilds it for the
    /// agent's runs decides.
    /// </summary>
    private async Task<bool> AccountExistsAsync(string agentId, string accountActorId, CancellationToken cancellationToken)
    {
        if (services.GetService<INhAiPrincipalFactory>() is not { } principals)
        {
            return false;
        }

        var subject = new NhAiSubjectInfo(new NhAiSubjectRef("agent", agentId), NhAiSubjectInfo.CreateScopeKey(null, null));
        return await principals.CreateAsync(accountActorId, subject, cancellationToken) is not null;
    }

    /// <summary>
    /// The failure of a change by <paramref name="actorId"/> with <paramref name="reason"/>, which is
    /// trimmed, or <see langword="null"/> when both are valid.
    /// </summary>
    private static TaskResult? ValidateChange(string actorId, ref string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return reason?.Length > NhAssistantLimits.MaxStateReasonCharacters
            ? NhAssistantValidationErrors.Failed("reason", NhAssistantFieldErrors.TooLong)
            : null;
    }

    private async Task<TaskResult<NhAssistantSkillState>> ChangeSkillAsync(
        string agentId,
        string skillId,
        string target,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason?.Length > NhAssistantLimits.MaxStateReasonCharacters)
        {
            return TaskResult<NhAssistantSkillState>.Failed(
                NhAssistantValidationErrors.Failed("reason", NhAssistantFieldErrors.TooLong));
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<NhAssistantSkillState>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (!registration.AgentRuns.TryGetValue(agentId, out var options) || !options.Skills.TryGetValue(skillId, out var skill))
        {
            return TaskResult<NhAssistantSkillState>.Failed(NhAssistantErrorCodes.SkillNotFound, "The agent declares no such skill.");
        }

        var stored = await store.GetSkillStatesAsync(agentId, cancellationToken);
        var current = stored.TryGetValue(skillId, out var row) ? row.RunState : NhAssistantAgentRunStates.Active;
        var cancelled = 0;
        var started = 0;
        if (current != target)
        {
            await store.SetSkillStateAsync(agentId, skillId, target, actorId, reason, cancellationToken);
            if (target == NhAssistantAgentRunStates.Stopped)
            {
                foreach (var run in await runs.GetActiveRunsAsync(agentId, cancellationToken, skillId))
                {
                    var thread = await runs.FindThreadByConversationAsync(run.ConversationId, cancellationToken);
                    var owner = thread is null
                        ? NhBackgroundOperationActor.ForAgent(agent.Definition.ActorId)
                        : NhAssistantAgentTriggers.OwnerActor(thread, run.AccountableOwnerId, agent.Definition);
                    await cancellation.CancelAsync(run, actorId, owner, cancellationToken);
                    cancelled++;
                }

                await runs.DiscardSkillWaitingInputsAsync(agentId, skillId, cancellationToken);
            }
            else if (target == NhAssistantAgentRunStates.Active && agent.RunState == NhAssistantAgentRunStates.Active)
            {
                started = await triggers.ResumeWaitingAsync(agentId, cancellationToken, skillId);
            }

            await events.AgentStateChangedAsync(
                agentId,
                actorId,
                target,
                target == NhAssistantAgentRunStates.Stopped ? cancelled : started,
                cancellationToken,
                skillId);
        }

        var states = await SkillStatesAsync(agent, options, cancellationToken);
        return TaskResult<NhAssistantSkillState>.Succeeded(states.Single(state => state.SkillId == skill.Id) with
        {
            CancelledRuns = cancelled,
            StartedRuns = started
        });
    }

    /// <summary>
    /// The skills of an agent with their states, shadow modes and today's usage.
    /// </summary>
    private async Task<IReadOnlyList<NhAssistantSkillState>> SkillStatesAsync(
        NhAssistantAgent agent,
        NhAssistantAgentRunOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Skills.Count == 0)
        {
            return [];
        }

        var agentId = agent.Id;
        var agentShadow = NhAssistantShadowModes.IsAgentShadow(options, agent.ShadowOverride);
        var stored = await store.GetSkillStatesAsync(agentId, cancellationToken);
        var dayStart = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var states = new List<NhAssistantSkillState>(options.Skills.Count);
        foreach (var skill in options.Skills.Values)
        {
            stored.TryGetValue(skill.Id, out var row);
            var runState = row?.RunState ?? NhAssistantAgentRunStates.Active;
            var usage = await runs.GetSkillUsageSinceAsync(agentId, skill.Id, null, dayStart, cancellationToken);
            states.Add(new NhAssistantSkillState(
                skill.Id,
                skill.Description,
                runState,
                row?.StateChangedAt,
                row?.StateChangedBy,
                row?.StateReason,
                runState == NhAssistantAgentRunStates.Paused
                    ? await runs.CountSkillWaitingInputsAsync(agentId, skill.Id, cancellationToken)
                    : 0,
                usage.ToolCalls,
                usage.Tokens,
                skill.DailyBudget?.ToolCalls,
                skill.DailyBudget?.Tokens,
                0,
                0)
            {
                Shadow = new NhAssistantShadowState(
                    agentShadow || NhAssistantShadowModes.IsSkillShadow(skill, row),
                    skill.Shadow,
                    row?.ShadowOverride,
                    options.ShadowOfAgentId is not null,
                    row?.ShadowChangedAt,
                    row?.ShadowChangedBy,
                    row?.ShadowReason)
            });
        }

        return states;
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
            started)
        {
            Skills = registration.AgentRuns.TryGetValue(agent.Id, out var runOptions)
                ? await SkillStatesAsync(agent, runOptions, cancellationToken)
                : [],
            ShadowOf = runOptions?.ShadowOfAgentId,
            Shadow = runOptions is null
                ? null
                : new NhAssistantShadowState(
                    NhAssistantShadowModes.IsAgentShadow(runOptions, agent.ShadowOverride),
                    runOptions.Shadow,
                    agent.ShadowOverride,
                    runOptions.ShadowOfAgentId is not null,
                    agent.ShadowChangedAt,
                    agent.ShadowChangedBy,
                    agent.ShadowReason),
            ServiceAccount = runOptions is { Accountability: NhAiAgentAccountability.ServiceAccount, ServiceAccountActorId: { } configured }
                ? new NhAssistantServiceAccountState(
                    agent.ServiceAccountOverride ?? configured,
                    configured,
                    agent.ServiceAccountOverride is not null,
                    agent.ServiceAccountChangedAt,
                    agent.ServiceAccountChangedBy,
                    agent.ServiceAccountReason)
                : null
        };
    }
}
