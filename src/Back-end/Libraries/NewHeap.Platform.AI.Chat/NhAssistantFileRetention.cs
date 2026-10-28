using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The application's retention of the files people uploaded to a conversation, registered with
/// <see cref="NhAssistantBuilder.UseFileRetention{TRetention}"/>. The file source stores uploads in
/// the application's own storage (<see cref="INhAssistantFileSource.StoreUploadAsync"/>); this lets them
/// go with their conversation. The assistant calls it once a conversation is gone for its people:
/// after the owner's archive of a chat committed (<c>DELETE conversations/{id}</c>) and after subject
/// retention (<see cref="INhAiSubjectRetention"/>) deleted an agent's thread. Without it uploads stay
/// under the application's own retention.
/// <para>
/// The call is best effort: a failed result or an exception is logged as a warning with the
/// conversation and the reason, never undoes the archive or the deletion, and is not retried. Make it
/// idempotent: forgetting a conversation without uploads, or one forgotten before, succeeds, so the
/// application's own retention can call it again for a conversation it missed.
/// </para>
/// </summary>
public interface INhAssistantFileRetention
{
    /// <summary>
    /// Removes, or marks for removal, the uploads the file source stored for
    /// <see cref="NhAssistantForgottenConversation.ConversationId"/>
    /// (<see cref="NhAssistantFileUpload.ConversationId"/>). Return a failed result when they could not
    /// be removed; the assistant logs its code only. The call follows the caller's cancellation.
    /// </summary>
    Task<TaskResult> ForgetConversationAsync(
        NhAssistantForgottenConversation conversation,
        CancellationToken cancellationToken);
}

/// <summary>
/// A conversation whose uploads <see cref="INhAssistantFileRetention.ForgetConversationAsync"/> forgets.
/// </summary>
/// <param name="ConversationId">The conversation the uploads were stored for.</param>
/// <param name="AgentId">The conversation's agent.</param>
/// <param name="Reason">Why the conversation is gone: one of <see cref="NhAssistantFileRetentionReasons"/>.</param>
public sealed record NhAssistantForgottenConversation(
    Guid ConversationId,
    string AgentId,
    string Reason)
{
    /// <summary>
    /// The conversation's tenant; <see langword="null"/> in a host without tenants.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The subject of a forgotten agent thread; <see langword="null"/> for a chat.
    /// </summary>
    public NhAiSubjectRef? Subject { get; init; }
}

/// <summary>
/// Why <see cref="INhAssistantFileRetention.ForgetConversationAsync"/> is called.
/// </summary>
public static class NhAssistantFileRetentionReasons
{
    /// <summary>The owner archived the chat for everyone.</summary>
    public const string Archived = "archived";

    /// <summary>Subject retention deleted the agent's thread with its subject.</summary>
    public const string SubjectForgotten = "subject-forgotten";
}

/// <summary>
/// Calls the application's <see cref="INhAssistantFileRetention"/>, when it registered one, after the
/// conversation is gone (FB24): best effort, so a failure or an exception only warns, and the caller's
/// cancellation still throws.
/// </summary>
internal sealed class NhAssistantFileRetentionCalls(
    IServiceProvider services,
    ILogger<NhAssistantFileRetentionCalls> logger)
{
    public async Task ForgetConversationAsync(
        NhAssistantForgottenConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var retention = services.GetService<INhAssistantFileRetention>();
        if (retention is null)
        {
            return;
        }

        TaskResult forgotten;
        try
        {
            forgotten = await retention.ForgetConversationAsync(conversation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The application's messages may name its files; log the type only.
            logger.LogWarning(
                "The file retention failed to forget the uploads of conversation {ConversationId} ({Reason}) with {ExceptionType}; the conversation stays gone.",
                conversation.ConversationId,
                conversation.Reason,
                exception.GetType().Name);
            return;
        }

        if (!forgotten.Success)
        {
            logger.LogWarning(
                "The file retention did not forget the uploads of conversation {ConversationId} ({Reason}): {Code}; the conversation stays gone.",
                conversation.ConversationId,
                conversation.Reason,
                NhAssistantResults.CodeOf(forgotten, "failed"));
        }
    }
}
