using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Posts the application's own messages into conversations, for example when a long-running export
/// that a person started from the chat has finished. It is a server-side API for the application's
/// code; there is no endpoint through which clients post such messages. A message reaches people's
/// chat conversations and agent threads alike: it gets the next position in the conversation, the
/// people of the conversation see it at once through the live updates, it may notify them, and the
/// audit records it without its content. A later turn gives it to the model as information from the
/// application, never as a person's request or as instructions, unless the application marked it
/// <see cref="NhAssistantApplicationMessage.Trusted"/>.
/// </summary>
public interface INhAssistantConversationMessages
{
    /// <summary>
    /// Posts <paramref name="message"/> into the conversation <paramref name="conversationId"/>.
    /// <para>
    /// The application decides which conversation hears what, and every message says whose access the
    /// post depends on (<see cref="NhAssistantApplicationMessage.Access"/>). When a client named the
    /// conversation, choose <see cref="NhAssistantApplicationMessageAccess.ReadableBy"/> with the
    /// person it is meant for, so the message is posted only when that person may open the
    /// conversation; choose <see cref="NhAssistantApplicationMessageAccess.WithoutPersonCheck"/> only
    /// for a conversation the application chose itself.
    /// </para>
    /// <para>
    /// Fails with <c>assistant-disabled</c> while the assistant is disabled, <c>assistant-agent-work-off</c>
    /// while agent work is off on this host (<see cref="NhAssistantOptions.AgentWork"/>),
    /// <c>assistant-conversation-not-found</c> for an unknown or archived conversation or one that the
    /// person of <see cref="NhAssistantApplicationMessageAccess.ReadableBy"/> may not open,
    /// <c>assistant-message-invalid</c> for a message without an access choice, an empty text or an
    /// invalid title, sender, link or idempotency key, and <c>assistant-message-too-long</c> for a
    /// text longer than
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
/// A message of the application for <see cref="INhAssistantConversationMessages.PostAsync"/>. It
/// always says whose access the post depends on (<see cref="Access"/>).
/// </summary>
/// <param name="Text">What the message says, in Markdown as the chat renders the agent's answers; plain
/// text is Markdown too. Put data people entered, such as a project name, in it with
/// <see cref="NhAssistantMarkdown.Escape"/>, so it shows as it is. At most
/// <see cref="NhAssistantLimits.MaxMessageChars"/> characters.</param>
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
    /// it and, when the application turned on in-app agent notifications
    /// (<see cref="NhAssistantBuilder.UseInAppAgentNotifications"/>), in the application's inbox, each
    /// with a short text that never carries the message. In a chat conversation they are its owner and
    /// participants; in an agent thread the people who joined it. A shadow thread never notifies
    /// anyone.
    /// </summary>
    public bool Notify { get; init; }

    /// <summary>
    /// Makes the post safe to repeat, for example from a retried background step: a conversation
    /// keeps one message per key. Without a key every post adds a message.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Whose access the post depends on, a choice every message makes:
    /// <see cref="NhAssistantApplicationMessageAccess.ReadableBy"/> the person a client named the
    /// conversation for, or explicitly <see cref="NhAssistantApplicationMessageAccess.WithoutPersonCheck"/>.
    /// A message without one is <c>assistant-message-invalid</c>.
    /// </summary>
    public required NhAssistantApplicationMessageAccess Access { get; init; }

    /// <summary>
    /// Whether the model follows the message as the application's instruction. Off by default: a
    /// later turn gives the message to the model as information from the application, never as
    /// instructions. Turn it on only for text the application wrote itself. Never put user-generated
    /// data, such as names, descriptions or comments people entered, into a trusted message, not even
    /// escaped with <see cref="NhAssistantMarkdown.Escape"/>: the model would follow instructions
    /// hidden in it. People see a trusted message as any other application message; the audit records
    /// that it was trusted.
    /// </summary>
    public bool Trusted { get; init; }
}

/// <summary>
/// Whose access an application message depends on (<see cref="NhAssistantApplicationMessage.Access"/>):
/// <see cref="ReadableBy"/> a person, or explicitly <see cref="WithoutPersonCheck"/>. Every post makes
/// this choice, so no message reaches a conversation that a client named without checking that the
/// person may open it.
/// </summary>
public sealed record NhAssistantApplicationMessageAccess
{
    private static readonly NhAssistantApplicationMessageAccess Unchecked = new(person: null);

    private NhAssistantApplicationMessageAccess(NhAssistantConversationPerson? person)
    {
        Person = person;
    }

    /// <summary>
    /// The person who has to be able to open the conversation; <see langword="null"/> for
    /// <see cref="WithoutPersonCheck"/>.
    /// </summary>
    public NhAssistantConversationPerson? Person { get; }

    /// <summary>
    /// Posts only when <paramref name="person"/> may open the conversation now: a chat as its owner or
    /// as a participant of the same tenant; an agent thread by the live read rule, so with a live role
    /// on the thread's subject or an invitation to the thread, never through a participant row left
    /// from an earlier role, never in a shadow thread or a thread of a shadow variant, and only in the
    /// person's tenant or the whole application they take part in. Any other conversation is
    /// <c>assistant-conversation-not-found</c>, and nothing is stored, shown or notified. Choose it
    /// whenever a client named the conversation.
    /// </summary>
    public static NhAssistantApplicationMessageAccess ReadableBy(NhAssistantConversationPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentException.ThrowIfNullOrWhiteSpace(person.ActorId, nameof(person));

        return new NhAssistantApplicationMessageAccess(person);
    }

    /// <summary>
    /// Posts into any conversation that is not archived, without checking a person's access. Choose
    /// it only for a conversation the application chose itself, such as the thread of an agent it
    /// runs, never for a conversation id a client sent.
    /// </summary>
    public static NhAssistantApplicationMessageAccess WithoutPersonCheck()
    {
        return Unchecked;
    }
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
