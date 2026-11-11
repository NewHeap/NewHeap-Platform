using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The assistant as MCP server: an exported tool call that left a proposal for a person to decide sends
/// an MCP client that supports multi round-trip requests and link requests to the application's
/// approval page (<see cref="NhAssistantMcpOptions.ApprovalLinkTemplate"/>), over a stateless and a
/// stateful Streamable HTTP server alike. The retry carries only a protected reference to the proposal,
/// which is checked again for the caller; it never runs the tool again and returns the proposal's
/// decision. The client's accept is never an approval, and without the template or the client's support
/// the tool's result goes back unchanged.
/// </summary>
public sealed class AssistantMcpApprovalElicitationTests
{
    private const string Person = "user-1";
    private const string Template = "https://app.example.com/approvals/{approvalId}";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_proposal_sends_the_client_to_the_approval_page_and_the_retry_returns_its_decision(bool stateless)
    {
        var approval = Approval(NhAssistantApprovalStatuses.Pending);
        await using var server = await ExportServer.StartAsync(stateless, Template, approval);
        var links = new ConcurrentQueue<ElicitRequestParams>();
        await using var client = await server.ConnectAsync((request, _) =>
        {
            links.Enqueue(request!);
            // The person decided on the page while the client waited; the client only says they looked.
            approval.Status = links.Count == 1 ? NhAssistantApprovalStatuses.Pending : NhAssistantApprovalStatuses.Executed;
            approval.ExecutionResultCode = approval.Status == NhAssistantApprovalStatuses.Executed ? "succeeded" : null;
            return ValueTask.FromResult(new ElicitResult { Action = "accept" });
        });

        var result = await client.CallToolAsync("propose_change", new Dictionary<string, object?> { ["status"] = "OnHold" });

        // The tool ran once; the retries only read the proposal's decision.
        Assert.Equal(1, server.ToolRuns);
        Assert.Equal(2, links.Count);
        Assert.All(links, link =>
        {
            Assert.Equal("url", link.Mode);
            Assert.Equal($"https://app.example.com/approvals/{approval.Id:D}", link.Url);
        });
        Assert.NotEqual(true, result.IsError);
        var decision = Assert.Single(result.Meta![NhAssistantMcpApprovalElicitation.ResultMetaKey]!.AsArray());
        Assert.Equal((approval.Id.ToString("D"), "executed", "succeeded"), (decision!["approvalId"]!.GetValue<string>(), decision["status"]!.GetValue<string>(), decision["executionResultCode"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_rejected_proposal_fails_the_call_and_a_declined_link_leaves_it_pending()
    {
        var rejected = Approval(NhAssistantApprovalStatuses.Pending);
        await using (var server = await ExportServer.StartAsync(stateless: true, Template, rejected))
        {
            await using var client = await server.ConnectAsync((_, _) =>
            {
                rejected.Status = NhAssistantApprovalStatuses.Rejected;
                return ValueTask.FromResult(new ElicitResult { Action = "accept" });
            });

            var result = await client.CallToolAsync("propose_change", new Dictionary<string, object?> { ["status"] = "OnHold" });

            Assert.True(result.IsError);
            Assert.Equal(NhAssistantErrorCodes.McpProposalRejected, CodeOf(result));
        }

        var waiting = Approval(NhAssistantApprovalStatuses.Pending);
        await using (var server = await ExportServer.StartAsync(stateless: true, Template, waiting))
        {
            await using var client = await server.ConnectAsync((_, _) => ValueTask.FromResult(new ElicitResult { Action = "decline" }));

            var result = await client.CallToolAsync("propose_change", new Dictionary<string, object?> { ["status"] = "OnHold" });

            // Declining the page approves nothing: the proposal waits, and the tool ran once.
            Assert.True(result.IsError);
            Assert.Equal(NhAiToolFailureCodes.ApprovalRequired, CodeOf(result));
            Assert.Equal(NhAssistantApprovalStatuses.Pending, waiting.Status);
            Assert.Equal(1, server.ToolRuns);
        }
    }

    [Fact]
    public async Task A_client_that_keeps_accepting_a_pending_proposal_is_sent_at_most_three_times()
    {
        var approval = Approval(NhAssistantApprovalStatuses.Pending);
        await using var server = await ExportServer.StartAsync(stateless: true, Template, approval);
        var rounds = 0;
        await using var client = await server.ConnectAsync((_, _) =>
        {
            Interlocked.Increment(ref rounds);
            return ValueTask.FromResult(new ElicitResult { Action = "accept" });
        });

        var result = await client.CallToolAsync("propose_change", new Dictionary<string, object?> { ["status"] = "OnHold" });

        Assert.Equal(NhAssistantMcpApprovalElicitation.MaxRounds, rounds);
        Assert.Equal(NhAiToolFailureCodes.ApprovalRequired, CodeOf(result));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, approval.Status);
    }

    [Fact]
    public async Task Without_the_template_or_the_clients_support_the_tools_result_goes_back_unchanged()
    {
        var approval = Approval(NhAssistantApprovalStatuses.Pending);
        await using (var server = await ExportServer.StartAsync(stateless: true, template: null, approval))
        {
            await using var client = await server.ConnectAsync((_, _) => throw new InvalidOperationException("No link without the template."));

            var result = await client.CallToolAsync("propose_change", new Dictionary<string, object?> { ["status"] = "OnHold" });

            Assert.Equal("Proposal recorded.", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }

        await using (var server = await ExportServer.StartAsync(stateless: true, Template, approval))
        {
            // A client that takes no link requests gets the tool's result.
            await using var client = await server.ConnectAsync(null);

            var result = await client.CallToolAsync("propose_change", new Dictionary<string, object?> { ["status"] = "OnHold" });

            Assert.Equal("Proposal recorded.", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }
    }

    [Fact]
    public async Task A_retry_with_a_state_of_another_caller_or_a_forged_state_never_runs_the_tool()
    {
        var approval = Approval(NhAssistantApprovalStatuses.Pending);
        await using var server = await ExportServer.StartAsync(stateless: true, Template, approval);
        await using var client = await server.ConnectAsync((_, _) => ValueTask.FromResult(new ElicitResult { Action = "accept" }));

        foreach (var state in new[] { NhAssistantMcpApprovalElicitation.StatePrefix + "forged", server.StateFor("user-2") })
        {
            var result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "propose_change",
                Arguments = new Dictionary<string, JsonElement> { ["status"] = JsonSerializer.SerializeToElement("OnHold") },
                RequestState = state,
                InputResponses = new Dictionary<string, InputResponse>
                {
                    [NhAssistantMcpApprovalElicitation.InputKey] = InputResponse.FromElicitResult(new ElicitResult { Action = "accept" })
                }
            });

            Assert.True(result.IsError);
            Assert.Equal(NhAssistantErrorCodes.McpApprovalStateInvalid, CodeOf(result));
        }

        Assert.Equal(0, server.ToolRuns);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, approval.Status);
    }

    [Theory]
    [InlineData("https://app.example.com/approvals/{approvalId}", true)]
    [InlineData("http://localhost:4200/approvals/{approvalId}", true)]
    [InlineData("http://app.example.com/approvals/{approvalId}", false)]
    [InlineData("https://{approvalId}.example.com/approvals", false)]
    [InlineData("https://app.example.com/approvals/{approvalId}/{approvalId}", false)]
    [InlineData("https://user@app.example.com/approvals/{approvalId}", false)]
    [InlineData("/approvals/{approvalId}", false)]
    public void The_approval_link_template_is_an_absolute_https_page_with_one_proposal_id(string template, bool valid)
    {
        Assert.Equal(valid, NhAssistantMcpOptions.IsValidApprovalLinkTemplate(template));
    }

    private static AssistantApproval Approval(string status)
    {
        return new AssistantApproval
        {
            Id = Guid.NewGuid(),
            Status = status,
            InitiatorActorId = Person,
            ToolId = "projects.change-status"
        };
    }

    private static string? CodeOf(CallToolResult result)
    {
        return result.Meta?[NhAiMcpResultMetadata.CodeKey]?.GetValue<string>();
    }

    /// <summary>
    /// An MCP server over real HTTP on loopback whose one tool records a proposal for the caller, as an
    /// exported tool that calls <c>INhAssistantProposals.ProposeForAsync</c> does, inside the assistant's
    /// approval step. The proposal lives in a substitute store, so a test decides it between rounds.
    /// </summary>
    private sealed class ExportServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly NhAssistantMcpApprovalElicitation _step;
        private int _toolRuns;

        private ExportServer(WebApplication app, NhAssistantMcpApprovalElicitation step)
        {
            _app = app;
            _step = step;
        }

        public string Endpoint { get; private set; } = string.Empty;

        public int ToolRuns => Volatile.Read(ref _toolRuns);

        public AssistantApproval Approval { get; private set; } = null!;

        private static NhAiToolDescriptor Descriptor { get; } = new(
            "projects.propose-change",
            1,
            "Propose a project change.",
            typeof(JsonElement),
            typeof(JsonElement),
            NhAiToolEffect.Mutation,
            NhAiToolExposure.Mcp,
            true,
            ["projects.manage"]);

        public static async Task<ExportServer> StartAsync(bool stateless, string? template, AssistantApproval approval)
        {
            var options = new NhAssistantOptions();
            options.Mcp.ApprovalLinkTemplate = template;
            var step = new NhAssistantMcpApprovalElicitation(
                new StaticOptionsMonitor(options),
                new EphemeralDataProtectionProvider(),
                (approvalId, _) => Task.FromResult(approvalId == approval.Id ? approval : null),
                NullLogger<NhAssistantMcpApprovalElicitation>.Instance);
            ExportServer? created = null;
            var app = await LocalhostTestServer.StartAsync(port =>
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls($"http://localhost:{port}");
                builder.Logging.ClearProviders();
                builder.Services
                    .AddMcpServer()
                    .WithHttpTransport(transport => transport.Stateless = stateless)
                    .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
                    {
                        Tools =
                        [
                            new Tool
                            {
                                Name = "propose_change",
                                Description = "Propose a project change.",
                                InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { status = new { type = "string" } } })
                            }
                        ]
                    }))
                    .WithCallToolHandler((request, cancellationToken) => created!.CallAsync(request, cancellationToken));
                var server = builder.Build();
                server.MapMcp("/mcp");
                return server;
            });
            created = new ExportServer(app, step)
            {
                Endpoint = LocalhostTestServer.Origin(app) + "/mcp",
                Approval = approval
            };
            return created;
        }

        /// <summary>
        /// A state the step issued for <paramref name="actorId"/>, valid but not this caller's.
        /// </summary>
        public string StateFor(string actorId)
        {
            return _step.StateOf(actorId, null, Descriptor.Id, [Approval.Id], 1);
        }
        /// <summary>
        /// A client that takes link requests with <paramref name="elicitation"/>, or none without it.
        /// </summary>
        public async Task<McpClient> ConnectAsync(
            Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? elicitation,
            string? protocolVersion = null)
        {
            return await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(Endpoint),
                    TransportMode = HttpTransportMode.StreamableHttp
                }),
                new McpClientOptions
                {
                    ProtocolVersion = protocolVersion,
                    Capabilities = elicitation is null
                        ? new ClientCapabilities()
                        : new ClientCapabilities { Elicitation = new ElicitationCapability { Url = new UrlElicitationCapability() } },
                    Handlers = new McpClientHandlers { ElicitationHandler = elicitation }
                });
        }

        private async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
        {
            var call = new NhAiMcpExportedCall(request, new NhAiInvocationContext(Person, "mcp", new Dictionary<string, string>()), Descriptor);
            return await _step.InvokeAsync(
                call,
                _ =>
                {
                    Interlocked.Increment(ref _toolRuns);
                    NhAssistantMcpCallProposals.Record(Approval.Id, Person);
                    return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "Proposal recorded." }] });
                },
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class StaticOptionsMonitor(NhAssistantOptions value) : IOptionsMonitor<NhAssistantOptions>
    {
        public NhAssistantOptions CurrentValue => value;

        public NhAssistantOptions Get(string? name)
        {
            return value;
        }

        public IDisposable? OnChange(Action<NhAssistantOptions, string?> listener)
        {
            return null;
        }
    }
}
