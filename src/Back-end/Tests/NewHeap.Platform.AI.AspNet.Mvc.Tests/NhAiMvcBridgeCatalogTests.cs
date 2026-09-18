using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

public sealed class NhAiMvcBridgeCatalogTests
{
    [Fact]
    public void Catalog_snapshot_lists_the_published_actions_with_contract_fields()
    {
        using var factory = new BridgeApiFactory();
        var catalog = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>();

        var snapshot = catalog.Descriptors
            .Select(descriptor => string.Join(
                " | ",
                descriptor.Id,
                descriptor.ExportName,
                descriptor.Effect,
                descriptor.Approval,
                descriptor.Idempotency,
                descriptor.Exposure,
                string.Join(",", descriptor.AuthorizationPolicies),
                (int)descriptor.Timeout.TotalSeconds,
                descriptor.MaxResultBytes,
                descriptor.MaxInputBytes))
            .ToArray();

        Assert.Equal(
            [
                "test-api.order.approval-read | test-api_order_approval-read_v1 | ReadOnly | Required | None | Local, Mcp, Agent | order.view | 30 | 65536 | 16384",
                "test-api.order.create | test-api_order_create_v1 | Mutation | Required | Required | Local, Mcp, Agent | order.manage,order.view | 30 | 65536 | 16384",
                "test-api.order.described | test-api_order_described_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 5 | 2048 | 16384",
                "test-api.order.get | test-api_order_get_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 30 | 65536 | 16384",
                "test-api.order.get-by-id | test-api_order_get-by-id_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 30 | 65536 | 16384",
                "test-api.order.notify | test-api_order_notify_v1 | ExternalSideEffect | Required | Required | Local, Mcp, Agent | order.manage,order.view | 30 | 65536 | 16384",
                "test-api.order.search | test-api_order_search_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 30 | 65536 | 16384",
                "test-api.order.update | test-api_order_update_v1 | IdempotentMutation | Required | Required | Local, Mcp, Agent | order.manage,order.view | 30 | 65536 | 16384",
                "test-api.probe.broken | test-api_probe_broken_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 30 | 65536 | 16384",
                "test-api.probe.conflict | test-api_probe_conflict_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 30 | 65536 | 16384",
                "test-api.probe.echo-read | test-api_probe_echo-read_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 30 | 65536 | 16384",
                "test-api.probe.echo-write | test-api_probe_echo-write_v1 | Mutation | Required | Required | Local, Mcp, Agent | order.manage,order.view | 30 | 65536 | 16384",
                "test-api.probe.forbidden | test-api_probe_forbidden_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 30 | 65536 | 16384",
                "test-api.probe.large | test-api_probe_large_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 30 | 65536 | 16384",
                "test-api.probe.slow | test-api_probe_slow_v1 | ReadOnly | PolicyControlled | None | Local, Mcp, Agent | order.view | 1 | 65536 | 16384",
                "test-api.probe.validate | test-api_probe_validate_v1 | Mutation | Required | Required | Local, Mcp, Agent | order.manage,order.view | 30 | 65536 | 16384"
            ],
            snapshot);
        Assert.All(catalog.Descriptors, descriptor =>
        {
            Assert.True(descriptor.RequiresAuthorization);
            Assert.Equal("test-api", descriptor.CatalogId);
            Assert.Equal(64, descriptor.ContractHash.Length);
        });
        Assert.Equal(catalog.AttestationHash, catalog.Manifest.SchemaHash);
    }

    [Fact]
    public void Delete_anonymous_non_action_upload_hidden_and_policy_less_actions_are_absent()
    {
        using var factory = new BridgeApiFactory();
        var ids = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>()
            .Descriptors
            .Select(descriptor => descriptor.Id)
            .ToArray();

        Assert.DoesNotContain("test-api.order.delete", ids);
        Assert.DoesNotContain("test-api.order.public", ids);
        Assert.DoesNotContain("test-api.order.helper", ids);
        Assert.DoesNotContain("test-api.order.upload", ids);
        Assert.DoesNotContain("test-api.order.hidden", ids);
        Assert.DoesNotContain("test-api.unnamed-policy.get", ids);
    }

