using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The background-operation payload of an agent run.
/// </summary>
public sealed record NhAssistantAgentRunRequest(Guid RunId);

/// <summary>
/// The payload that executes an approved deferred proposal of a read or idempotent tool. It is
/// retried after a failure.
/// </summary>
public sealed record NhAssistantExecuteProposalRequest(Guid ApprovalId);

/// <summary>
/// The payload that executes an approved deferred proposal of a non-idempotent tool. It is never
/// retried automatically: an interrupted execution becomes <c>execution-unknown</c>.
/// </summary>
public sealed record NhAssistantExecuteProposalOnceRequest(Guid ApprovalId);

/// <summary>
/// Revalidates open proposals after an event registered with <c>AddProposalRevalidation</c>
/// committed: those about <see cref="Subject"/> and those that change one of <see cref="Keys"/>.
/// </summary>
public sealed record NhAssistantRevalidateProposalsRequest(
    NhAiSubjectRef? Subject,
    IReadOnlyList<NhAiProposalKey> Keys,
    string? ScopeKey);

/// <summary>
/// A trigger published inside the application's transaction. The operation commits or rolls back
/// with that transaction and fires the trigger after the commit.
/// </summary>
/// <param name="InputId">The id of the input the trigger writes, so a retry fires only once.</param>
public sealed record NhAssistantFireTriggerRequest(
    Guid InputId,
    string TriggerId,
    string AgentId,
    NhAiSubjectRef Subject,
    string? InitiatorActorId,
    string Message,
    string? EventKey,
    NhAssistantTriggerConflict Conflict,
    TimeSpan Debounce,
    string? PayloadJson)
{
    /// <summary>
    /// The candidate version of the data trigger the fire evaluates in the agent's shadow thread;
    /// <see langword="null"/> for the live trigger. A candidate that was promoted, discarded or replaced
    /// before the commit fires nothing.
    /// </summary>
    public int? TriggerCandidateVersion { get; init; }
}

public static class NhAssistantAgentRunOperationTypes
{
    public const string Run = "nh-ai-agent-run";
    public const string ExecuteProposal = "nh-ai-execute-proposal";
    public const string ExecuteProposalOnce = "nh-ai-execute-proposal-once";
    public const string RevalidateProposals = "nh-ai-revalidate-proposals";
    public const string FireTrigger = "nh-ai-fire-trigger";
    public const string Replay = "nh-ai-agent-replay";

    public static bool IsAgentOperation(string operationType)
    {
        return operationType is Run or ExecuteProposal or ExecuteProposalOnce;
    }
}

public static class NhAssistantBackgroundOperationBuilderExtensions
{
    /// <summary>
    /// Registers the background operations that run agents and execute their approved proposals.
    /// Call it inside <c>WithBackgroundOperations</c> when the assistant has agents configured with
    /// <c>ConfigureAgent</c>.
    /// </summary>
    public static NhBackgroundOperationBuilder AddAssistantAgentRuns(this NhBackgroundOperationBuilder operations)
    {
        return operations.AddAssistantAgentRuns(_ => { });
    }

