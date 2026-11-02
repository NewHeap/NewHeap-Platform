using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Collaboration;

/// <summary>
/// The assistant's part of <see cref="INhRetention"/>: when the application forgets a user, for example
/// when NewHeap Identity deletes them, the assistant removes what reaches or names that person outside
/// a conversation's content: their Web Push subscriptions, their notification choices, the notices of
/// missing MCP connections they were sent, their deliveries of alerts, and their participant rows in chats and agent threads, so
/// no browser of theirs is notified again and no conversation lists them. The person's actor id is the
/// user id as <see cref="NhActorRef.ForUser"/> writes it. Conversations the person owns, their messages
/// and their personal MCP connections follow their own retention. Running it again changes nothing. The
/// assistant's storage is its own, so its rows are removed in a transaction of their own, before the
/// application's transaction that deletes the user commits.
/// </summary>
internal sealed class NhAssistantRetentionParticipant(
    NhAssistantDbContextFactory contextFactory,
    NhAssistantConversationAudience audience) : INhRetentionParticipant
{
    public int Order => 0;

    public async Task<TaskResult> ForgetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var actorId = NhActorRef.ForUser(userId).Id;
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        await context.PushSubscriptions
            .Where(subscription => subscription.ActorId == actorId)
            .ExecuteDeleteAsync(cancellationToken);
        await context.NotificationSettings
            .Where(setting => setting.ActorId == actorId)
            .ExecuteDeleteAsync(cancellationToken);
        // FB87-6: the person's choices per kind of notification.
        await context.NotificationPreferences
            .Where(preference => preference.ActorId == actorId)
            .ExecuteDeleteAsync(cancellationToken);
        await context.McpConnectionNotices
            .Where(notice => notice.PersonActorId == actorId)
            .ExecuteDeleteAsync(cancellationToken);
        // FB87: the person's deliveries of alerts, which name them; the alerts stay for their other recipients.
        await NhAssistantAlertStore.ForgetRecipientAsync(context, actorId, cancellationToken);

        var conversations = await context.Participants
            .Where(participant => participant.ActorId == actorId)
            .Select(participant => participant.ConversationId)
            .ToListAsync(cancellationToken);
        await context.Participants
            .Where(participant => participant.ActorId == actorId)
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // The live audience of those conversations no longer names the person in this process.
        foreach (var conversationId in conversations)
        {
            audience.Invalidate(conversationId);
        }

        return TaskResult.Succeeded();
    }

    public Task<TaskResult> ForgetDivisionAsync(Guid divisionId, CancellationToken cancellationToken)
    {
        return Task.FromResult(TaskResult.Succeeded());
    }
}
