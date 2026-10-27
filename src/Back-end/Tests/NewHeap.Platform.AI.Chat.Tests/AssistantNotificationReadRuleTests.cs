using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Notifications;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Web Push and in-app notifications about a conversation follow its read rule when they are sent:
/// a participant row that a role on the thread's subject once created reaches nobody after the role
/// ended, a chat participant who no longer passes the agent's policy hears nothing, and refreshing the
/// subject's members removes such stale rows while invitations stay. On both relational providers.
/// </summary>
public sealed class AssistantNotificationReadRuleTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string TenantA = "tenant-a";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Notifications_of_a_thread_reach_only_people_whose_role_on_the_subject_still_holds(AssistantTestProvider provider)
    {
        var host = await NotificationHost.StartAsync(database, provider);
        await using var app = host.App;
        var alice = Guid.NewGuid().ToString("D");
        var bob = Guid.NewGuid().ToString("D");
        host.Projects.Roles[alice] = NhAiSubjectRole.Participant;
        var thread = await host.CreateThreadAsync();
        // Both opened the thread while they had a role; bob's role ended since.
        await host.AddParticipantAsync(thread.ConversationId, alice, NhAssistantParticipantSources.Subject);
        await host.AddParticipantAsync(thread.ConversationId, bob, NhAssistantParticipantSources.Subject);
        await host.SubscribeAsync(alice);
        await host.SubscribeAsync(bob);

        await host.TurnEndedAsync(thread.ConversationId, alice);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var conversation = (await scope.ServiceProvider.GetRequiredService<INhAssistantStore>()
                .FindShareableConversationAsync(thread.ConversationId, CancellationToken.None))!;
            await scope.ServiceProvider.GetRequiredService<INhAssistantNotifier>().ApplicationMessageAsync(thread.ConversationId, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<NhAssistantInAppApplicationMessageNotifier>().DeliverAsync(conversation, CancellationToken.None);
        }

        Assert.Equal(["turn-completed", "application-message"], host.Push.KindsFor(alice));
        Assert.Empty(host.Push.KindsFor(bob));
        Assert.Equal([Guid.Parse(alice)], host.Inbox.Requests.Select(request => request.UserId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Notifications_of_a_chat_skip_a_participant_who_no_longer_passes_the_agents_policy(AssistantTestProvider provider)
    {
        var host = await NotificationHost.StartAsync(database, provider);
        await using var app = host.App;
        var owner = Guid.NewGuid().ToString("D");
        var pat = Guid.NewGuid().ToString("D");
        var quinn = Guid.NewGuid().ToString("D");
        host.People.Managers[pat] = true;
        var chat = await host.CreateChatAsync(owner, "project-manager");
        await host.AddParticipantAsync(chat, pat, NhAssistantParticipantSources.Link);
        // The application revoked the agent's permission of the second participant after the join.
        await host.AddParticipantAsync(chat, quinn, NhAssistantParticipantSources.Link);
        foreach (var person in new[] { owner, pat, quinn })
        {
            await host.SubscribeAsync(person);
        }

        await host.TurnEndedAsync(chat, owner);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INhAssistantNotifier>().InvitedAsync(chat, quinn, "Owner", CancellationToken.None);
        }

        Assert.Equal(["turn-completed"], host.Push.KindsFor(owner));
        Assert.Equal(["turn-completed-by-other"], host.Push.KindsFor(pat));
        Assert.Empty(host.Push.KindsFor(quinn));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Refreshing_a_subject_removes_the_rows_of_people_whose_role_ended_and_keeps_invitations(AssistantTestProvider provider)
    {
        var host = await NotificationHost.StartAsync(database, provider);
        await using var app = host.App;
        var alice = Guid.NewGuid().ToString("D");
        var bob = Guid.NewGuid().ToString("D");
        var dana = Guid.NewGuid().ToString("D");
        host.Projects.Roles[alice] = NhAiSubjectRole.Participant;
        host.Projects.Roles[bob] = NhAiSubjectRole.Participant;
        var thread = await host.CreateThreadAsync();
        await host.AddParticipantAsync(thread.ConversationId, alice, NhAssistantParticipantSources.Subject);
        await host.AddParticipantAsync(thread.ConversationId, bob, NhAssistantParticipantSources.Subject);
        // Dana has no role; she was invited to the thread.
        await host.AddParticipantAsync(thread.ConversationId, dana, NhAssistantParticipantSources.Invitation);

        host.Projects.Roles.TryRemove(bob, out _);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAiSubjectAccess>()
                .RefreshAsync(new NhAiSubjectRef("project", host.ProjectId))).Success);
        }

        var participants = await host.ParticipantsAsync(thread.ConversationId);
        Assert.Equal(
            new[] { (alice, NhAssistantParticipantSources.Subject), (dana, NhAssistantParticipantSources.Invitation) }.Order(),
            participants.Select(participant => (participant.ActorId, participant.JoinedVia)).Order());
        Assert.DoesNotContain(participants, participant => participant.ActorId == bob);
    }

    /// <summary>
    /// A host with Web Push to a recording push service, the application's inbox, one project of
    /// <see cref="TenantA"/> and the people the principal factory rebuilds.
    /// </summary>
    internal sealed class NotificationHost
    {
        private NotificationHost(AssistantWebApplication app, TwoTenantProjects projects, LiveReadPeople people, RecordingPushService push, RecordingInbox inbox, string projectId)
        {
            App = app;
            Projects = projects;
            People = people;
            Push = push;
            Inbox = inbox;
            ProjectId = projectId;
        }

        public AssistantWebApplication App { get; }

        public TwoTenantProjects Projects { get; }

        public LiveReadPeople People { get; }

        public RecordingPushService Push { get; }

        public RecordingInbox Inbox { get; }

        public string ProjectId { get; }

        public static async Task<NotificationHost> StartAsync(
            AssistantDatabaseFixture database,
            AssistantTestProvider provider,
            Action<IServiceCollection>? configure = null)
        {
            var projects = new TwoTenantProjects();
            var projectId = Guid.NewGuid().ToString("D");
            projects.Tenants[projectId] = TenantA;
            var people = new LiveReadPeople();
            var push = new RecordingPushService();
            var inbox = new RecordingInbox();
            var (publicKey, privateKey) = NhAssistantWebPushKeys.Generate();
            var app = await AssistantWebApplication.StartAsync(
                database,
                new NhAiScriptedChatClient(),
                configure: services =>
                {
                    services.AddSingleton(projects);
                    services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(people));
                    services.AddSingleton<INhUserNotificationSink>(inbox);
                    services.AddHttpClient(NhAssistantWebPushNotifier.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => push);
                    configure?.Invoke(services);
                },
                assistantBuilder: assistant => assistant
                    .AddSubjectType<TwoTenantProjectResolver>()
                    .ConfigurePush(options =>
                    {
                        options.PublicKey = publicKey;
                        options.PrivateKey = privateKey;
                        options.Subject = "mailto:assistant@example.com";
                        options.MinimumTurnDuration = TimeSpan.Zero;
                    }),
                provider: provider);
            return new NotificationHost(app, projects, people, push, inbox, projectId);
        }

        public async Task<AssistantThread> CreateThreadAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var info = await services.GetRequiredService<NhAssistantSubjectAccessService>()
                .DescribeAsync(new NhAiSubjectRef("project", ProjectId), CancellationToken.None);
            var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync("project-assistant", includeDisabled: false, CancellationToken.None))!;
            return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info!, CancellationToken.None)).Data!;
        }

        public async Task<Guid> CreateChatAsync(string ownerActorId, string agentId)
        {
            var now = DateTimeOffset.UtcNow;
            var conversation = new AssistantConversation
            {
                Id = Guid.NewGuid(),
                Kind = NhAssistantConversationKinds.Chat,
                OwnerActorId = ownerActorId,
                OwnerDisplayName = "Owner",
                AgentId = agentId,
                AgentVersion = 1,
                Title = "Quarterly plan",
                Status = NhAssistantConversationStatuses.Idle,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyStamp = Guid.NewGuid()
            };
            await using var scope = App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().AddConversationAsync(conversation, CancellationToken.None);
            return conversation.Id;
        }

        public async Task AddParticipantAsync(Guid conversationId, string actorId, string joinedVia)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var added = await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().TryAddParticipantAsync(
                new AssistantConversationParticipant
                {
                    ConversationId = conversationId,
                    ActorId = actorId,
                    DisplayName = "Person " + actorId[..4],
                    JoinedVia = joinedVia,
                    JoinedAt = DateTimeOffset.UtcNow
                },
                int.MaxValue,
                CancellationToken.None);
            Assert.Equal(NhAssistantParticipantAddResult.Added, added);
        }

        public async Task<IReadOnlyList<AssistantConversationParticipant>> ParticipantsAsync(Guid conversationId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().GetParticipantsAsync(conversationId, CancellationToken.None);
        }

        /// <summary>A browser of <paramref name="actorId"/> subscribes to push at its own endpoint.</summary>
        public async Task SubscribeAsync(string actorId)
        {
            var (key, auth) = Push.AddBrowser(actorId);
            await using var scope = App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<NhAssistantNotificationStore>()
                .UpsertSubscriptionAsync(actorId, RecordingPushService.EndpointOf(actorId), key, auth, "en", 5, CancellationToken.None);
        }

        /// <summary>A long turn of <paramref name="actorId"/> completed.</summary>
        public async Task TurnEndedAsync(Guid conversationId, string actorId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<INhAssistantNotifier>().TurnEndedAsync(
                new NhAssistantTurnOutcome(conversationId, Guid.NewGuid(), actorId, NhAssistantTurnStatuses.Completed, TimeSpan.FromSeconds(30)),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// A push service that accepts every notification and keeps what each browser received. Each
    /// browser decrypts its own notifications with the keys it subscribed with, so a test reads the
    /// kinds a person was told about.
    /// </summary>
    internal sealed class RecordingPushService : HttpMessageHandler
    {
        private const string EndpointPrefix = "https://fcm.googleapis.com/fcm/send/";

        private readonly ConcurrentQueue<(string Endpoint, byte[] Body)> _deliveries = new();
        private readonly ConcurrentDictionary<string, (ECDiffieHellman Browser, byte[] PublicKey, byte[] AuthSecret)> _browsers = new(StringComparer.Ordinal);

        public static string EndpointOf(string actorId)
        {
            return EndpointPrefix + actorId;
        }

        /// <summary>A new browser of <paramref name="actorId"/>: its public key and authentication secret.</summary>
        public (string PublicKey, string AuthSecret) AddBrowser(string actorId)
        {
            var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = NhAssistantWebPushKeys.UncompressedPoint(browser.ExportParameters(false).Q);
            var authSecret = RandomNumberGenerator.GetBytes(16);
            _browsers[EndpointOf(actorId)] = (browser, publicKey, authSecret);
            return (NhAssistantBase64Url.Encode(publicKey), NhAssistantBase64Url.Encode(authSecret));
        }

        /// <summary>The kinds of the notifications the browser of <paramref name="actorId"/> received, in order.</summary>
        public IReadOnlyList<string?> KindsFor(string actorId)
        {
            var endpoint = EndpointOf(actorId);
            var browser = _browsers[endpoint];
            return _deliveries
                .Where(delivery => delivery.Endpoint == endpoint)
                .Select(delivery => JsonDocument.Parse(AssistantWebPushTests.Decrypt(delivery.Body, browser.Browser, browser.PublicKey, browser.AuthSecret))
                    .RootElement.GetProperty("kind").GetString())
                .ToArray();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _deliveries.Enqueue((request.RequestUri!.ToString(), await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    /// <summary>The application's inbox: records every in-app notification.</summary>
    internal sealed class RecordingInbox : INhUserNotificationSink
    {
        private readonly ConcurrentQueue<NhUserNotificationRequest> _requests = new();

        public IReadOnlyList<NhUserNotificationRequest> Requests => _requests.ToArray();

        public Task<TaskResult<NhUserNotificationReceipt>> SendAsync(NhUserNotificationRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(request);
            return Task.FromResult(TaskResult<NhUserNotificationReceipt>.Succeeded(new NhUserNotificationReceipt(Guid.NewGuid())));
        }
    }
}
