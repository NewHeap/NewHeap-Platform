using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// What people may upload to a conversation of one agent: the accepted media types, the largest file,
/// the most files of one message and the extensions the application mapped to an accepted type
/// (<see cref="NhAssistantUploadOptions.MapExtension"/>), lowercase with their dot.
/// </summary>
internal sealed record NhAssistantUploadRules(
    IReadOnlyList<string> AcceptedMediaTypes,
    int MaxFileBytes,
    int MaxFiles,
    IReadOnlyDictionary<string, string> MappedExtensions);

/// <summary>
/// A file part of an upload request as the endpoint read it, or the file an application's channel
/// uploads: the name and media type the person's device gave it, its length and a way to open its
/// content, each time from its start.
/// </summary>
internal sealed record NhAssistantUploadInput(
    string? FileName,
    string? MediaType,
    long Length,
    Func<Stream> OpenContent)
{
    /// <summary>
    /// The upload of a readable, seekable <paramref name="content"/> from its current position to its
    /// end. Every opening reads it again from that position; the caller keeps and disposes the stream.
    /// </summary>
    /// <exception cref="ArgumentException">The stream cannot be read or cannot seek.</exception>
    public static NhAssistantUploadInput FromStream(string? fileName, string? mediaType, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || !content.CanSeek)
        {
            throw new ArgumentException("The content of an upload must be a readable stream that can seek, so it can be read again.", nameof(content));
        }

        var origin = content.Position;
        var length = Math.Max(0, content.Length - origin);
        return new NhAssistantUploadInput(fileName, mediaType, length, () => new NhAssistantRereadableContent(content, origin, length));
    }
}

/// <summary>
/// A read-only view of the caller's stream from <c>origin</c> for <c>length</c> bytes. Opening it moves
/// the caller's stream back to the origin; disposing it leaves the caller's stream open, because the
/// caller owns it and the upload reads it again for the scanner and the file source.
/// </summary>
internal sealed class NhAssistantRereadableContent : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _read;

    public NhAssistantRereadableContent(Stream inner, long origin, long length)
    {
        _inner = inner;
        _length = length;
        _inner.Position = origin;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var count = _inner.Read(buffer[..Allowed(buffer.Length)]);
        _read += count;
        return count;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await _inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken);
        _read += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
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

    /// <summary>
    /// The bytes a read may take: what is left of the view, so a stream that grew after the upload was
    /// measured gives no more than its measured length.
    /// </summary>
    private int Allowed(int requested)
    {
        var left = _length - _read;
        return (int)Math.Min(requested, Math.Max(0, left));
    }
}

