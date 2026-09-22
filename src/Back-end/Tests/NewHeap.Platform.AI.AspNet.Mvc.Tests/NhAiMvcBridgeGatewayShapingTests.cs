using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// Gateway <c>query</c> and <c>get</c> shape results after the HTTP call and before the size
/// bound: projection, default compaction, count-only queries, redaction and structured truncation.
/// </summary>
public sealed class NhAiMvcBridgeGatewayShapingTests
{
    private const string Describe = "test-gateway_describe-resource_v1";
    private const string Query = "test-gateway_query_v1";
    private const string Get = "test-gateway_get_v1";

    [Fact]
    public async Task Default_query_compacts_nested_objects_and_omits_nulls_and_collection_echoes()
    {
        using var factory = CreateFactory();

        var result = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new { resource = "staff-project", itemsPerPage = 2 });

        Assert.True(result.Success, result.Errors);
        var body = result.Data.GetProperty("body");
        Assert.Equal(StaffProjectController.TotalCount, body.GetProperty("totalCount").GetInt64());
        Assert.Equal(2, body.GetProperty("resultCount").GetInt32());
        Assert.False(body.TryGetProperty("filter", out _));
        Assert.False(body.TryGetProperty("orderBy", out _));
        var item = body.GetProperty("items")[0];
        Assert.Equal("Project 1", item.GetProperty("name").GetString());
        Assert.False(item.TryGetProperty("description", out _));
        Assert.Equal(new[] { "id", "name" }, Names(item.GetProperty("manager")));
        Assert.Equal(new[] { "id", "name" }, Names(item.GetProperty("members")[1]));
        Assert.Equal(2, item.GetProperty("tags").GetArrayLength());
        Assert.False(result.Data.GetProperty("truncated").GetBoolean());
        Assert.Equal(NhAiBridgeResultShaper.CompactedHint, result.Data.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task Fields_project_each_item_including_one_level_dotted_paths()
    {
        using var factory = CreateFactory();

        var query = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "staff-project",
            itemsPerPage = 3,
            fields = new[] { "id", "manager.name", "manager.id", "members.name" }
        });
        var get = await factory.InvokeAsync(TestTokenHandler.Viewer, Get, new
        {
            resource = "staff-project",
            id = 7,
            fields = new[] { "name", "manager.address" }
        });

