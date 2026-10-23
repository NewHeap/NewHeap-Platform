using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// A catalog whose descriptors change at runtime is exported to MCP clients from one attested view at
/// a time, never breaks the export of the other catalogs, and tells connected sessions that their
/// tool list changed.
/// </summary>
public sealed class NhAiChangingToolCatalogTests
{
    private static readonly NhAiInvocationContext Context = new(
        "actor-1",
        "tool-invocation",
        new Dictionary<string, string>());

    [Fact]
    public async Task A_changing_catalog_is_exported_from_its_current_view_and_every_session_hears_of_a_change()
    {
        var catalog = new SwitchingCatalog(View("runtime.echo", "runtime_echo_v1"));
        await using var provider = CreateProvider(catalog);
        await StartMcpValidatorAsync(provider);

        Assert.Equal(["runtime_echo_v1"], await ExportNamesAsync(provider));

        // A session of an MCP client learns that the server announces tool list changes. A client of
        // the 2025-11-25 revision hears every change on its session; a later client hears those it
        // listens for with subscriptions/listen, which the MCP SDK routes from the same change.
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            options,
            serviceProvider: provider);
        _ = server.RunAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            new McpClientOptions { ProtocolVersion = "2025-11-25" });
        Assert.True(client.ServerCapabilities.Tools?.ListChanged);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                changed.TrySetResult();
                return ValueTask.CompletedTask;
            });

        catalog.Switch(View("runtime.other", "runtime_other_v1"));

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["runtime_other_v1"], await ExportNamesAsync(provider));
    }

    [Fact]
    public async Task A_view_that_fails_its_attestation_or_takes_an_export_name_stays_out_while_the_export_goes_on()
    {
        var catalog = new SwitchingCatalog(View("runtime.echo", "runtime_echo_v1", governed: false));
        await using var provider = CreateProvider(catalog, withStaticCatalog: true);
        // Its data never stops startup.
        await StartMcpValidatorAsync(provider);

        Assert.Equal(["static_echo_v1"], await ExportNamesAsync(provider));

        // A runtime tool never takes the export name of a tool that exists since startup.
        catalog.Switch(View("runtime.static-twin", "static_echo_v1"));
        Assert.Equal(["static_echo_v1"], await ExportNamesAsync(provider));

        catalog.Switch(View("runtime.echo", "runtime_echo_v1"));
        Assert.Equal(["runtime_echo_v1", "static_echo_v1"], (await ExportNamesAsync(provider)).Order(StringComparer.Ordinal));
    }

    private static ServiceProvider CreateProvider(SwitchingCatalog catalog, bool withStaticCatalog = false)
    {
        var services = new ServiceCollection();
        services.AddScoped<INhAiToolInvocationGate>(_ => NhAiTestInvocationGate.Authorized(Context));
        services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
        services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
        services.AddNewHeapPlatformAI();
        services.AddSingleton(catalog);
        services.AddSingleton<INhAiToolCatalog>(catalog);
        services.AddSingleton<INhAiChangingToolCatalog>(catalog);
        if (withStaticCatalog)
        {
            services.AddSingleton<INhAiToolCatalog>(View("static.echo", "static_echo_v1", catalogId: "static"));
        }

        services.AddMcpServer(options => options.ScopeRequests = false)
            .WithNewHeapPlatformAITools();
        return services.BuildServiceProvider();
    }

    private static async Task<IReadOnlyList<string>> ExportNamesAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var tools = await scope.ServiceProvider
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(scope.ServiceProvider, Context);
        return tools.Select(tool => tool.ProtocolTool.Name).ToArray();
    }

    private static async Task StartMcpValidatorAsync(IServiceProvider provider)
    {
        var validator = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name.Contains(
                "NhAiMcpAuthorityStartupValidator",
                StringComparison.Ordinal));
        await validator.StartAsync(CancellationToken.None);
    }

    private static EchoView View(string id, string exportName, bool governed = true, string catalogId = "runtime")
    {
        var descriptor = new NhAiToolDescriptor(
            id,
            1,
            "Echo the input through the shared invoker.",
            typeof(string),
            typeof(string),
            NhAiToolEffect.ReadOnly,
            NhAiToolExposure.Local | NhAiToolExposure.Mcp,
            false,
            [])
        {
            ExportName = exportName,
            CatalogId = catalogId,
            ContractHash = Hash(id + "-contract")
        };
        return new EchoView(descriptor, governed, catalogId);
    }

    private static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// A catalog that changes from one view to the next when the test switches it.
    /// </summary>
    private sealed class SwitchingCatalog(EchoView first) : INhAiChangingToolCatalog
    {
        private EchoView _current = first;

        public event EventHandler? Changed;

        public INhAiAttestedToolCatalog Current => Volatile.Read(ref _current);

        public bool MayExportToMcp => true;

        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors => Current.Descriptors;

        public NhAiToolCatalogManifest Manifest => Current.Manifest;

        public string AttestationHash => Current.AttestationHash;

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            return Current.CreateFunctions(services);
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public void Switch(EchoView view)
        {
            Volatile.Write(ref _current, view);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// One immutable view with one echo tool; an ungoverned view fails its attestation.
    /// </summary>
    private sealed class EchoView(NhAiToolDescriptor descriptor, bool governed, string catalogId) : INhAiAttestedToolCatalog
    {
        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors => [descriptor];

        public string AttestationHash => Hash(descriptor.Id + "@1:" + descriptor.ContractHash);

        public NhAiToolCatalogManifest Manifest => new(
            catalogId,
            1,
            AttestationHash,
            [
                new NhAiToolManifestEntry(descriptor.Id, descriptor.Version, descriptor.SchemaHash, descriptor.ContractHash)
                {
                    ExportName = descriptor.ExportName
                }
            ]);

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            var invoker = services.GetRequiredService<INhAiToolInvoker>();
            Func<string, CancellationToken, Task<TaskResult<string>>> handler =
                (input, cancellationToken) => invoker.InvokeAsync(
                    descriptor,
                    input,
                    (_, _) => Task.FromResult(TaskResult<string>.Succeeded("echo:" + input)),
                    cancellationToken);
            var function = AIFunctionFactory.Create(handler, new AIFunctionFactoryOptions
            {
                Name = descriptor.ExportName,
                Description = descriptor.Description
            });
            return [governed ? NhAiGovernedAIFunction.Create(descriptor, function) : function];
        }
    }
}
