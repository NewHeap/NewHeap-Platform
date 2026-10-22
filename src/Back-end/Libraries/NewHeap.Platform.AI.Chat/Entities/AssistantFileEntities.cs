namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// The text a reading tool made of a file in one conversation, kept so that a later turn that opens
/// the same content again gives the model that text without sending the file to the reader once more
/// (D32, an exception to F6). The row is keyed by the conversation, the SHA-256 of the content the
/// reader received and the reader; the text is protected at rest, expires after
/// <see cref="NhAssistantLimits.ReaderTextRetention"/> and is removed with its conversation.
/// </summary>
public sealed class AssistantFileReaderText
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>
    /// The SHA-256 of the file content the reader received, <c>sha256:&lt;hex&gt;</c>.
    /// </summary>
    public string FileHash { get; set; } = string.Empty;

    /// <summary>
    /// The reader that made the text: the id of a reader of the application, or <c>mcp.&lt;server&gt;</c>.
    /// </summary>
    public string ReaderId { get; set; } = string.Empty;

    /// <summary>
    /// The text, protected with the host's data protection; never stored in plain text.
    /// </summary>
    public string ProtectedText { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
