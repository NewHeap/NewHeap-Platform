using System.Net;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Bounds what the responses of an MCP server can make the host hold, before the MCP SDK reads them:
/// a JSON response, and each event of an event stream, is at most
/// <see cref="NhAssistantMcpOptions.MaxResponseBytes"/> long. The host stops reading a longer response
/// as soon as it passed the bound, and a declared length beyond it fails before the body is read; the
/// request then fails with <see cref="NhAssistantAdminErrorCodes.McpResponseTooLarge"/>. An event ends
/// with a blank line, so a stream of many small events, such as progress notifications, stays open.
/// </summary>
internal sealed class NhAssistantMcpResponseLimitHandler(int maxBytes) : DelegatingHandler
{
    private const string EventStreamMediaType = "text/event-stream";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var perEvent = string.Equals(response.Content.Headers.ContentType?.MediaType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase);
        if (!perEvent && response.Content.Headers.ContentLength > maxBytes)
        {
            response.Dispose();
            throw new NhAssistantMcpResponseTooLargeException();
        }

        response.Content = new NhAssistantMcpBoundedContent(response.Content, maxBytes, perEvent);
        return response;
    }
}

/// <summary>
/// The response of an MCP server could not be read whole because it is longer than
/// <see cref="NhAssistantMcpOptions.MaxResponseBytes"/>.
/// </summary>
internal sealed class NhAssistantMcpResponseTooLargeException : NhAssistantMcpException
{
    public NhAssistantMcpResponseTooLargeException()
        : base(NhAssistantAdminErrorCodes.McpResponseTooLarge, "The MCP server's response is larger than the host accepts.")
    {
    }
}

/// <summary>
/// The content of a response, read through <see cref="NhAssistantMcpBoundedStream"/> with the headers of
/// the content it replaces.
/// </summary>
internal sealed class NhAssistantMcpBoundedContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly long _maxBytes;
    private readonly bool _perEvent;

    public NhAssistantMcpBoundedContent(HttpContent inner, long maxBytes, bool perEvent)
    {
        _inner = inner;
        _maxBytes = maxBytes;
        _perEvent = perEvent;
        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        return SerializeToStreamAsync(stream, context, CancellationToken.None);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var source = await CreateContentReadStreamAsync(cancellationToken);
        await source.CopyToAsync(stream, cancellationToken);
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        return CreateContentReadStreamAsync(CancellationToken.None);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        var inner = await _inner.ReadAsStreamAsync(cancellationToken);
        return new NhAssistantMcpBoundedStream(inner, _maxBytes, _perEvent);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Reads a response and fails with <see cref="NhAssistantMcpResponseTooLargeException"/> once it passed
/// its bound: the whole body, or, for an event stream, each event, which a blank line ends.
/// </summary>
internal sealed class NhAssistantMcpBoundedStream(Stream inner, long maxBytes, bool perEvent) : Stream
{
    private long _counted;
    private bool _lineEmpty = true;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Count(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Count(buffer.Span[..read]);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Count(ReadOnlySpan<byte> bytes)
    {
        if (!perEvent)
        {
            _counted += bytes.Length;
            if (_counted > maxBytes)
            {
                throw new NhAssistantMcpResponseTooLargeException();
            }

            return;
        }

        foreach (var value in bytes)
        {
            _counted++;
            if (_counted > maxBytes)
            {
                throw new NhAssistantMcpResponseTooLargeException();
            }
            if (value == (byte)'\n')
            {
                if (_lineEmpty)
                {
                    // A blank line ends the event; the next one has the whole bound again.
                    _counted = 0;
                }
                _lineEmpty = true;
            }
            else if (value != (byte)'\r')
            {
                _lineEmpty = false;
            }
        }
    }
}

/// <summary>
/// Lists the tools of a connected server page by page, within
/// <see cref="NhAssistantMcpOptions.MaxToolListPages"/> pages and <see cref="NhAssistantMcpOptions.MaxTools"/>
/// tools. A server that announces more fails the listing with
/// <see cref="NhAssistantAdminErrorCodes.McpToolListTooLarge"/>, so the host never holds an unbounded
/// list. Each tool is registered with the client for its <c>Mcp-Param-*</c> headers, and a tool whose
/// header annotations are invalid is left out, as the SDK's own listing does.
/// </summary>
internal static class NhAssistantMcpToolLists
{
    public static async Task<IList<McpClientTool>> ListAsync(
        McpClient client,
        NhAssistantMcpOptions options,
        CancellationToken cancellationToken)
    {
        var maxPages = options.BoundedMaxToolListPages();
        var maxTools = options.BoundedMaxTools();
        var tools = new List<McpClientTool>();
        string? cursor = null;
        for (var page = 1; ; page++)
        {
            var result = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, cancellationToken);
            if (tools.Count + result.Tools.Count > maxTools)
            {
                throw new NhAssistantMcpToolListTooLargeException();
            }

            foreach (var tool in result.Tools)
            {
                if (TryRegister(client, tool))
                {
                    tools.Add(new McpClientTool(client, tool));
                }
            }

            cursor = result.NextCursor;
            if (string.IsNullOrEmpty(cursor))
            {
                return tools;
            }
            if (page >= maxPages)
            {
                throw new NhAssistantMcpToolListTooLargeException();
            }
        }
    }

    /// <summary>
    /// Registers the tool with the client, so a call sends the headers its schema annotates; false
    /// when those annotations are invalid.
    /// </summary>
    private static bool TryRegister(McpClient client, Tool tool)
    {
        try
        {
            client.AddKnownTools([tool]);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// The server lists more tools, or more pages of them, than the host accepts.
/// </summary>
internal sealed class NhAssistantMcpToolListTooLargeException : NhAssistantMcpException
{
    public NhAssistantMcpToolListTooLargeException()
        : base(NhAssistantAdminErrorCodes.McpToolListTooLarge, "The MCP server lists more tools than the host accepts.")
    {
    }
}
