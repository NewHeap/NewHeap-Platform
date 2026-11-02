using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The kind, subject and person of one mute (FB87-11), as the delivery compares them.
/// </summary>
internal readonly record struct NhAssistantAlertSubjectMuteKey(string ActorId, string KindId, string SubjectType, string SubjectId);

/// <summary>
/// A person's mutes of one alert kind about one subject (FB87-11): "do not tell me about this object for
/// N days" when they dismiss an alert. Every call uses its own short-lived context and names the person,
/// so nobody reads or lifts another person's mutes. Ended mutes have no effect and are removed by the run
/// maintenance.
/// </summary>
internal sealed class NhAssistantAlertSubjectMuteStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>How often muting is tried again after a concurrent request created the row first.</summary>
    private const int MaxMuteAttempts = 3;

    /// <summary>
    /// Mutes <paramref name="kindId"/> about <paramref name="subject"/> for the person until
    /// <paramref name="until"/>, or keeps a mute of it that lasts longer, so muting again never shortens
    /// it. Returns the mute as stored.
    /// </summary>
    public async Task<AssistantAlertSubjectMute> MuteAsync(
        string actorId,
        string kindId,
        NhAiSubjectRef subject,
        string? subjectDisplayName,
        DateTimeOffset now,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kindId);
        for (var attempt = 1; ; attempt++)
        {
            await using var context = contextFactory.CreateDbContext();
            var extended = await context.AlertSubjectMutes
                .Where(mute => mute.ActorId == actorId
                    && mute.KindId == kindId
                    && mute.SubjectType == subject.Type
                    && mute.SubjectId == subject.Id)
                .ExecuteUpdateAsync(
                    update => update
                        // A mute that ended before the maintenance removed it starts again.
                        .SetProperty(mute => mute.MutedAt, mute => mute.Until > now ? mute.MutedAt : now)
                        .SetProperty(mute => mute.Until, mute => mute.Until > until ? mute.Until : until)
                        .SetProperty(mute => mute.SubjectDisplayName, subjectDisplayName),
                    cancellationToken);
            if (extended == 0)
            {
                context.AlertSubjectMutes.Add(new AssistantAlertSubjectMute
                {
                    Id = Guid.NewGuid(),
                    ActorId = actorId,
                    KindId = kindId,
                    SubjectType = subject.Type,
                    SubjectId = subject.Id,
                    SubjectDisplayName = subjectDisplayName,
                    MutedAt = now,
                    Until = until
                });
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException) when (attempt < MaxMuteAttempts)
                {
                    // A concurrent request of the same person muted the subject first; extend its row.
                    continue;
                }
            }

            await using var reading = contextFactory.CreateDbContext();
            return await reading.AlertSubjectMutes
                .AsNoTracking()
                .SingleAsync(
                    mute => mute.ActorId == actorId
                        && mute.KindId == kindId
                        && mute.SubjectType == subject.Type
                        && mute.SubjectId == subject.Id,
                    cancellationToken);
        }
    }

    /// <summary>
    /// The person's mutes that have not ended at <paramref name="now"/>, the most recent first, at most
    /// <paramref name="limit"/>.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAlertSubjectMute>> ListActiveAsync(
        string actorId,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        await using var context = contextFactory.CreateDbContext();
        return await context.AlertSubjectMutes
            .AsNoTracking()
            .Where(mute => mute.ActorId == actorId && mute.Until > now)
            .OrderByDescending(mute => mute.MutedAt)
            .ThenBy(mute => mute.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The mutes of <paramref name="actorIds"/> that have not ended at <paramref name="now"/>, for a
    /// delivery that serves several people at once.
    /// </summary>
    public async Task<IReadOnlySet<NhAssistantAlertSubjectMuteKey>> FindActiveAsync(
        IReadOnlyCollection<string> actorIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actorIds);
        if (actorIds.Count == 0)
        {
            return new HashSet<NhAssistantAlertSubjectMuteKey>();
        }

        var ids = actorIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var context = contextFactory.CreateDbContext();
        var active = await context.AlertSubjectMutes
            .AsNoTracking()
            .Where(mute => ids.Contains(mute.ActorId) && mute.Until > now)
            .Select(mute => new { mute.ActorId, mute.KindId, mute.SubjectType, mute.SubjectId })
            .ToListAsync(cancellationToken);
        return active
            .Select(mute => new NhAssistantAlertSubjectMuteKey(mute.ActorId, mute.KindId, mute.SubjectType, mute.SubjectId))
            .ToHashSet();
    }

    /// <summary>
    /// Lifts the person's own mutes among <paramref name="muteIds"/>; an id of another person's mute, or
    /// of none, changes nothing. Returns how many were lifted.
    /// </summary>
    public async Task<int> LiftAsync(string actorId, IReadOnlyCollection<Guid> muteIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(muteIds);
        if (muteIds.Count == 0)
        {
            return 0;
        }

        var ids = muteIds.Distinct().ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.AlertSubjectMutes
            .Where(mute => mute.ActorId == actorId && ids.Contains(mute.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Removes at most <paramref name="batchSize"/> mutes that ended at or before <paramref name="now"/>.
    /// Returns how many it removed.
    /// </summary>
    public async Task<int> DeleteEndedAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var ended = await context.AlertSubjectMutes
            .AsNoTracking()
            .Where(mute => mute.Until <= now)
            .OrderBy(mute => mute.Until)
            .ThenBy(mute => mute.Id)
            .Select(mute => mute.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (ended.Count == 0)
        {
            return 0;
        }

        return await context.AlertSubjectMutes
            .Where(mute => ended.Contains(mute.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Removes every mute of the person, for the retention of a forgotten user.
    /// </summary>
    internal static Task<int> ForgetActorAsync(NhAssistantDbContext context, string actorId, CancellationToken cancellationToken)
    {
        return context.AlertSubjectMutes
            .Where(mute => mute.ActorId == actorId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
