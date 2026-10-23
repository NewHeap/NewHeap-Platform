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

    /// <summary>
    /// Tells the recipients about an agent event in a thread.
    /// </summary>
    Task AgentNotificationAsync(
        NhAssistantAgentNotificationContext context,
        IReadOnlyList<string> recipientActorIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Tells the people of a conversation that the application posted a message into it: the owner and
    /// participants of a chat, the participants of an agent thread. The notification never carries
    /// the message.
    /// </summary>
    Task ApplicationMessageAsync(
        Guid conversationId,
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

    public Task AgentNotificationAsync(
        NhAssistantAgentNotificationContext context,
        IReadOnlyList<string> recipientActorIds,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task ApplicationMessageAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
