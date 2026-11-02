using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// How a person sees their delivery of an alert in the alert list: exactly one state per delivery. A
/// dismissal wins, then a closed alert, then whether the person read the open alert.
/// </summary>
internal enum NhAssistantAlertReadingState
{
    /// <summary>The alert is open and the person has not read it: it waits, or went out.</summary>
    Unread = 0,

    /// <summary>The alert is open and the person read it.</summary>
    Read = 1,

    /// <summary>The alert was resolved; the person did not dismiss it.</summary>
    Resolved = 2,

    /// <summary>The alert's expiry passed, also before the maintenance closed it; the person did not dismiss it.</summary>
    Expired = 3,

    /// <summary>The person dismissed the alert.</summary>
    Dismissed = 4
}

/// <summary>
/// What a person's alert list selects, translated to SQL on both providers: the states, and optionally
/// kinds, one subject and urgencies. <see cref="Now"/> is the application's clock, which decides whether
/// an open alert's expiry passed.
/// </summary>
internal sealed record NhAssistantAlertListFilter(
    IReadOnlySet<NhAssistantAlertReadingState> States,
    DateTimeOffset Now)
{
    public IReadOnlyList<string> KindIds { get; init; } = [];

    public string? SubjectType { get; init; }

    public string? SubjectId { get; init; }

    public IReadOnlyList<NhAssistantAlertUrgency> Urgencies { get; init; } = [];
}

