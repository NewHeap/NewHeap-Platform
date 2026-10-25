using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Composition;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-326: which intake review a project's creation started. The intake reviewer's trigger takes the
/// <c>ProjectCreatedEvent</c> with its event id as dedupe key, so the application finds the review by
/// that id, as the outbox shows it, through <see cref="INhAssistantAgentRunLookup"/>: the run that took
/// the event, or the event while it still waits for one. A redelivered event finds the review of its
/// first delivery. The lookup checks no person's access; the maintenance page that calls it is for
/// operators who manage projects.
/// </summary>
public sealed class SampleIntakeReviewLookup(INhAssistantAgentRunLookup runs)
{
    public Task<NhAssistantTriggerEventRun?> FindAsync(Guid projectCreatedEventId, CancellationToken cancellationToken)
    {
        return runs.FindByEventAsync(
            SampleIntakeReviewerComposition.AgentId,
            SampleIntakeReviewerComposition.TriggerId,
            projectCreatedEventId.ToString("N"),
            cancellationToken: cancellationToken);
    }
}
