using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Decides whether a person may decide a proposal of an agent thread: the person may see the thread
/// (for the whole application: passes the agent's view policy), the approval policy's approvers,
/// four-eyes, the tool's approve policy, and never the agent itself. Every check is live.
/// </summary>
internal sealed class NhAssistantApproverCheck(
    NhAssistantSubjectAccessService subjects,
    IAuthorizationService authorization,
    IEnumerable<INhAiToolCatalog> catalogs)
{
    private readonly IReadOnlyList<INhAiToolCatalog> _catalogs = catalogs.ToArray();

    public Task<TaskResult> CheckAsync(
        AssistantApproval approval,
        AssistantThread thread,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        return CheckAsync(approval, thread, actorId, user, null, cancellationToken);
    }

    /// <summary>
    /// The same check with the thread's subject already described and resolved for
    /// <paramref name="actorId"/>, so a caller that checks several proposals of one subject asks the
    /// resolver once; without it the check asks the resolver itself.
    /// </summary>
    public async Task<TaskResult> CheckAsync(
        AssistantApproval approval,
        AssistantThread thread,
        string actorId,
        ClaimsPrincipal user,
        NhAssistantResolvedSubject? resolved,
        CancellationToken cancellationToken)
    {
        var policy = approval.PolicyJson is null
            ? NhAiApprovalPolicy.TriggerDefault
            : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
        var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
        if (actorId.StartsWith("assistant-agent:", StringComparison.Ordinal))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ApprovalDecisionInvalid, "An agent never decides its own proposal.");
        }
        // A proposal of the whole application is for people who see the agent's work there; to anyone
        // else it reads as unknown, as GET approvals/{id} answers.
        if (!await MaySeeAsync(thread, actorId, cancellationToken))
        {
            return NotVisible();
        }
        if (policy.FourEyes && string.Equals(actorId, approval.InitiatorActorId, StringComparison.Ordinal))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.FourEyesRequired, "Another person must decide this proposal.");
        }

        var info = resolved is null ? await subjects.DescribeAsync(subject, cancellationToken) : resolved.Info;
        var resource = new NhAiSubjectAuthorizationResource(subject, thread.ScopeKey, info?.DivisionId ?? thread.DivisionId, "approval");
        var access = resolved is null ? await subjects.ResolveAsync(subject, actorId, cancellationToken) : resolved.Access;
        var qualifies = false;
        foreach (var rule in policy.Approvers)
        {
            qualifies = rule switch
            {
                // A proposal a run placed at another object is that object's to decide: the run's
                // initiator decides it only as one of the object's approvers.
                NhAiInitiatorApproverRule => approval.PlacedFromConversationId is null
                    && string.Equals(actorId, approval.InitiatorActorId, StringComparison.Ordinal),
                NhAiSubjectRoleApproverRule role => access.Has(role.Role)
                    && (role.NamedRole is null || access.NamedRoles.Contains(role.NamedRole)),
                NhAiPolicyApproverRule named => (await authorization.AuthorizeAsync(user, resource, named.PolicyName)).Succeeded,
                _ => false
            };
            if (qualifies)
            {
                break;
            }
        }
        if (!qualifies)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ApprovalPolicyDenied, "The caller is not an approver of this proposal.");
        }

        // The tool's approve policy keeps a person from approving what they may not do themselves.
        var descriptor = FindDescriptor(approval.ToolId);
        foreach (var toolPolicy in descriptor?.ApproverPolicies ?? [])
        {
            if (!(await authorization.AuthorizeAsync(user, resource, toolPolicy)).Succeeded)
            {
                return TaskResult.Failed(NhAssistantErrorCodes.ApprovalPolicyDenied, "The caller may not approve this tool.");
            }
        }

        return TaskResult.Succeeded();
    }

    /// <summary>
    /// Whether the person may see the proposals of the thread at all: always for the application's own
    /// subjects (their roles decide the rest), for the whole application only when the person passes
    /// the agent's view policy.
    /// </summary>
    public async Task<bool> MaySeeAsync(AssistantThread thread, string actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return await subjects.MayReadAgentThreadAsync(
            new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
            thread.AgentId,
            actorId,
            cancellationToken);
    }

    /// <summary>
    /// The failure for a proposal the person may not see: it reads as an unknown one.
    /// </summary>
    public static TaskResult NotVisible()
    {
        return TaskResult.Failed(NhAssistantErrorCodes.ApprovalNotFound, "The proposal was not found.");
    }

    public NhAiToolDescriptor? FindDescriptor(string toolId)
    {
        return _catalogs
            .SelectMany(catalog => catalog.Descriptors)
            .Where(descriptor => string.Equals(descriptor.Id, toolId, StringComparison.Ordinal))
            .OrderByDescending(descriptor => descriptor.Version)
            .FirstOrDefault();
    }
}

