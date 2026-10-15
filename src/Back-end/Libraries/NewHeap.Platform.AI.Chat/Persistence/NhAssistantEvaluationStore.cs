using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// A run as the evaluation compares it: its subject, the event it handled, how it routed and what
/// it cost. No content.
/// </summary>
internal sealed record NhAssistantEvaluationRun(
    Guid Id,
    Guid ConversationId,
    string SubjectType,
    string SubjectId,
    string TriggerId,
    string? TriggerEventKey,
    Guid? ReplayOfRunId,
    string Status,
    string? SkillId,
    string? SkillChosenBy,
    int ModelCalls,
    long Tokens,
    int ToolCalls,
    DateTimeOffset CreatedAt);

/// <summary>
/// A proposal as the evaluation compares it: its identity and what people decided, no arguments.
/// </summary>
internal sealed record NhAssistantEvaluationProposal(
    Guid ApprovalId,
    Guid RunId,
    string ToolId,
    string? ArgumentsHash,
    string Status);

/// <summary>
/// Reads runs and proposals of an agent and its shadow variant for their comparison, and keeps the
/// replays of recorded runs into variants. Every call uses its own short-lived context.
/// </summary>
internal sealed class NhAssistantEvaluationStore(NhAssistantDbContextFactory contextFactory)
{
    private static readonly string[] ActiveRunStatuses =
    [
        NhAssistantRunStatuses.Queued,
        NhAssistantRunStatuses.Running,
        NhAssistantRunStatuses.WaitingForApproval
    ];