    [Fact]
    public void Policy_less_actions_are_published_when_explicit_policies_are_not_required()
    {
        using var factory = new BridgeApiFactory(bridge =>
        {
            BridgeApiFactory.DefaultBridge(bridge);
            bridge.RequireExplicitPolicy(false);
        });

        var descriptor = Assert.Single(
            factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>().Descriptors,
            item => item.Id == "test-api.unnamed-policy.get");
        Assert.Empty(descriptor.AuthorizationPolicies);
        Assert.True(descriptor.RequiresAuthorization);
    }

    [Fact]
    public void File_uploads_are_published_only_when_enabled()
    {
        using var factory = new BridgeApiFactory(bridge =>
        {
            BridgeApiFactory.DefaultBridge(bridge);
            bridge.IncludeFileUploads(true);
        });

        var descriptor = Assert.Single(
            factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>().Descriptors,
            item => item.Id == "test-api.order.upload");
        Assert.Contains("contentBase64", descriptor.InputSchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_prefers_attribute_then_xml_summary_then_endpoint_metadata()
    {
        using var factory = new BridgeApiFactory();
        var descriptors = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>()
            .Descriptors
            .ToDictionary(descriptor => descriptor.Id);

        Assert.Equal("Attribute description wins.", descriptors["test-api.order.described"].Description);
        Assert.Equal(
            "Lists orders with paging, search, ordering and filters.",
            descriptors["test-api.order.get"].Description);
        Assert.Equal(
            "Get an order. Returns one order by identifier.",
            descriptors["test-api.order.get-by-id"].Description);
        Assert.Equal(
            "Calls GET /orders/search as the signed-in user.",
            descriptors["test-api.order.search"].Description);
    }

    [Fact]
    public void Include_and_exclude_patterns_select_controllers_and_actions()
    {
        using var factory = new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("*")
            .ExcludeControllers("Probe", "Collision", "Unnamed*")
            .ExcludeActions("Order.Get", "Order.Search"));

        var ids = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>()
            .Descriptors
            .Select(descriptor => descriptor.Id)
            .ToArray();

        Assert.Contains("test-api.order.create", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("test-api.probe.", StringComparison.Ordinal));
        Assert.DoesNotContain("test-api.order.get", ids);
        Assert.DoesNotContain("test-api.order.get-by-id", ids);
        Assert.DoesNotContain("test-api.order.search", ids);
        Assert.All(factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>().Descriptors, descriptor =>
            Assert.False(descriptor.Exposure.HasFlag(NhAiToolExposure.Mcp)));
    }

    [Fact]
    public void Tool_id_collision_fails_at_startup_with_both_route_templates()
    {
        using var factory = new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Collision"));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("test-api.collision.get-by-id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GET /collision/first/{id}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GET /collision/second/{id}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Include_delete_actions_fails_at_startup_because_v1_does_not_support_delete()
    {
        using var factory = new BridgeApiFactory(bridge =>
        {
            BridgeApiFactory.DefaultBridge(bridge);
            bridge.IncludeDeleteActions(true);
        });

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("does not support DELETE actions in v1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_with_get_post_and_put_bridge_tools_passes_the_platform_startup_validator()
    {
        using var factory = new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Order"));

        var descriptors = factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>().Descriptors;

        Assert.Contains(descriptors, descriptor => descriptor.Effect == NhAiToolEffect.ReadOnly);
        Assert.Contains(descriptors, descriptor => descriptor.Effect == NhAiToolEffect.Mutation);
        Assert.Contains(descriptors, descriptor => descriptor.Effect == NhAiToolEffect.IdempotentMutation);
        Assert.All(
            descriptors.Where(descriptor => descriptor.Effect != NhAiToolEffect.ReadOnly),
            descriptor => Assert.Equal(NhAiApprovalRequirement.Required, descriptor.Approval));
    }

    [Fact]
    public void Missing_self_base_url_fails_at_startup()
    {
        using var factory = new BridgeApiFactory(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl(null)
            .IncludeControllers("Order"));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("UseSelfBaseUrl", exception.Message, StringComparison.Ordinal);
    }
}
