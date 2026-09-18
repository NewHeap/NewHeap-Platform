using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantAdminEndpointTests(AssistantDatabaseFixture database)
{
    private const string McpUrl = "http://localhost/planning/mcp";

    [Fact]
    public async Task Preferences_are_personal_validated_and_follow_the_contract()
    {
        await using var app = await StartAsync();
        using var user = app.CreateClient("user-1");
        using var other = app.CreateClient("user-2");

        var defaults = await ReadJsonAsync(await user.GetAsync("/api/assistant/preferences"));
        using var saved = await user.PutAsync("/api/assistant/preferences", Json(new
        {
            style = "direct",
            addressForm = "formal",
            responseLength = "short",
            customInstructions = "Use bullet points."
        }));
        using var invalid = await user.PutAsync("/api/assistant/preferences", Json(new
        {
            style = "loud",
            addressForm = "formal",
            responseLength = "short",
            customInstructions = (string?)null
        }));
        var otherDefaults = await ReadJsonAsync(await other.GetAsync("/api/assistant/preferences"));

        AssertNames(defaults, "style", "addressForm", "responseLength", "customInstructions");
        Assert.Equal("default", defaults.GetProperty("style").GetString());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Use bullet points.", (await ReadJsonAsync(saved)).GetProperty("customInstructions").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("default", otherDefaults.GetProperty("style").GetString());
    }

    [Fact]
    public async Task Administration_requires_the_admin_policy_and_status_reports_it()
    {
        await using var app = await StartAsync();
        using var anonymous = app.CreateClient(user: null, access: false);
        using var user = app.CreateClient();
        using var admin = app.CreateClient(admin: true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/assistant/admin/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/assistant/admin/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/assistant/admin/agents")).StatusCode);
        Assert.False((await ReadJsonAsync(await user.GetAsync("/api/assistant/status"))).GetProperty("canAdminister").GetBoolean());
        Assert.True((await ReadJsonAsync(await admin.GetAsync("/api/assistant/status"))).GetProperty("canAdminister").GetBoolean());
    }

    [Fact]
    public async Task A_disabled_assistant_hides_preferences_and_administration()
    {
        await using var app = await StartAsync(enabled: false);
        using var admin = app.CreateClient(admin: true);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/assistant/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/assistant/admin/context")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/assistant/admin/mcp-servers")).StatusCode);
        Assert.False((await ReadJsonAsync(await admin.GetAsync("/api/assistant/status"))).GetProperty("canAdminister").GetBoolean());
    }

    [Fact]
    public async Task The_application_context_is_versioned_over_http()
    {
        await using var app = await StartAsync();
        using var admin = app.CreateClient(admin: true);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/assistant/admin/context")).StatusCode);
        using var first = await admin.PutAsync("/api/assistant/admin/context", Json(new { text = "Projects belong to divisions.", expectedVersion = 0 }));
        using var stale = await admin.PutAsync("/api/assistant/admin/context", Json(new { text = "Stale.", expectedVersion = 0 }));
        using var second = await admin.PutAsync("/api/assistant/admin/context", Json(new { text = "Projects and tasks belong to divisions.", expectedVersion = 1 }));
        var current = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/context"));
        var versions = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/context/versions"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("assistant-version-conflict", (await ReadJsonAsync(stale)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        AssertNames(current, "text", "version", "hash", "updatedAt", "updatedBy");
        Assert.Equal(2, current.GetProperty("version").GetInt32());
        Assert.Equal("user-1", current.GetProperty("updatedBy").GetString());
        Assert.Equal(2, versions.GetArrayLength());
        AssertNames(versions[0], "version", "hash", "updatedAt", "updatedBy");
    }

    [Fact]
    public async Task Agents_are_administered_with_versions_sources_and_validation()
    {
        await using var app = await StartAsync();
        using var admin = app.CreateClient(admin: true);
        var agentInput = new
        {
            id = "budget-helper",
            displayName = "Budget helper",
            description = "Answers budget questions.",
            instructions = "Explain budgets briefly.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        };

        var list = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"));
        using var created = await admin.PostAsync("/api/assistant/admin/agents", Json(agentInput));
        using var duplicate = await admin.PostAsync("/api/assistant/admin/agents", Json(agentInput));
        using var badSelector = await admin.PostAsync("/api/assistant/admin/agents", Json(agentInput with { id = "x-agent", toolSelectors = new[] { "*" } }));
        using var unknownPolicy = await admin.PostAsync("/api/assistant/admin/agents", Json(agentInput with { id = "y-agent", requiredPolicy = "app.unknown" }));
        using var noVersion = await admin.PutAsync("/api/assistant/admin/agents/budget-helper", Json(agentInput));
        using var updated = await admin.PutAsync("/api/assistant/admin/agents/budget-helper", Json(new
        {
            agentInput.id, agentInput.displayName, agentInput.description, instructions = "Explain budgets in one sentence.",
            agentInput.toolSelectors, agentInput.mcpServerIds, agentInput.requiredPolicy, agentInput.autonomy, agentInput.isEnabled,
            expectedVersion = 1
        }));
        using var stale = await admin.PutAsync("/api/assistant/admin/agents/budget-helper", Json(new
        {
            agentInput.id, agentInput.displayName, agentInput.description, agentInput.instructions,
            agentInput.toolSelectors, agentInput.mcpServerIds, agentInput.requiredPolicy, agentInput.autonomy, agentInput.isEnabled,
            expectedVersion = 1
        }));
        using var resetAdmin = await admin.PostAsync("/api/assistant/admin/agents/budget-helper/reset", null);
        using var resetCode = await admin.PostAsync("/api/assistant/admin/agents/project-assistant/reset", null);
        using var deleteCode = await admin.DeleteAsync("/api/assistant/admin/agents/project-assistant");
        var visible = await ReadJsonAsync(await admin.GetAsync("/api/assistant/agents"));
        using var deleteAdmin = await admin.DeleteAsync("/api/assistant/admin/agents/budget-helper");

        var code = list.EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == "project-assistant");
        AssertNames(code, "id", "displayName", "description", "instructions", "toolSelectors", "mcpServerIds", "requiredPolicy",
            "autonomy", "isEnabled", "version", "source", "isOverridden", "instructionsHash", "updatedAt");
        Assert.Equal("code", code.GetProperty("source").GetString());
        Assert.Equal("execute", code.GetProperty("autonomy").GetString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("admin", (await ReadJsonAsync(created)).GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badSelector.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownPolicy.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await ReadJsonAsync(updated)).GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, resetAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resetCode.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteCode.StatusCode);
        Assert.Contains(visible.EnumerateArray(), agent => agent.GetProperty("displayNameKey").GetString() == "Budget helper");
        Assert.Equal(HttpStatusCode.NoContent, deleteAdmin.StatusCode);
    }

    [Fact]
    public async Task Mcp_servers_and_tools_are_administered_without_exposing_secrets()
    {
        const string secret = "SECRET-HTTP-TOKEN-99";
        var servers = new InMemoryMcpServers();
        servers.Register(McpUrl, () => servers.PlanningTools());
        await using var app = await StartAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)));
        using var admin = app.CreateClient(admin: true);
        var input = new
        {
            id = "planning",
            displayName = "Planning",
            url = McpUrl,
            authMode = "bearer",
            headerName = (string?)null,
            secret,
            requiredPolicy = (string?)null,
            isEnabled = true
        };

        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(input));
        using var blocked = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(input with { id = "metadata", url = "http://169.254.169.254/mcp" }));
        using var updated = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(input with { displayName = "Planning 2", secret = (string?)null }));
        var test = await ReadJsonAsync(await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/test", null));
        using var sync = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/sync", null);
        using var enable = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/planning/tools/Lookup_Order",
            Json(new { isEnabled = true, effect = "read-only", descriptionOverride = "Find the planning of one order." }));
        var tools = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/mcp-servers/planning/tools"));
        var catalog = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/tools"));
        var listText = await (await admin.GetAsync("/api/assistant/admin/mcp-servers")).Content.ReadAsStringAsync();
        using var deleted = await admin.DeleteAsync("/api/assistant/admin/mcp-servers/planning");
        using var missing = await admin.GetAsync("/api/assistant/admin/mcp-servers/planning/tools");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var server = await ReadJsonAsync(created);
        AssertNames(server, "id", "displayName", "url", "authMode", "headerName", "hasSecret", "requiredPolicy", "isEnabled",
            "lastSyncAt", "lastSyncStatus", "assignedAgentIds");
        Assert.True(server.GetProperty("hasSecret").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal("assistant-mcp-host-blocked", (await ReadJsonAsync(blocked)).GetProperty("code").GetString());
        Assert.True((await ReadJsonAsync(updated)).GetProperty("hasSecret").GetBoolean());
        AssertNames(test, "ok", "code", "toolCount");
        Assert.True(test.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        var lookup = tools.EnumerateArray().Single(tool => tool.GetProperty("remoteName").GetString() == "Lookup_Order");
        AssertNames(lookup, "remoteName", "localId", "description", "descriptionOverride", "isEnabled", "effect", "status", "readOnlyHint");
        Assert.True(lookup.GetProperty("isEnabled").GetBoolean());
        Assert.True(lookup.GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal("mcp.planning.lookup-order", lookup.GetProperty("localId").GetString());
        Assert.False(tools.EnumerateArray().Single(tool => tool.GetProperty("remoteName").GetString() == "reschedule").GetProperty("isEnabled").GetBoolean());
        AssertNames(catalog[0], "id", "source", "effect", "description");
        Assert.Contains(catalog.EnumerateArray(), entry => entry.GetProperty("id").GetString() == "projects.search" && entry.GetProperty("source").GetString() == "local");
        Assert.Contains(catalog.EnumerateArray(), entry => entry.GetProperty("id").GetString() == "mcp.planning.lookup-order" && entry.GetProperty("source").GetString() == "mcp");
        Assert.DoesNotContain(secret, listText);
        Assert.DoesNotContain(secret, await sync.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Bearer " + secret, servers.Connections.Last().Headers["Authorization"]);
    }

    private Task<AssistantWebApplication> StartAsync(bool enabled = true, Action<IServiceCollection>? configure = null)
    {
        return AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient(), enabled, configure: configure);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertNames(JsonElement element, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }
}
