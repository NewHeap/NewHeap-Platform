using System.Text.RegularExpressions;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The application's files for the assistant, registered with <c>UseFileSource&lt;T&gt;()</c>. The
/// application decides where files live, who may read them and how they are classified; the
/// assistant keeps only references and hashes, never content. Without a file source a turn takes
/// no files: tool results that name files and attachments are refused with
/// <see cref="NhAssistantErrorCodes.FilesUnavailable"/>.
/// </summary>
public interface INhAssistantFileSource
{
    /// <summary>
    /// Opens <paramref name="reference"/> for <paramref name="actor"/>, the actor of the turn that
    /// needs it: the person of a chat turn, or the agent and the people it works for in a run. Check
    /// that this actor may read the file now and return a failed result otherwise, with
    /// <see cref="NhAssistantErrorCodes.FileNotFound"/> or <see cref="NhAssistantErrorCodes.FileForbidden"/>.
    /// A turn that opens a file from an earlier turn asks again with its own actor. The assistant
    /// disposes the returned file.
    /// </summary>
    Task<TaskResult<NhAssistantFile>> OpenAsync(
        NhAiFileReference reference,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a file a person uploads to a conversation in the application's own storage, with its
    /// own retention, and returns its reference, which a message then carries. Return a failed result
    /// for a type, size or virus-scan outcome the application refuses; an application that takes no
    /// uploads always returns one.
    /// </summary>
    Task<TaskResult<NhAiFileReference>> StoreUploadAsync(
        NhAssistantFileUpload upload,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// An opened file: its content and what the application knows about it. The assistant reads the
/// content once, within <see cref="NhAssistantLimits.MaxFileBytes"/>, and disposes the file.
/// </summary>
public sealed class NhAssistantFile : IAsyncDisposable
{
    /// <param name="content">The content, read from its current position to its end.</param>
    /// <param name="mediaType">The media type of the content, such as <c>application/pdf</c>.</param>
    /// <param name="length">The length of the content in bytes.</param>
    /// <param name="classification">How sensitive the content is. A turn takes the highest
    /// classification of its files, its agent and its skill, and its model profile must permit it.</param>
    public NhAssistantFile(
        Stream content,
        string mediaType,
        long length,
        NhAiDataClassification classification)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!Enum.IsDefined(classification))
        {
            throw new ArgumentOutOfRangeException(nameof(classification));
        }

        Content = content;
        MediaType = mediaType;
        Length = length;
        Classification = classification;
    }

    public Stream Content { get; }

    public string MediaType { get; }

    public long Length { get; }

    public NhAiDataClassification Classification { get; }

    /// <summary>
    /// The number of pages of a paged document such as a PDF, when the application knows it; it is
    /// checked against <see cref="NhAssistantLimits.MaxFilePages"/>.
    /// </summary>
    public int? PageCount { get; init; }

    public ValueTask DisposeAsync()
    {
        return Content.DisposeAsync();
    }
}

/// <summary>
/// The actor a file is opened or stored for, and how the file reached the turn.
/// </summary>
/// <param name="ActorId">The actor of the turn as tools see it: the person of a chat turn or of a
/// message in an agent's thread, the initiator of a run a person started, or the agent's own actor id
/// (<c>assistant-agent:&lt;id&gt;</c>) in a run nobody started.</param>
/// <param name="ActorKind">Whether <paramref name="ActorId"/> is a person or the agent.</param>
/// <param name="AgentId">The agent of the turn.</param>
/// <param name="ConversationId">The conversation or agent thread of the turn.</param>
public sealed record NhAssistantFileActor(
    string ActorId,
    NhAiActorKind ActorKind,
    string AgentId,
    Guid ConversationId)
{
    /// <summary>
    /// The person or technical account that answers for the turn.
    /// </summary>
    public string? AccountableOwnerId { get; init; }

    /// <summary>
    /// The person who started the run, in a run a person started.
    /// </summary>
    public string? InitiatorActorId { get; init; }

    public string? TenantId { get; init; }

    /// <summary>
    /// The subject of the agent's thread; <see langword="null"/> in a chat.
    /// </summary>
    public NhAiSubjectRef? Subject { get; init; }

    /// <summary>
    /// The subject of the person's chat, when the chat is about one (FB25), as
    /// <see cref="NhAiInvocationContext.ConversationSubject"/>: the person had a role on it when the turn
    /// or the upload started. The file is still opened or stored for <see cref="ActorId"/>, with that
    /// person's rights; <see langword="null"/> in a chat without a subject and in an agent's thread,
    /// whose subject is <see cref="Subject"/>.
    /// </summary>
    public NhAiSubjectRef? ConversationSubject { get; init; }

    public Guid? RunId { get; init; }

    /// <summary>
    /// How the file reached the turn: one of <see cref="NhAssistantFileOrigins"/>.
    /// </summary>
    public string Origin { get; init; } = NhAssistantFileOrigins.ToolResult;

    /// <summary>
    /// The tool whose result named the file, for <see cref="NhAssistantFileOrigins.ToolResult"/>.
    /// </summary>
    public string? ToolId { get; init; }
}

/// <summary>
/// A file a person uploads to a conversation, before the application stores it. The upload endpoint
/// has checked it against <see cref="NhAssistantUploadOptions"/> and the file limits of the
/// conversation's agent before the file source receives it.
/// </summary>
/// <param name="ConversationId">The conversation the file is for.</param>
/// <param name="Name">The file name the person's device gave it, without a path.</param>
/// <param name="MediaType">The media type the upload declares, lowercase and without parameters.</param>
/// <param name="Length">The length of the content in bytes.</param>
/// <param name="Content">The content; the caller disposes it.</param>
public sealed record NhAssistantFileUpload(
    Guid ConversationId,
    string Name,
    string MediaType,
    long Length,
    Stream Content)
{
    /// <summary>
    /// The classification the assistant gives the upload:
    /// <see cref="NhAssistantUploadOptions.DefaultClassification"/>, <c>Confidential</c> unless the
    /// application configured another. Store it with the file and report it from
    /// <see cref="INhAssistantFileSource.OpenAsync"/>; the application may raise it, for example after
    /// it inspected the content.
    /// </summary>
    public NhAiDataClassification Classification { get; init; } = NhAiDataClassification.Confidential;
}

/// <summary>
/// How people upload files to a conversation, configured with
/// <see cref="NhAssistantBuilder.ConfigureUploads"/>. The upload endpoint accepts a file only when the
/// application registered a file source, the conversation's agent accepts files and the file is of an
/// accepted media type within the agent's <see cref="NhAssistantLimits.MaxFileBytes"/>; then the file
/// source stores it.
/// </summary>
public sealed partial class NhAssistantUploadOptions
{
    /// <summary>
    /// The most media types an application accepts for uploads.
    /// </summary>
    public const int MaxAcceptedMediaTypes = 50;

