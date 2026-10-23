using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet.Notifications;

/// <summary>
/// Sends Web Push notifications to the browsers of people who belong to a conversation. It runs after
/// the final event of a turn was written, sends to every browser in parallel within
/// <see cref="NhAssistantPushOptions.DeliveryTimeout"/> and removes subscriptions the push service
/// reports as gone. Notifications carry the conversation id, a kind and short texts, and the run id
/// of a notification about the proposals of a run; never messages, tool data or other content.
/// Failures are logged with status codes or exception types only.
/// </summary>
internal sealed class NhAssistantWebPushNotifier(
    INhAssistantStore store,
    NhAssistantNotificationStore notifications,
    IOptionsMonitor<NhAssistantOptions> options,
    IHttpClientFactory httpClients,
    IServiceProvider services,
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

    public async Task AgentNotificationAsync(
        NhAssistantAgentNotificationContext context,
        IReadOnlyList<string> recipientActorIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var push = options.CurrentValue.Push;
        if (!push.IsConfigured || recipientActorIds.Count == 0)
        {
            return;
        }

        try
        {
            var conversation = await store.FindShareableConversationAsync(context.ConversationId, cancellationToken);
            if (conversation is null)
            {
                return;
            }

            var kind = NhAssistantPushKinds.ForAgent(context.Kind);
            var recipients = recipientActorIds.Distinct(StringComparer.Ordinal).ToDictionary(actorId => actorId, _ => kind, StringComparer.Ordinal);
            if (NhAssistantAgentNotificationTexts.IsRunProposals(context.Kind))
            {
                // The proposals of a run (RN4, RN5): the click opens the conversation the context
                // names, and the run id lets the application open its inbox of the run instead.
                var pluralNames = NhAssistantAgentNotificationTexts.PluralNamesOf(services.GetService<NhAssistantSubjectAccessService>());
                await DeliverAsync(
                    push,
                    conversation,
                    recipients,
                    (_, language) => NhAssistantAgentNotificationTexts.ForRunProposals(
                        context.Kind,
                        language,
                        context.Objects,
                        context.ProposalCount,
                        push.IncludeConversationTitle ? context.SubjectDisplayName : null,
                        pluralNames),
                    cancellationToken,
                    context.RunId);
                return;
            }

            await DeliverAsync(
                push,
                conversation,
                recipients,
                (_, language) => NhAssistantPushTexts.ForAgent(
                    context.Kind,
                    language,
                    push.IncludeConversationTitle ? context.SubjectDisplayName : null,
                    context.ProposalCount,
                    proposalStatus: context.ProposalStatus),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Assistant push for agent event {Kind} in thread {ConversationId} was not sent ({ExceptionType}).",
                context.Kind,
                context.ConversationId,
                exception.GetType().Name);
        }
    }

    public async Task ApplicationMessageAsync(Guid conversationId, CancellationToken cancellationToken)
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

            var participants = await store.GetParticipantsAsync(conversation.Id, cancellationToken);
            var recipients = NhAssistantApplicationMessageRecipients.Of(conversation, participants)
                .ToDictionary(actorId => actorId, _ => NhAssistantPushKinds.ApplicationMessage, StringComparer.Ordinal);
            if (recipients.Count == 0)
            {
                return;
            }

            await DeliverAsync(
                push,
                conversation,
                recipients,
                (_, language) => NhAssistantPushTexts.ForApplicationMessage(
                    language,
                    push.IncludeConversationTitle ? conversation.Title : null),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Assistant push for an application message in conversation {ConversationId} was not sent ({ExceptionType}).",
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

    /// <param name="runId">The run a notification about its proposals names: its payload carries the
    /// run, and a newer notification about the same run, not the same conversation, replaces an
    /// undelivered older one.</param>
    private async Task DeliverAsync(
        NhAssistantPushOptions push,
        AssistantConversation conversation,
        IReadOnlyDictionary<string, string> kindsByActor,
        Func<string, string, (string Title, string Body)> texts,
        CancellationToken cancellationToken,
        Guid? runId = null)
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
                var payload = NhAssistantPushPayload.Create(kind, conversation.Id, title, body, runId);
                var status = await SendAsync(
                    client,
                    push,
                    item.Subscription,
                    item.Endpoint,
                    authorizations[item.Endpoint.GetLeftPart(UriPartial.Authority)],
                    payload,
                    kind is NhAssistantPushKinds.ApprovalRequired
                        or NhAssistantPushKinds.AgentProposals
                        or NhAssistantPushKinds.AgentRunProposals
                        or NhAssistantPushKinds.AgentExecutionUnknown ? "high" : "normal",
                    runId ?? conversation.Id,
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
        Guid topicId,
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
        // A newer notification about the same conversation, or the same run, replaces an undelivered
        // older one.
        request.Headers.TryAddWithoutValidation("Topic", NhAssistantBase64Url.Encode(topicId.ToByteArray()));
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
    public const string AgentProposals = "agent-proposals";
    public const string AgentProposalExpiring = "agent-proposal-expiring";
    public const string AgentProposalEscalated = "agent-proposal-escalated";
    public const string AgentExecutionUnknown = "agent-execution-unknown";
    public const string AgentRunFailed = "agent-run-failed";
    public const string AgentRequestAnswered = "agent-request-answered";
    public const string AgentMcpConnectionMissing = "agent-mcp-connection-missing";
    public const string AgentConsultationProposalDecided = "agent-consultation-proposal-decided";

    /// <summary>
    /// The application reported that it carried out, or could not carry out, an approved proposal.
    /// </summary>
    public const string AgentExecutionReported = "agent-execution-reported";

    /// <summary>
    /// Proposals of one run wait for the recipient; the payload carries the run.
    /// </summary>
    public const string AgentRunProposals = "agent-run-proposals";

    /// <summary>
    /// Proposals of one run still wait for the recipient; the payload carries the run.
    /// </summary>
    public const string AgentRunProposalsExpiring = "agent-run-proposals-expiring";

    /// <summary>
    /// Proposals of one run were escalated to the recipient; the payload carries the run.
    /// </summary>
    public const string AgentRunProposalsEscalated = "agent-run-proposals-escalated";

    /// <summary>
    /// The application posted a message into the conversation.
    /// </summary>
    public const string ApplicationMessage = "application-message";

    public static string ForAgent(NhAssistantAgentNotificationKind kind)
    {
        return kind switch
        {
            NhAssistantAgentNotificationKind.RunEndedWithProposals => AgentProposals,
            NhAssistantAgentNotificationKind.ProposalExpiring => AgentProposalExpiring,
            NhAssistantAgentNotificationKind.ProposalEscalated => AgentProposalEscalated,
            NhAssistantAgentNotificationKind.RunProposalsWaiting => AgentRunProposals,
            NhAssistantAgentNotificationKind.RunProposalsExpiring => AgentRunProposalsExpiring,
            NhAssistantAgentNotificationKind.RunProposalsEscalated => AgentRunProposalsEscalated,
            NhAssistantAgentNotificationKind.ExecutionUnknown => AgentExecutionUnknown,
            NhAssistantAgentNotificationKind.RunFailed => AgentRunFailed,
            NhAssistantAgentNotificationKind.RequestAnswered => AgentRequestAnswered,
            NhAssistantAgentNotificationKind.McpConnectionMissing => AgentMcpConnectionMissing,
            NhAssistantAgentNotificationKind.ConsultationProposalDecided => AgentConsultationProposalDecided,
            NhAssistantAgentNotificationKind.ExecutionReported => AgentExecutionReported,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}

/// <summary>
/// The JSON the service worker of <c>@newheap/platform-ai-chat</c> shows. Bounded and content-free.
/// A notification about the proposals of a run also carries <c>runId</c> and is tagged per run, so the
/// notifications of different runs about one object stay apart and a reminder replaces the run's
/// earlier notification.
/// </summary>
internal static class NhAssistantPushPayload
{
    public static byte[] Create(string kind, Guid conversationId, string title, string body, Guid? runId = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "nh-assistant");
            writer.WriteString("kind", kind);
            writer.WriteString("conversationId", conversationId);
            if (runId is { } run)
            {
                writer.WriteString("runId", run);
            }
            writer.WriteString("title", Bound(title, 120));
            writer.WriteString("body", Bound(body, 240));
            writer.WriteString(
                "tag",
                runId is { } tagged ? "nh-assistant-run:" + tagged.ToString("D") : "nh-assistant:" + conversationId.ToString("D"));
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

    /// <summary>
    /// The notification of an application message: the conversation's title, when the server includes
    /// titles, and a fixed text; never the message itself.
    /// </summary>
    public static (string Title, string Body) ForApplicationMessage(string language, string? conversationTitle)
    {
        return NhAssistantAgentNotificationTexts.ForApplicationMessage(language, conversationTitle);
    }

    public static (string Title, string Body) ForAgent(
        NhAssistantAgentNotificationKind kind,
        string language,
        string? subjectDisplayName,
        int proposalCount,
        string? mcpServerDisplayName = null,
        string? code = null,
        string? proposalStatus = null)
    {
        return NhAssistantAgentNotificationTexts.ForAgent(kind, language, subjectDisplayName, proposalCount, mcpServerDisplayName, code, proposalStatus);
    }

    private static bool IsDutch(string language)
    {
        return language.StartsWith("nl", StringComparison.OrdinalIgnoreCase);
    }
}