/// <summary>
/// The reads of the alert list (FB87): a person's own deliveries with their alerts, selected and ordered
/// in the database. The recipient check runs on what comes back, so a person who lost access to a
/// subject never reads its alerts' text.
/// </summary>
internal sealed partial class NhAssistantAlertStore
{
    /// <summary>
    /// The person's deliveries that <paramref name="filter"/> selects, with their alerts, the most recently
    /// raised alert first, at most <paramref name="limit"/>. Dropped deliveries and shadow alerts never
    /// appear.
    /// </summary>
    public async Task<IReadOnlyList<AssistantAlertDelivery>> ListForRecipientAsync(
        string recipientActorId,
        NhAssistantAlertListFilter filter,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientActorId);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.States.Count == 0)
        {
            return [];
        }

        await using var context = contextFactory.CreateDbContext();
        var query = context.AlertDeliveries
            .AsNoTracking()
            .Include(delivery => delivery.Alert)
            .Where(delivery => delivery.RecipientActorId == recipientActorId
                && delivery.Status != NhAssistantAlertDeliveryStatuses.Dropped
                && !delivery.Alert!.Shadow);
        query = WhereIn(query, filter);

        if (filter.KindIds.Count > 0)
        {
            var kindIds = filter.KindIds.ToArray();
            query = query.Where(delivery => kindIds.Contains(delivery.Alert!.KindId));
        }
        if (filter.SubjectType is { } subjectType)
        {
            query = query.Where(delivery => delivery.Alert!.SubjectType == subjectType);
        }
        if (filter.SubjectId is { } subjectId)
        {
            query = query.Where(delivery => delivery.Alert!.SubjectId == subjectId);
        }
        if (filter.Urgencies.Count > 0)
        {
            var urgencies = filter.Urgencies.ToArray();
            query = query.Where(delivery => urgencies.Contains(delivery.Alert!.Urgency));
        }

        return await query
            .OrderByDescending(delivery => delivery.Alert!.UpdatedAt)
            .ThenBy(delivery => delivery.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Dismisses the person's delivery from their alert list once: a delivery that waits, went out or
    /// was read, and also one that a resolved or expired alert took back, so the person can clear a
    /// closed alert from the list. Returns <see langword="true"/> only for the request that dismissed it;
    /// a delivery dismissed before, a dropped one and one that does not exist return
    /// <see langword="false"/>, so the dismissal is audited once.
    /// </summary>
    public async Task<bool> DismissOnceAsync(Guid alertId, string recipientActorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var changed = await context.AlertDeliveries
            .Where(delivery => delivery.AlertId == alertId
                && delivery.RecipientActorId == recipientActorId
                && (delivery.Status == NhAssistantAlertDeliveryStatuses.Pending
                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Sent
                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Read
                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Resolved
                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Expired))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(delivery => delivery.Status, NhAssistantAlertDeliveryStatuses.Dismissed)
                    .SetProperty(delivery => delivery.DismissedAt, now)
                    .SetProperty(delivery => delivery.ClaimedBy, (string?)null)
                    .SetProperty(delivery => delivery.ClaimedUntil, (DateTimeOffset?)null)
                    .SetProperty(delivery => delivery.UpdatedAt, now)
                    .SetProperty(delivery => delivery.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return changed == 1;
    }

    /// <summary>
    /// The state of one delivery as the person sees it, on the application's clock; the same rule
    /// <see cref="ListForRecipientAsync(string, NhAssistantAlertListFilter, int, CancellationToken)"/>
    /// selects by.
    /// </summary>
    public static NhAssistantAlertReadingState StateOf(AssistantAlertDelivery delivery, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var alert = delivery.Alert ?? throw new ArgumentException("The delivery carries no alert.", nameof(delivery));
        if (delivery.Status == NhAssistantAlertDeliveryStatuses.Dismissed)
        {
            return NhAssistantAlertReadingState.Dismissed;
        }
        if (alert.Status == NhAssistantAlertStatuses.Resolved)
        {
            return NhAssistantAlertReadingState.Resolved;
        }
        if (alert.Status == NhAssistantAlertStatuses.Expired || (alert.ExpiresAt is { } expiresAt && expiresAt <= now))
        {
            return NhAssistantAlertReadingState.Expired;
        }

        return delivery.Status == NhAssistantAlertDeliveryStatuses.Read
            ? NhAssistantAlertReadingState.Read
            : NhAssistantAlertReadingState.Unread;
    }

    /// <summary>
    /// Keeps the deliveries in one of the filter's states, by the rule of <see cref="StateOf"/>. Every
    /// state is a captured flag, so the providers translate one predicate whatever the filter asks.
    /// </summary>
    private static IQueryable<AssistantAlertDelivery> WhereIn(IQueryable<AssistantAlertDelivery> query, NhAssistantAlertListFilter filter)
    {
        var now = filter.Now;
        var unread = filter.States.Contains(NhAssistantAlertReadingState.Unread);
        var read = filter.States.Contains(NhAssistantAlertReadingState.Read);
        var resolved = filter.States.Contains(NhAssistantAlertReadingState.Resolved);
        var expired = filter.States.Contains(NhAssistantAlertReadingState.Expired);
        var dismissed = filter.States.Contains(NhAssistantAlertReadingState.Dismissed);

        return query.Where(delivery =>
            (dismissed && delivery.Status == NhAssistantAlertDeliveryStatuses.Dismissed)
            || (delivery.Status != NhAssistantAlertDeliveryStatuses.Dismissed
                && ((resolved && delivery.Alert!.Status == NhAssistantAlertStatuses.Resolved)
                    || (expired
                        && (delivery.Alert!.Status == NhAssistantAlertStatuses.Expired
                            || (delivery.Alert.Status == NhAssistantAlertStatuses.Open
                                && delivery.Alert.ExpiresAt != null
                                && delivery.Alert.ExpiresAt <= now)))
                    || (delivery.Alert!.Status == NhAssistantAlertStatuses.Open
                        && (delivery.Alert.ExpiresAt == null || delivery.Alert.ExpiresAt > now)
                        && ((unread
                                && (delivery.Status == NhAssistantAlertDeliveryStatuses.Pending
                                    || delivery.Status == NhAssistantAlertDeliveryStatuses.Sent))
                            || (read && delivery.Status == NhAssistantAlertDeliveryStatuses.Read))))));
    }
}
