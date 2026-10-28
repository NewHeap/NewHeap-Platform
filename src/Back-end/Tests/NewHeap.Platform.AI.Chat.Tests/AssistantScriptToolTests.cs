using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using Microsoft.AspNetCore.Builder;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Scripting;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The script tool of <c>NewHeap.Platform.AI.Scripting</c> in the assistant: offered where the agent
/// allows scripts and the switch has them on, described with the turn's tools, running every call as a
/// step of the turn that the activity shows under the script call, stopping at an inline approval, in a
/// run that a trigger started and over MCP. Both relational providers where a turn persists.
/// </summary>
public sealed class AssistantScriptToolTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Agent = "project-assistant";
    private const string RunAgent = "project-reviewer";
    private const string SearchFunction = "projects_search_v1";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Member = "member-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_script_in_a_persons_chat_chains_the_turns_tools_and_its_calls_appear_in_the_activity(AssistantTestProvider provider)
    {
        const string script = """
            const first = await tools.projects_search_v1({ query: "road" });
            const second = await tools.projects_search_v1({ query: "map" });
            return { names: first.data.map(project => project.name), more: second.data.length };
            """;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script }, "call-script")
            .RespondWithText("The roadmap project.");
        var described = new DescribingChatClient(model);
        await using var host = await CreateHostAsync(provider, described, scripts: true);
        var conversation = await host.CreateConversationAsync();

        await host.SendAsync(conversation.Id, "Which roadmap projects are there?");

        // Offered with the turn's tools as signatures, next to the individual tools; the agent's scripts
        // are read-only, so the change is no tool of a script.
        var offered = model.Requests[0].Options!.Tools!.OfType<AIFunction>().ToArray();
        Assert.Contains(offered, tool => tool.Name == NhAiScriptTool.FunctionName);
        Assert.Contains(offered, tool => tool.Name == SearchFunction);
        var description = described.ScriptDescriptions.First();
        Assert.Contains("projects_search_v1(input", description, StringComparison.Ordinal);
        Assert.DoesNotContain(ChangeStatusFunction + "(input", description, StringComparison.Ordinal);

        // Only the program's value returned to the model.
        var returned = Assert.Single(model.FunctionResults);
        var result = JsonSerializer.SerializeToElement(returned.Result);
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal("""{"names":["Roadmap project"],"more":1}""", result.GetProperty("data").GetProperty("value").GetRawText());
        Assert.Equal(1, result.GetProperty("data").GetProperty("roundsSaved").GetInt32());

        // The script call and each of its calls are rows of the turn; the steps name the script call.
        var rows = await AssistantToolStepTests.RowsAsync(host, conversation.Id);
        var parent = Assert.Single(rows, row => row.FunctionName == NhAiScriptTool.FunctionName);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, parent.Status);
        Assert.Contains("tools.projects_search_v1", parent.ArgumentsJson, StringComparison.Ordinal);
        var steps = rows
            .Where(row => row.CallId.StartsWith(NhAssistantToolStepCallIds.PrefixOf(NhAiToolCallOriginKind.Script, parent.Id), StringComparison.Ordinal))
            .OrderBy(row => row.CallId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([TestProjectToolCatalog.Search.Id, TestProjectToolCatalog.Search.Id], steps.Select(row => row.ToolId).ToArray());
        Assert.All(steps, row => Assert.Equal(NhAssistantToolCallStatuses.Succeeded, row.Status));

        // Every call carries the script's hash and its position; the script call is audited on its own.
        var contexts = host.Tools.Contexts.ToArray();
        Assert.Equal(2, contexts.Length);
        Assert.All(contexts, context =>
        {
            Assert.Equal(NhAiToolCallOriginKind.Script, context.Origin?.Kind);
            Assert.Equal(NhAiScriptTool.ToolId, context.Origin?.ToolId);
            Assert.Equal(NhAiScriptRunner.HashOf(script), context.Origin?.Hash);
            Assert.Equal(parent.Id, context.Origin?.ParentInvocationId);
        });
        Assert.Equal([0, 1], contexts.Select(context => context.Origin!.StepIndex!.Value).ToArray());
        var scriptAudit = Assert.Single(host.Audit.Records, record => record.ToolId == NhAiScriptTool.ToolId);
        Assert.Equal(NhAiOutcomeKind.Succeeded, scriptAudit.Outcome);
        Assert.Equal(parent.Id, scriptAudit.InvocationId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_inline_approval_stops_the_script_where_it_waits_and_the_turn_waits_for_the_decision(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var script = $$"""
            await tools.projects_search_v1({ query: "road" });
            await tools.projects_change_status_v1({ projectId: "{{projectId}}", status: "Closed" });
            await tools.projects_search_v1({ query: "after" });
            return "never";
            """;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script }, "call-script")
            .RespondWithText("The project is closed.");
        await using var host = await CreateHostAsync(provider, model, scripts: true, mutations: 1);
        var conversation = await host.CreateConversationAsync();

        var waiting = await host.SendAsync(conversation.Id, "Close the roadmap project.");

        // The change waits for the person's decision; nothing after it ran.
        var approval = waiting.Single<NhAssistantApprovalRequiredEvent>().Approval;
        Assert.Empty(host.Tools.StatusChanges);
        Assert.Single(host.Tools.Contexts, context => context.Origin?.StepIndex == 0);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, (await host.ReloadAsync(conversation.Id)).Status);
        var rows = await AssistantToolStepTests.RowsAsync(host, conversation.Id);
        var parent = Assert.Single(rows, row => row.FunctionName == NhAiScriptTool.FunctionName);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, parent.Status);
        var waitingStep = Assert.Single(rows, row => row.Status == NhAssistantToolCallStatuses.AwaitingApproval);
        Assert.Equal(NhAssistantToolStepCallIds.Of(NhAiToolCallOriginKind.Script, parent.Id, 1), waitingStep.CallId);
        using (var stored = JsonDocument.Parse(parent.ResultJson!))
        {
            Assert.Equal(NhAiScriptCodes.WaitingForApproval, stored.RootElement.GetProperty("code").GetString());
            Assert.Equal(approval.ProposalId.ToString(), stored.RootElement.GetProperty("data").GetProperty("stoppedAt").GetProperty("proposalId").GetString());
        }

        var decided = await host.DecideAsync(conversation.Id, approval, approve: true);

        Assert.Null(decided.StartCode);
        Assert.Equal(new ProjectStatusInput(projectId, "Closed"), Assert.Single(host.Tools.StatusChanges));
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(conversation.Id)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task With_scripts_switched_off_the_script_tool_is_not_offered_and_a_call_runs_nothing(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script = "return await tools.projects_search_v1({});" }, "call-script")
            .RespondWithText("Searched.");
        await using var host = await CreateHostAsync(provider, model, scripts: false);
        var conversation = await host.CreateConversationAsync();

        await host.SendAsync(conversation.Id, "Search.");

        var offered = (model.Requests[0].Options?.Tools ?? []).Select(tool => tool.Name).ToArray();
        Assert.DoesNotContain(NhAiScriptTool.FunctionName, offered);
        Assert.Contains(SearchFunction, offered);
        Assert.Empty(host.Tools.Contexts);
        Assert.DoesNotContain(host.Audit.Records, record => record.ToolId == NhAiScriptTool.ToolId);
        Assert.DoesNotContain(await AssistantToolStepTests.RowsAsync(host, conversation.Id), row => row.FunctionName == NhAiScriptTool.FunctionName);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_that_a_trigger_started_is_offered_the_script_tool_with_the_runs_tools(AssistantTestProvider provider)
    {
        const string script = "const found = await tools.projects_search_v1({ query: 'road' }); return found.data.length;";
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(NhAiScriptTool.FunctionName, new { script }, "call-script")
            .RespondWithText("Reviewed.");
        var described = new DescribingChatClient(model);
        await using var host = await CreateHostAsync(provider, described, scripts: true, withRuns: true);

        var turn = await RunTriggeredTurnAsync(host, Guid.NewGuid());

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Outcome);
        Assert.Contains(model.Requests[0].Options!.Tools!, tool => tool.Name == NhAiScriptTool.FunctionName);
        Assert.Contains("projects_search_v1(input", described.ScriptDescriptions.First(), StringComparison.Ordinal);
        var context = Assert.Single(host.Tools.Contexts);
        Assert.Equal(NhAiToolCallOriginKind.Script, context.Origin?.Kind);
        Assert.Equal(RunAgent, context.AgentId);
        var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        Assert.Equal("1", result.GetProperty("data").GetProperty("value").GetRawText());
    }

    [Fact]
    public async Task Over_mcp_the_script_tool_runs_the_callers_tools_only_and_follows_the_switch()
    {
        var switchState = new TestCompositionSwitchState();
        await using var host = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(switchState);
                services.AddSingleton<ScriptMcpLookups>();
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ScriptMcpLookupCatalog>());
                services.AddNewHeapPlatformAIScripting();
                services.AddMcpServer()
                    .WithHttpTransport(options => options.Stateless = true)
                    .WithNewHeapPlatformAITools(mcp => mcp.AllowScripts());
            },
            assistantBuilder: assistant => assistant.UseCompositionSwitch<TestCompositionSwitch>(),
            map: app => app.MapMcp("/mcp"));
        await using var client = await ConnectAsync(host.CreateClient(Member));

        // Listed with the caller's MCP tools as signatures, read-only by default.
        var listed = await client.ListToolsAsync();
        var scriptTool = Assert.Single(listed, tool => tool.Name == NhAiScriptTool.FunctionName);
        Assert.Contains("projects_lookup_v1(input: { query: string })", scriptTool.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(SearchFunction, scriptTool.Description, StringComparison.Ordinal);
        Assert.True(scriptTool.ProtocolTool.Annotations?.ReadOnlyHint);

        var ran = await client.CallToolAsync(
            NhAiScriptTool.FunctionName,
            new Dictionary<string, object?> { ["script"] = "const a = await tools.projects_lookup_v1({ query: 'a' }); const b = await tools.projects_lookup_v1({ query: 'b' }); return a.data.concat(b.data);" });
        Assert.NotEqual(true, ran.IsError);
        Assert.Equal("""["a-1","b-1"]""", ran.StructuredContent!.Value.GetProperty("data").GetProperty("value").GetRawText());
        Assert.Equal(["a", "b"], host.Services.GetRequiredService<ScriptMcpLookups>().Queries.ToArray());
        Assert.All(host.Services.GetRequiredService<ScriptMcpLookups>().Origins, origin => Assert.Equal(NhAiToolCallOriginKind.Script, origin?.Kind));

        // An agent tool that the MCP exposure does not list is no tool of an MCP client's script.
        var agentOnly = await client.CallToolAsync(
            NhAiScriptTool.FunctionName,
            new Dictionary<string, object?> { ["script"] = "return await tools.projects_search_v1({ query: 'a' });" });
        Assert.True(agentOnly.IsError);
        Assert.Equal(NhAiScriptCodes.UnknownTool, agentOnly.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());

        // The switch was asked for the MCP channel and scripts; off, the tool is not listed and refused.
        Assert.All(switchState.Asked, context => Assert.Equal((NhAssistantCompositionChannel.Mcp, NhAiCompositionFeature.Scripts), (context.Channel, context.Feature)));
        switchState.Answer = _ => false;
        Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name == NhAiScriptTool.FunctionName);
        var refused = await client.CallToolAsync(NhAiScriptTool.FunctionName, new Dictionary<string, object?> { ["script"] = "return 1;" });
        Assert.True(refused.IsError);
        Assert.Equal(NhAiCompositionFeatures.ScriptingOffCode, refused.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());
        Assert.Equal(2, host.Services.GetRequiredService<ScriptMcpLookups>().Queries.Count);
    }

    private static async Task<McpClient> ConnectAsync(HttpClient http)
    {
        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                http,
                ownsHttpClient: true));
    }

    private static async Task<NhAssistantAgentTurnResult> RunTriggeredTurnAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var receipt = Assert.Single(await services.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, "created")));
        var runs = services.GetRequiredService<NhAssistantRunStore>();
        var thread = (await runs.FindThreadByConversationAsync(receipt.ConversationId!.Value, CancellationToken.None))!;
        var run = (await runs.FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
        var subject = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), CancellationToken.None))!;
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[RunAgent];
        return await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                new NhAssistantAgentTurnContext(thread, subject, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false),
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Review the project.")]),
            CancellationToken.None);
    }

    private Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        IChatClient model,
        bool scripts,
        int mutations = 0,
        bool withRuns = false)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddNewHeapPlatformAIScripting(scripting => scripting
                    .ConfigureAgent(Agent, limits => limits.MaxMutationsPerScript = mutations));
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["NewHeap:AI:Assistant:Enabled"] = "true",
                        ["NewHeap:AI:Assistant:Scripts"] = scripts ? "true" : "false"
                    })
                    .Build());
                if (withRuns)
                {
                    services.AddSingleton(new TestSubjects());
                    services.AddSingleton<RecordingOperations>();
                    services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                    services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                }
            },
            assistantBuilder: assistant =>
            {
                assistant.ConfigureAgentContext(Agent, context => context.AllowScripts());
                if (withRuns)
                {
                    assistant
                        .AddAgent(AssistantTestData.Agent(RunAgent))
                        .AddSubjectType<TestSubjectResolver>()
                        // A run without a person is granted the script tool as any other tool; its calls
                        // are still authorized one by one.
                        .ConfigureAgent(RunAgent, agent => agent.Grant("projects.*", NhAiScriptTool.ToolId))
                        .ConfigureAgentContext(RunAgent, context => context.AllowScripts())
                        .AddTrigger<ProjectChanged>("project-changed", trigger => trigger
                            .StartAgent(RunAgent)
                            .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                            .Message(evt => "The project changed: " + evt.Change)
                            .Dedupe(evt => evt.EventId.ToString("N")));
                }
            });
    }
}

