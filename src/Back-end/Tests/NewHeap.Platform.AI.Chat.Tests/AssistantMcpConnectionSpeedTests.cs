using System.ComponentModel;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB71: what a turn waits for before its model starts. An agent's MCP servers connect at the same time
/// and their tools keep the agent's order, and a server that could not be reached costs one turn its
/// timeout instead of every turn.
/// </summary>
public sealed class AssistantMcpConnectionSpeedTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string PlanningUrl = "http://localhost/planning/mcp";
    private const string BillingUrl = "http://localhost/billing/mcp";

    [Fact]
    public async Task The_servers_of_an_agent_connect_at_the_same_time_and_keep_its_order()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Nothing to do.");
        var servers = new GatedMcpServers();
        await using var host = await CreateHostAsync(model, servers);
        await ConnectServersAsync(host, servers);
        servers.HoldUntilBothConnect();

        var conversation = await host.CreateConversationAsync();
        // The first server only finishes after the second started, which a sequential turn never reaches,
        // and its tools still come first.
        var turn = await host.SendAsync(conversation.Id, "Which tools do you have?").WaitAsync(TimeSpan.FromSeconds(60));

        var completed = turn.Single<NhAssistantTurnCompletedEvent>();
        Assert.Equal(NhAssistantTurnStatuses.Completed, completed.Status);
        Assert.True(completed.Timings!.McpTools > TimeSpan.Zero);
        var tools = model.Requests[0].Options!.Tools!.Select(tool => tool.Name).Where(name => name.StartsWith("mcp_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["mcp_billing_lookup", "mcp_planning_lookup"], tools);
    }

    [Fact]
    public async Task An_unreachable_server_is_tried_by_one_turn_and_again_after_a_change()
    {
        var model = new NhAiScriptedChatClient(loop: true).RespondWithText("Without planning.");
        var servers = new GatedMcpServers();
        await using var host = await CreateHostAsync(model, servers);
        await ConnectServersAsync(host, servers);
        servers.Fail(PlanningUrl, new HttpRequestException("No route."));
        var conversation = await host.CreateConversationAsync();

        await InvalidateAsync(host);
        var attemptsBefore = servers.Attempts(PlanningUrl);
        var first = await host.SendAsync(conversation.Id, "One.");
        var second = await host.SendAsync(conversation.Id, "Two.");

        Assert.Equal(attemptsBefore + 1, servers.Attempts(PlanningUrl));
        Assert.Equal(NhAssistantTurnStatuses.Completed, first.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Equal(NhAssistantTurnStatuses.Completed, second.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Contains(model.Requests[^1].Options!.Tools!, tool => tool.Name == "mcp_billing_lookup");
        Assert.DoesNotContain(model.Requests[^1].Options!.Tools!, tool => tool.Name == "mcp_planning_lookup");

        // An administrator's test tries it at once, and a change to the server lets the next turn try.
        var test = await WithAdministrationAsync(host, administration => administration.TestAsync("planning", CancellationToken.None));
        Assert.Equal(NhAssistantAdminErrorCodes.McpUnreachable, test.Data!.Code);
        Assert.Equal(attemptsBefore + 2, servers.Attempts(PlanningUrl));
        servers.Heal(PlanningUrl);
        await InvalidateAsync(host);
        await host.SendAsync(conversation.Id, "Three.");
        Assert.Contains(model.Requests[^1].Options!.Tools!, tool => tool.Name == "mcp_planning_lookup");
    }

    [Fact]
    public async Task A_refused_credential_is_not_remembered_because_it_is_one_persons()
    {
        var model = new NhAiScriptedChatClient(loop: true).RespondWithText("Without planning.");
        var servers = new GatedMcpServers();
        await using var host = await CreateHostAsync(model, servers);
        await ConnectServersAsync(host, servers);
        servers.Fail(PlanningUrl, new HttpRequestException("Refused.", null, HttpStatusCode.Unauthorized));
        var conversation = await host.CreateConversationAsync();

        await InvalidateAsync(host);
        var attemptsBefore = servers.Attempts(PlanningUrl);
        await host.SendAsync(conversation.Id, "One.");
        await host.SendAsync(conversation.Id, "Two.");

        Assert.Equal(attemptsBefore + 2, servers.Attempts(PlanningUrl));
    }

    [Fact]
    public async Task Zero_tries_an_unreachable_server_on_every_turn()
    {
        var model = new NhAiScriptedChatClient(loop: true).RespondWithText("Without planning.");
        var servers = new GatedMcpServers();
        await using var host = await CreateHostAsync(model, servers, retryDelay: TimeSpan.Zero);
        await ConnectServersAsync(host, servers);
        servers.Fail(PlanningUrl, new HttpRequestException("No route."));
        var conversation = await host.CreateConversationAsync();

        await InvalidateAsync(host);
        var attemptsBefore = servers.Attempts(PlanningUrl);
        await host.SendAsync(conversation.Id, "One.");
        await host.SendAsync(conversation.Id, "Two.");

        Assert.Equal(attemptsBefore + 2, servers.Attempts(PlanningUrl));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void A_retry_delay_outside_its_range_is_refused_at_start(int seconds)
    {
        var options = new NhAssistantMcpOptions { UnreachableRetryDelay = TimeSpan.FromSeconds(seconds) };

        var refused = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("UnreachableRetryDelay", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_provider_gets_one_call_at_a_time_while_its_servers_connect_together()
    {
        var provider = new CountingCredentialProvider();
        var gate = new SemaphoreSlim(1, 1);
        var first = new NhAssistantMcpCredentialSource(provider, new AssistantMcpServer { Id = "planning" }, NhAssistantMcpCredentialSubject.Request, gate);
        var second = new NhAssistantMcpCredentialSource(provider, new AssistantMcpServer { Id = "billing" }, NhAssistantMcpCredentialSubject.Request, gate);

        var calls = Enumerable.Range(0, 8)
            .Select(index => (index % 2 == 0 ? first : second).GetAsync(CancellationToken.None).AsTask())
            .ToArray();
        await Task.WhenAll(calls);

        Assert.Equal(8, provider.Calls);
        Assert.Equal(1, provider.MostAtOnce);
    }

    private Task<AssistantTestHost> CreateHostAsync(NhAiScriptedChatClient model, GatedMcpServers servers, TimeSpan? retryDelay = null)
    {
        return AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)),
            assistantBuilder: assistant => assistant.ConfigureMcp(mcp =>
            {
                mcp.RequireHttps = false;
                if (retryDelay is { } delay)
                {
                    mcp.UnreachableRetryDelay = delay;
                }
            }));
    }

    /// <summary>
    /// Adds the two servers, activates a tool of each and assigns both to the agent, in that order.
    /// </summary>
    private static async Task ConnectServersAsync(AssistantTestHost host, GatedMcpServers servers)
    {
        foreach (var (id, url) in new[] { ("planning", PlanningUrl), ("billing", BillingUrl) })
        {
            servers.Register(url);
            await WithAdministrationAsync(host, async administration =>
            {
                var created = await administration.CreateAsync(
                    new NhAssistantMcpServerInput(id, id, url, NhAssistantMcpAuthModes.None, null, null, null, true),
                    "admin-1",
                    CancellationToken.None);
                Assert.True(created.Success);
                var synced = await administration.SyncAsync(id, "admin-1", CancellationToken.None);
                Assert.True(synced.Success);
                var updated = await administration.UpdateToolAsync(id, "Lookup", true, NhAssistantMcpToolEffects.ReadOnly, null, "admin-1", CancellationToken.None);
                Assert.True(updated.Success);
                return true;
            });
        }

        await using var scope = host.Services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var agent = (await catalog.FindAsync("project-assistant", includeDisabled: true, CancellationToken.None))!;
        var assigned = await agents.UpdateAsync(
            new NhAssistantAgentInput(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                agent.Definition.Instructions.Content,
                agent.Definition.ToolSelectors,
                ["planning", "billing"],
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None);
        Assert.True(assigned.Success);
        await InvalidateAsync(host);
    }

    /// <summary>
    /// Empties the connection cache, as a host after a restart, so the next turn connects.
    /// </summary>
    private static async Task InvalidateAsync(AssistantTestHost host)
    {
        var cache = host.Services.GetRequiredService<NhAssistantMcpConnectionCache>();
        await cache.InvalidateAsync("planning");
        await cache.InvalidateAsync("billing");
    }

    private static async Task<T> WithAdministrationAsync<T>(
        AssistantTestHost host,
        Func<NhAssistantMcpAdministration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
        return await action(scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>());
    }

    /// <summary>
    /// In-memory servers that can hold a connection until the other one starts, or fail.
    /// </summary>
    private sealed class GatedMcpServers : INhAssistantMcpClientFactory
    {
        private readonly InMemoryMcpServers _servers = new();
        private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hold;

        public void Register(string url)
        {
            _servers.Register(url, () => [McpServerTool.Create(
                ([Description("Number")] string number) => "Found " + number,
                new McpServerToolCreateOptions { Name = "Lookup", Description = "Look something up.", ReadOnly = true })]);
        }

        public int Attempts(string url)
        {
            return _servers.Connections.Count(plan => plan.Endpoint.ToString() == url);
        }

        public void HoldUntilBothConnect()
        {
            _hold = true;
        }

        public void Fail(string url, Exception failure)
        {
            lock (_failures)
            {
                _failures[url] = failure;
            }
        }

        public void Heal(string url)
        {
            lock (_failures)
            {
                _failures.Remove(url);
            }
        }

        public async Task<McpClient> ConnectAsync(NhAssistantMcpConnectionPlan plan, CancellationToken cancellationToken)
        {
            var url = plan.Endpoint.ToString();
            Exception? failure;
            lock (_failures)
            {
                _failures.TryGetValue(url, out failure);
            }
            if (failure is not null)
            {
                _servers.Connections.Enqueue(plan);
                throw failure;
            }
            if (_hold && url == PlanningUrl)
            {
                _secondStarted.TrySetResult();
            }
            if (_hold && url == BillingUrl)
            {
                await _secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return await _servers.ConnectAsync(plan, cancellationToken);
        }
    }

    private sealed class CountingCredentialProvider : INhAssistantMcpCredentialProvider
    {
        private int _running;
        private int _calls;
        private int _mostAtOnce;

        public int Calls => Volatile.Read(ref _calls);

        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public bool CanRenew => false;

        public bool Supports(string authMode)
        {
            return true;
        }

        public async ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(
            AssistantMcpServer server,
            NhAssistantMcpCredentialSubject subject,
            CancellationToken cancellationToken)
        {
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _mostAtOnce, running);
            Interlocked.Increment(ref _calls);
            await Task.Yield();
            Interlocked.Decrement(ref _running);
            return TaskResult<NhAssistantMcpCredential>.Succeeded(new NhAssistantMcpCredential(new Dictionary<string, string>()));
        }

        public ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(
            AssistantMcpServer server,
            NhAssistantMcpCredentialSubject subject,
            NhAssistantMcpChallenge challenge,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
        {
            return TaskResult.Succeeded();
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref target);
                if (value <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, current) != current);
        }
    }
}
