using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead;

/// <summary>
/// The <c>session</c> command: one process that serves many requests over JSON lines, so a caller
/// pays for a process start, a connection and a principal verification once instead of per call.
/// Each line runs through the pipeline of a one-shot command, in its own read-only transaction.
/// </summary>
internal static class DatabaseReadSession
{
    public const int ProtocolVersion = 1;

    public const int MaximumIdLength = 64;

    /// <summary>
    /// The largest request body of a one-shot command with room for the line around it.
    /// </summary>
    public const int MaximumLineBytes = DatabaseReadJson.MaximumRequestBytes + 1024;

    private static readonly DatabaseReadSessionReadyLine ReadyLine = new()
    {
        Session = new DatabaseReadSessionCapabilities
        {
            ProtocolVersion = ProtocolVersion,
            Commands = ["query", "schema"],
            SchemaOperations = ["search", "describe", "indexes", "catalog"]
        }
    };

    /// <summary>
    /// Serves requests until the input ends, the session was idle or alive for its bound, or the
    /// caller cancels. None of those is a failure of a request; only a cancellation or an output
    /// that can no longer be written ends the session with a non-zero exit code.
    /// </summary>
    public static async Task<int> RunAsync(
        CliOptions options,
        Stream input,
        Stream output,
        IDatabaseReadConnectionFactory connectionFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var sessionOptions = options.Session!;
        await DatabaseReadJson.WriteAsync(output, ReadyLine, cancellationToken);

        await using var host = new DatabaseReadSessionHost(
            options.ProfilesPath,
            options.EnvironmentName,
            connectionFactory,
            sessionOptions.VerificationInterval,
            timeProvider);
        var lines = new DatabaseReadLineReader(input, MaximumLineBytes);
        var lifetimeEnd = timeProvider.GetUtcNow() + sessionOptions.MaximumLifetime;

        try
        {
            while (true)
            {
                var now = timeProvider.GetUtcNow();
                var remaining = Min(lifetimeEnd - now, sessionOptions.MaximumIdleTime);
                if (remaining <= TimeSpan.Zero)
                {
                    return (int)DatabaseReadExitCode.Success;
                }

                var line = await ReadLineWithinAsync(lines, remaining, timeProvider, cancellationToken);
                if (line is null)
                {
                    return (int)DatabaseReadExitCode.Success;
                }

                var answer = await AnswerAsync(line, options, host, cancellationToken);
                await WriteAnswerAsync(output, answer, CancellationToken.None);

                if (answer.ExitCode == (int)DatabaseReadExitCode.Cancelled)
                {
                    return (int)DatabaseReadExitCode.Cancelled;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return (int)DatabaseReadExitCode.Cancelled;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return (int)DatabaseReadExitCode.UnexpectedFailure;
        }
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    /// <summary>
    /// The next line, or <see langword="null"/> when the input ended or the time ran out. A read
    /// from the console stream of a real process cannot be cancelled once it started, so the wait
    /// ends without it and the read goes down with the process.
    /// </summary>
    private static async Task<DatabaseReadLine?> ReadLineWithinAsync(
        DatabaseReadLineReader lines,
        TimeSpan remaining,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await lines
                .ReadLineAsync(cancellationToken)
                .WaitAsync(remaining, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<SessionAnswer> AnswerAsync(
        DatabaseReadLine line,
        CliOptions options,
        DatabaseReadSessionHost host,
        CancellationToken cancellationToken)
    {
        if (line.IsOversized)
        {
            return await RefuseAsync(
                null,
                "request-too-large",
                $"A session line may contain at most {MaximumLineBytes} bytes.",
                cancellationToken);
        }

        DatabaseReadSessionRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<DatabaseReadSessionRequest>(line.Bytes.Span, DatabaseReadJson.Options);
        }
        catch (JsonException)
        {
            return await RefuseAsync(
                null,
                "invalid-json",
                "The request is not valid JSON or contains unknown properties.",
                cancellationToken);
        }

        if (string.IsNullOrEmpty(request?.Id) || request.Id.Length > MaximumIdLength)
        {
            return await RefuseAsync(
                null,
                "invalid-session-id",
                $"A session request needs an id of at most {MaximumIdLength} characters.",
                cancellationToken);
        }

        DatabaseReadCommand? command = request.Command switch
        {
            "query" => DatabaseReadCommand.Query,
            "schema" => DatabaseReadCommand.Schema,
            _ => null
        };
        if (command is null)
        {
            return await RefuseAsync(
                request.Id,
                "unknown-session-command",
                "A session command must be 'query' or 'schema'.",
                cancellationToken);
        }

        if (request.Request.ValueKind == JsonValueKind.Undefined)
        {
            return await RefuseAsync(
                request.Id,
                "session-request-required",
                "A session line needs a request.",
                cancellationToken);
        }

        using var body = new MemoryStream(Encoding.UTF8.GetBytes(request.Request.GetRawText()));
        var response = new MemoryStream();
        var exitCode = await NewHeapDatabaseReadApplication.RunRequestAsync(
            command.Value,
            options.ProfileName,
            token => DatabaseReadJson.ReadRequestAsync(body, token),
            host,
            response,
            cancellationToken);

        return new SessionAnswer(request.Id, exitCode, response);
    }

    private static async Task<SessionAnswer> RefuseAsync(
        string? id,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var response = new MemoryStream();
        await NewHeapDatabaseReadApplication.WriteErrorAsync(response, code, message, cancellationToken);

        return new SessionAnswer(id, (int)DatabaseReadExitCode.InvalidRequest, response);
    }

    /// <summary>
    /// Puts the one-shot response into the answer as it is. It is one line of JSON, so the answer is
    /// one line too.
    /// </summary>
    private static async Task WriteAnswerAsync(
        Stream output,
        SessionAnswer answer,
        CancellationToken cancellationToken)
    {
        using var line = new MemoryStream((int)answer.Response.Length + 128);
        await using (var writer = new Utf8JsonWriter(line))
        {
            writer.WriteStartObject();
            writer.WriteString("id", answer.Id);
            writer.WriteNumber("exitCode", answer.ExitCode);
            writer.WritePropertyName("response");
            writer.WriteRawValue(
                answer.Response.GetBuffer().AsSpan(0, (int)answer.Response.Length).TrimEnd((byte)'\n'),
                skipInputValidation: true);
            writer.WriteEndObject();
        }

        line.WriteByte((byte)'\n');
        await output.WriteAsync(line.GetBuffer().AsMemory(0, (int)line.Length), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private sealed record SessionAnswer(string? Id, int ExitCode, MemoryStream Response);
}