/// <summary>
/// A thread's subject as its resolver described it and as it resolved one caller's role.
/// </summary>
internal sealed record NhAssistantResolvedSubject(NhAiSubjectInfo? Info, NhAiSubjectAccess Access);

/// <summary>
/// Records decisions about proposals of agent threads and starts what follows: the execution of
/// an approved deferred proposal, the signal that wakes a run waiting inline, and an optional
/// follow-up run.
/// </summary>
internal sealed class NhAssistantProposalDecisions(
    INhAssistantStore store,
    NhAssistantRunStore runs,
    NhAssistantApproverCheck approvers,
    NhAssistantAgentTriggers triggers,
    NhAssistantRegistrationState registration,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentEvents events,
    NhAssistantAgentWorkUpdates workUpdates,
    NhAssistantProposalGuards guards,
    NhAssistantProposalInvalidation invalidation,
    IServiceProvider services,
    IEnumerable<INhAssistantBusinessAuditSink> businessSinks,
    ILogger<NhAssistantProposalDecisions> logger)
{
    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _businessSinks = businessSinks.ToArray();

    /// <summary>
    /// The application's clock (a registered <see cref="TimeProvider"/>, otherwise the system
    /// clock), which a proposal's lifetime follows: a decision is refused once it reaches the
    /// expiry and is recorded at its time. Audit events keep the system clock.
    /// </summary>
    private readonly TimeProvider _applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// Background operations are needed only once an agent runs; hosts without agents do not
    /// register them.
    /// </summary>
    private INhBackgroundOperationService Operations => services.GetRequiredService<INhBackgroundOperationService>();

    private INhBackgroundOperationSignalService Signals => services.GetRequiredService<INhBackgroundOperationSignalService>();

    /// <summary>
    /// Decides the proposal as the decision endpoints do: through the <c>web</c> channel, without an
    /// idempotency key.
    /// </summary>
    /// <param name="tenantId">
    /// The caller's tenant; a caller bound to a tenant that the single-tenant mode does not name finds
    /// no proposal of the whole application (<see cref="NhAssistantWholeApplicationTenancy"/>).
    /// </param>
    public Task<TaskResult<AssistantApproval>> DecideAsync(
        Guid approvalId,
        string actorId,
        ClaimsPrincipal user,
        bool approve,
        string? expectedProposalHash,
        string? reason,
        CancellationToken cancellationToken,
        string? tenantId = null)
    {
        return DecideAsync(
            approvalId,
            actorId,
            user,
            approve,
            expectedProposalHash,
            reason,
            NhAssistantDecisionOrigin.Web,
            cancellationToken,
            tenantId);
    }

    /// <summary>
    /// The one decision of a proposal of an agent thread, for the decision endpoints and
    /// <see cref="INhAssistantProposalDecisionService"/> alike: the checks, the transition, the audit
    /// with <paramref name="origin"/>'s channel and key, and what follows the decision.
    /// </summary>
    /// <param name="tenantId">The deciding person's tenant, as for the web decision above.</param>
    public async Task<TaskResult<AssistantApproval>> DecideAsync(
        Guid approvalId,
        string actorId,
        ClaimsPrincipal user,
        bool approve,
        string? expectedProposalHash,
        string? reason,
        NhAssistantDecisionOrigin origin,
        CancellationToken cancellationToken,
        string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        var thread = approval is null ? null : await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        if (approval is null
            || thread is null
            || NhAssistantWholeApplicationTenancy.KeepsOut(registration, thread, tenantId)
            || !await approvers.MaySeeAsync(thread, actorId, cancellationToken))
        {
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalNotFound, "The proposal was not found.");
        }
        if (expectedProposalHash is not null
            && !string.Equals(approval.ProposalHash, expectedProposalHash, StringComparison.Ordinal))
        {
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ProposalHashMismatch, "The proposal changed.");
        }
        if (approval.Status != NhAssistantApprovalStatuses.Pending)
        {
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalNotPending, "The proposal is no longer pending.");
        }
        if (approval.Mode == NhAssistantApprovalModes.Inline && approval.RunId is null)
        {
            // A person's own turn waits for this decision in the conversation stream; a consultation's
            // proposal is decided by the person who asked, in their own conversation.
            return TaskResult<AssistantApproval>.Failed(
                approval.ConsultationId is null ? NhAssistantErrorCodes.ApprovalDecisionInvalid : NhAssistantErrorCodes.ConsultAskerDecides,
                "Decide this proposal in the conversation.");
        }

        var now = _applicationClock.GetUtcNow();
        if (approval.ExpiresAt <= now)
        {
            await runs.TryTransitionApprovalAsync(
                approval.Id,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantApprovalStatuses.Expired,
                NhAssistantApprovalTransition.None,
                cancellationToken);
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalExpired, "The proposal expired.");
        }

        var check = await approvers.CheckAsync(approval, thread, actorId, user, cancellationToken);
        if (!check.Success)
        {
            return TaskResult<AssistantApproval>.Failed(check);
        }
        // The execution's own check first, then the application's guards, in the execution's order.
        if (approve && await RefuseChangedSubjectAsync(approval, thread, actorId, cancellationToken) is { } changed)
        {
            return changed;
        }
        if (approve && await RefuseStaleAsync(approval, thread, actorId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var status = approve ? NhAssistantApprovalStatuses.Approved : NhAssistantApprovalStatuses.Rejected;
        var decided = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            status,
            new NhAssistantApprovalTransition
            {
                DecidedByActorId = actorId,
                DecidedAt = now,
                ApprovalExpiresAt = approve ? approval.ExpiresAt : null,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
            },
            cancellationToken);
        if (!decided)
        {
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalNotPending, "The proposal is no longer pending.");
        }

        approval = (await store.FindApprovalAsync(approval.Id, cancellationToken))!;
        await RecordAsync(approval, thread, actorId, approve, origin, cancellationToken);
        if (approval.Mode == NhAssistantApprovalModes.Deferred)
        {
            if (approve)
            {
                await EnqueueExecutionAsync(approval, thread, cancellationToken);
            }
            else
            {
                await services.GetRequiredService<NhAssistantAgentNotifier>().ConsultationProposalEndedAsync(thread, approval, cancellationToken);
            }
        }
        else
        {
            await SignalRunAsync(approval, actorId, approve, cancellationToken);
        }

        // An approved deferred proposal follows up once its execution ended; a rejection now.
        var policy = approval.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
        if (policy?.StartFollowUpRun == true && approval.Mode == NhAssistantApprovalModes.Deferred && !approve)
        {
            await triggers.StartFollowUpAsync(thread, approval, actorId, cancellationToken);
        }

        await workUpdates.PublishAsync(
            approval.ConversationId,
            NhAssistantAgentWorkReasons.Proposal,
            approval.Id,
            approval.RunId,
            cancellationToken);
        return TaskResult<AssistantApproval>.Succeeded(approval);
    }

    /// <summary>
    /// Refuses an approval of a proposal whose policy requires an unchanged subject when the object
    /// changed since it was proposed, by the precondition the execution runs (LS16), and closes it as
    /// the execution would: stale, the asker of a consultation told, and a deferred proposal's
    /// follow-up run started when its policy asks for one. A rejection needs no check.
    /// </summary>
    private async Task<TaskResult<AssistantApproval>?> RefuseChangedSubjectAsync(
        AssistantApproval approval,
        AssistantThread thread,
        string actorId,
        CancellationToken cancellationToken)
    {
        var policy = approval.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
        if (policy?.RequireUnchangedSubject != true)
        {
            return null;
        }

        var change = await CheckSubjectAsync(approval, thread, cancellationToken);
        if (change.Result.Outcome != NhAiPreconditionOutcome.Stale)
        {
            return null;
        }

        var closed = await invalidation.CloseAsync(
            approval,
            thread,
            thread.AgentId,
            change.Result,
            NhAssistantInvalidationStages.Decision,
            [NhAssistantApprovalStatuses.Pending],
            actorId,
            CancellationToken.None);
        if (closed is not null)
        {
            await services.GetRequiredService<NhAssistantAgentNotifier>().ConsultationProposalEndedAsync(thread, closed, CancellationToken.None);
            if (policy.StartFollowUpRun && closed.Mode == NhAssistantApprovalModes.Deferred)
            {
                await triggers.StartFollowUpAsync(thread, closed, actorId, CancellationToken.None);
            }
        }

        return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ProposalStale, "The object changed since the proposal.");
    }

    /// <summary>
    /// The precondition the execution runs, for the object the proposal waits at.
    /// </summary>
    private Task<NhAssistantSubjectChange> CheckSubjectAsync(
        AssistantApproval approval,
        AssistantThread thread,
        CancellationToken cancellationToken)
    {
        return NhAssistantSubjectChangeCheck.CheckAsync(
            services.GetRequiredService<INhAiProposalPrecondition>(),
            services.GetRequiredService<NhAssistantSubjectAccessService>(),
            approval,
            Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson),
            new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
            cancellationToken);
    }

    /// <summary>
    /// Runs the proposal guards before an approval. A stale proposal closes and the approval is
    /// refused; a guard that fails keeps the proposal open and refuses the approval for now. A
    /// rejection needs no check.
    /// </summary>
    private async Task<TaskResult<AssistantApproval>?> RefuseStaleAsync(
        AssistantApproval approval,
        AssistantThread thread,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!guards.HasGuards(approval.ToolId, NhAiProposalGuardStages.Decision))
        {
            return null;
        }

        var check = await guards.CheckAsync(
            NhAssistantProposalGuards.ContextOf(approval, thread),
            NhAiProposalGuardStages.Decision,
            cancellationToken);
        if (check.Stale is { } stale)
        {
            await invalidation.CloseAsync(
                approval,
                thread,
                thread.AgentId,
                stale,
                NhAssistantInvalidationStages.Decision,
                [NhAssistantApprovalStatuses.Pending],
                actorId,
                CancellationToken.None);
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ProposalStale, "The proposal no longer applies.");
        }

        return check.Failed
            ? TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ProposalCheckFailed,
                "Whether the proposal still applies could not be checked.")
            : null;
    }

    /// <summary>
    /// What a decision of the person would meet now, without deciding: the proposal's hash and
    /// expiry, whether four eyes apply, how many approvals it still needs and whether the person may
    /// decide it, with the code a decision would be refused with. It makes the checks of
    /// <see cref="DecideAsync(Guid, string, ClaimsPrincipal, bool, string?, string?, NhAssistantDecisionOrigin, CancellationToken, string?)"/>
    /// in the same order without their effects: an expired proposal is not closed and the proposal
    /// guards do not run, so a decision may still be refused as stale. A proposal the person may not
    /// see is unknown, as for a decision, also one of the whole application for a person bound to a
    /// tenant that the single-tenant mode does not name.
    /// </summary>
    public async Task<TaskResult<NhAssistantProposalDecisionFacts>> FactsAsync(
        Guid approvalId,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken,
        string? tenantId = null)
    {
        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        var thread = approval is null ? null : await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        if (approval is null
            || thread is null
            || NhAssistantWholeApplicationTenancy.KeepsOut(registration, thread, tenantId)
            || !await approvers.MaySeeAsync(thread, actorId, cancellationToken))
        {
            return TaskResult<NhAssistantProposalDecisionFacts>.Failed(NhAssistantErrorCodes.ApprovalNotFound, "The proposal was not found.");
        }

        var now = _applicationClock.GetUtcNow();
        var policy = approval.PolicyJson is null
            ? NhAiApprovalPolicy.TriggerDefault
            : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
        var waiting = approval.Status == NhAssistantApprovalStatuses.Pending && approval.ExpiresAt > now;
        var refusal = await RefusalOfAsync(approval, thread, actorId, user, now, cancellationToken);
        // Whether the object changed, by the execution's precondition; only a pending proposal is asked.
        var subjectChanged = approval.Status == NhAssistantApprovalStatuses.Pending
            ? (await CheckSubjectAsync(approval, thread, cancellationToken)).Changed
            : null;
        return TaskResult<NhAssistantProposalDecisionFacts>.Succeeded(new NhAssistantProposalDecisionFacts(
            approval.Id,
            approval.Status,
            approval.ProposalHash,
            approval.ExpiresAt,
            policy.FourEyes,
            waiting ? 1 : 0,
            refusal is null,
            refusal)
        {
            SubjectChanged = subjectChanged
        });
    }

    /// <summary>
    /// The code a decision of the person would be refused with now, in the order of the decision's
    /// checks after the proposal hash; <see langword="null"/> when the person may decide it.
    /// </summary>
    private async Task<string?> RefusalOfAsync(
        AssistantApproval approval,
        AssistantThread thread,
        string actorId,
        ClaimsPrincipal user,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (approval.Status != NhAssistantApprovalStatuses.Pending)
        {
            return NhAssistantErrorCodes.ApprovalNotPending;
        }
        if (approval.Mode == NhAssistantApprovalModes.Inline && approval.RunId is null)
        {
            return approval.ConsultationId is null
                ? NhAssistantErrorCodes.ApprovalDecisionInvalid
                : NhAssistantErrorCodes.ConsultAskerDecides;
        }
        if (approval.ExpiresAt <= now)
        {
            return NhAssistantErrorCodes.ApprovalExpired;
        }

        var check = await approvers.CheckAsync(approval, thread, actorId, user, cancellationToken);
        return check.Success ? null : NhAssistantResults.CodeOf(check, NhAssistantErrorCodes.ApprovalPolicyDenied);
    }

    /// <summary>
    /// The person's earlier decision of the proposal when a decision with <paramref name="approve"/>
    /// and <paramref name="expectedProposalHash"/> repeats it: the proposal is recorded as decided by
    /// <paramref name="actorId"/>, with the same hash, and its status still says that decision
    /// (approved, executing, awaiting the application, executed, failed or uncertain for an approval;
    /// rejected for a rejection).
    /// Otherwise <see langword="null"/>, and the repeat gets the ordinary refusal.
    /// </summary>
    public async Task<AssistantApproval?> FindRepeatAsync(
        Guid approvalId,
        string actorId,
        bool approve,
        string expectedProposalHash,
        CancellationToken cancellationToken)
    {
        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        if (approval?.DecidedAt is null
            || !string.Equals(approval.DecidedByActorId, actorId, StringComparison.Ordinal)
            || !string.Equals(approval.ProposalHash, expectedProposalHash, StringComparison.Ordinal))
        {
            return null;
        }

        var approved = approval.Status is NhAssistantApprovalStatuses.Approved
            or NhAssistantApprovalStatuses.Executing
            or NhAssistantApprovalStatuses.AwaitingApplication
            or NhAssistantApprovalStatuses.Executed
            or NhAssistantApprovalStatuses.ExecutionFailed
            or NhAssistantApprovalStatuses.ExecutionUnknown;
        var rejected = approval.Status == NhAssistantApprovalStatuses.Rejected;
        return (approve && approved) || (!approve && rejected) ? approval : null;
    }

    /// <summary>
    /// Resolves an execution whose outcome is unknown: run it again with a new idempotency key, hand a
    /// proposal the application executes to the application again, or confirm the outcome without
    /// running it. A proposal that was handed to the application is handed to it again, never run in the
    /// tool's function, and only such a proposal is handed to the application.
    /// </summary>
    /// <param name="tenantId">The caller's tenant, as for a decision.</param>
    public async Task<TaskResult<AssistantApproval>> ResolveExecutionAsync(
        Guid approvalId,
        string actorId,
        ClaimsPrincipal user,
        NhAssistantExecutionResolution resolution,
        string? reason,
        CancellationToken cancellationToken,
        string? tenantId = null)
    {
        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        var thread = approval is null ? null : await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        if (approval is null
            || thread is null
            || NhAssistantWholeApplicationTenancy.KeepsOut(registration, thread, tenantId)
            || !await approvers.MaySeeAsync(thread, actorId, cancellationToken))
        {
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalNotFound, "The proposal was not found.");
        }
        if (approval.Status != NhAssistantApprovalStatuses.ExecutionUnknown)
        {
            return TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ExecutionNotUncertain,
                "Only an execution with an unknown outcome can be resolved.");
        }

        var check = await approvers.CheckAsync(approval, thread, actorId, user, cancellationToken);
        if (!check.Success)
        {
            return TaskResult<AssistantApproval>.Failed(check);
        }

        // The application executes a proposal it was handed: an approver hands it again, and never
        // runs it in the tool's function; any other proposal runs again in its function.
        var handedToApplication = approval.ExecutionReportDueAt is not null;
        if ((resolution == NhAssistantExecutionResolution.HandToApplication && !handedToApplication)
            || (resolution == NhAssistantExecutionResolution.ReExecute && handedToApplication))
        {
            return TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ApprovalDecisionInvalid,
                handedToApplication
                    ? "The application executes this proposal; hand it to the application again."
                    : "Only a proposal the application executes is handed to it again.");
        }

        var executeAgain = resolution != NhAssistantExecutionResolution.MarkResolved;
        var resolved = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.ExecutionUnknown],
            executeAgain ? NhAssistantApprovalStatuses.Approved : NhAssistantApprovalStatuses.Resolved,
            new NhAssistantApprovalTransition
            {
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                ExecutionResultCode = resolution switch
                {
                    NhAssistantExecutionResolution.ReExecute => "re-execution-acknowledged",
                    NhAssistantExecutionResolution.HandToApplication => "hand-off-acknowledged",
                    _ => "resolved-by-approver"
                },
                DecidedByActorId = actorId,
                ClearExecutionOperation = executeAgain
            },
            cancellationToken);
        if (!resolved)
        {
            return TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ExecutionNotUncertain,
                "The execution was resolved by someone else.");
        }

        approval = (await store.FindApprovalAsync(approval.Id, cancellationToken))!;
        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalResolved,
            thread,
            approval,
            actorId,
            NhAssistantExecutionResolutions.Of(resolution),
            cancellationToken);
        if (executeAgain)
        {
            await EnqueueExecutionAsync(approval, thread, cancellationToken);
        }

        await workUpdates.PublishAsync(
            approval.ConversationId,
            NhAssistantAgentWorkReasons.Proposal,
            approval.Id,
            approval.RunId,
            cancellationToken);
        return TaskResult<AssistantApproval>.Succeeded(approval);
    }

    /// <summary>
    /// Queues the execution of an approved deferred proposal. Read and idempotent tools use the
    /// retried operation type; every other effect runs at most once per acknowledged attempt.
    /// </summary>
    internal async Task EnqueueExecutionAsync(
        AssistantApproval approval,
        AssistantThread thread,
        CancellationToken cancellationToken)
    {
        if (await agents.FindAsync(thread.AgentId, includeDisabled: false, cancellationToken) is null
            || (registration.AgentRuns.TryGetValue(thread.AgentId, out var runOptions) && !runOptions.Enabled))
        {
            // A stopped or code-disabled agent holds approved proposals; the maintenance job
            // queues them once the agent works again. A paused agent's proposals execute.
            return;
        }

        var descriptor = approvers.FindDescriptor(approval.ToolId);
        var idempotent = descriptor?.Effect is NhAiToolEffect.ReadOnly or NhAiToolEffect.IdempotentMutation;
        var proposal = Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
        var owner = NhAssistantAgentTriggers.OwnerActor(thread, proposal.AccountableOwnerId);
        var options = new NhBackgroundOperationEnqueueOptions
        {
            OwnerUserId = owner.UserId,
            OwnerAgentKey = owner.Key,
            DivisionId = thread.DivisionId,
            IdempotencyKey = $"execute-{approval.Id:N}-{approval.ExecutionAttempts}",
            DomainObjectType = thread.SubjectType,
            DomainObjectId = thread.SubjectId,
            CorrelationId = approval.RunId?.ToString("N")
        };
        var enqueued = idempotent
            ? await Operations.EnqueueAsync(new NhAssistantExecuteProposalRequest(approval.Id), options, cancellationToken)
            : await Operations.EnqueueAsync(new NhAssistantExecuteProposalOnceRequest(approval.Id), options, cancellationToken);
        if (!enqueued.Success)
        {
            // The maintenance job queues approved proposals that have no execution yet.
            logger.LogWarning("The execution of proposal {ApprovalId} could not be queued yet.", approval.Id);
            return;
        }

        await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Approved],
            NhAssistantApprovalStatuses.Approved,
            new NhAssistantApprovalTransition { ExecutionOperationId = enqueued.Data!.Id },
            cancellationToken);
    }

    private async Task SignalRunAsync(
        AssistantApproval approval,
        string actorId,
        bool approve,
        CancellationToken cancellationToken)
    {
        var run = approval.RunId is { } runId ? await runs.FindRunAsync(runId, cancellationToken) : null;
        if (run?.BackgroundOperationId is not { } operationId || !Guid.TryParse(actorId, out var deciderUserId))
        {
            logger.LogWarning("The run of inline proposal {ApprovalId} cannot be signaled.", approval.Id);
            return;
        }

        var signaled = await Signals.SignalAsync(
            operationId,
            deciderUserId,
            NhAssistantAgentRunOperation.WaitKey(approval.Id),
            new NhAiBackgroundApprovalSignal(
                approval.Id,
                approval.ProposalId,
                approval.ProposalHash,
                approve,
                approve ? NhAssistantApprovalStatuses.Approved : NhAssistantApprovalStatuses.Rejected),
            cancellationToken: cancellationToken);
        if (!signaled.Success)
        {
            logger.LogWarning("The run waiting for proposal {ApprovalId} could not be woken.", approval.Id);
        }
    }

    private async Task RecordAsync(
        AssistantApproval approval,
        AssistantThread thread,
        string actorId,
        bool approve,
        NhAssistantDecisionOrigin origin,
        CancellationToken cancellationToken)
    {
        var evt = new NhAssistantAuditEvent(
            approve ? NhAssistantAuditEventKind.ApprovalApproved : NhAssistantAuditEventKind.ApprovalRejected,
            approval.ConversationId,
            approval.TurnId,
            actorId,
            thread.AgentId,
            0,
            DateTimeOffset.UtcNow)
        {
            ToolId = approval.ToolId,
            InvocationId = approval.ToolInvocationId,
            ResultCode = approval.Status,
            ApprovalId = approval.Id,
            ProposalId = approval.ProposalId,
            RunId = approval.RunId,
            GrantSource = approval.GrantSource,
            ApprovalPolicyId = approval.PolicyId,
            SubjectType = thread.SubjectType,
            SubjectId = thread.SubjectId,
            InitiatorActorId = approval.InitiatorActorId,
            ConsultationId = approval.ConsultationId,
            ConsultationRoute = NhAssistantConsultationRoutes.Of(approval.ConsultationId, approval.Mode, approval.GrantSource),
            Channel = origin.Channel,
            IdempotencyKey = origin.IdempotencyKey
        };
        foreach (var sink in _businessSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }
}

