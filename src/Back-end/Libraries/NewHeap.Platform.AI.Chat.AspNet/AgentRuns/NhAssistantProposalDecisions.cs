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
/// Decides whether a person may decide a proposal of an agent thread: the approval policy's
/// approvers, four-eyes, the tool's approve policy, and never the agent itself. Every check is live.
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
    /// Background operations are needed only once an agent runs; hosts without agents do not
    /// register them.
    /// </summary>
    private INhBackgroundOperationService Operations => services.GetRequiredService<INhBackgroundOperationService>();

    private INhBackgroundOperationSignalService Signals => services.GetRequiredService<INhBackgroundOperationSignalService>();

    public async Task<TaskResult<AssistantApproval>> DecideAsync(
        Guid approvalId,
        string actorId,
        ClaimsPrincipal user,
        bool approve,
        string? expectedProposalHash,
        string? reason,
        CancellationToken cancellationToken)
    {
        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        var thread = approval is null ? null : await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        if (approval is null || thread is null)
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
            // A person's own turn waits for this decision in the conversation stream.
            return TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ApprovalDecisionInvalid,
                "Decide this proposal in the conversation.");
        }

        var now = DateTimeOffset.UtcNow;
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
        await RecordAsync(approval, thread, actorId, approve, cancellationToken);
        if (approval.Mode == NhAssistantApprovalModes.Deferred)
        {
            if (approve)
            {
                await EnqueueExecutionAsync(approval, thread, cancellationToken);
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
    /// Resolves an execution whose outcome is unknown: run it again with a new idempotency key, or
    /// confirm the outcome without running it.
    /// </summary>
    public async Task<TaskResult<AssistantApproval>> ResolveExecutionAsync(
        Guid approvalId,
        string actorId,
        ClaimsPrincipal user,
        bool reExecute,
        string? reason,
        CancellationToken cancellationToken)
    {
        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        var thread = approval is null ? null : await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        if (approval is null || thread is null)
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

        var resolved = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.ExecutionUnknown],
            reExecute ? NhAssistantApprovalStatuses.Approved : NhAssistantApprovalStatuses.Resolved,
            new NhAssistantApprovalTransition
            {
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                ExecutionResultCode = reExecute ? "re-execution-acknowledged" : "resolved-by-approver",
                DecidedByActorId = actorId,
                ClearExecutionOperation = reExecute
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
            reExecute ? "re-execute" : "mark-resolved",
            cancellationToken);
        if (reExecute)
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
            ApprovalPolicyId = approval.PolicyId
        };
        foreach (var sink in _businessSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }
}
