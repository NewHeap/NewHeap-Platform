using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A host that opts in exports reviewed imported MCP tools to its own MCP clients, such as a person's AI
/// app, over real Streamable HTTP: only the read-only tools its selector names, of servers with the
/// organization's credential the caller may use, with the reviewed description and schema. A call runs
/// through the governed invoker as the calling person, sends the assistant context with that person and
/// no conversation, and is audited. Without the opt-in nothing changes.
/// </summary>
public sealed class AssistantImportedMcpToolExportTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string DeskUrl = "http://localhost/servicedesk/mcp";
    private const string Admin = "admin-1";
    private const string Member = "user-1";

    [Theory]
    [InlineData(AssistantTestProvider.PostgreSql)]
    [InlineData(AssistantTestProvider.SqlServer)]
    public async Task Selected_reviewed_read_tools_reach_the_persons_ai_app_and_run_as_that_person(AssistantTestProvider provider)
    {
        var desk = new DeskServer();
        var selection = new Selection();
        var audit = new RecordingAuditSink();
        await using var host = await StartAsync(desk, selection, audit, provider: provider);
        await ConnectDeskAsync(host, descriptionOverride: "Lists the tickets assigned to me.");

        await using var client = await ConnectAsync(host, Member);
        var listed = (await client.ListToolsAsync()).Where(tool => tool.Name.StartsWith("servicedesk_", StringComparison.Ordinal)).ToArray();

        // The selector was asked only about the read-only tools; the mutating one never reaches it.
        Assert.Equal(["my-work", "read-ticket"], selection.Asked.Select(tool => tool.RemoteName).Order(StringComparer.Ordinal));
        Assert.Equal(["servicedesk_my_work", "servicedesk_read_ticket"], listed.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        var myWork = Assert.Single(listed, tool => tool.Name == "servicedesk_my_work");
        // The reviewed description, not the remote text; the remote schema; honest hints.
        Assert.Equal("Lists the tickets assigned to me.", myWork.Description);
        Assert.Equal("Lists the tickets assigned to me.", Assert.Single(selection.Asked, tool => tool.RemoteName == "my-work").Description);
        Assert.Equal(["status"], myWork.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(item => item.Name));
        Assert.True(myWork.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(myWork.ProtocolTool.Annotations?.DestructiveHint);

        var result = await myWork.CallAsync(new Dictionary<string, object?> { ["status"] = "open" });

        Assert.NotEqual(true, result.IsError);
        var call = Assert.Single(desk.Calls);
        Assert.Equal("my-work", call.Tool);
        Assert.Equal(["status"], call.Arguments.Keys);
        // The remote names the calling person; an exported call has no conversation.
        var context = call.Context!;
        Assert.Equal(NhAssistantMcpContext.Version, context["version"]!.GetValue<int>());
        Assert.Equal("mcp", context["channel"]!.GetValue<string>());
        Assert.Equal(Member, context["invocation"]!["actor"]!["id"]!.GetValue<string>());
        Assert.Equal("human", context["invocation"]!["actor"]!["kind"]!.GetValue<string>());
        Assert.Equal(Member, context["invocation"]!["accountableOwnerId"]!.GetValue<string>());
        Assert.Equal("mcp.desk.my-work", context["invocation"]!["tool"]!["id"]!.GetValue<string>());
        Assert.Null(context["conversation"]);
        Assert.Null(context["snapshot"]);
        Assert.Null(context["chain"]);
        // The caller's own token never travels to the remote server.
        Assert.DoesNotContain(desk.AuthorizationHeaders, header => header?.Contains(Member, StringComparison.Ordinal) == true);

        // The governed invoker audited the call: server and tool, the caller and the outcome.
        var audited = Assert.Single(audit.Records, record => record.ToolId == "mcp.desk.my-work");
        Assert.Equal(Member, audited.ActorId);
        Assert.Equal(NhAiOutcomeKind.Succeeded, audited.Outcome);
    }

    [Fact]
    public async Task Without_the_opt_in_no_imported_tool_reaches_an_mcp_client()
    {
        var desk = new DeskServer();
        await using var host = await StartAsync(desk, new Selection(), new RecordingAuditSink(), export: false);
        await ConnectDeskAsync(host);

        await using var client = await ConnectAsync(host, Member);

        Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name.StartsWith("servicedesk_", StringComparison.Ordinal)
            || tool.Name.Contains("desk", StringComparison.Ordinal));
        Assert.Empty(desk.Calls);
    }

    [Fact]
    public async Task A_switched_off_assistant_exports_nothing()
    {
        var desk = new DeskServer();
        await using var host = await StartAsync(desk, new Selection(), new RecordingAuditSink(), enabled: false);
        await ConnectDeskAsync(host);

        await using var client = await ConnectAsync(host, Member);

        Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name.StartsWith("servicedesk_", StringComparison.Ordinal));
        Assert.Empty(desk.Calls);
    }

    [Theory]
    [InlineData(AssistantTestProvider.PostgreSql)]
    [InlineData(AssistantTestProvider.SqlServer)]
    public async Task A_changed_remote_schema_stops_the_export_and_disables_the_tool_as_in_a_turn(AssistantTestProvider provider)
    {
        var desk = new DeskServer();
        await using var host = await StartAsync(desk, new Selection(), new RecordingAuditSink(), provider: provider);
        await ConnectDeskAsync(host);
        // The remote changes the reviewed tool's schema after the administrator enabled it.
        desk.ChangeMyWorkSchema = true;
        await using var client = await ConnectAsync(host, Member);

        var names = (await client.ListToolsAsync()).Select(tool => tool.Name).ToArray();
        Assert.DoesNotContain("servicedesk_my_work", names);
        Assert.Contains("servicedesk_read_ticket", names);
        await using var scope = host.Services.CreateAsyncScope();
        var tool = Assert.Single(
            await scope.ServiceProvider.GetRequiredService<NhAssistantAdminStore>().ListMcpToolsAsync("desk", CancellationToken.None),
            item => item.RemoteName == "my-work");
        Assert.False(tool.IsEnabled);
        Assert.Equal(NhAssistantMcpToolStatuses.SchemaChanged, tool.Status);
        Assert.Empty(desk.Calls);
    }

    [Fact]
    public async Task The_servers_policy_and_the_hosts_discovery_policy_decide_per_caller()
    {
        var desk = new DeskServer();
        await using var host = await StartAsync(
            desk,
            new Selection(),
            new RecordingAuditSink(),
            discovery: (descriptor, _) => descriptor.Id != "mcp.desk.read-ticket");
        await ConnectDeskAsync(host, requiredPolicy: "app.assistant.admin");

        await using (var member = await ConnectAsync(host, Member))
        {
            // The member does not pass the server's required policy.
            Assert.DoesNotContain(await member.ListToolsAsync(), tool => tool.Name.StartsWith("servicedesk_", StringComparison.Ordinal));
        }

        await using var admin = await ConnectAsync(host, Admin, admin: true);
        var names = (await admin.ListToolsAsync()).Select(tool => tool.Name).Where(name => name.StartsWith("servicedesk_", StringComparison.Ordinal));
        // The host's discovery policy hides the ticket reader from everyone.
        Assert.Equal(["servicedesk_my_work"], names);
    }

    [Fact]
    public async Task A_server_that_serves_background_runs_with_the_organizations_credential_is_exported_only_when_the_selection_allows_it()
    {
        var desk = new DeskServer();
        var selection = new Selection();
        await using var host = await StartAsync(desk, selection, new RecordingAuditSink());
        await ConnectDeskAsync(host, headlessUse: NhAssistantMcpHeadlessUses.Organization);
        await using var client = await ConnectAsync(host, Member);

        Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name.StartsWith("servicedesk_", StringComparison.Ordinal));

        selection.Choose = tool => new NhAssistantImportedMcpToolExport(Selection.NameOf(tool)) { AllowOrganizationHeadlessServer = true };

        Assert.Contains(await client.ListToolsAsync(), tool => tool.Name == "servicedesk_my_work");
    }

    [Fact]
    public async Task An_export_name_that_another_tool_of_the_export_has_fails_the_listing()
    {
        var desk = new DeskServer();
        var selection = new Selection { Choose = _ => new NhAssistantImportedMcpToolExport("servicedesk_tickets") };
        await using var host = await StartAsync(desk, selection, new RecordingAuditSink());
        await ConnectDeskAsync(host);
        await using var client = await ConnectAsync(host, Member);

        await Assert.ThrowsAnyAsync<McpException>(async () => await client.ListToolsAsync());
        Assert.Empty(desk.Calls);
    }

    private Task<AssistantWebApplication> StartAsync(
        DeskServer desk,
        Selection selection,
        RecordingAuditSink audit,
        bool export = true,
        Func<NhAiToolDescriptor, NhAiInvocationContext, bool>? discovery = null,
        AssistantTestProvider provider = AssistantTestProvider.PostgreSql,
        bool enabled = true)
    {
        var servers = new InMemoryMcpServers();
        servers.Register(DeskUrl, desk.Handlers);
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            enabled: enabled,
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers));
                services.AddSingleton(selection);
                services.AddSingleton(audit);
                services.AddSingleton<INhAiAuditSink>(audit);
                if (discovery is not null)
                {
                    services.Replace(ServiceDescriptor.Scoped<INhAiToolDiscoveryPolicy>(_ => new NhAiTestDiscoveryPolicy(discovery)));
                }
                services.AddMcpServer()
                    .WithHttpTransport(options => options.Stateless = true)
                    .WithNewHeapPlatformAITools();
            },
            assistantBuilder: assistant =>
            {
                assistant.AddMcpContextBinding(new NhAssistantMcpContextBinding("desk", DeskUrl, "my-work") { IncludeSnapshot = false });
                if (export)
                {
                    assistant.ExportImportedMcpTools<TestSelector>();
                }
            },
            provider: provider,
            map: app => app.MapMcp("/mcp"));
    }

    /// <summary>
    /// Connects the servicedesk with the organization's credential, syncs it and enables its three tools:
    /// two that read and one that changes a ticket.
    /// </summary>
    private static async Task ConnectDeskAsync(
        AssistantWebApplication host,
        string? descriptionOverride = null,
        string? requiredPolicy = null,
        string? headlessUse = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, Admin);
        var administration = scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>();
        var created = await administration.CreateAsync(
            new NhAssistantMcpServerInput("desk", "Servicedesk", DeskUrl, NhAssistantMcpAuthModes.None, null, null, requiredPolicy, true, headlessUse),
            Admin,
            CancellationToken.None);
        Assert.True(created.Success, string.Join(", ", created.GetResultItems().Select(item => item.Name)));
        Assert.True((await administration.SyncAsync("desk", Admin, CancellationToken.None)).Success);
        Assert.True((await administration.UpdateToolAsync("desk", "my-work", true, NhAssistantMcpToolEffects.ReadOnly, descriptionOverride, Admin, CancellationToken.None)).Success);
        Assert.True((await administration.UpdateToolAsync("desk", "read-ticket", true, NhAssistantMcpToolEffects.ReadOnly, null, Admin, CancellationToken.None)).Success);
        Assert.True((await administration.UpdateToolAsync("desk", "close-ticket", true, NhAssistantMcpToolEffects.Mutation, null, Admin, CancellationToken.None)).Success);
    }

    private static async Task<McpClient> ConnectAsync(AssistantWebApplication host, string user, bool admin = false)
    {
        var http = host.CreateClient(user, admin: admin);
        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                http,
                ownsHttpClient: true),
            new McpClientOptions { ProtocolVersion = "2025-11-25" });
    }

    /// <summary>What the host's selector decides; by default every tool it is asked about.</summary>
    private sealed class Selection
    {
        public ConcurrentQueue<NhAssistantExportableMcpTool> AskedQueue { get; } = new();

        public IReadOnlyList<NhAssistantExportableMcpTool> Asked => AskedQueue.ToArray();

        public Func<NhAssistantExportableMcpTool, NhAssistantImportedMcpToolExport?> Choose { get; set; } =
            tool => new NhAssistantImportedMcpToolExport(NameOf(tool));

        public static string NameOf(NhAssistantExportableMcpTool tool)
        {
            return "servicedesk_" + tool.RemoteName.Replace('-', '_');
        }
    }

    private sealed class TestSelector(Selection selection) : INhAssistantImportedMcpToolExportSelector
    {
        public ValueTask<NhAssistantImportedMcpToolExport?> SelectAsync(
            NhAssistantExportableMcpTool tool,
            NhAiInvocationContext caller,
            CancellationToken cancellationToken)
        {
            selection.AskedQueue.Enqueue(tool);
            return ValueTask.FromResult(selection.Choose(tool));
        }
    }

    private sealed class RecordingAuditSink : INhAiAuditSink
    {
        private readonly ConcurrentQueue<NhAiAuditRecord> _records = new();

        public IReadOnlyList<NhAiAuditRecord> Records => _records.ToArray();

        public ValueTask WriteAsync(NhAiAuditRecord record, CancellationToken cancellationToken = default)
        {
            _records.Enqueue(record);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A servicedesk that binds each call to the person in its request metadata: two tools that read and
    /// one that closes a ticket.
    /// </summary>
    private sealed class DeskServer
    {
        public ConcurrentQueue<(string Tool, JsonObject? Context, IReadOnlyDictionary<string, JsonElement> Arguments)> CallQueue { get; } = new();

        public IReadOnlyList<(string Tool, JsonObject? Context, IReadOnlyDictionary<string, JsonElement> Arguments)> Calls => CallQueue.ToArray();

        public ConcurrentQueue<string?> AuthorizationHeaders { get; } = new();

        public bool ChangeMyWorkSchema { get; set; }

        public McpServerHandlers Handlers => new()
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    Tool("my-work", "Remote: the caller's work.", ChangeMyWorkSchema ? new { status = new { type = "string" }, team = new { type = "string" } } : new { status = new { type = "string" } }),
                    Tool("read-ticket", "Remote: reads a ticket.", new { ticketId = new { type = "string" } }),
                    Tool("close-ticket", "Remote: closes a ticket.", new { ticketId = new { type = "string" } })
                ]
            }),
            CallToolHandler = (request, _) =>
            {
                CallQueue.Enqueue((
                    request.Params!.Name,
                    request.Params.Meta?[NhAssistantMcpContext.MetaKey]?.DeepClone() as JsonObject,
                    new Dictionary<string, JsonElement>(request.Params.Arguments ?? new Dictionary<string, JsonElement>())));
                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "Two open tickets." }] });
            }
        };

        private static Tool Tool(string name, string description, object properties)
        {
            return new Tool
            {
                Name = name,
                Description = description,
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties })
            };
        }
    }
}
