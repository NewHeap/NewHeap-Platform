using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for assistant administration and personalization (SPM-250, SPM-251,
/// SPM-252) over the assistant HTTP API of the sample composition.
/// </summary>
public sealed partial class AssistantSamplesTests
{
    [Fact]
    public async Task SPM_250_an_administrator_creates_an_agent_that_users_can_use()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "portfolio", limit = 5 } })
            .RespondWithText("The portfolio has one roadmap project.");
        Host.Model.Use(model);
        using var admin = Host.CreateClient("spm-250-admin", admin: true);
        using var user = Host.CreateClient("spm-250-user");

        using var created = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "portfolio-reviewer",
            displayName = "Portfolio reviewer",
            description = "Reviews the project portfolio of the active division.",
            instructions = "Review the project portfolio and summarize it in one sentence.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        }), TestContext.Current.CancellationToken);
        using var forbidden = await user.GetAsync("/api/assistant/admin/agents", TestContext.Current.CancellationToken);
        var agents = await Host.GetJsonAsync(user, "/api/assistant/agents");
        var conversationId = await CreateConversationAsync(user, "portfolio-reviewer");
        var events = await Host.SendAsync(user, conversationId, "Review the portfolio.");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains(agents.EnumerateArray(), agent => agent.GetProperty("displayNameKey").GetString() == "Portfolio reviewer");
        Assert.Equal("succeeded", events.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        Assert.Contains("Review the project portfolio", model.Requests[0].Options!.Instructions);
        Assert.DoesNotContain(model.Requests[0].Options!.Tools!, tool => tool.Name == "projects_change_status_v1");
        Assert.Contains(
            Host.Services.GetRequiredService<SampleAssistantAuditLog>().Events,
            evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentCreated && evt.ObjectId == "portfolio-reviewer");
    }

    [Fact]
    public async Task SPM_250_the_application_decides_how_many_tool_selectors_an_agent_may_list()
    {
        using var admin = Host.CreateClient("spm-250-selectors-admin", admin: true);
        string[] settingsActions = [.. Enumerable.Range(0, 200).Select(index => $"settings.action-{index:000}")];

        using var withinLimit = await admin.PostAsync(
            "/api/assistant/admin/agents",
            AgentWithSelectors("settings-assistant", settingsActions),
            TestContext.Current.CancellationToken);
        using var aboveLimit = await admin.PostAsync(
            "/api/assistant/admin/agents",
            AgentWithSelectors(
                "settings-assistant-too-many",
                [.. Enumerable.Range(0, 257).Select(index => $"settings.a-{index:000}")]),
            TestContext.Current.CancellationToken);

        // The sample raises MaxToolSelectorsPerAgent from 128 to 256 in SampleAssistantComposition.
        Assert.Equal(HttpStatusCode.Created, withinLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, aboveLimit.StatusCode);
    }

    private static HttpContent AgentWithSelectors(string id, string[] toolSelectors)
    {
        return AssistantSampleHost.Json(new
        {
            id,
            displayName = "Settings assistant",
            description = "Changes application settings on request.",
            instructions = "Change application settings when asked.",
            toolSelectors,
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        });
    }

    [Fact]
    public async Task SPM_251_an_administrator_connects_an_mcp_server_and_assigns_it_to_one_agent()
    {
        await using var planning = await SampleMcpServer.StartAsync("sample-planning-key");
        using var admin = Host.CreateClient("spm-251-admin", admin: true);
        using var user = Host.CreateClient("spm-251-user");

        using var server = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(new
        {
            id = "planning",
            displayName = "Planning",
            url = planning.Url,
            authMode = "api-key",
            headerName = (string?)null,
            secret = "sample-planning-key",
            requiredPolicy = (string?)null,
            isEnabled = true
        }), TestContext.Current.CancellationToken);
        using var sync = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/sync", null, TestContext.Current.CancellationToken);
        var synced = await AssistantSampleHost.ReadJsonAsync(sync);
        using var enable = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/planning/tools/next_milestone",
            AssistantSampleHost.Json(new { isEnabled = true, effect = "read-only", descriptionOverride = (string?)null }),
            TestContext.Current.CancellationToken);
        using var agent = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "planning-helper",
            displayName = "Planning helper",
            description = "Answers milestone questions.",
            instructions = "Answer milestone questions with the planning tool.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = new[] { "planning" },
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        }), TestContext.Current.CancellationToken);

        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("mcp_planning_next_milestone", new { project = "PRJ-1" })
            .RespondWithText("PRJ-1 reaches its next milestone on Friday.")
            .RespondWithText("I can search projects.");
        Host.Model.Use(model);
        var helperConversation = await CreateConversationAsync(user, "planning-helper");
        var helperEvents = await Host.SendAsync(user, helperConversation, "When is the next milestone of PRJ-1?");
        var otherConversation = await Host.CreateConversationAsync(user);
        await Host.SendAsync(user, otherConversation, "Which tools do you have?");

        Assert.Equal(HttpStatusCode.Created, server.StatusCode);
        Assert.False(synced.EnumerateArray().Single().GetProperty("isEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(HttpStatusCode.Created, agent.StatusCode);
        var started = helperEvents.Single(evt => evt.Name == "tool.started").Data;
        Assert.Equal("mcp.planning.next-milestone", started.GetProperty("toolId").GetString());
        Assert.Equal("succeeded", helperEvents.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        Assert.Equal(["PRJ-1"], planning.Calls);
        Assert.All(planning.ApiKeys, key => Assert.Equal("sample-planning-key", key));
        Assert.DoesNotContain(model.Requests[^1].Options!.Tools!, tool => tool.Name == "mcp_planning_next_milestone");
    }

    [Fact]
    public async Task SPM_251_mcp_secrets_can_stay_with_the_application_and_basic_authentication_sends_a_user_name()
    {
        // SPM-251: an administrator names a secret that the application holds instead of storing
        // one. config:<name> reads NewHeap:AI:Assistant:Mcp:Secrets:<name>, and secret-file:<name>
        // reads a mounted secret file through the sample's resolver. Both are read for every
        // connection, so a rotated secret applies at once; a reference never reaches other settings.
        var configuration = Host.Services.GetRequiredService<IConfiguration>();
        var secrets = Directory.CreateTempSubdirectory("spm-251-secrets-");
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("planner:spm-251-basic-password"));
        await using var planning = await SampleMcpServer.StartAsync(request =>
            request.Headers["X-Api-Key"].ToString() is "spm-251-configured-key" or "spm-251-mounted-key" or "spm-251-rotated-key"
            || request.Headers.Authorization.ToString() == basic);
        using var admin = Host.CreateClient("spm-251-secrets-admin", admin: true);
        var server = new
        {
            id = "secured-planning",
            displayName = "Secured planning",
            url = planning.Url,
            authMode = "api-key",
            headerName = (string?)null,
            requiredPolicy = (string?)null,
            isEnabled = true,
            secretReference = "config:spm-251-key"
        };
        try
        {
            configuration[NhAssistantMcpSecretReferences.ConfigurationSection + ":spm-251-key"] = "spm-251-configured-key";
            configuration[SampleMountedSecretResolver.DirectoryKey] = secrets.FullName;
            await File.WriteAllTextAsync(Path.Combine(secrets.FullName, "spm-251-key"), "spm-251-mounted-key", TestContext.Current.CancellationToken);

            using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(server), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var stored = await AssistantSampleHost.ReadJsonAsync(created);
            Assert.False(stored.GetProperty("hasSecret").GetBoolean());
            Assert.Equal("config:spm-251-key", stored.GetProperty("secretReference").GetString());
            Assert.True(await TestAsync(admin, "secured-planning"));
            Assert.Equal("spm-251-configured-key", planning.ApiKeys.Last());

            using var mounted = await admin.PutAsync(
                "/api/assistant/admin/mcp-servers/secured-planning",
                AssistantSampleHost.Json(server with { secretReference = "secret-file:spm-251-key" }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, mounted.StatusCode);
            Assert.True(await TestAsync(admin, "secured-planning"));
            Assert.Equal("spm-251-mounted-key", planning.ApiKeys.Last());

            // The platform rotates the mounted file; the next connection sends the new key.
            await File.WriteAllTextAsync(Path.Combine(secrets.FullName, "spm-251-key"), "spm-251-rotated-key", TestContext.Current.CancellationToken);
            Assert.True(await TestAsync(admin, "secured-planning"));
            Assert.Equal("spm-251-rotated-key", planning.ApiKeys.Last());

            using var otherSetting = await admin.PutAsync(
                "/api/assistant/admin/mcp-servers/secured-planning",
                AssistantSampleHost.Json(server with { secretReference = "config:ConnectionStrings:DefaultConnection" }),
                TestContext.Current.CancellationToken);
            using var climbing = await admin.PutAsync(
                "/api/assistant/admin/mcp-servers/secured-planning",
                AssistantSampleHost.Json(server with { secretReference = "secret-file:../spm-251-key" }),
                TestContext.Current.CancellationToken);
            using var missing = await admin.PutAsync(
                "/api/assistant/admin/mcp-servers/secured-planning",
                AssistantSampleHost.Json(server with { secretReference = "secret-file:spm-251-missing" }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, otherSetting.StatusCode);
            Assert.Equal("invalid", (await AssistantSampleHost.ReadJsonAsync(otherSetting)).GetProperty("errors").GetProperty("secretReference")[0].GetString());
            Assert.Equal(HttpStatusCode.BadRequest, climbing.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("assistant-mcp-secret-reference-invalid", (await AssistantSampleHost.ReadJsonAsync(missing)).GetProperty("code").GetString());

            // Basic authentication: the user name is shown, the password is a write-only secret that
            // replaces the reference.
            using var withBasic = await admin.PutAsync("/api/assistant/admin/mcp-servers/secured-planning", AssistantSampleHost.Json(new
            {
                server.id,
                server.displayName,
                server.url,
                authMode = "basic",
                userName = "planner",
                secret = "spm-251-basic-password",
                server.requiredPolicy,
                server.isEnabled
            }), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, withBasic.StatusCode);
            var basicServer = await AssistantSampleHost.ReadJsonAsync(withBasic);
            Assert.Equal("planner", basicServer.GetProperty("userName").GetString());
            Assert.True(basicServer.GetProperty("hasSecret").GetBoolean());
            Assert.Equal(JsonValueKind.Null, basicServer.GetProperty("secretReference").ValueKind);
            Assert.True(await TestAsync(admin, "secured-planning"));
            Assert.Equal(basic, planning.Authorizations.Last());

            var listed = await (await admin.GetAsync("/api/assistant/admin/mcp-servers", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("spm-251-basic-password", listed);
            Assert.DoesNotContain("spm-251-mounted-key", listed);
            Assert.DoesNotContain("spm-251-configured-key", listed);
        }
        finally
        {
            using var deleted = await admin.DeleteAsync("/api/assistant/admin/mcp-servers/secured-planning", TestContext.Current.CancellationToken);
            configuration[SampleMountedSecretResolver.DirectoryKey] = null;
            secrets.Delete(recursive: true);
        }
    }

    private static async Task<bool> TestAsync(HttpClient admin, string serverId)
    {
        using var response = await admin.PostAsync($"/api/assistant/admin/mcp-servers/{serverId}/test", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await AssistantSampleHost.ReadJsonAsync(response)).GetProperty("ok").GetBoolean();
    }

    [Fact]
    public async Task SPM_252_an_edited_context_and_personal_preferences_shape_the_instructions()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Graag gedaan.");
        Host.Model.Use(model);
        using var admin = Host.CreateClient("spm-252-admin", admin: true);
        using var user = Host.CreateClient("spm-252-user");
        user.DefaultRequestHeaders.AcceptLanguage.Clear();
        user.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");

        var seeded = await Host.GetJsonAsync(admin, "/api/assistant/admin/context");
        var version = seeded.GetProperty("version").GetInt32();
        using var edited = await admin.PutAsync("/api/assistant/admin/context", AssistantSampleHost.Json(new
        {
            text = seeded.GetProperty("text").GetString() + "\nAlways mention the project key.",
            expectedVersion = version
        }), TestContext.Current.CancellationToken);
        using var preferences = await user.PutAsync("/api/assistant/preferences", AssistantSampleHost.Json(new
        {
            style = "direct",
            addressForm = "formal",
            responseLength = "short",
            customInstructions = "Answer in bullet points."
        }), TestContext.Current.CancellationToken);
        var conversationId = await Host.CreateConversationAsync(user);
        await Host.SendAsync(user, conversationId, "Which projects are running?");

        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.Contains("Sample Project Management tracks projects per division.", seeded.GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(version + 1, (await AssistantSampleHost.ReadJsonAsync(edited)).GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.OK, preferences.StatusCode);
        Assert.Contains("Always mention the project key.", instructions);
        Assert.Contains("aan met u.", instructions); // Dutch formal address line, chosen from Accept-Language.
        Assert.Contains("Answer in bullet points.", instructions);
        Assert.True(instructions.IndexOf("# Application context", StringComparison.Ordinal)
            < instructions.IndexOf("# User preferences", StringComparison.Ordinal));
        Assert.Contains(
            Host.Services.GetRequiredService<SampleAssistantAuditLog>().Events,
            evt => evt.Kind == NhAssistantAuditEventKind.AdminContextUpdated);
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client, string agentId)
    {
        using var response = await client.PostAsync(
            "/api/assistant/conversations",
            AssistantSampleHost.Json(new { agentId }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await AssistantSampleHost.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }
}

/// <summary>
/// A real Streamable HTTP MCP server built with the official SDK. It requires an API key, or the
/// credential a test accepts, and records the API keys and `Authorization` headers it receives.
/// </summary>
public sealed class SampleMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleMcpServer(
        WebApplication app,
        ConcurrentQueue<string> calls,
        ConcurrentQueue<string> apiKeys,
        ConcurrentQueue<string> authorizations)
    {
        _app = app;
        Calls = calls;
        ApiKeys = apiKeys;
        Authorizations = authorizations;
        Url = app.Urls.First() + "/mcp";
    }

    public string Url { get; }

    public ConcurrentQueue<string> Calls { get; }

    public ConcurrentQueue<string> ApiKeys { get; }

    public ConcurrentQueue<string> Authorizations { get; }

    public static Task<SampleMcpServer> StartAsync(string apiKey)
    {
        return StartAsync(request => string.Equals(request.Headers["X-Api-Key"].ToString(), apiKey, StringComparison.Ordinal));
    }

    /// <summary>A server that answers only the requests <paramref name="accepts"/> allows.</summary>
    public static async Task<SampleMcpServer> StartAsync(Func<HttpRequest, bool> accepts)
    {
        var calls = new ConcurrentQueue<string>();
        var apiKeys = new ConcurrentQueue<string>();
        var authorizations = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(
            [
                McpServerTool.Create(
                    (string project) =>
                    {
                        calls.Enqueue(project);
                        return $"The next milestone of {project} is on Friday.";
                    },
                    new McpServerToolCreateOptions
                    {
                        Name = "next_milestone",
                        Description = "Returns the next milestone of a project.",
                        ReadOnly = true
                    })
            ]);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            apiKeys.Enqueue(context.Request.Headers["X-Api-Key"].ToString());
            authorizations.Enqueue(context.Request.Headers.Authorization.ToString());
            if (!accepts(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new SampleMcpServer(app, calls, apiKeys, authorizations);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
