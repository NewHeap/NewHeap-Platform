using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// The gateway's resources for NewHeap's agent builder (FB95): its own search-resources and
/// describe-resource with the rights of the person of the request, registered with the gateway only.
/// </summary>
public sealed class NhAiMvcBridgeGatewayResourcesTests
{
    /// <summary>The seam is internal to NewHeap; the test names it by the type that implements it.</summary>
    private static readonly Type ResourcesType = typeof(NhAiMvcBridgeGatewayResources)
        .GetInterfaces()
        .Single(type => type.Name == "INhAiGatewayResources");

    [Fact]
    public async Task The_builder_searches_and_describes_only_the_resources_the_person_may_use()
    {
        using var factory = CreateFactory();

        var viewer = await AsAsync(factory, TestTokenHandler.Viewer, resources => resources.SearchAsync("order", 10, CancellationToken.None));
        var manager = await AsAsync(factory, TestTokenHandler.Manager, resources => resources.SearchAsync("order", 10, CancellationToken.None));
        var outsider = await AsAsync(factory, TestTokenHandler.Outsider, resources => resources.SearchAsync("order", 10, CancellationToken.None));
        Assert.Equal(["order"], Names(viewer.Data));
        Assert.Equal(["order", "order-audit"], Names(manager.Data).Order(StringComparer.Ordinal));
        Assert.Empty(Names(outsider.Data));

        // A resource the person may not use cannot be told from one that does not exist.
        var hidden = await AsAsync(factory, TestTokenHandler.Viewer, resources => resources.DescribeAsync("order-audit", CancellationToken.None));
        var missing = await AsAsync(factory, TestTokenHandler.Manager, resources => resources.DescribeAsync("unknown", CancellationToken.None));
        Assert.Equal(NhAiBridgeFailureCodes.ResourceNotFound, hidden.GetResultItems().First().Name);
        Assert.Equal(NhAiBridgeFailureCodes.ResourceNotFound, missing.GetResultItems().First().Name);

        var described = await AsAsync(factory, TestTokenHandler.Manager, resources => resources.DescribeAsync("order-audit", CancellationToken.None));
        Assert.True(described.Success);
        Assert.Equal("order-audit", described.Data.GetProperty("resource").GetString());
        Assert.True(described.Data.TryGetProperty("query", out _));

        var tools = await AsAsync(factory, TestTokenHandler.Viewer, resources => Task.FromResult(resources.ToolIds));
        Assert.Equal(
            ["test-gateway.describe-resource", "test-gateway.get", "test-gateway.query", "test-gateway.search-resources"],
            tools.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Without_the_gateway_the_builder_finds_no_resources_to_read()
    {
        using var factory = new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Order"));

        Assert.Null(factory.Services.CreateScope().ServiceProvider.GetService(ResourcesType));
    }

    private static Task<T> AsAsync<T>(BridgeApiFactory factory, string token, Func<NhAiMvcBridgeGatewayResources, Task<T>> read)
    {
        return factory.AsUserAsync(token, services => read((NhAiMvcBridgeGatewayResources)services.GetRequiredService(ResourcesType)));
    }

    private static IReadOnlyList<string> Names(JsonElement resources)
    {
        return resources.EnumerateArray().Select(resource => resource.GetProperty("resource").GetString()!).ToArray();
    }

    private static BridgeApiFactory CreateFactory()
    {
        return new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Order", "OrderAudit")
            .EnableGateway(gateway => gateway
                .UseGatewayToolSetId("test-gateway")
                .UseResourceDescriber<NhAiMvcBridgeGatewayTests.GatewayTestDescriber>()));
    }
}
