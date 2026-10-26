using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Persistence of the feedback people give on closed agent proposals: one record per person and
/// proposal, the rejections a new run of the thread starts with, and the aggregates and listings of
/// the administrators' report. Every report query filters, groups and pages on the server.
/// </summary>
internal sealed class NhAssistantProposalFeedbackStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// Stores the feedback of one person on one proposal, replacing what that person said before.
    /// The first record keeps its id and creation time.
    /// </summary>
    public async Task<AssistantProposalFeedback> SaveAsync(
        AssistantProposalFeedback feedback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        if (await TryReplaceAsync(feedback, cancellationToken) is { } replaced)
        {
            return replaced;
        }

        try
        {
            await using var context = contextFactory.CreateDbContext();
            context.ProposalFeedback.Add(feedback);
            await context.SaveChangesAsync(cancellationToken);
            return feedback;
        }
        catch (DbUpdateException)
        {
            // The same person saved feedback on the same proposal at the same moment; the unique
            // index kept one record, which this call now replaces.
            return await TryReplaceAsync(feedback, cancellationToken)
                ?? throw new InvalidOperationException("The proposal feedback could not be stored.");
        }
    }

    public async Task<IReadOnlyDictionary<Guid, AssistantProposalFeedback>> FindByAuthorAsync(
        IReadOnlyCollection<Guid> approvalIds,
        string authorActorId,
        CancellationToken cancellationToken)
    {
        if (approvalIds.Count == 0)
        {
            return new Dictionary<Guid, AssistantProposalFeedback>();
        }

        await using var context = contextFactory.CreateDbContext();
        return await context.ProposalFeedback
            .AsNoTracking()
            .Where(item => item.AuthorActorId == authorActorId && approvalIds.Contains(item.ApprovalId))
            .ToDictionaryAsync(item => item.ApprovalId, cancellationToken);
    }

    /// <summary>
    /// The proposals of a thread that people rejected since <paramref name="since"/>, newest first,
    /// with the feedback they gave.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantRejectedProposal>> GetRecentRejectionsAsync(
        Guid conversationId,
        DateTimeOffset since,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (maximum < 1)
        {
            return [];
        }

        await using var context = contextFactory.CreateDbContext();
        var approvals = await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.ConversationId == conversationId
                && approval.Status == NhAssistantApprovalStatuses.Rejected
                && approval.DecidedAt != null
                && approval.DecidedAt >= since
                // A proposal the asker declined in their own conversation is no rejection of the
                // agent's work by the subject's approvers.
                && !(approval.ConsultationId != null && approval.Mode == NhAssistantApprovalModes.Inline))
            .OrderByDescending(approval => approval.DecidedAt)
            .ThenBy(approval => approval.Id)
            .Take(maximum)
            .Select(approval => new { approval.Id, approval.ToolId, approval.Summary, approval.DecidedAt })
            .ToListAsync(cancellationToken);
        if (approvals.Count == 0)
        {
            return [];
        }

        var approvalIds = approvals.Select(approval => approval.Id).ToList();
        var feedback = await context.ProposalFeedback
            .AsNoTracking()
            .Where(item => approvalIds.Contains(item.ApprovalId))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => new { item.ApprovalId, item.Category, item.Text })
            .ToListAsync(cancellationToken);
        var feedbackByApproval = feedback.ToLookup(item => item.ApprovalId);

        return approvals
            .Select(approval => new NhAssistantRejectedProposal(
                approval.Id,
                approval.ToolId,
                approval.Summary,
                approval.DecidedAt!.Value,
                feedbackByApproval[approval.Id]
                    .Select(item => new NhAssistantRejectionNote(item.Category, item.Text))
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// The proposals of a thread that closed since <paramref name="since"/> because they were
    /// already done or no longer applied, newest first. Proposals closed because the subject changed
    /// are left out: the agent may propose them again for the changed subject.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantInvalidatedProposalNote>> GetRecentInvalidationsAsync(
        Guid conversationId,
        DateTimeOffset since,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (maximum < 1)
        {
            return [];
        }

        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.ConversationId == conversationId
                && approval.Status == NhAssistantApprovalStatuses.Stale
                && (approval.StaleKind == NhAssistantStaleKinds.AlreadyDone
                    || approval.StaleKind == NhAssistantStaleKinds.NotApplicable)
                && (approval.DecidedAt ?? approval.ExecutedAt) >= since)
            .OrderByDescending(approval => approval.DecidedAt ?? approval.ExecutedAt)
            .ThenBy(approval => approval.Id)
            .Take(maximum)
            .Select(approval => new NhAssistantInvalidatedProposalNote(
                approval.Id,
                approval.ToolId,
                approval.ArgumentsPreview,
                approval.PresentationJson,
                approval.PresentationsJson,
                (approval.DecidedAt ?? approval.ExecutedAt)!.Value,
                approval.StaleKind!,
                approval.StaleExplanationsJson))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Counts the agent's proposals per tool, agent version and status, and the feedback on them,
    /// for proposals made within the filter's period.
    /// </summary>
    public async Task<NhAssistantFeedbackAggregates> GetAggregatesAsync(
        string agentId,
        NhAssistantFeedbackFilter filter,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var proposals =
            from approval in context.Approvals.AsNoTracking()
            join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
            join run in context.Runs.AsNoTracking() on approval.RunId equals (Guid?)run.Id into runs
            from run in runs.DefaultIfEmpty()
            where thread.AgentId == agentId
                && approval.CreatedAt >= filter.From
                && approval.CreatedAt < filter.To
                && approval.Status != NhAssistantApprovalStatuses.Shadow
                && approval.Status != NhAssistantApprovalStatuses.ShadowSuperseded
            select new
            {
                approval.ToolId,
                AgentVersion = run == null ? (int?)null : run.AgentVersion,
                approval.Status,
                thread.ScopeKey,
                approval.StaleKind,
                approval.ExecutionResultCode,
                AfterApproval = approval.ExecutedAt != null
            };
        if (filter.ScopeKey is { } scopeKey)
        {
            proposals = proposals.Where(row => row.ScopeKey == scopeKey);
        }
        if (filter.ToolId is { } toolId)
        {
            proposals = proposals.Where(row => row.ToolId == toolId);
        }
        if (filter.AgentVersion is { } agentVersion)
        {
            proposals = proposals.Where(row => row.AgentVersion == agentVersion);
        }

        var statuses = await proposals
            .GroupBy(row => new { row.ToolId, row.AgentVersion, row.Status })
            .Select(group => new NhAssistantProposalStatusCount(
                group.Key.ToolId,
                group.Key.AgentVersion,
                group.Key.Status,
                group.Count()))
            .ToListAsync(cancellationToken);

        // A stale proposal closed at execution was approved first; one closed before a decision
        // was neither approved nor rejected.
        var invalidations = await proposals
            .Where(row => row.Status == NhAssistantApprovalStatuses.Stale)
            .GroupBy(row => new { row.ToolId, row.AgentVersion, row.StaleKind, row.ExecutionResultCode, row.AfterApproval })
            .Select(group => new NhAssistantInvalidationCount(
                group.Key.ToolId,
                group.Key.AgentVersion,
                group.Key.StaleKind ?? NhAssistantStaleKinds.Changed,
                group.Key.ExecutionResultCode ?? NhAssistantErrorCodes.ProposalStale,
                group.Key.AfterApproval,
                group.Count()))
            .ToListAsync(cancellationToken);

        var feedback =
            from item in context.ProposalFeedback.AsNoTracking()
            join approval in context.Approvals.AsNoTracking() on item.ApprovalId equals approval.Id
            where item.AgentId == agentId
                && approval.CreatedAt >= filter.From
                && approval.CreatedAt < filter.To
            select new
            {
                item.ToolId,
                item.AgentVersion,
                item.Category,
                item.AuthorActorId,
                item.ApprovalId,
                item.ScopeKey,
                approval.Status
            };
        if (filter.ScopeKey is { } feedbackScopeKey)
        {
            feedback = feedback.Where(row => row.ScopeKey == feedbackScopeKey);
        }
        if (filter.ToolId is { } feedbackToolId)
        {
            feedback = feedback.Where(row => row.ToolId == feedbackToolId);
        }
        if (filter.AgentVersion is { } feedbackAgentVersion)
        {
            feedback = feedback.Where(row => row.AgentVersion == feedbackAgentVersion);
        }

        var categories = await feedback
            .GroupBy(row => new { row.ToolId, row.AgentVersion, row.Category })
            .Select(group => new NhAssistantFeedbackCategoryCount(
                group.Key.ToolId,
                group.Key.AgentVersion,
                group.Key.Category,
                group.Count()))
            .ToListAsync(cancellationToken);
        var reach = await feedback
            .GroupBy(row => new { row.ToolId, row.AgentVersion })
            .Select(group => new NhAssistantFeedbackReach(
                group.Key.ToolId,
                group.Key.AgentVersion,
                group.Select(row => row.AuthorActorId).Distinct().Count(),
                group.Select(row => row.ApprovalId).Distinct().Count()))
            .ToListAsync(cancellationToken);
        var rejectedWithFeedback = await feedback
            .Where(row => row.Status == NhAssistantApprovalStatuses.Rejected)
            .GroupBy(row => new { row.ToolId, row.AgentVersion })
            .Select(group => new NhAssistantFeedbackRejectionReach(
                group.Key.ToolId,
                group.Key.AgentVersion,
                group.Select(row => row.ApprovalId).Distinct().Count()))
            .ToListAsync(cancellationToken);

        return new NhAssistantFeedbackAggregates(statuses, categories, reach, rejectedWithFeedback)
        {
            Invalidations = invalidations
        };
    }

    /// <summary>
    /// The feedback on the agent's proposals made within the filter's period, newest change first.
    /// </summary>
    public async Task<(IReadOnlyList<NhAssistantFeedbackRow> Items, int Total)> ListAsync(
        string agentId,
        NhAssistantFeedbackFilter filter,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var query = FilteredFeedback(context, agentId, filter);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(row => row.Feedback.UpdatedAt)
            .ThenBy(row => row.Feedback.Id)
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage)
            .Select(row => new NhAssistantFeedbackRow(
                row.Feedback,
                row.Approval.ProposalId,
                row.Approval.RunId,
                row.Approval.Summary,
                row.Approval.PresentationJson,
                row.Approval.PresentationsJson,
                row.Thread.SubjectDisplayName,
                null))
            .ToListAsync(cancellationToken);
        return (rows, total);
    }

    /// <summary>
    /// The feedback on the agent's proposals made within the filter's period, oldest first, with
    /// the stored proposal, in batches of <paramref name="batchSize"/> up to <paramref name="maximum"/> records.
    /// </summary>
    public async IAsyncEnumerable<NhAssistantFeedbackRow> ExportAsync(
        string agentId,
        NhAssistantFeedbackFilter filter,
        int maximum,
        int batchSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var exported = 0;
        while (exported < maximum)
        {
            await using var context = contextFactory.CreateDbContext();
            var batch = await FilteredFeedback(context, agentId, filter)
                .OrderBy(row => row.Feedback.CreatedAt)
                .ThenBy(row => row.Feedback.Id)
                .Skip(exported)
                .Take(Math.Min(batchSize, maximum - exported))
                .Select(row => new NhAssistantFeedbackRow(
                    row.Feedback,
                    row.Approval.ProposalId,
                    row.Approval.RunId,
                    row.Approval.Summary,
                    row.Approval.PresentationJson,
                    row.Approval.PresentationsJson,
                    row.Thread.SubjectDisplayName,
                    row.Approval.ProposalJson))
                .ToListAsync(cancellationToken);
            foreach (var row in batch)
            {
                yield return row;
            }

            exported += batch.Count;
            if (batch.Count < batchSize)
            {
                yield break;
            }
        }
    }

    private static IQueryable<FeedbackJoin> FilteredFeedback(
        NhAssistantDbContext context,
        string agentId,
        NhAssistantFeedbackFilter filter)
    {
        var query =
            from feedback in context.ProposalFeedback.AsNoTracking()
            join approval in context.Approvals.AsNoTracking() on feedback.ApprovalId equals approval.Id
            join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
            where feedback.AgentId == agentId
                && approval.CreatedAt >= filter.From
                && approval.CreatedAt < filter.To
            select new FeedbackJoin
            {
                Feedback = feedback,
                Approval = approval,
                Thread = thread
            };
        if (filter.ScopeKey is { } scopeKey)
        {
            query = query.Where(row => row.Feedback.ScopeKey == scopeKey);
        }
        if (filter.ToolId is { } toolId)
        {
            query = query.Where(row => row.Feedback.ToolId == toolId);
        }
        if (filter.Category is { } category)
        {
            query = query.Where(row => row.Feedback.Category == category);
        }
        if (filter.AgentVersion is { } agentVersion)
        {
            query = query.Where(row => row.Feedback.AgentVersion == agentVersion);
        }

        return query;
    }

    private async Task<AssistantProposalFeedback?> TryReplaceAsync(
        AssistantProposalFeedback feedback,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var updated = await context.ProposalFeedback
            .Where(item => item.ApprovalId == feedback.ApprovalId && item.AuthorActorId == feedback.AuthorActorId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.ApprovalStatus, feedback.ApprovalStatus)
                    .SetProperty(item => item.Category, feedback.Category)
                    .SetProperty(item => item.Text, feedback.Text)
                    .SetProperty(item => item.Bulk, feedback.Bulk)
                    .SetProperty(item => item.UpdatedAt, feedback.UpdatedAt),
                cancellationToken);
        if (updated == 0)
        {
            return null;
        }

        return await context.ProposalFeedback
            .AsNoTracking()
            .SingleAsync(
                item => item.ApprovalId == feedback.ApprovalId && item.AuthorActorId == feedback.AuthorActorId,
                cancellationToken);
    }

    private sealed class FeedbackJoin
    {
        public required AssistantProposalFeedback Feedback { get; init; }

        public required AssistantApproval Approval { get; init; }

        public required AssistantThread Thread { get; init; }
    }
}

