using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A catalog that changes at runtime, exported through <c>WithNewHeapPlatformAITools</c> over real
/// Streamable HTTP on loopback with the MCP SDK's HTTP client (AC26). A session of a stateful server
/// hears <c>notifications/tools/list_changed</c> on its standalone GET stream; a stateless server, the
/// sample's configuration and the only one the 2026-07-28 revision has over HTTP, does not declare
/// <c>listChanged</c> and grants no tool list changes on <c>subscriptions/listen</c>, so its clients see a
/// change on their next <c>tools/list</c>. On both, a call of a tool from a stale list fails with a safe
/// code and runs nothing.
/// </summary>
/// <remarks>
/// The tests live here rather than in NewHeap.Platform.AI.Tests because this project already references
/// the MCP SDK's ASP.NET Core transport and its loopback server helper (AC53).
/// </remarks>
public sealed class NhAiMcpHttpToolListChangeTests(NhAiMcpHttpToolListChangeTests.Hosts hosts)
    : IClassFixture<NhAiMcpHttpToolListChangeTests.Hosts>
{
    private const string FirstTool = "runtime_echo_v1";
    private const string SecondTool = "runtime_other_v1";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_session_of_a_stateful_server_hears_over_streamable_http_that_the_catalog_changed_and_a_stale_call_runs_nothing()
    {
        var host = hosts.Stateful;
        // A client of the initialize handshake (2025-11-25) gets a session and its standalone GET stream.
        await using var client = await ConnectAsync(host, "2025-11-25");
        Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
        Assert.False(string.IsNullOrEmpty(client.SessionId));
        Assert.True(client.ServerCapabilities.Tools?.ListChanged);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                changed.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var stale = await ListAndCallAsync(client, host);

        host.Catalog.Switch(EchoView.Create("runtime.other", SecondTool, host.Runs));

        await changed.Task.WaitAsync(SignalTimeout);
        Assert.Equal([SecondTool], (await client.ListToolsAsync()).Select(tool => tool.Name));
        await AssertStaleCallRunsNothingAsync(stale, host);
    }

    [Fact]
    public async Task A_stateless_server_cannot_tell_its_clients_and_their_next_listing_shows_the_change_while_a_stale_call_runs_nothing()
    {
        var host = hosts.Stateless;
        // A client that asks for no revision speaks 2026-07-28, which has no sessions over HTTP.
        await using var client = await ConnectAsync(host, null);
        Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
        Assert.True(string.IsNullOrEmpty(client.SessionId));
        // The SDK drops listChanged where the transport cannot deliver it.
        Assert.NotEqual(true, client.ServerCapabilities.Tools?.ListChanged);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<SubscriptionsAcknowledgedNotificationParams?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var changes = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                changed.TrySetResult();
                return ValueTask.CompletedTask;
            });
        await using var acknowledgements = client.RegisterNotificationHandler(
            NotificationMethods.SubscriptionsAcknowledgedNotification,
            (notification, _) =>
            {
                acknowledged.TrySetResult(notification.Params.Deserialize<SubscriptionsAcknowledgedNotificationParams>(
                    McpJsonUtilities.DefaultOptions));
                return ValueTask.CompletedTask;
            });

        // The client asks to hear of tool list changes on subscriptions/listen. The server's built-in
        // handler acknowledges the request without granting them and ends the stream, so no channel
        // is left that could carry a change.
        var listen = client.SendRequestAsync<SubscriptionsListenRequestParams, EmptyResult>(
            RequestMethods.SubscriptionsListen,
            new SubscriptionsListenRequestParams
            {
                Notifications = new SubscriptionsListenNotifications { ToolsListChanged = true }
            },
            McpJsonUtilities.DefaultOptions).AsTask();
        var granted = await acknowledged.Task.WaitAsync(SignalTimeout);
        Assert.NotNull(granted);
        Assert.NotEqual(true, granted.Notifications?.ToolsListChanged);
        await listen.WaitAsync(SignalTimeout);

        var stale = await ListAndCallAsync(client, host);

        host.Catalog.Switch(EchoView.Create("runtime.other", SecondTool, host.Runs));

        // Every listing reads the catalog's current view, so the next one shows the change.
        Assert.Equal([SecondTool], (await client.ListToolsAsync()).Select(tool => tool.Name));
        Assert.False(changed.Task.IsCompleted);
        await AssertStaleCallRunsNothingAsync(stale, host);
    }

    private static async Task<McpClient> ConnectAsync(Host host, string? protocolVersion)
    {
        return await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.Endpoint),
                TransportMode = HttpTransportMode.StreamableHttp
            }),
            new McpClientOptions { ProtocolVersion = protocolVersion });
    }

    /// <summary>
    /// Lists the catalog's first view and calls its tool once, so the tool's handler has run once; returns
    /// the tool of that listing.
    /// </summary>
    private static async Task<McpClientTool> ListAndCallAsync(McpClient client, Host host)
    {
        var tool = Assert.Single(await client.ListToolsAsync());
        Assert.Equal(FirstTool, tool.Name);

        var result = await tool.CallAsync(new Dictionary<string, object?> { ["input"] = "first" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, host.Runs.Count);
        return tool;
    }

    private static async Task AssertStaleCallRunsNothingAsync(McpClientTool stale, Host host)
    {
        var result = await stale.CallAsync(new Dictionary<string, object?> { ["input"] = "stale" });

        Assert.True(result.IsError);
        Assert.Equal("ai-tool-not-authorized", result.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Equal("ai-tool-not-authorized", result.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());
        Assert.Equal(1, host.Runs.Count);
    }

    /// <summary>
    /// Two loopback servers of the same application, one stateful and one stateless, each with a
    /// catalog of its own, started once for the class.
    /// </summary>
    public sealed class Hosts : IAsyncLifetime
    {
        private Host? _stateful;
        private Host? _stateless;

        internal Host Stateful => _stateful ?? throw new InvalidOperationException("The hosts did not start.");

        internal Host Stateless => _stateless ?? throw new InvalidOperationException("The hosts did not start.");

        public async Task InitializeAsync()
        {
            var stateful = Host.StartAsync(HttpServerSessionMode.Stateful);
            var stateless = Host.StartAsync(HttpServerSessionMode.Stateless);
            _stateful = await stateful;
            _stateless = await stateless;
        }

        public async Task DisposeAsync()
        {
            if (_stateful is not null)
            {
                await _stateful.DisposeAsync();
            }
            if (_stateless is not null)
            {
                await _stateless.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// An MCP server of <c>WithNewHeapPlatformAITools</c> over Streamable HTTP at <c>/mcp</c>, whose
    /// requests are signed in as one person, with a catalog that changes when the test switches it.
    /// </summary>
    internal sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app, SwitchingCatalog catalog, RunCounter runs)
        {
            _app = app;
            Catalog = catalog;
            Runs = runs;
            Endpoint = LocalhostTestServer.Origin(app) + "/mcp";
        }

        public SwitchingCatalog Catalog { get; }

        public RunCounter Runs { get; }

        public string Endpoint { get; }

        public static async Task<Host> StartAsync(HttpServerSessionMode sessionMode)
        {
            var runs = new RunCounter();
            var catalog = new SwitchingCatalog(EchoView.Create("runtime.echo", FirstTool, runs));
            var app = await LocalhostTestServer.StartAsync(port =>
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls($"http://localhost:{port}");
                builder.Logging.ClearProviders();
                var services = builder.Services;
                services.AddAuthorization();
                services.AddSingleton<INhAiToolCatalog>(catalog);
                services.AddSingleton<INhAiChangingToolCatalog>(catalog);
                services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
                services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
                services.AddNewHeapPlatformAIAspNet(ai => ai.UseToolInvocationPurpose("tool-invocation"));
                services.AddMcpServer()
                    .WithHttpTransport(options => options.SessionMode = sessionMode)
                    .WithNewHeapPlatformAITools();

                var server = builder.Build();
                server.Use((context, next) =>
                {
                    // Every request is the same signed-in person, as an authentication handler would
                    // establish it.
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "actor-1")],
                        "test"));
                    return next(context);
                });
                server.MapMcp("/mcp");
                return server;
            });
            return new Host(app, catalog, runs);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Counts the runs of the tools' own work, behind the shared invoker.
    /// </summary>
    internal sealed class RunCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Ran()
        {
            Interlocked.Increment(ref _count);
        }
    }

    /// <summary>
    /// A catalog that changes from one view to the next when the test switches it.
    /// </summary>
    internal sealed class SwitchingCatalog(EchoView first) : INhAiChangingToolCatalog
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
    /// One immutable, attested view with one echo tool exported to MCP.
    /// </summary>
    internal sealed class EchoView : INhAiAttestedToolCatalog
    {
        private readonly NhAiToolDescriptor _descriptor;
        private readonly RunCounter _runs;

        private EchoView(NhAiToolDescriptor descriptor, RunCounter runs)
        {
            _descriptor = descriptor;
            _runs = runs;
        }

        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors => [_descriptor];

        public string AttestationHash => Hash(_descriptor.Id + "@1:" + _descriptor.ContractHash);

        public NhAiToolCatalogManifest Manifest => new(
            "runtime",
            1,
            AttestationHash,
            [
                new NhAiToolManifestEntry(_descriptor.Id, _descriptor.Version, _descriptor.SchemaHash, _descriptor.ContractHash)
                {
                    ExportName = _descriptor.ExportName
                }
            ]);

        public static EchoView Create(string id, string exportName, RunCounter runs)
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
                CatalogId = "runtime",
                ContractHash = Hash(id + "-contract")
            };
            return new EchoView(descriptor, runs);
        }

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            var invoker = services.GetRequiredService<INhAiToolInvoker>();
            var descriptor = _descriptor;
            var runs = _runs;
            Func<string, CancellationToken, Task<TaskResult<string>>> handler =
                (input, cancellationToken) => invoker.InvokeAsync(
                    descriptor,
                    input,
                    (_, _) =>
                    {
                        runs.Ran();
                        return Task.FromResult(TaskResult<string>.Succeeded("echo:" + input));
                    },
                    cancellationToken);
            var function = AIFunctionFactory.Create(handler, new AIFunctionFactoryOptions
            {
                Name = descriptor.ExportName,
                Description = descriptor.Description
            });
            return [NhAiGovernedAIFunction.Create(descriptor, function)];
        }

        private static string Hash(string value)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }
}
