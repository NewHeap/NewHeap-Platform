using Microsoft.EntityFrameworkCore;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The reads of the platform tool through which an agent's model raises alerts (FB87-9).
/// </summary>
internal sealed partial class NhAssistantAlertStore
{
    /// <summary>
    /// How many other alerts than the one of <paramref name="kindId"/>, <paramref name="subject"/> and
    /// <paramref name="key"/> the run <paramref name="runId"/> raised or last updated, live or shadow as
    /// <paramref name="shadow"/> says: what <see cref="NhAssistantLimits.MaxAlertsPerRun"/> bounds for a
    /// run's model before it raises that alert. An alert raised again counts once, so raising one the run
    /// already raised is never over the limit.
    /// </summary>
    public async Task<int> CountOthersForRunAsync(
        Guid runId,
        bool shadow,
        string kindId,
        NhAiSubjectRef subject,
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var context = contextFactory.CreateDbContext();
        return await context.Alerts
            .AsNoTracking()
            .Where(alert => alert.RunId == runId && alert.Shadow == shadow)
            .Where(alert => !(alert.KindId == kindId
                && alert.SubjectType == subject.Type
                && alert.SubjectId == subject.Id
                && alert.Key == key))
            .CountAsync(cancellationToken);
    }
}
