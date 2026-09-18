using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

public sealed class NhAiMvcBridgeSchemaTests
{
    [Fact]
    public void Primitive_enum_and_nullable_query_parameters_are_top_level_properties()
    {
        Assert.Equal(
            "{\"type\":\"object\",\"properties\":{"
            + "\"text\":{\"type\":\"string\"},"
            + "\"status\":{\"enum\":[\"Draft\",\"Confirmed\",\"Shipped\",null]},"
            + "\"limit\":{\"type\":\"integer\"}},"
            + "\"required\":[\"text\"],\"additionalProperties\":false}",
            InputSchema("test-api.order.search"));
    }

    [Fact]
    public void Route_parameters_are_required_top_level_properties()
    {
        Assert.Equal(
            "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}},\"required\":[\"id\"],\"additionalProperties\":false}",
            InputSchema("test-api.order.get-by-id"));
    }

    [Fact]
    public void Nested_body_is_published_under_body_with_required_and_nullable_members()
    {
        Assert.Equal(
            "{\"type\":\"object\",\"properties\":{\"body\":{\"type\":\"object\",\"properties\":{"
            + "\"customer\":{\"type\":\"string\"},"
            + "\"status\":{\"enum\":[\"Draft\",\"Confirmed\",\"Shipped\"]},"
            + "\"deliverBy\":{\"type\":[\"string\",\"null\"],\"format\":\"date-time\"},"
            + "\"lines\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{"
            + "\"sku\":{\"type\":\"string\"},\"quantity\":{\"type\":\"integer\"}},\"required\":[\"sku\"]}}},"
            + "\"required\":[\"customer\"]}},"
            + "\"required\":[\"body\"],\"additionalProperties\":false}",
            InputSchema("test-api.order.create"));
    }

    [Fact]
    public void Collection_action_publishes_the_collection_fragment_and_extra_query_values()
    {
        var schema = JsonNode.Parse(InputSchema("test-api.order.get"))!.AsObject();
        var properties = schema["properties"]!.AsObject();

        Assert.Equal(
            ["page", "itemsPerPage", "search", "orderBy", "filter", "status"],
            properties.Select(property => property.Key).ToArray());
        Assert.Equal("integer", properties["page"]!["type"]!.GetValue<string>());
        Assert.Equal(1, properties["page"]!["default"]!.GetValue<int>());
        Assert.Equal(20, properties["itemsPerPage"]!["default"]!.GetValue<int>());
        Assert.Equal(
            "[\"asc\",\"desc\"]",
            properties["orderBy"]!["items"]!["properties"]!["direction"]!["enum"]!.ToJsonString());
        Assert.Equal(
            "[\"key\",\"operator\",\"value\"]",
            new JsonArray(properties["filter"]!["items"]!["properties"]!.AsObject()
                .Select(property => (JsonNode?)property.Key).ToArray()).ToJsonString());
        Assert.False(schema.ContainsKey("required"));
    }

    [Fact]
    public void Output_schema_is_the_bridge_response_envelope()
    {
        using var factory = new BridgeApiFactory();
        var descriptor = Descriptor(factory, "test-api.order.get-by-id");
        var properties = JsonNode.Parse(descriptor.OutputSchemaJson)!["properties"]!.AsObject();

        Assert.Equal(
            ["status", "contentType", "body", "truncated", "bodyBytes", "bodyText", "hint"],
            properties.Select(property => property.Key).ToArray());
        Assert.Equal(typeof(NhAiBridgeResponse), descriptor.OutputType);
        Assert.Equal(typeof(JsonElement), descriptor.InputType);
    }

