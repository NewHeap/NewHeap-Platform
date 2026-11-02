using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// One person an alert is planned for, with the moment the delivery falls due.
/// </summary>
internal sealed record NhAssistantAlertPlannedRecipient(string ActorId, DateTimeOffset DueAt);

/// <summary>
/// What the store records when an alert is raised: its identity, what its subject's resolver said, its
/// content, the people it is planned for and those who no longer qualify.
/// </summary>
internal sealed record NhAssistantAlertDraft(
    string KindId,
    NhAiSubjectInfo Subject,
    string Key,
    bool Shadow,
    NhAssistantAlertUrgency Urgency,
    string ParametersJson,
    DateTimeOffset? ExpiresAt,
    string? AgentId,
    Guid? RunId,
    DateTimeOffset Now)
{
    /// <summary>The people who qualify now, in order; empty for a shadow alert.</summary>
    public IReadOnlyList<NhAssistantAlertPlannedRecipient> Recipients { get; init; } = [];

    /// <summary>The people chosen who no longer qualify, with why; their waiting deliveries are dropped.</summary>
    public IReadOnlyDictionary<string, string> Dropped { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>After how long the open alert is delivered again when it is raised again.</summary>
    public TimeSpan? RepeatAfter { get; init; }

    /// <summary>The most new alerts the subject gets since <see cref="DayStart"/>.</summary>
    public int MaxPerSubjectPerDay { get; init; } = int.MaxValue;

    public DateTimeOffset DayStart { get; init; } = DateTimeOffset.MinValue;
}

/// <summary>
/// What became of a draft: the alert as it is stored now, the outcome, and an open alert the raise
/// found expired and closed first. <see cref="Alert"/> is <see langword="null"/> when the subject's
/// daily limit refused a new alert.
/// </summary>
internal sealed record NhAssistantAlertWrite(
    AssistantAlert? Alert,
    NhAssistantAlertOutcome Outcome,
    int RecipientCount,
    AssistantAlert? ExpiredFirst)
{
    public bool LimitReached => Alert is null;
}

/// <summary>
/// Persistence of alerts and their deliveries (FB87). Every write about one subject runs in a transaction
/// under the subject's transaction lock, so one open alert exists per kind, subject and key, raising it
/// again updates it in place and the daily limit per subject holds across instances; the unique index
/// over the alert's identity and its closed marker backs it up. The maintenance and the delivery change
/// rows with conditional updates, so several instances can run them at the same time. Every time is on
/// the application's clock, which callers pass.
/// </summary>
internal sealed partial class NhAssistantAlertStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>How long a write waits for the subject's alert lock.</summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How often a raise is tried again after another write changed the same alert first.</summary>
    private const int MaxWriteAttempts = 3;

    /// <summary>
    /// Records <paramref name="draft"/>: a new alert, or the open alert of its identity updated in place,
    /// with its deliveries planned. An open alert whose expiry passed is closed as expired first.
    /// </summary>
    /// <exception cref="TimeoutException">The subject's alert lock stayed taken past <see cref="LockTimeout"/>.</exception>
    public async Task<NhAssistantAlertWrite> RaiseAsync(NhAssistantAlertDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryRaiseAsync(draft, cancellationToken);
            }
            catch (DbUpdateException) when (attempt < MaxWriteAttempts)
            {
                // The maintenance closed or another instance wrote the same alert meanwhile; read it again.
            }
        }
    }

    private async Task<NhAssistantAlertWrite> TryRaiseAsync(NhAssistantAlertDraft draft, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockSubjectAsync(context, draft.Subject.Subject, draft.Shadow, cancellationToken);

        var identity = IdentityOf(context.Model.GetDefaultSchema(), draft.Subject.ScopeKey, draft.KindId, draft.Subject.Subject, draft.Key, draft.Shadow);
        var open = await context.Alerts
            .Include(alert => alert.Deliveries)
            .SingleOrDefaultAsync(alert => alert.IdentityHash == identity && alert.ClosedMarker == Guid.Empty, cancellationToken);

        AssistantAlert? expiredFirst = null;
        if (open is not null && open.ExpiresAt is { } expiresAt && expiresAt <= draft.Now)
        {
            // An alert whose expiry passed is no longer open, even before the maintenance closed it. It is
            // written first, so the new alert of its identity finds the open slot free.
            Close(open, NhAssistantAlertStatuses.Expired, draft.Now);
            await context.SaveChangesAsync(cancellationToken);
            expiredFirst = open;
            open = null;
        }

        NhAssistantAlertWrite write;
        if (open is null)
        {
            var raisedToday = await context.Alerts.CountAsync(
                alert => alert.SubjectType == draft.Subject.Subject.Type
                    && alert.SubjectId == draft.Subject.Subject.Id
                    && alert.ScopeKey == draft.Subject.ScopeKey
                    && alert.Shadow == draft.Shadow
                    && alert.RaisedAt >= draft.DayStart,
                cancellationToken);
            if (raisedToday >= draft.MaxPerSubjectPerDay)
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new NhAssistantAlertWrite(null, NhAssistantAlertOutcome.Raised, 0, expiredFirst);
            }

            var alert = Create(draft, identity);
            context.Alerts.Add(alert);
            write = new NhAssistantAlertWrite(alert, NhAssistantAlertOutcome.Raised, Reached(alert), expiredFirst);
        }
        else
        {
            write = Update(context, open, draft);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return write;
    }

    private static AssistantAlert Create(NhAssistantAlertDraft draft, string identity)
    {
        var alert = new AssistantAlert
        {
            Id = Guid.NewGuid(),
            KindId = draft.KindId,
            ScopeKey = draft.Subject.ScopeKey,
            SubjectType = draft.Subject.Subject.Type,
            SubjectId = draft.Subject.Subject.Id,
            Key = draft.Key,
            IdentityHash = identity,
            ClosedMarker = Guid.Empty,
            Status = NhAssistantAlertStatuses.Open,
            Urgency = draft.Urgency,
            ParametersJson = draft.ParametersJson,
            Revision = 1,
            Shadow = draft.Shadow,
            AgentId = draft.AgentId,
            RunId = draft.RunId,
            RaisedAt = draft.Now,
            UpdatedAt = draft.Now,
            DeliveryPlannedAt = draft.Now,
            ExpiresAt = draft.ExpiresAt,
            ConcurrencyStamp = Guid.NewGuid()
        };
        Describe(alert, draft.Subject);
        foreach (var recipient in draft.Recipients)
        {
            alert.Deliveries.Add(NewDelivery(alert, recipient, draft.Now));
        }

        return alert;
    }

    /// <summary>
    /// Updates the open alert in place: new content is its next revision, and a higher urgency or the
    /// kind's repeat interval plans it again for everyone chosen now who did not dismiss it. People chosen
    /// for the first time are planned either way. A person chosen before and not now keeps their delivery,
    /// since another raiser may name other people for the same alert; the delivery's own check leaves
    /// them out once they no longer qualify. Waiting deliveries of people chosen now who no longer qualify
    /// are dropped.
    /// </summary>
    private static NhAssistantAlertWrite Update(NhAssistantDbContext context, AssistantAlert alert, NhAssistantAlertDraft draft)
    {
        var changed = alert.Urgency != draft.Urgency
            || !string.Equals(alert.ParametersJson, draft.ParametersJson, StringComparison.Ordinal)
            || alert.ExpiresAt != draft.ExpiresAt;
        var redeliver = draft.Urgency > alert.Urgency
            || (draft.RepeatAfter is { } repeatAfter && draft.Now - alert.DeliveryPlannedAt >= repeatAfter);
        if (changed)
        {
            alert.Revision++;
            alert.Urgency = draft.Urgency;
            alert.ParametersJson = draft.ParametersJson;
            alert.ExpiresAt = draft.ExpiresAt;
        }
        if (redeliver)
        {
            alert.DeliveryPlannedAt = draft.Now;
        }

        alert.UpdatedAt = draft.Now;
        alert.AgentId = draft.AgentId;
        alert.RunId = draft.RunId;
        alert.ConcurrencyStamp = Guid.NewGuid();
        Describe(alert, draft.Subject);

        var existing = alert.Deliveries.ToDictionary(delivery => delivery.RecipientActorId, StringComparer.Ordinal);
        var planned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipient in draft.Recipients)
        {
            planned.Add(recipient.ActorId);
            if (!existing.TryGetValue(recipient.ActorId, out var delivery))
            {
                // Tracked as added; the alert's navigation picks it up through the context's fix-up.
                var added = NewDelivery(alert, recipient, draft.Now);
                context.AlertDeliveries.Add(added);
                if (!alert.Deliveries.Contains(added))
                {
                    alert.Deliveries.Add(added);
                }
                continue;
            }

            PlanAgain(delivery, alert.Revision, recipient.DueAt, redeliver, draft.Now);
        }

        foreach (var delivery in alert.Deliveries.Where(delivery =>
                     draft.Dropped.ContainsKey(delivery.RecipientActorId)
                     && delivery.Status == NhAssistantAlertDeliveryStatuses.Pending))
        {
            delivery.Status = NhAssistantAlertDeliveryStatuses.Dropped;
            delivery.ReasonCode = draft.Dropped[delivery.RecipientActorId];
            delivery.ClosedAt = draft.Now;
            ReleaseClaim(delivery, draft.Now);
        }

        var outcome = redeliver
            ? NhAssistantAlertOutcome.Redelivered
            : changed ? NhAssistantAlertOutcome.Updated : NhAssistantAlertOutcome.Unchanged;
        return new NhAssistantAlertWrite(alert, outcome, Reached(alert), null);
    }

    /// <summary>
    /// Plans one person's existing delivery for the alert's current revision. A dismissal stays: the
    /// person does not get the alert again until its key changes. A delivery again plans everything else
    /// anew; otherwise only a person who never received it is planned again, and a delivery that still
    /// waits carries the new revision.
    /// </summary>
    private static void PlanAgain(AssistantAlertDelivery delivery, int revision, DateTimeOffset dueAt, bool redeliver, DateTimeOffset now)
    {
        switch (delivery.Status)
        {
            case NhAssistantAlertDeliveryStatuses.Dismissed:
                return;
            case NhAssistantAlertDeliveryStatuses.Pending:
                delivery.PlannedRevision = revision;
                if (redeliver && (delivery.DueAt is null || dueAt < delivery.DueAt))
                {
                    delivery.DueAt = dueAt;
                }
                delivery.UpdatedAt = now;
                delivery.ConcurrencyStamp = Guid.NewGuid();
                return;
        }

        if (!redeliver && (delivery.SentAt is not null || delivery.ReadAt is not null))
        {
            // The person received or read the alert; its new content shows where they read it.
            return;
        }

        delivery.Status = NhAssistantAlertDeliveryStatuses.Pending;
        delivery.ReasonCode = null;
        delivery.DueAt = dueAt;
        delivery.PlannedRevision = revision;
        delivery.Attempts = 0;
        delivery.ClosedAt = null;
        ReleaseClaim(delivery, now);
    }

    /// <summary>
    /// The people the alert reaches now: every delivery that waits, went out or was read.
    /// </summary>
    private static int Reached(AssistantAlert alert)
    {
        return alert.Deliveries.Count(delivery => delivery.Status
            is NhAssistantAlertDeliveryStatuses.Pending
            or NhAssistantAlertDeliveryStatuses.Sent
            or NhAssistantAlertDeliveryStatuses.Read);
    }

    private static AssistantAlertDelivery NewDelivery(AssistantAlert alert, NhAssistantAlertPlannedRecipient recipient, DateTimeOffset now)
    {
        return new AssistantAlertDelivery
        {
            Id = Guid.NewGuid(),
            AlertId = alert.Id,
            RecipientActorId = recipient.ActorId,
            Status = NhAssistantAlertDeliveryStatuses.Pending,
            DueAt = recipient.DueAt,
            PlannedRevision = alert.Revision,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
    }

    private static void Describe(AssistantAlert alert, NhAiSubjectInfo subject)
    {
        alert.SubjectDisplayName = subject.DisplayName is { } name ? NhAssistantContent.Bound(name, 200) : null;
        alert.DivisionId = subject.DivisionId;
        alert.TenantId = subject.TenantId;
        alert.ParentSubjectType = subject.Parent?.Subject.Type;
        alert.ParentSubjectId = subject.Parent?.Subject.Id;
        alert.ParentDisplayName = subject.Parent?.DisplayName is { } parent
            ? NhAssistantContent.Bound(parent, NhAiSubjectParent.MaxDisplayNameLength)
            : null;
    }

    /// <summary>
    /// Resolves the open alerts of <paramref name="kindId"/> about <paramref name="subject"/>, with
    /// <paramref name="key"/> or any key, in <paramref name="scopeKey"/> or, for a subject its resolver no
    /// longer describes, in any scope: they close and their waiting deliveries are taken back. Returns the
    /// alerts that closed.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAlert>> ResolveAsync(
        string kindId,
        NhAiSubjectRef subject,
        string? key,
        string? scopeKey,
        bool shadow,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryResolveAsync(kindId, subject, key, scopeKey, shadow, now, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxWriteAttempts)
            {
                // The maintenance expired one of the alerts meanwhile; read them again.
            }
        }
    }

    private async Task<IReadOnlyList<AssistantAlert>> TryResolveAsync(
        string kindId,
        NhAiSubjectRef subject,
        string? key,
        string? scopeKey,
        bool shadow,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockSubjectAsync(context, subject, shadow, cancellationToken);
        var query = context.Alerts
            .Include(alert => alert.Deliveries)
            .Where(alert => alert.ClosedMarker == Guid.Empty
                && alert.KindId == kindId
                && alert.SubjectType == subject.Type
                && alert.SubjectId == subject.Id
                && alert.Shadow == shadow);
        if (key is not null)
        {
            query = query.Where(alert => alert.Key == key);
        }
        if (scopeKey is not null)
        {
            query = query.Where(alert => alert.ScopeKey == scopeKey);
        }

        var open = await query.ToListAsync(cancellationToken);
        foreach (var alert in open)
        {
            Close(alert, NhAssistantAlertStatuses.Resolved, now);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return open;
    }

    /// <summary>
    /// Closes up to <paramref name="batchSize"/> open alerts whose expiry <paramref name="now"/> reached,
    /// each with a conditional update, and takes their waiting deliveries back. Returns the alerts that
    /// closed, as they were before.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAlert>> ExpireDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var due = await context.Alerts
            .AsNoTracking()
            .Where(alert => alert.Status == NhAssistantAlertStatuses.Open && alert.ExpiresAt != null && alert.ExpiresAt <= now)
            .OrderBy(alert => alert.ExpiresAt)
            .ThenBy(alert => alert.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        var expired = new List<AssistantAlert>();
        foreach (var alert in due)
        {
            var closed = await context.Alerts
                .Where(item => item.Id == alert.Id
                    && item.ConcurrencyStamp == alert.ConcurrencyStamp
                    && item.Status == NhAssistantAlertStatuses.Open)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(item => item.Status, NhAssistantAlertStatuses.Expired)
                        .SetProperty(item => item.ClosedAt, now)
                        .SetProperty(item => item.ClosedMarker, item => item.Id)
                        .SetProperty(item => item.ConcurrencyStamp, Guid.NewGuid()),
                    cancellationToken);
            if (closed != 1)
            {
                // Raised again, resolved or expired by another instance meanwhile.
                continue;
            }

            await TakeBackAsync(context, alert.Id, NhAssistantAlertDeliveryStatuses.Expired, now, cancellationToken);
            expired.Add(alert);
        }

        return expired;
    }

    /// <summary>
    /// Removes up to <paramref name="batchSize"/> closed alerts that closed before <paramref name="before"/>,
    /// with their deliveries. Returns how many.
    /// </summary>
    public async Task<int> PurgeClosedAsync(DateTimeOffset before, int batchSize, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var ids = await context.Alerts
            .Where(alert => alert.Status != NhAssistantAlertStatuses.Open && alert.ClosedAt != null && alert.ClosedAt < before)
            .OrderBy(alert => alert.ClosedAt)
            .ThenBy(alert => alert.Id)
            .Select(alert => alert.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return 0;
        }

        await context.AlertDeliveries
            .Where(delivery => ids.Contains(delivery.AlertId))
            .ExecuteDeleteAsync(cancellationToken);
        return await context.Alerts
            .Where(alert => ids.Contains(alert.Id) && alert.Status != NhAssistantAlertStatuses.Open)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<AssistantAlert?> FindAsync(Guid alertId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Alerts
            .AsNoTracking()
            .Include(alert => alert.Deliveries)
            .SingleOrDefaultAsync(alert => alert.Id == alertId, cancellationToken);
    }

    /// <summary>
    /// The open alert of an identity, with its deliveries, or <see langword="null"/>.
    /// </summary>
    public async Task<AssistantAlert?> FindOpenAsync(
        string scopeKey,
        string kindId,
        NhAiSubjectRef subject,
        string key,
        bool shadow,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var identity = IdentityOf(context.Model.GetDefaultSchema(), scopeKey, kindId, subject, key, shadow);
        return await context.Alerts
            .AsNoTracking()
            .Include(alert => alert.Deliveries)
            .SingleOrDefaultAsync(alert => alert.IdentityHash == identity && alert.ClosedMarker == Guid.Empty, cancellationToken);
    }

    /// <summary>
    /// The person's delivery of one alert with the alert, or <see langword="null"/>: the row a read or a
    /// dismissal of the alert list changes, after the recipient check.
    /// </summary>
    public async Task<AssistantAlertDelivery?> FindDeliveryAsync(Guid alertId, string recipientActorId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.AlertDeliveries
            .AsNoTracking()
            .Include(delivery => delivery.Alert)
            .SingleOrDefaultAsync(
                delivery => delivery.AlertId == alertId && delivery.RecipientActorId == recipientActorId,
                cancellationToken);
    }

    /// <summary>
    /// The person's deliveries with their alerts, newest alert first, at most <paramref name="limit"/>:
    /// those in <paramref name="statuses"/>, or every one that was not dropped when it is empty. Shadow
    /// alerts have no deliveries, so they never appear.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAlertDelivery>> ListForRecipientAsync(
        string recipientActorId,
        IReadOnlyCollection<string> statuses,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        await using var context = contextFactory.CreateDbContext();
        var query = context.AlertDeliveries
            .AsNoTracking()
            .Include(delivery => delivery.Alert)
            .Where(delivery => delivery.RecipientActorId == recipientActorId);
        query = statuses.Count > 0
            ? query.Where(delivery => statuses.Contains(delivery.Status))
            : query.Where(delivery => delivery.Status != NhAssistantAlertDeliveryStatuses.Dropped);
        return await query
            .OrderByDescending(delivery => delivery.Alert!.UpdatedAt)
            .ThenBy(delivery => delivery.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Marks the person's delivery read: a waiting one is then not sent, since the person saw the alert.
    /// Returns <see langword="false"/> for a delivery that was dismissed, taken back or does not exist.
    /// </summary>
    public async Task<bool> MarkReadAsync(Guid alertId, string recipientActorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var changed = await context.AlertDeliveries
            .Where(delivery => delivery.AlertId == alertId
                && delivery.RecipientActorId == recipientActorId
                && (delivery.Status == NhAssistantAlertDeliveryStatuses.Pending
                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Sent
                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Read))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(delivery => delivery.Status, NhAssistantAlertDeliveryStatuses.Read)
                    .SetProperty(delivery => delivery.ReadAt, delivery => delivery.ReadAt ?? now)
                    .SetProperty(delivery => delivery.ClaimedBy, (string?)null)
                    .SetProperty(delivery => delivery.ClaimedUntil, (DateTimeOffset?)null)
                    .SetProperty(delivery => delivery.UpdatedAt, now)
                    .SetProperty(delivery => delivery.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return changed == 1;
    }

    /// <summary>
    /// Dismisses the person's delivery: raising the alert again does not reach them until its key
    /// changes. Returns <see langword="false"/> for a delivery that was taken back or does not exist; a
    /// delivery that was dismissed already stays as it is and succeeds.
    /// </summary>
    public async Task<bool> DismissAsync(Guid alertId, string recipientActorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var changed = await context.AlertDeliveries
            .Where(delivery => delivery.AlertId == alertId
                && delivery.RecipientActorId == recipientActorId
                && (delivery.Status == NhAssistantAlertDeliveryStatuses.Pending
                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Sent
                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Read
                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Dismissed))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(delivery => delivery.Status, NhAssistantAlertDeliveryStatuses.Dismissed)
                    .SetProperty(delivery => delivery.DismissedAt, delivery => delivery.DismissedAt ?? now)
                    .SetProperty(delivery => delivery.ClaimedBy, (string?)null)
                    .SetProperty(delivery => delivery.ClaimedUntil, (DateTimeOffset?)null)
                    .SetProperty(delivery => delivery.UpdatedAt, now)
                    .SetProperty(delivery => delivery.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return changed == 1;
    }

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> waiting deliveries of open alerts that are due at
    /// <paramref name="now"/>, oldest first, for <paramref name="claimant"/> until
    /// <paramref name="claimedUntil"/>; a claim that ran out is taken over. Each row is claimed with a
    /// conditional update, so two instances never claim the same row. Returns the claimed rows with their
    /// alerts.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAlertDelivery>> ClaimDueAsync(
        DateTimeOffset now,
        string claimant,
        DateTimeOffset claimedUntil,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimant);
        await using var context = contextFactory.CreateDbContext();
        var candidates = await context.AlertDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.Status == NhAssistantAlertDeliveryStatuses.Pending
                && delivery.DueAt != null
                && delivery.DueAt <= now
                && (delivery.ClaimedUntil == null || delivery.ClaimedUntil < now)
                && delivery.Alert!.Status == NhAssistantAlertStatuses.Open
                && !delivery.Alert.Shadow)
            .OrderBy(delivery => delivery.DueAt)
            .ThenBy(delivery => delivery.Id)
            .Select(delivery => new { delivery.Id, delivery.ConcurrencyStamp })
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        var claimed = new List<Guid>();
        foreach (var candidate in candidates)
        {
            var stamp = Guid.NewGuid();
            var changed = await context.AlertDeliveries
                .Where(delivery => delivery.Id == candidate.Id
                    && delivery.ConcurrencyStamp == candidate.ConcurrencyStamp
                    && delivery.Status == NhAssistantAlertDeliveryStatuses.Pending)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(delivery => delivery.ClaimedBy, claimant)
                        .SetProperty(delivery => delivery.ClaimedUntil, claimedUntil)
                        .SetProperty(delivery => delivery.Attempts, delivery => delivery.Attempts + 1)
                        .SetProperty(delivery => delivery.UpdatedAt, now)
                        .SetProperty(delivery => delivery.ConcurrencyStamp, stamp),
                    cancellationToken);
            if (changed == 1)
            {
                claimed.Add(candidate.Id);
            }
        }
        if (claimed.Count == 0)
        {
            return [];
        }

        return await context.AlertDeliveries
            .AsNoTracking()
            .Include(delivery => delivery.Alert)
            .Where(delivery => claimed.Contains(delivery.Id))
            .OrderBy(delivery => delivery.DueAt)
            .ThenBy(delivery => delivery.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Records that a claimed delivery went out for <paramref name="revision"/> of its alert, through
    /// <paramref name="channels"/> and in the digest <paramref name="digestId"/>, if any. Returns
    /// <see langword="false"/> when the claim was lost or the delivery was taken back meanwhile.
    /// </summary>
    public async Task<bool> MarkSentAsync(
        Guid deliveryId,
        string claimant,
        int revision,
        string? channels,
        Guid? digestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var changed = await context.AlertDeliveries
            .Where(delivery => delivery.Id == deliveryId
                && delivery.ClaimedBy == claimant
                && delivery.Status == NhAssistantAlertDeliveryStatuses.Pending)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(delivery => delivery.Status, NhAssistantAlertDeliveryStatuses.Sent)
                    .SetProperty(delivery => delivery.SentAt, now)
                    .SetProperty(delivery => delivery.SentRevision, revision)
                    .SetProperty(delivery => delivery.Channels, channels)
                    .SetProperty(delivery => delivery.DigestId, digestId)
                    .SetProperty(delivery => delivery.ReasonCode, (string?)null)
                    .SetProperty(delivery => delivery.ClaimedBy, (string?)null)
                    .SetProperty(delivery => delivery.ClaimedUntil, (DateTimeOffset?)null)
                    .SetProperty(delivery => delivery.UpdatedAt, now)
                    .SetProperty(delivery => delivery.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return changed == 1;
    }

    /// <summary>
    /// Releases a claimed delivery that could not go out, to be due again at <paramref name="dueAt"/>,
    /// with the failure's code.
    /// </summary>
    public async Task<bool> ReleaseAsync(
        Guid deliveryId,
        string claimant,
        DateTimeOffset dueAt,
        string? reasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var changed = await context.AlertDeliveries
            .Where(delivery => delivery.Id == deliveryId
                && delivery.ClaimedBy == claimant
                && delivery.Status == NhAssistantAlertDeliveryStatuses.Pending)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(delivery => delivery.DueAt, dueAt)
                    .SetProperty(delivery => delivery.ReasonCode, reasonCode)
                    .SetProperty(delivery => delivery.ClaimedBy, (string?)null)
                    .SetProperty(delivery => delivery.ClaimedUntil, (DateTimeOffset?)null)
                    .SetProperty(delivery => delivery.UpdatedAt, now)
                    .SetProperty(delivery => delivery.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return changed == 1;
    }

    /// <summary>
    /// Drops a waiting delivery whose recipient no longer qualifies, with why
    /// (<see cref="NhAssistantAlertDropReasons"/>). Returns <see langword="false"/> when it no longer waits.
    /// </summary>
    public async Task<bool> DropAsync(Guid deliveryId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var context = contextFactory.CreateDbContext();
        var changed = await context.AlertDeliveries
            .Where(delivery => delivery.Id == deliveryId && delivery.Status == NhAssistantAlertDeliveryStatuses.Pending)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(delivery => delivery.Status, NhAssistantAlertDeliveryStatuses.Dropped)
                    .SetProperty(delivery => delivery.ReasonCode, reason)
                    .SetProperty(delivery => delivery.ClosedAt, now)
                    .SetProperty(delivery => delivery.ClaimedBy, (string?)null)
                    .SetProperty(delivery => delivery.ClaimedUntil, (DateTimeOffset?)null)
                    .SetProperty(delivery => delivery.UpdatedAt, now)
                    .SetProperty(delivery => delivery.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return changed == 1;
    }

    /// <summary>
    /// Closes a claimed delivery that was due without sending it, with why
    /// (<see cref="NhAssistantAlertWithholdReasons"/>), such as for a person who muted its kind. Returns
    /// <see langword="false"/> when the claim was lost or the delivery was taken back meanwhile.
    /// </summary>
    public async Task<bool> WithholdAsync(
        Guid deliveryId,
        string claimant,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var context = contextFactory.CreateDbContext();
        var changed = await context.AlertDeliveries
            .Where(delivery => delivery.Id == deliveryId
                && delivery.ClaimedBy == claimant
                && delivery.Status == NhAssistantAlertDeliveryStatuses.Pending)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(delivery => delivery.Status, NhAssistantAlertDeliveryStatuses.Withheld)
                    .SetProperty(delivery => delivery.ReasonCode, reason)
                    .SetProperty(delivery => delivery.ClosedAt, now)
                    .SetProperty(delivery => delivery.ClaimedBy, (string?)null)
                    .SetProperty(delivery => delivery.ClaimedUntil, (DateTimeOffset?)null)
                    .SetProperty(delivery => delivery.UpdatedAt, now)
                    .SetProperty(delivery => delivery.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return changed == 1;
    }

    /// <summary>
    /// Which of <paramref name="deliveryIds"/> <paramref name="claimant"/> still holds, waiting, for an
    /// alert that is still open: the check right before a notification goes out, so a delivery that was
    /// read, resolved or expired since it was claimed is not sent.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> FindStillClaimedAsync(
        IReadOnlyCollection<Guid> deliveryIds,
        string claimant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliveryIds);
        if (deliveryIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = deliveryIds.ToArray();
        await using var context = contextFactory.CreateDbContext();
        var held = await context.AlertDeliveries
            .AsNoTracking()
            .Where(delivery => ids.Contains(delivery.Id)
                && delivery.ClaimedBy == claimant
                && delivery.Status == NhAssistantAlertDeliveryStatuses.Pending
                && delivery.Alert!.Status == NhAssistantAlertStatuses.Open)
            .Select(delivery => delivery.Id)
            .ToListAsync(cancellationToken);
        return held.ToHashSet();
    }

    /// <summary>
    /// How many alerts went out to each of <paramref name="recipientActorIds"/> since
    /// <paramref name="since"/>, for the daily limit per person; a person without any is missing.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> CountSentSinceAsync(
        IReadOnlyCollection<string> recipientActorIds,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipientActorIds);
        if (recipientActorIds.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var ids = recipientActorIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var context = contextFactory.CreateDbContext();
        var counts = await context.AlertDeliveries
            .AsNoTracking()
            .Where(delivery => ids.Contains(delivery.RecipientActorId) && delivery.SentAt != null && delivery.SentAt >= since)
            .GroupBy(delivery => delivery.RecipientActorId)
            .Select(group => new { ActorId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(item => item.ActorId, item => item.Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// Removes every delivery to the person, for the retention of a forgotten user.
    /// </summary>
    internal static Task<int> ForgetRecipientAsync(NhAssistantDbContext context, string actorId, CancellationToken cancellationToken)
    {
        return context.AlertDeliveries
            .Where(delivery => delivery.RecipientActorId == actorId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Closes a tracked open alert and takes its waiting deliveries back with the matching status.
    /// </summary>
    private static void Close(AssistantAlert alert, string status, DateTimeOffset now)
    {
        alert.Status = status;
        alert.ClosedAt = now;
        alert.ClosedMarker = alert.Id;
        alert.ConcurrencyStamp = Guid.NewGuid();
        var deliveryStatus = status == NhAssistantAlertStatuses.Expired
            ? NhAssistantAlertDeliveryStatuses.Expired
            : NhAssistantAlertDeliveryStatuses.Resolved;
        foreach (var delivery in alert.Deliveries.Where(delivery => delivery.Status == NhAssistantAlertDeliveryStatuses.Pending))
        {
            delivery.Status = deliveryStatus;
            delivery.ClosedAt = now;
            ReleaseClaim(delivery, now);
        }
    }

    private static Task<int> TakeBackAsync(NhAssistantDbContext context, Guid alertId, string status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return context.AlertDeliveries
            .Where(delivery => delivery.AlertId == alertId && delivery.Status == NhAssistantAlertDeliveryStatuses.Pending)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(delivery => delivery.Status, status)
                    .SetProperty(delivery => delivery.ClosedAt, now)
                    .SetProperty(delivery => delivery.ClaimedBy, (string?)null)
                    .SetProperty(delivery => delivery.ClaimedUntil, (DateTimeOffset?)null)
                    .SetProperty(delivery => delivery.UpdatedAt, now)
                    .SetProperty(delivery => delivery.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
    }

    private static void ReleaseClaim(AssistantAlertDelivery delivery, DateTimeOffset now)
    {
        delivery.ClaimedBy = null;
        delivery.ClaimedUntil = null;
        delivery.UpdatedAt = now;
        delivery.ConcurrencyStamp = Guid.NewGuid();
    }

    private async Task LockSubjectAsync(NhAssistantDbContext context, NhAiSubjectRef subject, bool shadow, CancellationToken cancellationToken)
    {
        if (!await contextFactory.TransactionLocks.TryAcquireAsync(
            context,
            LockResource(context.Model.GetDefaultSchema(), subject, shadow),
            LockTimeout,
            cancellationToken))
        {
            throw new TimeoutException(
                $"An assistant alert write waited {LockTimeout} for its subject's transaction lock and changed nothing.");
        }
    }

    /// <summary>
    /// The transaction lock of one subject's alerts, live or shadow.
    /// </summary>
    internal static string LockResource(string? schema, NhAiSubjectRef subject, bool shadow)
    {
        return "nhai:alerts:" + Hash([schema, subject.Type, subject.Id, shadow ? "shadow" : "live"]);
    }

    /// <summary>
    /// The identity of an alert: one open alert exists per schema, scope key, kind, subject, key and shadow mark.
    /// </summary>
    internal static string IdentityOf(string? schema, string scopeKey, string kindId, NhAiSubjectRef subject, string key, bool shadow)
    {
        return Hash([schema, scopeKey, kindId, subject.Type, subject.Id, key, shadow ? "shadow" : "live"]);
    }

    private static string Hash(string?[] parts)
    {
        var identity = JsonSerializer.Serialize(parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }
}
