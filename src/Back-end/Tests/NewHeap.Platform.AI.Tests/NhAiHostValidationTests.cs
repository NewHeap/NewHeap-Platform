using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// Builds and starts real hosts in the Development environment, where the container validates
/// scopes and every registration on build, as consuming applications do when they run locally.
/// </summary>
public sealed class NhAiHostValidationTests
{
    private static readonly NhAiInvocationContext Context = new(
        "actor-1",
        "host-validation",
        new Dictionary<string, string>())
    {
        CapabilityGrants = new HashSet<string>(StringComparer.Ordinal)
        {
            "tenant-a-inspect",
            "tenant-b-inspect"
        }
    };

    [Fact]
    public async Task Structured_output_host_without_an_invocation_gate_builds_and_starts()
    {
        var builder = CreateDevelopmentHostBuilder();
        builder.Services.AddKeyedSingleton<IChatClient>(
            "worker-model",
            new NhAiDeterministicChatClient("{}"));
        builder.Services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile("worker", profile => profile
                .UseKeyedClient("worker-model")
                .RequireCapabilities(NhAiModelCapability.StructuredOutput))
            .UseInMemoryBudget());
        using var host = builder.Build();

        await host.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INhAiChatExecutor>());
        var exception = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<INhAiToolInvoker>());
        Assert.Contains("requires an INhAiToolInvocationGate", exception.Message, StringComparison.Ordinal);
        await host.StopAsync();
    }

    [Fact]
    public async Task Invoker_uses_the_registered_invocation_gate()
    {
        var builder = CreateDevelopmentHostBuilder();
        builder.Services.AddScoped<INhAiToolInvocationGate>(
            _ => NhAiTestInvocationGate.Authorized(Context));
        builder.Services.AddNewHeapPlatformAI(ai => ai.UseInMemoryBudget());
        using var host = builder.Build();

        await host.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();

        Assert.IsType<NhAiToolInvoker>(scope.ServiceProvider.GetRequiredService<INhAiToolInvoker>());
        await host.StopAsync();
    }

    [Fact]
    public async Task Run_scoped_catalog_is_validated_in_its_own_scope_and_released_after_startup()
    {
        var builder = CreateDevelopmentHostBuilder();
        builder.Services.AddNewHeapPlatformAI(ai => ai.UseInMemoryBudget());
        builder.Services.AddSingleton<RunCatalogLifetime>();
        builder.Services.AddScoped<INhAiToolCatalog, RunScopedAgentCatalog>();
        using var host = builder.Build();

        await host.StartAsync();

        var lifetime = host.Services.GetRequiredService<RunCatalogLifetime>();
        Assert.Equal(1, lifetime.Created);
        Assert.Equal(1, lifetime.Disposed);
        await host.StopAsync();
    }

    [Fact]
    public async Task Run_scoped_catalog_descriptors_are_still_validated_at_startup()
    {
        var builder = CreateDevelopmentHostBuilder();
        builder.Services.AddNewHeapPlatformAI(ai => ai.UseInMemoryBudget());
        builder.Services.AddScoped<INhAiToolCatalog, NhAiFoundationTests.ProtectedMutationCatalog>();
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());

        Assert.Contains("requires a configured idempotency manager", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_host_starts_with_a_run_scoped_agent_catalog_and_exports_only_mcp_tools()
    {
        var builder = CreateDevelopmentWebApplicationBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<AuthenticatedMcpTools>();
        builder.Services.AddScoped<INhAiToolDiscoveryPolicy>(
            _ => NhAiTestDiscoveryPolicy.Allowed());
        builder.Services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UseToolInvocationPurpose("host-validation"));
        builder.Services.AddNewHeapPlatformAI(ai => ai
            .AddGeneratedToolCatalog<AuthenticatedMcpToolsNhAiCatalog>()
            .UseInMemoryBudget());
        builder.Services.AddSingleton<RunCatalogLifetime>();
        builder.Services.AddScoped<INhAiToolCatalog, RunScopedAgentCatalog>();
        builder.Services.AddMcpServer().WithNewHeapPlatformAITools();
        await using var app = builder.Build();

        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var tools = await scope.ServiceProvider
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(scope.ServiceProvider, Context);

        Assert.Equal(
            ["tenant-a.inspect", "tenant-b.inspect"],
            tools.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal));
        await app.StopAsync();
    }

    [Fact]
    public async Task Mcp_host_still_rejects_an_unattested_catalog_with_mcp_exposure()
    {
        var builder = CreateDevelopmentWebApplicationBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UseToolInvocationPurpose("host-validation"));
        builder.Services.AddNewHeapPlatformAI(ai => ai.UseInMemoryBudget());
        builder.Services.AddScoped<INhAiToolCatalog, UnattestedMcpCatalog>();
        builder.Services.AddMcpServer().WithNewHeapPlatformAITools();
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.StartAsync());

        Assert.Contains("is not a generated or attested catalog", exception.Message, StringComparison.Ordinal);
    }

    private static HostApplicationBuilder CreateDevelopmentHostBuilder()
    {
        return Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development
        });
    }

    private static WebApplicationBuilder CreateDevelopmentWebApplicationBuilder()
    {
        return WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
    }

    private static NhAiToolDescriptor CreateDescriptor(string id, NhAiToolExposure exposure)
    {
        return new NhAiToolDescriptor(
            id,
            1,
            "Look up a record through a run-specific integration.",
            typeof(string),
            typeof(string),
            NhAiToolEffect.ReadOnly,
            exposure,
            true,
            []);
    }

    private sealed class RunCatalogLifetime
    {
        public int Created { get; set; }

        public int Disposed { get; set; }
    }

    /// <summary>
    /// Stands in for a catalog whose tools depend on the current agent run, such as an imported
    /// MCP catalog. It is neither generated nor attested and only serves agents.
    /// </summary>
    private sealed class RunScopedAgentCatalog : INhAiToolCatalog, IDisposable
    {
        private readonly RunCatalogLifetime _lifetime;

        public RunScopedAgentCatalog(RunCatalogLifetime lifetime)
        {
            _lifetime = lifetime;
            _lifetime.Created++;
        }

        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } =
            [CreateDescriptor("mcp.crm.lookup", NhAiToolExposure.Agent)];

        public NhAiToolCatalogManifest Manifest { get; } = new("mcp-crm", 1, "run-hash", []);

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            throw new InvalidOperationException("The MCP export path must not create agent-only functions.");
        }

        public void Dispose()
        {
            _lifetime.Disposed++;
        }
    }

    private sealed class UnattestedMcpCatalog : INhAiToolCatalog
    {
        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } =
            [CreateDescriptor("records.lookup", NhAiToolExposure.Mcp)];

        public NhAiToolCatalogManifest Manifest { get; } = new("unattested-records", 1, "hash", []);

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            return [];
        }
    }
}
