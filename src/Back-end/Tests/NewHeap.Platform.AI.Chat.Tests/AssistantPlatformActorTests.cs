using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The assistant uses the platform's actor interfaces: names come from the directory when claims
/// carry none, and agent notifications can reach the application's in-app inbox.
/// </summary>
public sealed class AssistantPlatformActorTests
{
    [Fact]
    public async Task DisplayNamesFallBackToThePlatformDirectory()
    {
        var userId = Guid.NewGuid();
        var caller = new NhAiInvocationContext(userId.ToString("D"), "assistant", new Dictionary<string, string>());
        var withoutName = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var withName = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim("name", "Claims Name")], "test"));
        var directory = new Directory(new Dictionary<Guid, NhActorInfo>
        {
            [userId] = new(NhActorRef.ForUser(userId), "Directory Name")
        });
        var resolver = new NhAssistantClaimsDisplayNameResolver(
            new ServiceCollection().AddSingleton<INhActorDirectory>(directory).BuildServiceProvider());

        Assert.Equal("Claims Name", await resolver.GetDisplayNameAsync(withName, caller, CancellationToken.None));
        Assert.Equal("Directory Name", await resolver.GetDisplayNameAsync(withoutName, caller, CancellationToken.None));

        var withoutDirectory = new NhAssistantClaimsDisplayNameResolver(new ServiceCollection().BuildServiceProvider());
        Assert.Null(await withoutDirectory.GetDisplayNameAsync(withoutName, caller, CancellationToken.None));
    }

    [Fact]
    public async Task InAppAgentNotificationsGoThroughTheSinkInEachRecipientsLanguage()
    {
        var dutchUser = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var sink = new RecordingSink();
        var services = new ServiceCollection()
            .AddSingleton<INhUserNotificationSink>(sink)
            .AddSingleton<INhActorDirectory>(new Directory(new Dictionary<Guid, NhActorInfo>
            {
                [dutchUser] = new(NhActorRef.ForUser(dutchUser), "Dutch User") { Culture = "nl-NL" }
            }))
            .BuildServiceProvider();
        var channel = new NhAssistantInAppAgentNotificationChannel(
            services,
            NullLogger<NhAssistantInAppAgentNotificationChannel>.Instance);
        var conversationId = Guid.NewGuid();
        var context = new NhAssistantAgentNotificationContext(
            NhAssistantAgentNotificationKind.RunEndedWithProposals,
            "intake-reviewer",
            conversationId,
            new NhAiSubjectRef("project", "PRJ-1"),
            "Project PRJ-1",
            Guid.NewGuid(),
            null,
            null,
            [])
        {
            ProposalCount = 2
        };

        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            await channel.DeliverAsync(
                context,
                [dutchUser.ToString("D"), otherUser.ToString("D"), "assistant-agent:intake-reviewer"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        Assert.Equal(2, sink.Requests.Count);
        var dutch = Assert.Single(sink.Requests, request => request.UserId == dutchUser);
        Assert.Equal("Project PRJ-1", dutch.Title);
        Assert.Equal("2 voorstellen wachten op een besluit.", dutch.Message);
        Assert.Equal("assistant-agent", dutch.Category);
        Assert.Equal($"assistant-agent:{conversationId:N}", dutch.GroupKey);
        var english = Assert.Single(sink.Requests, request => request.UserId == otherUser);
        Assert.Equal("2 proposals wait for a decision.", english.Message);

        // Without a sink the channel delivers nothing and never fails the run.
        var withoutSink = new NhAssistantInAppAgentNotificationChannel(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<NhAssistantInAppAgentNotificationChannel>.Instance);
        await withoutSink.DeliverAsync(context, [dutchUser.ToString("D")]);
    }

    [Fact]
    public async Task InAppNotificationsAboutTheProposalsOfARunCountTheObjectsAndGroupPerRun()
    {
        // RN4, RN5: several objects count proposals and objects ("objects" without the subject types'
        // plural names), and the message about a run, its reminders and escalations group per run.
        var dutchUser = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var sink = new RecordingSink();
        var services = new ServiceCollection()
            .AddSingleton<INhUserNotificationSink>(sink)
            .AddSingleton<INhActorDirectory>(new Directory(new Dictionary<Guid, NhActorInfo>
            {
                [dutchUser] = new(NhActorRef.ForUser(dutchUser), "Dutch User") { Culture = "nl-NL" },
                [otherUser] = new(NhActorRef.ForUser(otherUser), "Other User") { Culture = "en-GB" }
            }))
            .BuildServiceProvider();
        var channel = new NhAssistantInAppAgentNotificationChannel(
            services,
            NullLogger<NhAssistantInAppAgentNotificationChannel>.Instance);
        var runId = Guid.NewGuid();
        var opened = Guid.NewGuid();
        var context = new NhAssistantAgentNotificationContext(
            NhAssistantAgentNotificationKind.RunProposalsWaiting,
            "project-follow-up",
            opened,
            new NhAiSubjectRef("project", "PRJ-1"),
            "Project PRJ-1",
            runId,
            null,
            null,
            [])
        {
            ProposalCount = 3,
            Objects =
            [
                new NhAssistantAgentNotificationObject(new NhAiSubjectRef("project", "PRJ-1"), "Project PRJ-1", opened, 2),
                new NhAssistantAgentNotificationObject(new NhAiSubjectRef("project", "PRJ-2"), "Project PRJ-2", Guid.NewGuid(), 1)
            ]
        };

        await channel.DeliverAsync(context, [dutchUser.ToString("D"), otherUser.ToString("D")]);
        await channel.DeliverAsync(context with { Kind = NhAssistantAgentNotificationKind.RunProposalsEscalated }, [otherUser.ToString("D")]);

        var dutch = Assert.Single(sink.Requests, request => request.UserId == dutchUser);
        Assert.Equal(("Assistent", "3 voorstellen bij 2 objecten wachten op je besluit."), (dutch.Title, dutch.Message));
        Assert.Equal($"assistant-agent:run:{runId:N}", dutch.GroupKey);
        var english = sink.Requests.Where(request => request.UserId == otherUser).ToArray();
        Assert.Equal("3 proposals at 2 objects wait for your decision.", english[0].Message);
        Assert.Equal(NhUserNotificationLevel.Information, english[0].Level);
        Assert.Equal("3 proposals at 2 objects were escalated because nobody decided in time.", english[1].Message);
        Assert.Equal(NhUserNotificationLevel.Warning, english[1].Level);
        Assert.All(english, request => Assert.Equal($"assistant-agent:run:{runId:N}", request.GroupKey));

        // One object reads as before, with the object's name; a consultation's proposals have no run
        // and stay with their agent thread.
        sink.Requests.Clear();
        var single = context with
        {
            Kind = NhAssistantAgentNotificationKind.RunProposalsExpiring,
            RunId = null,
            ProposalCount = 2,
            Objects = [context.Objects[0]]
        };
        await channel.DeliverAsync(single, [otherUser.ToString("D")]);
        var reminder = Assert.Single(sink.Requests);
        Assert.Equal(("Project PRJ-1", "2 proposals still wait for a decision."), (reminder.Title, reminder.Message));
        Assert.Equal($"assistant-agent:{opened:N}", reminder.GroupKey);
    }

    private sealed class RecordingSink : INhUserNotificationSink
    {
        public List<NhUserNotificationRequest> Requests { get; } = [];

        public Task<TaskResult<NhUserNotificationReceipt>> SendAsync(
            NhUserNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(TaskResult<NhUserNotificationReceipt>.Succeeded(new NhUserNotificationReceipt(Guid.NewGuid())));
        }
    }

    private sealed class Directory(IReadOnlyDictionary<Guid, NhActorInfo> users) : INhActorDirectory
    {
        public Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(
            IReadOnlyCollection<NhActorRef> actors,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<NhActorRef, NhActorInfo> result = actors
                .Where(actor => actor.TryGetUserId(out var id) && users.ContainsKey(id))
                .ToDictionary(actor => actor, actor =>
                {
                    actor.TryGetUserId(out var id);
                    return users[id];
                });
            return Task.FromResult(result);
        }
    }
}
