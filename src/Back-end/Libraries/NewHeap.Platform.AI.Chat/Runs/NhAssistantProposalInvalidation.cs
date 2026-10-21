using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Wakes an agent run that waits for an inline proposal the platform closed itself. The ASP.NET
/// package implements it with a platform-internal background-operation signal.
/// </summary>
internal interface INhAssistantInlineRunWaker
{
    Task WakeAsync(AssistantApproval approval, CancellationToken cancellationToken);
}

/// <summary>
/// Closes proposals as stale when a guard reports that they no longer fit: the status, kind, reason
/// and explanations in one compare-and-set transition, then the live update, the audit event and,
/// for an inline proposal whose run waits, the wake-up.
/// </summary>
internal sealed class NhAssistantProposalInvalidation(
    NhAssistantRunStore runs,
    INhAssistantStore store,
    NhAssistantStaleExplanations explanations,
    NhAssistantAgentWorkUpdates workUpdates,
    NhAssistantLivePublisher live,
    NhAssistantAgentEvents events,
    IServiceProvider services,
    ILogger<NhAssistantProposalInvalidation> logger)
{
    /// <summary>
    /// The actor of invalidations nobody caused directly, such as a revalidation.
    /// </summary>
    public const string SystemActorId = "assistant-system";

    /// <summary>
    /// The application's clock, on which a proposal's decisions are recorded, closing it included;
    /// the time of an execution keeps the system clock.
    /// </summary>
    private readonly TimeProvider _applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// Moves the proposal from one of <paramref name="expectedStatuses"/> to <c>stale</c>. Returns
    /// the closed proposal, or <see langword="null"/> when another decision or worker moved it first.
    /// </summary>
    public async Task<AssistantApproval?> CloseAsync(
        AssistantApproval approval,
        AssistantThread? thread,
        string agentId,
        NhAiPreconditionResult result,
        string stage,
        IReadOnlyCollection<string> expectedStatuses,
        string actorId,
        CancellationToken cancellationToken)
    {
        var reasonCode = NhAssistantProposalGuards.ReasonCodeOf(result);
        var kind = NhAssistantStaleKinds.From(result.Kind);
        var executing = stage == NhAssistantInvalidationStages.Execution;
        var closed = await runs.TryTransitionApprovalAsync(
            approval.Id,
            expectedStatuses,
            NhAssistantApprovalStatuses.Stale,
            new NhAssistantApprovalTransition
            {
                DecidedAt = executing ? null : _applicationClock.GetUtcNow(),
                ExecutedAt = executing ? DateTimeOffset.UtcNow : null,
                ExecutionResultCode = reasonCode,
                StaleKind = kind,
                StaleExplanationsJson = explanations.Render(result.Explain),
                StaleByActorId = result.ByActorId is { Length: > 0 and <= 256 } byActorId ? byActorId : null
            },
            cancellationToken);
        if (!closed)
        {
            return null;
        }

        var current = await store.FindApprovalAsync(approval.Id, CancellationToken.None) ?? approval;
        if (thread is null)
        {
            await live.PublishChangedAsync(current.ConversationId, CancellationToken.None);
        }
        else
        {
            await workUpdates.PublishAsync(
                current.ConversationId,
                NhAssistantAgentWorkReasons.Proposal,
                current.Id,
                current.RunId,
                CancellationToken.None);
        }

        await events.InvalidatedAsync(current, agentId, thread, actorId, kind, reasonCode, stage, CancellationToken.None);
        if (!executing && current.Mode == NhAssistantApprovalModes.Inline && current.RunId is not null)
        {
            await WakeAsync(current);
        }

        return current;
    }

    /// <summary>
    /// Withdraws a pending proposal that the newer proposal <paramref name="supersededBy"/> for the
    /// same objects replaced. Returns the withdrawn proposal, or <see langword="null"/> when someone
    /// decided it first.
    /// </summary>
    public async Task<AssistantApproval?> SupersedeAsync(
        AssistantApproval approval,
        AssistantThread thread,
        Guid supersededBy,
        string actorId,
        CancellationToken cancellationToken)
    {
        var closed = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Withdrawn,
            new NhAssistantApprovalTransition
            {
                DecidedAt = _applicationClock.GetUtcNow(),
                ExecutionResultCode = NhAssistantErrorCodes.ProposalSuperseded,
                SupersededByApprovalId = supersededBy
            },
            cancellationToken);
        if (!closed)
        {
            return null;
        }

        var current = await store.FindApprovalAsync(approval.Id, CancellationToken.None) ?? approval;
        await workUpdates.PublishAsync(
            current.ConversationId,
            NhAssistantAgentWorkReasons.Proposal,
            current.Id,
            current.RunId,
            CancellationToken.None);
        await events.SupersededAsync(current, thread, supersededBy, actorId, CancellationToken.None);
        if (current.Mode == NhAssistantApprovalModes.Inline && current.RunId is not null)
        {
            await WakeAsync(current);
        }

        return current;
    }

    private async Task WakeAsync(AssistantApproval approval)
    {
        var waker = services.GetService<INhAssistantInlineRunWaker>();
        if (waker is null)
        {
            logger.LogWarning("The run waiting for stale proposal {ApprovalId} cannot be woken.", approval.Id);
            return;
        }

        try
        {
            await waker.WakeAsync(approval, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // The run's wait expires on its own; the proposal is closed either way.
            logger.LogWarning(
                "The run waiting for stale proposal {ApprovalId} could not be woken ({ExceptionType}).",
                approval.Id,
                exception.GetType().Name);
        }
    }
}

