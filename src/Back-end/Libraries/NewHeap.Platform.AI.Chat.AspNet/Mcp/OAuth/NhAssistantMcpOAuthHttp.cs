using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The answer of an OAuth endpoint: the status and, when the body was JSON within the size limit, its
/// root. A body over the limit is not read (<see cref="TooLarge"/>), so the answer is unusable like
/// one without the expected fields.
/// </summary>
internal sealed record NhAssistantMcpOAuthResponse(HttpStatusCode Status, JsonElement? Body, HttpResponseHeaders Headers)
{
    /// <summary>The body exceeded <see cref="NhAssistantMcpOAuthHttp.MaxResponseBytes"/> and was dropped.</summary>
    public bool TooLarge { get; init; }

    public string? String(string name)
    {
        return Body is { ValueKind: JsonValueKind.Object } body
            && body.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    public long? Number(string name)
    {
        return Body is { ValueKind: JsonValueKind.Object } body
            && body.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : null;
    }
}

/// <summary>
/// HTTP for OAuth URLs that remote documents name (D9): every URL passes the host guard with https
/// required, private networks blocked unless allowed and the resolved addresses checked again when
/// the socket connects; no redirects, the connect timeout and at most 64 KiB per response. A larger
/// body is dropped with a warning: the endpoint answered, so it is not reported unreachable. Bodies,
/// tokens and secrets never reach a log.
/// </summary>
internal sealed partial class NhAssistantMcpOAuthHttp : IDisposable
{
    public const int MaxResponseBytes = 64 * 1024;

    private readonly NhAssistantMcpHostGuard _guard;
    private readonly IOptionsMonitor<NhAssistantOptions> _options;
    private readonly ILogger<NhAssistantMcpOAuthHttp> _logger;
    private readonly HttpClient _client;
    private readonly HttpClient _mcpClient;

    public NhAssistantMcpOAuthHttp(
        NhAssistantMcpHostGuard guard,
        IOptionsMonitor<NhAssistantOptions> options,
        ILogger<NhAssistantMcpOAuthHttp> logger)
    {
        _guard = guard;
        _options = options;
        _logger = logger;
        _client = CreateClient(guard.ResolveAllowedForOAuthAsync);
        // The MCP endpoint itself follows the MCP endpoint rules, which allow intranet servers.
        _mcpClient = CreateClient(guard.ResolveAllowedAsync);
    }

    private static HttpClient CreateClient(Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>> resolve)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await resolve(context.DnsEndPoint.Host, token);
                if (addresses.Count == 0)
                {
                    throw new NhAssistantMcpHostBlockedException();
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses.ToArray(), context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Whether an OAuth URL passes the host guard (before DNS).</summary>
    public bool IsAllowed(Uri uri)
    {
        return _guard.CheckOAuthUri(uri) is null;
    }

    public Task<TaskResult<NhAssistantMcpOAuthResponse>> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        return SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), uri, cancellationToken);
    }

    public Task<TaskResult<NhAssistantMcpOAuthResponse>> PostFormAsync(
        Uri uri,
        IEnumerable<KeyValuePair<string, string>> form,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken)
    {
        var fields = form.ToArray();
        return SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(fields) };
                request.Headers.Authorization = authorization;
                return request;
            },
            uri,
            cancellationToken);
    }

    public Task<TaskResult<NhAssistantMcpOAuthResponse>> PostJsonAsync(Uri uri, string json, CancellationToken cancellationToken)
    {
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") },
            uri,
            cancellationToken);
    }

    /// <summary>
    /// Requests the MCP endpoint without a credential, to read its <c>WWW-Authenticate</c> challenge.
    /// The MCP endpoint was typed by an administrator, so it follows the MCP endpoint rules.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpOAuthResponse>> ProbeAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (_guard.CheckUri(endpoint) is { } blocked)
        {
            return TaskResult<NhAssistantMcpOAuthResponse>.Failed(blocked, "The MCP server address is not allowed.");
        }
        return await SendCheckedAsync(
            _mcpClient,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
                return request;
            },
            cancellationToken);
    }

    public void Dispose()
    {
        _client.Dispose();
        _mcpClient.Dispose();
    }

    private async Task<TaskResult<NhAssistantMcpOAuthResponse>> SendAsync(
        Func<HttpRequestMessage> create,
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (_guard.CheckOAuthUri(uri) is { } blocked)
        {
            return TaskResult<NhAssistantMcpOAuthResponse>.Failed(blocked, "The OAuth address is not allowed.");
        }
        return await SendCheckedAsync(_client, create, cancellationToken);
    }

    private async Task<TaskResult<NhAssistantMcpOAuthResponse>> SendCheckedAsync(
        HttpClient client,
        Func<HttpRequestMessage> create,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CurrentValue.Mcp.ConnectTimeout);
        try
        {
            using var request = create();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await ReadBoundedAsync(response.Content, timeout.Token);
            if (body is null)
            {
                LogResponseTooLarge(_logger, request.RequestUri?.Host ?? string.Empty, (int)response.StatusCode);
                return TaskResult<NhAssistantMcpOAuthResponse>.Succeeded(
                    new NhAssistantMcpOAuthResponse(response.StatusCode, null, response.Headers) { TooLarge = true });
            }
            JsonElement? json = null;
            if (body.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(body);
                    json = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    json = null;
                }
            }
            return TaskResult<NhAssistantMcpOAuthResponse>.Succeeded(new NhAssistantMcpOAuthResponse(response.StatusCode, json, response.Headers));
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or NhAssistantMcpException
            && !cancellationToken.IsCancellationRequested)
        {
            var code = NhAssistantMcpFailures.Classify(exception);
            return TaskResult<NhAssistantMcpOAuthResponse>.Failed(
                code == NhAssistantAdminErrorCodes.McpHostBlocked ? code : NhAssistantAdminErrorCodes.McpUnreachable,
                "The OAuth endpoint could not be reached.");
        }
    }

    /// <summary>
    /// Reads the body up to <see cref="MaxResponseBytes"/>; <see langword="null"/> when it is larger.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An OAuth endpoint at {Host} answered {Status} with a body over 64 KiB; the body was dropped.")]
    private static partial void LogResponseTooLarge(ILogger logger, string host, int status);
}
