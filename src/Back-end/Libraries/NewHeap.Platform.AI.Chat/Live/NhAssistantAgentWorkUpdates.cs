using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Live;

/// <summary>
/// Tells the people of an agent thread that its runs, proposals or feedback changed: the thread's
/// participants, everyone in the subject's member snapshot, so an approval inbox refreshes before
/// its owner ever opened the thread, and the person who started the run while they still have a
/// role on the subject. The first update of a thread fills a snapshot that was never filled. The
/// update carries no content; access to what changed is checked again when clients reload. The whole
/// application lists no members, so the work of its agents also reaches the people who watch it, such
/// as its approvers while their inbox is shown (<see cref="NhAssistantWatchedWork"/>).
/// </summary>
internal sealed class NhAssistantAgentWorkUpdates(
    NhAssistantLivePublisher live,
    NhAssistantRunStore runs,
    NhAssistantSubjectAccessService subjects,
    ILogger<NhAssistantAgentWorkUpdates> logger)
{
    public async ValueTask PublishAsync(
        Guid conversationId,
        string reason,
        Guid? approvalId = null,
        Guid? runId = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<string> people;
        AssistantThread? thread;
        try
        {
            thread = await runs.FindThreadByConversationAsync(conversationId, cancellationToken);
            if (thread is null)
            {
                return;
            }

            // A shadow thread belongs to administrators: only its own audience hears about it.
            people = thread.Shadow
                ? []
                : await FindSubjectPeopleAsync(thread, runId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Agent work update for conversation {ConversationId} was not published ({ExceptionType}).",
                conversationId,
                exception.GetType().Name);
            return;
        }

        var change = new NhAssistantLiveAgentWorkChanged(conversationId, reason, approvalId, runId, thread.SubjectType, thread.SubjectId);
        await live.PublishWithAsync(people, change, cancellationToken);
        if (!thread.Shadow && string.Equals(thread.SubjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal))
        {
            await live.PublishToWatchersAsync(new NhAssistantWatchedWork(thread.AgentId, thread.SubjectId), change, cancellationToken);
        }
    }

    /// <summary>
    /// The people on the thread's subject who hear about its work besides the thread's own
    /// audience: the member snapshot and the run's initiator.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> FindSubjectPeopleAsync(
        AssistantThread thread,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
        if (thread.MembersRefreshedAt is null)
        {
            // A thread that a person started by hand, or whose runs proposed nothing yet, has no
            // member snapshot: its first update fills it, as its first notification would.
            await RefreshMembersAsync(thread, subject, cancellationToken);
        }

        var people = (await runs.GetMembersAsync(thread.ScopeKey, subject, cancellationToken))
            .Select(member => member.ActorId)
            .ToHashSet(StringComparer.Ordinal);

        if (runId is { } id
            && await FindInitiatorAsync(thread, subject, id, people, cancellationToken) is { } initiator)
        {
            people.Add(initiator);
        }

        return people;
    }

    /// <summary>
    /// Fills the subject's member snapshot. The snapshot feeds listings, names, notifications and
    /// live updates, never access, so a refresh that fails costs the update nothing.
    /// </summary>
    private async Task RefreshMembersAsync(
        AssistantThread thread,
        NhAiSubjectRef subject,
        CancellationToken cancellationToken)
    {
        try
        {
            var refreshed = await subjects.RefreshAsync(subject, cancellationToken);
            if (!refreshed.Success)
            {
                logger.LogInformation(
                    "The member snapshot of subject type {SubjectType} could not be filled for an agent work update of conversation {ConversationId}.",
                    thread.SubjectType,
                    thread.ConversationId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "The member snapshot of subject type {SubjectType} was not filled for an agent work update of conversation {ConversationId} ({ExceptionType}).",
                thread.SubjectType,
                thread.ConversationId,
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Returns the person who started the run when the snapshot does not name them and the
    /// resolver still gives them a role on the subject, for example through a permission instead
    /// of a membership. Agents, shadow runs and runs of another thread name nobody, and a failing
    /// resolver costs the update nothing.
    /// </summary>
    private async Task<string?> FindInitiatorAsync(
        AssistantThread thread,
        NhAiSubjectRef subject,
        Guid runId,
        IReadOnlySet<string> people,
        CancellationToken cancellationToken)
    {
        var run = await runs.FindRunAsync(runId, cancellationToken);
        if (run is not { InitiatorActorId: { } initiator, Shadow: false }
            || run.ConversationId != thread.ConversationId
            || string.IsNullOrWhiteSpace(initiator)
            || initiator.StartsWith(NhAssistantAgentEvents.AgentActorPrefix, StringComparison.Ordinal)
            || people.Contains(initiator))
        {
            return null;
        }

        try
        {
            var access = await subjects.ResolveAsync(subject, initiator, cancellationToken);
            return access.Role == NhAiSubjectRole.None ? null : initiator;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "The initiator of run {RunId} was not checked for an agent work update ({ExceptionType}).",
                runId,
                exception.GetType().Name);
            return null;
        }
    }
}
