using System.Collections.Concurrent;
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
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// <c>UseTasks</c> of <c>WithNewHeapPlatformAITools</c> over real stateless Streamable HTTP on loopback:
/// a client with the MCP Tasks extension gets a task for a tool that declares task support and polls its
/// result, a client without it gets the tool's own answer, and a task exists only for the actor and
/// division that created it, until it expires on the application's clock.
/// </summary>
public sealed class NhAiMcpTaskTests(NhAiMcpTaskTests.Host host) : IClassFixture<NhAiMcpTaskTests.Host>
{
    private const string TasksProtocolVersion = "2026-07-28";
    private const string LegacyProtocolVersion = "2025-11-25";
    private const string ActorHeader = "X-Test-Actor";
    private const string DivisionHeader = "X-NH-ActiveDivisionId";
    private const string AnalyzeTool = "projects_analyze_v1";
    private const string ExportTool = "projects_export_v1";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_client_with_tasks_polls_the_result_and_a_client_without_them_gets_the_tool_answer()
    {
        var cancellationToken = CancellationToken.None;
        await using var client = await host.ConnectAsync("actor-1", TasksProtocolVersion);

        var created = await client.CallToolAsTaskAsync(Call(AnalyzeTool, "alpha"), cancellationToken);

        Assert.True(created.IsTask);
        var taskId = created.TaskCreated!.TaskId;
        Assert.Equal(McpTaskStatus.Working, created.TaskCreated.Status);
        Assert.Equal(2_000, created.TaskCreated.PollIntervalMs);
        Assert.IsType<WorkingTaskResult>(await client.GetTaskAsync(taskId, cancellationToken));

        host.Release("alpha");
        var completed = await PollUntilEndedAsync(client, taskId);

        var result = Assert.IsType<CompletedTaskResult>(completed);
        Assert.Equal("analysed alpha as task " + taskId, Text(result));
        Assert.Equal(taskId, host.SeenTaskId("alpha"));

        // Without the extension: the protocol revision before tasks, and a client that did not opt in.
        host.Release("beta");
        await using var legacy = await host.ConnectAsync("actor-1", LegacyProtocolVersion);
        var plain = await legacy.CallToolAsync(AnalyzeTool, Arguments("beta"), cancellationToken: cancellationToken);
        Assert.Equal("analysed beta directly", Data(plain));

        host.Release("gamma");
        var notOptedIn = await client.CallToolAsync(AnalyzeTool, Arguments("gamma"), cancellationToken: cancellationToken);
        Assert.Equal("analysed gamma directly", Data(notOptedIn));
        Assert.Null(host.SeenTaskId("gamma"));
    }

    [Fact]
    public async Task A_task_does_not_exist_for_another_actor_or_division_and_its_owner_still_polls_and_cancels_it()
    {
        var cancellationToken = CancellationToken.None;
        await using var owner = await host.ConnectAsync("actor-2", TasksProtocolVersion);
        var created = await owner.CallToolAsTaskAsync(Call(AnalyzeTool, "delta"), cancellationToken);
        var taskId = created.TaskCreated!.TaskId;

        await using var other = await host.ConnectAsync("actor-3", TasksProtocolVersion);
        var read = await Assert.ThrowsAnyAsync<McpProtocolException>(async () => await other.GetTaskAsync(taskId, cancellationToken));
        Assert.Equal(McpErrorCode.InvalidParams, read.ErrorCode);
        Assert.Contains("Unknown task", read.Message, StringComparison.Ordinal);
        var cancel = await Assert.ThrowsAnyAsync<McpProtocolException>(async () => await other.CancelTaskAsync(taskId, cancellationToken));
        Assert.Equal(McpErrorCode.InvalidParams, cancel.ErrorCode);

        await using var otherDivision = await host.ConnectAsync("actor-2", TasksProtocolVersion, Guid.NewGuid());
        await Assert.ThrowsAnyAsync<McpProtocolException>(async () => await otherDivision.GetTaskAsync(taskId, cancellationToken));

        // The refused cancellation changed nothing; the owner's own one ends the task and stops the tool.
        Assert.IsType<WorkingTaskResult>(await owner.GetTaskAsync(taskId, cancellationToken));
        await owner.CancelTaskAsync(taskId, cancellationToken);

        Assert.IsType<CancelledTaskResult>(await owner.GetTaskAsync(taskId, cancellationToken));
        Assert.True(await host.WasCancelledAsync("delta"));
    }

