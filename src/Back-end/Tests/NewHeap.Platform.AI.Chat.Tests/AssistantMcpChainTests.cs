using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Applications that consult each other's agents over MCP continue one consultation chain (FB28). Each
/// host is a real ASP.NET application with its own schema, MCP endpoint and model; each imports the
/// consult tools of its peers as code-owned MCP tools that send the assistant context with the chain.
/// A loop between applications is refused with <c>assistant-consult-depth-exceeded</c> before a run
/// starts, a chain from a caller that was not reviewed or a malformed one is ignored, a forged shallow
/// chain stays bounded by the daily limit per person, and two applications with an agent of the same
/// id never refuse each other.
/// </summary>
public sealed class AssistantMcpChainTests(AssistantMcpChainHosts hosts) : IClassFixture<AssistantMcpChainHosts>
{
    [Fact]
    public async Task An_agent_consulted_across_applications_that_consults_back_is_refused_before_a_run_starts()
    {
        const string project = "project-0101";
        hosts.Reset();
        hosts.A.Model.Chat
            .RespondWithFunctionCall("mcp_b_consult_reviewer", Consult(project, "Is the intake complete?"), "call-a")
            .RespondWithText("The reviewer answered.");
        hosts.B.Model.Consulted
            .RespondWithFunctionCall("mcp_a_consult_intake", Consult(project, "What did the intake find?"), "call-b")
            .RespondWithText("The intake could not be asked back.");

        await hosts.A.ChatAsync("intake", "Ask the reviewer whether the intake is complete.");

        // The application that asked first sent its intake; the reviewer's consultation turn sent the
        // chain on, with its own application.
        var toB = Assert.Single(hosts.B.Inbound);
        Assert.Equal(Chain(0, (hosts.A, "intake")), toB.Context!["chain"]!.ToJsonString());
        var toA = Assert.Single(hosts.A.Inbound);
        Assert.Equal(Chain(1, (hosts.A, "intake"), (hosts.B, "reviewer")), toA.Context!["chain"]!.ToJsonString());
        Assert.Equal(hosts.B.ConsultationIdOf(project).ToString(), toA.Context["conversation"]!["turnId"]!.GetValue<string>());
        Assert.Null(toA.Context["snapshot"]);

        // The reviewer was consulted once, one level deep: an MCP call names no asking agent here.
        var consulted = Assert.Single(await hosts.B.ConsultationsAsync(project));
        Assert.Equal("reviewer", consulted.AgentId);
        Assert.Equal(1, consulted.Depth);
        Assert.Null(consulted.AskingAgentId);
        Assert.Equal(NhAssistantConsultationStatuses.Completed, consulted.Status);

        // Asking the intake back was refused: the intake asked first. Nothing was recorded and no
        // consultation turn ran.
        Assert.Contains(NhAssistantErrorCodes.ConsultDepthExceeded, hosts.B.Model.Consulted.FunctionResults.Single().Result?.ToString());
        Assert.Empty(await hosts.A.ConsultationsAsync(project));
        Assert.Empty(hosts.A.Model.Consulted.Requests);
        Assert.Equal(2, hosts.A.Model.Chat.Requests.Count);
    }

