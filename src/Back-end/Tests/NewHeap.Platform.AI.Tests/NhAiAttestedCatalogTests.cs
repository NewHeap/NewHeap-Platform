using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiAttestedCatalogTests
{
    private static readonly NhAiInvocationContext Context = new(
        "actor-1",
        "tool-invocation",
        new Dictionary<string, string>());

    [Fact]
    public async Task Attested_catalog_with_governed_functions_is_exported_through_mcp()
    {
        await using var provider = CreateProvider<ValidAttestedCatalog>();
        await StartMcpValidatorAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var mcpTools = await scope.ServiceProvider
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(scope.ServiceProvider, Context);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ScopeRequests = false,
                ToolCollection = [.. mcpTools]
            },
            serviceProvider: scope.ServiceProvider);
        _ = server.RunAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream()));

        var tool = Assert.Single(await client.ListToolsAsync());
        Assert.Equal("runtime_echo_v1", tool.Name);
        var result = await client.CallNewHeapToolAsync<string, string>(tool.Name, "value");
        Assert.Equal("echo:value", result);
    }

    [Fact]
    public async Task Attested_catalog_with_an_ungoverned_function_fails_at_startup()
    {
        await using var provider = CreateProvider<UngovernedAttestedCatalog>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await StartMcpValidatorAsync(provider));

        Assert.Contains("ungoverned function", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attested_catalog_with_a_mismatched_descriptor_fails_at_startup()
    {
        await using var provider = CreateProvider<MismatchedAttestedCatalog>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await StartMcpValidatorAsync(provider));

        Assert.Contains("not bound to one of its descriptors", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attested_catalog_with_a_wrong_hash_fails_at_startup()
    {
        await using var provider = CreateProvider<WrongHashAttestedCatalog>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await StartMcpValidatorAsync(provider));

        Assert.Contains("attestation hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attested_catalog_that_is_not_governed_by_the_shared_invoker_is_rejected()
    {
        await using var provider = CreateProvider<ValidAttestedCatalog>();
        await using var scope = provider.CreateAsyncScope();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NhAiToolCatalogAttestation.Validate(new UnmanagedAttestedCatalog(), scope.ServiceProvider));

        Assert.Contains("is not governed by INhAiToolInvoker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generated_catalog_keeps_passing_validation_and_the_mcp_startup_check()
    {
        var services = new ServiceCollection();
        services.AddScoped<AuthenticatedMcpTools>();
        services.AddScoped<INhAiToolInvocationGate>(_ => NhAiTestInvocationGate.Authorized(Context));
        services.AddNewHeapPlatformAI(ai =>
            ai.AddGeneratedToolCatalog<AuthenticatedMcpToolsNhAiCatalog>());
        services.AddMcpServer().WithNewHeapPlatformAITools();
        await using var provider = services.BuildServiceProvider();

        await StartMcpValidatorAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        NhAiToolCatalogAttestation.Validate(
            scope.ServiceProvider.GetServices<INhAiToolCatalog>().Single(),
            scope.ServiceProvider);
    }

    private static ServiceProvider CreateProvider<TCatalog>()
        where TCatalog : class, INhAiToolCatalog
    {
        var services = new ServiceCollection();
        services.AddScoped<INhAiToolInvocationGate>(_ => NhAiTestInvocationGate.Authorized(Context));
        services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
        services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TCatalog>());
        services.AddMcpServer(options => options.ScopeRequests = false)
            .WithNewHeapPlatformAITools();
        return services.BuildServiceProvider();
    }

    private static async Task StartMcpValidatorAsync(IServiceProvider provider)
    {
        var validator = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name.Contains(
                "NhAiMcpAuthorityStartupValidator",
                StringComparison.Ordinal));
        await validator.StartAsync(CancellationToken.None);
    }

    private abstract class RuntimeCatalog : INhAiAttestedToolCatalog
    {
        protected static readonly NhAiToolDescriptor EchoDescriptor = new(
            "runtime.echo",
            1,
            "Echo the input through the shared invoker.",
            typeof(string),
            typeof(string),
            NhAiToolEffect.ReadOnly,
            NhAiToolExposure.Local | NhAiToolExposure.Mcp,
            false,
            [])
        {
            ExportName = "runtime_echo_v1",
            CatalogId = "runtime",
            ContractHash = Hash("runtime.echo-contract")
        };

        public virtual NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors => [EchoDescriptor];

        public virtual string AttestationHash => Hash("runtime.echo@1:" + EchoDescriptor.ContractHash);

        public NhAiToolCatalogManifest Manifest => new(
            "runtime",
            1,
            AttestationHash,
            [
                new NhAiToolManifestEntry(
                    EchoDescriptor.Id,
                    EchoDescriptor.Version,
                    EchoDescriptor.SchemaHash,
                    EchoDescriptor.ContractHash)
                {
                    ExportName = EchoDescriptor.ExportName
                }
            ]);

        public abstract IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services);

        protected static AIFunction CreateEchoFunction(IServiceProvider services, NhAiToolDescriptor descriptor)
        {
            var invoker = services.GetRequiredService<INhAiToolInvoker>();
            Func<string, CancellationToken, Task<TaskResult<string>>> handler =
                (input, cancellationToken) => invoker.InvokeAsync(
                    descriptor,
                    input,
                    (_, _) => Task.FromResult(TaskResult<string>.Succeeded("echo:" + input)),
                    cancellationToken);
            return AIFunctionFactory.Create(handler, new AIFunctionFactoryOptions
            {
                Name = EchoDescriptor.ExportName,
                Description = EchoDescriptor.Description
            });
        }

        protected static string Hash(string value)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }

    private sealed class ValidAttestedCatalog : RuntimeCatalog
    {
        public override IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            return [NhAiGovernedAIFunction.Create(EchoDescriptor, CreateEchoFunction(services, EchoDescriptor))];
        }
    }

    private sealed class UngovernedAttestedCatalog : RuntimeCatalog
    {
        public override IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            return [CreateEchoFunction(services, EchoDescriptor)];
        }
    }

    private sealed class MismatchedAttestedCatalog : RuntimeCatalog
    {
        public override IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            var foreign = EchoDescriptor with { Id = "runtime.other" };
            return [NhAiGovernedAIFunction.Create(foreign, CreateEchoFunction(services, foreign))];
        }
    }

    private sealed class WrongHashAttestedCatalog : RuntimeCatalog
    {
        public override string AttestationHash => Hash("unrelated");

        public override IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            return [NhAiGovernedAIFunction.Create(EchoDescriptor, CreateEchoFunction(services, EchoDescriptor))];
        }
    }

    private sealed class UnmanagedAttestedCatalog : RuntimeCatalog
    {
        public override NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.None;

        public override IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            return [];
        }
    }
}