    /// <summary>
    /// Registers the background operations that run agents and execute their approved proposals, on
    /// the queue and with the concurrency <paramref name="configure"/> chooses, for example
    /// <c>AddAssistantAgentRuns(runs =&gt; runs.Queue("agent-runs").MaxConcurrency(4))</c>. Reserve part of
    /// the global limit for that queue with <c>WithReservedQueueConcurrency</c>, so other work cannot
    /// take every slot; <see cref="NhAssistantAgentRunOperationsBuilder.MaxConcurrency"/> keeps the agent
    /// work from taking every slot in turn.
    /// </summary>
    public static NhBackgroundOperationBuilder AddAssistantAgentRuns(
        this NhBackgroundOperationBuilder operations,
        Action<NhAssistantAgentRunOperationsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(configure);
        var placement = new NhAssistantAgentRunOperationsBuilder();
        configure(placement);
        var queue = placement.QueueName;
        if (placement.Concurrency is { } concurrency)
        {
            if (queue is null)
            {
                throw new InvalidOperationException(
                    "MaxConcurrency limits the queue of the assistant's operations; choose a queue of their own with Queue.");
            }

            if (operations.Options.QueueConcurrencyLimits.TryGetValue(queue, out var existing) && existing != concurrency)
            {
                throw new InvalidOperationException(
                    $"Queue '{queue}' already has a concurrency limit of {existing}; MaxConcurrency would change it to {concurrency}.");
            }

            operations.WithQueueConcurrency(queue, concurrency);
        }

        // The agent's Retry (0 to MaxRetryCount) decides how often a run is retried: the run's
        // operation allows the most an agent can configure, and the run's handler fails the run once
        // its failed attempts exceed the agent's setting, so a lower value is never stretched and a
        // higher one is never cut off.
        operations.Add<NhAssistantAgentRunRequest, NhAssistantAgentRunOperation>(
            NhAssistantAgentRunOperationTypes.Run,
            operation => Place(operation, queue)
                .WithRetry(NhAssistantAgentConfigurationLimits.MaxRetryCount)
                .WithSoftTimeout(TimeSpan.FromMinutes(15))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        operations.Add<NhAssistantExecuteProposalRequest, NhAssistantExecuteProposalOperation>(
            NhAssistantAgentRunOperationTypes.ExecuteProposal,
            operation => Place(operation, queue)
                .WithRetry(3)
                .WithSoftTimeout(TimeSpan.FromMinutes(5))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        operations.Add<NhAssistantExecuteProposalOnceRequest, NhAssistantExecuteProposalOnceOperation>(
            NhAssistantAgentRunOperationTypes.ExecuteProposalOnce,
            operation => Place(operation, queue)
                .WithRetry(0)
                .WithSoftTimeout(TimeSpan.FromMinutes(5))
                .RequireIdempotency(NhBackgroundOperationIdempotency.NonIdempotent));
        operations.Add<NhAssistantRevalidateProposalsRequest, NhAssistantRevalidateProposalsOperation>(
            NhAssistantAgentRunOperationTypes.RevalidateProposals,
            operation => Place(operation, queue)
                .WithRetry(2)
                .WithSoftTimeout(TimeSpan.FromMinutes(2))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        operations.Add<NhAssistantFireTriggerRequest, NhAssistantFireTriggerOperation>(
            NhAssistantAgentRunOperationTypes.FireTrigger,
            operation => Place(operation, queue)
                .WithRetry(3)
                .WithSoftTimeout(TimeSpan.FromMinutes(2))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        operations.Add<NhAssistantReplayOperationRequest, NhAssistantReplayOperation>(
            NhAssistantAgentRunOperationTypes.Replay,
            operation => Place(operation, queue)
                .WithRetry(3)
                .WithSoftTimeout(TimeSpan.FromMinutes(5))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        return operations;
    }

    private static NhBackgroundOperationDefinitionBuilder<TRequest> Place<TRequest>(
        NhBackgroundOperationDefinitionBuilder<TRequest> operation,
        string? queue)
    {
        return queue is null ? operation : operation.UseQueue(queue);
    }
}

/// <summary>
/// The queue and concurrency of the assistant's background operations, chosen in
/// <c>AddAssistantAgentRuns</c>. Without a choice they run on the default queue, as other work does,
/// within the global and default-queue limits only.
/// </summary>
public sealed class NhAssistantAgentRunOperationsBuilder
{
    internal NhAssistantAgentRunOperationsBuilder()
    {
    }

    internal string? QueueName { get; private set; }

    internal int? Concurrency { get; private set; }

    /// <summary>
    /// Places every operation of the assistant on <paramref name="queue"/>: agent runs, proposal
    /// executions, proposal revalidations, deferred trigger fires and replays
    /// (<see cref="NhAssistantAgentRunOperationTypes"/>). The process's Hangfire server serves the queue
    /// on its own. Operations queued before keep the queue they were queued on, so keep serving it until
    /// they ended.
    /// </summary>
    public NhAssistantAgentRunOperationsBuilder Queue(string queue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        QueueName = queue.Trim();
        return this;
    }

    /// <summary>
    /// At most <paramref name="maxConcurrentOperations"/> of the assistant's operations run at the same
    /// time on this processor: the concurrency limit of the queue chosen with <see cref="Queue"/>, as
    /// <c>WithQueueConcurrency</c> sets it. Agents' own <c>maxConcurrentRuns</c> still applies per agent.
    /// </summary>
    public NhAssistantAgentRunOperationsBuilder MaxConcurrency(int maxConcurrentOperations)
    {
        if (maxConcurrentOperations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentOperations));
        }

        Concurrency = maxConcurrentOperations;
        return this;
    }
}

/// <summary>
/// Checkpoint of a run that waits for an inline approval.
/// </summary>
internal sealed record NhAssistantRunWait(Guid ApprovalId, DateTimeOffset ExpiresAt);

/// <summary>
/// Builds the agent side of a run turn from stored rows: thread, subject, run configuration and
/// the run's accountable party.
/// </summary>
internal sealed class NhAssistantAgentTurnFactory(
    NhAssistantRunStore runs,
    NhAssistantSubjectAccessService subjects,
    NhAssistantRegistrationState registration)
{
    public async Task<TaskResult<NhAssistantAgentTurnContext>> CreateAsync(
        Guid conversationId,
        Guid? runId,
        string triggerId,
        string? initiatorActorId,
        string accountableOwnerId,
        CancellationToken cancellationToken)
    {
        var thread = await runs.FindThreadByConversationAsync(conversationId, cancellationToken);
        if (thread is null)
        {
            return TaskResult<NhAssistantAgentTurnContext>.Failed(
                NhAssistantErrorCodes.ConversationNotFound,
                "The agent thread was not found.");
        }

        if (!registration.AgentRuns.TryGetValue(thread.AgentId, out var options))
        {
            return TaskResult<NhAssistantAgentTurnContext>.Failed(
                NhAssistantErrorCodes.AgentNotConfigured,
                "The agent has no run configuration.");
        }

        var subject = await subjects.DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), cancellationToken);
        if (subject is null)
        {
            return TaskResult<NhAssistantAgentTurnContext>.Failed(
                NhAssistantErrorCodes.SubjectNotFound,
                "The subject of the agent thread no longer exists.");
        }

        return TaskResult<NhAssistantAgentTurnContext>.Succeeded(new NhAssistantAgentTurnContext(
            thread,
            subject,
            options,
            runId,
            triggerId,
            initiatorActorId,
            accountableOwnerId,
            IsRequestTurn: false));
    }
}

/// <summary>
/// Runs one agent run: it claims the waiting inputs of the thread and runs a headless turn. When the
/// turn waits for an inline approval, the operation suspends until the decision signal arrives and
/// then continues the same turn. Transient failures are retried; budget, policy and validation
/// failures are permanent.
/// </summary>
/// <summary>
/// Headless runs and executions have no request, so they use the application's default request
/// culture. Approval presentations and other texts they store then do not depend on the thread
/// or the event consumer that happened to start them.
/// </summary>
internal static class NhAssistantRunCulture
{
    public static IDisposable Use(RequestLocalizationOptions options)
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = options.DefaultRequestCulture.Culture;
        CultureInfo.CurrentUICulture = options.DefaultRequestCulture.UICulture;
        return new Restore(previous.CurrentCulture, previous.CurrentUICulture);
    }

    /// <summary>
    /// Uses the UI culture named <paramref name="uiCulture"/>, such as the one a person's turn worked
    /// in, when the application supports it (<see cref="RequestLocalizationOptions.SupportedUICultures"/>),
    /// with the supported culture of the same name for formatting or else the default one; otherwise,
    /// and without a name, the application's default request culture, as <see cref="Use(RequestLocalizationOptions)"/>.
    /// </summary>
    public static IDisposable Use(RequestLocalizationOptions options, string? uiCulture)
    {
        var supported = string.IsNullOrEmpty(uiCulture)
            ? null
            : options.SupportedUICultures?.FirstOrDefault(item => string.Equals(item.Name, uiCulture, StringComparison.OrdinalIgnoreCase));
        if (supported is null)
        {
            return Use(options);
        }

        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = options.SupportedCultures?.FirstOrDefault(item => string.Equals(item.Name, supported.Name, StringComparison.OrdinalIgnoreCase))
            ?? options.DefaultRequestCulture.Culture;
        CultureInfo.CurrentUICulture = supported;
        return new Restore(previous.CurrentCulture, previous.CurrentUICulture);
    }

    /// <summary>
    /// The name of the application's supported UI culture (<see cref="RequestLocalizationOptions.SupportedUICultures"/>)
    /// that <paramref name="uiCulture"/> names, as the application spells it, for recording the culture a
    /// person decided a proposal in (FB22): the culture of that exact name, otherwise the first supported
    /// culture of its language, as the endpoints read an <c>Accept-Language</c> that names another culture
    /// of a supported language (<c>nl</c> or <c>nl-BE</c> for <c>nl-NL</c>). <see langword="null"/> without
    /// options or a name, for a language the application does not support and for a name longer than an
    /// approval keeps.
    /// </summary>
    public static string? SupportedUiCultureName(RequestLocalizationOptions? options, string? uiCulture)
    {
        if (options?.SupportedUICultures is not { Count: > 0 } supported || string.IsNullOrWhiteSpace(uiCulture))
        {
            return null;
        }

        var name = uiCulture.Trim();
        var language = name.Split('-')[0];
        var match = supported.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? supported.FirstOrDefault(item => string.Equals(item.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase));

        return match is { Name.Length: > 0 and <= AssistantApproval.MaxDecisionCultureLength }
            ? match.Name
            : null;
    }

