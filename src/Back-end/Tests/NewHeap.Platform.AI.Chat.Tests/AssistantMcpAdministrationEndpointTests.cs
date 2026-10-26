using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB9 and FB10 over HTTP, on both relational providers: <c>PUT admin/agents/{id}</c> keeps the MCP
/// assignments when its body omits <c>mcpServerIds</c>, <c>PUT</c> and
/// <c>DELETE admin/mcp-servers/{id}/agents/{agentId}</c> change one assignment with the service's codes
/// and audit behind the admin policy, and <c>PUT admin/mcp-servers/{id}/tools/{remoteName}</c> stores the
/// reviewed settings of a tool that no sync has seen yet.
/// </summary>
public sealed class AssistantMcpAdministrationEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string McpUrl = "http://localhost/planning/mcp";
    private const string Agent = "budget-helper";
    private const string AssignmentPath = "/api/assistant/admin/mcp-servers/planning/agents/" + Agent;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_save_without_mcp_server_ids_keeps_an_assignment_made_after_the_editor_opened(AssistantTestProvider provider)
    {
        var (app, business) = await StartAsync(provider);
        await using var _ = app;
        using var admin = app.CreateClient(admin: true);
        await CreateServerAsync(admin);
        // A new agent without mcpServerIds has no servers, as before.
        using var created = await admin.PostAsync("/api/assistant/admin/agents", Json(AgentBody("Answers budget questions.", version: null, servers: null)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Empty((await ReadJsonAsync(created)).GetProperty("mcpServerIds").EnumerateArray());

        // An editor holds version 1 while an administrator assigns the server: only the assignment changes.
        using var assigned = await admin.PutAsync(AssignmentPath, null);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal([Agent], (await ReadJsonAsync(assigned)).GetProperty("assignedAgentIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(1, (await AgentAsync(admin)).GetProperty("version").GetInt32());

        // The editor saves version 1 without the list: no conflict, and the assignment stays.
        using var saved = await admin.PutAsync("/api/assistant/admin/agents/" + Agent, Json(AgentBody("Answers budget questions briefly.", version: 1, servers: null)));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var kept = await ReadJsonAsync(saved);
        Assert.Equal((2, "Answers budget questions briefly."), (kept.GetProperty("version").GetInt32(), kept.GetProperty("description").GetString()));
        Assert.Equal(["planning"], kept.GetProperty("mcpServerIds").EnumerateArray().Select(item => item.GetString()));

        // An empty list still removes every assignment; a list still replaces them.
        using var cleared = await admin.PutAsync("/api/assistant/admin/agents/" + Agent, Json(AgentBody("Answers budget questions briefly.", version: 2, servers: [])));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await ReadJsonAsync(cleared)).GetProperty("mcpServerIds").EnumerateArray());
        using var replaced = await admin.PutAsync("/api/assistant/admin/agents/" + Agent, Json(AgentBody("Answers budget questions briefly.", version: 3, servers: ["planning"])));
        Assert.Equal(["planning"], (await ReadJsonAsync(replaced)).GetProperty("mcpServerIds").EnumerateArray().Select(item => item.GetString()));

        var assignment = Assert.Single(business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged);
        Assert.Equal(("planning", Agent, NhAssistantMcpAssignmentAudit.Assigned), (assignment.ObjectId, assignment.AgentId, assignment.ResultCode));
        Assert.Equal(3, business.Events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentUpdated && evt.ObjectId == Agent));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_assignment_endpoints_need_the_admin_policy_and_answer_with_the_services_codes(AssistantTestProvider provider)
    {
        var (app, business) = await StartAsync(provider);
        await using var _ = app;
        using var anonymous = app.CreateClient(user: null, access: false);
        using var user = app.CreateClient();
        using var admin = app.CreateClient(admin: true);
        await CreateServerAsync(admin);
        using (var created = await admin.PostAsync("/api/assistant/admin/agents", Json(AgentBody("Answers budget questions.", version: null, servers: []))))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var unauthenticated = await anonymous.PutAsync(AssignmentPath, null);
        using var forbidden = await user.PutAsync(AssignmentPath, null);
        using var forbiddenRemoval = await user.DeleteAsync(AssignmentPath);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("assistant-forbidden", (await ReadJsonAsync(forbidden)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRemoval.StatusCode);
        Assert.Empty((await AgentAsync(admin)).GetProperty("mcpServerIds").EnumerateArray());

        using var unknownServer = await admin.PutAsync("/api/assistant/admin/mcp-servers/unknown/agents/" + Agent, null);
        using var unknownAgent = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning/agents/unknown-agent", null);
        using var unknownRemoval = await admin.DeleteAsync("/api/assistant/admin/mcp-servers/planning/agents/unknown-agent");
        Assert.Equal((HttpStatusCode.NotFound, "assistant-mcp-server-not-found"), (unknownServer.StatusCode, (await ReadJsonAsync(unknownServer)).GetProperty("code").GetString()));
        Assert.Equal((HttpStatusCode.NotFound, "assistant-agent-not-found"), (unknownAgent.StatusCode, (await ReadJsonAsync(unknownAgent)).GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, unknownRemoval.StatusCode);

        // Removing an assignment the agent does not have, and assigning twice, change nothing more.
        using var notAssigned = await admin.DeleteAsync(AssignmentPath);
        using var first = await admin.PutAsync(AssignmentPath, null);
        using var again = await admin.PutAsync(AssignmentPath, null);
        Assert.Equal(HttpStatusCode.OK, notAssigned.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal([Agent], (await ReadJsonAsync(again)).GetProperty("assignedAgentIds").EnumerateArray().Select(item => item.GetString()));
        using var removed = await admin.DeleteAsync(AssignmentPath);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty((await ReadJsonAsync(removed)).GetProperty("assignedAgentIds").EnumerateArray());
        Assert.Equal(
            [NhAssistantMcpAssignmentAudit.Assigned, NhAssistantMcpAssignmentAudit.Unassigned],
            business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged).Select(evt => evt.ResultCode));
        Assert.Equal(1, (await AgentAsync(admin)).GetProperty("version").GetInt32());

        // An agent with as many servers as allowed gets no more.
        await using (var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < NhAssistantMcpServerLimits.MaxServersPerAgent; index++)
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
                context.AgentMcpServers.Add(new AssistantAgentMcpServer { AgentId = Agent, McpServerId = "server-" + index });
            }

            await context.SaveChangesAsync();
        }

        using var full = await admin.PutAsync(AssignmentPath, null);
        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
        var fullBody = await ReadJsonAsync(full);
        Assert.Equal("too-long", fullBody.GetProperty("errors").GetProperty("mcpServerIds")[0].GetString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_tool_that_no_sync_has_seen_is_reviewed_over_http_and_listed_as_awaiting_its_sync(AssistantTestProvider provider)
    {
        var (app, business) = await StartAsync(provider);
        await using var _ = app;
        using var user = app.CreateClient();
        using var admin = app.CreateClient(admin: true);
        await CreateServerAsync(admin);
        var review = Json(new { isEnabled = true, effect = "read-only", descriptionOverride = "Find the planning of one order." });

        using var forbidden = await user.PutAsync("/api/assistant/admin/mcp-servers/planning/tools/Lookup_Order", review);
        using var reviewed = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/planning/tools/Lookup_Order",
            Json(new { isEnabled = true, effect = "read-only", descriptionOverride = "Find the planning of one order." }));
        using var tooLong = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/planning/tools/" + new string('a', 129),
            Json(new { isEnabled = false, effect = "mutation", descriptionOverride = (string?)null }));
        using var unknownServer = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/unknown/tools/Lookup_Order",
            Json(new { isEnabled = false, effect = "mutation", descriptionOverride = (string?)null }));
        var tools = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/mcp-servers/planning/tools"));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var tool = await ReadJsonAsync(reviewed);
        Assert.Equal(
            ("missing", true, true, "read-only", "mcp.planning.lookup-order"),
            (tool.GetProperty("status").GetString(), tool.GetProperty("awaitingSync").GetBoolean(), tool.GetProperty("isEnabled").GetBoolean(),
                tool.GetProperty("effect").GetString(), tool.GetProperty("localId").GetString()));
        Assert.Equal(JsonValueKind.Null, tool.GetProperty("readOnlyHint").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("too-long", (await ReadJsonAsync(tooLong)).GetProperty("errors").GetProperty("remoteName")[0].GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknownServer.StatusCode);
        Assert.Equal("assistant-mcp-server-not-found", (await ReadJsonAsync(unknownServer)).GetProperty("code").GetString());
        var listed = Assert.Single(tools.EnumerateArray());
        Assert.True(listed.GetProperty("awaitingSync").GetBoolean());
        var audited = Assert.Single(business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolReviewedBeforeSync);
        Assert.Equal(("planning/Lookup_Order", "user-1", NhAssistantMcpToolAudit.Enabled), (audited.ObjectId, audited.ActorId, audited.ResultCode));
    }

    private async Task<(AssistantWebApplication App, CapturedBusinessAuditSink Business)> StartAsync(AssistantTestProvider provider)
    {
        var business = new CapturedBusinessAuditSink();
        var servers = new InMemoryMcpServers();
        servers.Register(McpUrl, () => servers.PlanningTools());
        var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers));
                services.AddSingleton(business);
                services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
            },
            provider: provider);
        return (app, business);
    }

    private static async Task CreateServerAsync(HttpClient admin)
    {
        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(new
        {
            id = "planning",
            displayName = "Planning",
            url = McpUrl,
            authMode = "bearer",
            headerName = (string?)null,
            secret = "SECRET-PLANNING-TOKEN-4713",
            requiredPolicy = (string?)null,
            isEnabled = true
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    /// <summary>
    /// The body of the agent editor: <paramref name="servers"/> <see langword="null"/> leaves
    /// <c>mcpServerIds</c> out, as the editor does on an update.
    /// </summary>
    private static Dictionary<string, object?> AgentBody(string description, int? version, string[]? servers)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = Agent,
            ["displayName"] = "Budget helper",
            ["description"] = description,
            ["instructions"] = "Explain budgets briefly.",
            ["toolSelectors"] = new[] { "projects.search" },
            ["requiredPolicy"] = null,
            ["autonomy"] = "observe"
        };
        if (version is { } expected)
        {
            body["expectedVersion"] = expected;
        }
        if (servers is not null)
        {
            body["mcpServerIds"] = servers;
        }

        return body;
    }

    private static async Task<JsonElement> AgentAsync(HttpClient admin)
    {
        var agents = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"));
        return agents.EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == Agent);
    }

    private static StringContent Json(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value, Web), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