        Assert.True(query.Success, query.Errors);
        var item = query.Data.GetProperty("body").GetProperty("items")[2];
        Assert.Equal(new[] { "id", "manager", "members" }, Names(item));
        Assert.Equal(new[] { "name", "id" }, Names(item.GetProperty("manager")));
        Assert.Equal(new[] { "name" }, Names(item.GetProperty("members")[0]));
        Assert.False(query.Data.TryGetProperty("hint", out _));
        Assert.True(get.Success, get.Errors);
        var body = get.Data.GetProperty("body");
        Assert.Equal(new[] { "name", "manager" }, Names(body));
        Assert.Equal("City", body.GetProperty("manager").GetProperty("address").GetProperty("city").GetString());
    }

    [Theory]
    [InlineData("salary")]
    [InlineData("manager.address.city")]
    [InlineData("")]
    public async Task Unknown_or_too_deep_fields_are_rejected_before_the_http_call(string field)
    {
        using var factory = CreateFactory();

        var result = await CallAsync(factory, Query, new { resource = "staff-project", fields = new[] { field } });

        Assert.True(result.IsError);
        Assert.Equal(NhAiBridgeFailureCodes.Validation, Code(result));
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Count_only_returns_the_total_from_a_single_page_request_without_ordering()
    {
        using var factory = CreateFactory();

        var result = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "staff-project",
            countOnly = true,
            page = 4,
            itemsPerPage = 50,
            filter = new[] { new { key = "name", @operator = "LIKE", value = "Project" } },
            orderBy = new[] { new { key = "name", direction = "desc" } }
        });

        Assert.True(result.Success, result.Errors);
        var body = result.Data.GetProperty("body");
        Assert.Equal(new[] { "totalCount" }, Names(body));
        Assert.Equal(StaffProjectController.TotalCount, body.GetProperty("totalCount").GetInt64());
        var request = Uri.UnescapeDataString(Assert.Single(factory.Requests));
        Assert.Contains("page=1&itemsPerPage=1", request, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"name\"", request, StringComparison.Ordinal);
        Assert.DoesNotContain("orderBy", request, StringComparison.Ordinal);
        Assert.DoesNotContain("countOnly", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collection_contract_provider_maps_count_only_to_the_api_contract()
    {
        using var factory = CreateFactory(bridge => bridge.AddCollectionContractProvider<CountingCollectionContractProvider>());

        var result = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new { resource = "staff-project", countOnly = true });

        Assert.True(result.Success, result.Errors);
        Assert.Equal(StaffProjectController.TotalCount, result.Data.GetProperty("body").GetProperty("totalCount").GetInt64());
        Assert.Contains("countOnly=true", Assert.Single(factory.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Count_only_on_a_list_without_a_total_fails_with_a_validation_code()
    {
        using var factory = new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("LegacyOrder")
            .EnableMcpExposure()
            .AddCollectionContractProvider<NhAiMvcBridgeGatewayCollectionTests.LegacyCollectionContractProvider>()
            .EnableGateway(gateway => gateway.UseGatewayToolSetId("test-gateway")));

        var result = await CallAsync(factory, Query, new { resource = "legacy-order", countOnly = true });

        Assert.True(result.IsError);
        Assert.Equal(NhAiBridgeFailureCodes.Validation, Code(result));
        Assert.Contains("legacyPage=1", Assert.Single(factory.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redacted_fields_are_removed_at_every_depth_and_never_described_selected_or_filtered()
    {
        using var factory = CreateFactory(configureGateway: gateway => gateway.RedactResultFields("*email*", "phoneNumber"));

        var described = await factory.InvokeAsync(TestTokenHandler.Viewer, Describe, new { resource = "staff-project" });
        var get = await factory.InvokeAsync(TestTokenHandler.Viewer, Get, new
        {
            resource = "staff-project",
            id = 3,
            fields = new[] { "id", "manager", "members" }
        });
        var query = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new { resource = "staff-project", itemsPerPage = 1 });
        var requestsBeforeRejections = factory.Requests.Count;
        var selected = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new { resource = "staff-project", fields = new[] { "manager.email" } });
        var filtered = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "staff-project",
            filter = new[] { new { key = "contactEmail", @operator = "LIKE", value = "project" } }
        });
        var ordered = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "staff-project",
            orderBy = new[] { new { key = "contactEmail" } }
        });

        Assert.True(described.Success, described.Errors);
        var description = described.Data;
        Assert.DoesNotContain("contactEmail", Keys(description.GetProperty("resultFields")));
        Assert.DoesNotContain("contactEmail", Keys(description.GetProperty("query").GetProperty("filterFields")));
        Assert.DoesNotContain(
            "contactEmail",
            description.GetProperty("query").GetProperty("orderFields").EnumerateArray().Select(field => field.GetString()));
        Assert.True(description.GetProperty("resultShaping").GetProperty("countOnly").GetBoolean());
        Assert.Equal("compact", description.GetProperty("resultShaping").GetProperty("default").GetString());

        Assert.True(get.Success, get.Errors);
        var raw = get.Data.GetProperty("body").GetRawText();
        Assert.DoesNotContain("@example.test", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("+31", raw, StringComparison.Ordinal);
        Assert.Contains("Street 30", raw, StringComparison.Ordinal);
        Assert.True(query.Success, query.Errors);
        Assert.DoesNotContain("@example.test", query.Data.GetRawText(), StringComparison.Ordinal);

        Assert.False(selected.Success);
        Assert.False(filtered.Success);
        Assert.False(ordered.Success);
        Assert.Equal(requestsBeforeRejections, factory.Requests.Count);
    }

    [Fact]
    public async Task Oversized_page_keeps_whole_items_and_returns_structured_truncation_guidance()
    {
        using var factory = CreateFactory();

        var result = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "staff-project",
            itemsPerPage = 50,
            parameters = new { noteLength = 3000 }
        });
        var projected = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "staff-project",
            itemsPerPage = 50,
            fields = new[] { "id", "name" },
            parameters = new { noteLength = 3000 }
        });

        Assert.True(result.Success, result.Errors);
        var data = result.Data;
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.TryGetProperty("bodyText", out _));
        var truncation = data.GetProperty("truncation");
        var returned = truncation.GetProperty("returnedCount").GetInt32();
        Assert.InRange(returned, 1, 49);
        Assert.Equal(returned, data.GetProperty("body").GetProperty("items").GetArrayLength());
        Assert.Equal(StaffProjectController.TotalCount, truncation.GetProperty("totalCount").GetInt64());
        Assert.Equal(50, truncation.GetProperty("resultCount").GetInt32());
        Assert.Equal(returned, truncation.GetProperty("suggestedItemsPerPage").GetInt32());
        var suggested = truncation.GetProperty("suggestedFields").EnumerateArray().Select(field => field.GetString()).ToArray();
        Assert.Equal(new[] { "id", "name" }, suggested.Take(2));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(data).Length <= 65_536);

        Assert.True(projected.Success, projected.Errors);
        Assert.False(projected.Data.GetProperty("truncated").GetBoolean());
        Assert.Equal(50, projected.Data.GetProperty("body").GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Oversized_single_item_returns_guidance_without_a_body()
    {
        using var factory = CreateFactory();

        var result = await factory.InvokeAsync(TestTokenHandler.Viewer, Get, new
        {
            resource = "staff-project",
            id = 1,
            parameters = new { noteLength = 100_000 }
        });

        Assert.True(result.Success, result.Errors);
        Assert.True(result.Data.GetProperty("truncated").GetBoolean());
        Assert.False(result.Data.TryGetProperty("body", out _));
        Assert.False(result.Data.TryGetProperty("bodyText", out _));
        Assert.Contains(
            "name",
            result.Data.GetProperty("truncation").GetProperty("suggestedFields").EnumerateArray().Select(field => field.GetString()));
    }

    [Fact]
    public async Task Response_beyond_the_read_limit_is_reported_without_parsing()
    {
        using var factory = CreateFactory(configureGateway: gateway => gateway.UseMaxResponseBytes(16 * 1024));

        var result = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "staff-project",
            itemsPerPage = 20,
            parameters = new { noteLength = 2000 }
        });

        Assert.True(result.Success, result.Errors);
        var data = result.Data;
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.TryGetProperty("body", out _));
        Assert.False(data.TryGetProperty("bodyText", out _));
        Assert.InRange(data.GetProperty("truncation").GetProperty("suggestedItemsPerPage").GetInt32(), 1, 19);
    }

    [Fact]
    public void Tool_and_resource_descriptions_explain_the_result_shaping()
    {
        using var factory = CreateFactory();

        var catalog = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
        var query = catalog.Descriptors.Single(descriptor => descriptor.Id == "test-gateway.query");
        var get = catalog.Descriptors.Single(descriptor => descriptor.Id == "test-gateway.get");

        Assert.Contains("compacted", query.Description, StringComparison.Ordinal);
        Assert.Contains("countOnly", query.InputSchemaJson, StringComparison.Ordinal);
        Assert.Contains("\"fields\"", get.InputSchemaJson, StringComparison.Ordinal);
        Assert.Contains("truncation", query.OutputSchemaJson, StringComparison.Ordinal);
    }

    private static BridgeApiFactory CreateFactory(
        Action<NhAiMvcBridgeBuilder>? configure = null,
        Action<NhAiMvcBridgeGatewayBuilder>? configureGateway = null)
    {
        return new BridgeApiFactory(bridge =>
        {
            bridge
                .UseToolSetId("test-api")
                .UseSelfBaseUrl("http://localhost/")
                .IncludeControllers("StaffProject")
                .EnableMcpExposure()
                .EnableGateway(gateway =>
                {
                    gateway.UseGatewayToolSetId("test-gateway");
                    configureGateway?.Invoke(gateway);
                });
            configure?.Invoke(bridge);
        });
    }

    private static Task<CallToolResult> CallAsync(BridgeApiFactory factory, string exportName, object input)
    {
        return factory.WithMcpClientAsync(TestTokenHandler.Viewer, async client =>
        {
            var tool = (await client.ListToolsAsync()).Single(item => item.Name == exportName);
            return await tool.CallAsync(new Dictionary<string, object?> { ["input"] = input });
        });
    }

    private static string? Code(CallToolResult result)
    {
        return result.Meta?[NhAiMcpResultMetadata.CodeKey]?.GetValue<string>();
    }

    private static string[] Names(JsonElement element)
    {
        return element.EnumerateObject().Select(property => property.Name).ToArray();
    }

    private static IEnumerable<string?> Keys(JsonElement fields)
    {
        return fields.EnumerateArray().Select(field => field.GetProperty("key").GetString());
    }

    /// <summary>Maps count-only queries to the API's own <c>countOnly</c> query value.</summary>
    public sealed class CountingCollectionContractProvider : INhAiBridgeCollectionContractProvider
    {
        private readonly NhAiNewHeapCollectionContractProvider _canonical = new();

        public bool TryDescribe(NhAiBridgeActionInfo action, out NhAiBridgeCollectionContract contract)
        {
            return _canonical.TryDescribe(action, out contract);
        }

        public bool TryEncodeCountQuery(NhAiBridgeActionInfo action, NhAiBridgeHttpRequest request)
        {
            request.Query.Add(new("countOnly", "true"));
            return true;
        }
    }
}