/// <summary>
/// Accepts a person's upload to a conversation (F-3): the media type must be accepted and match the
/// content (a missing or generic type takes the accepted type of the file name's extension, see
/// <see cref="NhAssistantUploadMediaTypes"/>), the file must fit the agent's limits, the application's
/// <see cref="INhAssistantUploadScanner"/>, when it registered one, must let it pass (A5), and only then
/// does the application's <see cref="INhAssistantFileSource.StoreUploadAsync"/> store it with the
/// default classification (F5). The refusals of the scanner and the source pass through as the upload
/// codes; nothing here logs names or content.
/// </summary>
internal sealed class NhAssistantUploads(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    NhAssistantTurnFiles turnFiles,
    INhAiModelProfileRegistry profiles,
    ILogger<NhAssistantUploads> logger)
{
    /// <summary>
    /// The bytes of the content the upload's media type is checked against.
    /// </summary>
    internal const int SignatureBytes = 1_024;

    /// <summary>
    /// Whether people can upload at all: the application registered a file source and accepts at
    /// least one media type. Checks the registration without constructing the application's source.
    /// </summary>
    public bool Available => registration.Uploads.AcceptedMediaTypes.Count > 0
        && services.GetService<IServiceProviderIsService>()?.IsService(typeof(INhAssistantFileSource)) == true;

    /// <summary>
    /// What people may upload to a conversation with <paramref name="agent"/>, or
    /// <see langword="null"/> when uploads are off, the agent takes no files (F11) or its profile chain
    /// may not receive what a message with an upload carries (FB24).
    /// </summary>
    public NhAssistantUploadRules? RulesOf(NhAssistantAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (!Available)
        {
            return null;
        }

        var limits = turnFiles.LimitsOfAgent(agent.Id);
        if (limits is null || !MayReceiveUploads(agent))
        {
            return null;
        }

        // Only a mapping to an accepted type names anything; the client receives the same mappings.
        var accepted = registration.Uploads.AcceptedMediaTypes;
        var mapped = registration.Uploads.MappedExtensions
            .Where(mapping => accepted.Contains(mapping.Value, StringComparer.Ordinal))
            .ToDictionary(mapping => mapping.Key, mapping => mapping.Value, StringComparer.Ordinal);
        return new NhAssistantUploadRules(accepted, limits.MaxFileBytes, limits.MaxFilesPerTurn, mapped);
    }

    /// <summary>
    /// Whether a profile of the agent's chain may serve a person's message with an upload: a turn takes
    /// the higher of the agent's own classification and the upload's
    /// (<see cref="NhAssistantUploadOptions.DefaultClassification"/>), and the profile that serves it
    /// must permit that classification in the execution region, as the turn resolves its profile. A
    /// profile that is not registered serves nothing.
    /// </summary>
    internal bool MayReceiveUploads(NhAssistantAgent agent)
    {
        var configured = agent.DataClassification ?? NhAiDataClassification.Internal;
        var upload = registration.Uploads.DefaultClassification;
        var classification = upload > configured ? upload : configured;
        return profiles.TryGet(agent.Definition.ProfileName, out var profile)
            && NhAssistantRegionChecks.PermitsTogether(profiles, profile, classification, registration.ExecutionRegion);
    }

    /// <summary>
    /// Checks the upload against <paramref name="rules"/> and has the application's file source store
    /// it for <paramref name="actor"/>. Returns the reference a message then carries.
    /// </summary>
    public async Task<TaskResult<NhAiFileReference>> StoreAsync(
        Guid conversationId,
        NhAssistantUploadInput input,
        NhAssistantUploadRules rules,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(actor);

        var name = NameOf(input.FileName);
        if (name is null || input.Length <= 0)
        {
            return Failed(NhAssistantErrorCodes.FileUploadInvalid, "The upload has no valid name or no content.");
        }
        // A device that gives no type or a generic one has the file name's extension name it, only as
        // a type the application accepts; the content check and the scanner below still apply.
        var mediaType = NhAssistantUploadMediaTypes.Resolve(name, input.MediaType, rules.AcceptedMediaTypes, rules.MappedExtensions);
        if (mediaType is null)
        {
            return Failed(NhAssistantErrorCodes.FileTypeNotAccepted, "Files of this type cannot be uploaded.");
        }
        if (input.Length > rules.MaxFileBytes)
        {
            return Failed(NhAssistantErrorCodes.FileTooLarge, "The file is larger than the agent may read.");
        }

        await using (var head = input.OpenContent())
        {
            if (!await MatchesSignatureAsync(head, mediaType, cancellationToken))
            {
                return Failed(NhAssistantErrorCodes.FileTypeNotAccepted, "The content does not match the media type of the upload.");
            }
        }

        // The application's scanner, when it registered one, sees the content before its file source
        // stores anything (A5).
        if (await ScanAsync(conversationId, name, mediaType, input, actor, cancellationToken) is { } scanRefusal)
        {
            return scanRefusal;
        }

        await using var content = input.OpenContent();
        var upload = new NhAssistantFileUpload(conversationId, name, mediaType, input.Length, content)
        {
            Classification = registration.Uploads.DefaultClassification
        };
        var source = services.GetRequiredService<INhAssistantFileSource>();
        TaskResult<NhAiFileReference> stored;
        try
        {
            stored = await source.StoreUploadAsync(upload, actor, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // The application's source may carry the file's name or content in its messages; log the type only.
            logger.LogWarning(
                "The file source failed to store an upload to conversation {ConversationId} with {ExceptionType}.",
                conversationId,
                exception.GetType().Name);
            return Failed(NhAssistantErrorCodes.FileUploadFailed, "The upload could not be stored.");
        }

        if (!stored.Success)
        {
            var code = NhAssistantResults.CodeOf(stored) switch
            {
                NhAssistantErrorCodes.FilesUnavailable => NhAssistantErrorCodes.FilesUnavailable,
                NhAssistantErrorCodes.FileTooLarge => NhAssistantErrorCodes.FileTooLarge,
                NhAssistantErrorCodes.FileTypeNotAccepted or NhAssistantErrorCodes.FileTypeNotSupported => NhAssistantErrorCodes.FileTypeNotAccepted,
                NhAssistantErrorCodes.FileUploadFailed or NhAssistantErrorCodes.FileUnavailable => NhAssistantErrorCodes.FileUploadFailed,
                _ => NhAssistantErrorCodes.FileUploadRefused
            };
            return Failed(code, "The application did not store the upload.");
        }
        if (!NhAiFileReference.IsValid(stored.Data))
        {
            logger.LogWarning(
                "The file source returned an invalid reference for an upload to conversation {ConversationId}.",
                conversationId);
            return Failed(NhAssistantErrorCodes.FileUploadFailed, "The upload could not be stored.");
        }

        return TaskResult<NhAiFileReference>.Succeeded(stored.Data);
    }

    /// <summary>
    /// Has the application's <see cref="INhAssistantUploadScanner"/> scan the upload, when it registered
    /// one; <see langword="null"/> when the upload may be stored. A refusal is
    /// <see cref="NhAssistantErrorCodes.FileScanRefused"/>; a scan that could not run, reported as
    /// <see cref="NhAssistantErrorCodes.FileUploadFailed"/> or thrown, is
    /// <see cref="NhAssistantErrorCodes.FileUploadFailed"/>. The scanner's messages are neither logged
    /// nor shown, because they may name the file or quote its content.
    /// </summary>
    private async Task<TaskResult<NhAiFileReference>?> ScanAsync(
        Guid conversationId,
        string name,
        string mediaType,
        NhAssistantUploadInput input,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        if (registration.Uploads.ScannerType is not { } scannerType)
        {
            return null;
        }

        var scanner = (INhAssistantUploadScanner)services.GetRequiredService(scannerType);
        TaskResult scanned;
        await using (var content = input.OpenContent())
        {
            var upload = new NhAssistantFileUpload(conversationId, name, mediaType, input.Length, content)
            {
                Classification = registration.Uploads.DefaultClassification
            };
            try
            {
                scanned = await scanner.ScanAsync(upload, actor, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(
                    "The upload scanner failed on an upload to conversation {ConversationId} with {ExceptionType}.",
                    conversationId,
                    exception.GetType().Name);
                return Failed(NhAssistantErrorCodes.FileUploadFailed, "The upload could not be scanned.");
            }
        }

        if (scanned.Success)
        {
            return null;
        }
        if (NhAssistantResults.CodeOf(scanned) == NhAssistantErrorCodes.FileUploadFailed)
        {
            logger.LogWarning("The upload scanner could not scan an upload to conversation {ConversationId}.", conversationId);
            return Failed(NhAssistantErrorCodes.FileUploadFailed, "The upload could not be scanned.");
        }

        logger.LogInformation("The upload scanner refused an upload to conversation {ConversationId}.", conversationId);
        return Failed(NhAssistantErrorCodes.FileScanRefused, "The upload did not pass the scan.");
    }

    /// <summary>
    /// The file name without a path, or <see langword="null"/> when nothing usable is left: empty,
    /// longer than <see cref="NhAiFileReference.MaxNameLength"/> or with control characters.
    /// </summary>
    internal static string? NameOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var separator = fileName.LastIndexOfAny(['/', '\\']);
        var name = (separator < 0 ? fileName : fileName[(separator + 1)..]).Trim();
        return name.Length == 0 || name.Length > NhAiFileReference.MaxNameLength || name.Any(char.IsControl)
            ? null
            : name;
    }

    /// <summary>
    /// Whether the content starts as files of <paramref name="mediaType"/> do, for the types the
    /// platform names: PDF, PNG, JPEG, and Word, Excel and PowerPoint documents, which are ZIP packages.
    /// Text (<c>text/*</c>, JSON and XML, also with a <c>+json</c> or <c>+xml</c> suffix) is refused when
    /// it starts as a binary format does (<see cref="StartsAsBinary"/>). Other accepted types are the
    /// application's to check.
    /// </summary>
    internal static async Task<bool> MatchesSignatureAsync(Stream content, string mediaType, CancellationToken cancellationToken)
    {
        var head = new byte[SignatureBytes];
        var read = 0;
        while (read < head.Length)
        {
            var count = await content.ReadAsync(head.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                break;
            }
            read += count;
        }

        return MatchesSignature(head.AsSpan(0, read), mediaType);
    }

    internal static bool MatchesSignature(ReadOnlySpan<byte> head, string mediaType)
    {
        if (IsText(mediaType))
        {
            // An .xls workbook renamed to .csv, or a package or PDF named .txt, is no text. There is no
            // NUL-byte check: UTF-16 text, such as Excel's "Unicode text" export, has NUL bytes.
            return !StartsAsBinary(head);
        }

        return mediaType switch
        {
            // A PDF reader finds the header within the first kilobyte.
            NhAiMediaTypes.Pdf => head.IndexOf("%PDF-"u8) >= 0,
            NhAiMediaTypes.Png => head.StartsWith(PngSignature),
            NhAiMediaTypes.Jpeg => head.StartsWith(JpegSignature),
            NhAiMediaTypes.Docx or NhAiMediaTypes.Xlsx or NhAssistantUploadMediaTypes.Pptx => head.StartsWith(ZipSignature),
            _ => true
        };
    }

    /// <summary>
    /// Whether <paramref name="head"/> starts with the signature of a binary format that a text upload
    /// may be in disguise: an OLE2 compound file (Word, Excel and PowerPoint 97 to 2003), a ZIP package
    /// (also an empty one), a PDF, or a PNG, JPEG or GIF image.
    /// </summary>
    internal static bool StartsAsBinary(ReadOnlySpan<byte> head)
    {
        return head.StartsWith(Ole2Signature)
            || head.StartsWith(ZipSignature)
            || head.StartsWith(EmptyZipSignature)
            || head.StartsWith("%PDF-"u8)
            || head.StartsWith(PngSignature)
            || head.StartsWith(JpegSignature)
            || head.StartsWith("GIF87a"u8)
            || head.StartsWith("GIF89a"u8);
    }

    /// <summary>
    /// Whether <paramref name="mediaType"/> is text: <c>text/*</c>, <c>application/json</c>,
    /// <c>application/xml</c> or a type with the <c>+json</c> or <c>+xml</c> suffix.
    /// </summary>
    private static bool IsText(string mediaType)
    {
        return mediaType.StartsWith("text/", StringComparison.Ordinal)
            || mediaType is "application/json" or "application/xml"
            || mediaType.EndsWith("+json", StringComparison.Ordinal)
            || mediaType.EndsWith("+xml", StringComparison.Ordinal);
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    private static readonly byte[] EmptyZipSignature = [0x50, 0x4B, 0x05, 0x06];

    private static readonly byte[] Ole2Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static TaskResult<NhAiFileReference> Failed(string code, string message)
    {
        return TaskResult<NhAiFileReference>.Failed(code, message);
    }
}

/// <summary>
/// The media type an upload is checked and stored as. Browsers send no type, or a generic one, for
/// files their system does not know, and Windows names some extensions after the program that opens
/// them, such as <c>application/vnd.ms-excel</c> for a <c>.csv</c> file. For such a type the file
/// name's extension names the type, but only a type the application accepts: the type the application
/// mapped the extension to (<see cref="NhAssistantUploadOptions.MapExtension"/>), otherwise the
/// platform's. A specific type the application does not accept stays refused: the extension never
/// overrides it. The chat composer of <c>@newheap/platform-ai-chat</c> applies the same table, with the
/// application's mappings from the agent's upload rules, before it uploads.
/// </summary>
internal static class NhAssistantUploadMediaTypes
{
    /// <summary>A PowerPoint presentation (<c>.pptx</c>), a ZIP package.</summary>
    internal const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    /// <summary>
    /// Types that say nothing about the content, whatever the file's extension.
    /// </summary>
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "application/octet-stream",
        "binary/octet-stream",
        "application/unknown"
    };

    /// <summary>
    /// The media type of each extension the platform knows.
    /// </summary>
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = NhAiMediaTypes.Pdf,
        [".png"] = NhAiMediaTypes.Png,
        [".jpg"] = NhAiMediaTypes.Jpeg,
        [".jpeg"] = NhAiMediaTypes.Jpeg,
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".docx"] = NhAiMediaTypes.Docx,
        [".xlsx"] = NhAiMediaTypes.Xlsx,
        [".pptx"] = Pptx,
        [".doc"] = "application/msword",
        [".xls"] = "application/vnd.ms-excel",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".json"] = "application/json",
        [".xml"] = "application/xml"
    };

    /// <summary>
    /// The types devices commonly send for an extension instead of its own type: the program that
    /// opens it, an outdated name or the container format.
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [".csv"] =
        [
            "application/vnd.ms-excel",
            "application/csv",
            "application/x-csv",
            "text/x-csv",
            "text/comma-separated-values",
            "text/x-comma-separated-values"
        ],
        [".md"] = ["text/x-markdown"],
        [".markdown"] = ["text/x-markdown"],
        [".jpg"] = ["image/jpg", "image/pjpeg"],
        [".jpeg"] = ["image/jpg", "image/pjpeg"],
        [".png"] = ["image/x-png"],
        [".docx"] = ["application/zip", "application/x-zip-compressed"],
        [".xlsx"] = ["application/zip", "application/x-zip-compressed"],
        [".pptx"] = ["application/zip", "application/x-zip-compressed"],
        // Browsers send the older name of XML; an application that accepts only it keeps receiving it.
        [".xml"] = ["text/xml"]
    };

    /// <summary>
    /// The accepted media type of an upload named <paramref name="fileName"/> that the device gave
    /// <paramref name="declared"/>, or <see langword="null"/> when no accepted type applies. A
    /// missing or generic declared type is replaced by the extension's type when
    /// <paramref name="accepted"/> lists it; otherwise the declared type counts as it is. The
    /// extension's type is the one of <paramref name="mappedExtensions"/> (lowercase extensions with
    /// their dot) when it is accepted, otherwise the platform's.
    /// </summary>
    public static string? Resolve(
        string fileName,
        string? declared,
        IReadOnlyList<string> accepted,
        IReadOnlyDictionary<string, string>? mappedExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(accepted);

        var normalized = NhAiMediaTypes.Normalize(declared);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (normalized is null || IsGeneric(normalized, extension))
        {
            if (TypeOfExtension(extension, accepted, mappedExtensions) is { } derived)
            {
                return derived;
            }
        }

        return normalized is not null && accepted.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : null;
    }

    /// <summary>
    /// The accepted type <paramref name="extension"/> names: the application's mapping, otherwise the
    /// platform's table; <see langword="null"/> when neither names an accepted type.
    /// </summary>
    private static string? TypeOfExtension(
        string extension,
        IReadOnlyList<string> accepted,
        IReadOnlyDictionary<string, string>? mappedExtensions)
    {
        if (mappedExtensions is not null
            && mappedExtensions.TryGetValue(extension, out var mapped)
            && accepted.Contains(mapped, StringComparer.Ordinal))
        {
            return mapped;
        }

        return ByExtension.TryGetValue(extension, out var known) && accepted.Contains(known, StringComparer.Ordinal)
            ? known
            : null;
    }

    /// <summary>
    /// Whether <paramref name="mediaType"/> says nothing about a file with <paramref name="extension"/>.
    /// </summary>
    private static bool IsGeneric(string mediaType, string extension)
    {
        if (Generic.Contains(mediaType))
        {
            return true;
        }

        return Aliases.TryGetValue(extension, out var aliases) && aliases.Contains(mediaType, StringComparer.Ordinal);
    }
}