/// <summary>
/// Reads the script tool's description during each model call, inside the turn, as a model provider
/// does when it sends the tools; the description lists the tools of that turn.
/// </summary>
public sealed class DescribingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public System.Collections.Concurrent.ConcurrentQueue<string> ScriptDescriptions { get; } = new();

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(options);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private void Record(ChatOptions? options)
    {
        foreach (var tool in (options?.Tools ?? []).OfType<AIFunction>().Where(tool => tool.Name == NhAiScriptTool.FunctionName))
        {
            ScriptDescriptions.Enqueue(tool.Description);
        }
    }
}

public sealed record ScriptLookupInput(string Query);

/// <summary>
/// What the MCP lookup tool of the script tests was asked, with the origin of each call.
/// </summary>
public sealed class ScriptMcpLookups
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Queries { get; } = new();

    public System.Collections.Concurrent.ConcurrentQueue<NhAiToolCallOrigin?> Origins { get; } = new();
}

/// <summary>
/// One read tool that the MCP exposure lists, governed by the shared invoker.
/// </summary>
public sealed class ScriptMcpLookupCatalog : INhAiGeneratedToolCatalog
{
    public static readonly NhAiToolDescriptor Lookup = new(
        "projects.lookup",
        1,
        "Look up project keys.",
        typeof(ScriptLookupInput),
        typeof(IReadOnlyList<string>),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Mcp,
        true,
        [TestProjectToolCatalog.ReadPolicy])
    {
        ExportName = "projects_lookup_v1",
        CatalogId = "script-lookups",
        InputSchemaJson = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""",
        OutputSchemaJson = """{"type":"array","items":{"type":"string"}}""",
        ContractHash = new string('7', 64),
        SchemaHash = new string('7', 64)
    };

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [Lookup];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "script-lookups",
        1,
        new string('8', 64),
        [new NhAiToolManifestEntry(Lookup.Id, 1, Lookup.SchemaHash, Lookup.ContractHash) { ExportName = Lookup.ExportName }]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var lookups = services.GetRequiredService<ScriptMcpLookups>();
        Func<ScriptLookupInput, CancellationToken, Task<TaskResult<IReadOnlyList<string>>>> lookup =
            (input, cancellationToken) => invoker.InvokeAsync(
                Lookup,
                input,
                (context, _) =>
                {
                    lookups.Queries.Enqueue(input.Query);
                    lookups.Origins.Enqueue(context.Origin);
                    IReadOnlyList<string> keys = [input.Query + "-1"];
                    return Task.FromResult(TaskResult<IReadOnlyList<string>>.Succeeded(keys));
                },
                cancellationToken);

        return
        [
            NhAiGovernedAIFunction.Create(
                Lookup,
                AIFunctionFactory.Create(lookup, new AIFunctionFactoryOptions { Name = Lookup.ExportName, Description = Lookup.Description }),
                services)
        ];
    }
}