    /// <summary>
    /// The runs of <paramref name="agentId"/> created in the period, oldest first, at most
    /// <paramref name="max"/>.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantEvaluationRun>> GetRunsAsync(
        string agentId,
        DateTimeOffset from,
        DateTimeOffset to,
        int max,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await Project(RunsOf(context, agentId)
                .Where(row => row.Run.CreatedAt >= from && row.Run.CreatedAt < to)
                .OrderBy(row => row.Run.CreatedAt)
                .ThenBy(row => row.Run.Id)
                .Take(max))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The runs of <paramref name="agentId"/> that replay one of <paramref name="replayedRunIds"/>,
    /// whenever they ran.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantEvaluationRun>> GetReplaysOfAsync(
        string agentId,
        IReadOnlyCollection<Guid> replayedRunIds,
        CancellationToken cancellationToken)
    {
        if (replayedRunIds.Count == 0)
        {
            return [];
        }

        await using var context = contextFactory.CreateDbContext();
        var ids = replayedRunIds.ToArray();
        return await Project(RunsOf(context, agentId)
                .Where(row => row.Run.ReplayOfRunId.HasValue && ids.Contains(row.Run.ReplayOfRunId.Value)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NhAssistantEvaluationProposal>> GetProposalsAsync(
        IReadOnlyCollection<Guid> runIds,
        CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
        {
            return [];
        }

        await using var context = contextFactory.CreateDbContext();
        var ids = runIds.ToArray();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.RunId.HasValue && ids.Contains(approval.RunId.Value))
            .OrderBy(approval => approval.CreatedAt)
            .Select(approval => new NhAssistantEvaluationProposal(
                approval.Id,
                approval.RunId!.Value,
                approval.ToolId,
                approval.ArgumentsHash,
                approval.Status))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The objects each proposal changes, as <c>type:id</c>.
    /// </summary>
    public async Task<ILookup<Guid, string>> GetObjectsAsync(
        IReadOnlyCollection<Guid> approvalIds,
        CancellationToken cancellationToken)
    {
        if (approvalIds.Count == 0)
        {
            return Array.Empty<(Guid, string)>().ToLookup(item => item.Item1, item => item.Item2);
        }

        await using var context = contextFactory.CreateDbContext();
        var ids = approvalIds.ToArray();
        var keys = await context.ApprovalKeys
            .AsNoTracking()
            .Where(key => ids.Contains(key.ApprovalId))
            .Select(key => new { key.ApprovalId, key.KeyType, key.KeyId })
            .ToListAsync(cancellationToken);
        return keys.ToLookup(key => key.ApprovalId, key => key.KeyType + ":" + key.KeyId);
    }

    /// <summary>
    /// The feedback categories people gave on each proposal.
    /// </summary>
    public async Task<ILookup<Guid, string>> GetFeedbackCategoriesAsync(
        IReadOnlyCollection<Guid> approvalIds,
        CancellationToken cancellationToken)
    {
        if (approvalIds.Count == 0)
        {
            return Array.Empty<(Guid, string)>().ToLookup(item => item.Item1, item => item.Item2);
        }

        await using var context = contextFactory.CreateDbContext();
        var ids = approvalIds.ToArray();
        var rows = await context.ProposalFeedback
            .AsNoTracking()
            .Where(feedback => ids.Contains(feedback.ApprovalId))
            .Select(feedback => new { feedback.ApprovalId, feedback.Category })
            .ToListAsync(cancellationToken);
        return rows.ToLookup(row => row.ApprovalId, row => row.Category);
    }

    public async Task<AssistantAgentReplay?> FindActiveReplayAsync(string variantAgentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentReplays
            .AsNoTracking()
            .FirstOrDefaultAsync(
                replay => replay.VariantAgentId == variantAgentId && replay.Status == NhAssistantReplayStatuses.Running,
                cancellationToken);
    }

    public async Task<AssistantAgentReplay?> FindReplayAsync(Guid replayId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentReplays
            .AsNoTracking()
            .SingleOrDefaultAsync(replay => replay.Id == replayId, cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantAgentReplay>> ListReplaysAsync(
        string variantAgentId,
        int take,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AgentReplays
            .AsNoTracking()
            .Where(replay => replay.VariantAgentId == variantAgentId)
            .OrderByDescending(replay => replay.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task AddReplayAsync(AssistantAgentReplay replay, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        context.AgentReplays.Add(replay);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Saves the progress of a replay; <see langword="false"/> when another worker changed it first.
    /// </summary>
    public async Task<bool> TrySaveReplayAsync(AssistantAgentReplay replay, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var expected = replay.ConcurrencyStamp;
        replay.ConcurrencyStamp = Guid.NewGuid();
        context.AgentReplays.Attach(replay);
        context.Entry(replay).Property(item => item.ConcurrencyStamp).OriginalValue = expected;
        context.Entry(replay).State = EntityState.Modified;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>
    /// The next runs of <paramref name="sourceAgentId"/> in the period after the cursor, oldest first,
    /// with their threads. Replays and runs that still work are left out.
    /// </summary>
    public async Task<IReadOnlyList<(AssistantRun Run, AssistantThread Thread)>> GetReplaySourceRunsAsync(
        AssistantAgentReplay replay,
        int take,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var query = context.Runs
            .AsNoTracking()
            .Where(run => run.CreatedAt >= replay.From
                && run.CreatedAt < replay.To
                && run.ReplayOfRunId == null
                && !ActiveRunStatuses.Contains(run.Status))
            .Join(
                context.Threads.AsNoTracking().Where(thread => thread.AgentId == replay.SourceAgentId),
                run => run.ConversationId,
                thread => thread.ConversationId,
                (run, thread) => new { Run = run, Thread = thread });
        if (replay.CursorCreatedAt is { } after)
        {
            // Runs are created one at a time, so no two of an agent share a creation time.
            query = query.Where(row => row.Run.CreatedAt > after);
        }

        var rows = await query
            .OrderBy(row => row.Run.CreatedAt)
            .ThenBy(row => row.Run.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        return rows.Select(row => (row.Run, row.Thread)).ToArray();
    }

    /// <summary>
    /// The inputs a run claimed, oldest first.
    /// </summary>
    public async Task<IReadOnlyList<AssistantQueuedInput>> GetClaimedInputsAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.QueuedInputs
            .AsNoTracking()
            .Where(input => input.ConsumedByRunId == runId)
            .OrderBy(input => input.CreatedAt)
            .ThenBy(input => input.Id)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<RunRow> RunsOf(NhAssistantDbContext context, string agentId)
    {
        return context.Runs
            .AsNoTracking()
            .Join(
                context.Threads.AsNoTracking().Where(thread => thread.AgentId == agentId),
                run => run.ConversationId,
                thread => thread.ConversationId,
                (run, thread) => new RunRow { Run = run, Thread = thread });
    }

    private static IQueryable<NhAssistantEvaluationRun> Project(IQueryable<RunRow> rows)
    {
        return rows.Select(row => new NhAssistantEvaluationRun(
            row.Run.Id,
            row.Run.ConversationId,
            row.Thread.SubjectType,
            row.Thread.SubjectId,
            row.Run.TriggerId,
            row.Run.TriggerEventKey,
            row.Run.ReplayOfRunId,
            row.Run.Status,
            row.Run.SkillId,
            row.Run.SkillChosenBy,
            row.Run.ModelCalls,
            row.Run.InputTokens + row.Run.OutputTokens,
            row.Run.ToolCalls,
            row.Run.CreatedAt));
    }

    private sealed class RunRow
    {
        public required AssistantRun Run { get; init; }

        public required AssistantThread Thread { get; init; }
    }
}
