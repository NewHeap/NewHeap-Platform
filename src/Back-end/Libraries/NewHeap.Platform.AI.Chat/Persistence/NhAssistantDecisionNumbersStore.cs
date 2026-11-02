using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The numbers of how people decide agent proposals: how long a person took from a proposal to their
/// decision, and per agent and kind of action (tool) the counts, the last rejection and the name the
/// proposal cards show, for the administrators' feedback summary and the view per kind of action.
/// Every query filters and groups on the server with provider-neutral LINQ, the same on SQL Server and
/// PostgreSQL. The decision times are a bounded load: at most a sample of the newest decisions per
/// group, two timestamps each, from which the caller computes the median and the 90th percentile.
/// </summary>
internal sealed class NhAssistantDecisionNumbersStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// The statuses in which a person approved a proposal, before or after it executed; a stale
    /// proposal closed at execution (<see cref="AssistantApproval.ExecutedAt"/> set) was approved too.
    /// </summary>
    private static readonly string[] ApprovedStatuses =
    [
        NhAssistantApprovalStatuses.Approved,
        NhAssistantApprovalStatuses.Executing,
        NhAssistantApprovalStatuses.AwaitingApplication,
        NhAssistantApprovalStatuses.Executed,
        NhAssistantApprovalStatuses.ExecutionFailed,
        NhAssistantApprovalStatuses.ExecutionUnknown,
        NhAssistantApprovalStatuses.Resolved
    ];

    /// <summary>
    /// The decision times of the agent's proposals made within the filter's period, per tool and
    /// agent version: at most <paramref name="samplePerGroup"/> plus one of the newest decisions per
    /// group, so the caller sees whether the group had more.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantDecisionTimeRow>> GetDecisionTimesAsync(
        string agentId,
        NhAssistantFeedbackFilter filter,
        int samplePerGroup,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var decided =
            from approval in DecidedByPeople(context.Approvals.AsNoTracking())
            join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
            join run in context.Runs.AsNoTracking() on approval.RunId equals (Guid?)run.Id into runs
            from run in runs.DefaultIfEmpty()
            where thread.AgentId == agentId
                && approval.CreatedAt >= filter.From
                && approval.CreatedAt < filter.To
            select new DecisionJoin
            {
                AgentId = thread.AgentId,
                ToolId = approval.ToolId,
                AgentVersion = run == null ? (int?)null : run.AgentVersion,
                ScopeKey = thread.ScopeKey,
                // Both on the application's clock; a proposal made before 30.1 has its recorded time only.
                ProposedAt = approval.ProposedAt ?? approval.CreatedAt,
                DecidedAt = approval.DecidedAt!.Value
            };
        if (filter.ScopeKey is { } scopeKey)
        {
            decided = decided.Where(row => row.ScopeKey == scopeKey);
        }
        if (filter.ToolId is { } toolId)
        {
            decided = decided.Where(row => row.ToolId == toolId);
        }
        if (filter.AgentVersion is { } agentVersion)
        {
            decided = decided.Where(row => row.AgentVersion == agentVersion);
        }

        var groups = decided.Select(row => new { row.ToolId, row.AgentVersion }).Distinct();
        var sample =
            from key in groups
            from row in decided
                .Where(row => row.ToolId == key.ToolId && row.AgentVersion == key.AgentVersion)
                .OrderByDescending(row => row.DecidedAt)
                .Take(samplePerGroup + 1)
            select new NhAssistantDecisionTimeRow(row.AgentId, row.ToolId, row.AgentVersion, row.ProposedAt, row.DecidedAt);
        return await sample.ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The numbers of every agent's proposals made within the period, per agent and tool: the counts
    /// per status, the newest rejection, a sample of the newest decision times and the stored
    /// presentation of each tool's newest proposal, for the view per kind of action. Shadow proposals
    /// are left out, as nobody decides them.
    /// </summary>
    public async Task<NhAssistantProposalKindAggregates> GetKindAggregatesAsync(
        NhAssistantProposalKindFilter filter,
        int samplePerGroup,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var proposals =
            from approval in context.Approvals.AsNoTracking()
            join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
            where approval.CreatedAt >= filter.From
                && approval.CreatedAt < filter.To
                && approval.Status != NhAssistantApprovalStatuses.Shadow
                && approval.Status != NhAssistantApprovalStatuses.ShadowSuperseded
            select new { thread.AgentId, Approval = approval };
        if (filter.AgentId is { } agentId)
        {
            proposals = proposals.Where(row => row.AgentId == agentId);
        }
        if (filter.ToolId is { } toolId)
        {
            proposals = proposals.Where(row => row.Approval.ToolId == toolId);
        }

        var statuses = await proposals
            .GroupBy(row => new
            {
                row.AgentId,
                row.Approval.ToolId,
                row.Approval.Status,
                AfterApproval = row.Approval.ExecutedAt != null
            })
            .Select(group => new NhAssistantKindStatusCount(
                group.Key.AgentId,
                group.Key.ToolId,
                group.Key.Status,
                group.Key.AfterApproval,
                group.Count()))
            .ToListAsync(cancellationToken);

        // The newest rejection of the period's proposals that happened before the period's end.
        var lastRejections = await proposals
            .Where(row => row.Approval.Status == NhAssistantApprovalStatuses.Rejected
                && row.Approval.DecidedAt != null
                && row.Approval.DecidedAt < filter.To)
            .GroupBy(row => new { row.AgentId, row.Approval.ToolId })
            .Select(group => new NhAssistantKindLastRejection(
                group.Key.AgentId,
                group.Key.ToolId,
                group.Max(row => row.Approval.DecidedAt)!.Value))
            .ToListAsync(cancellationToken);

        var decided =
            from approval in DecidedByPeople(context.Approvals.AsNoTracking())
            join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
            where approval.CreatedAt >= filter.From
                && approval.CreatedAt < filter.To
            select new DecisionJoin
            {
                AgentId = thread.AgentId,
                ToolId = approval.ToolId,
                AgentVersion = null,
                ScopeKey = thread.ScopeKey,
                // Both on the application's clock; a proposal made before 30.1 has its recorded time only.
                ProposedAt = approval.ProposedAt ?? approval.CreatedAt,
                DecidedAt = approval.DecidedAt!.Value
            };
        if (filter.AgentId is { } decidedAgentId)
        {
            decided = decided.Where(row => row.AgentId == decidedAgentId);
        }
        if (filter.ToolId is { } decidedToolId)
        {
            decided = decided.Where(row => row.ToolId == decidedToolId);
        }

        var groups = decided.Select(row => new { row.AgentId, row.ToolId }).Distinct();
        var times = await (
                from key in groups
                from row in decided
                    .Where(row => row.AgentId == key.AgentId && row.ToolId == key.ToolId)
                    .OrderByDescending(row => row.DecidedAt)
                    .Take(samplePerGroup + 1)
                select new NhAssistantDecisionTimeRow(row.AgentId, row.ToolId, null, row.ProposedAt, row.DecidedAt))
            .ToListAsync(cancellationToken);

        var toolIds = statuses.Select(row => row.ToolId).Distinct(StringComparer.Ordinal).ToList();
        var presentations = toolIds.Count == 0
            ? []
            : await context.Approvals
                .AsNoTracking()
                .Where(approval => toolIds.Contains(approval.ToolId)
                    && approval.PresentationJson != null
                    && approval.CreatedAt >= filter.From
                    && approval.CreatedAt < filter.To)
                .GroupBy(approval => approval.ToolId)
                .Select(group => group
                    .OrderByDescending(approval => approval.CreatedAt)
                    .ThenBy(approval => approval.Id)
                    .Select(approval => new NhAssistantKindPresentation(
                        approval.ToolId,
                        approval.PresentationJson,
                        approval.PresentationsJson))
                    .First())
                .ToListAsync(cancellationToken);

        return new NhAssistantProposalKindAggregates(statuses, lastRejections, times, presentations);
    }

    /// <summary>
    /// The proposals a person decided: approved or rejected with the time and the person of the
    /// decision. Decisions of agents and of the platform itself do not count.
    /// </summary>
    private static IQueryable<AssistantApproval> DecidedByPeople(IQueryable<AssistantApproval> approvals)
    {
        return approvals.Where(approval => approval.DecidedAt != null
            && approval.DecidedByActorId != null
            && !approval.DecidedByActorId.StartsWith(NhAssistantAgentEvents.AgentActorPrefix)
            && approval.DecidedByActorId != NhAssistantProposalInvalidation.SystemActorId
            && (ApprovedStatuses.Contains(approval.Status)
                || approval.Status == NhAssistantApprovalStatuses.Rejected
                || (approval.Status == NhAssistantApprovalStatuses.Stale && approval.ExecutedAt != null)));
    }

    private sealed class DecisionJoin
    {
        public required string AgentId { get; init; }

        public required string ToolId { get; init; }

        public required int? AgentVersion { get; init; }

        public required string ScopeKey { get; init; }

        public required DateTimeOffset ProposedAt { get; init; }

        public required DateTimeOffset DecidedAt { get; init; }
    }
}

