using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Actors;

namespace NewHeap.Platform.AI.Chat.Notifications;

/// <summary>
/// Delivers agent notifications as in-app messages through the platform's
/// <see cref="INhUserNotificationSink"/>, so they appear in the application's own inbox next to
/// Web Push. Recipients whose actor id is not a user id are skipped. Messages use the recipient's
/// culture from <see cref="INhActorDirectory"/> when known, and otherwise the culture of the run.
/// The proposals of a run are one message per run, grouped by the run.
/// </summary>
internal sealed class NhAssistantInAppAgentNotificationChannel : INhAssistantAgentNotificationChannel
{
    internal const string Category = "assistant-agent";

    private readonly IServiceProvider _services;
    private readonly ILogger<NhAssistantInAppAgentNotificationChannel> _logger;

    public NhAssistantInAppAgentNotificationChannel(
        IServiceProvider services,
        ILogger<NhAssistantInAppAgentNotificationChannel> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task DeliverAsync(
        NhAssistantAgentNotificationContext context,
        IReadOnlyList<string> recipientActorIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recipientActorIds);

        var sink = _services.GetService<INhUserNotificationSink>();
        if (sink is null)
        {
            _logger.LogWarning(
                "In-app agent notifications are enabled, but no notification sink is registered; register the NewHeap notifications module or an INhUserNotificationSink.");
            return;
        }

        var recipients = new List<NhActorRef>();
        foreach (var actorId in recipientActorIds.Distinct(StringComparer.Ordinal))
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

        var cultures = await NhAssistantNotificationCultures.GetAsync(_services, recipients, cancellationToken);
        var pluralNames = NhAssistantAgentNotificationTexts.PluralNamesOf(_services.GetService<NhAssistantSubjectAccessService>());
        foreach (var recipient in recipients)
        {
            recipient.TryGetUserId(out var userId);
            var language = cultures.GetValueOrDefault(recipient) ?? CultureInfo.CurrentUICulture.Name;
            var (title, body) = NhAssistantAgentNotificationTexts.IsRunProposals(context.Kind)
                ? NhAssistantAgentNotificationTexts.ForRunProposals(
                    context.Kind,
                    language,
                    context.Objects,
                    context.ProposalCount,
                    context.SubjectDisplayName,
                    pluralNames)
                : NhAssistantAgentNotificationTexts.ForAgent(
                    context.Kind,
                    language,
                    context.SubjectDisplayName,
                    context.ProposalCount,
                    context.McpServerDisplayName,
                    context.FailureCode,
                    context.ProposalStatus);
            try
            {
                var result = await sink.SendAsync(
                    new NhUserNotificationRequest(userId, title, body)
                    {
                        Category = Category,
                        Level = LevelFor(context),
                        GroupKey = GroupKeyOf(context)
                    },
                    cancellationToken);
                if (!result.Success)
                {
                    _logger.LogWarning(
                        "In-app agent notification {Kind} for thread {ConversationId} was refused by the notification sink.",
                        context.Kind,
                        context.ConversationId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogWarning(
                    "In-app agent notification {Kind} for thread {ConversationId} was not delivered ({ExceptionType}).",
                    context.Kind,
                    context.ConversationId,
                    exception.GetType().Name);
            }
        }
    }

    /// <summary>
    /// The proposals of a run stay together per run (RN5), so its reminders and escalations join the
    /// message about it; every other event stays together per agent thread, so repeated events of a
    /// subject stay together.
    /// </summary>
    internal static string GroupKeyOf(NhAssistantAgentNotificationContext context)
    {
        return NhAssistantAgentNotificationTexts.IsRunProposals(context.Kind) && context.RunId is { } runId
            ? $"{Category}:run:{runId:N}"
            : $"{Category}:{context.ConversationId:N}";
    }

    private static NhUserNotificationLevel LevelFor(NhAssistantAgentNotificationContext context)
    {
        return context.Kind switch
        {
            NhAssistantAgentNotificationKind.RunFailed => NhUserNotificationLevel.Error,
            NhAssistantAgentNotificationKind.ExecutionUnknown => NhUserNotificationLevel.Warning,
            NhAssistantAgentNotificationKind.ProposalEscalated => NhUserNotificationLevel.Warning,
            NhAssistantAgentNotificationKind.RunProposalsEscalated => NhUserNotificationLevel.Warning,
            NhAssistantAgentNotificationKind.McpConnectionMissing => NhUserNotificationLevel.Warning,
            NhAssistantAgentNotificationKind.RequestAnswered => NhUserNotificationLevel.Success,
            NhAssistantAgentNotificationKind.ConsultationProposalDecided => context.ProposalStatus == NhAssistantApprovalStatuses.Executed
                ? NhUserNotificationLevel.Success
                : NhUserNotificationLevel.Information,
            _ => NhUserNotificationLevel.Information
        };
    }
}
