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
/// A file a person uploads to a conversation, before the application stores it.
/// </summary>
/// <param name="ConversationId">The conversation the file is for.</param>
/// <param name="Name">The file name the person's device gave it.</param>
/// <param name="MediaType">The media type the upload declares.</param>
/// <param name="Length">The length of the content in bytes.</param>
/// <param name="Content">The content; the caller disposes it.</param>
public sealed record NhAssistantFileUpload(
    Guid ConversationId,
    string Name,
    string MediaType,
    long Length,
    Stream Content);

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
