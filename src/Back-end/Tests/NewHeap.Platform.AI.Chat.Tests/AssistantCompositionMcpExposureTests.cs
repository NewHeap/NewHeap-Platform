using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// TC7: a composition that opts in with <c>ExposeOverMcp</c> is a tool of an MCP exposure that allows
/// compositions, read-only when its steps only read, under the same governance as any tool: each step
/// runs through its governed function with the MCP caller's rights, marked as a step of the call, and
/// the composition's call is audited with its steps under it. With the switch off it is not listed and
/// a call is refused before a step runs.
/// </summary>
public sealed class AssistantCompositionMcpExposureTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Member = "member-1";

    [Fact]
    public async Task An_exposed_composition_runs_its_steps_for_the_mcp_caller_and_follows_the_switch()
    {
        var switchState = new TestCompositionSwitchState();
        var audit = new NhAiCapturedAuditSink();
        await using var host = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TestRiskToolRecorder>();
                services.AddSingleton(switchState);
                services.AddSingleton<INhAiAuditSink>(audit);
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestRiskToolCatalog>());
                services.AddMcpServer()
                    .WithHttpTransport(options => options.Stateless = true)
                    .WithNewHeapPlatformAITools(mcp => mcp.AllowCompositions());
            },
            assistantBuilder: assistant => assistant
                .UseCompositionSwitch<TestCompositionSwitch>()
                .AddTestCompositions(),
            map: app => app.MapMcp("/mcp"));
        var recorder = host.Services.GetRequiredService<TestRiskToolRecorder>();
        await using var client = await ConnectAsync(host.CreateClient(Member));

        // Only the composition that opts in is listed, and it reads only.
        var listed = await client.ListToolsAsync();
        var overview = Assert.Single(listed, tool => tool.Name == TestCompositions.OverviewFunction);
        Assert.True(overview.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.DoesNotContain(listed, tool => tool.Name == TestCompositions.FlagOpenRisksFunction);

        var projectId = Guid.NewGuid();
        var ran = await overview.CallAsync(new Dictionary<string, object?> { ["input"] = new { projectId } });

        Assert.NotEqual(true, ran.IsError);
        Assert.True(ran.StructuredContent!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(projectId.ToString(), ran.StructuredContent.Value.GetProperty("data").GetProperty("project").GetProperty("id").GetString());
        Assert.Equal([TestRiskToolCatalog.GetProject.Id, TestRiskToolCatalog.ListRisks.Id, TestRiskToolCatalog.GetPeople.Id], recorder.RanTools);
        var call = Assert.Single(audit.Records, record => record.ToolId == TestCompositions.Overview);
        Assert.Equal(NhAiOutcomeKind.Succeeded, call.Outcome);
        Assert.Equal(Member, call.ActorId);
        Assert.All(recorder.Contexts, context =>
        {
            Assert.Equal(NhAiToolCallOriginKind.Composition, context.Origin?.Kind);
            Assert.Equal(call.InvocationId, context.Origin?.ParentInvocationId);
            Assert.Equal(Member, context.ActorId);
        });
        Assert.Equal(3, audit.Records.Count(record => record.Origin?.ParentInvocationId == call.InvocationId));

        // Off: the next listing leaves it out and a call is refused before any step runs.
        switchState.Answer = _ => false;
        Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name == TestCompositions.OverviewFunction);
        var refused = await overview.CallAsync(new Dictionary<string, object?> { ["input"] = new { projectId } });
        Assert.True(refused.IsError);
        Assert.Equal(NhAiCompositionFeatures.CompositionOffCode, refused.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());
        Assert.Equal(3, recorder.Ran.Count);
    }

    private static async Task<McpClient> ConnectAsync(HttpClient http)
    {
        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                http,
                ownsHttpClient: true));
    }
}
