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
/// <para>
/// A second message then tells the assistant what to do with the finished analysis. It is
/// <see cref="NhAssistantApplicationMessage.Trusted"/>, so the model follows it as the application's
/// instruction, and therefore it is fixed text that the application wrote itself
/// (<see cref="AssistantInstruction"/>), without the division's name or any other data people
/// entered. People see it too, labelled as an instruction for the assistant.
/// </para>
/// </summary>
public sealed class ProjectAnalysisAssistantNotices(
    INhAssistantConversationMessages messages,
    SampleProjectManagementDbContext dbContext,
    ILogger<ProjectAnalysisAssistantNotices> logger)
{
    /// <summary>
    /// The application's instruction to the assistant after an analysis finished: fixed text, never
    /// people's data, because the model follows it.
    /// </summary>
    public const string AssistantInstruction =
        "When the person asks about this portfolio analysis, first offer a short summary of its outcome "
        + "and point to the analysis link in the message before this one for the outcome per project. "
        + "Start no new analysis unless the person asks for one.";

    /// <summary>
    /// Posts that the analysis <paramref name="operationId"/> of <paramref name="projectCount"/>
    /// projects of <paramref name="divisionId"/> finished into <paramref name="conversationId"/> for
    /// <paramref name="ownerUserId"/>, followed by the trusted <see cref="AssistantInstruction"/>, and
    /// returns the receipt of the first. An expected refusal, such as a conversation the person deleted
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
        // The conversation id came from the person's client.
        var access = NhAssistantApplicationMessageAccess.ReadableBy(new NhAssistantConversationPerson(ownerUserId.ToString()));
        var message = new NhAssistantApplicationMessage(string.Create(
            CultureInfo.InvariantCulture,
            $"{analysis} finished with **{projectCount} projects**. Open it to see the outcome per project."))
        {
            Access = access,
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
            return posted;
        }

        await PostInstructionAsync(conversationId, access, operationId, cancellationToken);
        return posted;
    }

    /// <summary>
    /// Posts the trusted <see cref="AssistantInstruction"/> after the analysis message, with an
    /// idempotency key of its own per operation and without a notification: the person was notified of
    /// the analysis already.
    /// </summary>
    private async Task PostInstructionAsync(
        Guid conversationId,
        NhAssistantApplicationMessageAccess access,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var instruction = new NhAssistantApplicationMessage(AssistantInstruction)
        {
            Access = access,
            SenderName = "Portfolio analysis",
            // Fixed text the application wrote itself; never put people's data into a trusted message.
            Trusted = true,
            IdempotencyKey = "portfolio-analysis-instruction:" + operationId.ToString("N")
        };

        var posted = await messages.PostAsync(conversationId, instruction, cancellationToken);
        if (!posted.Success)
        {
            logger.LogWarning(
                "The portfolio analysis {OperationId} could not give conversation {ConversationId} its instruction for the assistant ({Code}).",
                operationId,
                conversationId,
                posted.GetResultItems().FirstOrDefault()?.Name);
        }
    }
}
