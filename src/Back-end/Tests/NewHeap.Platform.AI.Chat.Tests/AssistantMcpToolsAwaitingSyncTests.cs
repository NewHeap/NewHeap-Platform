using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB10: the reviewed settings of a tool that no sync has seen yet, such as a tool of a server the host
/// cannot reach yet. <see cref="INhAssistantMcpServers.UpdateToolAsync"/> stores them as a tool with
/// the status <c>missing</c> and no schema, which no turn offers; the first sync that sees the tool
/// makes it <c>available</c> with them, and a later change of its schema disables it as for any tool;
/// on both relational providers.
/// </summary>
public sealed class AssistantMcpToolsAwaitingSyncTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ServerUrl = "http://localhost/planning/mcp";
    private const string ServerId = "planning";
    private const string AgentId = "project-assistant";
    private const string Admin = "admin-1";
    private const string Lookup = "Lookup_Order";
    private const string Reschedule = "reschedule";
    private const string LookupFunction = "mcp_planning_lookup_order";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Reviewed_tools_of_an_unreachable_server_wait_unoffered_until_the_first_sync_makes_them_available(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("I cannot look up orders yet.")
            .RespondWithText("Order A-100 is planned for Monday.");
        // The host cannot reach the server yet: nothing answers at its address.
        var servers = new InMemoryMcpServers();
        await using var host = await CreateHostAsync(provider, model, servers);
        Assert.True((await McpAsync(host, mcp => mcp.CreateAsync(Server(), Admin))).Success);
        Assert.True((await McpAsync(host, mcp => mcp.AssignAsync(ServerId, AgentId, Admin))).Success);

        var lookup = await McpAsync(host, mcp => mcp.UpdateToolAsync(
            ServerId,
            Lookup,
            new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.ReadOnly, "Finds when an order is planned."),
            Admin));
        var reschedule = await McpAsync(host, mcp => mcp.UpdateToolAsync(
            ServerId,
            Reschedule,
            new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.Mutation),
            Admin));

        Assert.True(lookup.Success, string.Join(", ", lookup.AllErrorMessages));
        Assert.True(reschedule.Success, string.Join(", ", reschedule.AllErrorMessages));
        var waiting = lookup.Data!;
        Assert.Equal(
            (Lookup, "mcp.planning.lookup-order", "", "Finds when an order is planned.", true, NhAssistantMcpToolEffects.ReadOnly, NhAssistantMcpToolStatuses.Missing, (bool?)null, true),
            (waiting.RemoteName, waiting.LocalId, waiting.Description, waiting.DescriptionOverride, waiting.IsEnabled, waiting.Effect, waiting.Status, waiting.ReadOnlyHint, waiting.AwaitingSync));
        var listed = await McpAsync(host, mcp => mcp.ListToolsAsync(ServerId));
        Assert.Equal([Lookup, Reschedule], listed.Data!.Select(tool => tool.RemoteName));
        Assert.All(listed.Data!, tool => Assert.Equal((NhAssistantMcpToolStatuses.Missing, true), (tool.Status, tool.AwaitingSync)));
        var row = await FindToolRowAsync(host, Lookup);
        Assert.Equal(string.Empty, row.InputSchemaHash);
        var reviewed = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolReviewedBeforeSync && evt.ObjectId == ServerId + "/" + Lookup);
        Assert.Equal((Admin, NhAssistantMcpToolAudit.Enabled), (reviewed.ActorId, reviewed.ResultCode));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolUpdated);

        // A sync that cannot reach the server leaves them waiting.
        var unreachable = await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin));
        Assert.False(unreachable.Success);
        Assert.All((await McpAsync(host, mcp => mcp.ListToolsAsync(ServerId))).Data!, tool => Assert.True(tool.AwaitingSync && tool.IsEnabled));

        // No turn offers a tool while it waits, and the turn does not even contact the server.
        var connections = servers.Connections.Count;
        var conversation = await host.CreateConversationAsync();
        await host.SendAsync(conversation.Id, "When is order A-100 planned?");
        Assert.DoesNotContain(model.Requests[0].Options?.Tools ?? [], tool => tool.Name == LookupFunction);
        Assert.Equal(connections, servers.Connections.Count);

        // The server becomes reachable: the first sync that sees the tools takes their schema and keeps
        // the reviewed settings, so they are offered at once.
        var schemaChanges = false;
        servers.Register(ServerUrl, () => servers.PlanningTools(withPriority: schemaChanges));
        var synced = await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin));

        Assert.True(synced.Success, string.Join(", ", synced.AllErrorMessages));
        var available = synced.Data!.Single(tool => tool.RemoteName == Lookup);
        Assert.Equal(
            (NhAssistantMcpToolStatuses.Available, false, true, NhAssistantMcpToolEffects.ReadOnly, "Finds when an order is planned.", "Look up the planning of an order.", (bool?)true),
            (available.Status, available.AwaitingSync, available.IsEnabled, available.Effect, available.DescriptionOverride, available.Description, available.ReadOnlyHint));
        var moved = synced.Data!.Single(tool => tool.RemoteName == Reschedule);
        Assert.Equal((NhAssistantMcpToolStatuses.Available, true, NhAssistantMcpToolEffects.Mutation), (moved.Status, moved.IsEnabled, moved.Effect));
        Assert.NotEqual(string.Empty, (await FindToolRowAsync(host, Lookup)).InputSchemaHash);
        var seen = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolSeenAfterReview).ToArray();
        Assert.Equal([ServerId + "/" + Lookup, ServerId + "/" + Reschedule], seen.Select(evt => evt.ObjectId));
        Assert.All(seen, evt => Assert.Equal((Admin, NhAssistantMcpToolAudit.Enabled), (evt.ActorId, evt.ResultCode)));

        await host.SendAsync(conversation.Id, "And now?");
        Assert.Contains(model.Requests[1].Options!.Tools!, tool => tool.Name == LookupFunction);

        // From now on the tools follow the rules of any synced tool: a changed schema disables one, and
        // no further sync reports them as seen after review.
        schemaChanges = true;
        var changed = await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin));

        Assert.True(changed.Success);
        var disabled = changed.Data!.Single(tool => tool.RemoteName == Reschedule);
        var kept = changed.Data!.Single(tool => tool.RemoteName == Lookup);
        Assert.Equal((NhAssistantMcpToolStatuses.SchemaChanged, false), (disabled.Status, disabled.IsEnabled));
        Assert.Equal((NhAssistantMcpToolStatuses.Available, true), (kept.Status, kept.IsEnabled));
        Assert.Equal(2, host.Business.Events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolSeenAfterReview));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_reviewed_tool_keeps_waiting_through_a_sync_that_does_not_see_it_and_takes_the_servers_spelling(AssistantTestProvider provider)
    {
        var servers = new InMemoryMcpServers();
        var offered = new List<string> { Reschedule };
        servers.Register(ServerUrl, () => servers.PlanningTools().Where(tool => offered.Contains(tool.ProtocolTool.Name)).ToArray());
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), servers);
        Assert.True((await McpAsync(host, mcp => mcp.CreateAsync(Server(), Admin))).Success);
        Assert.True((await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin))).Success);

        // Reviewed in another spelling than the server's: SQL Server would take both for one key.
        var review = await McpAsync(host, mcp => mcp.UpdateToolAsync(
            ServerId,
            "lookup_order",
            new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.ReadOnly, "Finds when an order is planned."),
            Admin));
        Assert.True(review.Success, string.Join(", ", review.AllErrorMessages));
        // Another spelling of a synced tool is refused, as the key would not tell them apart.
        Assert.Equal(["remoteName:invalid"], Fields(await McpAsync(host, mcp => mcp.UpdateToolAsync(
            ServerId,
            "Reschedule",
            new NhAssistantMcpToolSettingsRequest(false, NhAssistantMcpToolEffects.Mutation),
            Admin))));

        // A sync that does not see the tool keeps it waiting with its settings.
        var without = await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin));
        var still = without.Data!.Single(tool => tool.RemoteName == "lookup_order");
        Assert.Equal((NhAssistantMcpToolStatuses.Missing, true, true), (still.Status, still.AwaitingSync, still.IsEnabled));

        // A tool that waits for a sync can change and be enabled; a tool the server stopped listing
        // cannot be enabled.
        var disabled = await McpAsync(host, mcp => mcp.UpdateToolAsync(
            ServerId,
            "lookup_order",
            new NhAssistantMcpToolSettingsRequest(false, NhAssistantMcpToolEffects.ReadOnly, "Finds when an order is planned."),
            Admin));
        var enabled = await McpAsync(host, mcp => mcp.UpdateToolAsync(
            ServerId,
            "lookup_order",
            new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.ReadOnly, "Finds when an order is planned."),
            Admin));
        Assert.Equal((false, NhAssistantMcpToolStatuses.Missing, true), (disabled.Data!.IsEnabled, disabled.Data.Status, disabled.Data.AwaitingSync));
        Assert.Equal((true, NhAssistantMcpToolStatuses.Missing, true), (enabled.Data!.IsEnabled, enabled.Data.Status, enabled.Data.AwaitingSync));
        Assert.Equal(2, host.Business.Events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolUpdated && evt.ObjectId == ServerId + "/lookup_order"));

        offered.Clear();
        offered.Add(Lookup);
        var moved = await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin));

        // The server's spelling takes the reviewed settings; the tool it stopped listing is missing.
        Assert.True(moved.Success, string.Join(", ", moved.AllErrorMessages));
        Assert.Equal([Lookup, Reschedule], moved.Data!.Select(tool => tool.RemoteName));
        var lookup = moved.Data!.Single(tool => tool.RemoteName == Lookup);
        Assert.Equal(
            (NhAssistantMcpToolStatuses.Available, false, true, NhAssistantMcpToolEffects.ReadOnly, "Finds when an order is planned."),
            (lookup.Status, lookup.AwaitingSync, lookup.IsEnabled, lookup.Effect, lookup.DescriptionOverride));
        var vanished = moved.Data!.Single(tool => tool.RemoteName == Reschedule);
        Assert.Equal((NhAssistantMcpToolStatuses.Missing, false), (vanished.Status, vanished.AwaitingSync));
        var seen = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolSeenAfterReview);
        Assert.Equal((ServerId + "/" + Lookup, NhAssistantMcpToolAudit.Enabled), (seen.ObjectId, seen.ResultCode));
        var refused = await McpAsync(host, mcp => mcp.UpdateToolAsync(
            ServerId,
            Reschedule,
            new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.Mutation),
            Admin));
        Assert.Equal(NhAssistantAdminErrorCodes.McpToolDisabled, Code(refused));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_tool_no_sync_has_seen_is_refused_as_a_sync_would_refuse_its_name_and_beyond_the_limit(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new InMemoryMcpServers());
        Assert.True((await McpAsync(host, mcp => mcp.CreateAsync(Server(), Admin))).Success);
        var settings = new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.ReadOnly);

        var tooLong = await McpAsync(host, mcp => mcp.UpdateToolAsync(ServerId, new string('a', NhAssistantMcpAdministration.MaxRemoteNameLength + 1), settings, Admin));
        var control = await McpAsync(host, mcp => mcp.UpdateToolAsync(ServerId, "lookup\u0001order", settings, Admin));
        var effect = await McpAsync(host, mcp => mcp.UpdateToolAsync(ServerId, Lookup, settings with { Effect = "destructive" }, Admin));
        var unknownServer = await McpAsync(host, mcp => mcp.UpdateToolAsync("unknown", Lookup, settings, Admin));
        var longest = await McpAsync(host, mcp => mcp.UpdateToolAsync(ServerId, new string('a', NhAssistantMcpAdministration.MaxRemoteNameLength), settings, Admin));

        Assert.Equal(["remoteName:too-long"], Fields(tooLong));
        Assert.Equal(["remoteName:invalid"], Fields(control));
        Assert.Equal(["effect:invalid"], Fields(effect));
        Assert.Equal(NhAssistantAdminErrorCodes.McpServerNotFound, Code(unknownServer));
        Assert.True(longest.Success, string.Join(", ", longest.AllErrorMessages));
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolReviewedBeforeSync);

        // A server holds a bounded number of tools that wait for a sync.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            for (var index = 1; index < NhAssistantMcpServerLimits.MaxToolsAwaitingSync; index++)
            {
                context.McpTools.Add(new AssistantMcpTool
                {
                    ServerId = ServerId,
                    RemoteName = "expected-" + index,
                    LocalId = "mcp.planning.expected-" + index,
                    Status = NhAssistantMcpToolStatuses.Missing,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            await context.SaveChangesAsync();
        }

        var beyond = await McpAsync(host, mcp => mcp.UpdateToolAsync(ServerId, Lookup, settings, Admin));

        Assert.Equal(NhAssistantErrorCodes.ConfigurationLimit, Code(beyond));
        Assert.Equal(NhAssistantMcpServerLimits.MaxToolsAwaitingSync, (await McpAsync(host, mcp => mcp.ListToolsAsync(ServerId))).Data!.Count);
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolReviewedBeforeSync);
    }

    private static NhAssistantMcpServerRequest Server()
    {
        return new NhAssistantMcpServerRequest(
            ServerId,
            "Planning",
            ServerUrl,
            NhAssistantMcpAuthModes.Bearer,
            null,
            "SECRET-PLANNING-TOKEN-4712",
            null,
            true);
    }

    private Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        InMemoryMcpServers servers)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)));
    }

    private static async Task<T> McpAsync<T>(AssistantTestHost host, Func<INhAssistantMcpServers, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, Admin);
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantMcpServers>());
    }

    private static async Task<AssistantMcpTool> FindToolRowAsync(AssistantTestHost host, string remoteName)
    {
        return (await host.Services.GetRequiredService<NhAssistantAdminStore>().ListMcpToolsAsync(ServerId, CancellationToken.None))
            .Single(tool => tool.RemoteName == remoteName);
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The field errors of a validation failure as <c>field:code</c>, ordered.
    /// </summary>
    private static IReadOnlyList<string> Fields(TaskResult result)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(result));
        return NhAssistantResults.FieldErrorsOf(result)
            .SelectMany(field => field.Value.Select(error => field.Key + ":" + error))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