    [Fact]
    public async Task A_loop_that_never_returns_to_its_origin_is_refused_where_it_closes()
    {
        const string project = "project-0103";
        hosts.Reset();
        hosts.A.Model.Chat
            .RespondWithFunctionCall("mcp_b_consult_reviewer", Consult(project, "Can the project be planned?"), "call-a")
            .RespondWithText("The reviewer answered.");
        hosts.B.Model.Consulted
            .RespondWithFunctionCall("mcp_c_consult_planner", Consult(project, "Is there capacity?"), "call-b")
            .RespondWithText("The planner answered.");
        hosts.C.Model.Consulted
            .RespondWithFunctionCall("mcp_b_consult_reviewer", Consult(project, "Was the project reviewed?"), "call-c")
            .RespondWithText("The reviewer could not be asked again.");

        await hosts.A.ChatAsync("intake", "Ask the reviewer whether the project can be planned.");

        // A asks B, B asks C, and C asks B again: B refuses with the chain's own agent, although the
        // depth limit of three would still allow a third level.
        Assert.Equal(1, Assert.Single(await hosts.B.ConsultationsAsync(project)).Depth);
        Assert.Equal(2, Assert.Single(await hosts.C.ConsultationsAsync(project)).Depth);
        var toB = hosts.B.Inbound.ToArray();
        Assert.Equal(2, toB.Length);
        Assert.Equal(Chain(2, (hosts.A, "intake"), (hosts.B, "reviewer"), (hosts.C, "planner")), toB[1].Context!["chain"]!.ToJsonString());
        Assert.Contains(NhAssistantErrorCodes.ConsultDepthExceeded, hosts.C.Model.Consulted.FunctionResults.Single().Result?.ToString());
        Assert.Single(hosts.B.Model.Consulted.FunctionResults);
        Assert.Equal(2, hosts.B.Model.Consulted.Requests.Count);

        // A is not in the inner loop: it was never called and consulted nobody but B.
        Assert.Empty(hosts.A.Inbound);
        Assert.Empty(await hosts.A.ConsultationsAsync(project));
        Assert.Equal(2, hosts.A.Model.Chat.Requests.Count);
    }

    [Fact]
    public async Task The_chain_of_a_caller_that_was_not_reviewed_is_ignored_and_one_hosts_rules_apply()
    {
        const string project = "project-0102";
        hosts.Reset();
        hosts.A.Model.Chat
            .RespondWithFunctionCall("mcp_c_consult_planner", Consult(project, "Is there capacity?"), "call-a")
            .RespondWithText("The planner answered.");
        hosts.C.Model.Consulted
            .RespondWithFunctionCall("mcp_a_consult_intake", Consult(project, "Is the intake complete?"), "call-c")
            .RespondWithText("The intake is complete.");
        hosts.A.Model.Consulted.RespondWithText("The intake has no open checks.");

        await hosts.A.ChatAsync("intake", "Ask the planner whether there is capacity.");

        // A did not review C: the chain C sent, which names A's own intake, is a direct call to A.
        Assert.Equal(Chain(1, (hosts.A, "intake"), (hosts.C, "planner")), Assert.Single(hosts.A.Inbound).Context!["chain"]!.ToJsonString());
        var consulted = Assert.Single(await hosts.A.ConsultationsAsync(project));
        Assert.Equal("intake", consulted.AgentId);
        Assert.Equal(1, consulted.Depth);
        Assert.Equal(NhAssistantConsultationStatuses.Completed, consulted.Status);
        Assert.Contains("The intake has no open checks.", hosts.C.Model.Consulted.FunctionResults.Single().Result?.ToString());
        // C reviewed A and counted A's chain.
        Assert.Equal(1, Assert.Single(await hosts.C.ConsultationsAsync(project)).Depth);
    }

    [Fact]
    public async Task Applications_with_an_agent_of_the_same_id_do_not_refuse_each_other()
    {
        const string project = "project-0104";
        hosts.Reset();
        hosts.A.Model.Chat
            .RespondWithFunctionCall("mcp_b_consult_reviewer", Consult(project, "What did you find?"), "call-a")
            .RespondWithText("The other reviewer answered.");
        hosts.B.Model.Consulted.RespondWithText("The reviewer of the second application has no findings.");

        await hosts.A.ChatAsync("reviewer", "Ask the reviewer of the second application what it found.");

        // B continues A's chain, in which A's reviewer is not B's reviewer.
        Assert.Equal(Chain(0, (hosts.A, "reviewer")), Assert.Single(hosts.B.Inbound).Context!["chain"]!.ToJsonString());
        var consulted = Assert.Single(await hosts.B.ConsultationsAsync(project));
        Assert.Equal(NhAssistantConsultationStatuses.Completed, consulted.Status);
        Assert.Contains("has no findings", hosts.A.Model.Chat.FunctionResults.Single().Result?.ToString());
    }

