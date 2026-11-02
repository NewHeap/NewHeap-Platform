using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The statuses of the set of a bulk decision (<see cref="AssistantDecisionBatch.Status"/>).
/// </summary>
internal static class NhAssistantDecisionBatchStatuses
{
    /// <summary>The overview made the set; nobody decided it yet.</summary>
    public const string Open = "open";

    /// <summary>A decision works through the set.</summary>
    public const string Deciding = "deciding";

    /// <summary>The set was decided; its result is stored.</summary>
    public const string Decided = "decided";
}

/// <summary>
/// Persistence of the sets of bulk decisions: the set an overview showed one person, claimed once by the
/// decision that works through it and closed with its result. Every change is a conditional update, so two
/// requests with the same set never both decide it.
/// </summary>
internal sealed class NhAssistantDecisionBatchStore(NhAssistantDbContextFactory contextFactory)
{
    public async Task AddAsync(AssistantDecisionBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using var context = contextFactory.CreateDbContext();
        context.DecisionBatches.Add(batch);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AssistantDecisionBatch?> FindAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.DecisionBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(batch => batch.Id == batchId, cancellationToken);
    }

    /// <summary>
    /// Claims the set for one decision: an open set, or one whose decision began before
    /// <paramref name="staleBefore"/> and never ended, such as one whose process stopped halfway. A set
    /// taken over keeps the decision it began with.
    /// </summary>
    public async Task<bool> TryClaimAsync(
        Guid batchId,
        string decision,
        DateTimeOffset now,
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var claimed = await context.DecisionBatches
            .Where(batch => batch.Id == batchId
                && (batch.Status == NhAssistantDecisionBatchStatuses.Open
                    || (batch.Status == NhAssistantDecisionBatchStatuses.Deciding && batch.ClaimedAt < staleBefore)))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(batch => batch.Status, NhAssistantDecisionBatchStatuses.Deciding)
                    .SetProperty(batch => batch.ClaimedAt, now)
                    .SetProperty(batch => batch.Decision, batch => batch.Decision ?? decision),
                cancellationToken);
        return claimed == 1;
    }

    /// <summary>
    /// Closes a set that is being decided with its result.
    /// </summary>
    public async Task<bool> CompleteAsync(
        Guid batchId,
        DateTimeOffset decidedAt,
        string resultJson,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var completed = await context.DecisionBatches
            .Where(batch => batch.Id == batchId && batch.Status == NhAssistantDecisionBatchStatuses.Deciding)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(batch => batch.Status, NhAssistantDecisionBatchStatuses.Decided)
                    .SetProperty(batch => batch.DecidedAt, decidedAt)
                    .SetProperty(batch => batch.ResultJson, resultJson),
                cancellationToken);
        return completed == 1;
    }

    /// <summary>
    /// Removes the sets whose lifetime ended before <paramref name="before"/>, decided or not.
    /// </summary>
    public async Task<int> RemoveExpiredAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.DecisionBatches
            .Where(batch => batch.ExpiresAt < before)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