    /// <summary>
    /// The most file name extensions an application maps to a media type with <see cref="MapExtension"/>.
    /// </summary>
    public const int MaxMappedExtensions = 100;

    /// <summary>
    /// The longest extension <see cref="MapExtension"/> takes, its dot included.
    /// </summary>
    private const int MaxExtensionLength = 32;

    private IReadOnlyList<string> _mediaTypes = DefaultMediaTypes;

    private IReadOnlyDictionary<string, string> _extensions = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly string[] DefaultMediaTypes =
    [
        NhAiMediaTypes.Pdf,
        NhAiMediaTypes.Png,
        NhAiMediaTypes.Jpeg,
        NhAiMediaTypes.Docx,
        NhAiMediaTypes.Xlsx
    ];

    /// <summary>
    /// The classification an upload gets (<see cref="NhAssistantFileUpload.Classification"/>):
    /// <c>Confidential</c> by default. A turn takes the highest classification of its files, so its
    /// model profile, or a fallback, must permit it.
    /// </summary>
    public NhAiDataClassification DefaultClassification { get; set; } = NhAiDataClassification.Confidential;

    /// <summary>
    /// The media types people may upload, lowercase and without parameters: PDF, PNG, JPEG, Word
    /// (<c>.docx</c>) and Excel (<c>.xlsx</c>) by default. Whether a model reads a type itself or a
    /// reading tool turns it into text is decided per turn; a type nothing reads is refused there with
    /// <see cref="NhAssistantErrorCodes.FileTypeNotSupported"/>. Without types people upload nothing,
    /// while tools can still name files.
    /// </summary>
    public IReadOnlyList<string> AcceptedMediaTypes => _mediaTypes;

