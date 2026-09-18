using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

public sealed class NhAiMvcBridgeDiscoveryTests
{
    [Fact]
    public async Task User_without_the_policy_neither_sees_nor_calls_the_tool()
    {
        using var factory = new BridgeApiFactory();

        var outsiderTools = await DiscoverAsync(factory, TestTokenHandler.Outsider);
        var viewerTools = await DiscoverAsync(factory, TestTokenHandler.Viewer);
        var outsiderCall = await factory.InvokeAsync(TestTokenHandler.Outsider, "test-api_order_get-by-id_v1", new { id = 1 });
        var viewerMutation = await factory.InvokeAsync(
            TestTokenHandler.Viewer,
            "test-api_order_create_v1",
            new { body = new { customer = "acme" } });

        Assert.Empty(outsiderTools);
        Assert.Contains("test-api.order.get-by-id", viewerTools);
        Assert.DoesNotContain("test-api.order.create", viewerTools);
        Assert.False(outsiderCall.Success);
        Assert.False(viewerMutation.Success);
        var denied = factory.Services.GetRequiredService<NewHeap.Platform.AI.Test.NhAiCapturedAuditSink>().Records
            .Where(record => record.Outcome == NhAiOutcomeKind.AuthorizationDenied)
            .Select(record => record.ToolId)
            .ToArray();
        Assert.Contains("test-api.order.get-by-id", denied);
        Assert.Contains("test-api.order.create", denied);
        Assert.DoesNotContain(
            factory.Logs.Entries,
            entry => entry.Contains("AI bridge tool test-api.order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_with_the_policies_sees_mutations_and_discovery_is_evaluated_per_request()
    {
        using var factory = new BridgeApiFactory();

        var viewerTools = await DiscoverAsync(factory, TestTokenHandler.Viewer);
        var managerTools = await DiscoverAsync(factory, TestTokenHandler.Manager);

        Assert.DoesNotContain("test-api.order.create", viewerTools);
        Assert.DoesNotContain("test-api.order.update", viewerTools);
        Assert.Contains("test-api.order.create", managerTools);
        Assert.Contains("test-api.order.update", managerTools);
        Assert.Equal(viewerTools.Length + 5, managerTools.Length);
    }

    [Fact]
    public async Task Non_bridge_descriptors_are_denied_without_an_inner_policy()
    {
        using var factory = new BridgeApiFactory(configureServices: services =>
            services.AddSingleton<INhAiToolCatalog, OtherToolCatalog>());

        var tools = await DiscoverAsync(factory, TestTokenHandler.Manager);

        Assert.DoesNotContain(tools, id => id.StartsWith("other.", StringComparison.Ordinal));
        Assert.Contains("test-api.order.get", tools);
    }

    [Fact]
    public async Task Inner_policy_decides_non_bridge_descriptors()
    {
        using var factory = new BridgeApiFactory(
            bridge =>
            {
                BridgeApiFactory.DefaultBridge(bridge);
                bridge.UseInnerDiscoveryPolicy<AllowListedDiscoveryPolicy>();
            },
            services => services.AddSingleton<INhAiToolCatalog, OtherToolCatalog>());

        var tools = await DiscoverAsync(factory, TestTokenHandler.Viewer);

        Assert.Contains("other.allowed", tools);
        Assert.DoesNotContain("other.denied", tools);
        Assert.Contains("test-api.order.get", tools);
    }

    [Fact]
    public void Replacing_the_discovery_policy_after_the_bridge_fails_at_startup()
    {
        using var factory = new BridgeApiFactory(configureServices: services =>
            services.AddNewHeapPlatformAI(ai => ai.UseDiscoveryPolicy<AllowListedDiscoveryPolicy>()));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("UseInnerDiscoveryPolicy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_rejects_a_context_bound_to_a_different_actor()
    {
        using var factory = new BridgeApiFactory();

        var tools = await factory.AsUserAsync(TestTokenHandler.Viewer, async services =>
            await services.GetRequiredService<INhAiToolDiscoveryService>().DiscoverAsync(
                new NhAiToolDiscoveryRequest(
                    new NhAiInvocationContext("someone-else", "assistant", new Dictionary<string, string>()),
                    NhAiToolExposure.Agent)));

        Assert.Empty(tools);
    }

    private static Task<string[]> DiscoverAsync(BridgeApiFactory factory, string token)
    {
        var actorId = token.Split(':')[1];
        return factory.AsUserAsync(token, async services =>
        {
            var descriptors = await services.GetRequiredService<INhAiToolDiscoveryService>().DiscoverAsync(
                new NhAiToolDiscoveryRequest(
                    new NhAiInvocationContext(actorId, "assistant", new Dictionary<string, string>()),
                    NhAiToolExposure.Agent));
            return descriptors.Select(descriptor => descriptor.Id).ToArray();
        });
    }

    public sealed class AllowListedDiscoveryPolicy : INhAiToolDiscoveryPolicy
    {
        public ValueTask<bool> CanDiscoverAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(descriptor.Id == "other.allowed");
        }
    }

    /// <summary>A second, generated-style catalog with read-only tools outside the bridge.</summary>
    public sealed class OtherToolCatalog : INhAiGeneratedToolCatalog
    {
        private static readonly NhAiToolDescriptor[] AllDescriptors =
        [
            CreateDescriptor("other.allowed"),
            CreateDescriptor("other.denied")
        ];

        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors => AllDescriptors;

        public NhAiToolCatalogManifest Manifest => new(
            "other",
            1,
            "other-hash",
            AllDescriptors
                .Select(descriptor => new NhAiToolManifestEntry(descriptor.Id, 1, "schema", descriptor.ContractHash)
                {
                    ExportName = descriptor.ExportName
                })
                .ToArray());

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            var invoker = services.GetRequiredService<INhAiToolInvoker>();
            return AllDescriptors
                .Select(descriptor =>
                {
                    Func<JsonElement, CancellationToken, Task<TaskResult<string>>> handler =
                        (input, cancellationToken) => invoker.InvokeAsync(
                            descriptor,
                            input,
                            (_, _) => Task.FromResult(TaskResult<string>.Succeeded("ok")),
                            cancellationToken);
                    return NhAiGovernedAIFunction.Create(
                        descriptor,
                        AIFunctionFactory.Create(handler, new AIFunctionFactoryOptions { Name = descriptor.ExportName }));
                })
                .ToArray();
        }

        private static NhAiToolDescriptor CreateDescriptor(string id)
        {
            return new NhAiToolDescriptor(
                id,
                1,
                "A curated read outside the bridge.",
                typeof(JsonElement),
                typeof(string),
                NhAiToolEffect.ReadOnly,
                NhAiToolExposure.Local | NhAiToolExposure.Agent,
                false,
                [])
            {
                ExportName = id.Replace('.', '_') + "_v1",
                CatalogId = "other",
                ContractHash = id + "-contract"
            };
        }
    }
}