    [Fact]
    public async Task A_task_expires_on_the_application_clock_and_a_task_whose_tool_cannot_answer_any_more_is_abandoned()
    {
        var cancellationToken = CancellationToken.None;
        await using var client = await host.ConnectAsync("actor-4", TasksProtocolVersion);
        var finished = await client.CallToolAsTaskAsync(Call(AnalyzeTool, "epsilon"), cancellationToken);
        host.Release("epsilon");
        Assert.IsType<CompletedTaskResult>(await PollUntilEndedAsync(client, finished.TaskCreated!.TaskId));
        var hanging = await client.CallToolAsTaskAsync(Call(AnalyzeTool, "zeta"), cancellationToken);
        Assert.Equal(
            Host.ToolTimeout + Host.AbandonAfter + Host.TimeToLive,
            hanging.TaskCreated!.TimeToLive);

        // The tool's timeout and the margin pass on the application's clock while the tool still waits.
        host.Clock.Advance(Host.ToolTimeout + Host.AbandonAfter);

        var abandoned = Assert.IsType<FailedTaskResult>(await client.GetTaskAsync(hanging.TaskCreated.TaskId, cancellationToken));
        Assert.Equal(NhAiMcpTaskFailureCodes.Abandoned, abandoned.StatusMessage);
        Assert.Equal(
            NhAiMcpTaskFailureCodes.Abandoned,
            abandoned.Error.GetProperty("data").GetProperty("code").GetString());

        // A late answer of the tool never overwrites the outcome.
        host.Release("zeta");
        Assert.Equal(hanging.TaskCreated.TaskId, await host.SeenTaskIdAsync("zeta"));
        Assert.IsType<FailedTaskResult>(await client.GetTaskAsync(hanging.TaskCreated.TaskId, cancellationToken));

        host.Clock.Advance(Host.TimeToLive);

        foreach (var taskId in new[] { finished.TaskCreated.TaskId, hanging.TaskCreated.TaskId })
        {
            var expired = await Assert.ThrowsAnyAsync<McpProtocolException>(async () => await client.GetTaskAsync(taskId, cancellationToken));
            Assert.Equal(McpErrorCode.InvalidParams, expired.ErrorCode);
        }
    }

    [Fact]
    public async Task A_tool_that_requires_tasks_refuses_a_client_without_them_and_bounds_the_kept_result()
    {
        var cancellationToken = CancellationToken.None;
        await using var legacy = await host.ConnectAsync("actor-5", LegacyProtocolVersion);

        var refused = await Assert.ThrowsAnyAsync<McpProtocolException>(async () =>
            await legacy.CallToolAsync(ExportTool, Arguments("small"), cancellationToken: cancellationToken));

        Assert.Equal(McpErrorCode.MissingRequiredClientCapability, refused.ErrorCode);

        await using var client = await host.ConnectAsync("actor-5", TasksProtocolVersion);
        var small = await client.CallToolAsTaskAsync(Call(ExportTool, "small"), cancellationToken);
        var large = await client.CallToolAsTaskAsync(Call(ExportTool, "large"), cancellationToken);

        Assert.Equal("exported small", Text(Assert.IsType<CompletedTaskResult>(await PollUntilEndedAsync(client, small.TaskCreated!.TaskId))));
        var bounded = Assert.IsType<CompletedTaskResult>(await PollUntilEndedAsync(client, large.TaskCreated!.TaskId));
        var boundedResult = bounded.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)!;
        Assert.True(boundedResult.IsError);
        Assert.Equal(
            NhAiMcpTaskFailureCodes.ResultTooLarge,
            boundedResult.StructuredContent!.Value.GetProperty("code").GetString());
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

