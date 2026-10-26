using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// What a chat waits for and that passed its lifetime (CS9): a question nobody answered, an approval
/// nobody decided, or a consultation's proposal its asker did not decide. Nothing closes them on time;
/// the next answer or decision finds them expired, and so does the next message, which then starts its
/// own turn instead of finding the chat busy. A read changes nothing.
/// </summary>
internal sealed partial class NhAssistantTurnRunner
{
    /// <summary>
    /// Closes what the chat's waiting turn waits for when it passed its lifetime on the application's
    /// clock, exactly as a late answer or decision closes it: the same codes and audit, and the waiting
    /// turn released, so the message that found it can claim the chat. Something that is still valid
    /// stays open, and the message is then refused as busy. Agent threads keep their own rules: their
    /// waiting runs and approvals follow the run maintenance.
    /// </summary>
    /// <param name="actorId">The person whose message found the wait expired, as a late answer or
    /// decision names its person.</param>
    /// <param name="channel">The application's channel of the message, which the audit records.</param>
    private async Task CloseExpiredWaitAsync(
        AssistantConversation conversation,
        string actorId,
        string? channel,
        CancellationToken cancellationToken)
    {
        if (conversation.Status != NhAssistantConversationStatuses.WaitingForApproval
            || conversation.ActiveTurnId is not { } turnId)
        {
            return;
        }

        var now = _applicationClock.GetUtcNow();
        var approval = await store.FindPendingApprovalAsync(conversation.Id, cancellationToken);
        if (approval is not null && approval.TurnId == turnId)
        {
            if (now >= approval.ExpiresAt)
            {
                await ExpireWaitingApprovalAsync(conversation, approval, actorId, channel);
            }

            return;
        }

        var waiting = await store.FindAwaitingQuestionAsync(conversation.Id, turnId, cancellationToken);
        if (waiting is not null)
        {
            if (await FindQuestionAsync(conversation, waiting.Id, cancellationToken) is { } question
                && now >= question.Part.ExpiresAt)
            {
                await ExpireQuestionAsync(conversation, question.Invocation, question.Part, actorId, channel);
            }

            return;
        }

        var asked = await runs.FindPendingAskerDecisionAsync(conversation.Id, turnId, cancellationToken);
        if (asked is not null && now >= asked.ExpiresAt)
        {
            await ExpireAskerDecisionAsync(conversation, asked, turnId, actorId, channel);
        }
    }

    /// <summary>
    /// Closes the chat's approval as a late decision does: the approval expires on the application's
    /// clock, its call fails with <c>assistant-approval-expired</c>, <c>ApprovalExpired</c> is audited and
    /// the waiting turn is released.
    /// </summary>
    private async Task ExpireWaitingApprovalAsync(
        AssistantConversation conversation,
        AssistantApproval approval,
        string actorId,
        string? channel)
    {
        var expired = await store.TryDecideApprovalAsync(
            approval.Id,
            NhAssistantApprovalStatuses.Expired,
            null,
            _applicationClock.GetUtcNow(),
            null,
            null,
            CancellationToken.None);
        if (expired)
        {
            var now = DateTimeOffset.UtcNow;
            var invocation = await store.FindToolInvocationAsync(approval.ToolInvocationId, CancellationToken.None);
            if (invocation is not null)
            {
                invocation.Status = NhAssistantToolCallStatuses.Failed;
                invocation.ResultCode = NhAssistantErrorCodes.ApprovalExpired;
                invocation.CompletedAt = now;
                await store.UpdateToolInvocationAsync(invocation, CancellationToken.None);
            }

            await RecordExpiryAsync(ExpiryEvent(conversation, approval.TurnId, approval, invocation, actorId, channel, now));
        }

        await ReleaseWaitingTurnAsync(conversation, approval.TurnId);
    }

    /// <summary>
    /// Closes the proposal of a consultation that the chat's person did not decide in time, as their late
    /// decision does: the proposal expires, the consult call that waited for it fails with
    /// <c>assistant-approval-expired</c>, <c>ApprovalExpired</c> is audited and the waiting turn is released.
    /// </summary>
    private async Task ExpireAskerDecisionAsync(
        AssistantConversation conversation,
        AssistantApproval approval,
        Guid turnId,
        string actorId,
        string? channel)
    {
        var expired = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Expired,
            NhAssistantApprovalTransition.None,
            CancellationToken.None);
        if (expired)
        {
            var askingCall = await runs.FindAskingCallAsync(conversation.Id, approval.Id, CancellationToken.None);
            await runs.EndAskingCallAsync(
                conversation.Id,
                approval.Id,
                NhAssistantToolCallStatuses.Failed,
                NhAssistantErrorCodes.ApprovalExpired,
                CancellationToken.None);
            await RecordExpiryAsync(ExpiryEvent(conversation, turnId, approval, askingCall, actorId, channel, DateTimeOffset.UtcNow) with
            {
                ToolId = approval.ToolId,
                InvocationId = approval.ToolInvocationId,
                ConsultationId = approval.ConsultationId,
                ConsultationRoute = NhAssistantConsultationRoutes.AskerDecides,
                GrantSource = approval.GrantSource,
                ApprovalPolicyId = approval.PolicyId
            });
        }

        await ReleaseWaitingTurnAsync(conversation, turnId);
    }

    private async Task ReleaseWaitingTurnAsync(AssistantConversation conversation, Guid turnId)
    {
        if (await store.TryReleaseWaitingTurnAsync(conversation.Id, turnId, CancellationToken.None))
        {
            await live.PublishChangedAsync(conversation.Id, CancellationToken.None);
        }
    }

    private async Task RecordExpiryAsync(NhAssistantAuditEvent evt)
    {
        foreach (var sink in _businessSinks)
        {
            await sink.RecordAsync(evt, CancellationToken.None);
        }
    }

    /// <summary>
    /// The content-free <c>ApprovalExpired</c> event of an approval a message found expired, as a late
    /// decision records it, about the chat's waiting turn <paramref name="turnId"/>.
    /// </summary>
    private static NhAssistantAuditEvent ExpiryEvent(
        AssistantConversation conversation,
        Guid turnId,
        AssistantApproval approval,
        AssistantToolInvocation? invocation,
        string actorId,
        string? channel,
        DateTimeOffset occurredAt)
    {
        return new NhAssistantAuditEvent(
            NhAssistantAuditEventKind.ApprovalExpired,
            conversation.Id,
            turnId,
            actorId,
            conversation.AgentId,
            conversation.AgentVersion,
            occurredAt)
        {
            ToolId = invocation?.ToolId ?? approval.ToolId,
            ToolVersion = invocation?.ToolVersion,
            InvocationId = invocation?.Id ?? approval.ToolInvocationId,
            ResultCode = NhAssistantErrorCodes.ApprovalExpired,
            ApprovalId = approval.Id,
            ProposalId = approval.ProposalId,
            StartedAt = invocation?.StartedAt,
            Channel = channel
        };
    }
}
