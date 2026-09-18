using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
    public async Task SPM_252_an_edited_context_and_personal_preferences_shape_the_instructions()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Graag gedaan.");
        Host.Model.Use(model);
        using var admin = Host.CreateClient("spm-252-admin", admin: true);
        using var user = Host.CreateClient("spm-252-user");
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
/// A real Streamable HTTP MCP server built with the official SDK. It requires an API key.
/// </summary>
public sealed class SampleMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleMcpServer(WebApplication app, ConcurrentQueue<string> calls, ConcurrentQueue<string> apiKeys)
    {
        _app = app;
        Calls = calls;
        ApiKeys = apiKeys;
        Url = app.Urls.First() + "/mcp";
    }

    public string Url { get; }

    public ConcurrentQueue<string> Calls { get; }

    public ConcurrentQueue<string> ApiKeys { get; }

    public static async Task<SampleMcpServer> StartAsync(string apiKey)
    {
        var calls = new ConcurrentQueue<string>();
        var apiKeys = new ConcurrentQueue<string>();
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
            var presented = context.Request.Headers["X-Api-Key"].ToString();
            apiKeys.Enqueue(presented);
            if (!string.Equals(presented, apiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new SampleMcpServer(app, calls, apiKeys);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
