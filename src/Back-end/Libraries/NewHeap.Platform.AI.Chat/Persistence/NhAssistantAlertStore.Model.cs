using Microsoft.EntityFrameworkCore;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The reads of the platform tool through which an agent's model raises alerts (FB87-9).
/// </summary>
internal sealed partial class NhAssistantAlertStore
{
    /// <summary>
    /// How many alerts <paramref name="runId"/> raised or last updated, live or shadow as
    /// <paramref name="shadow"/> says: what <see cref="NhAssistantLimits.MaxAlertsPerRun"/> bounds for a
    /// run's model. An alert raised again counts once.
    /// </summary>
    public async Task<int> CountForRunAsync(Guid runId, bool shadow, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Alerts
            .AsNoTracking()
            .CountAsync(alert => alert.RunId == runId && alert.Shadow == shadow, cancellationToken);
    }
}