    [Fact]
    public void Default_request_encodes_route_query_and_collection_values_in_the_newheap_contract()
    {
        using var factory = new BridgeApiFactory();
        var conventions = factory.Services.GetRequiredService<INhAiBridgeConventions>();
        var catalog = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>();

        var collection = conventions.BuildRequest(
            Action(catalog, "test-api.order.get"),
            JsonSerializer.SerializeToElement(new
            {
                page = 2,
                itemsPerPage = 5,
                search = "acme",
                orderBy = new[] { new { key = "customer", direction = "desc" } },
                filter = new[] { new { key = "status", @operator = "eq", value = "Draft" } },
                status = "Confirmed"
            }));
        var byId = conventions.BuildRequest(
            Action(catalog, "test-api.order.get-by-id"),
            JsonSerializer.SerializeToElement(new { id = 42 }));

        Assert.Equal("GET", collection.Method);
        Assert.Equal(
            "orders?page=2&itemsPerPage=5&search=acme"
            + "&orderBy=" + Uri.EscapeDataString("[{\"key\":\"customer\",\"direction\":\"DESC\"}]")
            + "&filter=" + Uri.EscapeDataString("[{\"key\":\"status\",\"operator\":\"eq\",\"value\":\"Draft\"}]")
            + "&Status=Confirmed",
            collection.BuildRelativeUri());
        Assert.Equal("orders/42", byId.BuildRelativeUri());
        Assert.Null(byId.Body);
    }

    [Fact]
    public void Input_that_cannot_be_mapped_is_rejected_before_the_api_is_called()
    {
        using var factory = new BridgeApiFactory();
        var conventions = factory.Services.GetRequiredService<INhAiBridgeConventions>();
        var catalog = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>();

        Assert.Throws<NhAiBridgeInputException>(() => conventions.BuildRequest(
            Action(catalog, "test-api.order.get-by-id"),
            JsonSerializer.SerializeToElement(new { })));
        Assert.Throws<NhAiBridgeInputException>(() => conventions.BuildRequest(
            Action(catalog, "test-api.order.get-by-id"),
            JsonSerializer.SerializeToElement(new { id = 1, unexpected = true })));
        Assert.Throws<NhAiBridgeInputException>(() => conventions.BuildRequest(
            Action(catalog, "test-api.order.create"),
            JsonSerializer.SerializeToElement(new { body = new { status = "Unknown" } })));
    }

    [Fact]
    public async Task Serialize_body_override_is_used_for_the_outgoing_request()
    {
        using var factory = new BridgeApiFactory(bridge =>
        {
            BridgeApiFactory.DefaultBridge(bridge);
            bridge.UseConventions<UpperCaseBodyConventions>();
        });
        var catalog = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
        var descriptor = Descriptor(factory, "test-api.probe.echo-write");

        var result = await factory.AsUserAsync(TestTokenHandler.Manager, services => services
            .GetRequiredService<INhAiMvcBridgeExecutor>()
            .ExecuteAsync(
                Action(catalog, descriptor.Id),
                descriptor,
                JsonSerializer.SerializeToElement(new { body = new { customer = "acme" } }),
                TestContexts.Create("key-1")));

        Assert.True(result.Success);
        Assert.Equal("ACME", result.Data!.Body!.Value.GetProperty("body").GetString());
    }

    private static string InputSchema(string id)
    {
        using var factory = new BridgeApiFactory();
        return Descriptor(factory, id).InputSchemaJson;
    }

    private static NhAiToolDescriptor Descriptor(BridgeApiFactory factory, string id)
    {
        return factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>()
            .Descriptors
            .Single(descriptor => descriptor.Id == id);
    }

    private static NhAiBridgeActionInfo Action(NhAiMvcBridgeToolCatalog catalog, string id)
    {
        Assert.True(catalog.TryGetAction(catalog.Descriptors.Single(descriptor => descriptor.Id == id), out var action));
        return action;
    }

    public sealed class UpperCaseBodyConventions : NhAiMvcBridgeDefaultConventions
    {
        public override string SerializeBody(object body)
        {
            var input = Assert.IsType<TestOrderInput>(body);
            input.Customer = input.Customer.ToUpperInvariant();
            return base.SerializeBody(input);
        }
    }
}
