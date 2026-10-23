using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
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
/// A failed result of <c>WithNewHeapPlatformAITools</c> over real Streamable HTTP on loopback carries
/// the format and arguments of its message in <c>_meta</c> next to the unchanged text, so a client can
/// translate it; a refusal of the export carries only its fixed text as its format, whatever the
/// application's resolver reported.
/// </summary>
/// <remarks>
/// The tests live here rather than in NewHeap.Platform.AI.Tests because this project already references
/// the MCP SDK's ASP.NET Core transport and its loopback server helper.
/// </remarks>
public sealed class NhAiMcpHttpMessageFormatTests(NhAiMcpHttpMessageFormatTests.Host host)
    : IClassFixture<NhAiMcpHttpMessageFormatTests.Host>
{
    private const string ToolName = "projects_close_v1";
    private const string ActorHeader = "X-Test-Actor";
    private const string ExpiredPrefix = "expired:";

    [Fact]
    public async Task A_tool_failure_over_streamable_http_carries_its_message_format_and_arguments_next_to_the_unchanged_text()
    {
        await using var client = await host.ConnectAsync("actor-1");
        var runs = host.Runs;

        var result = await client.CallToolAsync(
            ToolName,
            new Dictionary<string, object?> { ["input"] = "Roadmap" });

        const string message = "Project 'Roadmap' was closed at 10/01/2026 09:30:00 +00:00 with 3 open tasks.";
        Assert.True(result.IsError);
        Assert.Equal(
            "project-closed: " + message,
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(message, result.StructuredContent!.Value.GetProperty("message").GetString());
        Assert.Equal(message, result.Meta![NhAiMcpResultMetadata.MessageKey]!.GetValue<string>());
        Assert.Equal(
            "Project '{0}' was closed at {1} with {2} open tasks.",
            result.Meta[NhAiMcpResultMetadata.MessageFormatKey]!.GetValue<string>());
        var arguments = result.Meta[NhAiMcpResultMetadata.MessageArgumentsKey]!.AsArray();
        Assert.Equal(3, arguments.Count);
        Assert.Equal("Roadmap", arguments[0]!.GetValue<string>());
        Assert.Equal("2026-10-01T09:30:00+00:00", arguments[1]!.GetValue<string>());
        Assert.Equal(3, arguments[2]!.GetValue<int>());

        var exception = await Assert.ThrowsAsync<NhAiMcpToolException>(async () =>
            await client.CallNewHeapToolAsync<string, string>(ToolName, "Roadmap"));
        Assert.Equal("project-closed", exception.Code);
        Assert.Equal(message, exception.FailureMessage);
        Assert.Equal("Project '{0}' was closed at {1} with {2} open tasks.", exception.FailureMessageFormat);
        Assert.Collection(
            exception.FailureMessageArguments,
            name => Assert.Equal("Roadmap", name.GetString()),
            closedAt => Assert.Equal("2026-10-01T09:30:00+00:00", closedAt.GetString()),
            openTasks => Assert.Equal(3, openTasks.GetInt32()));
        Assert.Equal(runs + 2, host.Runs);
    }

    [Fact]
    public async Task A_rejected_context_publishes_only_the_fixed_text_of_the_export_as_its_format()
    {
        await using var client = await host.ConnectAsync(ExpiredPrefix + "actor-7");
        var runs = host.Runs;

        var result = await client.CallToolAsync(
            ToolName,
            new Dictionary<string, object?> { ["input"] = "Roadmap" });

        // The resolver failed with a message of its own, with the actor and a time as arguments; the
        // export shields it and publishes its own fixed text, without arguments.
        Assert.True(result.IsError);
        Assert.Equal(
            "test-session-expired: AI tool invocation context was rejected.",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(
            "AI tool invocation context was rejected.",
            result.Meta![NhAiMcpResultMetadata.MessageFormatKey]!.GetValue<string>());
        Assert.Empty(result.Meta[NhAiMcpResultMetadata.MessageArgumentsKey]!.AsArray());
        var published = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        Assert.DoesNotContain("actor-7", published, StringComparison.Ordinal);
        Assert.DoesNotContain("expired at", published, StringComparison.Ordinal);
        Assert.DoesNotContain("10/23/2026", published, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-23", published, StringComparison.Ordinal);
        Assert.Equal(runs, host.Runs);
    }

    /// <summary>
    /// An MCP server of <c>WithNewHeapPlatformAITools</c> over stateless Streamable HTTP at <c>/mcp</c>
    /// whose resolver reads the actor from a header, with one tool that fails with a formatted message,
    /// started once for the class.
    /// </summary>
    public sealed class Host : IAsyncLifetime
    {
        private WebApplication? _app;
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        private string Endpoint => LocalhostTestServer.Origin(
            _app ?? throw new InvalidOperationException("The host did not start.")) + "/mcp";

        public async Task InitializeAsync()
        {
            _app = await LocalhostTestServer.StartAsync(port =>
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls($"http://localhost:{port}");
                builder.Logging.ClearProviders();
                var services = builder.Services;
                services.AddAuthorization();
                services.AddSingleton<INhAiToolCatalog>(new ClosingCatalog(() => Interlocked.Increment(ref _runs)));
                services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
                services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
                services.AddNewHeapPlatformAIAspNet(ai => ai
                    .UseToolInvocationPurpose("tool-invocation")
                    .UseAuthenticatedInvocationContextResolver<HeaderResolver>());
                // The SDK's default: stateless Streamable HTTP.
                services.AddMcpServer()
                    .WithHttpTransport()
                    .WithNewHeapPlatformAITools();

                var server = builder.Build();
                server.MapMcp("/mcp");
                return server;
            });
        }

        public async Task<McpClient> ConnectAsync(string actor)
        {
            return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Endpoint),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { [ActorHeader] = actor }
            }));
        }

        public async Task DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Reads the actor from a header. An actor that starts with <c>expired:</c> is refused with a message
    /// whose arguments the export must never publish.
    /// </summary>
    internal sealed class HeaderResolver : INhAiAuthenticatedInvocationContextResolver
    {
        public ValueTask<TaskResult<NhAiInvocationContext>> ResolveAsync(
            HttpContext httpContext,
            CancellationToken cancellationToken = default)
        {
            var actor = httpContext.Request.Headers[ActorHeader].ToString();
            if (actor.StartsWith(ExpiredPrefix, StringComparison.Ordinal))
            {
                var expiredAt = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
                FormattableString expired = $"The session of {actor} expired at {expiredAt}.";
                return ValueTask.FromResult(
                    TaskResult<NhAiInvocationContext>.Failed("test-session-expired", expired));
            }

            return ValueTask.FromResult(TaskResult<NhAiInvocationContext>.Succeeded(
                new NhAiInvocationContext(actor, "tool-invocation", new Dictionary<string, string>())));
        }
    }

    /// <summary>
    /// One attested catalog with one read tool exported to MCP, which fails with a formatted message
    /// through the shared invoker.
    /// </summary>
    internal sealed class ClosingCatalog : INhAiAttestedToolCatalog
    {
        private static readonly DateTimeOffset ClosedAt = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);
        private readonly NhAiToolDescriptor _descriptor;
        private readonly Action _ran;

        public ClosingCatalog(Action ran)
        {
            _ran = ran;
            _descriptor = new NhAiToolDescriptor(
                "projects.close",
                1,
                "Close a project and report why it cannot be closed.",
                typeof(string),
                typeof(string),
                NhAiToolEffect.ReadOnly,
                NhAiToolExposure.Mcp,
                false,
                [])
            {
                ExportName = ToolName,
                CatalogId = "closing",
                ContractHash = Hash("projects.close-contract")
            };
        }

        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors => [_descriptor];

        public string AttestationHash => Hash(_descriptor.Id + "@1:" + _descriptor.ContractHash);

        public NhAiToolCatalogManifest Manifest => new(
            "closing",
            1,
            AttestationHash,
            [
                new NhAiToolManifestEntry(_descriptor.Id, _descriptor.Version, _descriptor.SchemaHash, _descriptor.ContractHash)
                {
                    ExportName = _descriptor.ExportName
                }
            ]);

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            var invoker = services.GetRequiredService<INhAiToolInvoker>();
            var descriptor = _descriptor;
            var ran = _ran;
            Func<string, CancellationToken, Task<TaskResult<string>>> handler =
                (input, cancellationToken) => invoker.InvokeAsync(
                    descriptor,
                    input,
                    (_, _) =>
                    {
                        ran();
                        var openTasks = 3;
                        FormattableString closed =
                            $"Project '{input}' was closed at {ClosedAt} with {openTasks} open tasks.";
                        return Task.FromResult(TaskResult<string>.Failed("project-closed", closed));
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
