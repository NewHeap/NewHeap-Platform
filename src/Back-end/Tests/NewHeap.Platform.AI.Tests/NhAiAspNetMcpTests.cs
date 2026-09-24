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
            .UseAuthenticatedClaims(
            [
                new NhAiAspNetIssuerClaimMapping("https://identity.example"),
                new NhAiAspNetIssuerClaimMapping(
                    "https://operator-identity.example",
                    "operator_issuer",
                    "operator_subject",
                    "operator_tenant")
            ])
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
        var actorAResult = await client.CallNewHeapToolAsync<string, string>(
            actorATool.Name,
            "value");
        Assert.Equal("value:subject-a:tenant-a", actorAResult);
        var failedResult = await actorATool.CallAsync(new Dictionary<string, object?>
        {
            ["input"] = "fail"
        });
        Assert.True(failedResult.IsError);
        var failedException = await Assert.ThrowsAsync<NhAiMcpToolException>(async () =>
            await client.CallNewHeapToolAsync<string, string>(
                actorATool.Name,
                "fail"));
        Assert.Equal(actorATool.Name, failedException.ToolName);
        Assert.True(failedException.Result.IsError);
        Assert.Equal("tenant-inspection-failed", failedException.Code);
        Assert.Equal("Tenant inspection failed safely.", failedException.FailureMessage);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await actorATool.CallAsync(
                new Dictionary<string, object?> { ["input"] = "wait" },
                cancellationToken: cancellation.Token));

        accessor.HttpContext = CreateOperatorContext(
            "subject-b",
            "tenant-b",
            "tenant-b.inspect");
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

    [Fact]
    public async Task Flat_export_schema_publishes_unwrapped_arguments_and_typed_denials()
    {
        var context = new NhAiInvocationContext(
            "agent-1",
            "order-maintenance",
            new Dictionary<string, string>());
        var services = new ServiceCollection();
        services.AddScoped<FlatMcpTools>();
        services.AddScoped<INhAiToolInvocationGate>(
            _ => NhAiTestInvocationGate.Authorized(context));
        services.AddScoped<INhAiToolDiscoveryPolicy>(
            _ => NhAiTestDiscoveryPolicy.Allowed());
        services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
        services.AddNewHeapPlatformAI(ai =>
            ai.AddGeneratedToolCatalog<FlatMcpToolsNhAiCatalog>());
        services.AddNewHeapPlatformAIMcp();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var mcpTools = await scope.ServiceProvider
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(scope.ServiceProvider, context);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ScopeRequests = false,
                ToolCollection = [.. mcpTools]
            },
            serviceProvider: scope.ServiceProvider);
        _ = server.RunAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream()));

        var tool = Assert.Single(await client.ListToolsAsync());
        Assert.Equal("orders.apply-status-receipt", tool.Name);
        Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty("orderId", out _));
        Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("input", out _));
        Assert.NotNull(tool.ReturnJsonSchema);
        Assert.True(tool.ReturnJsonSchema.Value.GetProperty("properties").TryGetProperty("execution", out _));

        var receipt = await client.CallNewHeapFlatToolAsync<FlatReceiptInput, FlatReceipt>(
            tool.Name,
            new FlatReceiptInput("order-1", "valid-grant", "key-1"));
        Assert.Equal("executed", receipt.Execution);
        Assert.Equal("agent-1", receipt.ActorId);

        // The official client contract: top-level arguments, structured content is the receipt.
        var rawResult = await tool.CallAsync(new Dictionary<string, object?>
        {
            ["orderId"] = "order-1",
            ["approvalGrant"] = "valid-grant",
            ["idempotencyKey"] = "key-1"
        });
        Assert.NotEqual(true, rawResult.IsError);
        Assert.Equal(
            "executed",
            rawResult.StructuredContent!.Value.GetProperty("execution").GetString());
        Assert.False(rawResult.StructuredContent.Value.TryGetProperty("success", out _));

        var denied = await Assert.ThrowsAsync<NhAiMcpToolException<FlatReceipt>>(async () =>
            await client.CallNewHeapFlatToolAsync<FlatReceiptInput, FlatReceipt>(
                tool.Name,
                new FlatReceiptInput("order-1", "burned-grant", "key-2")));
        Assert.True(denied.Result.IsError);
        var denial = denied.Result.StructuredContent!.Value;
        Assert.Equal("deny", denial.GetProperty("execution").GetString());
        Assert.Equal(
            "approval-invalid-expired-or-replayed",
            denial.GetProperty("code").GetString());
        Assert.Equal("approval-invalid-expired-or-replayed", denied.Code);
        Assert.Equal("The approval grant is invalid.", denied.FailureMessage);
        Assert.Equal("deny", denied.Payload!.Execution);
        Assert.Contains(
            "approval-invalid-expired-or-replayed",
            denied.Message,
            StringComparison.Ordinal);

        var failedWithoutPayload = await tool.CallAsync(new Dictionary<string, object?>
        {
            ["orderId"] = "order-1",
            ["approvalGrant"] = "crash",
            ["idempotencyKey"] = "key-3"
        });
        Assert.True(failedWithoutPayload.IsError);
        Assert.Equal(
            "{\"code\":\"engine-unavailable\",\"message\":\"The order engine returned no receipt.\"}",
            failedWithoutPayload.StructuredContent!.Value.GetRawText());
        Assert.Equal(
            "engine-unavailable: The order engine returned no receipt.",
            Assert.IsType<TextContentBlock>(Assert.Single(failedWithoutPayload.Content)).Text);
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

    private static DefaultHttpContext CreateOperatorContext(
        string subject,
        string tenant,
        string scope)
    {
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("operator_issuer", "https://operator-identity.example"),
                    new Claim("operator_subject", subject),
                    new Claim("operator_tenant", tenant),
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

public sealed record FlatReceiptInput(
    string OrderId,
    string ApprovalGrant,
    string IdempotencyKey);

public sealed record FlatReceipt(
    string Execution,
    string Code,
    string ActorId);

[NhAiToolSet("orders")]
public sealed class FlatMcpTools
{
    [NhAiTool(
        "apply-status-receipt",
        1,
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Mcp,
        Approval = NhAiApprovalRequirement.ConsumerAuthoritative,
        Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative,
        ExportSchema = NhAiToolExportSchema.Flat)]
    [NhAiToolExportName("orders.apply-status-receipt")]
    [Authorize(Policy = "order-manage")]
    [Description("Apply an approved order status change and return the domain receipt.")]
    public Task<TaskResult<FlatReceipt>> ApplyAsync(
        FlatReceiptInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(input.ApprovalGrant, "crash", StringComparison.Ordinal))
        {
            return Task.FromResult(TaskResult<FlatReceipt>.Failed(
                "engine-unavailable",
                "The order engine returned no receipt."));
        }
        if (!string.Equals(input.ApprovalGrant, "valid-grant", StringComparison.Ordinal))
        {
            return Task.FromResult(
                TaskResult<FlatReceipt>
                    .Failed("approval-invalid-expired-or-replayed", "The approval grant is invalid.")
                    .WithData(new FlatReceipt(
                        "deny",
                        "approval-invalid-expired-or-replayed",
                        context.ActorId)));
        }
        return Task.FromResult(TaskResult<FlatReceipt>.Succeeded(
            new FlatReceipt("executed", "status-updated", context.ActorId)));
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
