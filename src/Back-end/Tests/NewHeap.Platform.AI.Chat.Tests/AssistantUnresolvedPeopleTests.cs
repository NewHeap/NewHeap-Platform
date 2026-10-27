using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.Common.Actors;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.AssistantNotificationReadRuleTests;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Deliveries stop for people the platform no longer resolves: a person whose account was deleted,
/// blocked or locked out, so that the application's principal factory rebuilds nobody, receives no
/// Web Push, in-app entry or agent notification, whatever conversation or policy names them. When the
/// application forgets a user, the assistant removes their push subscriptions, notification choice,
/// MCP connection notices and participant rows, and leaves everyone else's. On both relational
/// providers.
/// </summary>
public sealed class AssistantUnresolvedPeopleTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Notifications_skip_people_the_platform_no_longer_resolves(AssistantTestProvider provider)
    {
        var host = await NotificationHost.StartAsync(database, provider);
        await using var app = host.App;
        var owner = Guid.NewGuid().ToString("D");
        var pat = Guid.NewGuid().ToString("D");
        var quinn = Guid.NewGuid().ToString("D");
        var alice = Guid.NewGuid().ToString("D");
        var carol = Guid.NewGuid().ToString("D");
        var chat = await host.CreateChatAsync(owner, "project-assistant");
        await host.AddParticipantAsync(chat, pat, NhAssistantParticipantSources.Link);
        await host.AddParticipantAsync(chat, quinn, NhAssistantParticipantSources.Link);
        host.Projects.Roles[alice] = NhAiSubjectRole.Approver;
        host.Projects.Roles[carol] = NhAiSubjectRole.Approver;
        var thread = await host.CreateThreadAsync();
        foreach (var person in new[] { owner, pat, quinn, alice, carol })
        {
            await host.SubscribeAsync(person);
        }
        // Pat's account was deleted and Alice's locked out: the platform rebuilds neither of them.
        host.People.Unresolved[pat] = true;
        host.People.Unresolved[alice] = true;

        await host.TurnEndedAsync(chat, owner);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var conversation = (await services.GetRequiredService<INhAssistantStore>().FindShareableConversationAsync(chat, CancellationToken.None))!;
            await services.GetRequiredService<INhAssistantNotifier>().ApplicationMessageAsync(chat, CancellationToken.None);
            await services.GetRequiredService<NhAssistantInAppApplicationMessageNotifier>().DeliverAsync(conversation, CancellationToken.None);
            // The thread's approvers hear that a run failed.
            await services.GetRequiredService<NhAssistantAgentNotifier>().NotifyAsync(
                NhAssistantAgentNotificationKind.RunFailed,
                thread,
                Guid.NewGuid(),
                null,
                null,
                false,
                CancellationToken.None);
        }

        Assert.Equal(["turn-completed", "application-message"], host.Push.KindsFor(owner));
        Assert.Equal(["turn-completed-by-other", "application-message"], host.Push.KindsFor(quinn));
        Assert.Empty(host.Push.KindsFor(pat));
        Assert.Equal(["agent-run-failed"], host.Push.KindsFor(carol));
        Assert.Empty(host.Push.KindsFor(alice));
        Assert.Equal(
            new[] { Guid.Parse(owner), Guid.Parse(quinn) }.Order(),
            host.Inbox.Requests.Select(request => request.UserId).Order());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Forgetting_a_user_removes_their_push_subscriptions_settings_notices_and_participant_rows(AssistantTestProvider provider)
    {
        var host = await NotificationHost.StartAsync(database, provider);
        await using var app = host.App;
        var userId = Guid.NewGuid();
        var user = NhActorRef.ForUser(userId).Id;
        var colleague = Guid.NewGuid().ToString("D");
        var owner = Guid.NewGuid().ToString("D");
        var chat = await host.CreateChatAsync(owner, "project-assistant");
        host.Projects.Roles[user] = NhAiSubjectRole.Participant;
        var thread = await host.CreateThreadAsync();
        await using (var setup = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            setup.McpServers.Add(new AssistantMcpServer
            {
                Id = "planning",
                DisplayName = "Planning",
                Url = "https://mcp.example.test/mcp",
                AuthMode = NhAssistantMcpAuthModes.None
            });
            await setup.SaveChangesAsync();
        }
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var notifications = scope.ServiceProvider.GetRequiredService<NhAssistantNotificationStore>();
            foreach (var person in new[] { user, colleague })
            {
                await host.SubscribeAsync(person);
                await notifications.SetPushEnabledAsync(person, false, CancellationToken.None);
                var now = DateTimeOffset.UtcNow;
                Assert.True(await notifications.TryClaimMcpConnectionNoticeAsync("planning", person, "connection-missing", now, now.AddDays(-1), CancellationToken.None));
                await host.AddParticipantAsync(chat, person, NhAssistantParticipantSources.Link);
            }
        }
        await host.AddParticipantAsync(thread.ConversationId, user, NhAssistantParticipantSources.Subject);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.Contains(scope.ServiceProvider.GetServices<INhRetentionParticipant>(), participant => participant is NhAssistantRetentionParticipant);
            var retention = scope.ServiceProvider.GetRequiredService<INhRetention>();
            Assert.True((await retention.ForgetUserAsync(userId, CancellationToken.None)).Success);
            // Forgetting the same user again changes nothing.
            Assert.True((await retention.ForgetUserAsync(userId, CancellationToken.None)).Success);
        }

        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        foreach (var (person, remaining) in new[] { (user, 0), (colleague, 1) })
        {
            Assert.Equal(remaining, await context.PushSubscriptions.CountAsync(row => row.ActorId == person));
            Assert.Equal(remaining, await context.NotificationSettings.CountAsync(row => row.ActorId == person));
            Assert.Equal(remaining, await context.McpConnectionNotices.CountAsync(row => row.PersonActorId == person));
            Assert.Equal(remaining, await context.Participants.CountAsync(row => row.ActorId == person));
        }
        // The conversations themselves stay for their other people.
        Assert.Equal(2, await context.Conversations.CountAsync(row => row.Id == chat || row.Id == thread.ConversationId));
    }
}
