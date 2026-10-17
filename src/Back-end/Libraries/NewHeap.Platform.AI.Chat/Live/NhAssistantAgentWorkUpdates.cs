using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Live;

/// <summary>
/// Tells the people of an agent thread that its runs, proposals or feedback changed: the thread's
/// participants and everyone in the subject's member snapshot, so an approval inbox refreshes before
/// its owner ever opened the thread. The update carries no content; access to what changed is
/// checked again when clients reload.
/// </summary>
internal sealed class NhAssistantAgentWorkUpdates(
    NhAssistantLivePublisher live,
    NhAssistantRunStore runs,
    ILogger<NhAssistantAgentWorkUpdates> logger)
{
    public async ValueTask PublishAsync(
        Guid conversationId,
        string reason,
        Guid? approvalId = null,
        Guid? runId = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> members;
        Entities.AssistantThread? thread;
        try
        {
            thread = await runs.FindThreadByConversationAsync(conversationId, cancellationToken);
            if (thread is null)
            {
                return;
            }

            // A shadow thread belongs to administrators: only its own audience hears about it.
            members = thread.Shadow
                ? []
                : (await runs.GetMembersAsync(
                    thread.ScopeKey,
                    new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
                    cancellationToken))
                .Select(member => member.ActorId)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Agent work update for conversation {ConversationId} was not published ({ExceptionType}).",
                conversationId,
                exception.GetType().Name);
            return;
        }

        await live.PublishWithAsync(
            members,
            new NhAssistantLiveAgentWorkChanged(conversationId, reason, approvalId, runId, thread.SubjectType, thread.SubjectId),
            cancellationToken);
    }
}
