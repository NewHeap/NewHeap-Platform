using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

public sealed class NhAiMvcBridgeGatewayTests
{
    private const string Search = "test-gateway_search-resources_v1";
    private const string Describe = "test-gateway_describe-resource_v1";
    private const string Query = "test-gateway_query_v1";
    private const string Get = "test-gateway_get_v1";

    [Fact]
    public void Gateway_publishes_four_read_only_tools_in_the_attested_catalog()
    {
        using var factory = CreateFactory();
        var catalog = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>();

        var gatewayTools = catalog.Descriptors.Where(descriptor => descriptor.Id.StartsWith("test-gateway.", StringComparison.Ordinal)).ToArray();

        Assert.Equal(
            ["test-gateway.describe-resource", "test-gateway.get", "test-gateway.query", "test-gateway.search-resources"],
            gatewayTools.Select(descriptor => descriptor.Id));
        Assert.All(gatewayTools, descriptor =>
        {
            Assert.Equal(NhAiToolEffect.ReadOnly, descriptor.Effect);
            Assert.True(descriptor.Exposure.HasFlag(NhAiToolExposure.Mcp));
            Assert.InRange(descriptor.ExportName.Length, 1, 64);
        });
        Assert.Equal(["order", "order-audit"], catalog.GatewayResources.Order(StringComparer.Ordinal));
        NhAiToolCatalogAttestation.Validate(catalog, factory.Services.CreateScope().ServiceProvider);
    }

