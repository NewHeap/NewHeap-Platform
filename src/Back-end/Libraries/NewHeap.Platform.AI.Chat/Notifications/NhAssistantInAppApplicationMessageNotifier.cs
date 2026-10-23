using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Actors;

namespace NewHeap.Platform.AI.Chat.Notifications;

/// <summary>
/// Who hears that the application posted a message into a conversation, by Web Push and in the
/// application's inbox alike.
/// </summary>
internal static class NhAssistantApplicationMessageRecipients
{
    /// <summary>
    /// The owner and participants of a chat; the participants of an agent thread, which belongs to the
    /// agent's own actor and whose people are those who joined it.
    /// </summary>
    public static IReadOnlyList<string> Of(
        AssistantConversation conversation,
        IEnumerable<AssistantConversationParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(participants);

        var recipients = new List<string>();
        if (string.Equals(conversation.Kind, NhAssistantConversationKinds.Chat, StringComparison.Ordinal))
        {
            recipients.Add(conversation.OwnerActorId);
        }
        recipients.AddRange(participants.Select(participant => participant.ActorId));

        return recipients.Distinct(StringComparer.Ordinal).ToArray();
    }
}

/// <summary>
/// Tells the people of a conversation in the application's own inbox, through the platform's
/// <see cref="INhUserNotificationSink"/>, that the application posted a message into it, next to Web
/// Push. It runs only for an application that turned on in-app agent notifications
/// (<see cref="NhAssistantBuilder.UseInAppAgentNotifications"/>), for a message with
/// <see cref="NhAssistantApplicationMessage.Notify"/> outside a shadow thread. The in-app message has
/// its own category (<see cref="Category"/>), one group per conversation, the conversation's title and
/// a fixed text in the recipient's language from <see cref="INhActorDirectory"/>, otherwise the
/// current UI culture; never the message itself. Recipients whose actor id is not a user id are
/// skipped. It never throws: a failed notification does not fail the post.
/// </summary>
internal sealed class NhAssistantInAppApplicationMessageNotifier(
    INhAssistantStore store,
    IServiceProvider services,
    ILogger<NhAssistantInAppApplicationMessageNotifier> logger)
{
    internal const string Category = "assistant-application";

    public async Task DeliverAsync(AssistantConversation conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var sink = services.GetService<INhUserNotificationSink>();
        if (sink is null)
        {
            logger.LogWarning(
                "In-app agent notifications are enabled, but no notification sink is registered; register the NewHeap notifications module or an INhUserNotificationSink.");
            return;
        }

        try
        {
            var participants = await store.GetParticipantsAsync(conversation.Id, cancellationToken);
            var recipients = new List<NhActorRef>();
            foreach (var actorId in NhAssistantApplicationMessageRecipients.Of(conversation, participants))
            {
                if (Guid.TryParse(actorId, out var userId))
                {
                    recipients.Add(NhActorRef.ForUser(userId));
                }
            }
            if (recipients.Count == 0)
            {
                return;
            }

            var cultures = await NhAssistantNotificationCultures.GetAsync(services, recipients, cancellationToken);
            foreach (var recipient in recipients)
            {
                await SendAsync(sink, conversation, recipient, cultures.GetValueOrDefault(recipient), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "In-app notifications of an application message in conversation {ConversationId} were not delivered ({ExceptionType}).",
                conversation.Id,
                exception.GetType().Name);
        }
    }

    private async Task SendAsync(
        INhUserNotificationSink sink,
        AssistantConversation conversation,
        NhActorRef recipient,
        string? culture,
        CancellationToken cancellationToken)
    {
        recipient.TryGetUserId(out var userId);
        var (title, body) = NhAssistantAgentNotificationTexts.ForApplicationMessage(
            culture ?? CultureInfo.CurrentUICulture.Name,
            conversation.Title);
        try
        {
            var result = await sink.SendAsync(
                new NhUserNotificationRequest(userId, title, body)
                {
                    Category = Category,
                    Level = NhUserNotificationLevel.Information,
                    // One thread per conversation, so repeated messages of the application stay together.
                    GroupKey = $"{Category}:{conversation.Id:N}"
                },
                cancellationToken);
            if (!result.Success)
            {
                logger.LogWarning(
                    "The in-app notification of an application message in conversation {ConversationId} was refused by the notification sink.",
                    conversation.Id);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "The in-app notification of an application message in conversation {ConversationId} was not delivered ({ExceptionType}).",
                conversation.Id,
                exception.GetType().Name);
        }
    }
}
