using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
        using var factory = CreateFactory();

        var described = await factory.InvokeAsync(TestTokenHandler.Viewer, Describe, new { resource = "legacy-order" });

        Assert.True(described.Success, described.Errors);
        Assert.False(described.Data.TryGetProperty("query", out _));
        Assert.Equal("id", described.Data.GetProperty("get").GetProperty("idParameter").GetString());
    }

    [Fact]
    public async Task Recognized_collection_action_is_described_and_queried_with_the_bridge_request()
    {
        using var factory = CreateFactory(bridge => bridge.AddCollectionContractProvider<LegacyCollectionContractProvider>());

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
            "GET /legacy-orders?Region=north&legacyPage=2&legacyItemsPerPage=5&legacySearch=acme"
            + "&legacyOrderBy=" + Uri.EscapeDataString("[{\"key\":\"customer\",\"direction\":\"desc\"}]")
            + "&legacyFilter=" + Uri.EscapeDataString("[{\"key\":\"customer\",\"operator\":\"eq\",\"value\":\"acme\"}]"),
            requests[1]);
    }

    [Fact]
    public async Task Unknown_filter_key_on_a_recognized_collection_is_rejected_before_the_http_call()
    {
        using var factory = CreateFactory(bridge => bridge.AddCollectionContractProvider<LegacyCollectionContractProvider>());

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

    [Fact]
    public async Task Canonical_newheap_collection_is_described_without_consumer_conventions()
    {
        using var factory = new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("CanonicalOrder")
            .EnableMcpExposure()
            .EnableGateway(gateway => gateway.UseGatewayToolSetId("test-gateway")));

        var described = await factory.InvokeAsync(TestTokenHandler.Viewer, Describe, new { resource = "canonical-order" });
        var query = await factory.InvokeAsync(TestTokenHandler.Viewer, Query, new
        {
            resource = "canonical-order",
            filter = new[] { new { key = "status", @operator = "==", value = "Draft" } },
            orderBy = new[] { new { key = "customer", direction = "asc" } }
        });

        Assert.True(described.Success, described.Errors);
        var description = described.Data.GetProperty("query");
        Assert.True(description.GetProperty("searchable").GetBoolean());
        Assert.Contains(description.GetProperty("filterFields").EnumerateArray(), field =>
            field.GetProperty("key").GetString() == "status"
            && field.GetProperty("type").GetString() == "enum");
        Assert.True(query.Success, query.Errors);
        Assert.Contains("filter=", Assert.Single(factory.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trusted_query_binding_overwrites_model_input_from_audited_invocation_scope()
    {
        using var factory = CreateFactory(bridge => bridge
            .AddCollectionContractProvider<LegacyCollectionContractProvider>()
            .AddTrustedQueryBindingProvider<RegionTrustedQueryBindingProvider>());
        var catalog = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
        var descriptor = catalog.Descriptors.Single(item => item.Id == "test-api.legacy-order.list");
        Assert.True(catalog.TryGetAction(descriptor, out var action));
        var context = new NhAiInvocationContext(
            "actor-1",
            "test",
            new Dictionary<string, string> { ["region-id"] = "trusted" });

        var result = await factory.AsUserAsync(TestTokenHandler.Viewer, services => services
            .GetRequiredService<INhAiMvcBridgeExecutor>()
            .ExecuteAsync(
                action,
                descriptor,
                JsonSerializer.SerializeToElement(new { region = "untrusted", page = 1 }),
                context));

        Assert.True(result.Success);
        Assert.StartsWith("trusted|1|", result.Data!.Body!.Value[0].GetProperty("customer").GetString(), StringComparison.Ordinal);
        Assert.Contains("Region=trusted", Assert.Single(factory.Requests), StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted", Assert.Single(factory.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Localized_resource_presentation_uses_fallbacks_and_rejects_orphaned_resources()
    {
        using var factory = new BridgeApiFactory(
            bridge => bridge
                .UseToolSetId("test-api")
                .UseSelfBaseUrl("http://localhost/")
                .IncludeControllers("CanonicalOrder")
                .EnableMcpExposure()
                .EnableGateway(gateway => gateway
                    .UseGatewayToolSetId("test-gateway")
                    .UseLocalizedResourcePresentation<ResourceMarker>(presentation =>
                    {
                        presentation.RequireLocalizedValues = false;
                        presentation.Add(
                            "canonical-order",
                            "CanonicalOrderTitle",
                            "CanonicalOrderSummary",
                            "Canonical orders",
                            "Authorized canonical orders.");
                    })),
            services => services.AddLocalization());

        var described = await factory.InvokeAsync(TestTokenHandler.Viewer, Describe, new { resource = "canonical-order" });

        Assert.True(described.Success, described.Errors);
        Assert.Equal("Canonical orders", described.Data.GetProperty("title").GetString());
        Assert.Equal("Authorized canonical orders.", described.Data.GetProperty("description").GetString());

        using var invalid = new BridgeApiFactory(
            bridge => bridge
                .UseToolSetId("test-api")
                .UseSelfBaseUrl("http://localhost/")
                .IncludeControllers("CanonicalOrder")
                .EnableGateway(gateway => gateway
                    .UseLocalizedResourcePresentation<ResourceMarker>(presentation =>
                    {
                        presentation.RequireLocalizedValues = false;
                        presentation.Add("ghost", "Title", "Summary", "Ghost", "Ghost resource.");
                    })),
            services => services.AddLocalization());
        var exception = Assert.Throws<InvalidOperationException>(() => invalid.Services);
        Assert.Contains("orphaned resources", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trusted_collection_filter_replaces_the_same_model_supplied_filter()
    {
        using var factory = new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("CanonicalOrder")
            .AddTrustedQueryBindingProvider<StatusTrustedFilterProvider>());
        var catalog = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
        var descriptor = catalog.Descriptors.Single(item => item.Id == "test-api.canonical-order.get");
        Assert.True(catalog.TryGetAction(descriptor, out var action));
        var context = new NhAiInvocationContext(
            "viewer",
            "test",
            new Dictionary<string, string> { ["status"] = "Confirmed" });

        var result = await factory.AsUserAsync(TestTokenHandler.Viewer, services => services
            .GetRequiredService<INhAiMvcBridgeExecutor>()
            .ExecuteAsync(
                action,
                descriptor,
                JsonSerializer.SerializeToElement(new
                {
                    filter = new[] { new { key = "status", @operator = "==", value = "Draft" } }
                }),
                context));

        Assert.True(result.Success);
        var uri = Assert.Single(factory.Requests);
        Assert.Contains("Confirmed", Uri.UnescapeDataString(uri), StringComparison.Ordinal);
        Assert.DoesNotContain("Draft", Uri.UnescapeDataString(uri), StringComparison.Ordinal);
    }

    private static BridgeApiFactory CreateFactory(Action<NhAiMvcBridgeBuilder>? configure = null)
    {
        return new BridgeApiFactory(bridge =>
        {
            bridge
                .UseToolSetId("test-api")
                .UseSelfBaseUrl("http://localhost/")
                .IncludeControllers("LegacyOrder")
                .EnableMcpExposure()
                .EnableGateway(gateway => gateway.UseGatewayToolSetId("test-gateway"));
            configure?.Invoke(bridge);
        });
    }

    /// <summary>Recognizes and encodes the deliberately noncanonical legacy list endpoint.</summary>
    public sealed class LegacyCollectionContractProvider : INhAiBridgeCollectionContractProvider
    {
        public bool TryDescribe(NhAiBridgeActionInfo action, out NhAiBridgeCollectionContract contract)
        {
            if (action.ControllerName != "LegacyOrder" || action.ActionName != "List")
            {
                contract = null!;
                return false;
            }
            contract = new NhAiBridgeCollectionContract(
                typeof(TestOrder),
                new NhAiBridgeQueryDescription
                {
                    Searchable = true,
                    FilterFields = [new("customer", "string", ["eq"])],
                    OrderFields = ["customer"],
                    ResultFields = [new("id", "integer"), new("customer", "string"), new("status", "enum")]
                });
            return true;
        }

        public bool TryEncodeQuery(
            NhAiBridgeActionInfo action,
            JsonElement input,
            NhAiBridgeHttpRequest request)
        {
            Add("page", "legacyPage");
            Add("itemsPerPage", "legacyItemsPerPage");
            Add("search", "legacySearch");
            Add("orderBy", "legacyOrderBy");
            Add("filter", "legacyFilter");
            return true;

            void Add(string inputName, string queryName)
            {
                if (input.TryGetProperty(inputName, out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    request.Query.Add(new(queryName, value.ValueKind == JsonValueKind.String
                        ? value.GetString()!
                        : value.GetRawText()));
                }
            }
        }
    }

    public sealed class RegionTrustedQueryBindingProvider : INhAiBridgeTrustedQueryBindingProvider
    {
        public ValueTask<IReadOnlyList<NhAiBridgeTrustedQueryBinding>> GetBindingsAsync(
            NhAiBridgeActionInfo action,
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<NhAiBridgeTrustedQueryBinding> bindings =
                action.ControllerName == "LegacyOrder" && action.ActionName == "List"
                    ? [new("Region", "region-id")]
                    : [];
            return ValueTask.FromResult(bindings);
        }
    }

    public sealed class ResourceMarker;

    public sealed class StatusTrustedFilterProvider : INhAiBridgeTrustedQueryBindingProvider
    {
        public ValueTask<IReadOnlyList<NhAiBridgeTrustedQueryBinding>> GetBindingsAsync(
            NhAiBridgeActionInfo action,
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<NhAiBridgeTrustedQueryBinding> bindings =
                action.ControllerName == "CanonicalOrder"
                    ? [new("status", "status", NhAiBridgeTrustedQueryBindingKind.CollectionFilter)]
                    : [];
            return ValueTask.FromResult(bindings);
        }
    }
}
