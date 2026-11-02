using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The lifecycle of open alerts beyond resolving by key (FB87 phase 2): resolving by omission after a rule
/// run, and the reads and conditional closes of a revalidation. Every close runs under the subject's
/// transaction lock, as raising and resolving do.
/// </summary>
internal sealed partial class NhAssistantAlertStore
{
    /// <summary>
    /// Resolves the open alerts of <paramref name="kindId"/> about <paramref name="subject"/> in
    /// <paramref name="scopeKey"/> that <paramref name="agentId"/> raised last, live or shadow as
    /// <paramref name="shadow"/> says, whose key is not one of <paramref name="raisedKeys"/>, at most
    /// <paramref name="limit"/>, the oldest first: they close and their waiting deliveries are taken back.
    /// An alert whose expiry passed is left to the maintenance, which closes it as expired. Returns the
    /// alerts that closed.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAlert>> ResolveNotRaisedAsync(
        string kindId,
        NhAiSubjectRef subject,
        string scopeKey,
        bool shadow,
        string agentId,
        IReadOnlySet<string> raisedKeys,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raisedKeys);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryResolveNotRaisedAsync(kindId, subject, scopeKey, shadow, agentId, raisedKeys, now, limit, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxWriteAttempts)
            {
                // The maintenance expired one of the alerts meanwhile; read them again.
            }
        }
    }

    private async Task<IReadOnlyList<AssistantAlert>> TryResolveNotRaisedAsync(
        string kindId,
        NhAiSubjectRef subject,
        string scopeKey,
        bool shadow,
        string agentId,
        IReadOnlySet<string> raisedKeys,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockSubjectAsync(context, subject, shadow, cancellationToken);
        var open = await context.Alerts
            .AsNoTracking()
            .Where(alert => alert.ClosedMarker == Guid.Empty
                && alert.KindId == kindId
                && alert.SubjectType == subject.Type
                && alert.SubjectId == subject.Id
                && alert.ScopeKey == scopeKey
                && alert.Shadow == shadow
                && alert.AgentId == agentId
                && (alert.ExpiresAt == null || alert.ExpiresAt > now))
            .OrderBy(alert => alert.RaisedAt)
            .ThenBy(alert => alert.Id)
            .Select(alert => new { alert.Id, alert.Key })
            .ToListAsync(cancellationToken);

        // The keys compare exactly in memory: a database collation may ignore case, while two keys that
        // differ in case are two alerts, as their identities are.
        var omitted = open
            .Where(alert => !raisedKeys.Contains(alert.Key))
            .Select(alert => alert.Id)
            .Take(limit)
            .ToArray();
        if (omitted.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var closing = await context.Alerts
            .Include(alert => alert.Deliveries)
            .Where(alert => omitted.Contains(alert.Id) && alert.ClosedMarker == Guid.Empty)
            .OrderBy(alert => alert.RaisedAt)
            .ThenBy(alert => alert.Id)
            .ToListAsync(cancellationToken);
        foreach (var alert in closing)
        {
            Close(alert, NhAssistantAlertStatuses.Resolved, now);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return closing;
    }

    /// <summary>
    /// The open alerts about <paramref name="subject"/> of <paramref name="kindIds"/>, in
    /// <paramref name="scopeKey"/> or, for a subject its resolver no longer describes, in any scope, whose
    /// expiry did not pass, at most <paramref name="limit"/>: live alerts before shadow ones, each the oldest
    /// first. The rows are not tracked; <see cref="ResolveUnchangedAsync"/> closes one only as it was read.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAlert>> ListOpenToRevalidateAsync(
        NhAiSubjectRef subject,
        string? scopeKey,
        IReadOnlyCollection<string> kindIds,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kindIds);
        if (kindIds.Count == 0)
        {
            return [];
        }

        var kinds = kindIds.ToArray();
        await using var context = contextFactory.CreateDbContext();
        var query = context.Alerts
            .AsNoTracking()
            .Where(alert => alert.ClosedMarker == Guid.Empty
                && alert.SubjectType == subject.Type
                && alert.SubjectId == subject.Id
                && kinds.Contains(alert.KindId)
                && (alert.ExpiresAt == null || alert.ExpiresAt > now));
        if (scopeKey is not null)
        {
            query = query.Where(alert => alert.ScopeKey == scopeKey);
        }

        return await query
            .OrderBy(alert => alert.Shadow)
            .ThenBy(alert => alert.RaisedAt)
            .ThenBy(alert => alert.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the open alert <paramref name="read"/> as it was read: when it was raised again, resolved or
    /// expired meanwhile, it stays as it is now and <see langword="null"/> is returned, since a check looked
    /// at the content it had then. Its waiting deliveries are taken back.
    /// </summary>
    public async Task<AssistantAlert?> ResolveUnchangedAsync(AssistantAlert read, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryResolveUnchangedAsync(read, now, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxWriteAttempts)
            {
                // A delivery went out, or the maintenance expired the alert, meanwhile; read it again.
            }
        }
    }

    private async Task<AssistantAlert?> TryResolveUnchangedAsync(AssistantAlert read, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockSubjectAsync(context, new NhAiSubjectRef(read.SubjectType, read.SubjectId), read.Shadow, cancellationToken);
        var alert = await context.Alerts
            .Include(item => item.Deliveries)
            .SingleOrDefaultAsync(
                item => item.Id == read.Id
                    && item.ClosedMarker == Guid.Empty
                    && item.ConcurrencyStamp == read.ConcurrencyStamp,
                cancellationToken);
        if (alert is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        Close(alert, NhAssistantAlertStatuses.Resolved, now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return alert;
    }
}
