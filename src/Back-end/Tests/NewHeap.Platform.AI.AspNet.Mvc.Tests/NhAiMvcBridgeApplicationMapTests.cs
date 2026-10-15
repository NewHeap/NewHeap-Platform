using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// The application map of the bridge gateway: the resources a person may use, with relations by
/// convention and the explicit additions and removals.
/// </summary>
public sealed class NhAiMvcBridgeApplicationMapTests
{
    [Fact]
    public async Task The_map_lists_the_resources_a_person_may_use_with_their_relations_and_reading_tools()
    {
        using var factory = CreateFactory();

        var viewer = await MapAsync(factory, TestTokenHandler.Viewer);
        var manager = await MapAsync(factory, TestTokenHandler.Manager);
        var outsider = await MapAsync(factory, TestTokenHandler.Outsider);

        var order = Assert.Single(viewer);
        Assert.Equal("order", order.Resource);
        Assert.Equal("Orders with their customer and status.", order.Summary);
        Assert.Equal(["query", "get"], order.Operations);
        Assert.Equal(["test-gateway.query", "test-gateway.get"], order.ToolIds);
        Assert.Empty(order.Relations);
        Assert.Equal(["order", "order-audit"], manager.Select(entry => entry.Resource));
        var audit = manager[1];
        Assert.Equal([new NhAiApplicationMapRelation("orderId", "order")], audit.Relations);
        Assert.Empty(outsider);
        Assert.Equal(1, factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>().Gateway!.DerivedRelations);
    }

    [Fact]
    public async Task Explicit_relations_add_and_remove_what_the_convention_derives()
    {
        using var factory = CreateFactory(gateway => gateway
            .IgnoreRelation("order-audit", "orderId")
            .Relate("order-audit", "reviewedOrder", "order"));

        var manager = await MapAsync(factory, TestTokenHandler.Manager);

        Assert.Equal([new NhAiApplicationMapRelation("reviewedOrder", "order")], manager[1].Relations);
    }

    [Theory]
    [InlineData("missing", "orderId", "order", "names resource 'missing'")]
    [InlineData("order-audit", "unknownField", "order", "no filter field")]
    [InlineData("order-audit", "orderId", "missing", "names resource 'missing'")]
    public void A_relation_to_an_unknown_resource_or_filter_field_fails_at_startup(
        string resource,
        string field,
        string target,
        string message)
    {
        using var factory = CreateFactory(gateway => gateway.Relate(resource, field, target));

        var failure = Assert.ThrowsAny<Exception>(() => factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>().Descriptors);

        Assert.Contains(message, Flatten(failure), StringComparison.Ordinal);
    }

    private static string Flatten(Exception exception)
    {
        return exception is AggregateException aggregate
            ? string.Join(" ", aggregate.Flatten().InnerExceptions.Select(inner => inner.Message))
            : exception.Message + " " + exception.InnerException?.Message;
    }

    private static Task<IReadOnlyList<NhAiApplicationMapEntry>> MapAsync(BridgeApiFactory factory, string token)
    {
        return factory.AsUserAsync(token, async services =>
        {
            var source = Assert.Single(services.GetServices<INhAiApplicationMapSource>());
            return await source.GetEntriesAsync(
                new NhAiInvocationContext("actor", "assistant", new Dictionary<string, string>()),
                CancellationToken.None);
        });
    }

    private static BridgeApiFactory CreateFactory(Action<NhAiMvcBridgeGatewayBuilder>? configure = null)
    {
        return new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Order", "OrderAudit")
            .UseConventions<RelationConventions>()
            .EnableGateway(gateway =>
            {
                gateway
                    .UseGatewayToolSetId("test-gateway")
                    .UseResourceDescriber<NhAiMvcBridgeGatewayTests.GatewayTestDescriber>();
                configure?.Invoke(gateway);
            }));
    }

    /// <summary>
    /// The audit resource filters on the order it belongs to and on the order a person reviewed.
    /// </summary>
    public sealed class RelationConventions : NhAiMvcBridgeDefaultConventions
    {
        public override NhAiBridgeQueryDescription DescribeQuery(NhAiBridgeActionInfo action)
        {
            return base.DescribeQuery(action) with
            {
                FilterFields = action.ControllerName == "OrderAudit"
                    ?
                    [
                        new NhAiBridgeFilterField("orderId", "integer", ["eq"]),
                        new NhAiBridgeFilterField("reviewedOrder", "integer", ["eq"]),
                        new NhAiBridgeFilterField("customer", "string", ["eq"])
                    ]
                    : [new NhAiBridgeFilterField("customer", "string", ["eq"])]
            };
        }
    }
}