    [Fact]
    public async Task A_forged_shallow_chain_is_bounded_by_the_daily_limit_per_person()
    {
        const string project = "project-0105";
        hosts.Reset();
        hosts.D.Model.Consulted
            .RespondWithText("First answer.")
            .RespondWithText("Second answer.");
        // A reviewed caller claims every call starts a new chain.
        var client = await hosts.D.ConnectAsync(AssistantMcpChainHosts.Forger, hosts.A.Application);
        var consult = await ToolAsync(client, AssistantMcpChainHosts.ConsultTool("reviewer"));
        var forged = Meta(Chain(0, (hosts.A, "intake")));

        var first = await consult.CallAsync(Question(project), options: new RequestOptions { Meta = forged });
        var second = await consult.CallAsync(Question(project), options: new RequestOptions { Meta = forged });
        var third = await consult.CallAsync(Question(project), options: new RequestOptions { Meta = forged });

        Assert.NotEqual(true, first.IsError);
        Assert.NotEqual(true, second.IsError);
        Assert.True(third.IsError);
        Assert.Equal(NhAssistantErrorCodes.ConsultLimitReached, CodeOf(third));
        Assert.All(await hosts.D.ConsultationsAsync(project), consultation => Assert.Equal(1, consultation.Depth));
        Assert.Equal(2, (await hosts.D.ConsultationsAsync(project)).Count);
        Assert.Equal(2, hosts.D.Model.Consulted.Requests.Count);
    }

    [Fact]
    public async Task A_loop_chain_is_refused_from_a_reviewed_caller_and_ignored_from_any_other()
    {
        const string project = "project-0106";
        hosts.Reset();
        hosts.B.Model.Consulted
            .RespondWithText("Answered for a caller that was not reviewed.")
            .RespondWithText("Answered for a caller without an issuer.");
        var loop = Meta(Chain(1, (hosts.B, "reviewer"), (hosts.A, "intake")));

        var reviewed = await ToolAsync(await hosts.B.ConnectAsync(AssistantMcpChainHosts.Member, hosts.A.Application), AssistantMcpChainHosts.ConsultTool("reviewer"));
        var refused = await reviewed.CallAsync(Question(project), options: new RequestOptions { Meta = loop });
        Assert.True(refused.IsError);
        Assert.Equal(NhAssistantErrorCodes.ConsultDepthExceeded, CodeOf(refused));
        Assert.Empty(await hosts.B.ConsultationsAsync(project));
        Assert.Empty(hosts.B.Model.Consulted.Requests);

        // The same chain from another issuer, or from a caller without one, is ignored: an ordinary MCP
        // client keeps working.
        foreach (var issuer in new[] { AssistantMcpChainHosts.Unknown, null })
        {
            var other = await ToolAsync(await hosts.B.ConnectAsync(AssistantMcpChainHosts.Member, issuer), AssistantMcpChainHosts.ConsultTool("reviewer"));
            var answered = await other.CallAsync(Question(project), options: new RequestOptions { Meta = loop });
            Assert.NotEqual(true, answered.IsError);
        }

        Assert.All(await hosts.B.ConsultationsAsync(project), consultation => Assert.Equal(1, consultation.Depth));
        Assert.Equal(2, hosts.B.Model.Consulted.Requests.Count);
    }

