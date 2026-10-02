using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet.Notifications;

/// <summary>
/// Sends Web Push notifications to the browsers of people who belong to a conversation. It runs after
/// the final event of a turn was written, sends to every browser in parallel within
/// <see cref="NhAssistantPushOptions.DeliveryTimeout"/> and removes subscriptions the push service
/// reports as gone. Notifications carry the conversation id, a kind and short texts; never messages,
/// tool data or other content. Failures are logged with status codes or exception types only.
/// </summary>
internal sealed class NhAssistantWebPushNotifier(
    INhAssistantStore store,
    NhAssistantNotificationStore notifications,
    IOptionsMonitor<NhAssistantOptions> options,
    IHttpClientFactory httpClients,
    ILogger<NhAssistantWebPushNotifier> logger) : INhAssistantNotifier
{
    public const string HttpClientName = "NewHeap.Platform.AI.Chat.Push";

    private const int MaxParallelDeliveries = 8;

    public async Task TurnEndedAsync(NhAssistantTurnOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var push = options.CurrentValue.Push;
        if (!push.IsConfigured)
        {
            return;
        }

        try
        {
            var conversation = await store.FindShareableConversationAsync(outcome.ConversationId, cancellationToken);
            if (conversation is null)
            {
                return;
            }
            var participants = await store.GetParticipantsAsync(conversation.Id, cancellationToken);
            var recipients = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var actorId in participants.Select(participant => participant.ActorId).Prepend(conversation.OwnerActorId))
            {
                if (KindFor(outcome, actorId, push.MinimumTurnDuration) is { } kind)
                {
                    recipients[actorId] = kind;
                }
            }
            if (recipients.Count == 0)
            {
                return;
            }

            var actorName = NameOf(conversation, participants, outcome.ActorId);
            await DeliverAsync(
                push,
                conversation,
                recipients,
                (kind, language) => NhAssistantPushTexts.ForTurn(
                    kind,
                    language,
                    push.IncludeConversationTitle ? conversation.Title : null,
                    actorName),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Assistant push notifications for turn {TurnId} were not sent ({ExceptionType}).",
                outcome.TurnId,
                exception.GetType().Name);
        }
    }

    public async Task InvitedAsync(
        Guid conversationId,
        string inviteeActorId,
        string inviterDisplayName,
        CancellationToken cancellationToken)
    {
        var push = options.CurrentValue.Push;
        if (!push.IsConfigured)
        {
            return;
        }

        try
        {
            var conversation = await store.FindShareableConversationAsync(conversationId, cancellationToken);
            if (conversation is null)
            {
                return;
            }
            await DeliverAsync(
                push,
                conversation,
                new Dictionary<string, string>(StringComparer.Ordinal) { [inviteeActorId] = NhAssistantPushKinds.Invited },
                (kind, language) => NhAssistantPushTexts.ForInvitation(
                    language,
                    push.IncludeConversationTitle ? conversation.Title : null,
                    inviterDisplayName),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Assistant push invitation for conversation {ConversationId} was not sent ({ExceptionType}).",
                conversationId,
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// The person who started the turn hears about an approval request, which needs them, and about a
    /// long turn; everyone else only about a long turn that finished or failed. Cancelled turns and
    /// quick turns are silent: the user is still looking at them.
    /// </summary>
    internal static string? KindFor(NhAssistantTurnOutcome outcome, string actorId, TimeSpan minimumDuration)
    {
        var isActor = string.Equals(actorId, outcome.ActorId, StringComparison.Ordinal);
        var ranLong = outcome.Duration >= minimumDuration;
        return outcome.Status switch
        {
            NhAssistantTurnStatuses.WaitingForApproval => isActor ? NhAssistantPushKinds.ApprovalRequired : null,
            NhAssistantTurnStatuses.Completed when ranLong => isActor ? NhAssistantPushKinds.TurnCompleted : NhAssistantPushKinds.TurnCompletedByOther,
            NhAssistantTurnStatuses.Failed when ranLong => isActor ? NhAssistantPushKinds.TurnFailed : NhAssistantPushKinds.TurnFailedByOther,
            _ => null
        };
    }

    private async Task DeliverAsync(
        NhAssistantPushOptions push,
        AssistantConversation conversation,
        IReadOnlyDictionary<string, string> kindsByActor,
        Func<string, string, (string Title, string Body)> texts,
        CancellationToken cancellationToken)
    {
        var subscriptions = await notifications.GetDeliverableSubscriptionsAsync(kindsByActor.Keys.ToArray(), cancellationToken);
        if (subscriptions.Count == 0)
        {
            return;
        }

        // Sign once per push service origin, before the parallel deliveries share the results.
        var authorizations = new Dictionary<string, string>(StringComparer.Ordinal);
        var deliverable = new List<(AssistantPushSubscription Subscription, Uri Endpoint)>(subscriptions.Count);
        using (var signingKey = NhAssistantWebPushKeys.CreateSigningKey(push.PublicKey!, push.PrivateKey!))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var subscription in subscriptions)
            {
                if (!NhAssistantPushSubscriptionValidator.TryGetAllowedEndpoint(subscription.Endpoint, push.AllowedEndpointHosts, out var endpoint))
                {
                    logger.LogWarning(
                        "Assistant push subscription {SubscriptionId} points to a push service that is no longer allowed.",
                        subscription.Id);
                    continue;
                }
                var origin = endpoint.GetLeftPart(UriPartial.Authority);
                if (!authorizations.ContainsKey(origin))
                {
                    authorizations[origin] = NhAssistantVapid.CreateAuthorization(signingKey, push.PublicKey!, push.Subject!, endpoint, now);
                }
                deliverable.Add((subscription, endpoint));
            }
        }

        var gone = new ConcurrentBag<Guid>();
        var client = httpClients.CreateClient(HttpClientName);
        await Parallel.ForEachAsync(
            deliverable,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDeliveries, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                var kind = kindsByActor[item.Subscription.ActorId];
                var (title, body) = texts(kind, item.Subscription.Language);
                var payload = NhAssistantPushPayload.Create(kind, conversation.Id, title, body);
                var status = await SendAsync(
                    client,
                    push,
                    item.Subscription,
                    item.Endpoint,
                    authorizations[item.Endpoint.GetLeftPart(UriPartial.Authority)],
                    payload,
                    kind == NhAssistantPushKinds.ApprovalRequired ? "high" : "normal",
                    conversation.Id,
                    token);
                if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    gone.Add(item.Subscription.Id);
                }
            });

        if (!gone.IsEmpty)
        {
            await notifications.DeleteSubscriptionsAsync(gone.ToArray(), cancellationToken);
        }
    }

    private async Task<HttpStatusCode?> SendAsync(
        HttpClient client,
        NhAssistantPushOptions push,
        AssistantPushSubscription subscription,
        Uri endpoint,
        string authorization,
        byte[] payload,
        string urgency,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        byte[] body;
        try
        {
            body = NhAssistantWebPushEncryption.Encrypt(
                payload,
                NhAssistantBase64Url.Decode(subscription.P256dh),
                NhAssistantBase64Url.Decode(subscription.Auth));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning("Assistant push subscription {SubscriptionId} has unusable keys.", subscription.Id);
            return HttpStatusCode.Gone;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", ((long)push.TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", urgency);
        // A newer notification about the same conversation replaces an undelivered older one.
        request.Headers.TryAddWithoutValidation("Topic", NhAssistantBase64Url.Encode(conversationId.ToByteArray()));
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(push.DeliveryTimeout);
        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode && response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
            {
                logger.LogWarning(
                    "Assistant push to subscription {SubscriptionId} was refused with status {StatusCode}.",
                    subscription.Id,
                    (int)response.StatusCode);
            }
            return response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(
                "Assistant push to subscription {SubscriptionId} failed ({ExceptionType}).",
                subscription.Id,
                exception.GetType().Name);
            return null;
        }
    }

    private static string NameOf(
        AssistantConversation conversation,
        IReadOnlyList<AssistantConversationParticipant> participants,
        string actorId)
    {
        if (string.Equals(actorId, conversation.OwnerActorId, StringComparison.Ordinal))
        {
            return NhAssistantParticipantNames.Normalize(conversation.OwnerDisplayName);
        }
        var participant = participants.FirstOrDefault(item => string.Equals(item.ActorId, actorId, StringComparison.Ordinal));
        return NhAssistantParticipantNames.Normalize(participant?.DisplayName);
    }
}

