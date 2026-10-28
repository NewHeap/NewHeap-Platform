using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Collaboration;

/// <summary>
/// Posts the application's messages into conversations (<see cref="INhAssistantConversationMessages"/>):
/// checks the message and the conversation, stores the message with the next sequence number under
/// the <see cref="NhAssistantMessageRoles.Application"/> role, shows it to the conversation's people
/// through the live updates, notifies them when the application asks for it (by Web Push and, when the
/// application turned on in-app notifications, in its inbox) and records a content-free audit event.
/// Nothing here logs the message.
/// </summary>
internal sealed class NhAssistantConversationMessages(
    INhAssistantStore store,
    NhAssistantRunStore runs,
    NhAssistantThreadService threads,
    NhAssistantLivePublisher live,
    INhAssistantNotifier notifier,
    NhAssistantInAppApplicationMessageNotifier inAppNotifier,
    NhAssistantRegistrationState registration,
    NhAssistantTenancy tenancy,
    IOptionsMonitor<NhAssistantOptions> options,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks,
    ILogger<NhAssistantConversationMessages> logger) : INhAssistantConversationMessages
{
    /// <summary>
    /// The client message id under which a message with an idempotency key is stored.
    /// </summary>
    internal const string IdempotencyPrefix = "application:";

    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _auditSinks = auditSinks.ToArray();

    public async Task<TaskResult<NhAssistantApplicationMessageReceipt>> PostAsync(
        Guid conversationId,
        NhAssistantApplicationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!options.CurrentValue.Enabled)
        {
            return Failed(NhAssistantErrorCodes.Disabled, "The assistant is disabled.");
        }
        if (Validate(message) is { } invalid)
        {
            return invalid;
        }

        var conversation = await store.FindShareableConversationAsync(conversationId, cancellationToken);
        var thread = conversation is not null && string.Equals(conversation.Kind, NhAssistantConversationKinds.Thread, StringComparison.Ordinal)
            ? await runs.FindThreadByConversationAsync(conversation.Id, cancellationToken)
            : null;
        if (conversation is null
            || (message.Access.Person is { } person && !await MayReadAsync(conversation, thread, person, cancellationToken)))
        {
            // Nothing is stored, shown, notified or audited for a conversation the person may not read.
            return Failed(NhAssistantErrorCodes.ConversationNotFound, "The conversation does not exist or is archived.");
        }

        var clientMessageId = string.IsNullOrWhiteSpace(message.IdempotencyKey)
            ? null
            : IdempotencyPrefix + message.IdempotencyKey.Trim();
        var text = message.Text.Trim();
        // An untrusted message stores no flag, as before trusted messages existed.
        var details = new NhAssistantStoredPart(
            NhAssistantStoredPart.ApplicationType,
            Title: Optional(message.Title),
            LinkUrl: message.Link?.Url.Trim(),
            LinkLabel: Optional(message.Link?.Label),
            Sender: Optional(message.SenderName),
            Trusted: message.Trusted ? true : null);
        var stored = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Role = NhAssistantMessageRoles.Application,
            AuthorActorId = NhAssistantSpeakers.ApplicationActorId,
            PartsJson = NhAssistantContent.SerializeParts([details, new NhAssistantStoredPart(NhAssistantStoredPart.TextType, text)]),
            ClientMessageId = clientMessageId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // The store looks for an earlier message with the key and adds this one in one transaction
        // under the conversation's message lock, so two posts with the same key at the same moment
        // store one message. A repeated post is neither published, notified nor audited again.
        if (await store.AddApplicationMessageAsync(stored, cancellationToken) is { } earlier)
        {
            return TaskResult<NhAssistantApplicationMessageReceipt>.Succeeded(
                new NhAssistantApplicationMessageReceipt(earlier.Id, earlier.Sequence, Duplicate: true));
        }

        // Live updates are best effort: people who are not connected read the message from the snapshot.
        var view = new NhAssistantMessageView(
            stored.Id,
            stored.Role,
            stored.CreatedAt,
            [new NhAssistantTextPartView(text)])
        {
            Sequence = stored.Sequence,
            AuthorActorId = stored.AuthorActorId,
            Application = new NhAssistantApplicationMessageView(details.Title, details.LinkUrl, details.LinkLabel, details.Sender, message.Trusted)
        };
        await live.PublishAsync(
            new NhAssistantLiveMessageCreated(conversation.Id, NhAssistantSpeakers.ApplicationActorId, view),
            CancellationToken.None);
        await live.PublishChangedAsync(conversation.Id, CancellationToken.None);

        // A shadow thread holds work only administrators see; nobody is notified about it.
        var notified = message.Notify && thread?.Shadow != true;
        if (notified)
        {
            await notifier.ApplicationMessageAsync(conversation.Id, CancellationToken.None);
            // Next to Web Push, the application's inbox, when it turned on in-app agent notifications.
            if (registration.InAppNotifications)
            {
                await inAppNotifier.DeliverAsync(conversation, CancellationToken.None);
            }
        }

        await AuditAsync(conversation, thread, stored.Id, notified ? "notified" : "posted", message.Trusted, cancellationToken);
        logger.LogInformation(
            "The application posted message {MessageId} into conversation {ConversationId}.",
            stored.Id,
            conversation.Id);
        return TaskResult<NhAssistantApplicationMessageReceipt>.Succeeded(
            new NhAssistantApplicationMessageReceipt(stored.Id, stored.Sequence, Duplicate: false));
    }

    /// <summary>
    /// Whether <paramref name="person"/> may read the conversation now, which a message
    /// <see cref="NhAssistantApplicationMessageAccess.ReadableBy"/> them needs. A chat they own or take
    /// part in within their tenant. An agent thread by the live read rule, never by a stored participant
    /// row, which can outlive the role that created it: no shadow thread or thread of a shadow variant,
    /// only a thread the person's tenant reaches, and only with a live role on the thread's subject or
    /// an invitation to the thread. Nothing here records the person as a participant.
    /// </summary>
    private async Task<bool> MayReadAsync(
        AssistantConversation conversation,
        AssistantThread? thread,
        NhAssistantConversationPerson person,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(conversation.Kind, NhAssistantConversationKinds.Thread, StringComparison.Ordinal))
        {
            return await store.FindAccessAsync(conversation.Id, person.ActorId, person.TenantId, cancellationToken) is not null;
        }
        if (thread is null || IsShadow(thread) || !tenancy.Reaches(thread, person.TenantId))
        {
            return false;
        }

        return await threads.ReadAccessAsync(thread, person.ActorId, cancellationToken) is not null;
    }

    /// <summary>
    /// Shadow threads, and every thread of a shadow variant, are read by administrators only.
    /// </summary>
    private bool IsShadow(AssistantThread thread)
    {
        return thread.Shadow
            || (registration.AgentRuns.TryGetValue(thread.AgentId, out var options) && options.ShadowOfAgentId is not null);
    }

    /// <summary>
    /// The refusal of a message without an access choice, that is empty or too long, or whose title,
    /// sender, link or idempotency key is not usable; <see langword="null"/> when it may be posted.
    /// </summary>
    private TaskResult<NhAssistantApplicationMessageReceipt>? Validate(NhAssistantApplicationMessage message)
    {
        // The compiler requires the choice; null reaches here only past it, such as through null!.
        if (message.Access is null)
        {
            return Failed(
                NhAssistantErrorCodes.MessageInvalid,
                "An application message needs an access choice: ReadableBy(person) or WithoutPersonCheck().");
        }
        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return Failed(NhAssistantErrorCodes.MessageInvalid, "An application message needs a text.");
        }
        if (message.Text.Trim().Length > registration.Limits.MaxMessageChars)
        {
            return Failed(NhAssistantErrorCodes.MessageTooLong, "The application message is longer than the assistant accepts.");
        }

        var valid = IsLabel(message.Title, NhAssistantApplicationMessage.MaxTitleLength)
            && IsLabel(message.SenderName, NhAssistantApplicationMessage.MaxSenderNameLength)
            && IsLabel(message.IdempotencyKey, NhAssistantApplicationMessage.MaxIdempotencyKeyLength)
            && (message.Link is null
                || (IsLink(message.Link.Url) && IsLabel(message.Link.Label, NhAssistantApplicationMessageLink.MaxLabelLength)));
        return valid
            ? null
            : Failed(NhAssistantErrorCodes.MessageInvalid, "The title, sender, link or idempotency key of the application message is not valid.");
    }

    /// <summary>
    /// Whether an optional single-line value fits: absent or blank, or at most
    /// <paramref name="maximum"/> characters without control characters.
    /// </summary>
    private static bool IsLabel(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximum && !trimmed.Any(char.IsControl);
    }

    /// <summary>
    /// Whether <paramref name="url"/> is a path within the application or an absolute <c>https</c>
    /// address, the links the chat opens, as it does for subject links.
    /// </summary>
    internal static bool IsLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var trimmed = url.Trim();
        if (trimmed.Length > NhAssistantApplicationMessageLink.MaxUrlLength || trimmed.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }
        if (trimmed.StartsWith('/'))
        {
            return !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith("/\\", StringComparison.Ordinal);
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var address)
            && address.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(address.Host);
    }

    private static string? Optional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task AuditAsync(
        AssistantConversation conversation,
        AssistantThread? thread,
        Guid messageId,
        string resultCode,
        bool trusted,
        CancellationToken cancellationToken)
    {
        var evt = new NhAssistantAuditEvent(
            NhAssistantAuditEventKind.ApplicationMessagePosted,
            conversation.Id,
            Guid.Empty,
            NhAssistantSpeakers.ApplicationActorId,
            conversation.AgentId,
            conversation.AgentVersion,
            DateTimeOffset.UtcNow)
        {
            ObjectId = messageId.ToString(),
            ResultCode = resultCode,
            SubjectType = thread?.SubjectType,
            SubjectId = thread?.SubjectId,
            ApplicationMessageTrusted = trusted
        };
        foreach (var sink in _auditSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }

    private static TaskResult<NhAssistantApplicationMessageReceipt> Failed(string code, string message)
    {
        return TaskResult<NhAssistantApplicationMessageReceipt>.Failed(code, message);
    }
}
