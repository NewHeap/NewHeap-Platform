using Microsoft.EntityFrameworkCore;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The numbers of alerts per kind for the administration (FB87 phase 2): of the alerts raised within a
/// period, how many there were and what became of their deliveries, and per agent how many shadow alerts
/// would have been sent. Every query filters and groups on the server with provider-neutral LINQ, the
/// same on SQL Server and PostgreSQL, and returns one count per group: a bounded load whatever the number
/// of alerts. Closed alerts are purged after <see cref="NhAssistantLimits.AlertRetention"/>, so the numbers
/// cover what the store still keeps.
/// </summary>
internal sealed class NhAssistantAlertNumbersStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// The numbers of the kinds <paramref name="kindIds"/> names, of the alerts raised from
    /// <paramref name="from"/> up to <paramref name="to"/>.
    /// </summary>
    public async Task<NhAssistantAlertKindAggregates> GetAsync(
        IReadOnlyCollection<string> kindIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kindIds);
        if (kindIds.Count == 0)
        {
            return NhAssistantAlertKindAggregates.None;
        }

        var ids = kindIds.Distinct(StringComparer.Ordinal).ToList();
        await using var context = contextFactory.CreateDbContext();
        var alerts = context.Alerts
            .AsNoTracking()
            .Where(alert => ids.Contains(alert.KindId) && alert.RaisedAt >= from && alert.RaisedAt < to);

        var raised = await alerts
            .Where(alert => !alert.Shadow)
            .GroupBy(alert => alert.KindId)
            .Select(group => new NhAssistantAlertKindCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        // A shadow alert reaches nobody; each one is an alert the agent would have sent.
        var shadow = await alerts
            .Where(alert => alert.Shadow)
            .GroupBy(alert => new { alert.KindId, alert.AgentId })
            .Select(group => new NhAssistantAlertShadowCount(group.Key.KindId, group.Key.AgentId, group.Count()))
            .ToListAsync(cancellationToken);

        // A dropped delivery never reached its person: they no longer qualified.
        var deliveries = await context.AlertDeliveries
            .AsNoTracking()
            .Join(
                alerts.Where(alert => !alert.Shadow),
                delivery => delivery.AlertId,
                alert => alert.Id,
                (delivery, alert) => new
                {
                    alert.KindId,
                    AlertStatus = alert.Status,
                    alert.ClosedAt,
                    delivery.Status,
                    delivery.ReasonCode,
                    delivery.SentAt,
                    delivery.ReadAt,
                    delivery.DismissedAt
                })
            .Where(row => row.Status != NhAssistantAlertDeliveryStatuses.Dropped)
            .GroupBy(row => new
            {
                row.KindId,
                Delivered = row.SentAt != null,
                Read = row.ReadAt != null,
                Dismissed = row.DismissedAt != null,
                Withheld = row.Status == NhAssistantAlertDeliveryStatuses.Withheld,
                Muted = row.Status == NhAssistantAlertDeliveryStatuses.Withheld
                    && row.ReasonCode == NhAssistantAlertWithholdReasons.Muted,
                ResolvedBeforeReading = row.AlertStatus == NhAssistantAlertStatuses.Resolved
                    && (row.ReadAt == null || row.ReadAt > row.ClosedAt)
            })
            .Select(group => new NhAssistantAlertDeliveryCount(
                group.Key.KindId,
                group.Key.Delivered,
                group.Key.Read,
                group.Key.Dismissed,
                group.Key.Withheld,
                group.Key.Muted,
                group.Key.ResolvedBeforeReading,
                group.Count()))
            .ToListAsync(cancellationToken);

        return new NhAssistantAlertKindAggregates(raised, deliveries, shadow);
    }
}

/// <summary>How many live alerts of a kind were raised.</summary>
internal sealed record NhAssistantAlertKindCount(string KindId, int Count);

/// <summary>How many shadow alerts of a kind an agent raised; <see cref="AgentId"/> is never empty for a shadow alert.</summary>
internal sealed record NhAssistantAlertShadowCount(string KindId, string? AgentId, int Count);

/// <summary>
/// How many deliveries of a kind's live alerts share one combination of outcomes.
/// </summary>
/// <param name="Delivered">The delivery went out to the person's channels.</param>
/// <param name="Read">The person read the alert.</param>
/// <param name="Dismissed">The person dismissed the alert.</param>
/// <param name="Withheld">The delivery was due and withheld, for any reason.</param>
/// <param name="Muted">The delivery was withheld because the person muted the kind.</param>
/// <param name="ResolvedBeforeReading">The alert was resolved before the person read it.</param>
internal sealed record NhAssistantAlertDeliveryCount(
    string KindId,
    bool Delivered,
    bool Read,
    bool Dismissed,
    bool Withheld,
    bool Muted,
    bool ResolvedBeforeReading,
    int Count);

internal sealed record NhAssistantAlertKindAggregates(
    IReadOnlyList<NhAssistantAlertKindCount> Raised,
    IReadOnlyList<NhAssistantAlertDeliveryCount> Deliveries,
    IReadOnlyList<NhAssistantAlertShadowCount> Shadow)
{
    public static NhAssistantAlertKindAggregates None { get; } = new([], [], []);
}
