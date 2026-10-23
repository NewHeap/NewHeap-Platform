using System.Globalization;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-312: tells a person in their assistant conversation that the long-running portfolio analysis
/// they started has finished. The application posts the message with
/// <see cref="INhAssistantConversationMessages"/>, the supported way for an application to write into
/// a conversation: the person sees it at once as the application's, with a title, the sender and a
/// link to the analysis, is notified by Web Push when they are not looking, and a later turn gives it
/// to the model as information from the application, never as the person's request.
/// <para>
/// The analysis runs as a background operation that may retry its last step, so the post carries an
/// idempotency key per operation: the conversation keeps one message however often it runs. The
/// conversation id came from the person's client, so the message is posted only when that person
/// may open the conversation (<see cref="NhAssistantApplicationMessage.ReadableBy"/>).
/// </para>
/// </summary>
public sealed class ProjectAnalysisAssistantNotices(
    INhAssistantConversationMessages messages,
    ILogger<ProjectAnalysisAssistantNotices> logger)
{
    /// <summary>
    /// Posts that the analysis <paramref name="operationId"/> of <paramref name="projectCount"/>
    /// projects finished into <paramref name="conversationId"/> for <paramref name="ownerUserId"/>.
    /// An expected refusal, such as a conversation the person deleted meanwhile, is logged by its code
    /// and returned; it never fails the analysis.
    /// </summary>
    public async Task<TaskResult<NhAssistantApplicationMessageReceipt>> AnalysisFinishedAsync(
        Guid conversationId,
        Guid ownerUserId,
        Guid operationId,
        int projectCount,
        CancellationToken cancellationToken)
    {
        var message = new NhAssistantApplicationMessage(string.Create(
            CultureInfo.InvariantCulture,
            $"The portfolio analysis of **{projectCount} projects** finished. Open it to see the outcome per project."))
        {
            Title = "Portfolio analysis finished",
            SenderName = "Portfolio analysis",
            Link = new NhAssistantApplicationMessageLink($"/background-operations/{operationId}", "Open the analysis"),
            Notify = true,
            IdempotencyKey = "portfolio-analysis:" + operationId.ToString("N"),
            ReadableBy = new NhAssistantConversationPerson(ownerUserId.ToString())
        };

        var posted = await messages.PostAsync(conversationId, message, cancellationToken);
        if (!posted.Success)
        {
            logger.LogWarning(
                "The portfolio analysis {OperationId} could not tell conversation {ConversationId} that it finished ({Code}).",
                operationId,
                conversationId,
                posted.GetResultItems().FirstOrDefault()?.Name);
        }

        return posted;
    }
}