    [Fact]
    public async Task A_malformed_chain_of_a_reviewed_caller_is_ignored_never_refused()
    {
        const string project = "project-0107";
        hosts.Reset();
        hosts.B.Model.Consulted
            .RespondWithText("First answer.")
            .RespondWithText("Second answer.")
            .RespondWithText("Third answer.");
        var reviewer = new JsonObject { ["application"] = hosts.B.Application, ["agentId"] = "reviewer" };
        var malformed = new[]
        {
            // An agent id no application can have.
            new JsonObject
            {
                ["agents"] = new JsonArray(reviewer.DeepClone(), new JsonObject { ["application"] = hosts.A.Application, ["agentId"] = "Not An Id" }),
                ["consultationDepth"] = 1
            },
            // A depth that is no number.
            new JsonObject
            {
                ["agents"] = new JsonArray(reviewer.DeepClone(), new JsonObject { ["application"] = hosts.A.Application, ["agentId"] = "intake" }),
                ["consultationDepth"] = "1"
            },
            // A calling agent of another application than the caller's.
            new JsonObject
            {
                ["agents"] = new JsonArray(new JsonObject { ["application"] = hosts.A.Application, ["agentId"] = "intake" }, reviewer.DeepClone()),
                ["consultationDepth"] = 1
            }
        };
        var consult = await ToolAsync(await hosts.B.ConnectAsync(AssistantMcpChainHosts.Member, hosts.A.Application), AssistantMcpChainHosts.ConsultTool("reviewer"));

        foreach (var chain in malformed)
        {
            // Each names B's own reviewer, which a chain that was read would refuse.
            var answered = await consult.CallAsync(Question(project), options: new RequestOptions { Meta = Meta(chain.ToJsonString()) });
            Assert.NotEqual(true, answered.IsError);
        }

        var consultations = await hosts.B.ConsultationsAsync(project);
        Assert.Equal(3, consultations.Count);
        Assert.All(consultations, consultation => Assert.Equal(1, consultation.Depth));
    }

    [Fact]
    public async Task A_reviewed_caller_asking_from_a_consultation_cannot_request_a_run()
    {
        const string project = "project-0108";
        hosts.Reset();
        var client = await hosts.B.ConnectAsync(AssistantMcpChainHosts.Member, hosts.A.Application, idempotencyKey: "chain-request-1");
        var request = await ToolAsync(client, AssistantMcpChainHosts.RequestTool("reviewer"));

        var refused = await request.CallAsync(
            new Dictionary<string, object?> { ["input"] = new Dictionary<string, object?> { ["subjectId"] = project, ["message"] = "Review it again." } },
            options: new RequestOptions { Meta = Meta(Chain(1, (hosts.C, "planner"), (hosts.A, "intake"))) });

        // Within one host a consulted agent is never offered a request; across applications its request
        // is refused before a run starts.
        Assert.True(refused.IsError);
        Assert.Equal(NhAssistantErrorCodes.ConsultDepthExceeded, CodeOf(refused));
        await using var context = hosts.B.Context();
        // No test of these hosts starts a run, so the host has none at all.
        Assert.False(await context.Runs.AnyAsync());
    }

    private static object Consult(string project, string question)
    {
        return new { input = new { subjectId = project, question } };
    }

    private static Dictionary<string, object?> Question(string project)
    {
        return new Dictionary<string, object?>
        {
            ["input"] = new Dictionary<string, object?>
            {
                ["subjectId"] = project,
                ["question"] = "What is still open?"
            }
        };
    }

    /// <summary>
    /// The chain as a host sends it: its agents with their applications, outermost first.
    /// </summary>
    private static string Chain(int depth, params (ChainHost Host, string AgentId)[] agents)
    {
        return new JsonObject
        {
            ["agents"] = new JsonArray(agents
                .Select(agent => (JsonNode)new JsonObject { ["application"] = agent.Host.Application, ["agentId"] = agent.AgentId })
                .ToArray()),
            ["consultationDepth"] = depth
        }.ToJsonString();
    }

    /// <summary>
    /// The request metadata of a caller that sends the assistant context with <paramref name="chain"/>.
    /// </summary>
    private static JsonObject Meta(string chain)
    {
        return new JsonObject
        {
            [NhAssistantMcpContext.MetaKey] = new JsonObject
            {
                ["version"] = NhAssistantMcpContext.Version,
                ["invocation"] = new JsonObject { ["invocationId"] = Guid.NewGuid().ToString() },
                ["chain"] = JsonNode.Parse(chain)
            }
        };
    }

    private static async Task<McpClientTool> ToolAsync(McpClient client, string name)
    {
        return Assert.Single(await client.ListToolsAsync(), tool => tool.Name == name);
    }

    private static string? CodeOf(CallToolResult result)
    {
        return result.Meta?[NhAiMcpResultMetadata.CodeKey]?.GetValue<string>();
    }
}

