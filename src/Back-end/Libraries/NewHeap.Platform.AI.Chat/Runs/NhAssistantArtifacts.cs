using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Stores the artifacts of agent threads. Each save of a name adds the next version.
/// </summary>
internal sealed partial class NhAssistantArtifactStore(NhAssistantDbContextFactory contextFactory)
{
    public async Task<AssistantArtifact> AddVersionAsync(AssistantArtifact artifact, CancellationToken cancellationToken)
    {
        // Two attempts: a concurrent save of the same name takes a version first.
        for (var attempt = 0; ; attempt++)
        {
            await using var context = contextFactory.CreateDbContext();
            var latest = await context.Artifacts
                .Where(item => item.ConversationId == artifact.ConversationId && item.Name == artifact.Name)
                .MaxAsync(item => (int?)item.Version, cancellationToken);
            artifact.Version = (latest ?? 0) + 1;
            context.Artifacts.Add(artifact);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return artifact;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
            }
        }
    }

    public async Task<AssistantArtifact?> FindAsync(Guid conversationId, Guid artifactId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == artifactId && item.ConversationId == conversationId, cancellationToken);
    }

    /// <summary>
    /// Every version in the thread, newest first, without content.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantArtifactRow>> ListAsync(Guid conversationId, int maximum, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Artifacts
            .AsNoTracking()
            .Where(item => item.ConversationId == conversationId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Version)
            .Take(maximum)
            .Select(item => new NhAssistantArtifactRow(
                item.Id,
                item.Name,
                item.Version,
                item.ContentType,
                item.SizeBytes,
                item.CreatedAt,
                item.RunId,
                item.ToolInvocationId))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The versions one run saved in its thread, newest first, without content.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantArtifactRow>> ListOfRunAsync(
        Guid conversationId,
        Guid runId,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Artifacts
            .AsNoTracking()
            .Where(item => item.ConversationId == conversationId && item.RunId == runId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Version)
            .Take(maximum)
            .Select(item => new NhAssistantArtifactRow(
                item.Id,
                item.Name,
                item.Version,
                item.ContentType,
                item.SizeBytes,
                item.CreatedAt,
                item.RunId,
                item.ToolInvocationId))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The latest version of each name, newest first.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantArtifactRow>> ListLatestAsync(Guid conversationId, int maximum, CancellationToken cancellationToken)
    {
        var all = await ListAsync(conversationId, 200, cancellationToken);
        return all
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.MaxBy(item => item.Version)!)
            .OrderByDescending(item => item.CreatedAt)
            .Take(maximum)
            .ToList();
    }

    internal static bool IsValidContentType(string contentType)
    {
        return contentType.Length <= 100 && ContentTypePattern().IsMatch(contentType);
    }

    /// <summary>
    /// The artifact type pattern: a type, a slash and one character class repeated, so a match is linear in
    /// the input and needs no match timeout.
    /// </summary>
    [GeneratedRegex("^[a-z]+/[a-z0-9][a-z0-9.+-]*$", RegexOptions.CultureInvariant)]
    internal static partial Regex ContentTypePattern();
}

internal sealed record NhAssistantArtifactRow(
    Guid Id,
    string Name,
    int Version,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    Guid? RunId,
    Guid? ToolInvocationId);

/// <summary>
/// <see cref="INhAiArtifacts"/> for tools that run inside an assistant turn: artifacts belong to
/// the thread of the turn and record the tool call that saved them. A consultation reads the
/// artifacts its asker may read and saves none.
/// </summary>
internal sealed class NhAssistantArtifacts(
    NhAssistantArtifactStore store,
    NhAssistantRegistrationState registration,
    NhAssistantMemoryService memory) : INhAiArtifacts
{
    public async Task<TaskResult<NhAiArtifactInfo>> SaveAsync(
        string name,
        string contentType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var turn = NhAssistantExecutionScope.CurrentTurn;
        if (turn is null)
        {
            return TaskResult<NhAiArtifactInfo>.Failed(
                NhAssistantErrorCodes.ArtifactOutsideTurn,
                "Artifacts are saved inside a tool call of an assistant turn.");
        }
        if (turn.Consultation is not null)
        {
            return TaskResult<NhAiArtifactInfo>.Failed(
                NhAssistantErrorCodes.ConsultReadOnly,
                "A consultation saves no artifacts.");
        }
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200 || name.Any(char.IsControl))
        {
            return TaskResult<NhAiArtifactInfo>.Failed(NhAssistantErrorCodes.ArtifactInvalid, "An artifact name has 1 to 200 characters.");
        }
        if (!NhAssistantArtifactStore.IsValidContentType(contentType))
        {
            return TaskResult<NhAiArtifactInfo>.Failed(NhAssistantErrorCodes.ArtifactInvalid, "The content type is not a media type.");
        }
        if (content.Length > registration.Limits.MaxArtifactBytes)
        {
            return TaskResult<NhAiArtifactInfo>.Failed(NhAssistantErrorCodes.ArtifactInvalid, "The artifact is too large.");
        }

        var bytes = content.ToArray();
        var artifact = await store.AddVersionAsync(
            new AssistantArtifact
            {
                Id = Guid.NewGuid(),
                ConversationId = turn.ConversationId,
                RunId = NhAiAgentInvocationScopes.Current?.RunId is { } runId && Guid.TryParse(runId, out var parsed) ? parsed : null,
                ToolInvocationId = NhAssistantExecutionScope.CurrentCall?.InvocationId,
                Name = name.Trim(),
                ContentType = contentType,
                SizeBytes = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                Content = bytes,
                CreatedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
        return TaskResult<NhAiArtifactInfo>.Succeeded(ToInfo(artifact));
    }

    public async Task<NhAiArtifact?> GetAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var turn = NhAssistantExecutionScope.CurrentTurn;
        if (turn is null)
        {
            return null;
        }

        var artifact = await store.FindAsync(turn.ConversationId, artifactId, cancellationToken);
        if (artifact is null)
        {
            return null;
        }
        if (turn.Consultation is { } consultation
            && !await memory.MayReadArtifactAsync(consultation.Viewer, artifact.ToolInvocationId, cancellationToken))
        {
            return null;
        }

        return new NhAiArtifact(ToInfo(artifact), artifact.Content);
    }

    private static NhAiArtifactInfo ToInfo(AssistantArtifact artifact)
    {
        return new NhAiArtifactInfo(
            artifact.Id,
            artifact.Name,
            artifact.Version,
            artifact.ContentType,
            artifact.SizeBytes,
            artifact.CreatedAt);
    }
}
