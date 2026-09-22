using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using NewHeap.Platform.AI.Mcp;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// Bridge tools share the governed-function envelope tolerance: flat arguments run once with the
/// same request as the envelope, while the bridge input schema still rejects unknown properties.
/// </summary>
public sealed class NhAiMvcBridgeArgumentEnvelopeTests
{
    [Fact]
    public async Task Flat_arguments_produce_the_same_result_as_the_envelope()
    {
        using var factory = new BridgeApiFactory();

        var enveloped = await factory.InvokeAsync(TestTokenHandler.Viewer, "test-api_order_get-by-id_v1", new { id = 42 });
        var flat = await factory.AsUserAsync(TestTokenHandler.Viewer, async services =>
        {
            var function = services.GetRequiredServiceCatalogFunction("test-api_order_get-by-id_v1");
            var output = await function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
            {
                ["id"] = System.Text.Json.JsonSerializer.SerializeToElement(42)
            });
            return new BridgeToolResult((System.Text.Json.JsonElement)output!);
        });

        Assert.True(flat.Success, flat.Errors);
        Assert.Equal(enveloped.Data.GetRawText(), flat.Data.GetRawText());
        Assert.Equal(2, factory.Logs.Entries.Count(entry => entry.Contains("test-api.order.get-by-id returned HTTP 200", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Flat_unknown_property_is_rejected_by_the_bridge_input_schema_and_mixed_shape_is_input_invalid()
    {
        using var factory = new BridgeApiFactory();

        var (unknown, mixed) = await factory.WithMcpClientAsync(TestTokenHandler.Viewer, async client =>
        {
            var tool = (await client.ListToolsAsync()).Single(item => item.Name == "test-api_order_get-by-id_v1");
            var unknownResult = await tool.CallAsync(new Dictionary<string, object?> { ["id"] = 1, ["unexpected"] = true });
            var mixedResult = await tool.CallAsync(new Dictionary<string, object?>
            {
                ["input"] = new { id = 1 },
                ["id"] = 2
            });
            return (unknownResult, mixedResult);
        });

        Assert.True(unknown.IsError);
        Assert.Equal(NhAiBridgeFailureCodes.Validation, Code(unknown));
        Assert.True(mixed.IsError);
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, Code(mixed));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("test-api.order.get-by-id returned HTTP", StringComparison.Ordinal));
    }

    private static string? Code(CallToolResult result)
    {
        return result.Meta?[NhAiMcpResultMetadata.CodeKey]?.GetValue<string>();
    }
}

internal static class BridgeCatalogServiceExtensions
{
    public static Microsoft.Extensions.AI.AIFunction GetRequiredServiceCatalogFunction(this IServiceProvider services, string exportName)
    {
        var catalog = (NhAiMvcBridgeToolCatalog)services.GetService(typeof(NhAiMvcBridgeToolCatalog))!;
        return catalog.CreateFunctions(services).Single(function => function.Name == exportName);
    }
}