    private sealed class Restore(CultureInfo culture, CultureInfo uiCulture) : IDisposable
    {
        public void Dispose()
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}

internal sealed class NhAssistantAgentRunOperation(
    NhAssistantRunStore runs,
    NhAssistantAgentTurnFactory turns,
    INhAssistantTurnRunner runner,
    INhAiBackgroundOperationRunAdapter runAdapter,
    INhAssistantStore store,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentNotifier notifier,
    NhAssistantRunEnding ending,
    NhAssistantRunResultWriter results,
    NhAssistantAgentWorkUpdates workUpdates,
    NhAssistantHandOffService handOffs,
    NhAssistantAgentTriggers triggers,
    NhAssistantRunConfigurations configurations,
    NhAssistantMcpConnectionNotices connectionNotices,
    IOptions<RequestLocalizationOptions> localization,
    IOptionsMonitor<NhAssistantOptions> assistantOptions,
    ILogger<NhAssistantAgentRunOperation> logger,
    TimeProvider? timeProvider = null,
    NhBackgroundOperationPersistence? attempts = null) : INhBackgroundOperationHandler<NhAssistantAgentRunRequest>
{
    internal const string WaitCheckpointKey = "agent-run-wait";
    private const int MaxTurnsPerRun = 5;

    /// <summary>
    /// How long a run waits before it tries again when a person's turn holds its thread.
    /// </summary>
    internal static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a run waits before it tries again when other runs of its agent use every slot.
    /// </summary>
    internal static readonly TimeSpan ConcurrencyDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest a run waits before it tries again for a busy thread or a full agent.
    /// </summary>
    internal static readonly TimeSpan MaxYieldDelay = TimeSpan.FromMinutes(5);

    private static readonly string[] StartableStatuses =
    [
        NhAssistantRunStatuses.Queued,
        NhAssistantRunStatuses.Running,
        NhAssistantRunStatuses.WaitingForApproval
    ];

    /// <summary>
    /// The application's clock, which a proposal's expiry and so the run's inline wait follow; the
    /// background operation's signal wait expires on the same clock.
    /// </summary>
    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    public static string WaitKey(Guid approvalId)
    {
        return "approval-" + approvalId.ToString("N");
    }

    public async Task<TaskResult> ExecuteAsync(
        NhAssistantAgentRunRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        using var culture = NhAssistantRunCulture.Use(localization.Value);
        // A run may start on another host than the one that changed a data skill.
        await configurations.EnsureCurrentAsync(cancellationToken);
        var run = await runs.FindRunAsync(request.RunId, cancellationToken);
        if (run is null)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.RunNotFound, "assistant.run-not-found");
        }
        if (NhAssistantRunStatuses.IsTerminal(run.Status))
        {
            return TaskResult.Succeeded();
        }
        if (await triggers.MoveQueuedRunToShadowAsync(run, cancellationToken) is { } moved)
        {
            // The agent or the run's skill went into shadow after the run was queued: it works in
            // the shadow thread, so nothing live starts once shadow mode is on.
            run = moved;
        }

        var created = await turns.CreateAsync(
            run.ConversationId,
            run.Id,
            run.TriggerId,
            run.InitiatorActorId,
            run.AccountableOwnerId,
            cancellationToken);
        if (!created.Success)
        {
            await ending.FailAsync(run, null, NhAssistantRunFailureClasses.Validation, NhAssistantResults.CodeOf(created, NhAssistantErrorCodes.TurnFailed));
            return TaskResult.Failed(created);
        }

        var agentTurn = created.Data! with
        {
            Attempt = context.AttemptNumber,
            FencingToken = context.FencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SkillId = run.SkillId,
            SkillChosenBy = run.SkillChosenBy
        };
        if (run.SkillCandidate && run.SkillId is { } candidateSkill)
        {
            // The run evaluates a candidate version and works with it; once promoted, the candidate
            // is the live version. A candidate discarded or replaced before the run started is gone.
            if (agentTurn.Options.WithCandidate(candidateSkill, run.SkillVersion) is { } candidateOptions)
            {
                agentTurn = agentTurn with { Options = candidateOptions };
            }
            else if (!(agentTurn.Options.Skills.TryGetValue(candidateSkill, out var current) && current.DataVersion == run.SkillVersion))
            {
                await runs.ClaimQueuedInputsAsync(run.ConversationId, run.Id, cancellationToken, candidateSkill, candidate: true);
                await ending.EndAsync(run, agentTurn.Thread, NhAssistantRunStatuses.Cancelled, NhAssistantRunFailureClasses.Policy, NhAssistantErrorCodes.SkillVersionGone);
                return TaskResult.Succeeded();
            }
        }

        var thread = agentTurn.Thread;
        if (await RefusalAsync(run, agentTurn, cancellationToken) is { } refusal)
        {
            // A stopped agent starts no run and leaves the thread as it was. A run that had not
            // started when its agent was paused ends without claiming its inputs, which wait for
            // the resume. A trigger's candidate evaluates work as it happens and lets nothing wait:
            // its run takes its inputs along.
            if (run.TriggerCandidate)
            {
                await runs.ClaimQueuedInputsAsync(run.ConversationId, run.Id, cancellationToken, run.SkillId, run.SkillCandidate, run.TriggerId);
            }

            await ending.EndAsync(run, thread, NhAssistantRunStatuses.Cancelled, NhAssistantRunFailureClasses.Policy, refusal);
            return TaskResult.Succeeded();
        }
        // Only attempts that failed spend the run's retries: not the attempts that gave way to a busy
        // thread or a full agent, nor those that waited for an inline decision.
        var failedAttempts = await CountFailedAttemptsAsync(context, cancellationToken);
        if (failedAttempts > agentTurn.Options.RetryCount)
        {
            await ending.FailAsync(run, thread, NhAssistantRunFailureClasses.Transient, NhAssistantErrorCodes.TurnFailed);
            return TaskResult.Failed(NhAssistantErrorCodes.TurnFailed, "assistant.run-retries-exhausted");
        }

        var agentId = agentTurn.Thread.AgentId;
        if (await runs.CountRunningRunsAsync(agentId, run.Id, cancellationToken) >= agentTurn.Options.MaxConcurrentRuns)
        {
            // Other runs of the agent use every slot; try again later without spending a retry.
            throw Yield(run, "assistant-agent-concurrency", ConcurrencyDelay, context, failedAttempts);
        }

