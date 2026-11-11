using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-222: the API's MCP exposure enables MCP tasks, so a client with the MCP
/// Tasks extension calls the long-running <c>projects.analyze-portfolio</c> as a task and polls the
/// finished analysis, while a client without tasks gets the analysis it started and polls
/// <c>projects.portfolio-analysis-status</c> as before. A task lives in the assistant's store on
/// PostgreSQL, exists only for the person and division that started it and expires on the
/// application's clock.
/// </summary>
public sealed class McpTaskSamplesTests(McpTaskSampleHost host) : IClassFixture<McpTaskSampleHost>
{
    private const string AnalyzeTool = "projects_analyze_portfolio_v1";
    private const string StatusTool = "projects_portfolio_analysis_status_v1";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task SPM_222_a_client_with_tasks_gets_the_finished_analysis_and_a_client_without_them_polls_the_status_tool()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var client = await host.ConnectAsync("spm-222-ada", "2026-07-28");

        var created = await client.CallToolAsTaskAsync(Analyze("request-1"), cancellationToken);

        Assert.True(created.IsTask);
        var taskId = created.TaskCreated!.TaskId;
        Assert.IsType<WorkingTaskResult>(await client.GetTaskAsync(taskId, cancellationToken));
        var analysis = await host.Analyses.StartedAsync("spm-222-ada", "request-1");
        host.Analyses.Finish(analysis);
        var completed = Assert.IsType<CompletedTaskResult>(await PollUntilEndedAsync(client, taskId));
        var finished = Data(completed.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)!);
        Assert.Equal(analysis, finished.GetProperty("analysisId").GetGuid());
        Assert.Equal(ProjectPortfolioAnalysisStatuses.Succeeded, finished.GetProperty("status").GetString());

        // A client of the protocol revision before tasks gets the analysis it started and polls it.
        await using var legacy = await host.ConnectAsync("spm-222-ada", "2025-11-25");
        var started = Data(await legacy.CallToolAsync(AnalyzeTool, Input(new { requestId = "request-2" }), cancellationToken: cancellationToken));
        Assert.Equal(ProjectPortfolioAnalysisStatuses.Running, started.GetProperty("status").GetString());
        Assert.False(started.GetProperty("finished").GetBoolean());
        var analysisId = started.GetProperty("analysisId").GetGuid();
        var polled = Data(await legacy.CallToolAsync(StatusTool, Input(new { analysisId }), cancellationToken: cancellationToken));
        Assert.Equal(ProjectPortfolioAnalysisStatuses.Running, polled.GetProperty("status").GetString());

        host.Analyses.Finish(analysisId);
        var done = Data(await legacy.CallToolAsync(StatusTool, Input(new { analysisId }), cancellationToken: cancellationToken));
        Assert.True(done.GetProperty("finished").GetBoolean());

        // The same request id finds the same analysis again instead of starting a second one.
        var again = Data(await legacy.CallToolAsync(AnalyzeTool, Input(new { requestId = "request-2" }), cancellationToken: cancellationToken));
        Assert.Equal(analysisId, again.GetProperty("analysisId").GetGuid());
    }

    [Fact]
    public async Task SPM_222_a_task_exists_only_for_the_person_and_division_that_started_it_until_it_expires()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var owner = await host.ConnectAsync("spm-222-bea", "2026-07-28");
        var created = await owner.CallToolAsTaskAsync(Analyze("request-3"), cancellationToken);
        var taskId = created.TaskCreated!.TaskId;

        await using var other = await host.ConnectAsync("spm-222-cas", "2026-07-28");
        var refused = await Assert.ThrowsAnyAsync<McpProtocolException>(async () => await other.GetTaskAsync(taskId, cancellationToken));
        Assert.Equal(McpErrorCode.InvalidParams, refused.ErrorCode);
        await using var otherDivision = await host.ConnectAsync("spm-222-bea", "2026-07-28", Guid.NewGuid());
        await Assert.ThrowsAnyAsync<McpProtocolException>(async () => await otherDivision.GetTaskAsync(taskId, cancellationToken));

        host.Analyses.Finish(await host.Analyses.StartedAsync("spm-222-bea", "request-3"));
        Assert.IsType<CompletedTaskResult>(await PollUntilEndedAsync(owner, taskId));

        // Ten minutes of the tool's timeout, a minute's margin and the hour a task stays.
        host.Clock.Advance(TimeSpan.FromSeconds(600) + TimeSpan.FromMinutes(1) + TimeSpan.FromHours(1));

        var expired = await Assert.ThrowsAnyAsync<McpProtocolException>(async () => await owner.GetTaskAsync(taskId, cancellationToken));
        Assert.Equal(McpErrorCode.InvalidParams, expired.ErrorCode);
    }

    private static async Task<GetTaskResult> PollUntilEndedAsync(McpClient client, string taskId)
    {
        using var patience = new CancellationTokenSource(Patience);
        while (true)
        {
            var task = await client.GetTaskAsync(taskId, patience.Token);
            if (task is not WorkingTaskResult)
            {
                return task;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), patience.Token);
        }
    }

    private static JsonElement Data(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return result.StructuredContent!.Value.GetProperty("data");
    }

    private static CallToolRequestParams Analyze(string requestId)
    {
        return new CallToolRequestParams
        {
            Name = AnalyzeTool,
            Arguments = new Dictionary<string, JsonElement>
            {
                ["input"] = JsonSerializer.SerializeToElement(new { requestId })
            }
        };
    }

    private static Dictionary<string, object?> Input(object input)
    {
        return new Dictionary<string, object?> { ["input"] = input };
    }
}

