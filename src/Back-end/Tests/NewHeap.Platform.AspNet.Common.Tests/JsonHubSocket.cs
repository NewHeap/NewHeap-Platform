using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The JSON hub protocol over a WebSocket, as a browser client speaks it: every message is a JSON
/// record that ends with the record separator. Shared by the background operation hub's tests.
/// </summary>
internal sealed class JsonHubSocket(WebSocket socket)
{
    private const char RecordSeparator = '\u001e';
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly Queue<string> _records = new();
    private readonly StringBuilder _pending = new();

    public Task SendAsync(string record)
    {
        return socket.SendAsync(
            Encoding.UTF8.GetBytes(record + RecordSeparator),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    /// <summary>
    /// The next hub message of the given type; pings are skipped.
    /// </summary>
    public async Task<JsonElement> ReceiveMessageAsync(int type)
    {
        while (true)
        {
            using var message = JsonDocument.Parse(await ReceiveAsync());
            if (message.RootElement.GetProperty("type").GetInt32() == type)
            {
                return message.RootElement.Clone();
            }
        }
    }

    /// <summary>
    /// Waits until the hub ends the connection: with a close message, a close frame or by aborting
    /// the socket. Fails when the connection is still open after <see cref="Patience"/>.
    /// </summary>
    public async Task WaitForCloseAsync()
    {
        using var timeout = new CancellationTokenSource(Patience);
        var buffer = new byte[4096];
        while (true)
        {
            WebSocketReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(buffer, timeout.Token);
            }
            catch (WebSocketException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            if (received.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            // A close message (type 7) announces the end before the close frame.
            if (Encoding.UTF8.GetString(buffer, 0, received.Count).Contains("\"type\":7", StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    public async Task<string> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(Patience);
        var buffer = new byte[4096];
        while (_records.Count == 0)
        {
            var received = await socket.ReceiveAsync(buffer, timeout.Token);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("The hub closed the connection.");
            }

            _pending.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
            var text = _pending.ToString();
            var end = text.LastIndexOf(RecordSeparator);
            if (end < 0)
            {
                continue;
            }

            foreach (var record in text[..end].Split(RecordSeparator))
            {
                _records.Enqueue(record);
            }

            _pending.Clear();
            _pending.Append(text[(end + 1)..]);
        }

        return _records.Dequeue();
    }
}
