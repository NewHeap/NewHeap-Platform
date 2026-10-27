using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Live;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// One connection to the assistant hub of a test host that records what it receives. A marker, a
/// read position of a conversation that exists nowhere, sent to the connection's actor after the
/// events a test publishes, tells when everything sent before it arrived: the hub keeps the order of
/// one connection's messages, so a test asserts that nothing arrived without waiting for a time.
/// </summary>
internal sealed class AssistantLiveListener : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<NhAssistantLiveEventDto> _events = new();
    private readonly ConcurrentQueue<NhAssistantLiveConversationDto> _changes = new();
    private readonly ConcurrentQueue<NhAssistantLiveAgentWorkDto> _work = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _markers = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AssistantLiveListener(HubConnection connection)
    {
        Connection = connection;
        connection.On<NhAssistantLiveEventDto>("ConversationEvent", message => Record(_events, message));
        connection.On<NhAssistantLiveConversationDto>("ConversationChanged", message => Record(_changes, message));
        connection.On<NhAssistantLiveAgentWorkDto>("AgentWorkChanged", message => Record(_work, message));
        connection.On<NhAssistantLiveReadDto>("ConversationRead", message =>
            _markers.GetOrAdd(message.ConversationId, _ => NewSignal()).TrySetResult());
        connection.On<NhAssistantLiveRemovedDto>("ConversationRemoved", _ => { });
        connection.Closed += _ =>
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }

    /// <summary>The turn events and <c>message.created</c> events received so far.</summary>
    public IReadOnlyList<NhAssistantLiveEventDto> Events => _events.ToArray();

    /// <summary>The conversation states received so far.</summary>
    public IReadOnlyList<NhAssistantLiveConversationDto> Changes => _changes.ToArray();

    /// <summary>The changes of agent work received so far.</summary>
    public IReadOnlyList<NhAssistantLiveAgentWorkDto> Work => _work.ToArray();

    /// <summary>Completes when the server closed the connection.</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Connects to <c>/hub/assistant</c> with the given request headers over
    /// <paramref name="transport"/>: long polling and server-sent events through the test server's
    /// handler, WebSockets through its WebSocket client.
    /// </summary>
    public static async Task<AssistantLiveListener> ConnectAsync(
        AssistantWebApplication app,
        IReadOnlyDictionary<string, string> headers,
        HttpTransportType transport = HttpTransportType.LongPolling)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hub/assistant"), options =>
            {
                options.Transports = transport;
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                foreach (var (name, value) in headers)
                {
                    options.Headers[name] = value;
                }
                if (transport == HttpTransportType.WebSockets)
                {
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                    {
                        var client = app.Server.CreateWebSocketClient();
                        client.ConfigureRequest = request =>
                        {
                            foreach (var (name, value) in headers)
                            {
                                request.Headers[name] = value;
                            }
                        };
                        return await client.ConnectAsync(context.Uri, cancellationToken);
                    };
                }
            })
            .Build();
        var listener = new AssistantLiveListener(connection);
        await connection.StartAsync();
        // The handshake completes before the hub joins the connection to its groups; the hub answers an
        // invocation only after it finished connecting, so the groups are in place after this one.
        await connection.InvokeAsync("UnwatchAgentWork");
        return listener;
    }

    /// <summary>
    /// Waits for a turn or <c>message.created</c> event that matches, at most a generous time that
    /// only matters when the test fails.
    /// </summary>
    public async Task<NhAssistantLiveEventDto> WaitForEventAsync(Func<NhAssistantLiveEventDto, bool> match)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (true)
        {
            var received = Volatile.Read(ref _received);
            if (_events.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await received.Task.WaitAsync(timeout.Token);
        }
    }

    /// <summary>
    /// Sends a marker to <paramref name="actorId"/> through the host's publisher and waits until this
    /// connection received it, so everything published to the actor before it has arrived.
    /// </summary>
    public async Task SyncAsync(AssistantWebApplication app, string actorId)
    {
        var marker = Guid.NewGuid();
        var arrived = _markers.GetOrAdd(marker, _ => NewSignal());
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NhAssistantLivePublisher>()
                .PublishToAsync(actorId, new NhAssistantLiveConversationRead(marker, 1), CancellationToken.None);
        }

        await arrived.Task.WaitAsync(Patience);
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
    }

    private void Record<T>(ConcurrentQueue<T> queue, T message)
    {
        queue.Enqueue(message);
        Interlocked.Exchange(ref _received, NewSignal()).TrySetResult();
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
