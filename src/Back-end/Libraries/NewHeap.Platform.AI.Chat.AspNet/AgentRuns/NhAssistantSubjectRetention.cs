using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Forgets a subject: its active runs are cancelled through their background operations, then
/// its threads and everything in them are deleted.
/// </summary>
internal sealed class NhAssistantSubjectRetention(
    NhAssistantRunStore runs,
    NhAssistantRunCancellation cancellation,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantSubjectRetention> logger) : INhAiSubjectRetention
{
    internal const string RetentionActorId = "system:subject-retention";

    public async Task<TaskResult<int>> ForgetSubjectAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var threads = await runs.FindThreadsBySubjectAsync(subject, cancellationToken);
        foreach (var thread in threads)
        {
            var agent = registration.Agents.FirstOrDefault(item => string.Equals(item.Id, thread.AgentId, StringComparison.Ordinal));
            foreach (var run in await runs.GetOpenRunsAsync(thread.ConversationId, cancellationToken))
            {
                var owner = agent is null
                    ? NhBackgroundOperationActor.ForAgent(NhAssistantAgentEvents.AgentActorPrefix + thread.AgentId)
                    : NhAssistantAgentTriggers.OwnerActor(thread, run.AccountableOwnerId, agent);
                await cancellation.CancelAsync(run, RetentionActorId, owner, cancellationToken);
            }

            await runs.DeleteThreadAsync(thread, cancellationToken);
        }

        if (threads.Count > 0)
        {
            logger.LogInformation(
                "Deleted {ThreadCount} agent threads of a forgotten subject of type {SubjectType}.",
                threads.Count,
                subject.Type);
        }

        return TaskResult<int>.Succeeded(threads.Count);
    }
}
