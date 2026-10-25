using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Loopback MCP servers in tests (LS29): <see cref="NhAssistantMcpOptions.AllowLoopback"/> lets a host
/// outside Development reach an MCP server on loopback, is refused at startup in Production, and never
/// opens link-local addresses or cloud metadata hosts; the OAuth redirect URI follows the same rule.
/// </summary>
public sealed class AssistantMcpLoopbackTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Admin = "admin-1";
    private const string ServerId = "planning";
    private const string Testing = "Testing";

    [Theory]
    [InlineData("Development", false, true)]
    [InlineData("Development", true, true)]
    [InlineData(Testing, false, false)]
    [InlineData(Testing, true, true)]
    [InlineData("Staging", true, true)]
    [InlineData("Production", true, false)]
    public void Loopback_is_allowed_in_development_and_with_the_option_outside_production(string environmentName, bool allowLoopback, bool allowed)
    {
        var environment = HostEnvironment(environmentName);

        Assert.Equal(allowed, NhAssistantMcpHostGuard.AllowsLoopbackIn(environment, new NhAssistantMcpOptions { AllowLoopback = allowLoopback }));
    }

    [Fact]
    public async Task A_testing_host_with_the_option_reaches_a_loopback_server()
    {
        var tools = new InMemoryMcpServers();
        await using var remote = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools());
        await using var host = await CreateHostAsync(allowLoopback: true);

        var created = await McpAsync(host, mcp => mcp.CreateAsync(Server(remote.StreamableUrl), Admin));
        var tested = await McpAsync(host, mcp => mcp.TestAsync(ServerId));
        var synced = await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin));

        Assert.True(created.Success, string.Join(", ", created.AllErrorMessages));
        Assert.True(tested.Data!.Ok, tested.Data.Code);
        Assert.Equal(2, tested.Data.ToolCount);
        Assert.True(synced.Success);
        Assert.Equal(["Lookup_Order", "reschedule"], synced.Data!.Select(tool => tool.RemoteName).Order(StringComparer.Ordinal));
        Assert.NotEmpty(remote.Requests);

        // Link-local addresses and cloud metadata hosts stay blocked with the option.
        foreach (var url in new[] { "http://169.254.169.254/latest/mcp", "https://metadata.google.internal/mcp", "http://[fe80::1]/mcp" })
        {
            var blocked = await McpAsync(host, mcp => mcp.CreateAsync(Server(url) with { Id = "metadata" }, Admin));
            Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, Code(blocked));
        }
    }

    [Fact]
    public async Task Without_the_option_a_testing_host_refuses_a_loopback_server()
    {
        var tools = new InMemoryMcpServers();
        await using var remote = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools());
        await using var host = await CreateHostAsync(allowLoopback: false);

        var created = await McpAsync(host, mcp => mcp.CreateAsync(Server(remote.StreamableUrl), Admin));

        Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, Code(created));

        // A server stored before, for example by a Development host on the same database, is refused
        // when it connects.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            context.McpServers.Add(new AssistantMcpServer
            {
                Id = ServerId,
                DisplayName = "Planning",
                Url = remote.StreamableUrl,
                AuthMode = NhAssistantMcpAuthModes.None,
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await context.SaveChangesAsync();
        }

        var tested = await McpAsync(host, mcp => mcp.TestAsync(ServerId));

        Assert.Equal((false, NhAssistantAdminErrorCodes.McpHostBlocked), (tested.Data!.Ok, tested.Data.Code));
        Assert.Empty(remote.Requests);
    }

    [Fact]
    public async Task A_production_host_with_the_option_does_not_start()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync("Production", allowLoopback: true));

        Assert.Contains("AllowLoopback", refused.Message, StringComparison.Ordinal);
        await using var production = await StartAsync("Production", allowLoopback: false);
        await using var testing = await StartAsync(Testing, allowLoopback: true);
    }

    [Fact]
    public async Task The_oauth_redirect_uri_may_be_http_on_loopback_where_mcp_servers_may_use_loopback()
    {
        var redirect = new Dictionary<string, string?>
        {
            ["NewHeap:AI:Assistant:Mcp:OAuth:RedirectUri"] = "http://localhost:5281/api/assistant/mcp/oauth/callback"
        };

        await using var allowed = await StartAsync(Testing, allowLoopback: true, redirect);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(Testing, allowLoopback: false, redirect));

        Assert.Contains("RedirectUri", refused.Message, StringComparison.Ordinal);
    }

    private static NhAssistantMcpServerRequest Server(string url)
    {
        return new NhAssistantMcpServerRequest(ServerId, "Planning", url, NhAssistantMcpAuthModes.None, null, null, null, true);
    }

    /// <summary>
    /// A host of the environment <c>Testing</c> with the assistant's own HTTP client, which checks every
    /// address after DNS resolution.
    /// </summary>
    private Task<AssistantTestHost> CreateHostAsync(bool allowLoopback)
    {
        return AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            new NhAiScriptedChatClient(),
            configure: services => services.Replace(ServiceDescriptor.Singleton<IHostEnvironment>(HostEnvironment(Testing))),
            assistantBuilder: assistant => assistant.ConfigureMcp(mcp =>
            {
                mcp.RequireHttps = true;
                mcp.AllowLoopback = allowLoopback;
            }));
    }

    /// <summary>
    /// Starts an assistant host of <paramref name="environmentName"/> without a database, as far as its
    /// startup checks reach.
    /// </summary>
    private static async Task<WebApplication> StartAsync(
        string environmentName,
        bool allowLoopback,
        IDictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>())
        {
            ["NewHeap:AI:Assistant:Mcp:AllowLoopback"] = allowLoopback ? "true" : "false"
        });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(NhAssistantOptions.DefaultAccessPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(NhAssistantOptions.DefaultAdminPolicy, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddKeyedSingleton<IChatClient>("project-chat-model", new NhAiScriptedChatClient());
        builder.Services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("project-chat", profile => profile
                .UseKeyedClient("project-chat-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local"));
            ai.UseBudgetManager<NhAiTestBudgetManager>();
        });
        builder.Services.AddNewHeapPlatformAIAspNet(_ => { });
        builder.Services.AddNewHeapAssistant(assistant => assistant
            .UsePostgreSql("Host=localhost;Database=nh_assistant_loopback", options => options.Schema = "nhai")
            .UseChatProfile("project-chat")
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" }));
        var app = builder.Build();
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return app;
    }

    private static IHostEnvironment HostEnvironment(string environmentName)
    {
        return new Microsoft.Extensions.Hosting.Internal.HostingEnvironment
        {
            EnvironmentName = environmentName,
            ApplicationName = "assistant-tests",
            ContentRootPath = AppContext.BaseDirectory
        };
    }

    private static async Task<T> McpAsync<T>(AssistantTestHost host, Func<INhAssistantMcpServers, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, Admin);
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantMcpServers>());
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }
}