        if (agentTurn.Options.DailyBudget is { } budget)
        {
            var dayStart = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
            var usage = await runs.GetAgentUsageSinceAsync(agentId, agentTurn.Thread.ScopeKey, dayStart, cancellationToken);
            if (usage.ToolCalls >= budget.ToolCalls || usage.Tokens >= budget.Tokens)
            {
                await ending.FailAsync(run, thread, NhAssistantRunFailureClasses.Budget, NhAssistantErrorCodes.BudgetExhausted);
                return TaskResult.Failed(NhAssistantErrorCodes.BudgetExhausted, "assistant.agent-budget-exhausted");
            }
        }

        // A run its trigger put in a skill checks the skill's own budget as well; rules and triage
        // check it when they choose a skill.
        if (run.SkillId is { } skillId
            && agentTurn.Options.Skills.TryGetValue(skillId, out var skill)
            && skill.DailyBudget is { } skillBudget)
        {
            var dayStart = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
            var usage = await runs.GetSkillUsageSinceAsync(agentId, skillId, agentTurn.Thread.ScopeKey, dayStart, cancellationToken);
            if (usage.ToolCalls >= skillBudget.ToolCalls || usage.Tokens >= skillBudget.Tokens)
            {
                await ending.FailAsync(run, thread, NhAssistantRunFailureClasses.Budget, NhAssistantErrorCodes.BudgetExhausted);
                return TaskResult.Failed(NhAssistantErrorCodes.BudgetExhausted, "assistant.skill-budget-exhausted");
            }
        }

        await runs.SetRunAttemptAsync(run.Id, context.AttemptNumber, context.OperationId, cancellationToken);
        await runs.TrySetRunStatusAsync(run.Id, StartableStatuses, NhAssistantRunStatuses.Running, cancellationToken);
        await workUpdates.PublishAsync(run.ConversationId, NhAssistantAgentWorkReasons.Run, runId: run.Id, cancellationToken: CancellationToken.None);

        // The token ceiling bounds this execution of the run: a resumed run gets a new allowance.
        // The ceiling is the one of the skill the run works in, otherwise the agent's.
        var used = 0L;
        var resumed = await ContinueWaitingTurnAsync(agentTurn with { RunTokensUsed = used }, context, cancellationToken);
        var result = resumed?.Result
            ?? await RunTurnAsync(run, agentTurn with { RunTokensUsed = used }, cancellationToken);
        used += result.InputTokens + result.OutputTokens;
        agentTurn = WithSkill(agentTurn, result);

        // Messages that arrived while the run worked get further turns, a bounded number of times,
        // also while the agent is off; after a pause they wait for the resume instead.
        var turnCount = 1;
        while (result.Outcome == NhAssistantTurnStatuses.Completed
               && turnCount < MaxTurnsPerRun
               && await runs.CountWaitingInputsAsync(run.ConversationId, cancellationToken, agentTurn.SkillId ?? string.Empty) > 0
               && await agents.FindRunStateAsync(agentId, cancellationToken) is NhAssistantAgentRunStates.Active or NhAssistantAgentRunStates.Off)
        {
            if (CeilingOf(agentTurn) is { } ceiling && used >= ceiling)
            {
                // The ceiling is used; the waiting messages stay for a resume of the run.
                result = NhAssistantAgentTurnResult.Failed(NhAssistantErrorCodes.RunTokenLimit);
                break;
            }

            result = await RunTurnAsync(run, agentTurn with { RunTokensUsed = used }, cancellationToken);
            used += result.InputTokens + result.OutputTokens;
            agentTurn = WithSkill(agentTurn, result);
            turnCount++;
        }

