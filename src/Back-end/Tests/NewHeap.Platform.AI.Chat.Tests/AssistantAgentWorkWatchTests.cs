using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Live;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Live updates for the approvers and readers of the whole application: it lists no members, so its
/// agents' work reaches people only while a screen such as the approval inbox or a board watches it
/// (<c>WatchAgentWork</c> on the assistant hub). The server joins a connection to the work of the agents
/// whose thread of the whole application the person reads, the agent's view policy included, and
/// leaves what the person may no longer read when the watch is renewed; on both relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantAgentWorkWatchTests(AssistantDatabaseFixture database)
{
    private const string FollowUp = "project-follow-up";
    private const string Statistics = "weekly-statistics";
    private const string ProjectAgent = "project-assistant";
    private const string ViewPolicy = "app.results.view";
    private const string Viewer = "viewer-1";
    private const string Reader = "reader-1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Watching_follows_the_rule_of_the_agents_thread_of_the_whole_application(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        // A shadow thread is the administrators' work and is never watched.
        await ThreadAtAsync(app, Statistics, NhAiSystemSubjects.Global, shadow: true);

        await using var scope = app.Services.CreateAsyncScope();
        var watchers = scope.ServiceProvider.GetRequiredService<NhAssistantAgentWorkWatchers>();

        // The work of every agent for the whole application whose thread there the person reads, also
        // before its first run; the follow-up's view policy keeps its work from the reader.
        Assert.Equal(
            [new NhAssistantWatchedWork(FollowUp, NhAiSystemSubjects.GlobalId), new NhAssistantWatchedWork(Statistics, NhAiSystemSubjects.GlobalId)],
            (await watchers.WatchableAsync(Viewer, null, CancellationToken.None)).OrderBy(work => work.AgentId, StringComparer.Ordinal));
        Assert.Equal(
            [new NhAssistantWatchedWork(Statistics, NhAiSystemSubjects.GlobalId)],
            await watchers.WatchableAsync(Reader, null, CancellationToken.None));
        // The whole application belongs to no tenant: a person bound to one follows none of it.
        Assert.Empty(await watchers.WatchableAsync(Viewer, "tenant-1", CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task People_who_may_read_an_agents_work_for_the_whole_application_follow_it_live_while_they_watch(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var followUp = await ThreadAtAsync(app, FollowUp, NhAiSystemSubjects.Global);
        var statistics = await ThreadAtAsync(app, Statistics, NhAiSystemSubjects.Global);
        await using var viewer = Connect(app, Viewer);
        await using var reader = Connect(app, Reader);
        var viewerUpdates = Record(viewer);
        var readerUpdates = Record(reader);
        await viewer.StartAsync();
        await reader.StartAsync();

        // Without a watch nothing about the whole application arrives: it lists no members.
        var approvalId = Guid.NewGuid();
        await PublishAsync(app, followUp.ConversationId, approvalId);
        await AssertNothingAsync(viewerUpdates, readerUpdates);

        // Watching joins the work the server decides the person reads.
        var viewerWatch = await viewer.InvokeAsync<NhAssistantLiveWatchDto>("WatchAgentWork");
        var readerWatch = await reader.InvokeAsync<NhAssistantLiveWatchDto>("WatchAgentWork");
        Assert.Equal((2, 300), (viewerWatch.Watched, viewerWatch.RefreshAfterSeconds));
        Assert.Equal(1, readerWatch.Watched);

        // A proposal of the follow-up reaches the viewer, who passes its view policy, with ids only;
        // the reader does not pass it and hears nothing.
        await PublishAsync(app, followUp.ConversationId, approvalId);
        var change = await NextAsync(viewerUpdates);
        Assert.Equal(
            (followUp.ConversationId, NhAssistantAgentWorkReasons.Proposal, approvalId, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId),
            (change.ConversationId, change.Reason, change.ApprovalId, change.SubjectType, change.SubjectId));
        await AssertNothingAsync(readerUpdates);

        // Work both may read reaches both.
        await PublishAsync(app, statistics.ConversationId, null);
        Assert.Equal(statistics.ConversationId, (await NextAsync(viewerUpdates)).ConversationId);
        Assert.Equal(statistics.ConversationId, (await NextAsync(readerUpdates)).ConversationId);

        // Renewing the watch keeps the same work without updates twice; stopping it ends the updates.
        Assert.Equal(2, (await viewer.InvokeAsync<NhAssistantLiveWatchDto>("WatchAgentWork")).Watched);
        await PublishAsync(app, followUp.ConversationId, approvalId);
        Assert.Equal(followUp.ConversationId, (await NextAsync(viewerUpdates)).ConversationId);
        await AssertNothingAsync(viewerUpdates);
        await viewer.InvokeAsync("UnwatchAgentWork");
        await PublishAsync(app, followUp.ConversationId, approvalId);
        await AssertNothingAsync(viewerUpdates);
    }

    private static HubConnection Connect(AssistantWebApplication app, string user)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hub/assistant"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                options.Headers[AssistantWebApplication.UserHeader] = user;
                options.Headers[AssistantWebApplication.AccessHeader] = "true";
            })
            .Build();
    }

    private static Channel<NhAssistantLiveAgentWorkDto> Record(HubConnection connection)
    {
        var updates = Channel.CreateUnbounded<NhAssistantLiveAgentWorkDto>();
        connection.On<NhAssistantLiveAgentWorkDto>("AgentWorkChanged", change => updates.Writer.TryWrite(change));
        return updates;
    }

    private static async Task<NhAssistantLiveAgentWorkDto> NextAsync(Channel<NhAssistantLiveAgentWorkDto> updates)
    {
        using var timeout = new CancellationTokenSource(Wait);
        return await updates.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>
    /// Waits long enough for an update that was sent to have arrived, then asserts that none did.
    /// </summary>
    private static async Task AssertNothingAsync(params Channel<NhAssistantLiveAgentWorkDto>[] updates)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        foreach (var channel in updates)
        {
            Assert.False(channel.Reader.TryRead(out var unexpected), unexpected?.ConversationId.ToString());
        }
    }

    private static async Task PublishAsync(AssistantWebApplication app, Guid conversationId, Guid? approvalId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantAgentWorkUpdates>().PublishAsync(
            conversationId,
            NhAssistantAgentWorkReasons.Proposal,
            approvalId,
            cancellationToken: CancellationToken.None);
    }

    private static async Task<AssistantThread> ThreadAtAsync(AssistantWebApplication app, string agentId, NhAiSubjectRef subject, bool shadow = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadow)).Data!;
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, WatchPrincipals>());
                services.AddAuthorization(options => options.AddPolicy(ViewPolicy, policy => policy.RequireClaim("permission", ViewPolicy)));
                // Nothing runs here: work is published by hand.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(FollowUp))
                .AddAgent(AssistantTestData.Agent(Statistics))
                .ConfigureAgent(ProjectAgent, run => run.WorksOnAllObjects())
                .ConfigureAgent(FollowUp, run => run.WorksOnApplication().ViewPolicy(ViewPolicy))
                .ConfigureAgent(Statistics, run => run.WorksOnApplication()),
            provider: provider);
    }

    /// <summary>
    /// The people as the whole application sees them: the viewer passes the follow-up's view policy,
    /// the reader only uses the assistant.
    /// </summary>
    private sealed class WatchPrincipals : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            var permissions = actorId switch
            {
                Viewer => new[] { "assistant", ViewPolicy },
                Reader => new[] { "assistant" },
                _ => null
            };
            return ValueTask.FromResult(permissions is null
                ? null
                : new ClaimsPrincipal(new ClaimsIdentity(
                    permissions.Select(value => new Claim("permission", value)).Append(new Claim(ClaimTypes.NameIdentifier, actorId)),
                    "test")));
        }
    }
}