/// <summary>
/// Four applications for <see cref="AssistantMcpChainTests"/>, started once. A reviews B; B reviews A and
/// C; C reviews A and B; D reviews A and allows two consultations per person and day. Each imports the
/// consult tools of its peers once all of them run, so applications can import each other.
/// </summary>
public sealed class AssistantMcpChainHosts : IAsyncLifetime
{
    public const string Member = "member-1";
    public const string Forger = "member-3";
    public const string Unknown = "https://unknown.test";

    private readonly AssistantDatabaseFixture _database = new();
    private ChainHost[] _hosts = [];

    internal ChainHost A => _hosts[0];

    internal ChainHost B => _hosts[1];

    internal ChainHost C => _hosts[2];

    internal ChainHost D => _hosts[3];

    public static string ConsultTool(string agentId)
    {
        return "consult_" + agentId + "_project_v1";
    }

    public static string RequestTool(string agentId)
    {
        return "request_" + agentId + "_project_v1";
    }

    public async Task InitializeAsync()
    {
        var started = await Task.WhenAll(
            ChainHost.StartAsync(_database, "a", ["intake", "reviewer"], [("b", "reviewer"), ("c", "planner")], ["b"]),
            ChainHost.StartAsync(_database, "b", ["reviewer"], [("a", "intake"), ("c", "planner")], ["a", "c"]),
            ChainHost.StartAsync(_database, "c", ["planner"], [("a", "intake"), ("b", "reviewer")], ["a", "b"]),
            ChainHost.StartAsync(_database, "d", ["reviewer"], [], ["a"], dailyLimit: 2));
        _hosts = started;
        foreach (var host in _hosts)
        {
            foreach (var peer in host.PeerNames)
            {
                await host.ImportAsync(_hosts.Single(item => item.Name == peer));
            }
        }
    }

    /// <summary>
    /// Gives every host new scripts and forgets the calls it received.
    /// </summary>
    internal void Reset()
    {
        foreach (var host in _hosts)
        {
            host.Model.Reset();
            host.Inbound.Clear();
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }
}

/// <summary>
/// One application of a consultation chain: an assistant with consultable agents, an MCP endpoint at
/// <c>/mcp</c>, the consult tools of its peers as code-owned imports and a record of the assistant
/// context it received.
/// </summary>
internal sealed class ChainHost : IAsyncDisposable
{
    private readonly Dictionary<string, DeferredMcpCatalog> _imports;
    private readonly ConcurrentBag<McpClient> _clients = new();

    private ChainHost(string name, AssistantWebApplication web, ChainModel model, Dictionary<string, DeferredMcpCatalog> imports, ConcurrentQueue<InboundCall> inbound)
    {
        Name = name;
        Web = web;
        Model = model;
        _imports = imports;
        Inbound = inbound;
    }

    public string Name { get; }

    public string Application => ApplicationOf(Name);

    public AssistantWebApplication Web { get; }

    public ChainModel Model { get; }

    /// <summary>
    /// The assistant context of every MCP tool call the host received, in order.
    /// </summary>
    public ConcurrentQueue<InboundCall> Inbound { get; }

    public IEnumerable<string> PeerNames => _imports.Keys;

    public static string ApplicationOf(string name)
    {
        return "https://" + name + ".test";
    }

