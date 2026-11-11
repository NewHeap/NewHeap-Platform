using System.Runtime.CompilerServices;
using System.Text;
using AGUI.Abstractions;
using AGUI.Formatting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AGUI;

/// <summary>
/// Writes one AG-UI run as server-sent events with the AG-UI formatter: the mapped events of a turn, or the
/// two events of a run that starts no turn. Like the assistant's own stream it writes a <c>: keep-alive</c>
/// comment while the turn is silent, lets a client disconnect cancel the turn through the request token, drops
/// the remaining events of a turn that keeps running after a disconnect
/// (<see cref="NhAssistantOptions.KeepTurnsAfterDisconnect"/>) and ends the request with the turn.
/// </summary>
internal sealed class NhAssistantAGUIEventStreamResult : IResult
{
    private static readonly byte[] KeepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");
    private static readonly SseEventStreamFormatter Formatter = new();

    private readonly NhAssistantTurnHandle? _handle;
    private readonly NhAssistantAGUIEventMapper _mapper;
    private readonly IReadOnlyList<BaseEvent> _events;

    /// <summary>The run of a turn.</summary>
    public NhAssistantAGUIEventStreamResult(NhAssistantTurnHandle handle, NhAssistantAGUIEventMapper mapper)
    {
        _handle = handle;
        _mapper = mapper;
        _events = [];
    }

    /// <summary>A run that starts no turn, with its events.</summary>
    public NhAssistantAGUIEventStreamResult(NhAssistantAGUIEventMapper mapper, IReadOnlyList<BaseEvent> events)
    {
        _mapper = mapper;
        _events = events;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = Formatter.MediaType;
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var aborted = httpContext.RequestAborted;
        if (_handle is null)
        {
            await WriteAsync(response, _events, aborted);
            return;
        }

        var timing = httpContext.RequestServices.GetService<NhAssistantServerSentEventsTiming>()
            ?? NhAssistantServerSentEventsTiming.Default;
        try
        {
            await response.Body.FlushAsync(aborted);
            while (true)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                wait.CancelAfter(timing.KeepAliveInterval);
                bool available;
                try
                {
                    available = await _handle.Events.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                {
                    await response.Body.WriteAsync(KeepAlive, aborted);
                    await response.Body.FlushAsync(aborted);
                    continue;
                }
                if (!available)
                {
                    break;
                }

                var events = new List<BaseEvent>();
                while (_handle.Events.TryRead(out var evt))
                {
                    events.AddRange(_mapper.Map(evt));
                }
                await WriteAsync(response, events, aborted);
            }

            // A turn stream always ends with turn.completed or error; a run must end all the same.
            await WriteAsync(response, _mapper.Finish(), aborted);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The client disconnected; the turn observes the same token and ends cancelled, unless it keeps
            // running after a disconnect.
            await DropRemainingAsync(_handle);
        }
        catch (IOException) when (aborted.IsCancellationRequested)
        {
            // The connection closed while an event was being written.
            await DropRemainingAsync(_handle);
        }

        await _handle.Completion;
    }

    private static async Task WriteAsync(HttpResponse response, IReadOnlyList<BaseEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        await Formatter.WriteAsync(Enumerate(events, cancellationToken), response.Body, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async IAsyncEnumerable<BaseEvent> Enumerate(
        IReadOnlyList<BaseEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Reads the events the turn still writes after the client left and drops them; the conversation's
    /// snapshot and live updates carry its result.
    /// </summary>
    private static async Task DropRemainingAsync(NhAssistantTurnHandle handle)
    {
        while (await handle.Events.WaitToReadAsync(CancellationToken.None))
        {
            while (handle.Events.TryRead(out _))
            {
                // Dropped: nobody reads the stream any more.
            }
        }
    }
}