/// <summary>
/// The period and filters of the view per kind of action; the period selects the proposals by when
/// they were made.
/// </summary>
internal sealed record NhAssistantProposalKindFilter(
    DateTimeOffset From,
    DateTimeOffset To,
    string? AgentId,
    string? ToolId);

/// <summary>When one proposal was made and when a person decided it, both on the application's clock.</summary>
internal sealed record NhAssistantDecisionTimeRow(
    string AgentId,
    string ToolId,
    int? AgentVersion,
    DateTimeOffset ProposedAt,
    DateTimeOffset DecidedAt);

/// <param name="AfterApproval">The proposal reached its execution, so a stale one was approved first.</param>
internal sealed record NhAssistantKindStatusCount(
    string AgentId,
    string ToolId,
    string Status,
    bool AfterApproval,
    int Count);

internal sealed record NhAssistantKindLastRejection(string AgentId, string ToolId, DateTimeOffset DecidedAt);

/// <summary>The stored presentation of a tool's newest proposal, with the name the cards show.</summary>
internal sealed record NhAssistantKindPresentation(string ToolId, string? PresentationJson, string? PresentationsJson);

internal sealed record NhAssistantProposalKindAggregates(
    IReadOnlyList<NhAssistantKindStatusCount> Statuses,
    IReadOnlyList<NhAssistantKindLastRejection> LastRejections,
    IReadOnlyList<NhAssistantDecisionTimeRow> DecisionTimes,
    IReadOnlyList<NhAssistantKindPresentation> Presentations);
