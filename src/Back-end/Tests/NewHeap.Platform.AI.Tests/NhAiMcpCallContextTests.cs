using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// The governed call context of an imported MCP tool reaches the client transport only while the
/// remote call runs, and an import that allows agent grants says so in its descriptor and contract.
/// </summary>
public sealed class NhAiMcpCallContextTests
{
    private static readonly NhAiInvocationContext AgentContext = new(
        "agent:planner",
        "assistant",
        new Dictionary<string, string>())
    {
        ActorKind = NhAiActorKind.Agent,
        AccountableOwnerId = "agent:planner",
        GrantSource = NhAiGrantSource.Agent
    };

    [Fact]
    public async Task The_call_context_is_set_only_while_the_remote_call_is_sent()
    {
        await using var probe = await CallProbe.StartAsync(allowAgentGrant: false);
        Assert.Null(NhAiMcpCallContext.Current);
        var writesBeforeCall = probe.Writes.Count;

        var result = Assert.IsType<JsonElement>(await probe.Function.InvokeAsync(new AIFunctionArguments { ["order"] = "A-100" }));

        Assert.True(result.GetProperty(NhAiToolResult.SuccessPropertyName).GetBoolean());
        Assert.Null(NhAiMcpCallContext.Current);
        Assert.All(probe.Writes.Take(writesBeforeCall), write => Assert.Null(write));
        var during = probe.Writes.Skip(writesBeforeCall).OfType<CallSnapshot>().ToArray();
        Assert.NotEmpty(during);
        Assert.All(during, write =>
        {
            Assert.Equal("planning", write.ServerId);
            Assert.Equal("lookup_order", write.RemoteName);
            Assert.Equal("mcp.planning.lookup-order", write.ToolId);
            Assert.Equal(NhAiGrantSource.Agent, write.GrantSource);
            Assert.Equal(AgentContext.InvocationId, write.InvocationId);
        });
    }

    [Fact]
    public async Task Allowing_agent_grants_is_part_of_the_descriptor_and_the_contract()
    {
        await using var plain = await CallProbe.StartAsync(allowAgentGrant: false);
        await using var granted = await CallProbe.StartAsync(allowAgentGrant: true);

        Assert.False(plain.Descriptor.AllowAgentGrant);
        Assert.True(granted.Descriptor.AllowAgentGrant);
        Assert.NotEqual(plain.Descriptor.ContractHash, granted.Descriptor.ContractHash);
        Assert.Equal(plain.Descriptor.SchemaHash, granted.Descriptor.SchemaHash);
    }

    private sealed record CallSnapshot(
        string ServerId,
        string RemoteName,
        string ToolId,
        NhAiGrantSource GrantSource,
        Guid InvocationId);

    /// <summary>
    /// Records the call context in the flow that writes each client message to the server.
    /// </summary>
    private sealed class RecordingStream(Stream inner) : Stream
    {
        public List<CallSnapshot?> Writes { get; } = [];

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Record();
            inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Record();
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Record();
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        private void Record()
        {
            var current = NhAiMcpCallContext.Current;
            lock (Writes)
            {
                Writes.Add(current is null
                    ? null
                    : new CallSnapshot(
                        current.ServerId,
                        current.RemoteName,
                        current.Descriptor.Id,
                        current.Invocation.GrantSource,
                        current.Invocation.InvocationId));
            }
        }
    }

    private sealed class CallProbe : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private readonly McpServer _server;
        private readonly McpClient _client;
        private readonly RecordingStream _stream;

        private CallProbe(
            ServiceProvider provider,
            AsyncServiceScope scope,
            McpServer server,
            McpClient client,
            RecordingStream stream,
            AIFunction function)
        {
            _provider = provider;
            _scope = scope;
            _server = server;
            _client = client;
            _stream = stream;
            Function = function;
        }

        public AIFunction Function { get; }

        public NhAiToolDescriptor Descriptor => Assert.IsAssignableFrom<INhAiGovernedAIFunction>(Function).Descriptor;

        public IReadOnlyList<CallSnapshot?> Writes
        {
            get
            {
                lock (_stream.Writes)
                {
                    return _stream.Writes.ToArray();
                }
            }
        }

        public static async Task<CallProbe> StartAsync(bool allowAgentGrant)
        {
            var services = new ServiceCollection();
            services.AddScoped<INhAiToolInvocationGate>(_ => NhAiTestInvocationGate.Authorized(AgentContext));
            services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
            services.AddSingleton<INhAiAuditSink>(new NhAiCapturedAuditSink());
            services.AddNewHeapPlatformAIMcp();
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateAsyncScope();
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var tool = new Tool
            {
                Name = "lookup_order",
                Description = "Looks up the planning of an order.",
                InputSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { order = new { type = "string" } },
                    required = new[] { "order" }
                })
            };
            var server = McpServer.Create(
                new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
                new McpServerOptions
                {
                    ScopeRequests = false,
                    Handlers = new McpServerHandlers
                    {
                        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [tool] }),
                        CallToolHandler = (_, _) => ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "Monday" }] })
                    }
                },
                serviceProvider: scope.ServiceProvider);
            _ = server.RunAsync();
            var stream = new RecordingStream(clientToServer.Writer.AsStream());
            var client = await McpClient.CreateAsync(new StreamClientTransport(stream, serverToClient.Reader.AsStream()));
            try
            {
                var policy = new NhAiMcpImportedToolPolicy(
                    "lookup_order",
                    "lookup-order",
                    "Looks up the planning of an order.",
                    NhAiToolEffect.ReadOnly,
                    NhAiToolExposure.Agent,
                    ["planning.read"])
                {
                    Approval = NhAiApprovalRequirement.NotRequired,
                    AllowAgentGrant = allowAgentGrant
                };
                var catalog = scope.ServiceProvider.GetRequiredService<INhAiMcpClientToolImporter>().Import(
                    await client.ListToolsAsync(),
                    new NhAiMcpImportOptions("planning", "planning", [policy]));
                var function = Assert.Single(catalog.CreateFunctions(scope.ServiceProvider));
                return new CallProbe(provider, scope, server, client, stream, function);
            }
            catch
            {
                await client.DisposeAsync();
                await server.DisposeAsync();
                await scope.DisposeAsync();
                await provider.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _server.DisposeAsync();
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