        // Only a resumed turn ends with an expired proposal, and it takes no further turns.
        return await CompleteAsync(run, agentTurn, result, resumed?.ApprovalId, context, failedAttempts, cancellationToken);
    }

    /// <summary>
    /// The attempts of the run's operation that failed before this one, as the background operations
    /// count them for their own retries. Attempts that gave way to contention or suspended for a
    /// decision did not fail. Without the platform's persistence, for example under a test context,
    /// every earlier attempt counts.
    /// </summary>
    private async Task<int> CountFailedAttemptsAsync(INhBackgroundOperationContext context, CancellationToken cancellationToken)
    {
        return attempts is null
            ? context.AttemptNumber - 1
            : await attempts.GetFailedAttemptCountAsync(context.OperationId, cancellationToken);
    }

    /// <summary>
    /// Gives this attempt up without spending a retry, as the background operations do when a
    /// resource they need is busy: the attempt is abandoned and the operation is due again after a
    /// delay on the application's clock. The delay starts at <paramref name="delay"/> and doubles with
    /// every earlier attempt that did not fail, up to <see cref="MaxYieldDelay"/>, so a run that keeps
    /// finding its thread busy or its agent full tries less often.
    /// </summary>
    private Exception Yield(
        AssistantRun run,
        string reason,
        TimeSpan delay,
        INhBackgroundOperationContext context,
        int failedAttempts)
    {
        var earlier = Math.Clamp(context.AttemptNumber - 1 - failedAttempts, 0, 16);
        var retryAfter = TimeSpan.FromTicks(Math.Min(MaxYieldDelay.Ticks, delay.Ticks * (1L << earlier)));
        logger.LogInformation(
            "Agent run {RunId} gives way ({Reason}) and tries again in {Delay}.",
            run.Id,
            reason,
            retryAfter);
        return new NhBackgroundOperationContentionSignal(retryAfter);
    }

    /// <summary>
    /// The turn context of the next turn of a run: in the skill the last turn ended in.
    /// </summary>
    private static NhAssistantAgentTurnContext WithSkill(NhAssistantAgentTurnContext agentTurn, NhAssistantAgentTurnResult result)
    {
        return result.SkillId == agentTurn.SkillId
            ? agentTurn
            : agentTurn with { SkillId = result.SkillId, SkillChosenBy = result.SkillId is null ? null : agentTurn.SkillChosenBy ?? NhAssistantSkillChoosers.Model };
    }

    private static long? CeilingOf(NhAssistantAgentTurnContext agentTurn)
    {
        return agentTurn.SkillId is { } skillId && agentTurn.Options.Skills.TryGetValue(skillId, out var skill) && skill.MaxTokensPerRun is { } skillCeiling
            ? skillCeiling
            : agentTurn.Options.MaxTokensPerRun;
    }

    private async Task<NhAssistantAgentTurnResult> RunTurnAsync(
        AssistantRun run,
        NhAssistantAgentTurnContext agentTurn,
        CancellationToken cancellationToken)
    {
        // The run owns every input it claimed; inputs already written by an earlier attempt are
        // skipped when the turn appends them.
        var inputs = await runs.ClaimQueuedInputsAsync(
            run.ConversationId,
            run.Id,
            cancellationToken,
            agentTurn.SkillId,
            run.SkillCandidate,
            run.TriggerCandidate ? run.TriggerId : null);
        // Work other agents handed off belongs to the run that claimed it, before its turn hands
        // work on itself.
        await handOffs.LinkAsync(run.Id, inputs, cancellationToken);
        var result = await runner.RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, inputs.Select(ToInput).ToArray()),
            cancellationToken);
        await RecordUsageAsync(run.Id, result, cancellationToken);
        await KeepAnswerAsync(run.Id, run.ConversationId, result, cancellationToken);
        // Personal MCP servers the turn left out are recorded on the run and their person is told.
        await connectionNotices.RecordAsync(run.Id, agentTurn.Thread, result.MissingMcpConnections, cancellationToken);
        return result;
    }

    /// <summary>
    /// Why the run may not work now: the assistant is disabled, the agent is disabled in code or
    /// stopped, or the agent is paused and the run has not started yet. A run that started before a
    /// pause finishes, and every run of an agent that is off works: it was accepted before the agent
    /// was switched off, or a person started it.
    /// </summary>
    private async Task<string?> RefusalAsync(
        AssistantRun run,
        NhAssistantAgentTurnContext agentTurn,
        CancellationToken cancellationToken)
    {
        if (!assistantOptions.CurrentValue.Enabled)
        {
            return NhAssistantErrorCodes.Disabled;
        }
        if (!agentTurn.Options.Enabled)
        {
            return NhAssistantErrorCodes.AgentConfiguredOff;
        }

        return await agents.FindRunStateAsync(agentTurn.Thread.AgentId, cancellationToken) switch
        {
            null or NhAssistantAgentRunStates.Stopped => NhAssistantErrorCodes.AgentStopped,
            NhAssistantAgentRunStates.Paused when run.Status == NhAssistantRunStatuses.Queued => NhAssistantErrorCodes.AgentPaused,
            _ => null
        };
    }

    /// <summary>
    /// Re-entry after a suspension: continues the turn that waited for an inline approval, or
    /// returns <see langword="null"/> when the run has no waiting turn. When the wait expired, the
    /// proposal expires here unless the maintenance job expired it first; either way the people on
    /// the subject hear about it once. A wait the run already gave up is dropped instead: its
    /// proposal is no longer pending and the thread no longer waits in its turn, because the run
    /// ended without resuming it (the maintenance job ended it after its operation ended, or a person
    /// cancelled it) and was resumed afterwards. The run then starts a new turn in the thread, as after
    /// any turn that ended.
    /// </summary>
    private async Task<NhAssistantResumedTurn?> ContinueWaitingTurnAsync(
        NhAssistantAgentTurnContext agentTurn,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        var waiting = await context.Checkpoints.GetAsync<NhAssistantRunWait>(WaitCheckpointKey, cancellationToken);
        if (waiting?.Value is not { } wait)
        {
            return null;
        }

        var waited = await store.FindApprovalAsync(wait.ApprovalId, cancellationToken);
        if (waited?.Status != NhAssistantApprovalStatuses.Pending
            && (waited is null || !await runs.WaitsInTurnAsync(agentTurn.Thread.ConversationId, waited.TurnId, cancellationToken)))
        {
            logger.LogInformation(
                "Agent run {RunId} drops its wait for proposal {ApprovalId}, which it gave up when it ended, and starts a new turn.",
                agentTurn.RunId,
                wait.ApprovalId);
            await context.Checkpoints.SetAsync<NhAssistantRunWait?>(WaitCheckpointKey, null, cancellationToken: cancellationToken);
            return null;
        }

        var signal = await runAdapter.WaitForApprovalAsync(
            context,
            WaitKey(wait.ApprovalId),
            wait.ExpiresAt,
            cancellationToken);
        if (signal.Status == NhBackgroundOperationSignalWaitStatus.Expired
            && await runs.TryTransitionApprovalAsync(
                wait.ApprovalId,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantApprovalStatuses.Expired,
                NhAssistantApprovalTransition.None,
                cancellationToken))
        {
            await workUpdates.PublishAsync(
                agentTurn.Thread.ConversationId,
                NhAssistantAgentWorkReasons.Proposal,
                wait.ApprovalId,
                agentTurn.RunId,
                CancellationToken.None);
        }

        // The continued turn answers the messages the run claimed before it waited; their authors
        // decide whose personal MCP connection may serve it and whose rights it may borrow.
        var requestedBy = await runs.ListInputAuthorsAsync(agentTurn.RunId!.Value, cancellationToken);
        var result = await runner.ResumeAgentTurnAsync(
            new NhAssistantAgentResumeRequest(agentTurn with { RequestedBy = requestedBy }, wait.ApprovalId),
            cancellationToken);
        await context.Checkpoints.SetAsync<NhAssistantRunWait?>(WaitCheckpointKey, null, cancellationToken: cancellationToken);
        await RecordUsageAsync(agentTurn.RunId!.Value, result, cancellationToken);
        await KeepAnswerAsync(agentTurn.RunId!.Value, agentTurn.Thread.ConversationId, result, cancellationToken);
        await connectionNotices.RecordAsync(agentTurn.RunId!.Value, agentTurn.Thread, result.MissingMcpConnections, cancellationToken);
        return new NhAssistantResumedTurn(result, wait.ApprovalId);
    }

    /// <summary>
    /// The outcome of a continued turn and the inline proposal it waited for.
    /// </summary>
    private sealed record NhAssistantResumedTurn(NhAssistantAgentTurnResult Result, Guid ApprovalId);

    /// <summary>
    /// Keeps the answer of a turn that completed as part of the run's result (K13): people read the
    /// run's full answer, while its summary stays small for the agent's memory. A turn that waits,
    /// failed or was cancelled adds nothing; its run ends with a status and a failure code instead.
    /// </summary>
    private async Task KeepAnswerAsync(
        Guid runId,
        Guid conversationId,
        NhAssistantAgentTurnResult result,
        CancellationToken cancellationToken)
    {
        if (result.Outcome != NhAssistantTurnStatuses.Completed || result.TurnId is not { } turnId)
        {
            return;
        }

        await results.AppendTurnAsync(runId, conversationId, turnId, result.LocalizedAnswer, cancellationToken);
    }

    /// <param name="waitedApprovalId">The inline proposal a resumed turn waited for; <see langword="null"/>
    /// when the run did not resume a waiting turn.</param>
    /// <param name="failedAttempts">The attempts of the run's operation that failed before this one.</param>
    private async Task<TaskResult> CompleteAsync(
        AssistantRun run,
        NhAssistantAgentTurnContext agentTurn,
        NhAssistantAgentTurnResult result,
        Guid? waitedApprovalId,
        INhBackgroundOperationContext context,
        int failedAttempts,
        CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case NhAssistantAgentTurnResult.Busy:
                // A person's turn owns the thread; try again later without spending a retry.
                throw Yield(run, NhAssistantErrorCodes.ConversationBusy, BusyDelay, context, failedAttempts);

            case NhAssistantTurnStatuses.WaitingForApproval when result.PendingApprovalId is { } approvalId:
                var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
                var expiresAt = approval?.ExpiresAt ?? _applicationClock.GetUtcNow();
                await runs.TrySetRunStatusAsync(
                    run.Id,
                    StartableStatuses,
                    NhAssistantRunStatuses.WaitingForApproval,
                    cancellationToken,
                    turnId: result.TurnId);
                var saved = await context.Checkpoints.SetAsync(
                    WaitCheckpointKey,
                    new NhAssistantRunWait(approvalId, expiresAt),
                    cancellationToken: cancellationToken);
                if (!saved.Success)
                {
                    return saved;
                }

                await workUpdates.PublishAsync(
                    run.ConversationId,
                    NhAssistantAgentWorkReasons.Run,
                    approvalId,
                    run.Id,
                    CancellationToken.None);
                if (approval is not null)
                {
                    await notifier.NotifyAsync(
                        NhAssistantAgentNotificationKind.RunEndedWithProposals,
                        agentTurn.Thread,
                        run.Id,
                        approval,
                        run.InitiatorActorId,
                        run.Shadow,
                        CancellationToken.None,
                        proposalCount: 1);
                }

                // Suspends the operation until the decision arrives; the handler then starts again
                // and continues the waiting turn.
                await runAdapter.WaitForApprovalAsync(context, WaitKey(approvalId), expiresAt, cancellationToken);
                return TaskResult.Succeeded();

            case NhAssistantTurnStatuses.Completed:
                await ending.EndAsync(run, agentTurn.Thread, NhAssistantRunStatuses.Completed, null, null, result.TurnId);
                await ending.NotifyWaitingProposalsAsync(run, agentTurn.Thread);
                return TaskResult.Succeeded();

            case NhAssistantTurnStatuses.Cancelled:
                // A run routed to a paused or stopped skill ends cancelled with that code.
                await ending.EndAsync(run, agentTurn.Thread, NhAssistantRunStatuses.Cancelled, NhAssistantRunFailureClasses.Cancelled, result.ErrorCode);
                if (result.ErrorCode == NhAssistantErrorCodes.MovedToShadow)
                {
                    // Routed to a skill in shadow: a run in the shadow thread takes the events, in
                    // the skill the routing recorded.
                    var routed = await runs.FindRunAsync(run.Id, CancellationToken.None) ?? run;
                    await triggers.ContinueInShadowAsync(routed, agentTurn.Thread, CancellationToken.None);
                }
                return TaskResult.Succeeded();

            default:
                if (result.ErrorCode == NhAssistantErrorCodes.ApprovalExpired)
                {
                    return await EndAfterExpiryAsync(run, agentTurn.Thread, result, waitedApprovalId, cancellationToken);
                }

                var failureClass = Classify(result.ErrorCode);
                var failure = failedAttempts + 1;
                if (failureClass == NhAssistantRunFailureClasses.Transient
                    && failure <= agentTurn.Options.RetryCount)
                {
                    logger.LogInformation(
                        "Agent run {RunId} attempt {Attempt} failed transiently ({ErrorCode}); it is retried.",
                        run.Id,
                        context.AttemptNumber,
                        result.ErrorCode);
                    return NhBackgroundOperationRetryResult.Retry(
                        result.ErrorCode ?? NhAssistantErrorCodes.TurnFailed,
                        "assistant.run-transient-failure",
                        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, failure) * 5)));
                }

                await ending.FailAsync(run, agentTurn.Thread, failureClass, result.ErrorCode ?? NhAssistantErrorCodes.TurnFailed, result.TurnId);
                return TaskResult.Failed(result.ErrorCode ?? NhAssistantErrorCodes.TurnFailed, "assistant.run-failed");
        }
    }

    internal static string Classify(string? errorCode)
    {
        return errorCode switch
        {
            NhAssistantErrorCodes.ModelUnavailable or NhAssistantErrorCodes.TurnTimeout => NhAssistantRunFailureClasses.Transient,
            NhAssistantErrorCodes.BudgetExhausted or NhAssistantErrorCodes.RunTokenLimit => NhAssistantRunFailureClasses.Budget,
            NhAssistantErrorCodes.AgentDisabled
                or NhAssistantErrorCodes.AgentForbidden
                or NhAssistantErrorCodes.AgentStopped
                or NhAssistantErrorCodes.AgentPaused
                or NhAssistantErrorCodes.AgentConfiguredOff
                or NhAssistantErrorCodes.ClassificationNotPermitted
                or NhAssistantErrorCodes.ModelRegionDenied
                or NhAssistantErrorCodes.Disabled => NhAssistantRunFailureClasses.Policy,
            NhAssistantErrorCodes.SubjectNotFound
                or NhAssistantErrorCodes.AgentNotConfigured
                or NhAssistantErrorCodes.AgentInstructionsUnavailable
                or NhAssistantErrorCodes.SkillNotFound => NhAssistantRunFailureClasses.Validation,
            NhAssistantErrorCodes.ProposalStale => NhAssistantRunFailureClasses.Stale,
            _ => NhAssistantRunFailureClasses.Internal
        };
    }

    /// <summary>
    /// Ends a run whose inline proposal expired before anyone decided, as the proposal's approval
    /// policy says: <see cref="NhAiApprovalExpiryAction.FailRun"/> fails the run, and every other
    /// expiry action lets it complete without the proposal, with <c>assistant-approval-expired</c>.
    /// The maintenance job leaves a run that waits inline to this operation, also when the job
    /// expired the proposal first, so the run ends the same way, and once, whichever came first.
    /// </summary>
    private async Task<TaskResult> EndAfterExpiryAsync(
        AssistantRun run,
        AssistantThread thread,
        NhAssistantAgentTurnResult result,
        Guid? approvalId,
        CancellationToken cancellationToken)
    {
        var approval = approvalId is { } id
            ? await store.FindApprovalAsync(id, cancellationToken)
            : null;
        if (FailsRunOnExpiry(approval))
        {
            await ending.FailAsync(run, thread, NhAssistantRunFailureClasses.Policy, NhAssistantErrorCodes.ApprovalExpired, result.TurnId);
            return TaskResult.Failed(NhAssistantErrorCodes.ApprovalExpired, "assistant.run-approval-expired");
        }

        await ending.EndAsync(run, thread, NhAssistantRunStatuses.Completed, null, NhAssistantErrorCodes.ApprovalExpired);
        return TaskResult.Succeeded();
    }

    /// <summary>
    /// True when the policy the proposal was made under fails its run on expiry.
    /// </summary>
    private static bool FailsRunOnExpiry(AssistantApproval? approval)
    {
        return approval?.PolicyJson is { } policyJson
            && NhAssistantApprovalPolicySnapshot.Deserialize(policyJson).OnExpiry == NhAiApprovalExpiryAction.FailRun;
    }

    private async Task RecordUsageAsync(Guid runId, NhAssistantAgentTurnResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome == NhAssistantAgentTurnResult.Busy)
        {
            return;
        }

        await runs.RecordRunUsageAsync(
            runId,
            result.ToolCalls,
            result.InputTokens,
            result.OutputTokens,
            result.ProposalCount,
            cancellationToken,
            // A resumed turn is the model's; it keeps the kind the run started with.
            result.Kind == NhAssistantRunKinds.Model ? null : result.Kind,
            result.UsedRestrictedResults);
    }

    private static NhAssistantAgentTurnInput ToInput(AssistantQueuedInput input)
    {
        return new NhAssistantAgentTurnInput(input.Id, input.AuthorActorId, input.TriggerId, input.Text)
        {
            PayloadJson = input.PayloadJson,
            CreatedAt = input.CreatedAt,
            Files = NhAssistantContent.DeserializeFiles(input.FilesJson)
        };
    }

}