/// <summary>
/// The period and filters of the administrators' feedback report. The period selects the
/// proposals by when they were made.
/// </summary>
internal sealed record NhAssistantFeedbackFilter(
    DateTimeOffset From,
    DateTimeOffset To,
    string? ScopeKey,
    string? ToolId,
    string? Category,
    int? AgentVersion);

internal sealed record NhAssistantRejectionNote(string Category, string? Text);

internal sealed record NhAssistantRejectedProposal(
    Guid ApprovalId,
    string ToolId,
    string Summary,
    DateTimeOffset DecidedAt,
    IReadOnlyList<NhAssistantRejectionNote> Notes);

internal sealed record NhAssistantProposalStatusCount(string ToolId, int? AgentVersion, string Status, int Count);

internal sealed record NhAssistantFeedbackCategoryCount(string ToolId, int? AgentVersion, string Category, int Count);

internal sealed record NhAssistantFeedbackReach(string ToolId, int? AgentVersion, int Authors, int Proposals);

internal sealed record NhAssistantFeedbackRejectionReach(string ToolId, int? AgentVersion, int Proposals);

/// <param name="AfterApproval">True when the proposal closed right before execution, after its approval.</param>
internal sealed record NhAssistantInvalidationCount(
    string ToolId,
    int? AgentVersion,
    string Kind,
    string ReasonCode,
    bool AfterApproval,
    int Count);

internal sealed record NhAssistantInvalidatedProposalNote(
    Guid ApprovalId,
    string ToolId,
    string ArgumentsPreview,
    string? PresentationJson,
    string? PresentationsJson,
    DateTimeOffset ClosedAt,
    string Kind,
    string? ExplanationsJson);

internal sealed record NhAssistantFeedbackAggregates(
    IReadOnlyList<NhAssistantProposalStatusCount> Statuses,
    IReadOnlyList<NhAssistantFeedbackCategoryCount> Categories,
    IReadOnlyList<NhAssistantFeedbackReach> Reach,
    IReadOnlyList<NhAssistantFeedbackRejectionReach> RejectedWithFeedback)
{
    public IReadOnlyList<NhAssistantInvalidationCount> Invalidations { get; init; } = [];
}

internal sealed record NhAssistantFeedbackRow(
    AssistantProposalFeedback Feedback,
    Guid ProposalId,
    Guid? RunId,
    string Summary,
    string? PresentationJson,
    string? PresentationsJson,
    string? SubjectDisplayName,
    string? ProposalJson);