    public static async Task<ChainHost> StartAsync(
        AssistantDatabaseFixture database,
        string name,
        string[] agents,
        (string Peer, string AgentId)[] peers,
        string[] reviewed,
        int dailyLimit = 50)
    {
        var model = new ChainModel();
        var inbound = new ConcurrentQueue<InboundCall>();
        var imports = peers
            .GroupBy(peer => peer.Peer)
            .ToDictionary(group => group.Key, group => new DeferredMcpCatalog(group.Key, group.Select(peer => peer.AgentId).ToArray()));
        var web = await AssistantWebApplication.StartAsync(
            database,
            model,
            limits: limits =>
            {
                limits.MaxConsultationDepth = 3;
                limits.MaxConsultationsPerActorPerDay = dailyLimit;
            },
            configure: services =>
            {
                var subjects = new TestSubjects();
                subjects.Roles[AssistantMcpChainHosts.Forger] = NhAiSubjectRole.Participant;
                services.AddSingleton(subjects);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, ConsultationPrincipals>());
                services.AddMcpServer().WithHttpTransport().WithNewHeapPlatformAITools();
                foreach (var import in imports.Values)
                {
                    services.AddSingleton<INhAiToolCatalog>(import);
                }

                // The agents only answer consultations; the startup check would require background
                // operations for their runs.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<TestSubjectResolver>()
                    .UseMcpChainApplication(ApplicationOf(name));
                foreach (var agentId in agents)
                {
                    assistant
                        .AddAgent(AssistantTestData.Agent(agentId) with { ToolSelectors = ["projects.*", "mcp.*"] })
                        .ConfigureAgent(agentId, run => run.AllowConsultation("project", consult => consult
                            .Description("Knows the " + agentId + " view of a project.")
                            .Proposals(false)
                            .AllowRequests()
                            .Exposure(NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp)));
                }

                foreach (var (peer, agentId) in peers)
                {
                    assistant.AddMcpContextBinding(new NhAssistantMcpContextBinding(peer, ApplicationOf(peer) + "/mcp", AssistantMcpChainHosts.ConsultTool(agentId))
                    {
                        IncludeSnapshot = false,
                        IncludeChain = true
                    });
                }

                foreach (var caller in reviewed)
                {
                    assistant.ContinueMcpChain(new NhAssistantMcpChainCaller(ApplicationOf(caller), ApplicationOf(caller)));
                }
            },
            map: app =>
            {
                app.Use((context, next) => RecordAsync(context, next, inbound));
                app.MapMcp("/mcp");
            });
        return new ChainHost(name, web, model, imports, inbound);
    }

    /// <summary>
    /// Imports the consult tools of <paramref name="peer"/> with their assistant context binder, calling
    /// as this application's person.
    /// </summary>
    public async Task ImportAsync(ChainHost peer)
    {
        var import = _imports[peer.Name];
        var client = await peer.ConnectAsync(AssistantMcpChainHosts.Member, Application);
        _clients.Add(client);
        var policies = import.AgentIds
            .Select(agentId => new NhAiMcpImportedToolPolicy(
                AssistantMcpChainHosts.ConsultTool(agentId),
                "consult-" + agentId,
                "Consults the " + agentId + " of application " + peer.Name + " about one project.",
                NhAiToolEffect.ReadOnly,
                NhAiToolExposure.Agent,
                [AssistantWebApplication.AccessPolicy])
            {
                Approval = NhAiApprovalRequirement.NotRequired,
                InvocationBinderId = NhAssistantMcpContext.BinderId,
                Timeout = TimeSpan.FromMinutes(2),
                MaxResultBytes = 1_048_576
            })
            .ToArray();
        var importer = Web.Services.GetRequiredService<INhAiMcpClientToolImporter>();
        import.Use(importer.Import(await client.ListToolsAsync(), new NhAiMcpImportOptions(peer.Name, peer.Name, policies)));
    }

