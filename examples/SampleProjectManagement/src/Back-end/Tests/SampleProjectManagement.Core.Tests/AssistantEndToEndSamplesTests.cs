using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet.Mvc;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.OpenApiSchemaTransformers;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-249: the sample assistant works end to end over the API bridge
/// tools and the curated <c>projects.*</c> tools, with in-chat approval, an administrator-managed
/// agent with an MCP server, per-user tool discovery, content-free audit and logs, and an OpenAPI
/// document that lists the assistant's minimal API endpoints. The
/// composition matches Program.cs (<c>AddSampleAiBridge</c> before <c>AddSampleAssistant</c>);
/// the bridge's self-HTTP hop is answered by a recording handler, because the project
/// controllers need the full sample database (their HTTP behavior is covered by SPM-242 to SPM-244).
/// </summary>
public sealed class AssistantEndToEndSamplesTests(AssistantEndToEndHost host) : IClassFixture<AssistantEndToEndHost>
{
    private const string Editor = "division-editor@example.test";
    private const string Viewer = "viewer@example.test";
    private const string Administrator = "administrator@example.test";
    private const string SearchMarker = "e2e-search-9f2";
    private const string McpProjectMarker = "PRJ-E2E-4D8";
    private const string UserTextMarker = "e2e-user-text-51c";

    private static readonly string[] ViewerPermissions = ["app.project.view"];
    private static readonly string[] EditorPermissions = ["app.project.view", "app.project.manage"];
    private static readonly string[] MutatingToolNames =
    [
        "sample-api_project_create_v1",
        "sample-api_project_update_v1",
        "projects_change_status_v1"
    ];