internal static class NhAssistantPushKinds
{
    public const string TurnCompleted = "turn-completed";
    public const string TurnCompletedByOther = "turn-completed-by-other";
    public const string TurnFailed = "turn-failed";
    public const string TurnFailedByOther = "turn-failed-by-other";
    public const string ApprovalRequired = "approval-required";
    public const string Invited = "invited";
}

/// <summary>
/// The JSON the service worker of <c>@newheap/platform-ai-chat</c> shows. Bounded and content-free.
/// </summary>
internal static class NhAssistantPushPayload
{
    public static byte[] Create(string kind, Guid conversationId, string title, string body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "nh-assistant");
            writer.WriteString("kind", kind);
            writer.WriteString("conversationId", conversationId);
            writer.WriteString("title", Bound(title, 120));
            writer.WriteString("body", Bound(body, 240));
            writer.WriteString("tag", "nh-assistant:" + conversationId.ToString("D"));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Bound(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
    }
}

/// <summary>
/// Notification texts in English and Dutch, chosen per browser by the language it subscribed with.
/// </summary>
internal static class NhAssistantPushTexts
{
    public static (string Title, string Body) ForTurn(string kind, string language, string? conversationTitle, string actorName)
    {
        var dutch = IsDutch(language);
        var title = string.IsNullOrWhiteSpace(conversationTitle) ? (dutch ? "Assistent" : "Assistant") : conversationTitle;
        var name = string.IsNullOrWhiteSpace(actorName) ? (dutch ? "Een deelnemer" : "A participant") : actorName;
        var body = kind switch
        {
            NhAssistantPushKinds.ApprovalRequired => dutch ? "Er wacht een actie op goedkeuring." : "An action is waiting for approval.",
            NhAssistantPushKinds.TurnCompleted => dutch ? "Het antwoord staat klaar." : "The answer is ready.",
            NhAssistantPushKinds.TurnCompletedByOther => dutch ? $"{name} heeft een antwoord gekregen." : $"{name} received an answer.",
            NhAssistantPushKinds.TurnFailed => dutch ? "De taak kon niet worden afgerond." : "The task could not be completed.",
            NhAssistantPushKinds.TurnFailedByOther => dutch ? $"De taak van {name} kon niet worden afgerond." : $"The task of {name} could not be completed.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return (title, body);
    }

    public static (string Title, string Body) ForInvitation(string language, string? conversationTitle, string inviterName)
    {
        var dutch = IsDutch(language);
        var name = string.IsNullOrWhiteSpace(inviterName) ? (dutch ? "Een collega" : "A colleague") : inviterName;
        var title = dutch ? $"{name} heeft een gesprek met je gedeeld" : $"{name} shared a conversation with you";
        var body = string.IsNullOrWhiteSpace(conversationTitle)
            ? (dutch ? "Open de assistent om mee te doen." : "Open the assistant to join in.")
            : conversationTitle;
        return (title, body);
    }

    private static bool IsDutch(string language)
    {
        return language.StartsWith("nl", StringComparison.OrdinalIgnoreCase);
    }
}
