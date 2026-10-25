using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
/// MCP servers from the application's code (LS28): <see cref="INhAssistantMcpServers"/> manages
/// servers, tools and agent assignments with the administration's validation, host guard, protected
/// secrets and audit; an assignment changes only the assignment, never the agent's definition or
/// version; on both relational providers.
/// </summary>
public sealed class AssistantMcpServerServiceTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ServerUrl = "http://localhost/planning/mcp";
    private const string ServerId = "planning";
    private const string AgentId = "project-assistant";
    private const string Admin = "admin-1";
    private const string LookupFunction = "mcp_planning_lookup_order";
    private const string Secret = "SECRET-PLANNING-TOKEN-4711";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_application_creates_updates_syncs_and_deletes_a_server_whose_secret_is_never_returned(AssistantTestProvider provider)
    {
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), servers);

        var created = await McpAsync(host, mcp => mcp.CreateAsync(Server() with { Secret = Secret }, Admin));

        Assert.True(created.Success, string.Join(", ", created.AllErrorMessages));
        Assert.Equal((ServerId, true, NhAssistantMcpHeadlessUses.Never), (created.Data!.Id, created.Data.HasSecret, created.Data.HeadlessUse));
        Assert.Empty(created.Data.AssignedAgentIds);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(created.Data), StringComparison.Ordinal);
        var stored = await FindServerRowAsync(host);
        Assert.NotNull(stored.ProtectedSecret);
        Assert.DoesNotContain(Secret, stored.ProtectedSecret, StringComparison.Ordinal);

        // Null keeps the stored secret; the next connection still carries it.
        var updated = await McpAsync(host, mcp => mcp.UpdateAsync(Server() with { DisplayName = "Planning desk", Secret = null }, Admin));
        Assert.True(updated.Success, string.Join(", ", updated.AllErrorMessages));
        Assert.Equal(("Planning desk", true), (updated.Data!.DisplayName, updated.Data.HasSecret));

        var tested = await McpAsync(host, mcp => mcp.TestAsync(ServerId));
        Assert.Equal((true, (string?)null, (int?)2), (tested.Data!.Ok, tested.Data.Code, tested.Data.ToolCount));
        Assert.Equal("Bearer " + Secret, servers.Connections.Last().Headers["Authorization"]);

        // Synced tools start disabled as mutations until the application enables one.
        var synced = await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin));
        Assert.True(synced.Success);
        Assert.All(synced.Data!, tool => Assert.False(tool.IsEnabled));
        Assert.All(synced.Data!, tool => Assert.Equal(NhAssistantMcpToolEffects.Mutation, tool.Effect));
        var enabled = await McpAsync(host, mcp => mcp.UpdateToolAsync(
            ServerId,
            "Lookup_Order",
            new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.ReadOnly, "Looks up when an order is planned."),
            Admin));
        Assert.True(enabled.Success);
        Assert.Equal((true, NhAssistantMcpToolEffects.ReadOnly, NhAssistantMcpToolStatuses.Available), (enabled.Data!.IsEnabled, enabled.Data.Effect, enabled.Data.Status));
        var tools = await McpAsync(host, mcp => mcp.ListToolsAsync(ServerId));
        Assert.Equal(["Lookup_Order", "reschedule"], tools.Data!.Select(tool => tool.RemoteName).Order(StringComparer.Ordinal));
        Assert.Equal("Looks up when an order is planned.", tools.Data!.Single(tool => tool.RemoteName == "Lookup_Order").DescriptionOverride);

        // A tool update the administration would refuse is refused here too.
        var invalidEffect = await McpAsync(host, mcp => mcp.UpdateToolAsync(ServerId, "reschedule", new NhAssistantMcpToolSettingsRequest(true, "destructive"), Admin));
        Assert.Equal(["effect:invalid"], Fields(invalidEffect));
        var unknownTool = await McpAsync(host, mcp => mcp.UpdateToolAsync(ServerId, "unknown", new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.ReadOnly), Admin));
        Assert.Equal(NhAssistantAdminErrorCodes.McpToolNotFound, Code(unknownTool));
        Assert.Equal(NhAssistantAdminErrorCodes.McpServerExists, Code(await McpAsync(host, mcp => mcp.CreateAsync(Server() with { Secret = Secret }, Admin))));

        var listed = Assert.Single(await McpAsync(host, mcp => mcp.ListAsync()));
        Assert.Equal(("Planning desk", true, "ok"), (listed.DisplayName, listed.HasSecret, listed.LastSyncStatus));

        var deleted = await McpAsync(host, mcp => mcp.DeleteAsync(ServerId, Admin));
        Assert.True(deleted.Success);
        Assert.Empty(await McpAsync(host, mcp => mcp.ListAsync()));
        Assert.Equal(NhAssistantAdminErrorCodes.McpServerNotFound, Code(await McpAsync(host, mcp => mcp.DeleteAsync(ServerId, Admin))));
        Assert.Equal(NhAssistantAdminErrorCodes.McpServerNotFound, Code(await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin))));

        // The administration's audit, with the server and the actor, never the secret.
        var events = host.Business.Events.ToArray();
        foreach (var kind in new[]
                 {
                     NhAssistantAuditEventKind.AdminMcpServerCreated,
                     NhAssistantAuditEventKind.AdminMcpServerUpdated,
                     NhAssistantAuditEventKind.AdminMcpServerSynced,
                     NhAssistantAuditEventKind.AdminMcpServerDeleted
                 })
        {
            var evt = Assert.Single(events, item => item.Kind == kind);
            Assert.Equal((ServerId, Admin), (evt.ObjectId, evt.ActorId));
        }
        Assert.Single(events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolUpdated && evt.ObjectId == ServerId + "/Lookup_Order");
        var captured = string.Join("\n", host.Logs.Messages.Concat(events.Select(evt => JsonSerializer.Serialize(evt))));
        Assert.DoesNotContain(Secret, captured, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_assignment_offers_the_servers_tools_to_the_agent_without_changing_its_definition(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Order A-100 is planned for Monday.")
            .RespondWithText("I cannot look up orders.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, model, servers);
        Assert.True((await McpAsync(host, mcp => mcp.CreateAsync(Server() with { Secret = Secret }, Admin))).Success);
        Assert.True((await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin))).Success);
        Assert.True((await McpAsync(host, mcp => mcp.UpdateToolAsync(ServerId, "Lookup_Order", new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.ReadOnly), Admin))).Success);
        var before = await FindAgentRowAsync(host);

        var assigned = await McpAsync(host, mcp => mcp.AssignAsync(ServerId, AgentId, Admin));

        Assert.True(assigned.Success, string.Join(", ", assigned.AllErrorMessages));
        Assert.Equal([AgentId], assigned.Data!.AssignedAgentIds);
        var after = await FindAgentRowAsync(host);
        Assert.Equal(
            (before.Version, before.IsOverridden, before.UpdatedAt, before.UpdatedBy),
            (after.Version, after.IsOverridden, after.UpdatedAt, after.UpdatedBy));
        Assert.Equal([ServerId], after.McpServers.Select(server => server.McpServerId));
        var audited = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged);
        Assert.Equal((ServerId, AgentId, Admin, NhAssistantMcpAssignmentAudit.Assigned), (audited.ObjectId, audited.AgentId, audited.ActorId, audited.ResultCode));

        // Assigning again changes nothing and audits nothing.
        Assert.True((await McpAsync(host, mcp => mcp.AssignAsync(ServerId, AgentId, Admin))).Success);
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentUpdated);

        // The agent's turns offer the server's enabled tools.
        var conversation = await host.CreateConversationAsync();
        await host.SendAsync(conversation.Id, "When is order A-100 planned?");
        Assert.Contains(model.Requests[0].Options!.Tools!, tool => tool.Name == LookupFunction);

        var unassigned = await McpAsync(host, mcp => mcp.UnassignAsync(ServerId, AgentId, Admin));

        Assert.True(unassigned.Success);
        Assert.Empty(unassigned.Data!.AssignedAgentIds);
        Assert.Equal(before.Version, (await FindAgentRowAsync(host)).Version);
        Assert.Equal(
            [NhAssistantMcpAssignmentAudit.Assigned, NhAssistantMcpAssignmentAudit.Unassigned],
            host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged).Select(evt => evt.ResultCode));
        await host.SendAsync(conversation.Id, "And order A-200?");
        Assert.DoesNotContain(model.Requests[1].Options?.Tools ?? [], tool => tool.Name == LookupFunction);

        // Removing a missing assignment changes nothing; unknown servers and agents are refused.
        Assert.True((await McpAsync(host, mcp => mcp.UnassignAsync(ServerId, AgentId, Admin))).Success);
        Assert.Equal(2, host.Business.Events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged));
        Assert.Equal(NhAssistantAdminErrorCodes.McpServerNotFound, Code(await McpAsync(host, mcp => mcp.AssignAsync("unknown", AgentId, Admin))));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await McpAsync(host, mcp => mcp.AssignAsync(ServerId, "unknown-agent", Admin))));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await McpAsync(host, mcp => mcp.UnassignAsync(ServerId, "unknown-agent", Admin))));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_with_as_many_servers_as_allowed_gets_no_more(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new InMemoryMcpServers());
        // The agent exists once the catalog stored the agents in code.
        await FindAgentRowAsync(host);
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index <= NhAssistantMcpServerLimits.MaxServersPerAgent; index++)
            {
                context.McpServers.Add(new AssistantMcpServer
                {
                    Id = "server-" + index,
                    DisplayName = "Server " + index,
                    Url = "https://server-" + index + ".example.org/mcp",
                    AuthMode = NhAssistantMcpAuthModes.None,
                    IsEnabled = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                if (index < NhAssistantMcpServerLimits.MaxServersPerAgent)
                {
                    context.AgentMcpServers.Add(new AssistantAgentMcpServer { AgentId = AgentId, McpServerId = "server-" + index });
                }
            }

            await context.SaveChangesAsync();
        }

        var refused = await McpAsync(host, mcp => mcp.AssignAsync("server-" + NhAssistantMcpServerLimits.MaxServersPerAgent, AgentId, Admin));

        Assert.Equal(["mcpServerIds:too-long"], Fields(refused));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged);
        // An assigned server is still assigned without a change.
        Assert.True((await McpAsync(host, mcp => mcp.AssignAsync("server-0", AgentId, Admin))).Success);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/mcp")]
    [InlineData("https://metadata.google.internal/mcp")]
    [InlineData("http://[fe80::1]/mcp")]
    [InlineData("http://planning.example.org/mcp")]
    public async Task The_host_guard_refuses_what_the_administration_refuses(string url)
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, new NhAiScriptedChatClient(), new InMemoryMcpServers());

        var created = await McpAsync(host, mcp => mcp.CreateAsync(Server() with { Url = url }, Admin));

        Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, Code(created));
        Assert.Empty(await McpAsync(host, mcp => mcp.ListAsync()));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerCreated);
    }

    [Fact]
    public async Task Invalid_input_is_refused_with_the_field_errors_of_the_administration()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, new NhAiScriptedChatClient(), new InMemoryMcpServers());

        var invalid = await McpAsync(host, mcp => mcp.CreateAsync(
            new NhAssistantMcpServerRequest("Planning Desk", "", "ftp://planning.example.org/mcp", "unknown-mode", null, null, null, true),
            Admin));
        var noSecret = await McpAsync(host, mcp => mcp.CreateAsync(Server() with { Secret = null }, Admin));
        var readers = await McpAsync(host, mcp => mcp.CreateAsync(
            Server() with { Secret = Secret, Readers = [new NhAssistantMcpReaderRequest(["application/pdf"], "Lookup_Order", "internal")] },
            Admin));

        Assert.Equal(["authMode:invalid", "displayName:required", "id:invalid", "url:invalid"], Fields(invalid));
        Assert.Equal(["secret:required"], Fields(noSecret));
        // A new server has no synced tools yet, so it gets its readers after a sync.
        Assert.Equal(["readers[0].toolName:not-found"], Fields(readers));
        // An actor is the caller's programming, not input to validate.
        await Assert.ThrowsAsync<ArgumentException>(() => McpAsync(host, mcp => mcp.CreateAsync(Server(), " ")));
    }

    private static NhAssistantMcpServerRequest Server()
    {
        return new NhAssistantMcpServerRequest(
            ServerId,
            "Planning",
            ServerUrl,
            NhAssistantMcpAuthModes.Bearer,
            null,
            null,
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
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)),
            assistantBuilder: assistant => assistant.ConfigureMcp(mcp => mcp.RequireHttps = true));
    }

    private static async Task<T> McpAsync<T>(AssistantTestHost host, Func<INhAssistantMcpServers, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, Admin);
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantMcpServers>());
    }

    private static async Task<AssistantMcpServer> FindServerRowAsync(AssistantTestHost host)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.McpServers.AsNoTracking().SingleAsync(server => server.Id == ServerId);
    }

    private static async Task<AssistantAgent> FindAgentRowAsync(AssistantTestHost host)
    {
        await using (var scope = host.Services.CreateAsyncScope())
        {
            // Stores the agents in code first.
            await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().ListAsync(includeDisabled: true, CancellationToken.None);
        }

        return (await host.Services.GetRequiredService<NhAssistantAdminStore>().FindAgentAsync(AgentId, CancellationToken.None))!;
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
