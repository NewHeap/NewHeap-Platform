namespace NewHeap.Platform.AI.Chat.Notifications;

/// <summary>
/// How a turn ended, for notifying people who are not looking at the conversation.
/// </summary>
/// <param name="Status">One of <see cref="NhAssistantTurnStatuses"/>.</param>
internal sealed record NhAssistantTurnOutcome(
    Guid ConversationId,
    Guid TurnId,
    string ActorId,
    string Status,
    TimeSpan Duration);

/// <summary>
/// Notifies the owner and participants of a conversation outside the application, such as through
/// Web Push. Implementations never throw: a failed notification must not fail a turn or a request.
/// Without the ASP.NET package nothing is sent.
/// </summary>
internal interface INhAssistantNotifier
{
    Task TurnEndedAsync(NhAssistantTurnOutcome outcome, CancellationToken cancellationToken);

    Task InvitedAsync(
        Guid conversationId,
        string inviteeActorId,
        string inviterDisplayName,
        CancellationToken cancellationToken);
}

internal sealed class NhAssistantNoNotifier : INhAssistantNotifier
{
    public Task TurnEndedAsync(NhAssistantTurnOutcome outcome, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task InvitedAsync(
        Guid conversationId,
        string inviteeActorId,
        string inviterDisplayName,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