            await Task.Delay(TimeSpan.FromMilliseconds(20), patience.Token);
        }
    }

    private static string Text(CompletedTaskResult completed)
    {
        return Data(completed.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)!);
    }

    private static string Data(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return result.StructuredContent!.Value.GetProperty("data").GetString()!;
    }

    private static CallToolRequestParams Call(string tool, string input)
    {
        return new CallToolRequestParams
        {
            Name = tool,
            Arguments = new Dictionary<string, JsonElement> { ["input"] = JsonSerializer.SerializeToElement(input) }
        };
    }

    private static Dictionary<string, object?> Arguments(string input)
    {
        return new Dictionary<string, object?> { ["input"] = input };
    }

    /// <summary>
    /// An MCP server of <c>WithNewHeapPlatformAITools</c> with tasks in this process, over stateless
    /// Streamable HTTP at <c>/mcp</c>, whose resolver reads the actor from a header, on a test clock.
    /// One tool supports tasks and waits until the test releases its input; another requires tasks.
    /// </summary>
    public sealed class Host : IAsyncLifetime
    {
        public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan AbandonAfter = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(30);

        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _seen = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _cancelled = new(StringComparer.Ordinal);
        private WebApplication? _app;

        public AssistantTestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));

        public Guid DivisionId { get; } = Guid.NewGuid();

        private string Endpoint => LocalhostTestServer.Origin(
            _app ?? throw new InvalidOperationException("The host did not start.")) + "/mcp";

        public void Release(string input)
        {
            Gate(input).TrySetResult();
        }

        /// <summary>The task id the tool saw for <paramref name="input"/>, once it ran.</summary>
        public string? SeenTaskId(string input)
        {
            var seen = Seen(input).Task;
            return seen.IsCompleted ? seen.Result : null;
        }

        public Task<string?> SeenTaskIdAsync(string input)
        {
            return Seen(input).Task.WaitAsync(Patience);
        }

        public async Task<bool> WasCancelledAsync(string input)
        {
            await Cancelled(input).Task.WaitAsync(Patience);
            return true;
        }

        public async Task InitializeAsync()
        {
            _app = await LocalhostTestServer.StartAsync(port =>
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls($"http://localhost:{port}");
                builder.Logging.ClearProviders();
                var services = builder.Services;
                services.AddAuthorization();
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<INhAiToolCatalog>(new LongRunningCatalog(this));
                services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
                services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
                services.AddNewHeapPlatformAIAspNet(ai => ai
                    .UseToolInvocationPurpose("tool-invocation")
                    .UseAuthenticatedInvocationContextResolver<HeaderResolver>());
                services.AddMcpServer()
                    .WithHttpTransport()
                    .WithNewHeapPlatformAITools(exposure => exposure.UseTasks(tasks =>
                    {
                        tasks.UseInMemoryStore();
                        tasks.TimeToLive = TimeToLive;
                        tasks.AbandonAfter = AbandonAfter;
                        tasks.MaxResultBytes = NhAiMcpTaskOptions.MinResultBytes;
                    }));

                var server = builder.Build();
                server.MapMcp("/mcp");
                return server;
            });
        }

        public async Task<McpClient> ConnectAsync(string actor, string protocolVersion, Guid? divisionId = null)
        {
            return await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(Endpoint),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        [ActorHeader] = actor,
                        [DivisionHeader] = (divisionId ?? DivisionId).ToString()
                    }
                }),
                new McpClientOptions { ProtocolVersion = protocolVersion });
        }

        public async Task DisposeAsync()
        {
            foreach (var gate in _gates.Values)
            {
                gate.TrySetResult();
            }

            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }

        internal async Task<TaskResult<string>> AnalyseAsync(string input, NhAiInvocationContext context, CancellationToken cancellationToken)
        {
            Seen(input).TrySetResult(context.McpTaskId);
            try
            {
                await Gate(input).Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled(input).TrySetResult();
                throw;
            }

            return TaskResult<string>.Succeeded(context.McpTaskId is { } taskId
                ? $"analysed {input} as task {taskId}"
                : $"analysed {input} directly");
        }

        private TaskCompletionSource Gate(string input)
        {
            return _gates.GetOrAdd(input, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        private TaskCompletionSource<string?> Seen(string input)
        {
            return _seen.GetOrAdd(input, _ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        private TaskCompletionSource Cancelled(string input)
        {
            return _cancelled.GetOrAdd(input, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    /// <summary>Reads the actor from a header.</summary>
    internal sealed class HeaderResolver : INhAiAuthenticatedInvocationContextResolver
    {
        public ValueTask<TaskResult<NhAiInvocationContext>> ResolveAsync(
            HttpContext httpContext,
            CancellationToken cancellationToken = default)
        {
            var actor = httpContext.Request.Headers[ActorHeader].ToString();
            return ValueTask.FromResult(TaskResult<NhAiInvocationContext>.Succeeded(
                new NhAiInvocationContext(actor, "tool-invocation", new Dictionary<string, string>())));
        }
    }

    /// <summary>
    /// An attested catalog with two read tools exported to MCP through the shared invoker: an analysis
    /// that supports tasks and an export that requires them.
    /// </summary>
    internal sealed class LongRunningCatalog : INhAiAttestedToolCatalog
    {
        private readonly Host _host;
        private readonly NhAiToolDescriptor _analyze;
        private readonly NhAiToolDescriptor _export;

        public LongRunningCatalog(Host host)
        {
            _host = host;
            _analyze = Descriptor("projects.analyze", AnalyzeTool, NhAiToolTaskSupport.Optional);
            _export = Descriptor("projects.export", ExportTool, NhAiToolTaskSupport.Required);
        }

        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors => [_analyze, _export];

        public string AttestationHash => Hash(string.Join(
            "\n",
            Descriptors.Select(descriptor => descriptor.Id + "@1:" + descriptor.ContractHash)));

        public NhAiToolCatalogManifest Manifest => new(
            "long-running",
            1,
            AttestationHash,
            Descriptors
                .Select(descriptor => new NhAiToolManifestEntry(
                    descriptor.Id,
                    descriptor.Version,
                    descriptor.SchemaHash,
                    descriptor.ContractHash)
                {
                    ExportName = descriptor.ExportName
                })
                .ToList());

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            var invoker = services.GetRequiredService<INhAiToolInvoker>();
            var host = _host;
            var analyze = _analyze;
            var export = _export;
            Func<string, CancellationToken, Task<TaskResult<string>>> analyzeHandler =
                (input, cancellationToken) => invoker.InvokeAsync(
                    analyze,
                    input,
                    (context, token) => host.AnalyseAsync(input, context, token),
                    cancellationToken);
            Func<string, CancellationToken, Task<TaskResult<string>>> exportHandler =
                (input, cancellationToken) => invoker.InvokeAsync(
                    export,
                    input,
                    (_, _) => Task.FromResult(TaskResult<string>.Succeeded(
                        input == "large" ? new string('x', 4_000) : "exported " + input)),
                    cancellationToken);
            return
            [
                NhAiGovernedAIFunction.Create(analyze, AIFunctionFactory.Create(analyzeHandler, new AIFunctionFactoryOptions
                {
                    Name = analyze.ExportName,
                    Description = analyze.Description
                })),
                NhAiGovernedAIFunction.Create(export, AIFunctionFactory.Create(exportHandler, new AIFunctionFactoryOptions
                {
                    Name = export.ExportName,
                    Description = export.Description
                }))
            ];
        }

        private static NhAiToolDescriptor Descriptor(string id, string exportName, NhAiToolTaskSupport taskSupport)
        {
            return new NhAiToolDescriptor(
                id,
                1,
                "A long-running read of a project.",
                typeof(string),
                typeof(string),
                NhAiToolEffect.ReadOnly,
                NhAiToolExposure.Mcp,
                false,
                [])
            {
                ExportName = exportName,
                CatalogId = "long-running",
                ContractHash = Hash(id + "-contract"),
                Timeout = Host.ToolTimeout,
                MaxResultBytes = 8_192,
                TaskSupport = taskSupport
            };
        }

        private static string Hash(string value)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }
}
