using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Protects the text a reading tool made of a file while it is kept. The ASP.NET package uses ASP.NET
/// Data Protection; without a protector no text is kept.
/// </summary>
internal interface INhAssistantReaderTextProtector
{
    string Protect(string text);

    /// <summary>
    /// The text, or <see langword="null"/> when it can no longer be unprotected, for example after a
    /// key loss.
    /// </summary>
    string? Unprotect(string protectedText);
}

/// <summary>
/// The texts reading tools made of files, kept per conversation (D32, an exception to F6): keyed by
/// the SHA-256 of the content the reader received and the reader, protected at rest, kept for
/// <see cref="NhAssistantLimits.ReaderTextRetention"/> and removed with their conversation. A turn that
/// opens the same content again and would choose the same reader gives the model the kept text instead
/// of sending the file to the reader once more. Nothing here logs a text, a name or a hash.
/// </summary>
internal sealed class NhAssistantReaderTexts(
    IServiceProvider services,
    NhAssistantDbContextFactory contextFactory,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantReaderTexts> logger)
{
    /// <summary>
    /// How many expired texts one cleanup removes at most.
    /// </summary>
    internal const int CleanupBatchSize = 500;

    /// <summary>
    /// Whether texts are kept: a retention above zero and a protector for them.
    /// </summary>
    public bool Enabled => registration.Limits.ReaderTextRetention > TimeSpan.Zero && Protector is not null;

    private INhAssistantReaderTextProtector? Protector => services.GetService<INhAssistantReaderTextProtector>();

    /// <summary>
    /// The text <paramref name="readerId"/> made of the content with <paramref name="fileHash"/> in the
    /// conversation, while it is kept; <see langword="null"/> otherwise.
    /// </summary>
    public async Task<string?> FindAsync(
        Guid conversationId,
        string fileHash,
        string readerId,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        await using var context = contextFactory.CreateDbContext();
        var protectedText = await context.FileReaderTexts
            .AsNoTracking()
            .Where(item => item.ConversationId == conversationId
                && item.FileHash == fileHash
                && item.ReaderId == readerId
                && item.ExpiresAt > now)
            .Select(item => item.ProtectedText)
            .FirstOrDefaultAsync(cancellationToken);
        if (protectedText is null)
        {
            return null;
        }

        var text = Protector!.Unprotect(protectedText);
        if (text is null)
        {
            // A lost or rotated-out key: the reader reads the file again and the new text replaces it.
            logger.LogWarning("A kept reader text of conversation {ConversationId} could no longer be unprotected.", conversationId);
        }

        return text;
    }

    /// <summary>
    /// Keeps the text <paramref name="readerId"/> made of the content with <paramref name="fileHash"/>,
    /// replacing an earlier one, for <see cref="NhAssistantLimits.ReaderTextRetention"/>. A text longer
    /// than any turn may read is not kept. A failure to keep it never fails the turn.
    /// </summary>
    public async Task KeepAsync(
        Guid conversationId,
        string fileHash,
        string readerId,
        string text,
        CancellationToken cancellationToken)
    {
        if (!Enabled || NhAssistantTurnFiles.EstimateTextTokens(text) > registration.Limits.MaxFileTokensPerTurn)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var row = new AssistantFileReaderText
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            FileHash = fileHash,
            ReaderId = readerId,
            ProtectedText = Protector!.Protect(text),
            CreatedAt = now,
            ExpiresAt = now + registration.Limits.ReaderTextRetention
        };
        try
        {
            await using var context = contextFactory.CreateDbContext();
            await context.FileReaderTexts
                .Where(item => item.ConversationId == conversationId && item.FileHash == fileHash && item.ReaderId == readerId)
                .ExecuteDeleteAsync(cancellationToken);
            context.FileReaderTexts.Add(row);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // A parallel turn kept the same text first, or the conversation is gone: the next turn asks
            // the reader again. The exception may quote the row, so only its type is logged.
            logger.LogInformation(
                "A reader text of conversation {ConversationId} was not kept ({ExceptionType}).",
                conversationId,
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Removes every kept text of a conversation, for example when its owner deletes it.
    /// </summary>
    public async Task<int> RemoveConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.FileReaderTexts
            .Where(item => item.ConversationId == conversationId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Removes at most <see cref="CleanupBatchSize"/> texts that expired before <paramref name="now"/>.
    /// </summary>
    public async Task<int> RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var expired = await context.FileReaderTexts
            .AsNoTracking()
            .Where(item => item.ExpiresAt <= now)
            .OrderBy(item => item.ExpiresAt)
            .Select(item => item.Id)
            .Take(CleanupBatchSize)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return 0;
        }

        return await context.FileReaderTexts
            .Where(item => expired.Contains(item.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
