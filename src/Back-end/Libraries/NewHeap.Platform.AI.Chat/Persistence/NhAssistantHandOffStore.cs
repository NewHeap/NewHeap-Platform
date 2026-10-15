using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Persistence of hand-offs between agents: their records, the chain that led to a run, the daily
/// limit per pair and subject, and the follow-ups to their sources. Every call uses its own
/// short-lived context.
/// </summary>
internal sealed class NhAssistantHandOffStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// The outcome of a hand-off while its trigger fires.
    /// </summary>
    public const string PendingOutcome = "pending";

    /// <summary>
    /// Adds a hand-off, or returns <see langword="false"/> when the same source run already handed
    /// the same subject to the same agent.
    /// </summary>
    public async Task<bool> TryAddAsync(AssistantHandOff handOff, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        context.HandOffs.Add(handOff);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    public async Task<AssistantHandOff?> FindAsync(
        Guid sourceRunId,
        string targetAgentId,
        NhAiSubjectRef subject,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.HandOffs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                handOff => handOff.SourceRunId == sourceRunId
                    && handOff.TargetAgentId == targetAgentId
                    && handOff.SubjectType == subject.Type
                    && handOff.SubjectId == subject.Id,
                cancellationToken);
    }

    /// <summary>
    /// The hand-offs from <paramref name="sourceAgentId"/> to <paramref name="targetAgentId"/> about
    /// one subject since <paramref name="since"/>, from any run.
    /// </summary>
    public async Task<int> CountSinceAsync(
        string sourceAgentId,
        string targetAgentId,
        NhAiSubjectRef subject,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.HandOffs
            .CountAsync(
                handOff => handOff.SourceAgentId == sourceAgentId
                    && handOff.TargetAgentId == targetAgentId
                    && handOff.SubjectType == subject.Type
                    && handOff.SubjectId == subject.Id
                    && handOff.CreatedAt >= since,
                cancellationToken);
    }

    /// <summary>
    /// Records how the target received the hand-off. The run is only recorded while no run took
    /// the work yet: a run that claimed the input already did.
    /// </summary>
    public async Task SetOutcomeAsync(
        Guid handOffId,
        string outcome,
        string? code,
        Guid? conversationId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.HandOffs
            .Where(handOff => handOff.Id == handOffId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(handOff => handOff.Outcome, outcome)
                    .SetProperty(handOff => handOff.Code, code)
                    .SetProperty(handOff => handOff.TargetConversationId, conversationId)
                    .SetProperty(handOff => handOff.TargetRunId, handOff => handOff.TargetRunId ?? runId),
                cancellationToken);
    }

    public async Task DeleteAsync(Guid handOffId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.HandOffs
            .Where(handOff => handOff.Id == handOffId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Records on a run that the hand-off created who handed it the work, unless it already names
    /// an agent.
    /// </summary>
    public async Task MarkRunAsync(Guid runId, AssistantHandOff handOff, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Runs
            .Where(run => run.Id == runId && run.RequestedByRunId == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.RequestedByAgentId, handOff.SourceAgentId)
                    .SetProperty(run => run.RequestedByRunId, handOff.SourceRunId)
                    .SetProperty(run => run.HandOffDepth, handOff.Depth),
                cancellationToken);
    }

    /// <summary>
    /// Gives the hand-offs whose inputs <paramref name="runId"/> claimed to that run, and records on
    /// a run that names no agent yet who handed it the work: the oldest hand-off's source and the
    /// deepest chain. A run may claim the input of a hand-off that started another run.
    /// </summary>
    public async Task LinkAsync(Guid runId, IReadOnlyCollection<Guid> inputIds, CancellationToken cancellationToken)
    {
        if (inputIds.Count == 0)
        {
            return;
        }

        await using var context = contextFactory.CreateDbContext();
        var ids = inputIds.ToArray();
        await context.HandOffs
            .Where(handOff => ids.Contains(handOff.InputId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(handOff => handOff.TargetRunId, runId), cancellationToken);
        var linked = await context.HandOffs
            .AsNoTracking()
            .Where(handOff => handOff.TargetRunId == runId)
            .OrderBy(handOff => handOff.CreatedAt)
            .ThenBy(handOff => handOff.Id)
            .ToListAsync(cancellationToken);
        if (linked.Count == 0)
        {
            return;
        }

        var first = linked[0];
        var depth = linked.Max(handOff => handOff.Depth);
        await context.Runs
            .Where(run => run.Id == runId && run.RequestedByRunId == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.RequestedByAgentId, first.SourceAgentId)
                    .SetProperty(run => run.RequestedByRunId, first.SourceRunId)
                    .SetProperty(run => run.HandOffDepth, depth),
                cancellationToken);
    }

    /// <summary>
    /// The chain of hand-offs that led to <paramref name="runId"/>: how many there were and which
    /// agents handed work along it, read at most <paramref name="maxLevels"/> levels back.
    /// </summary>
    public async Task<(int Depth, IReadOnlySet<string> Agents)> GetChainAsync(
        Guid runId,
        int maxLevels,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var agents = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new List<Guid> { runId };
        var depth = 0;
        for (var level = 0; level < maxLevels && frontier.Count > 0; level++)
        {
            var targets = frontier.ToArray();
            var sources = await context.HandOffs
                .AsNoTracking()
                .Where(handOff => handOff.TargetRunId.HasValue && targets.Contains(handOff.TargetRunId.Value))
                .Select(handOff => new { handOff.SourceAgentId, handOff.SourceRunId })
                .ToListAsync(cancellationToken);
            if (sources.Count == 0)
            {
                break;
            }

            depth = level + 1;
            foreach (var source in sources)
            {
                agents.Add(source.SourceAgentId);
            }
            frontier = sources.Select(source => source.SourceRunId).Distinct().ToList();
        }

        return (depth, agents);
    }

    /// <summary>
    /// The hand-offs <paramref name="runId"/> took whose sources still wait for a follow-up.
    /// </summary>
    public async Task<IReadOnlyList<AssistantHandOff>> GetUnnotifiedAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.HandOffs
            .AsNoTracking()
            .Where(handOff => handOff.TargetRunId == runId && handOff.NotifySource && handOff.NotifiedAt == null)
            .OrderBy(handOff => handOff.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Marks a follow-up as sent; <see langword="false"/> when another worker sent it first.
    /// </summary>
    public async Task<bool> TryMarkNotifiedAsync(Guid handOffId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        return await context.HandOffs
            .Where(handOff => handOff.Id == handOffId && handOff.NotifiedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(handOff => handOff.NotifiedAt, now), cancellationToken) == 1;
    }

    /// <summary>
    /// The threads of the runs that handed work off, by run, for the run timeline.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> GetSourceConversationsAsync(
        IReadOnlyCollection<Guid> sourceRunIds,
        CancellationToken cancellationToken)
    {
        if (sourceRunIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        await using var context = contextFactory.CreateDbContext();
        var ids = sourceRunIds.ToArray();
        return await context.Runs
            .AsNoTracking()
            .Where(run => ids.Contains(run.Id))
            .ToDictionaryAsync(run => run.Id, run => run.ConversationId, cancellationToken);
    }
}