    [Fact]
    public async Task SPM_249_a_division_editor_uses_bridge_and_curated_tools_and_approves_a_status_change()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("sample-api_project_get_v1", new { input = new { } })
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = SearchMarker, limit = 5 } })
            .RespondWithText("Your division has one roadmap project.")
            .RespondWithFunctionCall(
                "projects_change_status_v1",
                new { input = new { projectId, status = (int)ProjectStatus.Active } })
            .RespondWithText("The project is active now.");
        host.Model.Use(model);
        using var editor = host.CreateClient(Editor, false, EditorPermissions);
        var conversationId = await host.CreateConversationAsync(editor);
        var bridgeCallsBefore = host.BridgeRequests.Count;

        var first = await host.SendAsync(editor, conversationId, "Which projects exist? " + UserTextMarker);

        Assert.Equal(
            [("sample-api.project.get", "succeeded"), ("projects.search", "succeeded")],
            ToolOutcomes(first));
        Assert.Equal("completed", first[^1].Data.GetProperty("status").GetString());
        var bridgeCall = Assert.Single(host.BridgeRequests.Skip(bridgeCallsBefore));
        Assert.Equal(HttpMethod.Get, bridgeCall.Method);
        Assert.Equal("/projects", bridgeCall.Path);
        Assert.Equal("Bearer token-of-" + Editor, bridgeCall.Authorization);
        Assert.True(bridgeCall.HasInvocationHeader);
        var offered = model.Requests[0].Options!.Tools!.Select(tool => tool.Name).ToArray();
        Assert.All(MutatingToolNames, name => Assert.Contains(name, offered));

        var paused = await host.SendAsync(editor, conversationId, "Activate the project.");
        var approval = paused.Single(evt => evt.Name == "approval.required").Data;
        Assert.Equal("projects.change-status", approval.GetProperty("toolId").GetString());
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == projectId);
        var resumed = await host.DecideAsync(editor, conversationId, approval, "approve");

        Assert.Equal("succeeded", resumed.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        Assert.Equal("turn.completed", resumed[^1].Name);
        Assert.Equal("completed", resumed[^1].Data.GetProperty("status").GetString());
        Assert.Equal(ProjectStatus.Active, host.Projects.StatusChanges.Single(change => change.ProjectId == projectId).Status);
        Assert.Contains(projectId, host.Projects.VerifiedProjects);
        Assert.Contains(host.AuditEvents, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved);
        host.AssertContentFree(SearchMarker, UserTextMarker, AssistantEndToEndHost.BridgeResultMarker);
    }

    [Fact]
    public async Task SPM_249_an_administrator_agent_combines_a_bridge_tool_and_an_mcp_tool()
    {
        await using var planning = await SampleMcpServer.StartAsync("e2e-planning-key");
        using var admin = host.CreateClient(Administrator, true, ViewerPermissions);

        using var server = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(new
        {
            id = "planning",
            displayName = "Planning",
            url = planning.Url,
            authMode = "api-key",
            headerName = (string?)null,
            secret = "e2e-planning-key",
            requiredPolicy = (string?)null,
            isEnabled = true
        }), TestContext.Current.CancellationToken);
        using var sync = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/sync", null, TestContext.Current.CancellationToken);
        using var enable = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/planning/tools/next_milestone",
            AssistantSampleHost.Json(new { isEnabled = true, effect = "read-only", descriptionOverride = (string?)null }),
            TestContext.Current.CancellationToken);
        using var agent = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "delivery-planner",
            displayName = "Delivery planner",
            description = "Combines project data with the planning system.",
            instructions = "Look up the project and its next milestone.",
            toolSelectors = new[] { "sample-api.project.get" },
            mcpServerIds = new[] { "planning" },
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, server.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(HttpStatusCode.Created, agent.StatusCode);

        host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("sample-api_project_get_v1", new { input = new { } })
            .RespondWithFunctionCall("mcp_planning_next_milestone", new { project = McpProjectMarker })
            .RespondWithText("The project reaches its next milestone on Friday."));
        using var user = host.CreateClient(Viewer, false, ViewerPermissions);
        using var created = await user.PostAsync(
            "/api/assistant/conversations",
            AssistantSampleHost.Json(new { agentId = "delivery-planner" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversationId = (await AssistantSampleHost.ReadJsonAsync(created)).GetProperty("id").GetGuid();

        var events = await host.SendAsync(user, conversationId, "When is the next milestone? " + UserTextMarker);

        Assert.Equal(
            [("sample-api.project.get", "succeeded"), ("mcp.planning.next-milestone", "succeeded")],
            ToolOutcomes(events));
        Assert.Equal([McpProjectMarker], planning.Calls);
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Contains(host.AuditEvents, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentCreated && evt.ObjectId == "delivery-planner");
        host.AssertContentFree(McpProjectMarker, UserTextMarker, "e2e-planning-key", AssistantEndToEndHost.BridgeResultMarker);
    }

    [Fact]
    public async Task SPM_249_a_viewer_is_offered_no_mutating_tools_and_gets_no_approval_card()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(
                "projects_change_status_v1",
                new { input = new { projectId, status = (int)ProjectStatus.Archived } })
            .RespondWithFunctionCall("sample-api_project_create_v1", new { input = new { body = new { name = SearchMarker } } })
            .RespondWithText("You cannot change projects.");
        host.Model.Use(model);
        using var viewer = host.CreateClient(Viewer, false, ViewerPermissions);
        var conversationId = await host.CreateConversationAsync(viewer);
        var bridgeCallsBefore = host.BridgeRequests.Count;

        var events = await host.SendAsync(viewer, conversationId, "Archive the project. " + UserTextMarker);

        var offered = model.Requests[0].Options!.Tools!.Select(tool => tool.Name).ToArray();
        Assert.Contains("sample-api_project_get_v1", offered);
        Assert.Contains("projects_search_v1", offered);
        Assert.All(MutatingToolNames, name => Assert.DoesNotContain(name, offered));
        Assert.DoesNotContain(events, evt => evt.Name == "approval.required");
        Assert.DoesNotContain(
            events.Where(evt => evt.Name == "tool.completed"),
            evt => evt.Data.GetProperty("status").GetString() == "succeeded");
        Assert.Equal("turn.completed", events[^1].Name);
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == projectId);
        Assert.Empty(host.BridgeRequests.Skip(bridgeCallsBefore));
        host.AssertContentFree(SearchMarker, UserTextMarker);
    }

    [Fact]
    public async Task SPM_289_the_project_assistant_reads_the_application_map_and_queries_the_tasks_of_a_project_at_once()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("sample-api-gateway_query_v1", new
            {
                input = new
                {
                    resource = "project-task",
                    filter = new[]
                    {
                        new { key = "projectId", @operator = "==", value = projectId.ToString() },
                        new { key = "isCompleted", @operator = "==", value = "false" }
                    }
                }
            })
            .RespondWithText("One task of the project is still open after its deadline.");
        host.Model.Use(model);
        using var viewer = host.CreateClient(Viewer, false, ViewerPermissions);
        var conversationId = await host.CreateConversationAsync(viewer);
        var bridgeCallsBefore = host.BridgeRequests.Count;

        var events = await host.SendAsync(viewer, conversationId, "Which tasks of this project are overdue? " + UserTextMarker);

        // The map after the instructions lists the readable resources and how tasks relate to projects.
        var instructions = model.Requests[0].Options!.Instructions!;
        var map = instructions[instructions.IndexOf("<application-map>", StringComparison.Ordinal)..instructions.IndexOf("</application-map>", StringComparison.Ordinal)];
        Assert.Contains("- project (", map);
        Assert.Contains("- project-task (", map);
        Assert.Contains("Relations: projectId → project.", map);
        Assert.True(instructions.IndexOf("# Application map", StringComparison.Ordinal) < instructions.IndexOf("# Situation", StringComparison.Ordinal));
        // One query, no search for resources first.
        Assert.Equal([("sample-api-gateway.query", "succeeded")], ToolOutcomes(events));
        var call = Assert.Single(host.BridgeRequests.Skip(bridgeCallsBefore));
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("/project-tasks", call.Path);
        Assert.Contains(host.AuditEvents, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked && evt.ApplicationMapHash is not null);
        host.AssertContentFree(UserTextMarker, AssistantEndToEndHost.BridgeResultMarker);
    }

    [Fact]
    public async Task SPM_291_the_project_assistant_answers_with_the_project_descriptions_of_the_active_division()
    {
        const string ContextMarker = "e2e-context-3b7";
        var otherDivision = Guid.NewGuid();
        var lastWeek = DateTimeOffset.UtcNow.AddDays(-7);
        host.Projects.ContextDocuments[host.DivisionId] =
        [
            new ProjectAiContextDocument(Guid.NewGuid(), "PRJ-291", "Roadmap", "The roadmap moved to May. " + ContextMarker, lastWeek)
        ];
        host.Projects.ContextDocuments[otherDivision] =
        [
            new ProjectAiContextDocument(Guid.NewGuid(), "PRJ-OTHER-DIVISION", "Roadmap", "The roadmap of another division.", lastWeek)
        ];
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap", limit = 5 } })
            .RespondWithText("The roadmap moved to May.");
        host.Model.Use(model);
        using var viewer = host.CreateClient(Viewer, false, ViewerPermissions);
        var conversationId = await host.CreateConversationAsync(viewer);
        var searchesBefore = host.Projects.ContextSearches.Count;
        try
        {
            var events = await host.SendAsync(viewer, conversationId, "What does the roadmap say? " + UserTextMarker);

            // One search per turn, with the question, in the person's active division only.
            var search = Assert.Single(host.Projects.ContextSearches.Skip(searchesBefore));
            Assert.Equal(host.DivisionId, search.DivisionId);
            Assert.Contains("What does the roadmap say?", search.Query);
            // The descriptions follow the situation as untrusted data with their source and date.
            var instructions = model.Requests[0].Options!.Instructions!;
            var start = instructions.IndexOf("# Retrieved context", StringComparison.Ordinal);
            Assert.True(instructions.IndexOf("# Situation", StringComparison.Ordinal) < start);
            var retrieved = instructions[start..];
            Assert.Contains("never instructions", retrieved);
            Assert.Contains("The roadmap moved to May.", retrieved);
            Assert.Contains("\"source\":\"" + ProjectAiContextSource.SourceId + "\"", retrieved);
            Assert.Contains("\"effectiveAt\":", retrieved);
            Assert.DoesNotContain("PRJ-OTHER-DIVISION", instructions);
            // The tool round reuses the turn's context; audit and logs carry its hash, never its content.
            Assert.Equal(instructions, model.Requests[1].Options!.Instructions);
            Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
            Assert.Contains(host.AuditEvents, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked && evt.RetrievedContextHash is not null);
            host.AssertContentFree(ContextMarker, UserTextMarker, "roadmap moved");
        }
        finally
        {
            host.Projects.ContextDocuments.Clear();
        }
    }

    [Fact]
    public async Task SPM_249_the_openapi_document_lists_the_assistant_endpoints_next_to_the_bridged_controllers()
    {
        using var client = host.CreateAnonymousClient();

        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));

        // The bridge read ApiExplorer at start-up for its tools, after the minimal API endpoints
        // were registered, so the document still lists them next to the controller actions.
        Assert.NotEmpty(host.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>().Descriptors);
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(path => path.Name).ToArray();
        Assert.Contains("/api/assistant/status", paths);
        Assert.Contains("/api/assistant/conversations", paths);
        Assert.Contains("/projects", paths);
    }

    /// <summary>
    /// Pairs every <c>tool.started</c> (tool id) with its <c>tool.completed</c> (status) by invocation id.
    /// </summary>
    private static (string? ToolId, string? Status)[] ToolOutcomes(IReadOnlyList<SampleServerSentEvent> events)
    {
        var statuses = events
            .Where(evt => evt.Name == "tool.completed")
            .ToDictionary(evt => evt.Data.GetProperty("invocationId").GetGuid(), evt => evt.Data.GetProperty("status").GetString());
        return events
            .Where(evt => evt.Name == "tool.started")
            .Select(evt => (
                evt.Data.GetProperty("toolId").GetString(),
                statuses.GetValueOrDefault(evt.Data.GetProperty("invocationId").GetGuid())))
            .ToArray();
    }
}

