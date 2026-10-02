namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// A person the owner shared a conversation with. The owner itself is never a participant row;
/// <see cref="AssistantConversation.OwnerActorId"/> stays the single owner.
/// </summary>
public sealed class AssistantConversationParticipant
{
    public Guid ConversationId { get; set; }

    public string ActorId { get; set; } = string.Empty;

    /// <summary>
    /// Display name shown to the other people in the conversation, refreshed whenever the participant
    /// uses the conversation.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// One of the values in <see cref="NhAssistantParticipantSources"/>.
    /// </summary>
    public string JoinedVia { get; set; } = NhAssistantParticipantSources.Link;

    /// <summary>
    /// The owner who invited the participant directly; <see langword="null"/> for a join through a link.
    /// </summary>
    public string? InvitedByActorId { get; set; }

    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>
    /// Highest message sequence the participant has read.
    /// </summary>
    public int LastReadSequence { get; set; }
}

/// <summary>
/// One browser push subscription of an actor. The endpoint is unique: a browser that subscribes
/// again for another signed-in actor moves the subscription to that actor.
/// </summary>
public sealed class AssistantPushSubscription
{
    public Guid Id { get; set; }

    public string ActorId { get; set; } = string.Empty;

    /// <summary>
    /// Lowercase hexadecimal SHA-256 of <see cref="Endpoint"/>; unique.
    /// </summary>
    public string EndpointHash { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The browser's P-256 public key for message encryption, base64url.
    /// </summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>
    /// The browser's authentication secret for message encryption, base64url.
    /// </summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>
    /// Language of the notification texts for this browser: <c>en</c> or <c>nl</c>.
    /// </summary>
    public string Language { get; set; } = "en";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// An actor's notification choice. Without a row, push notifications are on.
/// </summary>
public sealed class AssistantNotificationSetting
{
    public string ActorId { get; set; } = string.Empty;

    public bool PushEnabled { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }
}
