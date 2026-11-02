namespace NewHeap.Platform.AI.Chat.Notifications;

/// <summary>
/// How a turn ended, for notifying people who are not looking at the conversation.
/// </summary>
/// <param name="ActorId">The person whose turn it was.</param>
/// <param name="Status">One of <see cref="NhAssistantTurnStatuses"/>.</param>
internal sealed record NhAssistantTurnOutcome(
    Guid ConversationId,
    Guid TurnId,
    string ActorId,
    string Status,
    TimeSpan Duration)
{
    /// <summary>
    /// The person the question asks when the turn waits for a question (LS21), who may be someone else
    /// than <see cref="ActorId"/> in a shared chat; <see langword="null"/> when the turn waits for an
    /// approval or does not wait.
    /// </summary>
    public string? QuestionAddresseeActorId { get; init; }
}

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

    /// <summary>
    /// Sends one notification of alerts to one person's browsers (FB87): a single alert or a digest. The
    /// person's checks, choices and global push switch were applied before; a person who turned push off
    /// has no browser to send to.
    /// </summary>
    Task<NhAssistantAlertChannelOutcome> AlertAsync(
        NhAssistantAlertPush alert,
        CancellationToken cancellationToken);
}

/// <summary>
/// What became of one notification of alerts on one channel.
/// </summary>
internal enum NhAssistantAlertChannelOutcome
{
    /// <summary>The channel had nothing to deliver to, such as a person without a browser for push.</summary>
    Skipped = 0,

    /// <summary>The channel delivered it.</summary>
    Delivered = 1,

    /// <summary>The channel tried and failed.</summary>
    Failed = 2
}

/// <summary>
/// One Web Push notification of alerts to one person: a single alert (<see cref="DigestId"/> is
/// <see langword="null"/>) or a digest.
/// </summary>
/// <param name="Texts">Writes the title and body for a browser in its language: the person's own culture
/// when the application knows it, otherwise the browser's. The body never carries an alert's text unless
/// its kind shows it on a lock screen.</param>
internal sealed record NhAssistantAlertPush(
    string RecipientActorId,
    Guid? AlertId,
    Guid? DigestId,
    bool Urgent,
    Func<string, (string Title, string Body)> Texts);

internal sealed class NhAssistantNoNotifier : INhAssistantNotifier
{
    public Task<NhAssistantAlertChannelOutcome> AlertAsync(
        NhAssistantAlertPush alert,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(NhAssistantAlertChannelOutcome.Skipped);
    }

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
