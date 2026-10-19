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
    string? PayloadJson);

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
        ArgumentNullException.ThrowIfNull(operations);
        operations.Add<NhAssistantAgentRunRequest, NhAssistantAgentRunOperation>(
            NhAssistantAgentRunOperationTypes.Run,
            operation => operation
                .WithRetry(3)
                .WithSoftTimeout(TimeSpan.FromMinutes(15))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        operations.Add<NhAssistantExecuteProposalRequest, NhAssistantExecuteProposalOperation>(
            NhAssistantAgentRunOperationTypes.ExecuteProposal,
            operation => operation
                .WithRetry(3)
                .WithSoftTimeout(TimeSpan.FromMinutes(5))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        operations.Add<NhAssistantExecuteProposalOnceRequest, NhAssistantExecuteProposalOnceOperation>(
            NhAssistantAgentRunOperationTypes.ExecuteProposalOnce,
            operation => operation
                .WithRetry(0)
                .WithSoftTimeout(TimeSpan.FromMinutes(5))
                .RequireIdempotency(NhBackgroundOperationIdempotency.NonIdempotent));
        operations.Add<NhAssistantRevalidateProposalsRequest, NhAssistantRevalidateProposalsOperation>(
            NhAssistantAgentRunOperationTypes.RevalidateProposals,
            operation => operation
                .WithRetry(2)
                .WithSoftTimeout(TimeSpan.FromMinutes(2))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        operations.Add<NhAssistantFireTriggerRequest, NhAssistantFireTriggerOperation>(
            NhAssistantAgentRunOperationTypes.FireTrigger,
            operation => operation
                .WithRetry(3)
                .WithSoftTimeout(TimeSpan.FromMinutes(2))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        operations.Add<NhAssistantReplayOperationRequest, NhAssistantReplayOperation>(
            NhAssistantAgentRunOperationTypes.Replay,
            operation => operation
                .WithRetry(3)
                .WithSoftTimeout(TimeSpan.FromMinutes(5))
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        return operations;
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
    NhAssistantAgentEvents events,
    NhAssistantAgentNotifier notifier,
    NhAssistantRunSummaries summaries,
    NhAssistantAgentWorkUpdates workUpdates,
    NhAssistantHandOffService handOffs,
    NhAssistantAgentTriggers triggers,
    NhAssistantRunConfigurations configurations,
    IOptions<RequestLocalizationOptions> localization,
    IOptionsMonitor<NhAssistantOptions> assistantOptions,
    ILogger<NhAssistantAgentRunOperation> logger) : INhBackgroundOperationHandler<NhAssistantAgentRunRequest>
{
    internal const string WaitCheckpointKey = "agent-run-wait";
    private const int MaxTurnsPerRun = 5;

    private static readonly string[] StartableStatuses =
    [
        NhAssistantRunStatuses.Queued,
        NhAssistantRunStatuses.Running,
        NhAssistantRunStatuses.WaitingForApproval
    ];

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
            await FailAsync(run, null, NhAssistantRunFailureClasses.Validation, FirstCode(created));
            return TaskResult.Failed(created);
        }

        var agentTurn = created.Data! with
        {
            Attempt = context.AttemptNumber,
            FencingToken = context.FencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SkillId = run.SkillId,
            SkillChosenBy = run.SkillChosenBy
        };
        var thread = agentTurn.Thread;
        if (await RefusalAsync(run, agentTurn, cancellationToken) is { } refusal)
        {
            // A stopped agent starts no run and leaves the thread as it was. A run that had not
            // started when its agent was paused ends without claiming its inputs, which wait for
            // the resume.
            await EndAsync(run, thread, NhAssistantRunStatuses.Cancelled, NhAssistantRunFailureClasses.Policy, refusal);
            return TaskResult.Succeeded();
        }
        if (context.AttemptNumber > agentTurn.Options.RetryCount + 1)
        {
            await FailAsync(run, thread, NhAssistantRunFailureClasses.Transient, NhAssistantErrorCodes.TurnFailed);
            return TaskResult.Failed(NhAssistantErrorCodes.TurnFailed, "assistant.run-retries-exhausted");
        }

        var agentId = agentTurn.Thread.AgentId;
        if (await runs.CountRunningRunsAsync(agentId, run.Id, cancellationToken) >= agentTurn.Options.MaxConcurrentRuns)
        {
            // Other runs of the agent use every slot; try again shortly without spending a retry.
            return NhBackgroundOperationRetryResult.Retry(
                "assistant-agent-concurrency",
                "assistant.agent-concurrency",
                TimeSpan.FromSeconds(30));
        }

        if (agentTurn.Options.DailyBudget is { } budget)
        {
            var dayStart = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
            var usage = await runs.GetAgentUsageSinceAsync(agentId, agentTurn.Thread.ScopeKey, dayStart, cancellationToken);
            if (usage.ToolCalls >= budget.ToolCalls || usage.Tokens >= budget.Tokens)
            {
                await FailAsync(run, thread, NhAssistantRunFailureClasses.Budget, NhAssistantErrorCodes.BudgetExhausted);
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
                await FailAsync(run, thread, NhAssistantRunFailureClasses.Budget, NhAssistantErrorCodes.BudgetExhausted);
                return TaskResult.Failed(NhAssistantErrorCodes.BudgetExhausted, "assistant.skill-budget-exhausted");
            }
        }

        await runs.SetRunAttemptAsync(run.Id, context.AttemptNumber, context.OperationId, cancellationToken);
        await runs.TrySetRunStatusAsync(run.Id, StartableStatuses, NhAssistantRunStatuses.Running, cancellationToken);
        await workUpdates.PublishAsync(run.ConversationId, NhAssistantAgentWorkReasons.Run, runId: run.Id, cancellationToken: CancellationToken.None);

        // The token ceiling bounds this execution of the run: a resumed run gets a new allowance.
        // The ceiling is the one of the skill the run works in, otherwise the agent's.
        var used = 0L;
        var result = await ContinueWaitingTurnAsync(agentTurn with { RunTokensUsed = used }, context, cancellationToken)
            ?? await RunTurnAsync(run, agentTurn with { RunTokensUsed = used }, cancellationToken);
        used += result.InputTokens + result.OutputTokens;
        agentTurn = WithSkill(agentTurn, result);

        // Messages that arrived while the run worked get further turns, a bounded number of times;
        // after a pause they wait for the resume instead.
        var turnCount = 1;
        while (result.Outcome == NhAssistantTurnStatuses.Completed
               && turnCount < MaxTurnsPerRun
               && await runs.CountWaitingInputsAsync(run.ConversationId, cancellationToken, agentTurn.SkillId ?? string.Empty) > 0
               && await agents.FindRunStateAsync(agentId, cancellationToken) == NhAssistantAgentRunStates.Active)
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

        return await CompleteAsync(run, agentTurn, result, context, cancellationToken);
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
        var inputs = await runs.ClaimQueuedInputsAsync(run.ConversationId, run.Id, cancellationToken, agentTurn.SkillId);
        // Work other agents handed off belongs to the run that claimed it, before its turn hands
        // work on itself.
        await handOffs.LinkAsync(run.Id, inputs, cancellationToken);
        var result = await runner.RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, inputs.Select(ToInput).ToArray()),
            cancellationToken);
        await RecordUsageAsync(run.Id, result, cancellationToken);
        return result;
    }

    /// <summary>
    /// Re-entry after a suspension: continues the turn that waited for an inline approval, or
    /// returns <see langword="null"/> when the run has no waiting turn.
    /// </summary>
    /// <summary>
    /// Why the run may not work now: the assistant is disabled, the agent is disabled in code or
    /// stopped, or the agent is paused and the run has not started yet. A run that started before a
    /// pause finishes.
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

    private async Task<NhAssistantAgentTurnResult?> ContinueWaitingTurnAsync(
        NhAssistantAgentTurnContext agentTurn,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        var waiting = await context.Checkpoints.GetAsync<NhAssistantRunWait>(WaitCheckpointKey, cancellationToken);
        if (waiting?.Value is not { } wait)
        {
            return null;
        }

        var signal = await runAdapter.WaitForApprovalAsync(
            context,
            WaitKey(wait.ApprovalId),
            wait.ExpiresAt,
            cancellationToken);
        if (signal.Status == NhBackgroundOperationSignalWaitStatus.Expired)
        {
            await runs.TryTransitionApprovalAsync(
                wait.ApprovalId,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantApprovalStatuses.Expired,
                NhAssistantApprovalTransition.None,
                cancellationToken);
        }

        var result = await runner.ResumeAgentTurnAsync(
            new NhAssistantAgentResumeRequest(agentTurn, wait.ApprovalId),
            cancellationToken);
        await context.Checkpoints.SetAsync<NhAssistantRunWait?>(WaitCheckpointKey, null, cancellationToken: cancellationToken);
        await RecordUsageAsync(agentTurn.RunId!.Value, result, cancellationToken);
        return result;
    }

    private async Task<TaskResult> CompleteAsync(
        AssistantRun run,
        NhAssistantAgentTurnContext agentTurn,
        NhAssistantAgentTurnResult result,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case NhAssistantAgentTurnResult.Busy:
                // A person's turn owns the thread; try again shortly without spending a retry.
                return NhBackgroundOperationRetryResult.Retry(
                    NhAssistantErrorCodes.ConversationBusy,
                    "assistant.thread-busy",
                    TimeSpan.FromSeconds(15));

            case NhAssistantTurnStatuses.WaitingForApproval when result.PendingApprovalId is { } approvalId:
                var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
                var expiresAt = approval?.ExpiresAt ?? DateTimeOffset.UtcNow;
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
                await EndAsync(run, agentTurn.Thread, NhAssistantRunStatuses.Completed, null, null, result.TurnId);
                await NotifyWaitingProposalsAsync(run, agentTurn.Thread);
                return TaskResult.Succeeded();

            case NhAssistantTurnStatuses.Cancelled:
                // A run routed to a paused or stopped skill ends cancelled with that code.
                await EndAsync(run, agentTurn.Thread, NhAssistantRunStatuses.Cancelled, NhAssistantRunFailureClasses.Cancelled, result.ErrorCode);
                if (result.ErrorCode == NhAssistantErrorCodes.MovedToShadow)
                {
                    // Routed to a skill in shadow: a run in the shadow thread takes the events, in
                    // the skill the routing recorded.
                    var routed = await runs.FindRunAsync(run.Id, CancellationToken.None) ?? run;
                    await triggers.ContinueInShadowAsync(routed, agentTurn.Thread, CancellationToken.None);
                }
                return TaskResult.Succeeded();

            default:
                var failureClass = Classify(result.ErrorCode);
                if (result.ErrorCode == NhAssistantErrorCodes.ApprovalExpired)
                {
                    // An expired inline approval ends the run unless the policy fails it.
                    await EndAsync(run, agentTurn.Thread, NhAssistantRunStatuses.Completed, null, result.ErrorCode);
                    return TaskResult.Succeeded();
                }
                if (failureClass == NhAssistantRunFailureClasses.Transient
                    && context.AttemptNumber <= agentTurn.Options.RetryCount)
                {
                    logger.LogInformation(
                        "Agent run {RunId} attempt {Attempt} failed transiently ({ErrorCode}); it is retried.",
                        run.Id,
                        context.AttemptNumber,
                        result.ErrorCode);
                    return NhBackgroundOperationRetryResult.Retry(
                        result.ErrorCode ?? NhAssistantErrorCodes.TurnFailed,
                        "assistant.run-transient-failure",
                        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, context.AttemptNumber) * 5)));
                }

                await FailAsync(run, agentTurn.Thread, failureClass, result.ErrorCode ?? NhAssistantErrorCodes.TurnFailed, result.TurnId);
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
                or NhAssistantErrorCodes.Disabled => NhAssistantRunFailureClasses.Policy,
            NhAssistantErrorCodes.SubjectNotFound
                or NhAssistantErrorCodes.AgentNotConfigured
                or NhAssistantErrorCodes.SkillNotFound => NhAssistantRunFailureClasses.Validation,
            NhAssistantErrorCodes.ProposalStale => NhAssistantRunFailureClasses.Stale,
            _ => NhAssistantRunFailureClasses.Internal
        };
    }

    private async Task FailAsync(AssistantRun run, AssistantThread? thread, string failureClass, string code, Guid? turnId = null)
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
        }
    }

    /// <summary>
    /// Ends the run once and records the end; a run that already ended stays as it was.
    /// </summary>
    private async Task<bool> EndAsync(
        AssistantRun run,
        AssistantThread? thread,
        string status,
        string? failureClass,
        string? failureCode,
        Guid? turnId = null)
    {
        var ended = await runs.TrySetRunStatusAsync(
            run.Id,
            StartableStatuses,
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

    private async Task NotifyWaitingProposalsAsync(AssistantRun run, AssistantThread thread)
    {
        var waiting = (await runs.GetRunApprovalsAsync(run.Id, CancellationToken.None))
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Pending)
            .ToArray();
        if (waiting.Length == 0)
        {
            return;
        }

        await notifier.NotifyAsync(
            NhAssistantAgentNotificationKind.RunEndedWithProposals,
            thread,
            run.Id,
            waiting[0],
            run.InitiatorActorId,
            run.Shadow,
            CancellationToken.None,
            proposalCount: waiting.Length);
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
            result.UsedRestrictedResults,
            result.ModelCalls);
    }

    private static NhAssistantAgentTurnInput ToInput(AssistantQueuedInput input)
    {
        return new NhAssistantAgentTurnInput(input.Id, input.AuthorActorId, input.TriggerId, input.Text)
        {
            PayloadJson = input.PayloadJson,
            CreatedAt = input.CreatedAt
        };
    }

    private static string FirstCode(TaskResult result)
    {
        return result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? NhAssistantErrorCodes.TurnFailed;
    }
}