    /// <summary>
    /// Replaces the media types people may upload.
    /// </summary>
    public NhAssistantUploadOptions AcceptMediaTypes(params string[] mediaTypes)
    {
        ArgumentNullException.ThrowIfNull(mediaTypes);
        var normalized = new List<string>(mediaTypes.Length);
        foreach (var mediaType in mediaTypes)
        {
            var essence = NhAiMediaTypes.Normalize(mediaType)
                ?? throw new ArgumentException($"'{mediaType}' is not a media type.", nameof(mediaTypes));
            if (!normalized.Contains(essence, StringComparer.Ordinal))
            {
                normalized.Add(essence);
            }
        }
        if (normalized.Count > MaxAcceptedMediaTypes)
        {
            throw new ArgumentException(
                $"An application accepts at most {MaxAcceptedMediaTypes} media types for uploads.",
                nameof(mediaTypes));
        }

        _mediaTypes = normalized.ToArray();
        return this;
    }

    /// <summary>
    /// The file name extensions the application mapped with <see cref="MapExtension"/>: each extension
    /// in lowercase with its dot, such as <c>.dwg</c>, and the media type it names.
    /// </summary>
    public IReadOnlyDictionary<string, string> MappedExtensions => _extensions;

    /// <summary>
    /// Maps the file name extension <paramref name="extension"/>, such as <c>.dwg</c>, to
    /// <paramref name="mediaType"/>, such as <c>application/acad</c>. An upload with that extension and
    /// no media type, a generic one such as <c>application/octet-stream</c> or a type devices send for
    /// that extension then takes <paramref name="mediaType"/>, exactly as for the extensions the platform
    /// knows, but only while the application accepts it (<see cref="AcceptMediaTypes"/>); a mapping to a
    /// type it does not accept has no effect. A specific declared type is never replaced, and the content
    /// check and the scanner still run. A mapping takes precedence over the platform's type of the same
    /// extension, for example <c>.xml</c> to <c>text/xml</c> for an application that accepts only that,
    /// and a later mapping of an extension replaces an earlier one. The agents' upload rules carry the
    /// mappings of accepted types, so the composer of <c>@newheap/platform-ai-chat</c> derives alike and
    /// its file picker offers the extensions.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="extension"/> is not a dot followed by letters, digits, <c>_</c>, <c>+</c> or
    /// <c>-</c> (at most 32 characters), <paramref name="mediaType"/> is not a media type, or the
    /// application maps more than <see cref="MaxMappedExtensions"/> extensions.
    /// </exception>
    public NhAssistantUploadOptions MapExtension(string extension, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(mediaType);

        var normalizedExtension = extension.Trim().ToLowerInvariant();
        if (normalizedExtension.Length > MaxExtensionLength || !ExtensionPattern().IsMatch(normalizedExtension))
        {
            throw new ArgumentException($"'{extension}' is not a file name extension such as '.dwg'.", nameof(extension));
        }
        var essence = NhAiMediaTypes.Normalize(mediaType)
            ?? throw new ArgumentException($"'{mediaType}' is not a media type.", nameof(mediaType));

        var extensions = new Dictionary<string, string>(_extensions, StringComparer.Ordinal)
        {
            [normalizedExtension] = essence
        };
        if (extensions.Count > MaxMappedExtensions)
        {
            throw new ArgumentException(
                $"An application maps at most {MaxMappedExtensions} extensions for uploads.",
                nameof(extension));
        }

        _extensions = extensions;
        return this;
    }

    /// <summary>
    /// Scans every upload with <typeparamref name="TScanner"/>, registered as a scoped service, before
    /// the file source stores it: after the checks of the type, the size and the content's signature.
    /// Without a scanner nothing is scanned. A later call replaces the scanner.
    /// </summary>
    public NhAssistantUploadOptions UseScanner<TScanner>()
        where TScanner : class, INhAssistantUploadScanner
    {
        ScannerType = typeof(TScanner);
        return this;
    }

    /// <summary>
    /// The scanner of <see cref="UseScanner{TScanner}"/>; <see langword="null"/> scans nothing.
    /// </summary>
    internal Type? ScannerType { get; private set; }

    internal NhAssistantUploadOptions Clone()
    {
        return (NhAssistantUploadOptions)MemberwiseClone();
    }

