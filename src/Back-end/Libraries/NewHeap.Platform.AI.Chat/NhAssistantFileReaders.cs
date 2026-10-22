using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// A reading tool of the application, registered with <c>AddFileReader&lt;T&gt;()</c>: it turns a file of
/// a type the model cannot read itself, such as a Word document, into text. The assistant asks it only
/// when the profile that serves the turn does not declare the file's media type, the reader accepts
/// that type and it permits the file's classification. The model then reads the text as untrusted
/// external data, never as instructions. Return a failed result when the file cannot be read; the
/// assistant refuses the file with <see cref="NhAssistantErrorCodes.FileReaderFailed"/> and never
/// shows the model, the person or the log the reader's own messages.
/// </summary>
public interface INhAssistantFileReader
{
    /// <summary>
    /// The text of the file in <paramref name="request"/>. The file and turn limits of
    /// <see cref="NhAssistantLimits"/> count the tokens of this text.
    /// </summary>
    Task<TaskResult<string>> ReadAsync(NhAssistantFileReadRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A file a reading tool turns into text: the reference, the media type the application's file source
/// reported, the content the assistant read within <see cref="NhAssistantLimits.MaxFileBytes"/>, the
/// file's classification and the actor of the turn the file was opened for.
/// </summary>
/// <param name="Reference">The file as the tool result or the message named it.</param>
/// <param name="MediaType">The media type of the content, lowercase and without parameters.</param>
/// <param name="Content">The content of the file; valid during the call only.</param>
/// <param name="Classification">How sensitive the file is, as the file source reported it.</param>
/// <param name="Actor">The actor of the turn that reads the file.</param>
public sealed record NhAssistantFileReadRequest(
    NhAiFileReference Reference,
    string MediaType,
    ReadOnlyMemory<byte> Content,
    NhAiDataClassification Classification,
    NhAssistantFileActor Actor);

/// <summary>
/// Declares what a reading tool registered with <c>AddFileReader&lt;T&gt;()</c> reads: the media types
/// it accepts and the data classifications it may receive. A reader receives no file of another type
/// or of a classification it does not permit; it returns text by its contract.
/// </summary>
public sealed class NhAssistantFileReaderBuilder
{
    /// <summary>
    /// The most media types one reader declares.
    /// </summary>
    public const int MaxMediaTypes = 16;

    private readonly HashSet<string> _mediaTypes = new(StringComparer.Ordinal);
    private readonly HashSet<NhAiDataClassification> _classifications = [NhAiDataClassification.Public];

    internal NhAssistantFileReaderBuilder()
    {
    }

    /// <summary>
    /// Declares the media types the reader turns into text, such as <see cref="NhAiMediaTypes.Docx"/>
    /// and <see cref="NhAiMediaTypes.Xlsx"/>. They are compared lowercase and without parameters.
    /// </summary>
    public NhAssistantFileReaderBuilder AcceptMediaTypes(params string[] mediaTypes)
    {
        ArgumentNullException.ThrowIfNull(mediaTypes);
        foreach (var mediaType in mediaTypes)
        {
            var normalized = NhAiMediaTypes.Normalize(mediaType)
                ?? throw new ArgumentException("A media type has the form type/subtype, such as application/pdf.", nameof(mediaTypes));
            _mediaTypes.Add(normalized);
        }

        return this;
    }

    /// <summary>
    /// Declares the classifications of files the reader may receive, next to
    /// <see cref="NhAiDataClassification.Public"/>, as a model profile permits them. A file of another
    /// classification passes the reader by; a reader that sends files elsewhere, such as an external
    /// service, should permit only what may leave the platform.
    /// </summary>
    public NhAssistantFileReaderBuilder PermitDataClassifications(params NhAiDataClassification[] classifications)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        foreach (var classification in classifications)
        {
            if (!Enum.IsDefined(classification))
            {
                throw new ArgumentOutOfRangeException(nameof(classifications));
            }
            _classifications.Add(classification);
        }

        return this;
    }

    internal NhAssistantFileReaderDefinition Build(string readerId, Type readerType)
    {
        if (_mediaTypes.Count == 0)
        {
            throw new InvalidOperationException($"File reader '{readerId}' accepts no media type; declare them with AcceptMediaTypes.");
        }
        if (_mediaTypes.Count > MaxMediaTypes)
        {
            throw new InvalidOperationException($"File reader '{readerId}' declares more than {MaxMediaTypes} media types.");
        }

        return new NhAssistantFileReaderDefinition(
            readerId,
            readerType,
            _mediaTypes.ToHashSet(StringComparer.Ordinal),
            _classifications.ToHashSet());
    }
}

/// <summary>
/// The readers of an MCP server in the administration: per reader, the synced tool that turns a file
/// into text, the media types it reads, the highest classification that may be sent to it and its
/// priority. Sending a file to an MCP server is data leaving the platform, so a file above a reader's
/// classification never reaches it. The assistant asks the readers of the application first, in
/// registration order, then the MCP readers by priority, highest first, then by server id and tool
/// name.
/// </summary>
public static class NhAssistantMcpReaders
{
    /// <summary>
    /// The most readers one server has.
    /// </summary>
    public const int MaxReaders = 16;

    /// <summary>
    /// The most media types one reader reads.
    /// </summary>
    public const int MaxMediaTypes = 16;

    /// <summary>
    /// The longest remote tool name of a reader.
    /// </summary>
    public const int MaxToolNameLength = 128;

    /// <summary>
    /// The lowest priority of a reader.
    /// </summary>
    public const int MinPriority = -1_000;

    /// <summary>
    /// The highest priority of a reader.
    /// </summary>
    public const int MaxPriority = 1_000;

    /// <summary>
    /// The longest stored list of media types: <see cref="MaxMediaTypes"/> types of at most
    /// <see cref="NhAiFileReference.MaxMediaTypeLength"/> characters with their separators.
    /// </summary>
    public const int MaxMediaTypesLength = MaxMediaTypes * (NhAiFileReference.MaxMediaTypeLength + 1);

    /// <summary>
    /// How the audit names an MCP server as reader (<see cref="NhAssistantAuditEvent.FileReaderId"/>):
    /// <c>mcp.&lt;server&gt;</c>.
    /// </summary>
    public static string ReaderIdOf(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        return "mcp." + serverId;
    }

    /// <summary>
    /// The media types of a stored list.
    /// </summary>
    internal static IReadOnlyList<string> Split(string? mediaTypes)
    {
        return string.IsNullOrWhiteSpace(mediaTypes)
            ? []
            : mediaTypes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
/// A reading tool of the application as composition registered it.
/// </summary>
internal sealed record NhAssistantFileReaderDefinition(
    string Id,
    Type ReaderType,
    IReadOnlySet<string> MediaTypes,
    IReadOnlySet<NhAiDataClassification> Classifications)
{
    public bool Accepts(string mediaType)
    {
        return MediaTypes.Contains(mediaType);
    }

    public bool Permits(NhAiDataClassification classification)
    {
        return Classifications.Contains(classification);
    }
}