/// <summary>
/// Executes an approved deferred proposal: it checks the precondition, runs the exact stored
/// invocation as the agent and records the outcome on the proposal.
/// </summary>
internal sealed class NhAssistantProposalExecutor(
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
    IOptions<RequestLocalizationOptions> localization)
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
            // A consultation's proposal executes with its asker's rights, never the agent's grants.
            DelegatedOnly = approval.ConsultationId is not null
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
            var current = await subjects.DescribeAsync(agentTurn.Subject.Subject, cancellationToken);
            var check = await precondition.CheckAsync(
                new NhAiProposalPreconditionContext(
                    proposal.ProposalId,
                    proposal.ToolId,
                    proposal.Arguments,
                    agentTurn.Subject.Subject,
                    approval.SubjectVersion,
                    current?.Version),
                cancellationToken);
            if (check.Outcome == NhAiPreconditionOutcome.Stale)
            {
                return new NhAssistantGuardCheck(check, false);
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
        var policy = closed?.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(closed.PolicyJson);
        if (policy?.StartFollowUpRun == true)
        {
            await triggers.StartFollowUpAsync(thread, closed!, closed!.DecidedByActorId, CancellationToken.None);
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
            await triggers.StartFollowUpAsync(thread, current, current.DecidedByActorId, CancellationToken.None);
        }
    }
}

internal sealed class NhAssistantExecuteProposalOperation(NhAssistantProposalExecutor executor)
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

internal sealed class NhAssistantExecuteProposalOnceOperation(NhAssistantProposalExecutor executor)
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
