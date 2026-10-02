using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Sharing, read state and push subscriptions on both relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantCollaborationPersistenceTests(AssistantDatabaseFixture database)
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Participants_reach_only_conversations_of_their_tenant_with_their_own_read_state(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateMigratedStorageAsync(provider);
        var store = services.GetRequiredService<INhAssistantStore>();
        var shared = await AddConversationAsync(store, "owner-1", "tenant-a", DateTimeOffset.UtcNow.AddMinutes(-2));
        var own = await AddConversationAsync(store, "member-1", "tenant-a", DateTimeOffset.UtcNow.AddMinutes(-1));
        var foreign = await AddConversationAsync(store, "owner-2", "tenant-b", DateTimeOffset.UtcNow);
        await AddMessagesAsync(store, shared.Id, 3);
        await AddMessagesAsync(store, own.Id, 2);
        Assert.Equal(NhAssistantParticipantAddResult.Added, await store.TryAddParticipantAsync(Participant(shared.Id, "member-1", 1), 5, CancellationToken.None));
        // A stale row in a conversation of another tenant never grants access.
        Assert.Equal(NhAssistantParticipantAddResult.Added, await store.TryAddParticipantAsync(Participant(foreign.Id, "member-1", 0), 5, CancellationToken.None));

        var (items, total) = await store.ListConversationsAsync("member-1", "tenant-a", 1, 20, CancellationToken.None);

        Assert.Equal(2, total);
        Assert.Equal([own.Id, shared.Id], items.Select(item => item.Conversation.Id));
        var ownItem = items[0];
        Assert.Equal(NhAssistantParticipantRoles.Owner, ownItem.Role);
        // An owner whose read state was never tracked has read everything.
        Assert.Equal(2, ownItem.LastMessageSequence);
        Assert.Equal(2, ownItem.LastReadSequence);
        var sharedItem = items[1];
        Assert.Equal(NhAssistantParticipantRoles.Participant, sharedItem.Role);
        Assert.Equal(1, sharedItem.ParticipantCount);
        Assert.Equal(3, sharedItem.LastMessageSequence);
        Assert.Equal(1, sharedItem.LastReadSequence);

        Assert.NotNull(await store.FindAccessAsync(shared.Id, "member-1", "tenant-a", CancellationToken.None));
        Assert.Null(await store.FindAccessAsync(shared.Id, "member-1", "tenant-b", CancellationToken.None));
        Assert.Null(await store.FindAccessAsync(shared.Id, "member-1", null, CancellationToken.None));
        Assert.Null(await store.FindAccessAsync(foreign.Id, "member-1", "tenant-a", CancellationToken.None));
        Assert.Null(await store.FindAccessAsync(shared.Id, "stranger", "tenant-a", CancellationToken.None));
        var ownerAccess = await store.FindAccessAsync(shared.Id, "owner-1", "tenant-other", CancellationToken.None);
        Assert.True(ownerAccess!.IsOwner);

        var state = await store.GetConversationStateAsync(shared.Id, CancellationToken.None);
        Assert.Equal(3, state!.LastMessageSequence);
        Assert.Equal(1, state.ParticipantCount);
        Assert.Null(state.ActiveActorId);
        Assert.Equal(["owner-1", "member-1"], await store.GetAudienceAsync(shared.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Read_positions_only_move_forward_and_stop_at_the_latest_message(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateMigratedStorageAsync(provider);
        var store = services.GetRequiredService<INhAssistantStore>();
        var conversation = await AddConversationAsync(store, "owner-1", null, DateTimeOffset.UtcNow);
        await AddMessagesAsync(store, conversation.Id, 4);
        await store.TryAddParticipantAsync(Participant(conversation.Id, "member-1", 0), 5, CancellationToken.None);
        var owner = (await store.FindAccessAsync(conversation.Id, "owner-1", null, CancellationToken.None))!;
        var member = (await store.FindAccessAsync(conversation.Id, "member-1", null, CancellationToken.None))!;

        Assert.Equal(2, await store.MarkReadAsync(owner, 2, CancellationToken.None));
        Assert.Null(await store.MarkReadAsync(owner, 1, CancellationToken.None));
        Assert.Equal(4, await store.MarkReadAsync(owner, int.MaxValue, CancellationToken.None));
        Assert.Equal(3, await store.MarkReadAsync(member, 3, CancellationToken.None));
        Assert.Null(await store.MarkReadAsync(member, 3, CancellationToken.None));

        var reloadedOwner = (await store.FindAccessAsync(conversation.Id, "owner-1", null, CancellationToken.None))!;
        var reloadedMember = (await store.FindAccessAsync(conversation.Id, "member-1", null, CancellationToken.None))!;
        Assert.Equal(4, reloadedOwner.LastReadSequence);
        Assert.Equal(3, reloadedMember.LastReadSequence);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Membership_respects_the_limit_survives_concurrent_joins_and_ends_with_the_conversation(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateMigratedStorageAsync(provider);
        var store = services.GetRequiredService<INhAssistantStore>();
        var conversation = await AddConversationAsync(store, "owner-1", null, DateTimeOffset.UtcNow);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            store.TryAddParticipantAsync(Participant(conversation.Id, "member-1", 0), 5, CancellationToken.None)));
        Assert.Equal(1, concurrent.Count(result => result == NhAssistantParticipantAddResult.Added));
        Assert.All(
            concurrent.Where(result => result != NhAssistantParticipantAddResult.Added),
            result => Assert.Equal(NhAssistantParticipantAddResult.AlreadyParticipant, result));
        Assert.Equal(
            NhAssistantParticipantAddResult.LimitReached,
            await store.TryAddParticipantAsync(Participant(conversation.Id, "member-2", 0), 1, CancellationToken.None));

        Assert.True(await store.SetProtectedShareTokenAsync(conversation.Id, "owner-1", "protected", CancellationToken.None));
        Assert.False(await store.SetProtectedShareTokenAsync(conversation.Id, "member-1", "stolen", CancellationToken.None));
        Assert.Equal("protected", (await store.FindShareableConversationAsync(conversation.Id, CancellationToken.None))!.ProtectedShareToken);
        var member = (await store.FindAccessAsync(conversation.Id, "member-1", null, CancellationToken.None))!;
        await store.UpdateDisplayNameAsync(member, "Pat", CancellationToken.None);
        Assert.Equal("Pat", Assert.Single(await store.GetParticipantsAsync(conversation.Id, CancellationToken.None)).DisplayName);

        Assert.True(await store.TryBeginTurnAsync(
            conversation.Id,
            "member-1",
            [NhAssistantConversationStatuses.Idle],
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            CancellationToken.None));
        Assert.Equal("member-1", (await store.GetConversationStateAsync(conversation.Id, CancellationToken.None))!.ActiveActorId);
        Assert.False(await store.TryArchiveConversationAsync(conversation.Id, "owner-1", CancellationToken.None));

        Assert.True(await store.TryRemoveParticipantAsync(conversation.Id, "member-1", CancellationToken.None));
        Assert.False(await store.TryRemoveParticipantAsync(conversation.Id, "member-1", CancellationToken.None));
        Assert.Null(await store.FindAccessAsync(conversation.Id, "member-1", null, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Push_subscriptions_follow_the_signed_in_actor_the_limit_and_the_opt_out(
        AssistantTestProvider provider)
    {
        await using var services = await database.CreateMigratedStorageAsync(
            provider,
            configure: collection => collection.AddSingleton<NhAssistantNotificationStore>());
        var notifications = services.GetRequiredService<NhAssistantNotificationStore>();
        const string shared = "https://fcm.googleapis.com/fcm/send/shared-browser";

        await notifications.UpsertSubscriptionAsync("actor-1", shared, "key", "auth", "en", 2, CancellationToken.None);
        await notifications.UpsertSubscriptionAsync("actor-1", shared + "-2", "key", "auth", "en", 2, CancellationToken.None);
        await notifications.UpsertSubscriptionAsync("actor-1", shared + "-3", "key", "auth", "nl", 2, CancellationToken.None);
        var subscriptions = await notifications.GetDeliverableSubscriptionsAsync(["actor-1"], CancellationToken.None);
        Assert.Equal(2, subscriptions.Count);
        Assert.DoesNotContain(subscriptions, subscription => subscription.Endpoint == shared);

        // The same browser signs in as another actor: the subscription moves.
        await notifications.UpsertSubscriptionAsync("actor-2", shared + "-2", "key-2", "auth-2", "en", 2, CancellationToken.None);
        Assert.Single(await notifications.GetDeliverableSubscriptionsAsync(["actor-1"], CancellationToken.None));
        var moved = Assert.Single(await notifications.GetDeliverableSubscriptionsAsync(["actor-2"], CancellationToken.None));
        Assert.Equal("key-2", moved.P256dh);

        Assert.True(await notifications.IsPushEnabledAsync("actor-2", CancellationToken.None));
        await notifications.SetPushEnabledAsync("actor-2", false, CancellationToken.None);
        await notifications.SetPushEnabledAsync("actor-2", false, CancellationToken.None);
        Assert.False(await notifications.IsPushEnabledAsync("actor-2", CancellationToken.None));
        Assert.Single(await notifications.GetDeliverableSubscriptionsAsync(["actor-1", "actor-2"], CancellationToken.None));

        Assert.False(await notifications.DeleteSubscriptionAsync("actor-1", shared + "-2", CancellationToken.None));
        Assert.True(await notifications.DeleteSubscriptionAsync("actor-2", shared + "-2", CancellationToken.None));
        await notifications.DeleteSubscriptionsAsync([moved.Id], CancellationToken.None);
    }

    private static async Task<AssistantConversation> AddConversationAsync(
        INhAssistantStore store,
        string owner,
        string? tenant,
        DateTimeOffset updatedAt)
    {
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            OwnerActorId = owner,
            TenantId = tenant,
            AgentId = "project-assistant",
            AgentVersion = 1,
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await store.AddConversationAsync(conversation, CancellationToken.None);
        return conversation;
    }

    private static async Task AddMessagesAsync(INhAssistantStore store, Guid conversationId, int count)
    {
        for (var index = 0; index < count; index++)
        {
            await store.AddMessageAsync(
                new AssistantMessage
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    Role = index % 2 == 0 ? NhAssistantMessageRoles.User : NhAssistantMessageRoles.Assistant,
                    PartsJson = "[]",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                CancellationToken.None);
        }
    }

    private static AssistantConversationParticipant Participant(Guid conversationId, string actorId, int lastRead)
    {
        return new AssistantConversationParticipant
        {
            ConversationId = conversationId,
            ActorId = actorId,
            DisplayName = actorId,
            JoinedVia = NhAssistantParticipantSources.Link,
            JoinedAt = DateTimeOffset.UtcNow,
            LastReadSequence = lastRead
        };
    }
}