/// <summary>
/// Revalidates the open proposals of agent runs and consultations by subject or by application
/// object; see <see cref="INhAssistantProposalRevalidation"/>.
/// </summary>
internal sealed class NhAssistantProposalRevalidation(
    NhAssistantRunStore runs,
    NhAssistantRegistrationState registration,
    NhAssistantProposalGuards guards,
    NhAssistantProposalInvalidation invalidation) : INhAssistantProposalRevalidation
{
    public async Task<NhAssistantRevalidationResult> RevalidateSubjectAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!HasRevalidatingGuards())
        {
            return NhAssistantRevalidationResult.Empty;
        }

        var rows = await runs.GetOpenProposalsForSubjectAsync(
            subject,
            NhAssistantLimits.MaxRevalidatedProposals,
            cancellationToken);
        return await RevalidateAsync(rows, cancellationToken);
    }

    public async Task<NhAssistantRevalidationResult> RevalidateKeysAsync(
        IReadOnlyCollection<NhAiProposalKey> keys,
        string? scopeKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count > NhAssistantLimits.MaxRevalidationKeys || keys.Any(key => key is null))
        {
            throw new ArgumentException(
                $"A revalidation names at most {NhAssistantLimits.MaxRevalidationKeys} objects.",
                nameof(keys));
        }
        if (scopeKey is not null && (string.IsNullOrWhiteSpace(scopeKey) || scopeKey.Length > 128))
        {
            throw new ArgumentException("A scope key has 1 to 128 characters.", nameof(scopeKey));
        }
        if (keys.Count == 0 || !HasRevalidatingGuards())
        {
            return NhAssistantRevalidationResult.Empty;
        }

        var rows = await runs.GetOpenProposalsByKeysAsync(
            scopeKey ?? NhAiSubjectInfo.CreateScopeKey(null, null),
            keys,
            NhAssistantLimits.MaxRevalidatedProposals,
            cancellationToken);
        return await RevalidateAsync(rows, cancellationToken);
    }

    private bool HasRevalidatingGuards()
    {
        return registration.ProposalGuards.Any(guard => guard.Stages.HasFlag(NhAiProposalGuardStages.Revalidation));
    }

    private async Task<NhAssistantRevalidationResult> RevalidateAsync(
        IReadOnlyList<NhAssistantOpenProposalRow> rows,
        CancellationToken cancellationToken)
    {
        var invalidated = new List<NhAssistantInvalidatedProposal>();
        var checkedCount = 0;
        var failed = 0;
        foreach (var (approval, thread) in rows)
        {
            if (!guards.HasGuards(approval.ToolId, NhAiProposalGuardStages.Revalidation))
            {
                continue;
            }

            checkedCount++;
            var check = await guards.CheckAsync(
                NhAssistantProposalGuards.ContextOf(approval, thread),
                NhAiProposalGuardStages.Revalidation,
                cancellationToken);
            if (check.Failed)
            {
                failed++;
            }
            if (check.Stale is not { } stale)
            {
                continue;
            }

            var closed = await invalidation.CloseAsync(
                approval,
                thread,
                thread.AgentId,
                stale,
                NhAssistantInvalidationStages.Revalidation,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantProposalInvalidation.SystemActorId,
                cancellationToken);
            if (closed is not null)
            {
                invalidated.Add(new NhAssistantInvalidatedProposal(
                    closed.Id,
                    closed.ConversationId,
                    closed.ToolId,
                    closed.StaleKind ?? NhAssistantStaleKinds.Changed,
                    closed.ExecutionResultCode ?? NhAssistantErrorCodes.ProposalStale));
            }
        }

        return new NhAssistantRevalidationResult(invalidated, checkedCount, failed);
    }
}
