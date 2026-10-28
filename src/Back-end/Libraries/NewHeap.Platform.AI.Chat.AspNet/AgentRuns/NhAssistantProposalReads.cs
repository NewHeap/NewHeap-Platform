using System.Security.Claims;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Reads proposals of agent threads by id as the caller may see them: the caller's tenant, the live
/// role on the thread's subject or an invitation to the thread, shadow agents for administrators
/// only, and the caller's live right to decide each pending proposal. Reading never joins a thread,
/// and every subject is resolved once per read. A proposal can no longer be decided once the
/// application's clock (a registered <see cref="TimeProvider"/>, otherwise the system clock) reaches
/// its expiry.
/// </summary>
internal sealed class NhAssistantProposalReads(
    NhAssistantRunStore runs,
    NhAssistantThreadService threads,
    NhAssistantSubjectAccessService subjects,
    NhAssistantApproverCheck approvers,
    NhAssistantProposalFeedbackStore feedback,
    NhAssistantTenancy tenancy,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The proposals among <paramref name="approvalIds"/> that the caller may see, in the order of
    /// the ids and each once; unknown and invisible ids are left out.
    /// </summary>
    /// <param name="isHiddenThread">Whether a thread is hidden from the caller, as shadow threads are from everyone but administrators.</param>
    public async Task<IReadOnlyList<NhAssistantSubjectProposalDto>> ReadAsync(
        IReadOnlyList<Guid> approvalIds,
        string actorId,
        string? tenantId,
        ClaimsPrincipal user,
        Func<AssistantThread, Task<bool>> isHiddenThread,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvalIds);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(isHiddenThread);
        var rows = await runs.GetThreadApprovalsByIdAsync(approvalIds, cancellationToken);
        var roles = new Dictionary<NhAiSubjectRef, NhAiSubjectAccess>();
        var visible = new List<(AssistantApproval Approval, NhAssistantThreadAccess Access)>();
        foreach (var group in rows.GroupBy(row => row.Thread.ConversationId))
        {
            var thread = group.First().Thread;
            // A thread of another tenant stays invisible, as its conversation does; the whole
            // application's threads are the single tenant's in the single-tenant mode.
            if (!tenancy.Includes(thread, tenantId))
            {
                continue;
            }
            if (await isHiddenThread(thread))
            {
                continue;
            }

            var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
            if (!roles.TryGetValue(subject, out var role))
            {
                role = await subjects.ResolveAsync(subject, actorId, cancellationToken);
                roles[subject] = role;
            }
            var access = await threads.ReadAccessAsync(thread, actorId, role, cancellationToken);
            if (access is not null && access.Role >= NhAiSubjectRole.Reader)
            {
                visible.AddRange(group.Select(row => (row.Approval, access)));
            }
        }
        if (visible.Count == 0)
        {
            return [];
        }

        // Each person sees their own feedback, as in the thread's list of proposals.
        var ownFeedback = await feedback.FindByAuthorAsync(
            visible.Select(item => item.Approval.Id).ToArray(),
            actorId,
            cancellationToken);
        var proposals = await NhAssistantAgentRunDtoMapper.CompleteAsync(
            visible
                .Select(item => NhAssistantAgentRunDtoMapper.ToDto(item.Approval, item.Access.Thread) with
                {
                    Feedback = ownFeedback.TryGetValue(item.Approval.Id, out var given)
                        ? NhAssistantAgentRunDtoMapper.ToDto(given)
                        : null,
                    CanGiveFeedback = NhAssistantProposalFeedbackService.MayGiveFeedback(item.Approval, item.Access.Role, actorId)
                })
                .ToArray(),
            runs,
            subjects,
            cancellationToken);

        var described = new Dictionary<NhAiSubjectRef, NhAssistantResolvedSubject>();
        var read = new Dictionary<Guid, NhAssistantSubjectProposalDto>();
        for (var index = 0; index < visible.Count; index++)
        {
            var (approval, access) = visible[index];
            var (canDecide, reasonCode) = await DecisionRightAsync(approval, access, actorId, user, described, cancellationToken);
            read[approval.Id] = new NhAssistantSubjectProposalDto(proposals[index], canDecide, reasonCode);
        }

        return approvalIds
            .Distinct()
            .Where(read.ContainsKey)
            .Select(id => read[id])
            .ToArray();
    }

    /// <summary>
    /// The caller's live right to decide a pending proposal, as the subject's work reports it. Other
    /// statuses have no decision right; their status says why.
    /// </summary>
    private async Task<(bool CanDecide, string? ReasonCode)> DecisionRightAsync(
        AssistantApproval approval,
        NhAssistantThreadAccess access,
        string actorId,
        ClaimsPrincipal user,
        Dictionary<NhAiSubjectRef, NhAssistantResolvedSubject> described,
        CancellationToken cancellationToken)
    {
        if (approval.Status != NhAssistantApprovalStatuses.Pending || approval.ExpiresAt <= _applicationClock.GetUtcNow())
        {
            return (false, null);
        }
        if (approval.Mode == NhAssistantApprovalModes.Inline && approval.RunId is null)
        {
            // A person's own turn in the thread decides its proposal in the conversation; a
            // consultation's, the person who asked in their own conversation.
            return (false, approval.ConsultationId is null
                ? NhAssistantErrorCodes.ApprovalDecisionInvalid
                : NhAssistantErrorCodes.ConsultAskerDecides);
        }

        if (!described.TryGetValue(access.Subject, out var resolved))
        {
            resolved = new NhAssistantResolvedSubject(
                await subjects.DescribeAsync(access.Subject, cancellationToken),
                access.Access);
            described[access.Subject] = resolved;
        }

        var check = await approvers.CheckAsync(approval, access.Thread, actorId, user, resolved, cancellationToken);
        return check.Success
            ? (true, null)
            : (false, check.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
    }
}
