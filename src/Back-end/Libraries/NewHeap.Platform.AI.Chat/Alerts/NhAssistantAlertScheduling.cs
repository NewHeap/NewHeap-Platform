namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// The moment one person's delivery of an alert is planned for.
/// </summary>
/// <param name="Kind">The alert's kind, with its declared delivery (<see cref="NhAssistantAlertKind.Delivery"/>).</param>
/// <param name="Urgency">The alert's urgency now.</param>
/// <param name="RecipientActorId">The person.</param>
/// <param name="Now">The moment the alert was raised or delivered again, on the application's clock.</param>
internal sealed record NhAssistantAlertScheduleRequest(
    NhAssistantAlertKind Kind,
    NhAssistantAlertUrgency Urgency,
    string RecipientActorId,
    DateTimeOffset Now);

/// <summary>
/// Plans when a delivery of an alert falls due: the seam the assistant's delivery replaces with the
/// daily digest, the person's time zone and choices. It is asked once per person whenever a delivery is
/// planned or planned again, before the alert's store writes it, and only for alerts that reach people.
/// </summary>
internal interface INhAssistantAlertScheduler
{
    /// <summary>
    /// When the delivery of <paramref name="request"/> falls due, on the application's clock; never
    /// before <see cref="NhAssistantAlertScheduleRequest.Now"/>.
    /// </summary>
    ValueTask<DateTimeOffset> DueAtAsync(NhAssistantAlertScheduleRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The default plan: every delivery falls due at once, as for a kind delivered immediately.
/// </summary>
internal sealed class NhAssistantImmediateAlertScheduler : INhAssistantAlertScheduler
{
    public ValueTask<DateTimeOffset> DueAtAsync(NhAssistantAlertScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(request.Now);
    }
}