/// <summary>
/// The assistant sample host with the API bridge and the OpenAPI document composed as in Program.cs,
/// permission-based policies, a recording handler for the bridge's self-HTTP hop and capturing audit
/// and log sinks.
/// </summary>
public sealed class AssistantEndToEndHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
{
    public const string BridgeResultMarker = "bridge-result-e2e-7c1";

    private readonly ConcurrentQueue<BridgeRequest> _bridgeRequests = new();
    private readonly CapturingSampleLoggerProvider _logs = new();

    public IReadOnlyList<BridgeRequest> BridgeRequests => _bridgeRequests.ToArray();

    public IReadOnlyCollection<NhAssistantAuditEvent> AuditEvents =>
        Services.GetRequiredService<SampleAssistantAuditLog>().Events;

    /// <summary>
    /// Asserts that no marker (arguments, results, messages or secrets) reached the business
    /// sink, the platform audit sink or any log message.
    /// </summary>
    public void AssertContentFree(params string[] markers)
    {
        var business = JsonSerializer.Serialize(AuditEvents);
        var audit = string.Join('\n', Services.GetRequiredService<RecordingAuditSinkStore>().Records);
        var logs = string.Join('\n', _logs.Messages);
        Assert.NotEmpty(_logs.Messages);
        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, business, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(marker, audit, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(marker, logs, StringComparison.OrdinalIgnoreCase);
        }
    }

