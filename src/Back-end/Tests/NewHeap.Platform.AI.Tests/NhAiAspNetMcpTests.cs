using System.ComponentModel;
using System.IO.Pipelines;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiAspNetMcpTests
{
    [Fact]
    public async Task Official_builder_isolates_actor_and_tenant_for_list_and_call()
    {
        var accessor = new MutableHttpContextAccessor
        {
            HttpContext = CreateContext("subject-a", "tenant-a", "tenant-a.inspect")
        };
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(accessor);
        services.AddSingleton<IAuthorizationService>(new AllowAuthorizationService());
        services.AddScoped<AuthenticatedMcpTools>();
        services.AddScoped<INhAiToolDiscoveryPolicy, TenantDiscoveryPolicy>();
        services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .UseAuthenticatedClaims("https://identity.example")
            .AddScopeCapability("scope", "tenant-a.inspect", "tenant-a-inspect")
            .AddScopeCapability("scope", "tenant-b.inspect", "tenant-b-inspect"));
        services.AddNewHeapPlatformAI(ai =>
            ai.AddGeneratedToolCatalog<AuthenticatedMcpToolsNhAiCatalog>());
        services.AddMcpServer(options => options.ScopeRequests = false)
            .WithNewHeapPlatformAITools()
            .WithTools<ExternalMcpTools>();
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream()),
            options,
            serviceProvider: provider);
        _ = server.RunAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream()));

        var actorATools = await client.ListToolsAsync();
        Assert.Equal(2, actorATools.Count);
        var actorATool = Assert.Single(actorATools, tool =>
            string.Equals(tool.Name, "tenant-a.inspect", StringComparison.Ordinal));
        Assert.Equal("tenant-a.inspect", actorATool.Name);
        var externalTool = Assert.Single(actorATools, tool =>
            string.Equals(tool.Name, "external.inspect", StringComparison.Ordinal));
        var externalResult = await externalTool.CallAsync();
        var externalContent = Assert.IsType<TextContentBlock>(
            Assert.Single(externalResult.Content));
        Assert.Contains("external", externalContent.Text, StringComparison.Ordinal);
        var actorAResult = await actorATool.CallAsync(new Dictionary<string, object?>
        {
            ["input"] = "value"
        });
        Assert.NotEqual(true, actorAResult.IsError);
        Assert.Contains("subject-a:tenant-a", actorAResult.StructuredContent!.Value.GetRawText());
        var failedResult = await actorATool.CallAsync(new Dictionary<string, object?>
        {
            ["input"] = "fail"
        });
        Assert.True(failedResult.IsError);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await actorATool.CallAsync(
                new Dictionary<string, object?> { ["input"] = "wait" },
                cancellationToken: cancellation.Token));

        accessor.HttpContext = CreateContext("subject-b", "tenant-b", "tenant-b.inspect");
        var mismatchedCall = await actorATool.CallAsync(new Dictionary<string, object?>
        {
            ["input"] = "value"
        });
        Assert.True(mismatchedCall.IsError);
        Assert.Equal(
            "ai-tool-not-authorized",
            mismatchedCall.StructuredContent!.Value.GetProperty("code").GetString());
        var actorBTools = await client.ListToolsAsync();
        Assert.Equal(2, actorBTools.Count);
        var actorBTool = Assert.Single(actorBTools, tool =>
            string.Equals(tool.Name, "tenant-b.inspect", StringComparison.Ordinal));
        Assert.Equal("tenant-b.inspect", actorBTool.Name);
    }

    [Fact]
    public async Task Official_builder_allows_external_sdk_tools_with_distinct_names()
    {
        var services = new ServiceCollection();
        services.AddScoped<AuthenticatedMcpTools>();
        services.AddNewHeapPlatformAI(ai =>
            ai.AddGeneratedToolCatalog<AuthenticatedMcpToolsNhAiCatalog>());
        services.AddMcpServer()
            .WithNewHeapPlatformAITools()
            .WithTools<ExternalMcpTools>();
        await using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name.Contains(
                "NhAiMcpAuthorityStartupValidator",
                StringComparison.Ordinal));

        await validator.StartAsync(CancellationToken.None);

        var externalTool = Assert.Single(provider.GetServices<McpServerTool>());
        Assert.Equal("external.inspect", externalTool.ProtocolTool.Name);
    }

    [Fact]
    public async Task Official_builder_rejects_an_sdk_tool_name_that_conflicts_with_a_newheap_export()
    {
        var services = new ServiceCollection();
        services.AddScoped<AuthenticatedMcpTools>();
        services.AddNewHeapPlatformAI(ai =>
            ai.AddGeneratedToolCatalog<AuthenticatedMcpToolsNhAiCatalog>());
        services.AddMcpServer()
            .WithNewHeapPlatformAITools()
            .WithTools<ConflictingMcpTools>();
        await using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name.Contains(
                "NhAiMcpAuthorityStartupValidator",
                StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await validator.StartAsync(CancellationToken.None));

        Assert.Contains("conflicts with a NewHeap AI export name", exception.Message, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateContext(
        string subject,
        string tenant,
        string scope)
    {
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("iss", "https://identity.example"),
                    new Claim("sub", subject),
                    new Claim("tenant_id", tenant),
                    new Claim("scope", scope)
                ],
                "test"))
        };
    }

    private sealed class TenantDiscoveryPolicy : INhAiToolDiscoveryPolicy
    {
        public ValueTask<bool> CanDiscoverAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = context.TenantId switch
            {
                "tenant-a" => "tenant-a.inspect",
                "tenant-b" => "tenant-b.inspect",
                _ => null
            };
            return ValueTask.FromResult(string.Equals(
                descriptor.ExportName,
                expected,
                StringComparison.Ordinal));
        }
    }

    private sealed class AllowAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
        {
            return Task.FromResult(AuthorizationResult.Success());
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            return Task.FromResult(AuthorizationResult.Success());
        }
    }

    private sealed class MutableHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}

