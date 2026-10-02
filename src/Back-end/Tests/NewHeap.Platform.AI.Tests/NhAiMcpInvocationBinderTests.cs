using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiMcpInvocationBinderTests
{
    private const string MetaKey = "com.example/research-context";

    private static readonly NhAiInvocationContext AgentContext = new(
        "agent:research-assistant",
        "assistant",
        new Dictionary<string, string>())
    {
        ActorKind = NhAiActorKind.Agent,
        AccountableOwnerId = "user-1",
        RunId = "turn-1",
        CorrelationId = "turn-1"
    };

    [Fact]
    public async Task Reviewed_binder_sends_meta_through_the_official_transport_while_the_model_sees_only_the_remote_schema()
    {
        await using var probe = await Probe.StartAsync(policy => policy with { InvocationBinderId = "research-context" });

        var result = await probe.InvokeAsync(new AIFunctionArguments { ["question"] = "Which risks block Alpha?" });

        Assert.True(result.Success);
        Assert.Equal(["question"], probe.Function.JsonSchema.GetProperty("properties").EnumerateObject().Select(item => item.Name));
        var call = Assert.Single(probe.Remote.Calls);
        Assert.Equal(["question"], call.Arguments.Keys);
        var context = Assert.IsType<JsonObject>(call.Meta![MetaKey]);
        Assert.Equal(AgentContext.InvocationId.ToString(), context["invocationId"]!.GetValue<string>());
        Assert.Equal("agent:research-assistant", context["actor"]!["id"]!.GetValue<string>());
        Assert.Equal("agent", context["actor"]!["kind"]!.GetValue<string>());
        Assert.Equal("user-1", context["accountableOwnerId"]!.GetValue<string>());
        Assert.Equal("turn-1", context["runId"]!.GetValue<string>());
        Assert.Equal(probe.Descriptor.ContractHash, context["tool"]!["contractHash"]!.GetValue<string>());
        // The binder saw the validated model arguments and the authorized context.
        var bound = Assert.Single(probe.Binder.Contexts);
        Assert.Equal("Which risks block Alpha?", bound.Arguments["question"]);
        Assert.Same(AgentContext, bound.Invocation);
        Assert.Equal(NhAiOutcomeKind.Succeeded, Assert.Single(probe.Audit.Records).Outcome);
    }

    [Fact]
    public async Task A_registered_binder_applies_only_to_a_policy_that_opts_in_and_changes_its_contract()
    {
        await using var bound = await Probe.StartAsync(policy => policy with { InvocationBinderId = "research-context" });
        await using var plain = await Probe.StartAsync(policy => policy);

        var result = await plain.InvokeAsync(new AIFunctionArguments { ["question"] = "Status?" });

        Assert.True(result.Success);
        var call = Assert.Single(plain.Remote.Calls);
        Assert.False(call.Meta?.ContainsKey(MetaKey) ?? false);
        Assert.Empty(plain.Binder.Contexts);
        Assert.NotEqual(plain.Descriptor.ContractHash, bound.Descriptor.ContractHash);
        Assert.Equal(plain.Descriptor.SchemaHash, bound.Descriptor.SchemaHash);
    }

    [Fact]
    public async Task A_policy_cannot_name_a_binder_that_is_not_registered_for_its_server_and_tool()
    {
        var unknownBinder = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Probe.StartAsync(policy => policy with { InvocationBinderId = "other-context" }));
        var otherServer = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Probe.StartAsync(policy => policy with { InvocationBinderId = "research-context" }, serverId: "elsewhere"));

        Assert.Contains("not registered for this server and tool", unknownBinder.Message, StringComparison.Ordinal);
        Assert.Contains("not registered for this server and tool", otherServer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_authorization_runs_neither_the_binder_nor_the_remote_call()
    {
        await using var probe = await Probe.StartAsync(
            policy => policy with { InvocationBinderId = "research-context" },
            gate: NhAiTestInvocationGate.Denied("ai-tool-authorization-denied"));

        var result = await probe.InvokeAsync(new AIFunctionArguments { ["question"] = "Status?" });

        Assert.False(result.Success);
        Assert.Empty(probe.Binder.Contexts);
        Assert.Empty(probe.Remote.Calls);
        Assert.Equal(NhAiOutcomeKind.AuthorizationDenied, Assert.Single(probe.Audit.Records).Outcome);
    }

    [Fact]
    public async Task A_mutation_binds_only_after_its_approval()
    {
        await using var probe = await Probe.StartAsync(policy => policy with
        {
            Effect = NhAiToolEffect.Mutation,
            Approval = NhAiApprovalRequirement.Required,
            Idempotency = NhAiIdempotencySupport.Required,
            InvocationBinderId = "research-context"
        });

        var result = await probe.InvokeAsync(new AIFunctionArguments { ["question"] = "Start research." });

        Assert.Equal(NhAiToolFailureCodes.ApprovalRequired, FirstCode(result));
        Assert.Empty(probe.Binder.Contexts);
        Assert.Empty(probe.Remote.Calls);
    }

    [Fact]
    public async Task Metadata_counts_toward_the_input_limit_of_the_tool()
    {
        await using var probe = await Probe.StartAsync(policy => policy with
        {
            InvocationBinderId = "research-context",
            MaxInputBytes = 512
        });
        probe.Binder.Padding = new string('x', 600);

        var result = await probe.InvokeAsync(new AIFunctionArguments { ["question"] = "Status?" });

        Assert.Equal(NhAiToolFailureCodes.InputTooLarge, FirstCode(result));
        Assert.Single(probe.Binder.Contexts);
        Assert.Empty(probe.Remote.Calls);
        var record = Assert.Single(probe.Audit.Records);
        Assert.Equal(NhAiOutcomeKind.TerminalFailure, record.Outcome);
        Assert.Equal(NhAiToolFailureCodes.InputTooLarge, record.ResultCode);
    }

    [Fact]
    public async Task A_failed_or_throwing_binder_stops_the_call_with_an_audited_code()
    {
        await using var refused = await Probe.StartAsync(policy => policy with { InvocationBinderId = "research-context" });
        refused.Binder.Failure = "example-turn-stale";
        await using var throwing = await Probe.StartAsync(policy => policy with { InvocationBinderId = "research-context" });
        throwing.Binder.Exception = new InvalidOperationException("secret-binder-message");

        var refusedResult = await refused.InvokeAsync(new AIFunctionArguments { ["question"] = "Status?" });
        var throwingResult = await throwing.InvokeAsync(new AIFunctionArguments { ["question"] = "Status?" });

        Assert.Equal("example-turn-stale", FirstCode(refusedResult));
        Assert.Equal("example-turn-stale", Assert.Single(refused.Audit.Records).ResultCode);
        Assert.Equal(NhAiMcpInvocationBindingCodes.BindingFailed, FirstCode(throwingResult));
        Assert.DoesNotContain("secret", string.Join(' ', throwingResult.GetResultItems().SelectMany(item => item.ErrorMessages).Select(message => message.ToString())), StringComparison.Ordinal);
        Assert.Empty(refused.Remote.Calls);
        Assert.Empty(throwing.Remote.Calls);
    }

    [Theory]
    [InlineData("com.newheap/assistant-context", true)]
    [InlineData("nl.example.research/context-v2", true)]
    [InlineData("progressToken", false)]
    [InlineData("newheap/context", false)]
    [InlineData("io.modelcontextprotocol/context", false)]
    [InlineData("dev.mcp/context", false)]
    [InlineData("com.newheap/", false)]
    [InlineData("com..newheap/context", false)]
    [InlineData("com.newheap/-context", false)]
    public void Metadata_keys_need_a_reverse_dns_prefix_that_mcp_does_not_reserve(string key, bool valid)
    {
        Assert.Equal(valid, NhAiMcpRequestMetadata.IsValidKey(key));
    }

    private static string? FirstCode(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private sealed class ProbeBinder : INhAiMcpInvocationBinder
    {
        public List<NhAiMcpInvocationBindingContext> Contexts { get; } = [];

        public string? Padding { get; set; }

        public string? Failure { get; set; }

        public Exception? Exception { get; set; }

        public ValueTask<TaskResult<NhAiMcpRequestMetadata>> BindAsync(
            NhAiMcpInvocationBindingContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            if (Exception is not null)
            {
                throw Exception;
            }
            if (Failure is not null)
            {
                return ValueTask.FromResult(TaskResult<NhAiMcpRequestMetadata>.Failed(Failure, "The context is stale."));
            }
            var json = context.Identity.ToJson();
            if (Padding is not null)
            {
                json["padding"] = Padding;
            }
            return ValueTask.FromResult(TaskResult<NhAiMcpRequestMetadata>.Succeeded(NhAiMcpRequestMetadata.Create(MetaKey, json)));
        }
    }

    private sealed class RemoteResearchServer
    {
        public List<(JsonObject? Meta, IReadOnlyDictionary<string, JsonElement> Arguments)> Calls { get; } = [];

        public static Tool Tool { get; } = new()
        {
            Name = "research-ask",
            Description = "Asks the research service a question.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { question = new { type = "string" } },
                required = new[] { "question" }
            })
        };

        public McpServerHandlers Handlers => new()
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [Tool] }),
            CallToolHandler = (request, _) =>
            {
                Calls.Add((
                    request.Params?.Meta?.DeepClone() as JsonObject,
                    new Dictionary<string, JsonElement>(request.Params?.Arguments ?? new Dictionary<string, JsonElement>())));
                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "queued" }] });
            }
        };
    }

    private sealed class Probe : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private readonly McpServer _server;
        private readonly McpClient _client;

        private Probe(
            ServiceProvider provider,
            AsyncServiceScope scope,
            McpServer server,
            McpClient client,
            RemoteResearchServer remote,
            AIFunction function)
        {
            _provider = provider;
            _scope = scope;
            _server = server;
            _client = client;
            Remote = remote;
            Function = function;
        }

        public RemoteResearchServer Remote { get; }

        public ProbeBinder Binder => _scope.ServiceProvider.GetRequiredService<ProbeBinder>();

        public NhAiCapturedAuditSink Audit => _provider.GetRequiredService<NhAiCapturedAuditSink>();

        public AIFunction Function { get; }

        public NhAiToolDescriptor Descriptor => Assert.IsAssignableFrom<INhAiGovernedAIFunction>(Function).Descriptor;

        public static async Task<Probe> StartAsync(
            Func<NhAiMcpImportedToolPolicy, NhAiMcpImportedToolPolicy> configure,
            INhAiToolInvocationGate? gate = null,
            string serverId = "research")
        {
            var audit = new NhAiCapturedAuditSink();
            var services = new ServiceCollection();
            services.AddScoped<INhAiToolInvocationGate>(_ => gate ?? NhAiTestInvocationGate.Authorized(AgentContext));
            services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
            services.AddSingleton(audit);
            services.AddSingleton<INhAiAuditSink>(audit);
            services.AddNewHeapPlatformAIMcpInvocationBinder<ProbeBinder>(
                new NhAiMcpInvocationBinding("research", "research-ask", "research-context"));
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateAsyncScope();
            var remote = new RemoteResearchServer();
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var server = McpServer.Create(
                new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
                new McpServerOptions { ScopeRequests = false, Handlers = remote.Handlers },
                serviceProvider: scope.ServiceProvider);
            _ = server.RunAsync();
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
            try
            {
                var policy = configure(new NhAiMcpImportedToolPolicy(
                    "research-ask",
                    "research-ask",
                    "Asks the research service a question.",
                    NhAiToolEffect.ReadOnly,
                    NhAiToolExposure.Agent,
                    ["research.ask"])
                {
                    Approval = NhAiApprovalRequirement.NotRequired
                });
                var catalog = scope.ServiceProvider.GetRequiredService<INhAiMcpClientToolImporter>().Import(
                    await client.ListToolsAsync(),
                    new NhAiMcpImportOptions(serverId, "research", [policy]));
                var function = Assert.Single(catalog.CreateFunctions(scope.ServiceProvider));
                return new Probe(provider, scope, server, client, remote, function);
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

        public async Task<TaskResult<CallToolResult>> InvokeAsync(AIFunctionArguments arguments)
        {
            return Assert.IsType<TaskResult<CallToolResult>>(await Function.InvokeAsync(arguments));
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
