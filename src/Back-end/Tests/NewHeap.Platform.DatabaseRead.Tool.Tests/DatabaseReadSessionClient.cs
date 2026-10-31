using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// One <c>session</c> in this test process, talked to the way a caller does: it runs the tool's
/// entry point over pipes, sends JSON lines and reads one line back per request.
/// </summary>
internal sealed class DatabaseReadSessionClient : IAsyncDisposable
{
    /// <summary>
    /// Generous, and only reached when a test fails.
    /// </summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);

    private readonly Pipe _input = new();
    private readonly Pipe _output = new();
    private readonly StreamReader _outputReader;
    private readonly Task<int> _session;
    private int _requestCount;

    public DatabaseReadSessionClient(
        DatabaseReadTestWorkspace workspace,
        IDatabaseReadConnectionFactory? connectionFactory = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default,
        params string[] arguments)
    {
        _outputReader = new StreamReader(_output.Reader.AsStream(), Encoding.UTF8);
        _session = Task.Run(async () =>
        {
            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                ["session", "--profiles", workspace.ProfileCatalogPath, .. arguments],
                _input.Reader.AsStream(),
                _output.Writer.AsStream(),
                connectionFactory ?? DatabaseReadConnectionFactory.Instance,
                timeProvider ?? TimeProvider.System,
                cancellationToken);
            await _output.Writer.CompleteAsync();

            return exitCode;
        });
    }

    public Task<int> Completion => _session;

    public async Task<string> ReadLineAsync()
    {
        var line = await _outputReader.ReadLineAsync().WaitAsync(ResponseTimeout);

        return line ?? throw new InvalidOperationException("The session ended without an answer.");
    }

    /// <summary>
    /// The rest of the output, which is empty unless the session wrote more than it was asked for.
    /// </summary>
    public Task<string> ReadToEndAsync()
    {
        return _outputReader.ReadToEndAsync().WaitAsync(ResponseTimeout);
    }

    public async Task<JsonDocument> ReadReadyLineAsync()
    {
        return JsonDocument.Parse(await ReadLineAsync());
    }

    public async Task WriteAsync(string text)
    {
        await _input.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Sends a raw line and returns the parsed answer.
    /// </summary>
    public async Task<JsonDocument> SendLineAsync(string line)
    {
        await WriteAsync(line + "\n");

        return JsonDocument.Parse(await ReadLineAsync());
    }

    public async Task<SessionAnswer> SendAsync(string command, string requestJson)
    {
        var id = "request-" + ++_requestCount;
        var document = await SendLineAsync($$"""{"id":"{{id}}","command":"{{command}}","request":{{requestJson}}}""");
        var root = document.RootElement;
        if (root.GetProperty("id").GetString() != id)
        {
            throw new InvalidOperationException($"The answer carries another id: {root.GetRawText()}");
        }

        return new SessionAnswer(document, root.GetProperty("exitCode").GetInt32(), root.GetProperty("response"));
    }

    public Task<SessionAnswer> QueryAsync(
        string sql,
        int maximumRows = 10,
        int timeoutSeconds = 10,
        object[]? parameters = null)
    {
        return SendAsync(
            "query",
            Json(DatabaseReadTestWorkspace.Request(sql, parameters, maximumRows, timeoutSeconds)));
    }

    public Task<SessionAnswer> SchemaAsync(
        string operation,
        string? schemaName = null,
        string? objectName = null,
        string? searchTerm = null,
        int maximumRows = 10)
    {
        return SendAsync(
            "schema",
            Json(DatabaseReadTestWorkspace.SchemaRequest(operation, schemaName, objectName, searchTerm, maximumRows)));
    }

    /// <summary>
    /// Ends the input, which ends the session.
    /// </summary>
    public async Task<int> CompleteAsync()
    {
        await _input.Writer.CompleteAsync();

        return await _session.WaitAsync(ResponseTimeout);
    }

    public async ValueTask DisposeAsync()
    {
        await _input.Writer.CompleteAsync();
        await _session.WaitAsync(ResponseTimeout);
        _outputReader.Dispose();
    }

    private static string Json(MemoryStream request)
    {
        using (request)
        {
            return Encoding.UTF8.GetString(request.ToArray());
        }
    }
}

internal sealed record SessionAnswer(JsonDocument Document, int ExitCode, JsonElement Response)
{
    public string Text => Document.RootElement.GetRawText();

    public JsonElement FirstCell => Response.GetProperty("result").GetProperty("rows")[0][0];

    public JsonElement Error => Response.GetProperty("error");

    public string? ErrorCode => Error.GetProperty("code").GetString();

    public string? Classification => Error.GetProperty("classification").GetString();
}