/// <summary>
/// How an approver resolves an execution whose outcome is unknown.
/// </summary>
internal enum NhAssistantExecutionResolution
{
    /// <summary>Run the proposal again in the tool's function.</summary>
    ReExecute = 0,

    /// <summary>Confirm the outcome without running it.</summary>
    MarkResolved = 1,

    /// <summary>Hand a proposal the application executes to the application again.</summary>
    HandToApplication = 2
}

/// <summary>
/// The actions of <c>POST approvals/{approvalId}/resolve</c>, as the request and the audit name them.
/// </summary>
internal static class NhAssistantExecutionResolutions
{
    public const string ReExecute = "re-execute";
    public const string MarkResolved = "mark-resolved";
    public const string HandToApplication = "hand-to-application";

    public static bool TryParse(string? action, out NhAssistantExecutionResolution resolution)
    {
        resolution = action switch
        {
            ReExecute => NhAssistantExecutionResolution.ReExecute,
            MarkResolved => NhAssistantExecutionResolution.MarkResolved,
            HandToApplication => NhAssistantExecutionResolution.HandToApplication,
            _ => (NhAssistantExecutionResolution)(-1)
        };
        return Enum.IsDefined(resolution);
    }

    public static string Of(NhAssistantExecutionResolution resolution)
    {
        return resolution switch
        {
            NhAssistantExecutionResolution.ReExecute => ReExecute,
            NhAssistantExecutionResolution.HandToApplication => HandToApplication,
            _ => MarkResolved
        };
    }
}

/// <summary>
/// Where a decision of a proposal comes from: the channel the content-free audit records and the
/// application's idempotency key of the delivery that decided it, which only the audit records.
/// </summary>
internal sealed record NhAssistantDecisionOrigin(string Channel, string? IdempotencyKey)
{
    /// <summary>
    /// The decision endpoints of the assistant.
    /// </summary>
    public static NhAssistantDecisionOrigin Web { get; } = new(NhAssistantDecisionChannels.Web, null);
}