/// <summary>
/// The assistant sample host with the API's MCP endpoint as Program.cs maps it, with tasks, the
/// portfolio analysis tools of <see cref="SamplePortfolioAnalysisToolsComposition"/> over a stand-in for
/// the background operation that the test finishes, and the sample's test clock.
/// </summary>
public sealed class McpTaskSampleHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
{
    public SampleTestClock Clock { get; } = new();

    public FakePortfolioAnalyses Analyses { get; } = new();

    public async Task<McpClient> ConnectAsync(string user, string protocolVersion, Guid? divisionId = null)
    {
        var http = CreateClient(user);
        if (divisionId is { } other)
        {
            http.DefaultRequestHeaders.Remove("X-NH-ActiveDivisionId");
            http.DefaultRequestHeaders.Add("X-NH-ActiveDivisionId", other.ToString());
        }

        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(BaseAddress, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                http,
                ownsHttpClient: true),
            new McpClientOptions { ProtocolVersion = protocolVersion });
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IProjectPortfolioAnalyses>(Analyses);
        services.AddSamplePortfolioAnalysisTools();
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithNewHeapPlatformAITools(exposure => exposure.UseTasks());
    }

    protected override void MapEndpoints(WebApplication app)
    {
        base.MapEndpoints(app);
        app.MapMcp("/mcp").RequireAuthorization();
    }
}

/// <summary>
/// The portfolio analysis as the tools see it, standing in for the background operation of SPM-083:
/// one analysis per person and request id, running until the test finishes it.
/// </summary>
public sealed class FakePortfolioAnalyses : IProjectPortfolioAnalyses
{
    private readonly ConcurrentDictionary<(string Actor, string RequestId), Analysis> _byRequest = new();
    private readonly ConcurrentDictionary<Guid, Analysis> _byId = new();
    private readonly ConcurrentDictionary<(string Actor, string RequestId), TaskCompletionSource<Guid>> _started = new();

    public Task<Guid> StartedAsync(string actor, string requestId)
    {
        return Started(actor, requestId).Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    public void Finish(Guid analysisId)
    {
        _byId[analysisId].Done.TrySetResult();
    }

    public Task<TaskResult<ProjectPortfolioAnalysis>> StartAsync(
        Guid divisionId,
        string actorId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var analysis = _byRequest.GetOrAdd((actorId, requestId), _ =>
        {
            var created = new Analysis(Guid.NewGuid(), actorId, divisionId);
            _byId[created.Id] = created;
            return created;
        });
        Started(actorId, requestId).TrySetResult(analysis.Id);
        return Task.FromResult(TaskResult<ProjectPortfolioAnalysis>.Succeeded(analysis.ToView()));
    }

    public Task<TaskResult<ProjectPortfolioAnalysis>> GetAsync(
        Guid divisionId,
        string actorId,
        Guid analysisId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_byId.TryGetValue(analysisId, out var analysis)
            && analysis.Actor == actorId
            && analysis.DivisionId == divisionId
                ? TaskResult<ProjectPortfolioAnalysis>.Succeeded(analysis.ToView())
                : TaskResult<ProjectPortfolioAnalysis>.Failed(
                    ProjectPortfolioAnalysisAiTools.AnalysisNotFoundCode,
                    "The portfolio analysis does not exist for the caller."));
    }

    public async Task<TaskResult<ProjectPortfolioAnalysis>> WaitAsync(
        Guid divisionId,
        string actorId,
        Guid analysisId,
        CancellationToken cancellationToken = default)
    {
        var found = await GetAsync(divisionId, actorId, analysisId, cancellationToken);
        if (!found.Success)
        {
            return found;
        }

        await _byId[analysisId].Done.Task.WaitAsync(cancellationToken);
        return await GetAsync(divisionId, actorId, analysisId, cancellationToken);
    }

    private TaskCompletionSource<Guid> Started(string actor, string requestId)
    {
        return _started.GetOrAdd(
            (actor, requestId),
            _ => new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed record Analysis(Guid Id, string Actor, Guid DivisionId)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProjectPortfolioAnalysis ToView()
        {
            return Done.Task.IsCompleted
                ? new ProjectPortfolioAnalysis(Id, ProjectPortfolioAnalysisStatuses.Succeeded, true)
                : new ProjectPortfolioAnalysis(Id, ProjectPortfolioAnalysisStatuses.Running, false);
        }
    }
}
