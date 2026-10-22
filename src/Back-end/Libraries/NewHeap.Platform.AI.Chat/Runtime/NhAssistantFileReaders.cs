using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// What became of a file a turn asked a reading tool to turn into text: the text, or the stable code
/// why it has none. <see cref="ReaderId"/> names the reader that read, refused or failed it.
/// </summary>
internal sealed record NhAssistantFileReading(string? ReaderId, string? Text, string? Code)
{
    /// <summary>
    /// Whose credential an MCP reader's request carried (<see cref="NhAssistantAuditEvent.McpConnectionOwner"/>).
    /// </summary>
    public string? ConnectionOwner { get; init; }

    /// <summary>
    /// Whether a reader received the file: it read it or failed. A reader passed over before it
    /// received the file, for its classification, consent or connection, did not.
    /// </summary>
    public bool Received { get; init; }

    public static NhAssistantFileReading Read(string readerId, string text, string? connectionOwner = null)
    {
        return new NhAssistantFileReading(readerId, text, null) { Received = true, ConnectionOwner = connectionOwner };
    }

    public static NhAssistantFileReading Failed(string readerId, string? connectionOwner = null)
    {
        return new NhAssistantFileReading(readerId, null, NhAssistantErrorCodes.FileReaderFailed) { Received = true, ConnectionOwner = connectionOwner };
    }

    public static NhAssistantFileReading PassedOver(string readerId, string code)
    {
        return new NhAssistantFileReading(readerId, null, code);
    }
}

/// <summary>
/// A file and the turn an MCP server set up as reader would read it for: whose request the turn
/// serves decides the credential and, in a headless turn, the consent it needs.
/// </summary>
internal sealed record NhAssistantMcpFileReadRequest(NhAssistantFileReadRequest File, NhAssistantMcpToolRequest Turn);

/// <summary>
/// The MCP servers an administrator set up as readers. The ASP.NET integration supplies them; the
/// core library knows none.
/// </summary>
internal interface INhAssistantMcpFileReader
{
    /// <summary>
    /// Whether an enabled MCP server is set up to read <paramref name="mediaType"/>.
    /// </summary>
    Task<bool> DeclaresAsync(string mediaType, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the file with the first server, ordered by id, that reads its type, permits its
    /// classification and that the turn may use; <see langword="null"/> when no server reads the type.
    /// </summary>
    Task<NhAssistantFileReading?> ReadAsync(NhAssistantMcpFileReadRequest request, CancellationToken cancellationToken);
}

internal sealed class NhAssistantNoMcpFileReader : INhAssistantMcpFileReader
{
    public Task<bool> DeclaresAsync(string mediaType, CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }

    public Task<NhAssistantFileReading?> ReadAsync(NhAssistantMcpFileReadRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult<NhAssistantFileReading?>(null);
    }
}

/// <summary>
/// Chooses the reading tool for a file the serving model cannot read itself: the readers of the
/// application in registration order first, then the MCP servers set up as readers. The first that
/// accepts the type and permits the file's classification, and that the turn may use, reads it. A
/// reader that received the file and failed ends it with
/// <see cref="NhAssistantErrorCodes.FileReaderFailed"/>; the file is never sent to a second reader. When
/// no reader may read it, the refusal of the first reader that declares the type applies, and
/// <see cref="NhAssistantErrorCodes.FileTypeNotSupported"/> when none declares it.
/// </summary>
internal sealed class NhAssistantFileReaders(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    INhAssistantMcpFileReader mcp,
    ILogger<NhAssistantFileReaders> logger)
{
    /// <summary>
    /// Whether a reader of the application or an MCP server is set up to read <paramref name="mediaType"/>.
    /// </summary>
    public async Task<bool> DeclaresAsync(string mediaType, CancellationToken cancellationToken)
    {
        return registration.FileReaders.Any(reader => reader.Accepts(mediaType))
            || await mcp.DeclaresAsync(mediaType, cancellationToken);
    }

    public async Task<NhAssistantFileReading> ReadAsync(
        NhAssistantFileReadRequest request,
        NhAssistantMcpToolRequest turn,
        CancellationToken cancellationToken)
    {
        NhAssistantFileReading? passedOver = null;
        foreach (var reader in registration.FileReaders)
        {
            if (!reader.Accepts(request.MediaType))
            {
                continue;
            }
            if (!reader.Permits(request.Classification))
            {
                passedOver ??= NhAssistantFileReading.PassedOver(reader.Id, NhAssistantErrorCodes.ClassificationNotPermitted);
                continue;
            }

            return await ReadLocalAsync(reader, request, cancellationToken);
        }

        var remote = await mcp.ReadAsync(new NhAssistantMcpFileReadRequest(request, turn), cancellationToken);
        if (remote is null)
        {
            return passedOver ?? new NhAssistantFileReading(null, null, NhAssistantErrorCodes.FileTypeNotSupported);
        }
        if (remote.Text is not null || remote.Received)
        {
            return remote;
        }

        return passedOver ?? remote;
    }

    private async Task<NhAssistantFileReading> ReadLocalAsync(
        NhAssistantFileReaderDefinition reader,
        NhAssistantFileReadRequest request,
        CancellationToken cancellationToken)
    {
        var instance = (INhAssistantFileReader)services.GetRequiredService(reader.ReaderType);
        TaskResult<string> read;
        try
        {
            read = await instance.ReadAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // The reader's message may carry the file's content or name: log its type only.
            logger.LogWarning(
                "File reader {ReaderId} failed with {ExceptionType}.",
                reader.Id,
                exception.GetType().Name);
            return NhAssistantFileReading.Failed(reader.Id);
        }
        if (!read.Success || read.Data is null)
        {
            logger.LogWarning("File reader {ReaderId} returned no text.", reader.Id);
            return NhAssistantFileReading.Failed(reader.Id);
        }

        return NhAssistantFileReading.Read(reader.Id, read.Data);
    }
}
