using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The assistant calls a tool of an imported MCP server that offers tasks as a task and polls it within
/// the call's cancellation, which it ends by cancelling the remote task; a server without tasks is called
/// as before.
/// </summary>
public sealed class AssistantMcpRemoteTaskTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_server_with_tasks_runs_the_tool_as_a_task_that_the_assistant_polls_and_cancels_when_the_call_ends()
    {
        var tool = new SlowTool();
        var store = new ObservedTaskStore();
        await using var server = await StartAsync(tool, store);
        await using var client = await ConnectAsync(server);
        var remote = Assert.Single(await client.ListToolsAsync());
        var call = NhAssistantMcpRemoteTasks.For(client);

        Assert.True(NhAssistantMcpRemoteTasks.OffersTasks(client));
        var answered = call(remote, Arguments("alpha"), null, CancellationToken.None).AsTask();
        await tool.StartedAsync("alpha");
        tool.Release("alpha");
        var result = await answered.WaitAsync(Patience);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("done alpha", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);

        // The turn's cancellation ends the call while the assistant polls, and cancels the task on the server.
        using var turn = new CancellationTokenSource();
        var polled = store.NextPoll();
        var cancelled = call(remote, Arguments("beta"), null, turn.Token).AsTask();
        await tool.StartedAsync("beta");
        await polled.WaitAsync(Patience);
        await turn.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Patience));
        Assert.True(await tool.CancelledAsync("beta"));
    }

    [Fact]
    public async Task A_server_without_tasks_is_called_as_before()
    {
        var tool = new SlowTool();
        await using var server = await StartAsync(tool, store: null);
        await using var client = await ConnectAsync(server);
        var remote = Assert.Single(await client.ListToolsAsync());

        Assert.False(NhAssistantMcpRemoteTasks.OffersTasks(client));
        tool.Release("gamma");
        var result = await NhAssistantMcpRemoteTasks.For(client)(remote, Arguments("gamma"), null, CancellationToken.None)
            .AsTask()
            .WaitAsync(Patience);

        Assert.Equal("done gamma", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private static Dictionary<string, object?> Arguments(string input)
    {
        return new Dictionary<string, object?> { ["input"] = input };
    }

    private static async Task<McpClient> ConnectAsync(WebApplication server)
    {
        // The assistant's own client options: the newest revision, with fallback.
        return await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(LocalhostTestServer.Origin(server) + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            }),
            new McpClientOptions());
    }

    private static Task<WebApplication> StartAsync(SlowTool tool, IMcpTaskStore? store)
    {
        return LocalhostTestServer.StartAsync(port =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://localhost:{port}");
            builder.Logging.ClearProviders();
            var mcp = builder.Services.AddMcpServer()
                .WithHttpTransport()
                .WithTools([McpServerTool.Create(tool.RunAsync, new McpServerToolCreateOptions { Name = "slow" })]);
            if (store is not null)
            {
                mcp.WithTasks(store);
            }

            var app = builder.Build();
            app.MapMcp("/mcp");
            return app;
        });
    }

    /// <summary>
    /// The SDK's in-memory store, with a poll interval below the assistant's lower bound, which the
    /// assistant raises, that tells the test when the assistant polls.
    /// </summary>
    private sealed class ObservedTaskStore : IMcpTaskStore
    {
        private readonly InMemoryMcpTaskStore _inner = new() { DefaultPollIntervalMs = 10 };
        private TaskCompletionSource _polled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<InputResponseReceivedEventArgs>? InputResponseReceived
        {
            add => _inner.InputResponseReceived += value;
            remove => _inner.InputResponseReceived -= value;
        }

        public Task NextPoll()
        {
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _polled, next);
            return next.Task;
        }

        public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
        {
            return _inner.CreateTaskAsync(cancellationToken);
        }

        public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            var task = await _inner.GetTaskAsync(taskId, cancellationToken);
            Volatile.Read(ref _polled).TrySetResult();
            return task;
        }

        public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
        {
            return _inner.SetCompletedAsync(taskId, result, cancellationToken);
        }

        public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
        {
            return _inner.SetFailedAsync(taskId, error, cancellationToken);
        }

        public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
        {
            return _inner.SetCancelledAsync(taskId, cancellationToken);
        }

        public Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken = default)
        {
            return _inner.ResolveInputRequestsAsync(taskId, inputResponses, cancellationToken);
        }

        public Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests, CancellationToken cancellationToken = default)
        {
            return _inner.SetInputRequestsAsync(taskId, inputRequests, cancellationToken);
        }
    }

    /// <summary>A remote tool that waits until the test releases its input.</summary>
    private sealed class SlowTool
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _cancelled = new(StringComparer.Ordinal);
        private readonly Lock _sync = new();

        public async Task<string> RunAsync(string input, CancellationToken cancellationToken)
        {
            Signal(_started, input).TrySetResult();
            try
            {
                await Signal(_gates, input).Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Signal(_cancelled, input).TrySetResult();
                throw;
            }

            return "done " + input;
        }

        public void Release(string input)
        {
            Signal(_gates, input).TrySetResult();
        }

        public Task StartedAsync(string input)
        {
            return Signal(_started, input).Task.WaitAsync(Patience);
        }

        public async Task<bool> CancelledAsync(string input)
        {
            await Signal(_cancelled, input).Task.WaitAsync(Patience);
            return true;
        }

        private TaskCompletionSource Signal(Dictionary<string, TaskCompletionSource> signals, string input)
        {
            lock (_sync)
            {
                if (!signals.TryGetValue(input, out var signal))
                {
                    signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    signals[input] = signal;
                }

                return signal;
            }
        }
    }
}