/// <summary>
/// Executes an approved deferred proposal: it checks the precondition, runs the exact stored
/// invocation as the agent and records the outcome on the proposal. The proposal of a tool the
/// application executes (<see cref="NhAssistantBuilder.ExecuteInApplication"/>) passes the same checks
/// and is then handed to the application instead (<see cref="NhAssistantApplicationExecutions"/>).
/// </summary>
internal sealed class NhAssistantProposalExecution(
    INhAssistantStore store,
    NhAssistantRunStore runs,
    NhAssistantAgentTurnFactory turns,
    INhAssistantTurnRunner runner,
    INhAiProposalPrecondition precondition,
    NhAssistantSubjectAccessService subjects,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentEvents events,
    NhAssistantAgentNotifier notifier,
    NhAssistantAgentTriggers triggers,
    NhAssistantAgentWorkUpdates workUpdates,
    NhAssistantProposalGuards guards,
    NhAssistantProposalInvalidation invalidation,
    NhAssistantRegistrationState registration,
    IOptionsMonitor<NhAssistantOptions> assistantOptions,
    IOptions<RequestLocalizationOptions> localization,
    NhAssistantApplicationExecutions applicationExecutions)
{
    private static readonly string[] ExecutableStatuses =
    [
        NhAssistantApprovalStatuses.Approved,
        NhAssistantApprovalStatuses.Executing
    ];

    public async Task<TaskResult> ExecuteAsync(
        Guid approvalId,
        INhBackgroundOperationContext context,
        bool fenced,
        CancellationToken cancellationToken)
    {
        using var culture = NhAssistantRunCulture.Use(localization.Value);
        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        if (approval is null)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ApprovalNotFound, "assistant.approval-not-found");
        }
        if (!ExecutableStatuses.Contains(approval.Status))
        {
            // Executed, failed, stale or resolved before: nothing to do again.
            return TaskResult.Succeeded();
        }

        var thread = await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        if (thread is not null
            && (!assistantOptions.CurrentValue.Enabled
                || (registration.AgentRuns.TryGetValue(thread.AgentId, out var runOptions) && !runOptions.Enabled)
                || await agents.FindAsync(thread.AgentId, includeDisabled: false, cancellationToken) is null))
        {
            // A stopped or code-disabled agent, or a disabled assistant, keeps approved proposals
            // waiting; the maintenance job queues their execution again once the agent works. A
            // paused agent's proposals execute.
            await runs.TryTransitionApprovalAsync(
                approval.Id,
                ExecutableStatuses,
                NhAssistantApprovalStatuses.Approved,
                new NhAssistantApprovalTransition { ClearExecutionOperation = true },
                cancellationToken);
            return TaskResult.Succeeded();
        }

        var proposal = Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
        var created = await turns.CreateAsync(
            approval.ConversationId,
            approval.RunId,
            NhAssistantTriggerIds.ProposalDecided,
            approval.InitiatorActorId,
            proposal.AccountableOwnerId,
            cancellationToken);
        if (!created.Success)
        {
            await CloseAsync(approval, thread, NhAssistantApprovalStatuses.ExecutionFailed, NhAssistantErrorCodes.SubjectNotFound);
            return TaskResult.Succeeded();
        }

        var agentTurn = created.Data! with
        {
            Attempt = context.AttemptNumber,
            FencingToken = fenced
                ? context.FencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null,
            // A consultation's proposal executes with its asker's rights, never the agent's grants, unless
            // its route executes with the agent's own rights: then never with the asker's.
            DelegatedOnly = approval.ConsultationId is not null && !AgentRights(approval),
            AgentRightsOnly = AgentRights(approval)
        };

        var check = await CheckBeforeExecutionAsync(approval, proposal, agentTurn, cancellationToken);
        if (check.Stale is { } stale)
        {
            await CloseStaleAsync(approval, agentTurn.Thread, stale);
            return TaskResult.Succeeded();
        }
        if (check.Failed)
        {
            // Whether the proposal still fits is unknown: it stays approved, and the maintenance job
            // queues its execution again, which checks again.
            await runs.TryTransitionApprovalAsync(
                approval.Id,
                ExecutableStatuses,
                NhAssistantApprovalStatuses.Approved,
                new NhAssistantApprovalTransition { ClearExecutionOperation = true, CountExecutionAttempt = true },
                cancellationToken);
            return TaskResult.Succeeded();
        }

        if (registration.TryGetApplicationExecution(approval.ToolId, out var applicationExecution))
        {
            // The application executes the tool: it takes the approved payload and reports later.
            await applicationExecutions.HandOffAsync(
                approval,
                agentTurn.Thread,
                proposal,
                applicationExecution,
                ExecutableStatuses,
                context.OperationId,
                cancellationToken);
            return TaskResult.Succeeded();
        }

        await runs.TryTransitionApprovalAsync(
            approval.Id,
            ExecutableStatuses,
            NhAssistantApprovalStatuses.Executing,
            new NhAssistantApprovalTransition
            {
                ExecutionOperationId = context.OperationId,
                CountExecutionAttempt = true
            },
            cancellationToken);

        var execution = await runner.ExecuteProposalAsync(
            new NhAssistantProposalExecutionRequest(agentTurn, approval.Id),
            cancellationToken);
        // A tool that threw may have written before it failed. A tool that may run only once
        // therefore waits for a person instead of being reported as failed.
        var status = execution.Succeeded
            ? NhAssistantApprovalStatuses.Executed
            : execution.Threw && fenced
                ? NhAssistantApprovalStatuses.ExecutionUnknown
                : NhAssistantApprovalStatuses.ExecutionFailed;
        await CloseAsync(approval, thread, status, execution.ResultCode);
        return TaskResult.Succeeded();
    }

    /// <summary>
    /// Whether the proposal is a consultation's that executes with the agent's own rights (option B).
    /// </summary>
    private static bool AgentRights(AssistantApproval approval)
    {
        return NhAssistantConsultationRoutes.Of(approval.ConsultationId, approval.Mode, approval.GrantSource)
            == NhAssistantConsultationRoutes.SubjectAgentRights;
    }

    /// <summary>
    /// The checks right before execution: the subject-version precondition when the policy requires
    /// an unchanged subject, then the application's guards for the execution stage.
    /// </summary>
    private async Task<NhAssistantGuardCheck> CheckBeforeExecutionAsync(
        AssistantApproval approval,
        NhAiProposal proposal,
        NhAssistantAgentTurnContext agentTurn,
        CancellationToken cancellationToken)
    {
        var policy = approval.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
        if (policy?.RequireUnchangedSubject == true)
        {
            // The decision runs the same check before it records an approval (LS16).
            var check = await NhAssistantSubjectChangeCheck.CheckAsync(
                precondition,
                subjects,
                approval,
                proposal,
                agentTurn.Subject.Subject,
                cancellationToken);
            if (check.Result.Outcome == NhAiPreconditionOutcome.Stale)
            {
                return new NhAssistantGuardCheck(check.Result, false);
            }
        }

        return await guards.CheckAsync(
            NhAssistantProposalGuards.ContextOf(approval, agentTurn.Thread),
            NhAiProposalGuardStages.Execution,
            cancellationToken);
    }

    private async Task CloseStaleAsync(AssistantApproval approval, AssistantThread thread, NhAiPreconditionResult stale)
    {
        var closed = await invalidation.CloseAsync(
            approval,
            thread,
            thread.AgentId,
            stale,
            NhAssistantInvalidationStages.Execution,
            ExecutableStatuses,
            NhAssistantAgentEvents.AgentActorPrefix + thread.AgentId,
            CancellationToken.None);
        if (closed is not null)
        {
            await notifier.ConsultationProposalEndedAsync(thread, closed, CancellationToken.None);
        }
        var policy = closed?.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(closed.PolicyJson);
        if (policy?.StartFollowUpRun == true)
        {
            await triggers.StartFollowUpAsync(thread, closed!, CancellationToken.None);
        }
    }

    private async Task CloseAsync(AssistantApproval approval, AssistantThread? thread, string status, string? code)
    {
        var closed = await runs.TryTransitionApprovalAsync(
            approval.Id,
            ExecutableStatuses,
            status,
            new NhAssistantApprovalTransition
            {
                ExecutedAt = DateTimeOffset.UtcNow,
                ExecutionResultCode = code
            },
            CancellationToken.None);
        if (!closed || thread is null)
        {
            return;
        }

        await workUpdates.PublishAsync(
            approval.ConversationId,
            NhAssistantAgentWorkReasons.Proposal,
            approval.Id,
            approval.RunId,
            CancellationToken.None);
        var current = await store.FindApprovalAsync(approval.Id, CancellationToken.None) ?? approval;
        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalExecuted,
            thread,
            current,
            NhAssistantAgentEvents.AgentActorPrefix + thread.AgentId,
            status,
            CancellationToken.None);
        await notifier.ConsultationProposalEndedAsync(thread, current, CancellationToken.None);
        if (status == NhAssistantApprovalStatuses.ExecutionUnknown)
        {
            await notifier.NotifyAsync(
                NhAssistantAgentNotificationKind.ExecutionUnknown,
                thread,
                current.RunId,
                current,
                current.InitiatorActorId,
                shadow: false,
                CancellationToken.None);
        }

        var policy = current.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(current.PolicyJson);
        if (policy?.StartFollowUpRun == true)
        {
            await triggers.StartFollowUpAsync(thread, current, CancellationToken.None);
        }
    }
}

