namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// One pass of the assistant's run maintenance on demand, the pass the host otherwise runs every
/// <see cref="NhAssistantLimits.MaintenanceInterval"/>: it expires, reminds and escalates proposals,
/// queues approved executions, marks overdue reports and interrupted executions, ends runs whose
/// background operation ended, and in a host with alert kinds closes expired alerts, purges closed ones
/// and sends the deliveries that are due. <c>AddNewHeapAssistant</c> registers it with the ASP.NET
/// integration. Use it in tests and operator tools instead of waiting for the interval: move the
/// application's clock (the registered <see cref="TimeProvider"/>) and run a pass, then assert what it
/// did. A pass works only on what is due on that clock.
/// </summary>
/// <remarks>
/// It is the same pass as the hosted maintenance, in the same instance: passes of one host never
/// overlap, so a pass that the interval started first ends before this one begins, and when
/// <see cref="RunOnceAsync"/> returns, a full pass ran after the call. Several hosts may run passes at
/// the same time, since every change is a compare-and-set update. A failing pass throws to the caller;
/// the hosted maintenance logs its failures and tries again at the next interval.
/// </remarks>
public interface INhAssistantMaintenance
{
    /// <summary>
    /// Runs one maintenance pass now, after a pass that is already running in this host ended.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait and the pass.</param>
    Task RunOnceAsync(CancellationToken cancellationToken = default);
}
