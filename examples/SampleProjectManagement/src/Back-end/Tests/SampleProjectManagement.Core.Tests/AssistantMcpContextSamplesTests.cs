using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-266: a reviewed research MCP tool receives the assistant context as
/// request metadata over Streamable HTTP, while the same tool connected under another server id
/// receives nothing.
/// </summary>
public sealed class AssistantMcpContextSamplesTests(AssistantResearchSampleHost host)
    : IClassFixture<AssistantResearchSampleHost>
{
    [Fact]
    public async Task SPM_266_a_reviewed_research_tool_receives_the_assistant_context_and_no_other_server_does()
    {
        using var admin = host.CreateClient("spm-266-admin", admin: true);
        using var user = host.CreateClient("spm-266-user");
        await ConnectAsync(admin, SampleAssistantResearch.ServerId, host.Research.Url);
        await ConnectAsync(admin, "research-copy", host.Research.Url);
        using var agent = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "research-helper",
            displayName = "Research helper",
            description = "Hands questions to the research service.",
            instructions = "Ask the research service when the user needs research.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = new[] { SampleAssistantResearch.ServerId, "research-copy" },
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, agent.StatusCode);

        host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithText("Alpha migrates the CRM to the new platform.")
            .RespondWithFunctionCall("mcp_research_research_ask", new { question = "Which risks block Alpha?" })
            .RespondWithText("The research service is looking into it.")
            .RespondWithFunctionCall("mcp_research_copy_research_ask", new { question = "Which risks block Alpha?" })
            .RespondWithText("The copy is looking into it too."));
        using var created = await user.PostAsync(
            "/api/assistant/conversations",
            AssistantSampleHost.Json(new { agentId = "research-helper" }),
            TestContext.Current.CancellationToken);
        var conversationId = (await AssistantSampleHost.ReadJsonAsync(created)).GetProperty("id").GetGuid();
        await host.SendAsync(user, conversationId, "What is the scope of Alpha?");
        var asked = await host.SendAsync(user, conversationId, "Ask research which risks block Alpha.");
        await host.SendAsync(user, conversationId, "Ask the copy as well.");

        Assert.Equal("succeeded", asked.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        var calls = host.Research.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        // The model-facing schema only asks for the question; the context travels in _meta.
        Assert.All(calls, call => Assert.Equal(["question"], call.Arguments.Keys));
        var context = calls[0].Context!;
        Assert.Equal("agent", context["invocation"]!["actor"]!["kind"]!.GetValue<string>());
        Assert.Equal("assistant-agent:research-helper", context["invocation"]!["actor"]!["id"]!.GetValue<string>());
        Assert.Equal("spm-266-user", context["invocation"]!["accountableOwnerId"]!.GetValue<string>());
        Assert.Equal(conversationId.ToString(), context["conversation"]!["id"]!.GetValue<string>());
        Assert.Equal("What is the scope of Alpha?", context["snapshot"]!["firstQuestion"]!["text"]!.GetValue<string>());
        Assert.Equal(
            ["Alpha migrates the CRM to the new platform.", "Ask research which risks block Alpha."],
            context["snapshot"]!["recentMessages"]!.AsArray().Select(message => message!["text"]!.GetValue<string>()));
        // The same remote tool under another server id was never reviewed for the context.
        Assert.Null(calls[1].Context);
    }

    private static async Task ConnectAsync(HttpClient admin, string serverId, string url)
    {
        using var server = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(new
        {
            id = serverId,
            displayName = "Research",
            url,
            authMode = "none",
            headerName = (string?)null,
            secret = (string?)null,
            requiredPolicy = (string?)null,
            isEnabled = true
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, server.StatusCode);
        using var sync = await admin.PostAsync($"/api/assistant/admin/mcp-servers/{serverId}/sync", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using var enable = await admin.PutAsync(
            $"/api/assistant/admin/mcp-servers/{serverId}/tools/{SampleAssistantResearch.ToolName}",
            AssistantSampleHost.Json(new { isEnabled = true, effect = "read-only", descriptionOverride = (string?)null }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
    }
}

/// <summary>
/// The assistant sample host with a research MCP server whose URL the composition reviews through
/// <see cref="SampleAssistantResearch.UrlSetting"/>.
/// </summary>
public sealed class AssistantResearchSampleHost : AssistantSampleHost
{
    public SampleResearchMcpServer Research { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        // The research server starts first, so the composition can review its URL.
        Research = await SampleResearchMcpServer.StartAsync();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Research.DisposeAsync();
    }

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        settings[SampleAssistantResearch.UrlSetting] = Research.Url;
    }
}

/// <summary>
/// A Streamable HTTP research server built with the official SDK. Its tool schema only has the
/// question, like a version 2 research contract; it reads the assistant context from <c>_meta</c>.
/// </summary>
public sealed class SampleResearchMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleResearchMcpServer(WebApplication app, ConcurrentQueue<(JsonObject? Context, IReadOnlyDictionary<string, JsonElement> Arguments)> calls)
    {
        _app = app;
        Calls = calls;
        Url = app.Urls.First() + "/mcp";
    }

    public string Url { get; }

    public ConcurrentQueue<(JsonObject? Context, IReadOnlyDictionary<string, JsonElement> Arguments)> Calls { get; }

    public static async Task<SampleResearchMcpServer> StartAsync()
    {
        var calls = new ConcurrentQueue<(JsonObject? Context, IReadOnlyDictionary<string, JsonElement> Arguments)>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool
                    {
                        Name = SampleAssistantResearch.ToolName,
                        Description = "Asks the research service a question.",
                        InputSchema = JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            properties = new { question = new { type = "string" } },
                            required = new[] { "question" }
                        }),
                        Annotations = new ToolAnnotations { ReadOnlyHint = true }
                    }
                ]
            }))
            .WithCallToolHandler((request, _) =>
            {
                calls.Enqueue((
                    request.Params?.Meta?[NhAssistantMcpContext.MetaKey]?.DeepClone() as JsonObject,
                    new Dictionary<string, JsonElement>(request.Params?.Arguments ?? new Dictionary<string, JsonElement>())));
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "The research request is queued." }]
                });
            });
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new SampleResearchMcpServer(app, calls);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
