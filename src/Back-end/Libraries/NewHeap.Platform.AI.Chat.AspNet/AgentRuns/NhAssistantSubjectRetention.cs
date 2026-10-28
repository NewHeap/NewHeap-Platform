using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Forgets a subject: its active runs are cancelled through their background operations, then
/// its threads and everything in them are deleted, in the scope the application names, and the
/// application's file retention forgets the uploads of each deleted thread (FB24). People's chats about
/// the subject (FB25) are deleted in the same scope too, with their uploads, because they hold what the
/// model said about it and no longer open once the subject is gone.
/// </summary>
internal sealed class NhAssistantSubjectRetention(
    NhAssistantRunStore runs,
    NhAssistantRunCancellation cancellation,
    NhAssistantFileRetentionCalls fileRetention,
    ILogger<NhAssistantSubjectRetention> logger) : INhAiSubjectRetention
{
    internal const string RetentionActorId = "system:subject-retention";

    [Obsolete("Forgets the subject in every tenant and division. Pass the scope key its resolver described before the deletion.")]
    public async Task<TaskResult<int>> ForgetSubjectAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var threads = await runs.FindThreadsBySubjectAsync(subject, cancellationToken);
        var chats = await runs.FindChatsAboutSubjectAsync(null, subject, cancellationToken);
        return await ForgetAsync(subject, threads, chats, cancellationToken);
    }

    public async Task<TaskResult<int>> ForgetSubjectAsync(
        NhAiSubjectRef subject,
        string scopeKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);

        // Only the forgotten subject's own scope: an equal id of another tenant or division is
        // another subject, whose threads, runs and chats stay.
        var threads = await runs.FindThreadsForSubjectAsync(scopeKey, subject, cancellationToken);
        var chats = await runs.FindChatsAboutSubjectAsync(scopeKey, subject, cancellationToken);
        return await ForgetAsync(subject, threads, chats, cancellationToken);
    }

    private async Task<TaskResult<int>> ForgetAsync(
        NhAiSubjectRef subject,
        IReadOnlyList<AssistantThread> threads,
        IReadOnlyList<AssistantConversation> chats,
        CancellationToken cancellationToken)
    {
        foreach (var thread in threads)
        {
            foreach (var run in await runs.GetOpenRunsAsync(thread.ConversationId, cancellationToken))
            {
                var owner = NhAssistantAgentTriggers.OwnerActor(thread, run.AccountableOwnerId);
                await cancellation.CancelAsync(run, RetentionActorId, owner, cancellationToken);
            }

            await runs.DeleteThreadAsync(thread, cancellationToken);

            // The thread is deleted: its uploads go with it, best effort, and a failure never brings it back.
            await fileRetention.ForgetConversationAsync(
                new NhAssistantForgottenConversation(thread.ConversationId, thread.AgentId, NhAssistantFileRetentionReasons.SubjectForgotten)
                {
                    TenantId = thread.TenantId,
                    Subject = subject
                },
                cancellationToken);
        }

        foreach (var chat in chats)
        {
            await runs.DeleteChatAsync(chat.Id, cancellationToken);
            await fileRetention.ForgetConversationAsync(
                new NhAssistantForgottenConversation(chat.Id, chat.AgentId, NhAssistantFileRetentionReasons.SubjectForgotten)
                {
                    TenantId = chat.TenantId,
                    Subject = subject
                },
                cancellationToken);
        }

        if (threads.Count > 0 || chats.Count > 0)
        {
            logger.LogInformation(
                "Deleted {ThreadCount} agent threads and {ChatCount} chats of a forgotten subject of type {SubjectType}.",
                threads.Count,
                chats.Count,
                subject.Type);
        }

        return TaskResult<int>.Succeeded(threads.Count);
    }
}
