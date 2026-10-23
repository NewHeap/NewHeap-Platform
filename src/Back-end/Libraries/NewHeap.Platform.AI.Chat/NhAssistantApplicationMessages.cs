using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Posts the application's own messages into conversations, for example when a long-running export
/// that a person started from the chat has finished. It is a server-side API for the application's
/// code; there is no endpoint through which clients post such messages. A message reaches people's
/// chat conversations and agent threads alike: it gets the next position in the conversation, the
/// people of the conversation see it at once through the live updates, it may notify them, and the
/// audit records it without its content. A later turn gives it to the model as information from the
/// application, never as a person's request or as instructions.
/// </summary>
public interface INhAssistantConversationMessages
{
    /// <summary>
    /// Posts <paramref name="message"/> into the conversation <paramref name="conversationId"/>.
    /// <para>
    /// The application decides which conversation hears what. When a client named the conversation,
    /// set <see cref="NhAssistantApplicationMessage.ReadableBy"/> to the person it is meant for, so the
    /// message is posted only when that person may open the conversation.
    /// </para>
    /// <para>
    /// Fails with <c>assistant-disabled</c> while the assistant is disabled,
    /// <c>assistant-conversation-not-found</c> for an unknown or archived conversation or one that
    /// <see cref="NhAssistantApplicationMessage.ReadableBy"/> may not open,
    /// <c>assistant-message-invalid</c> for an empty text or an invalid title, sender, link or
    /// idempotency key, and <c>assistant-message-too-long</c> for a text longer than
    /// <see cref="NhAssistantLimits.MaxMessageChars"/>. With an
    /// <see cref="NhAssistantApplicationMessage.IdempotencyKey"/> that the conversation already
    /// received, it posts nothing and returns the earlier message with
    /// <see cref="NhAssistantApplicationMessageReceipt.Duplicate"/>.
    /// </para>
    /// </summary>
    Task<TaskResult<NhAssistantApplicationMessageReceipt>> PostAsync(
        Guid conversationId,
        NhAssistantApplicationMessage message,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A message of the application for <see cref="INhAssistantConversationMessages.PostAsync"/>.
/// </summary>
/// <param name="Text">What the message says, in Markdown as the chat renders the agent's answers; plain
/// text is Markdown too. At most <see cref="NhAssistantLimits.MaxMessageChars"/> characters.</param>
public sealed record NhAssistantApplicationMessage(string Text)
{
    /// <summary>
    /// The most characters of <see cref="Title"/>.
    /// </summary>
    public const int MaxTitleLength = 200;

    /// <summary>
    /// The most characters of <see cref="SenderName"/>.
    /// </summary>
    public const int MaxSenderNameLength = 100;

    /// <summary>
    /// The most characters of <see cref="IdempotencyKey"/>.
    /// </summary>
    public const int MaxIdempotencyKeyLength = 100;

    /// <summary>
    /// A short heading people see above the text, such as <c>Export finished</c>.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Who in the application posts, such as <c>Exports</c>; people see the message as the
    /// application's either way.
    /// </summary>
    public string? SenderName { get; init; }

    /// <summary>
    /// Where people continue, such as the finished export.
    /// </summary>
    public NhAssistantApplicationMessageLink? Link { get; init; }

    /// <summary>
    /// Whether the people of the conversation are notified as well: by Web Push when the server offers
    /// it, with a short text that never carries the message. In a chat conversation they are its owner
    /// and participants; in an agent thread the people who joined it. A shadow thread never notifies
    /// anyone.
    /// </summary>
    public bool Notify { get; init; }

    /// <summary>
    /// Makes the post safe to repeat, for example from a retried background step: a conversation
    /// keeps one message per key. Without a key every post adds a message.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// The person the message is meant for, when a client named the conversation: the message is
    /// posted only when this person may open the conversation, as its owner or as a participant of
    /// the same tenant. <see langword="null"/> posts into any conversation that is not archived.
    /// </summary>
    public NhAssistantConversationPerson? ReadableBy { get; init; }
}

/// <summary>
/// A link of an application message: a path within the application, such as
/// <c>/exports/42</c>, which the chat opens through the application's router, or an absolute
/// <c>https</c> address.
/// </summary>
/// <param name="Url">The path or address, at most <see cref="MaxUrlLength"/> characters.</param>
/// <param name="Label">The text of the link, at most <see cref="MaxLabelLength"/> characters; the chat
/// shows its own text without one.</param>
public sealed record NhAssistantApplicationMessageLink(string Url, string? Label = null)
{
    public const int MaxUrlLength = 2_048;

    public const int MaxLabelLength = 100;
}

/// <summary>
/// A person of the application as the assistant knows them: the actor id and tenant the assistant's
/// context resolver gives the person's requests.
/// </summary>
public sealed record NhAssistantConversationPerson(string ActorId, string? TenantId = null);

/// <summary>
/// The message <see cref="INhAssistantConversationMessages.PostAsync"/> posted, or the one it posted
/// earlier with the same idempotency key.
/// </summary>
/// <param name="MessageId">The message.</param>
/// <param name="Sequence">Its position in the conversation.</param>
/// <param name="Duplicate">The conversation already had a message with this idempotency key; nothing
/// was posted, notified or audited again.</param>
public sealed record NhAssistantApplicationMessageReceipt(Guid MessageId, int Sequence, bool Duplicate);
