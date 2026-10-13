using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Stores what people say was wrong with a closed agent proposal. The person who decided it and
/// every approver of the subject may give feedback, checked live against the subject access
/// resolver; each person keeps one record per proposal and may change it. The audit sink receives
/// the category only; feedback sinks receive the text as well.
/// </summary>
internal sealed class NhAssistantProposalFeedbackService(
    INhAssistantStore store,
    NhAssistantRunStore runs,
    NhAssistantProposalFeedbackStore feedbackStore,
    NhAssistantSubjectAccessService subjects,
    NhAssistantAgentEvents events,
    IEnumerable<INhAssistantProposalFeedbackSink> feedbackSinks,
    ILogger<NhAssistantProposalFeedbackService> logger)
{
    private readonly IReadOnlyList<INhAssistantProposalFeedbackSink> _feedbackSinks = feedbackSinks.ToArray();

    /// <summary>
    /// Whether <paramref name="actorId"/> may give feedback on the proposal: it is closed without
    /// being approved, and the person decided it or is an approver of the subject.
    /// </summary>
    public static bool MayGiveFeedback(AssistantApproval approval, NhAiSubjectRole role, string actorId)
    {
        if (!NhAssistantApprovalStatuses.AcceptsFeedback(approval.Status))
        {
            return false;
        }

        return role >= NhAiSubjectRole.Approver
            || (role >= NhAiSubjectRole.Reader
                && string.Equals(approval.DecidedByActorId, actorId, StringComparison.Ordinal));
    }

    public async Task<TaskResult<AssistantProposalFeedback>> GiveAsync(
        Guid approvalId,
        string actorId,
        string? category,
        string? text,
        bool bulk,
        CancellationToken cancellationToken)
    {
        var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (!NhAssistantProposalFeedbackCategories.IsKnown(category)
            || trimmed is { Length: > NhAssistantLimits.MaxFeedbackCharacters })
        {
            return TaskResult<AssistantProposalFeedback>.Failed(
                NhAssistantErrorCodes.FeedbackInvalid,
                "The feedback needs a known category and at most 1,000 characters of text.");
        }

        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        var thread = approval is null ? null : await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        if (approval is null
            || thread is null
            || actorId.StartsWith(NhAssistantAgentEvents.AgentActorPrefix, StringComparison.Ordinal))
        {
            return TaskResult<AssistantProposalFeedback>.Failed(NhAssistantErrorCodes.ApprovalNotFound, "The proposal was not found.");
        }

        var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
        var access = await subjects.ResolveAsync(subject, actorId, cancellationToken);
        if (access.Role == NhAiSubjectRole.None)
        {
            // People without a role on the subject cannot tell whether the proposal exists.
            return TaskResult<AssistantProposalFeedback>.Failed(NhAssistantErrorCodes.ApprovalNotFound, "The proposal was not found.");
        }
        if (!NhAssistantApprovalStatuses.AcceptsFeedback(approval.Status))
        {
            return TaskResult<AssistantProposalFeedback>.Failed(
                NhAssistantErrorCodes.FeedbackNotAccepted,
                "Only a rejected, expired, withdrawn or stale proposal takes feedback.");
        }
        if (!MayGiveFeedback(approval, access.Role, actorId))
        {
            return TaskResult<AssistantProposalFeedback>.Failed(
                NhAssistantErrorCodes.SubjectAccessDenied,
                "Only the person who decided the proposal and the subject's approvers give feedback.");
        }

        var run = approval.RunId is { } runId ? await runs.FindRunAsync(runId, cancellationToken) : null;
        var proposal = Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
        var now = DateTimeOffset.UtcNow;
        var saved = await feedbackStore.SaveAsync(
            new AssistantProposalFeedback
            {
                Id = Guid.NewGuid(),
                ApprovalId = approval.Id,
                TargetKind = NhAssistantFeedbackTargetKinds.AgentProposal,
                ConversationId = approval.ConversationId,
                ScopeKey = thread.ScopeKey,
                AgentId = thread.AgentId,
                AgentVersion = run?.AgentVersion,
                ToolId = approval.ToolId,
                ToolContractHash = proposal.ToolContractHash,
                SubjectType = thread.SubjectType,
                SubjectId = thread.SubjectId,
                ApprovalStatus = approval.Status,
                Category = category!,
                Text = trimmed,
                AuthorActorId = actorId,
                Bulk = bulk,
                CreatedAt = now,
                UpdatedAt = now
            },
            cancellationToken);

        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalFeedbackGiven,
            thread,
            approval,
            actorId,
            saved.Category,
            cancellationToken);
        await PublishAsync(saved, approval, cancellationToken);
        return TaskResult<AssistantProposalFeedback>.Succeeded(saved);
    }

    private async Task PublishAsync(
        AssistantProposalFeedback feedback,
        AssistantApproval approval,
        CancellationToken cancellationToken)
    {
        if (_feedbackSinks.Count == 0)
        {
            return;
        }

        var record = new NhAssistantProposalFeedback(
            feedback.Id,
            feedback.ApprovalId,
            approval.ProposalId,
            approval.RunId,
            feedback.ScopeKey,
            feedback.AgentId,
            feedback.AgentVersion,
            feedback.ToolId,
            feedback.ToolContractHash,
            new NhAiSubjectRef(feedback.SubjectType, feedback.SubjectId),
            feedback.ApprovalStatus,
            feedback.Category,
            NhAssistantProposalFeedbackCategories.GroupOf(feedback.Category),
            feedback.Text,
            feedback.AuthorActorId,
            feedback.Bulk,
            feedback.CreatedAt,
            feedback.UpdatedAt);
        foreach (var sink in _feedbackSinks)
        {
            try
            {
                await sink.RecordAsync(record, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // The feedback is stored; a failing analytics sink must not turn that into an error.
                logger.LogWarning(
                    "Proposal feedback sink {Sink} failed for approval {ApprovalId} ({ExceptionType}).",
                    sink.GetType().Name,
                    feedback.ApprovalId,
                    exception.GetType().Name);
            }
        }
    }
}
