using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// What people may upload to a conversation of one agent: the accepted media types, the largest file
/// and the most files of one message.
/// </summary>
internal sealed record NhAssistantUploadRules(
    IReadOnlyList<string> AcceptedMediaTypes,
    int MaxFileBytes,
    int MaxFiles);

/// <summary>
/// A file part of an upload request as the endpoint read it: the name and media type the person's
/// device gave it, its length and a way to open its content, each time from its start.
/// </summary>
internal sealed record NhAssistantUploadInput(
    string? FileName,
    string? MediaType,
    long Length,
    Func<Stream> OpenContent);

/// <summary>
/// Accepts a person's upload to a conversation (F-3): the media type must be accepted and match the
/// content, the file must fit the agent's limits, the application's
/// <see cref="INhAssistantUploadScanner"/>, when it registered one, must let it pass (A5), and only then
/// does the application's <see cref="INhAssistantFileSource.StoreUploadAsync"/> store it with the
/// default classification (F5). The refusals of the scanner and the source pass through as the upload
/// codes; nothing here logs names or content.
/// </summary>
internal sealed class NhAssistantUploads(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    NhAssistantTurnFiles turnFiles,
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
    /// What people may upload to a conversation with <paramref name="agentId"/>, or
    /// <see langword="null"/> when uploads are off or the agent takes no files (F11).
    /// </summary>
    public NhAssistantUploadRules? RulesOf(string agentId)
    {
        if (!Available)
        {
            return null;
        }

        var limits = turnFiles.LimitsOfAgent(agentId);
        return limits is null
            ? null
            : new NhAssistantUploadRules(registration.Uploads.AcceptedMediaTypes, limits.MaxFileBytes, limits.MaxFilesPerTurn);
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
        var mediaType = NhAiMediaTypes.Normalize(input.MediaType);
        if (mediaType is null || !rules.AcceptedMediaTypes.Contains(mediaType, StringComparer.Ordinal))
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
    /// platform names: PDF, PNG, JPEG, and Word and Excel documents, which are ZIP packages. Other
    /// accepted types are the application's to check.
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
        return mediaType switch
        {
            // A PDF reader finds the header within the first kilobyte.
            NhAiMediaTypes.Pdf => head.IndexOf("%PDF-"u8) >= 0,
            NhAiMediaTypes.Png => head.StartsWith(PngSignature),
            NhAiMediaTypes.Jpeg => head.StartsWith(JpegSignature),
            NhAiMediaTypes.Docx or NhAiMediaTypes.Xlsx => head.StartsWith("PK\u0003\u0004"u8),
            _ => true
        };
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private static TaskResult<NhAiFileReference> Failed(string code, string message)
    {
        return TaskResult<NhAiFileReference>.Failed(code, message);
    }
}