[NhAiToolSet("authenticated-mcp")]
public sealed class AuthenticatedMcpTools
{
    [NhAiTool(
        "tenant-a-inspect",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Mcp,
        RequiredCapabilities = ["tenant-a-inspect"])]
    [NhAiToolExportName("tenant-a.inspect")]
    [Authorize(Policy = "tenant-inspect")]
    [Description("Inspect tenant A with the authenticated request context.")]
    public Task<TaskResult<string>> InspectTenantAAsync(
        string input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return InspectAsync(input, context, cancellationToken);
    }

    [NhAiTool(
        "tenant-b-inspect",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Mcp,
        RequiredCapabilities = ["tenant-b-inspect"])]
    [NhAiToolExportName("tenant-b.inspect")]
    [Authorize(Policy = "tenant-inspect")]
    [Description("Inspect tenant B with the authenticated request context.")]
    public Task<TaskResult<string>> InspectTenantBAsync(
        string input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return InspectAsync(input, context, cancellationToken);
    }

    private static async Task<TaskResult<string>> InspectAsync(
        string input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(input, "fail", StringComparison.Ordinal))
        {
            return TaskResult<string>.Failed(
                "tenant-inspection-failed",
                "Tenant inspection failed safely.");
        }
        if (string.Equals(input, "wait", StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        return TaskResult<string>.Succeeded($"{input}:{context.Subject}:{context.TenantId}");
    }
}

[McpServerToolType]
public sealed class ExternalMcpTools
{
    [McpServerTool(Name = "external.inspect")]
    [Description("Inspect through an independently governed external MCP library.")]
    public string Inspect()
    {
        return "external";
    }
}

[McpServerToolType]
public sealed class ConflictingMcpTools
{
    [McpServerTool(Name = "tenant-a.inspect")]
    [Description("This name conflicts with a NewHeap-managed export.")]
    public string Inspect()
    {
        return "conflicting";
    }
}
