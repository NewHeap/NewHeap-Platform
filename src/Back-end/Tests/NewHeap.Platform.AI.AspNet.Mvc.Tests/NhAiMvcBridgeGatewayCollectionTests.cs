using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using NewHeap.Platform.AI.Mcp;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// Consumer list endpoints that read the collection values from the query string themselves
/// become gateway <c>query</c> operations once the conventions recognize them.
/// </summary>
public sealed class NhAiMvcBridgeGatewayCollectionTests
{
    private const string Describe = "test-gateway_describe-resource_v1";
    private const string Query = "test-gateway_query_v1";

    [Fact]
    public async Task Default_conventions_offer_only_get_for_a_list_without_a_collection_model()
    {
        using var factory = CreateFactory<NhAiMvcBridgeGatewayTests.GatewayTestConventions>();

        var described = await factory.InvokeAsync(TestTokenHandler.Viewer, Describe, new { resource = "legacy-order" });

        Assert.True(described.Success, described.Errors);
        Assert.False(described.Data.TryGetProperty("query", out _));
        Assert.Equal("id", described.Data.GetProperty("get").GetProperty("idParameter").GetString());
    }

    [Fact]
    public async Task Recognized_collection_action_is_described_and_queried_with_the_bridge_request()
    {
        using var factory = CreateFactory<LegacyCollectionConventions>();

        var described = await factory.InvokeAsync(TestTokenHandler.Viewer, Describe, new { resource = "legacy-order" });
        var direct = await factory.InvokeAsync(TestTokenHandler.Viewer, "test-api_legacy-order_list_v1", new
        {
            region = "north",
            page = 2,
            itemsPerPage = 5,
            search = "acme",
            filter = new[] { new { key = "customer", @operator = "eq", value = "acme" } },
            orderBy = new[] { new { key = "customer", direction = "desc" } }
        });
        var query = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "legacy-order",
            page = 2,
            itemsPerPage = 5,
            search = "acme",
            filter = new[] { new { key = "customer", @operator = "eq", value = "acme" } },
            orderBy = new[] { new { key = "customer", direction = "desc" } },
            parameters = new { region = "north" }
        });

        Assert.True(described.Success, described.Errors);
        var describedQuery = described.Data.GetProperty("query");
        Assert.True(describedQuery.GetProperty("searchable").GetBoolean());
        Assert.Equal("customer", describedQuery.GetProperty("filterFields")[0].GetProperty("key").GetString());
        Assert.True(describedQuery.GetProperty("extraParameters").GetProperty("properties").TryGetProperty("region", out _));
        Assert.True(direct.Success, direct.Errors);
        Assert.True(query.Success, query.Errors);
        Assert.Equal("north|2|5|acme", query.Data.GetProperty("body")[0].GetProperty("customer").GetString());
        var requests = factory.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0], requests[1]);
        Assert.Equal(
            "GET /legacy-orders?Region=north&page=2&itemsPerPage=5&search=acme"
            + "&orderBy=" + Uri.EscapeDataString("[{\"key\":\"customer\",\"direction\":\"DESC\"}]")
            + "&filter=" + Uri.EscapeDataString("[{\"key\":\"customer\",\"operator\":\"eq\",\"value\":\"acme\"}]"),
            requests[1]);
    }

    [Fact]
    public async Task Unknown_filter_key_on_a_recognized_collection_is_rejected_before_the_http_call()
    {
        using var factory = CreateFactory<LegacyCollectionConventions>();

        var result = await factory.WithMcpClientAsync(TestTokenHandler.Viewer, async client =>
        {
            var query = (await client.ListToolsAsync()).Single(tool => tool.Name == Query);
            return await query.CallAsync(new Dictionary<string, object?>
            {
                ["input"] = new { resource = "legacy-order", filter = new[] { new { key = "internalRank", @operator = "eq", value = "1" } } }
            });
        });

        Assert.True(result.IsError);
        Assert.Equal(NhAiBridgeFailureCodes.Validation, result.Meta?[NhAiMcpResultMetadata.CodeKey]?.GetValue<string>());
        Assert.Empty(factory.Requests);
    }

    private static BridgeApiFactory CreateFactory<TConventions>()
        where TConventions : class, INhAiBridgeConventions
    {
        return new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("LegacyOrder")
            .UseConventions<TConventions>()
            .EnableMcpExposure()
            .EnableGateway(gateway => gateway.UseGatewayToolSetId("test-gateway")));
    }

    /// <summary>Recognizes the legacy list endpoint as a collection action.</summary>
    public sealed class LegacyCollectionConventions : NhAiMvcBridgeDefaultConventions
    {
        private readonly NhAiMvcBridgeGatewayTests.GatewayTestConventions _fields = new();

        public override bool IsCollectionAction(NhAiBridgeActionInfo action)
        {
            return base.IsCollectionAction(action)
                || (action.ControllerName == "LegacyOrder" && action.ActionName == "List");
        }

        public override NhAiBridgeQueryDescription DescribeQuery(NhAiBridgeActionInfo action)
        {
            return _fields.DescribeQuery(action) with { Searchable = IsCollectionAction(action) };
        }
    }
}