    /// <summary>
    /// Throws when the default classification is not a classification.
    /// </summary>
    internal void Validate()
    {
        if (!Enum.IsDefined(DefaultClassification))
        {
            throw new InvalidOperationException("The default classification of uploads must be a defined NhAiDataClassification.");
        }
    }

    /// <summary>
    /// The extension pattern of <see cref="MapExtension"/>: a dot, then one character class repeated, so a
    /// match is linear in the input and needs no match timeout.
    /// </summary>
    [GeneratedRegex("^\\.[a-z0-9][a-z0-9_+-]*$", RegexOptions.CultureInvariant)]
    internal static partial Regex ExtensionPattern();
}

/// <summary>
/// Scans a file a person uploads before the application's file source stores it, for example with a
/// virus scanner. Register it with <c>ConfigureUploads(uploads =&gt; uploads.UseScanner&lt;T&gt;())</c>. It
/// runs as a fixed step of every upload, after the checks of the media type, the size and the content's
/// signature and before <see cref="INhAssistantFileSource.StoreUploadAsync"/>; a file it refuses is never
/// stored. Without a scanner nothing is scanned.
/// </summary>
public interface INhAssistantUploadScanner
{
    /// <summary>
    /// Scans <paramref name="upload"/> for <paramref name="actor"/>. Return a succeeded result to let the
    /// file source store it, and a failed result to refuse it: the upload is refused with
    /// <see cref="NhAssistantErrorCodes.FileScanRefused"/>. Return a failed result with
    /// <see cref="NhAssistantErrorCodes.FileUploadFailed"/> when the scan could not run, for example
    /// because the scanning service is unreachable; the upload then fails with that code and the person
    /// may try again. An exception counts as such a failure. The assistant never shows or logs the
    /// scanner's messages, and disposes the content after the scan.
    /// </summary>
    Task<TaskResult> ScanAsync(
        NhAssistantFileUpload upload,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// How a file reached a turn.
/// </summary>
public static class NhAssistantFileOrigins
{
    /// <summary>A person attached it to a message.</summary>
    public const string Attachment = "attachment";

    /// <summary>A tool returned it in its result.</summary>
    public const string ToolResult = "tool-result";

    /// <summary>An earlier turn of the conversation read it; this turn opens it again.</summary>
    public const string EarlierTurn = "earlier-turn";
}

/// <summary>
/// Lower file limits of an agent or a skill that accepts files. A value is at most the application's
/// limit in <see cref="NhAssistantLimits"/>; <see langword="null"/> keeps the application's.
/// </summary>
public sealed class NhAssistantFileLimits
{
    /// <summary>
    /// The largest file in bytes, at most <see cref="NhAssistantLimits.MaxFileBytes"/>.
    /// </summary>
    public int? MaxFileBytes { get; set; }

    /// <summary>
    /// The most pages of one file, at most <see cref="NhAssistantLimits.MaxFilePages"/>.
    /// </summary>
    public int? MaxFilePages { get; set; }

    /// <summary>
    /// The most files of one turn, at most <see cref="NhAssistantLimits.MaxFilesPerTurn"/>.
    /// </summary>
    public int? MaxFilesPerTurn { get; set; }

    /// <summary>
    /// The most estimated file tokens of one turn, at most <see cref="NhAssistantLimits.MaxFileTokensPerTurn"/>.
    /// </summary>
    public int? MaxFileTokensPerTurn { get; set; }

    internal NhAssistantFileLimits Clone()
    {
        return (NhAssistantFileLimits)MemberwiseClone();
    }

    /// <summary>
    /// Throws when a limit is not positive or exceeds the application's.
    /// </summary>
    internal void Validate(NhAssistantLimits limits, string owner)
    {
        if ((MaxFileBytes is { } bytes && (bytes < 1 || bytes > limits.MaxFileBytes))
            || (MaxFilePages is { } pages && (pages < 1 || pages > limits.MaxFilePages))
            || (MaxFilesPerTurn is { } files && (files < 1 || files > limits.MaxFilesPerTurn))
            || (MaxFileTokensPerTurn is { } tokens && (tokens < 1 || tokens > limits.MaxFileTokensPerTurn)))
        {
            throw new InvalidOperationException(
                $"The file limits of {owner} must be positive and can only lower the application's NhAssistantLimits.");
        }
    }
}
