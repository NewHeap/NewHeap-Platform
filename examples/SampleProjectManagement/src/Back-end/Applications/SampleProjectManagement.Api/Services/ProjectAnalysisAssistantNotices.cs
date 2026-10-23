using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.DAL;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-312: tells a person in their assistant conversation that the long-running portfolio analysis
/// they started has finished. The application posts the message with
/// <see cref="INhAssistantConversationMessages"/>, the supported way for an application to write into
/// a conversation: the person sees it at once as the application's, with a title, the sender and a
/// link to the analysis, is notified by Web Push (and in the application's inbox when the host turned
/// on in-app notifications) when they are not looking, and a later turn gives it to the model as
/// information from the application, never as the person's request.
/// <para>
/// The analysis runs as a background operation that may retry its last step, so the post carries an
/// idempotency key per operation: the conversation keeps one message however often it runs. The
/// conversation id came from the person's client, so the post depends on that person being able to
/// open the conversation (<see cref="NhAssistantApplicationMessageAccess.ReadableBy"/>). The text names
/// the division, which people named themselves: <see cref="NhAssistantMarkdown.Escape"/> shows the name
/// as it is, and the message stays untrusted (no <see cref="NhAssistantApplicationMessage.Trusted"/>),
/// because the model must never follow instructions hidden in a name.
/// </para>
/// </summary>
public sealed class ProjectAnalysisAssistantNotices(
    INhAssistantConversationMessages messages,
    SampleProjectManagementDbContext dbContext,
    ILogger<ProjectAnalysisAssistantNotices> logger)
{
    /// <summary>
    /// Posts that the analysis <paramref name="operationId"/> of <paramref name="projectCount"/>
    /// projects of <paramref name="divisionId"/> finished into <paramref name="conversationId"/> for
    /// <paramref name="ownerUserId"/>. An expected refusal, such as a conversation the person deleted
    /// meanwhile, is logged by its code and returned; it never fails the analysis.
    /// </summary>
    public async Task<TaskResult<NhAssistantApplicationMessageReceipt>> AnalysisFinishedAsync(
        Guid conversationId,
        Guid ownerUserId,
        Guid operationId,
        Guid divisionId,
        int projectCount,
        CancellationToken cancellationToken)
    {
        var divisionName = await dbContext.Divisions
            .AsNoTracking()
            .Where(division => division.Id == divisionId)
            .Select(division => division.Name)
            .FirstOrDefaultAsync(cancellationToken);
        // The division's name is data people entered: escaped, it shows as it is, without Markdown.
        var analysis = string.IsNullOrWhiteSpace(divisionName)
            ? "The portfolio analysis"
            : $"The portfolio analysis of **{NhAssistantMarkdown.Escape(divisionName)}**";
        var message = new NhAssistantApplicationMessage(string.Create(
            CultureInfo.InvariantCulture,
            $"{analysis} finished with **{projectCount} projects**. Open it to see the outcome per project."))
        {
            // The conversation id came from the person's client.
            Access = NhAssistantApplicationMessageAccess.ReadableBy(new NhAssistantConversationPerson(ownerUserId.ToString())),
            Title = "Portfolio analysis finished",
            SenderName = "Portfolio analysis",
            Link = new NhAssistantApplicationMessageLink($"/background-operations/{operationId}", "Open the analysis"),
            Notify = true,
            IdempotencyKey = "portfolio-analysis:" + operationId.ToString("N")
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
