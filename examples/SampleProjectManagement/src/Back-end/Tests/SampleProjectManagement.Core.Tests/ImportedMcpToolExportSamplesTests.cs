using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-399: with the opt-in, the API offers the reviewed research tools on its own
/// /mcp to a person's AI app. A question and its withdrawal, which the person's app confirms, reach the
/// research service as that person, with the assistant context of an exported call and no conversation;
/// the withdrawal carries the operation key the app sent. The same service connected under another id
/// stays with the assistant.
/// </summary>
public sealed class ImportedMcpToolExportSamplesTests(ImportedMcpToolExportSampleHost host)
    : IClassFixture<ImportedMcpToolExportSampleHost>
{
    [Fact]
    public async Task SPM_399_a_person_calls_the_reviewed_research_tool_from_their_own_ai_app_as_themselves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var admin = host.CreateClient("spm-399-admin", admin: true);
        await ConnectAsync(admin, SampleAssistantResearch.ServerId, host.Research.Url, withdraw: true);
        await ConnectAsync(admin, "research-copy", host.Research.Url);

        var http = host.CreateClient("spm-399-user");
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(host.BaseAddress, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                http,
                ownsHttpClient: true),
            new McpClientOptions { ProtocolVersion = "2025-11-25" },
            cancellationToken: cancellationToken);

        // Once: the copy under another id is never exported.
        var research = Assert.Single(
            await client.ListToolsAsync(cancellationToken: cancellationToken),
            tool => tool.Name == SampleAssistantResearch.ToolName);
        Assert.True(research.ProtocolTool.Annotations?.ReadOnlyHint);

        var answered = await research.CallAsync(
            new Dictionary<string, object?> { ["question"] = "Which risks block Alpha?" },
            cancellationToken: cancellationToken);

        Assert.NotEqual(true, answered.IsError);
        var call = Assert.Single(host.Research.Calls);
        Assert.Equal(SampleAssistantResearch.ToolName, call.Tool);
        Assert.Equal(["question"], call.Arguments.Keys);
        var context = call.Context!;
        Assert.Equal("mcp", context["channel"]!.GetValue<string>());
        Assert.Equal("human", context["invocation"]!["actor"]!["kind"]!.GetValue<string>());
        Assert.Equal("spm-399-user", context["invocation"]!["actor"]!["id"]!.GetValue<string>());
        Assert.Equal("spm-399-user", context["invocation"]!["accountableOwnerId"]!.GetValue<string>());
        Assert.Null(context["conversation"]);
        Assert.Null(context["snapshot"]);

        // Withdrawing changes something: offered as destructive, so the app asks the person first; that
        // confirmation is the approval, and the research service holds replays against the app's key.
        var withdraw = Assert.Single(
            await client.ListToolsAsync(cancellationToken: cancellationToken),
            tool => tool.Name == SampleAssistantResearch.WithdrawToolName);
        Assert.True(withdraw.ProtocolTool.Annotations?.DestructiveHint);
        var withdrawn = await withdraw.CallAsync(
            new Dictionary<string, object?> { ["questionId"] = "Q-1" },
            options: new RequestOptions { Meta = new JsonObject { [NhAssistantMcpExportCall.IdempotencyKeyMetaKey] = "withdraw-Q-1" } },
            cancellationToken: cancellationToken);

        Assert.NotEqual(true, withdrawn.IsError);
        var change = host.Research.Calls.Last();
        Assert.Equal(SampleAssistantResearch.WithdrawToolName, change.Tool);
        Assert.Equal("withdraw-Q-1", change.Context!["idempotencyKey"]!.GetValue<string>());
        Assert.Equal("spm-399-user", change.Context["invocation"]!["accountableOwnerId"]!.GetValue<string>());
    }

    private static async Task ConnectAsync(HttpClient admin, string serverId, string url, bool withdraw = false)
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
        if (withdraw)
        {
            using var change = await admin.PutAsync(
                $"/api/assistant/admin/mcp-servers/{serverId}/tools/{SampleAssistantResearch.WithdrawToolName}",
                AssistantSampleHost.Json(new { isEnabled = true, effect = "mutation", descriptionOverride = (string?)null }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        }
    }
}

/// <summary>
/// The assistant sample host with the reviewed research server, the opt-in of
/// <see cref="SampleImportedMcpToolExport"/> and the API's MCP endpoint as Program.cs maps it.
/// </summary>
public sealed class ImportedMcpToolExportSampleHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
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
        settings[SampleImportedMcpToolExport.EnabledSetting] = "true";
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithNewHeapPlatformAITools();
    }

    protected override void MapEndpoints(WebApplication app)
    {
        base.MapEndpoints(app);
        app.MapMcp("/mcp").RequireAuthorization();
    }
}
