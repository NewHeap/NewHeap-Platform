using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Every function name a turn offers names one MCP tool. The name keeps the server id's dashes, so
/// servers whose ids and tool names meet at a dash, such as <c>acme</c> with <c>billing_refund</c> and
/// <c>acme-billing</c> with <c>refund</c>, keep names of their own and each call reaches its own server.
/// Tools that would still share a name, such as two tools of a server whose names differ only in
/// case or punctuation, are offered by neither, with <c>assistant-mcp-tool-name-conflict</c>, instead
/// of the first of a list. On both relational providers.
/// </summary>
public sealed class AssistantMcpFunctionNameTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string AcmeUrl = "http://localhost/acme/mcp";
    private const string AcmeBillingUrl = "http://localhost/acme-billing/mcp";
    private const string PlanningUrl = "http://localhost/planning/mcp";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Tools_of_servers_whose_ids_and_names_meet_at_a_dash_keep_names_and_calls_of_their_own(AssistantTestProvider provider)
    {
        var calls = new ConcurrentQueue<string>();
        var servers = new InMemoryMcpServers();
        servers.Register(AcmeUrl, () => [Tool("billing_refund", "Notes a billing refund request.", "acme", calls)]);
        servers.Register(AcmeBillingUrl, () => [Tool("refund", "Refunds a customer in the billing system.", "acme-billing", calls)]);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("mcp_acme-billing_refund", new { customer = "c-42" })
            .RespondWithText("Customer c-42 was refunded.");
        await using var host = await CreateHostAsync(provider, model, servers);
        await ConnectAsync(host, "acme", AcmeUrl, "billing_refund");
        await ConnectAsync(host, "acme-billing", AcmeBillingUrl, "refund");
        await AssignAsync(host, "acme", "acme-billing");
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Refund customer c-42.");

        var offered = model.Requests[0].Options!.Tools!.Select(tool => tool.Name).Where(name => name.StartsWith("mcp_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["mcp_acme-billing_refund", "mcp_acme_billing_refund"], offered);
        Assert.Equal(["acme-billing"], calls);
        Assert.Equal("mcp.acme-billing.refund", turn.Single<NhAssistantToolStartedEvent>().ToolId);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Tools_of_a_server_whose_names_make_one_function_name_are_offered_by_neither(AssistantTestProvider provider)
    {
        var calls = new ConcurrentQueue<string>();
        var servers = new InMemoryMcpServers();
        servers.Register(PlanningUrl, () =>
        [
            Tool("Lookup_Order", "Looks up the planning of an order.", "planning", calls),
            Tool("lookup-order", "Looks up an order somewhere else.", "planning", calls),
            Tool("reschedule", "Moves an order to another day.", "planning", calls)
        ]);
        var model = new NhAiScriptedChatClient().RespondWithText("I can reschedule orders.");
        await using var host = await CreateHostAsync(provider, model, servers);
        await ConnectAsync(host, "planning", PlanningUrl, "Lookup_Order", "lookup-order", "reschedule");
        await AssignAsync(host, "planning");
        var conversation = await host.CreateConversationAsync();

        await host.SendAsync(conversation.Id, "Which tools do you have?");

        var offered = model.Requests[0].Options!.Tools!.Select(tool => tool.Name).Where(name => name.StartsWith("mcp_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["mcp_planning_reschedule"], offered);
        Assert.Equal(2, host.Logs.Messages.Count(message => message.Contains(NhAssistantAdminErrorCodes.McpToolNameConflict, StringComparison.Ordinal)));
        Assert.Empty(calls);
    }

    /// <summary>
    /// The last line of defence: functions of one turn that share a name are all refused and recorded
    /// with the stable code, whatever produced them, while the others are offered as they were.
    /// </summary>
    [Fact]
    public async Task Functions_that_share_a_name_are_all_refused_with_a_stable_code()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, new NhAiScriptedChatClient(), new InMemoryMcpServers());
        await using var scope = host.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<NhAssistantMcpToolSource>();
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        AIFunction first = new NamedFunction("mcp_acme_refund", "mcp.acme.refund");
        AIFunction second = new NamedFunction("mcp_acme_refund", "mcp.other.refund");
        AIFunction other = new NamedFunction("mcp_acme_lookup", "mcp.acme.lookup");

        var offered = source.OfferedOnce([first, other, second], skipped);

        Assert.Equal([other], offered);
        Assert.Equal(
            [("mcp.acme.refund", NhAssistantAdminErrorCodes.McpToolNameConflict), ("mcp.other.refund", NhAssistantAdminErrorCodes.McpToolNameConflict)],
            skipped.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => (item.Key, item.Value)).ToArray());
        var set = new NhAssistantMcpToolSet(offered) { SkippedTools = skipped };
        Assert.Equal(NhAssistantAdminErrorCodes.McpToolNameConflict, set.SkippedCodeOf("mcp.other.refund"));
        Assert.Null(set.SkippedCodeOf("mcp.acme.lookup"));
    }

    private static McpServerTool Tool(string name, string description, string server, ConcurrentQueue<string> calls)
    {
        return McpServerTool.Create(
            (string customer) =>
            {
                calls.Enqueue(server);
                return "Done for " + customer + ".";
            },
            new McpServerToolCreateOptions { Name = name, Description = description, ReadOnly = true });
    }

    private Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model, InMemoryMcpServers servers)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)));
    }

    /// <summary>
    /// Connects a server without credentials, syncs it and enables the named tools as read-only.
    /// </summary>
    private static async Task ConnectAsync(AssistantTestHost host, string id, string url, params string[] tools)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
        var administration = scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>();
        var input = new NhAssistantMcpServerInput(id, id, url, NhAssistantMcpAuthModes.None, null, null, null, true);
        Assert.True((await administration.CreateAsync(input, "admin-1", CancellationToken.None)).Success);
        Assert.True((await administration.SyncAsync(id, "admin-1", CancellationToken.None)).Success);
        foreach (var tool in tools)
        {
            var enabled = await administration.UpdateToolAsync(id, tool, true, NhAssistantMcpToolEffects.ReadOnly, null, "admin-1", CancellationToken.None);
            Assert.True(enabled.Success);
        }
    }

    private static async Task AssignAsync(AssistantTestHost host, params string[] serverIds)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var agent = (await catalog.FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
        var assigned = await agents.UpdateAsync(
            new NhAssistantAgentInput(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                agent.Definition.Instructions.Content,
                agent.Definition.ToolSelectors,
                serverIds,
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None);
        Assert.True(assigned.Success);
    }

    /// <summary>
    /// A governed function with a name of its own choosing, as no importer would make it.
    /// </summary>
    private sealed class NamedFunction(string name, string toolId) : AIFunction, INhAiGovernedAIFunction
    {
        public override string Name => name;

        public NhAiToolDescriptor Descriptor { get; } = new(
            toolId,
            1,
            "A tool.",
            typeof(object),
            typeof(object),
            NhAiToolEffect.ReadOnly,
            NhAiToolExposure.Agent,
            true,
            [AssistantTestHost.AccessPolicy]);

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<object?>(null);
        }
    }
}