    [Fact]
    public async Task Resource_catalog_is_filtered_per_user()
    {
        using var factory = CreateFactory();

        var viewer = await factory.InvokeAsync(TestTokenHandler.Viewer, Search, new { query = "" });
        var manager = await factory.InvokeAsync(TestTokenHandler.Manager, Search, new { query = "" });
        var outsider = await factory.InvokeAsync(TestTokenHandler.Outsider, Search, new { query = "order" });
        var audit = await factory.InvokeAsync(TestTokenHandler.Manager, Search, new { query = "audit" });

        Assert.Equal(["order"], Resources(viewer));
        Assert.Equal(["order", "order-audit"], Resources(manager));
        Assert.Empty(Resources(outsider));
        Assert.Equal("order-audit", Resources(audit)[0]);
        Assert.Equal(["query", "get"], viewer.Data[0].GetProperty("operations").EnumerateArray().Select(item => item.GetString()));
        var viewerTools = await factory.AsUserAsync(TestTokenHandler.Outsider, services => DiscoverAsync(services));
        Assert.DoesNotContain(viewerTools, id => id.StartsWith("test-gateway.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Describe_returns_the_fields_from_the_conventions_and_the_describer()
    {
        using var factory = CreateFactory();

        var result = await factory.InvokeAsync(TestTokenHandler.Viewer, Describe, new { resource = "order" });

        Assert.True(result.Success, result.Errors);
        var description = result.Data;
        Assert.Equal("order", description.GetProperty("resource").GetString());
        Assert.Equal("Orders with their customer and status.", description.GetProperty("description").GetString());
        var query = description.GetProperty("query");
        var filter = Assert.Single(query.GetProperty("filterFields").EnumerateArray());
        Assert.Equal("customer", filter.GetProperty("key").GetString());
        Assert.Equal(["eq", "contains"], filter.GetProperty("operators").EnumerateArray().Select(item => item.GetString()));
        Assert.True(query.GetProperty("searchable").GetBoolean());
        Assert.Equal(["customer", "id"], query.GetProperty("orderFields").EnumerateArray().Select(item => item.GetString()));
        Assert.True(query.GetProperty("extraParameters").GetProperty("properties").TryGetProperty("status", out _));
        Assert.Equal("id", description.GetProperty("get").GetProperty("idParameter").GetString());
        Assert.Equal(3, description.GetProperty("resultFields").GetArrayLength());
    }

    [Fact]
    public async Task Query_and_get_send_exactly_the_request_of_the_bridge_tools_and_audit_the_underlying_tool()
    {
        using var factory = CreateFactory();

        await factory.InvokeAsync(TestTokenHandler.Viewer, "test-api_order_get_v1", new
        {
            page = 2,
            itemsPerPage = 5,
            search = "acme",
            filter = new[] { new { key = "customer", @operator = "eq", value = "acme" } },
            orderBy = new[] { new { key = "customer", direction = "desc" } },
            status = "Shipped"
        });
        var query = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "order",
            page = 2,
            itemsPerPage = 5,
            search = "acme",
            filter = new[] { new { key = "customer", @operator = "eq", value = "acme" } },
            orderBy = new[] { new { key = "customer", direction = "desc" } },
            parameters = new { status = "Shipped" }
        });
        await factory.InvokeAsync(TestTokenHandler.Viewer, "test-api_order_get-by-id_v1", new { id = 7 });
        var get = await factory.InvokeAsync(TestTokenHandler.Viewer, Get, new { resource = "order", id = 7 });

        Assert.True(query.Success, query.Errors);
        Assert.True(get.Success, get.Errors);
        var requests = factory.Requests.ToArray();
        Assert.Equal(4, requests.Length);
        Assert.Equal(requests[0], requests[1]);
        Assert.Equal(requests[2], requests[3]);
        Assert.Equal("GET /orders/7", requests[3]);
        var auditedTools = factory.Services.GetRequiredService<NhAiCapturedAuditSink>().Records
            .Where(record => record.Outcome == NhAiOutcomeKind.Succeeded)
            .Select(record => record.ToolId)
            .ToArray();
        Assert.Equal(
            ["test-api.order.get", "test-api.order.get", "test-api.order.get-by-id", "test-api.order.get-by-id"],
            auditedTools);
    }

    [Fact]
    public async Task Unknown_and_unauthorized_resources_fail_the_same_way_without_a_request()
    {
        using var factory = CreateFactory();

        var (unknownQuery, forbiddenQuery, forbiddenDescribe, forbiddenGet) = await factory.WithMcpClientAsync(TestTokenHandler.Viewer, async client =>
        {
            var tools = await client.ListToolsAsync();
            var query = tools.Single(tool => tool.Name == Query);
            var describe = tools.Single(tool => tool.Name == Describe);
            var get = tools.Single(tool => tool.Name == Get);
            return (
                await query.CallAsync(Arguments(new { resource = "does-not-exist" })),
                await query.CallAsync(Arguments(new { resource = "order-audit" })),
                await describe.CallAsync(Arguments(new { resource = "order-audit" })),
                await get.CallAsync(Arguments(new { resource = "order-audit", id = 1 })));
        });

        foreach (var result in new[] { unknownQuery, forbiddenQuery, forbiddenDescribe, forbiddenGet })
        {
            Assert.True(result.IsError);
            Assert.Equal(NhAiBridgeFailureCodes.ResourceNotFound, Code(result));
            Assert.DoesNotContain("audit", Text(result), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(Text(unknownQuery), Text(forbiddenQuery));
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Unknown_filter_or_order_key_is_rejected_before_the_http_call()
    {
        using var factory = CreateFactory();

        var (filter, order, page) = await factory.WithMcpClientAsync(TestTokenHandler.Viewer, async client =>
        {
            var query = (await client.ListToolsAsync()).Single(tool => tool.Name == Query);
            return (
                await query.CallAsync(Arguments(new { resource = "order", filter = new[] { new { key = "secretColumn", @operator = "eq", value = "x" } } })),
                await query.CallAsync(Arguments(new { resource = "order", orderBy = new[] { new { key = "internalRank" } } })),
                await query.CallAsync(Arguments(new { resource = "order", itemsPerPage = 500 })));
        });

        Assert.Equal(NhAiBridgeFailureCodes.Validation, Code(filter));
        Assert.Equal(NhAiBridgeFailureCodes.Validation, Code(order));
        Assert.Equal(NhAiBridgeFailureCodes.Validation, Code(page));
        Assert.Empty(factory.Requests);
        Assert.Contains(
            factory.Services.GetRequiredService<NhAiCapturedAuditSink>().Records,
            record => record.ToolId == "test-api.order.get" && record.ResultCode == NhAiBridgeFailureCodes.Validation);
    }

    [Fact]
    public async Task Mutations_are_unreachable_through_the_gateway()
    {
        using var factory = CreateFactory();

        var described = await factory.InvokeAsync(TestTokenHandler.Manager, Describe, new { resource = "order" });
        var parameterBody = await factory.InvokeAsync(TestTokenHandler.Manager, Query, new
        {
            resource = "order",
            parameters = new { body = new { customer = "acme" } }
        });

        Assert.True(described.Success);
        var catalog = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
        Assert.DoesNotContain(catalog.GatewayResources, resource => resource.Contains("create", StringComparison.Ordinal));
        Assert.False(parameterBody.Success);
        Assert.DoesNotContain(factory.Requests, request => request.StartsWith("POST", StringComparison.Ordinal)
            || request.StartsWith("PUT", StringComparison.Ordinal));
        Assert.DoesNotContain(
            factory.Services.GetRequiredService<NhAiCapturedAuditSink>().Records,
            record => record.ToolId is "test-api.order.create" or "test-api.order.update");
    }

    [Fact]
    public async Task Mcp_export_lists_and_calls_the_gateway_tools()
    {
        using var factory = CreateFactory();

        var (names, result) = await factory.WithMcpClientAsync(TestTokenHandler.Viewer, async client =>
        {
            var tools = await client.ListToolsAsync();
            var get = tools.Single(tool => tool.Name == Get);
            return (tools.Select(tool => tool.Name).ToArray(), await get.CallAsync(Arguments(new { resource = "order", id = 3 })));
        });

        Assert.Contains(Search, names);
        Assert.Contains(Describe, names);
        Assert.Contains(Query, names);
        Assert.Contains(Get, names);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(3, result.StructuredContent!.Value.GetProperty("data").GetProperty("body").GetProperty("id").GetInt32());
    }

    private static BridgeApiFactory CreateFactory()
    {
        return new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Order", "OrderAudit")
            .UseConventions<GatewayTestConventions>()
            .EnableMcpExposure()
            .EnableGateway(gateway => gateway
                .UseGatewayToolSetId("test-gateway")
                .IncludeReadOnlyOnly()
                .UseResourceDescriber<GatewayTestDescriber>()));
    }

    private static string[] Resources(BridgeToolResult result)
    {
        Assert.True(result.Success, result.Errors);
        return result.Data.EnumerateArray().Select(item => item.GetProperty("resource").GetString()!).ToArray();
    }

    private static async Task<string[]> DiscoverAsync(IServiceProvider services)
    {
        var descriptors = await services.GetRequiredService<INhAiToolDiscoveryService>().DiscoverAsync(
            new NhAiToolDiscoveryRequest(
                new NhAiInvocationContext("actor", "assistant", new Dictionary<string, string>()),
                NhAiToolExposure.Agent));
        return descriptors.Select(descriptor => descriptor.Id).ToArray();
    }

    private static Dictionary<string, object?> Arguments(object input)
    {
        return new Dictionary<string, object?> { ["input"] = input };
    }

    private static string? Code(CallToolResult result)
    {
        return result.Meta?[NhAiMcpResultMetadata.CodeKey]?.GetValue<string>();
    }

    private static string Text(CallToolResult result)
    {
        return string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    public sealed class GatewayTestConventions : NhAiMvcBridgeDefaultConventions
    {
        public override NhAiBridgeQueryDescription DescribeQuery(NhAiBridgeActionInfo action)
        {
            return base.DescribeQuery(action) with
            {
                FilterFields = [new NhAiBridgeFilterField("customer", "string", ["eq", "contains"])],
                OrderFields = ["customer", "id"],
                ResultFields =
                [
                    new NhAiBridgeResultField("id", "integer"),
                    new NhAiBridgeResultField("customer", "string"),
                    new NhAiBridgeResultField("status", "string", "Draft, Confirmed or Shipped.")
                ]
            };
        }
    }

    public sealed class GatewayTestDescriber : INhAiBridgeResourceDescriber
    {
        public NhAiBridgeResourceDescription Describe(NhAiBridgeResourceDescription description)
        {
            return description.Resource == "order"
                ? description with { Description = "Orders with their customer and status." }
                : description;
        }
    }
}