internal sealed class NhAssistantExecuteProposalOperation(NhAssistantProposalExecution executor)
    : INhBackgroundOperationHandler<NhAssistantExecuteProposalRequest>
{
    public Task<TaskResult> ExecuteAsync(
        NhAssistantExecuteProposalRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        // Read and idempotent tools replay safely, so a retry must not conflict on the fence.
        return executor.ExecuteAsync(request.ApprovalId, context, fenced: false, cancellationToken);
    }
}

/// <summary>
/// Runs a declarative revalidation once the application's change committed. Revalidation is
/// idempotent, so a retry closes nothing twice.
/// </summary>
internal sealed class NhAssistantRevalidateProposalsOperation(INhAssistantProposalRevalidation revalidation)
    : INhBackgroundOperationHandler<NhAssistantRevalidateProposalsRequest>
{
    public async Task<TaskResult> ExecuteAsync(
        NhAssistantRevalidateProposalsRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        if (request.Subject is { } subject)
        {
            await revalidation.RevalidateSubjectAsync(subject, cancellationToken);
        }
        if (request.Keys is { Count: > 0 } keys)
        {
            await revalidation.RevalidateKeysAsync(keys, request.ScopeKey, cancellationToken);
        }

        return TaskResult.Succeeded();
    }
}

/// <summary>
/// Fires a trigger once the application's transaction that published it committed, through the
/// same path as a trigger published after the commit.
/// </summary>
internal sealed class NhAssistantFireTriggerOperation(NhAssistantAgentTriggers triggers)
    : INhBackgroundOperationHandler<NhAssistantFireTriggerRequest>
{
    public async Task<TaskResult> ExecuteAsync(
        NhAssistantFireTriggerRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        // Rejections are outcomes of the trigger, audited like any other; they are no failure.
        await triggers.FireDeferredAsync(request, cancellationToken);
        return TaskResult.Succeeded();
    }
}

internal sealed class NhAssistantExecuteProposalOnceOperation(NhAssistantProposalExecution executor)
    : INhBackgroundOperationHandler<NhAssistantExecuteProposalOnceRequest>
{
    public Task<TaskResult> ExecuteAsync(
        NhAssistantExecuteProposalOnceRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        return executor.ExecuteAsync(request.ApprovalId, context, fenced: true, cancellationToken);
    }
}

/// <summary>
/// The default precondition: a proposal is stale when the subject version changed since it was
/// recorded. Without versions it is never stale.
/// </summary>
internal sealed class NhAssistantSubjectVersionPrecondition : INhAiProposalPrecondition
{
    public ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalPreconditionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RecordedSubjectVersion is null || context.CurrentSubjectVersion is null)
        {
            return ValueTask.FromResult(NhAiPreconditionResult.Satisfied);
        }

        return ValueTask.FromResult(
            string.Equals(context.RecordedSubjectVersion, context.CurrentSubjectVersion, StringComparison.Ordinal)
                ? NhAiPreconditionResult.Satisfied
                : NhAiPreconditionResult.Stale("subject-changed"));
    }
}
