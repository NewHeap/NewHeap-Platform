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
    /// What closes a proposal whose object no longer exists: not applicable, with
    /// <see cref="NhAssistantErrorCodes.ProposalSubjectGone"/> and an explanation in the languages the
    /// platform ships.
    /// </summary>
    public static NhAiPreconditionResult SubjectGone { get; } = NhAiPreconditionResult.NotApplicable(
        NhAssistantErrorCodes.ProposalSubjectGone,
        culture => string.Equals(culture.TwoLetterISOLanguageName, "nl", StringComparison.OrdinalIgnoreCase)
            ? "Het object van dit voorstel bestaat niet meer."
            : "The object of this proposal no longer exists.");

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

    /// <summary>
    /// Withdraws a pending proposal for an administrator, as a last resort when nobody can decide it:
    /// it becomes <see cref="NhAssistantApprovalStatuses.Withdrawn"/> with
    /// <see cref="NhAssistantErrorCodes.ProposalWithdrawnByAdministrator"/>, the administrator and their
    /// reason, the people of the thread hear of it, the audit records it without the reason, and a run
    /// that waits for it inline is woken. Returns the withdrawn proposal, or <see langword="null"/> when
    /// someone decided it first.
    /// </summary>
    public async Task<AssistantApproval?> WithdrawByAdministratorAsync(
        AssistantApproval approval,
        AssistantThread thread,
        string actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        var withdrawn = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Withdrawn,
            new NhAssistantApprovalTransition
            {
                DecidedByActorId = actorId,
                DecidedAt = _applicationClock.GetUtcNow(),
                Reason = reason,
                ExecutionResultCode = NhAssistantErrorCodes.ProposalWithdrawnByAdministrator
            },
            cancellationToken);
        if (!withdrawn)
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
        await events.WithdrawnByAdministratorAsync(current, thread, actorId, CancellationToken.None);
        if (current.Mode == NhAssistantApprovalModes.Inline && current.RunId is not null)
        {
            await WakeAsync(current);
        }

        return current;
    }

    /// <summary>
    /// Closes a shadow proposal that the newer shadow proposal <paramref name="supersededBy"/> for the
    /// same objects replaced within its lifetime (FB3): it becomes
    /// <see cref="NhAssistantApprovalStatuses.ShadowSuperseded"/> and names its successor, as a
    /// withdrawn live proposal does, and the audit records
    /// <see cref="NhAssistantAuditEventKind.AgentProposalSuperseded"/>. Nobody decided it, so no
    /// decision time is recorded, and nobody is notified; no run waits for a shadow proposal. Returns
    /// the closed proposal, or <see langword="null"/> when another run replaced it first.
    /// </summary>
    public async Task<AssistantApproval?> SupersedeShadowAsync(
        AssistantApproval approval,
        AssistantThread thread,
        Guid supersededBy,
        string actorId,
        CancellationToken cancellationToken)
    {
        var closed = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Shadow],
            NhAssistantApprovalStatuses.ShadowSuperseded,
            new NhAssistantApprovalTransition
            {
                ExecutionResultCode = NhAssistantErrorCodes.ProposalSuperseded,
                SupersededByApprovalId = supersededBy
            },
            cancellationToken);
        if (!closed)
        {
            return null;
        }

        var current = await store.FindApprovalAsync(approval.Id, CancellationToken.None) ?? approval;
        // A shadow thread's update reaches only its own audience, the administrators.
        await workUpdates.PublishAsync(
            current.ConversationId,
            NhAssistantAgentWorkReasons.Proposal,
            current.Id,
            current.RunId,
            CancellationToken.None);
        await events.SupersededAsync(current, thread, supersededBy, actorId, CancellationToken.None);
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
/// object; see <see cref="INhAssistantProposalRevalidation"/>. A proposal whose object no longer exists
/// closes as not applicable whether or not a guard checks its tool.
/// </summary>
internal sealed class NhAssistantProposalRevalidation(
    NhAssistantRunStore runs,
    NhAssistantRegistrationState registration,
    NhAssistantProposalGuards guards,
    NhAssistantProposalInvalidation invalidation,
    NhAssistantSubjectAccessService subjects,
    ILogger<NhAssistantProposalRevalidation> logger) : INhAssistantProposalRevalidation
{
    public async Task<NhAssistantRevalidationResult> RevalidateSubjectAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
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

    /// <summary>
    /// One page of the run maintenance's check for proposals whose object no longer exists: the
    /// pending proposals of agent runs and consultations after <paramref name="after"/>, in the order of
    /// their creation, whose subjects are each described once. Returns the cursor after the page, or
    /// <see langword="null"/> when the page was the last.
    /// </summary>
    public async Task<NhAssistantGoneSubjectCheck> CloseProposalsOfGoneSubjectsAsync(
        (DateTimeOffset CreatedAt, Guid Id)? after,
        int maximum,
        CancellationToken cancellationToken)
    {
        var rows = await runs.GetOpenProposalPageAsync(null, null, null, after, maximum, cancellationToken);
        var gone = new Dictionary<NhAiSubjectRef, bool>();
        var closed = 0;
        foreach (var (approval, thread) in rows)
        {
            if (!await IsGoneAsync(thread, gone, cancellationToken))
            {
                continue;
            }

            if (await CloseGoneAsync(approval, thread, cancellationToken) is not null)
            {
                closed++;
            }
        }

        var last = rows.Count < maximum ? null : rows[^1].Approval;
        return new NhAssistantGoneSubjectCheck(
            rows.Count,
            closed,
            last is null ? null : (last.CreatedAt, last.Id));
    }

    private async Task<NhAssistantRevalidationResult> RevalidateAsync(
        IReadOnlyList<NhAssistantOpenProposalRow> rows,
        CancellationToken cancellationToken)
    {
        var invalidated = new List<NhAssistantInvalidatedProposal>();
        var checkedCount = 0;
        var failed = 0;
        var gone = new Dictionary<NhAiSubjectRef, bool>();
        foreach (var (approval, thread) in rows)
        {
            // An object that no longer exists closes its proposals, whatever their guards would say.
            if (await IsGoneAsync(thread, gone, cancellationToken))
            {
                if (await CloseGoneAsync(approval, thread, cancellationToken) is { } closedGone)
                {
                    invalidated.Add(InvalidatedOf(closedGone));
                }

                continue;
            }
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
                invalidated.Add(InvalidatedOf(closed));
            }
        }

        return new NhAssistantRevalidationResult(invalidated, checkedCount, failed);
    }

    /// <summary>
    /// Whether the thread's subject no longer exists, asked once per subject of a revalidation. A
    /// resolver that fails is logged without content and the subject counts as existing, so its
    /// proposals stay open and their guards still run.
    /// </summary>
    private async Task<bool> IsGoneAsync(
        AssistantThread thread,
        Dictionary<NhAiSubjectRef, bool> known,
        CancellationToken cancellationToken)
    {
        var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
        if (known.TryGetValue(subject, out var gone))
        {
            return gone;
        }

        try
        {
            gone = await subjects.IsGoneAsync(subject, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Whether a subject of type {SubjectType} still exists could not be checked ({ExceptionType}); its proposals stay open.",
                subject.Type,
                exception.GetType().Name);
            gone = false;
        }

        known[subject] = gone;
        return gone;
    }

    private Task<AssistantApproval?> CloseGoneAsync(
        AssistantApproval approval,
        AssistantThread thread,
        CancellationToken cancellationToken)
    {
        return invalidation.CloseAsync(
            approval,
            thread,
            thread.AgentId,
            NhAssistantProposalInvalidation.SubjectGone,
            NhAssistantInvalidationStages.Revalidation,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantProposalInvalidation.SystemActorId,
            cancellationToken);
    }

    private static NhAssistantInvalidatedProposal InvalidatedOf(AssistantApproval closed)
    {
        return new NhAssistantInvalidatedProposal(
            closed.Id,
            closed.ConversationId,
            closed.ToolId,
            closed.StaleKind ?? NhAssistantStaleKinds.Changed,
            closed.ExecutionResultCode ?? NhAssistantErrorCodes.ProposalStale);
    }
}

/// <summary>
/// What one page of the run maintenance's check for proposals whose object no longer exists did.
/// </summary>
/// <param name="Read">The pending proposals the page read.</param>
/// <param name="Closed">The proposals it closed as not applicable.</param>
/// <param name="Next">Where the check continues, or <see langword="null"/> when the page was the last.</param>
internal sealed record NhAssistantGoneSubjectCheck(
    int Read,
    int Closed,
    (DateTimeOffset CreatedAt, Guid Id)? Next);