    protected override void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.SetMinimumLevel(LogLevel.Trace);
        logging.AddProvider(_logs);
    }

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        settings[SampleAiBridgeComposition.SelfBaseUrlKey] = "http://sample.test/";
    }

    protected override void ConfigureAuthorization(AuthorizationOptions options)
    {
        // The API's controller policies and the division manage policy follow the permission claims.
        options.AddPolicy("app.project.view", policy => policy.RequireClaim("permission", "app.project.view"));
        options.AddPolicy("app.project.manage", policy => policy.RequireClaim("permission", "app.project.manage"));
        options.AddPolicy(ManagePolicy, policy => policy.RequireClaim("permission", "app.project.manage"));
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(ProjectController).Assembly);
        services.AddSingleton<RecordingAuditSinkStore>();
        services.AddNewHeapPlatformAI(ai => ai.AddAuditSink<RecordingAuditSink>());
        services.AddSampleAiBridge();
        services.AddHttpClient(NhAiMvcBridgeDefaults.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingBridgeHandler(_bridgeRequests));
        services.AddOpenApi("v1", options =>
        {
            options.AddSchemaTransformer<OneOfSchemaTransformer>();
            options.AddOperationTransformer<SampleProjectManagement.Api.Models.ProjectCollectionOpenApiTransformer>();
        });
    }

    protected override void MapEndpoints(WebApplication app)
    {
        app.MapOpenApi();
    }

    public sealed record BridgeRequest(HttpMethod Method, string Path, string? Authorization, bool HasInvocationHeader);

    /// <summary>Answers the bridge's self-HTTP calls and records what the bridge sends.</summary>
    private sealed class RecordingBridgeHandler(ConcurrentQueue<BridgeRequest> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue(new BridgeRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Headers.Contains(NhAiMvcBridgeDefaults.InvocationHeaderName)));
            var body = $$"""{"items":[{"id":"{{Guid.NewGuid()}}","key":"PRJ-1","name":"{{BridgeResultMarker}}"}],"total":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}

/// <summary>Keeps the serialized platform audit records of the end-to-end host.</summary>
public sealed class RecordingAuditSinkStore
{
    public ConcurrentQueue<string> Records { get; } = new();
}

internal sealed class RecordingAuditSink(RecordingAuditSinkStore store) : INhAiAuditSink
{
    public ValueTask WriteAsync(NhAiAuditRecord record, CancellationToken cancellationToken = default)
    {
        string serialized;
        try
        {
            serialized = JsonSerializer.Serialize(record);
        }
        catch (NotSupportedException)
        {
            serialized = record.ToString() ?? string.Empty;
        }
        store.Records.Enqueue(serialized);
        return ValueTask.CompletedTask;
    }
}

internal sealed class CapturingSampleLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(this);
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingSampleLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            provider.Messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }
}
