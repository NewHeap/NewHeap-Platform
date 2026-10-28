using System.Globalization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Actors;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Posts the answer of a completed run that another assistant requested for a person into the chat
/// the request was made in, when the consultation answers requests there
/// (<see cref="NhAssistantRequestAnswerRoute.AskingConversation"/>, FB29). The answer is the run's
/// result as people read it, in the person's culture when the run wrote it per culture, as an
/// application message readable by the person who asked, with a link to where the application shows
/// the subject. It is posted once: one idempotency key per run. A shadow run, a run that read results
/// with the agent's own grant that people without the tool's policy may not see, a run without an
/// answer, a conversation that is gone, archived or no chat, and a person who may no longer open it
/// post nothing. A failed post never changes how the run ended; nothing here logs the answer.
/// </summary>
internal sealed class NhAssistantRequestAnswers(
    INhAssistantStore store,
    INhAssistantConversationMessages messages,
    NhAssistantSubjectAccessService subjects,
    NhAssistantRegistrationState registration,
    IServiceProvider services,
    ILogger<NhAssistantRequestAnswers> logger)
{
    /// <summary>
    /// The idempotency key of a run's answer is this prefix and the run id.
    /// </summary>
    internal const string IdempotencyPrefix = "request-answer:";

    /// <summary>
    /// Posts the answer of <paramref name="run"/>, which ended completed in <paramref name="thread"/>.
    /// Returns whether a message was posted now; a repeated call posts nothing again.
    /// </summary>
    public async Task<bool> PostAsync(AssistantRun run, AssistantThread thread, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(thread);
        if (run.AskingConversationId is not { } askingConversationId
            || string.IsNullOrWhiteSpace(run.InitiatorActorId))
        {
            return false;
        }

        // Shadow work stays with administrators, and what the agent read with its own grant that
        // people without the tool's policy may not see never leaves its thread.
        if (run.Shadow || thread.Shadow || run.UsedRestrictedResults)
        {
            Skipped(run, run.UsedRestrictedResults ? "restricted-results" : "shadow");
            return false;
        }

        try
        {
            return await PostCoreAsync(run, thread, askingConversationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "The answer of request run {RunId} was not posted into conversation {ConversationId} ({ExceptionType}).",
                run.Id,
                askingConversationId,
                exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> PostCoreAsync(
        AssistantRun run,
        AssistantThread thread,
        Guid askingConversationId,
        CancellationToken cancellationToken)
    {
        // Only a person's chat hears the answer; the post checks the person's access itself.
        var conversation = await store.FindShareableConversationAsync(askingConversationId, cancellationToken);
        if (conversation is null
            || !string.Equals(conversation.Kind, NhAssistantConversationKinds.Chat, StringComparison.Ordinal))
        {
            Skipped(run, NhAssistantErrorCodes.ConversationNotFound);
            return false;
        }

        var culture = await CultureOfAsync(run.InitiatorActorId!, cancellationToken);
        var (result, _) = NhAssistantRunResults.ResultOf(run, culture);
        var answer = string.IsNullOrWhiteSpace(result) ? run.Summary : result;
        if (string.IsNullOrWhiteSpace(answer))
        {
            Skipped(run, "no-answer");
            return false;
        }

        var dutch = string.Equals(culture.TwoLetterISOLanguageName, "nl", StringComparison.OrdinalIgnoreCase);
        var type = subjects.FindType(thread.SubjectType);
        var subjectName = type?.DisplayNameOf(thread.SubjectId, thread.SubjectDisplayName, culture) ?? thread.SubjectDisplayName;
        var link = type?.LinkFor(thread.SubjectId);
        var posted = await messages.PostAsync(
            askingConversationId,
            new NhAssistantApplicationMessage(TextOf(answer.Trim(), dutch))
            {
                Title = TitleOf(subjectName, dutch),
                Link = link is not null && link.Length <= NhAssistantApplicationMessageLink.MaxUrlLength
                    ? new NhAssistantApplicationMessageLink(link)
                    : null,
                IdempotencyKey = IdempotencyPrefix + run.Id.ToString("N"),
                // Only where the person who asked may open the chat now: its owner or a participant
                // of its tenant, as when they asked.
                Access = NhAssistantApplicationMessageAccess.ReadableBy(
                    new NhAssistantConversationPerson(run.InitiatorActorId!, conversation.TenantId)),
                // The person is told that the request was answered (RequestAnswered) as before.
                Notify = false
            },
            cancellationToken);
        if (!posted.Success)
        {
            Skipped(run, NhAssistantResults.CodeOf(posted, NhAssistantErrorCodes.ConversationNotFound));
            return false;
        }
        if (posted.Data!.Duplicate)
        {
            return false;
        }

        logger.LogInformation(
            "Request run {RunId} posted its answer into conversation {ConversationId}.",
            run.Id,
            askingConversationId);
        return true;
    }

    /// <summary>
    /// The answer within the length of an application message; a longer one is cut at a whole
    /// character and says that the agent's thread holds all of it.
    /// </summary>
    private string TextOf(string answer, bool dutch)
    {
        var maximum = registration.Limits.MaxMessageChars;
        if (answer.Length <= maximum)
        {
            return answer;
        }

        var note = dutch
            ? "\n\n*Het volledige antwoord staat in de thread van de agent.*"
            : "\n\n*The full answer is in the agent's thread.*";
        var length = Math.Max(0, maximum - note.Length - 1);
        if (length > 0 && char.IsHighSurrogate(answer[length - 1]))
        {
            length--;
        }

        return answer[..length].TrimEnd() + "…" + note;
    }

    private static string TitleOf(string? subjectName, bool dutch)
    {
        var title = string.IsNullOrWhiteSpace(subjectName)
            ? dutch ? "Antwoord op je verzoek" : "Answer to your request"
            : (dutch ? "Antwoord op je verzoek over " : "Answer to your request about ") + subjectName.Trim();
        if (title.Length <= NhAssistantApplicationMessage.MaxTitleLength && !title.Any(char.IsControl))
        {
            return title;
        }

        // A name that does not fit a title stays in the agent's thread.
        return dutch ? "Antwoord op je verzoek" : "Answer to your request";
    }

    /// <summary>
    /// The culture the person reads in: the one the application's directory knows for them, otherwise
    /// the culture the run ends in.
    /// </summary>
    private async Task<CultureInfo> CultureOfAsync(string actorId, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(actorId, out var userId))
        {
            var actor = NhActorRef.ForUser(userId);
            var known = await NhAssistantNotificationCultures.GetAsync(services, [actor], cancellationToken);
            if (known.GetValueOrDefault(actor) is { Length: > 0 } name)
            {
                try
                {
                    return CultureInfo.GetCultureInfo(name);
                }
                catch (CultureNotFoundException)
                {
                    // An unknown culture reads as the run's own.
                }
            }
        }

        return CultureInfo.CurrentUICulture;
    }

    private void Skipped(AssistantRun run, string reason)
    {
        logger.LogInformation(
            "Request run {RunId} posted no answer into its asking conversation ({Reason}).",
            run.Id,
            reason);
    }
}