    /// <summary>
    /// An MCP client of this host for <paramref name="user"/>, whose token carries
    /// <paramref name="issuer"/>.
    /// </summary>
    public async Task<McpClient> ConnectAsync(string user, string? issuer, string? idempotencyKey = null)
    {
        var http = Web.CreateClient(user);
        if (issuer is not null)
        {
            http.DefaultRequestHeaders.Add(AssistantWebApplication.IssuerHeader, issuer);
        }
        if (idempotencyKey is not null)
        {
            http.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        }

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                http,
                ownsHttpClient: true),
            new McpClientOptions { ProtocolVersion = "2025-11-25" });
        _clients.Add(client);
        return client;
    }

    /// <summary>
    /// Starts a conversation of the member with <paramref name="agentId"/> and sends one message.
    /// </summary>
    public async Task ChatAsync(string agentId, string text)
    {
        using var client = Web.CreateClient(AssistantMcpChainHosts.Member);
        using var created = await client.PostAsync("/api/assistant/conversations", ServerSentEventReader.Json(new { agentId }));
        created.EnsureSuccessStatusCode();
        using var conversation = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = conversation.RootElement.GetProperty("id").GetString();
        using var turn = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            ServerSentEventReader.Json(new { text, clientMessageId = Guid.NewGuid().ToString("N") }));
        turn.EnsureSuccessStatusCode();
        var (events, _) = await ServerSentEventReader.ReadAllAsync(turn);
        Assert.Equal("completed", events.Single(evt => evt.Name == "turn.completed").Data.GetProperty("status").GetString());
    }

    public NhAssistantDbContext Context()
    {
        return Web.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
    }

    public async Task<List<AssistantConsultation>> ConsultationsAsync(string subjectId)
    {
        await using var context = Context();
        return await context.Consultations
            .Where(consultation => consultation.SubjectId == subjectId)
            .OrderBy(consultation => consultation.CreatedAt)
            .ToListAsync();
    }

    public Guid ConsultationIdOf(string subjectId)
    {
        using var context = Context();
        return context.Consultations.Single(consultation => consultation.SubjectId == subjectId).Id;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        await Web.DisposeAsync();
    }

    /// <summary>
    /// Records the assistant context of an MCP tool call before the MCP endpoint reads the request.
    /// </summary>
    private static async Task RecordAsync(HttpContext context, Func<Task> next, ConcurrentQueue<InboundCall> inbound)
    {
        if (context.Request.Path.StartsWithSegments("/mcp") && HttpMethods.IsPost(context.Request.Method))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            if (JsonNode.Parse(body) is JsonObject message
                && message["method"]?.GetValue<string>() == "tools/call")
            {
                inbound.Enqueue(new InboundCall(
                    message["params"]?["name"]?.GetValue<string>() ?? string.Empty,
                    message["params"]?["_meta"]?[NhAssistantMcpContext.MetaKey]?.DeepClone() as JsonObject));
            }
        }

        await next();
    }
}

internal sealed record InboundCall(string Tool, JsonObject? Context);

/// <summary>
/// A host's model with a script for chat turns and one for consultation turns, which can run inside a
/// chat turn of the same host.
/// </summary>
internal sealed class ChainModel : IChatClient
{
    private const string ConsultedMarker = "You are being consulted";

    public NhAiScriptedChatClient Chat { get; private set; } = new();

    public NhAiScriptedChatClient Consulted { get; private set; } = new();

    public void Reset()
    {
        Chat = new NhAiScriptedChatClient();
        Consulted = new NhAiScriptedChatClient();
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var received = messages.ToArray();
        return Route(received).GetResponseAsync(received, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var received = messages.ToArray();
        return Route(received).GetStreamingResponseAsync(received, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private NhAiScriptedChatClient Route(IReadOnlyList<ChatMessage> messages)
    {
        return messages.Any(message => message.Role == ChatRole.System && message.Text.Contains(ConsultedMarker, StringComparison.Ordinal))
            ? Consulted
            : Chat;
    }
}

/// <summary>
/// The imported MCP tools of one peer, which exist once the peer runs: until then the catalog is empty.
/// </summary>
internal sealed class DeferredMcpCatalog(string peer, IReadOnlyList<string> agentIds) : INhAiToolCatalog
{
    private readonly INhAiToolCatalog _empty = new NhAiMcpClientToolImporter().Import([], new NhAiMcpImportOptions(peer, peer, []));
    private volatile INhAiToolCatalog? _imported;

    public IReadOnlyList<string> AgentIds { get; } = agentIds;

    private INhAiToolCatalog Current => _imported ?? _empty;

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors => Current.Descriptors;

    public NhAiToolCatalogManifest Manifest => Current.Manifest;

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        return Current.CreateFunctions(services);
    }

    public void Use(INhAiToolCatalog imported)
    {
        _imported = imported;
    }
}
