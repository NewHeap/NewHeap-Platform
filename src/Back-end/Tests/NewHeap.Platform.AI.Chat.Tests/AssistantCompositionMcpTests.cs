using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// TC8 over MCP: compositions and the script tool of an MCP exposure are part of it only where the
/// exposure allows them in code, are listed only while the application's switch has them on for the
/// request, and a call that still arrives is refused with <c>assistant-composition-off</c> or
/// <c>assistant-scripting-off</c> before anything runs. Individual tools are never affected. With the
/// assistant the switch is <see cref="INhAssistantCompositionSwitch"/> for the MCP channel; without it,
/// configuration.
/// </summary>
public sealed class AssistantCompositionMcpTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Member = "member-1";
    private const string EchoFunction = "runtime_echo_v1";
    private const string ScriptsKey = "NewHeap:AI:Assistant:Scripts";

    [Fact]
    public async Task The_assistants_switch_decides_per_mcp_request_and_the_exposure_ceiling_keeps_out_what_code_does_not_allow()
    {
        var switchState = new TestCompositionSwitchState();
        await using var host = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TestCompositionToolRecorder>();
                services.AddSingleton(switchState);
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestCompositionToolCatalog>());
                services.AddMcpServer()
                    .WithHttpTransport(options => options.Stateless = true)
                    .WithNewHeapPlatformAITools(mcp => mcp.AllowCompositions());
            },
            assistantBuilder: assistant => assistant.UseCompositionSwitch<TestCompositionSwitch>(),
            map: app => app.MapMcp("/mcp"));
        var recorder = host.Services.GetRequiredService<TestCompositionToolRecorder>();
        await using var client = await ConnectAsync(host.CreateClient(Member));

        // On: the composition is listed and runs; the script tool is not part of an exposure that does
        // not allow scripts, whatever the switch says.
        var listed = await client.ListToolsAsync();
        var overview = Assert.Single(listed, tool => tool.Name == TestCompositionToolCatalog.OverviewFunction);
        Assert.DoesNotContain(listed, tool => tool.Name == TestCompositionToolCatalog.ScriptFunction);
        var ran = await overview.CallAsync(new Dictionary<string, object?> { ["input"] = new { query = "risks" } });
        Assert.NotEqual(true, ran.IsError);
        Assert.Equal([TestCompositionToolCatalog.Overview.Id], recorder.Ran);
        var script = await client.CallToolAsync(TestCompositionToolCatalog.ScriptFunction, new Dictionary<string, object?> { ["input"] = new { code = "1" } });
        Assert.True(script.IsError);
        Assert.Equal("ai-tool-not-authorized", script.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());

        // The switch was asked for the MCP channel, for the caller, without an agent, and never about
        // scripts, which the exposure does not allow.
        Assert.NotEmpty(switchState.Asked);
        Assert.All(switchState.Asked, context =>
        {
            Assert.Equal(NhAssistantCompositionChannel.Mcp, context.Channel);
            Assert.Equal(NhAiCompositionFeature.Compositions, context.Feature);
            Assert.Equal(Member, context.PersonActorId);
            Assert.Null(context.AgentId);
        });

        // Off: the next listing leaves the composition out and a call from the earlier list is refused
        // with the code before it runs.
        switchState.Answer = _ => false;
        Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name == TestCompositionToolCatalog.OverviewFunction);
        var refused = await overview.CallAsync(new Dictionary<string, object?> { ["input"] = new { query = "risks" } });
        Assert.True(refused.IsError);
        Assert.Equal(NhAssistantErrorCodes.CompositionOff, refused.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());
        Assert.Equal(NhAssistantErrorCodes.CompositionOff, refused.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Single(recorder.Ran);
    }

    [Fact]
    public async Task Without_the_assistant_configuration_switches_the_script_tool_of_an_exposure_that_allows_it_at_run_time()
    {
        await using var host = await PlainMcpHost.StartAsync(scripts: true);
        await using var client = await ConnectAsync(host.CreateClient());

        var listed = (await client.ListToolsAsync()).Select(tool => tool.Name).ToArray();
        Assert.Contains(TestCompositionToolCatalog.ScriptFunction, listed);
        // The exposure does not allow compositions; individual tools are always listed.
        Assert.DoesNotContain(TestCompositionToolCatalog.OverviewFunction, listed);
        Assert.Contains(EchoFunction, listed);

        // Configuration changes while the host runs; the next request follows it.
        host.Configuration[ScriptsKey] = "false";
        listed = (await client.ListToolsAsync()).Select(tool => tool.Name).ToArray();
        Assert.DoesNotContain(TestCompositionToolCatalog.ScriptFunction, listed);
        Assert.Contains(EchoFunction, listed);
        var refused = await client.CallToolAsync(TestCompositionToolCatalog.ScriptFunction, new Dictionary<string, object?> { ["input"] = new { code = "1" } });
        Assert.True(refused.IsError);
        Assert.Equal(NhAiCompositionFeatures.ScriptingOffCode, refused.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());
        Assert.Empty(host.Services.GetRequiredService<TestCompositionToolRecorder>().Ran);
    }

    [Fact]
    public async Task Without_configuration_an_exposure_that_allows_both_lists_neither()
    {
        await using var host = await PlainMcpHost.StartAsync(scripts: null, allowCompositions: true);
        await using var client = await ConnectAsync(host.CreateClient());

        var listed = (await client.ListToolsAsync()).Select(tool => tool.Name).ToArray();

        Assert.Equal([EchoFunction], listed);
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

    /// <summary>
    /// An MCP exposure of <c>WithNewHeapPlatformAITools</c> without the assistant, on a test server, with
    /// one individual tool and the marked test tools; every request is the same signed-in person.
    /// </summary>
    private sealed class PlainMcpHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PlainMcpHost(WebApplication app)
        {
            _app = app;
        }

        public IServiceProvider Services => _app.Services;

        public IConfiguration Configuration => _app.Configuration;

        public static async Task<PlainMcpHost> StartAsync(bool? scripts, bool allowCompositions = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var settings = new Dictionary<string, string?>();
            if (scripts is { } on)
            {
                settings[ScriptsKey] = on ? "true" : "false";
            }
            builder.Configuration.AddInMemoryCollection(settings);
            var services = builder.Services;
            services.AddAuthorization(options =>
            {
                options.AddPolicy(TestProjectToolCatalog.ReadPolicy, policy => policy.RequireAuthenticatedUser());
                options.AddPolicy(TestProjectToolCatalog.ManagePolicy, policy => policy.RequireAuthenticatedUser());
            });
            services.AddSingleton<TestCompositionToolRecorder>();
            var runs = new NhAiMcpHttpToolListChangeTests.RunCounter();
            var echo = new NhAiMcpHttpToolListChangeTests.SwitchingCatalog(NhAiMcpHttpToolListChangeTests.EchoView.Create("runtime.echo", EchoFunction, runs));
            services.AddSingleton<INhAiToolCatalog>(echo);
            services.AddSingleton<INhAiChangingToolCatalog>(echo);
            services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestCompositionToolCatalog>());
            services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
            services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
            services.AddNewHeapPlatformAIAspNet(ai => ai.UseToolInvocationPurpose("tool-invocation"));
            services.AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .WithNewHeapPlatformAITools(mcp => mcp.AllowScripts().AllowCompositions(allowCompositions));

            var app = builder.Build();
            app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "actor-1")], "test"));
                return next(context);
            });
            app.MapMcp("/mcp");
            await app.StartAsync();
            return new PlainMcpHost(app);
        }

        public HttpClient CreateClient()
        {
            return _app.GetTestClient();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
