using NewHeap.Platform.AI.Chat;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Forwards feedback on rejected agent proposals to the application's analytics; the sample writes
/// a structured log entry. The feedback text is a person's words about a project, so it stays in
/// the assistant's store and the administrators' report instead of the log.
/// </summary>
public sealed class SampleProposalFeedbackLog(ILogger<SampleProposalFeedbackLog> logger) : INhAssistantProposalFeedbackSink
{
    public ValueTask RecordAsync(NhAssistantProposalFeedback feedback, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Agent {AgentId} version {AgentVersion} received {Category} ({Group}) feedback on a {ApprovalStatus} {ToolId} proposal for {SubjectType} {SubjectId}.",
            feedback.AgentId,
            feedback.AgentVersion,
            feedback.Category,
            feedback.Group,
            feedback.ApprovalStatus,
            feedback.ToolId,
            feedback.Subject.Type,
            feedback.Subject.Id);
        return ValueTask.CompletedTask;
    }
}
