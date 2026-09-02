using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

public sealed record NhAiIngestionDocument(
    string Id,
    int Version,
    string SourceId,
    string ContentType,
    string Content,
    string ContentHash,
    NhAiDataClassification Classification,
    NhAiContextTrust Trust,
    IReadOnlyList<NhAiExecutionScopeEntry> ExecutionScopes,
    IReadOnlyList<string> ProvenanceReferences,
    DateTimeOffset ObservedAt);

public sealed record NhAiIngestionRequest(
    NhAiInvocationContext InvocationContext,
    string SourceId,
    string DocumentId,
    string EmbeddingProfileName,
    string VectorStoreKey,
    string VectorCollectionName,
    int EmbeddingDimensions,
    string IdempotencyKey)
{
    public int MaxDocumentCharacters { get; init; } = 1_000_000;
    public int MaxChunks { get; init; } = 1_000;
    public int MaxChunkCharacters { get; init; } = 2_000;
    public NhAiIngestionReplacement? Replaces { get; init; }
}

public sealed record NhAiIngestionReplacement(
    int DocumentVersion,
    int ChunkCount,
    string DocumentHash);

public sealed record NhAiIngestionBatchRequest(
    IReadOnlyList<NhAiIngestionRequest> Documents);

public sealed record NhAiIngestionDeletionRequest(
    NhAiInvocationContext InvocationContext,
    string SourceId,
    string DocumentId,
    int DocumentVersion,
    int ChunkCount,
    string DocumentHash,
    string VectorStoreKey,
    string VectorCollectionName,
    int EmbeddingDimensions,
    string IdempotencyKey);

public sealed record NhAiIngestionChunk(
    string Id,
    int Index,
    string Content,
    string ContentHash,
    IReadOnlyList<string> ProvenanceReferences);

public sealed class NhAiVectorRecord
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string DocumentId { get; init; }
    public required int DocumentVersion { get; init; }
    public required string DocumentHash { get; init; }
    public int? ReplacesDocumentVersion { get; init; }
    public string? ReplacesDocumentHash { get; init; }
    public required string Content { get; init; }
    public required string ContentHash { get; init; }
    public required string IdempotencyKey { get; init; }
    public required ReadOnlyMemory<float> Vector { get; init; }
    public required int Classification { get; init; }
    public required int Trust { get; init; }
    public required IReadOnlyList<string> ExecutionScopeKeys { get; init; }
    public required IReadOnlyList<string> ProvenanceReferences { get; init; }
}

public sealed record NhAiIngestionResult(
    string SourceId,
    string DocumentId,
    int DocumentVersion,
    int ChunkCount,
    string DocumentHash,
    string BatchHash,
    string VectorCollectionName,
    int? ReplacedDocumentVersion,
    int DeletedRecordCount);

public sealed record NhAiIngestionBatchItemResult(
    string SourceId,
    string DocumentId,
    TaskResult<NhAiIngestionResult> Result);

public sealed record NhAiIngestionBatchResult(
    int Succeeded,
    int Failed,
    IReadOnlyList<NhAiIngestionBatchItemResult> Documents);

public sealed record NhAiIngestionDeletionResult(
    string SourceId,
    string DocumentId,
    int DocumentVersion,
    int DeletedRecordCount,
    string DocumentHash,
    string DeletionHash,
    string VectorCollectionName);

public sealed record NhAiIngestionCheckpoint(
    string SourceId,
    string DocumentId,
    int DocumentVersion,
    string DocumentHash,
    string BatchHash,
    DateTimeOffset CompletedAt);

public enum NhAiIngestionVersionDecisionKind
{
    Acquired = 0,
    Duplicate = 1,
    Conflict = 2,
    Denied = 3
}

public sealed record NhAiIngestionVersionRequest(
    string SourceId,
    string DocumentId,
    int DocumentVersion,
    string DocumentHash,
    string IdempotencyKey);

public sealed record NhAiIngestionVersionLease(
    NhAiIngestionVersionDecisionKind Decision,
    string Code,
    string? LeaseId = null);

public interface INhAiIngestionSource
{
    string Id { get; }

    ValueTask<NhAiIngestionDocument?> GetDocumentAsync(
        string documentId,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);
}

public interface INhAiIngestionAuthorizationPolicy
{
    ValueTask<bool> CanReadAsync(
        string sourceId,
        string documentId,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CanDeleteAsync(
        string sourceId,
        string documentId,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

public interface INhAiIngestionVersionManager
{
    ValueTask<NhAiIngestionVersionLease> AcquireAsync(
        NhAiIngestionVersionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(
        NhAiIngestionVersionLease lease,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken = default);
}

public interface INhAiDocumentChunker
{
    IReadOnlyList<NhAiIngestionChunk> Chunk(
        NhAiIngestionDocument document,
        int maxChunks,
        int maxChunkCharacters);
}

public interface INhAiIngestionPipeline
{
    Task<TaskResult<NhAiIngestionResult>> IngestAsync(
        NhAiIngestionRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskResult<NhAiIngestionBatchResult>> IngestBatchAsync(
        NhAiIngestionBatchRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskResult<NhAiIngestionDeletionResult>> DeleteAsync(
        NhAiIngestionDeletionRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiDenyIngestionAuthorizationPolicy : INhAiIngestionAuthorizationPolicy
{
    public ValueTask<bool> CanReadAsync(
        string sourceId,
        string documentId,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

internal sealed class NhAiDenyIngestionVersionManager : INhAiIngestionVersionManager
{
    public ValueTask<NhAiIngestionVersionLease> AcquireAsync(
        NhAiIngestionVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new NhAiIngestionVersionLease(
                NhAiIngestionVersionDecisionKind.Denied,
                "ingestion-version-manager-not-configured"));
    }

    public ValueTask CompleteAsync(
        NhAiIngestionVersionLease lease,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class NhAiDeterministicDocumentChunker : INhAiDocumentChunker
{
    public IReadOnlyList<NhAiIngestionChunk> Chunk(
        NhAiIngestionDocument document,
        int maxChunks,
        int maxChunkCharacters)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (maxChunks < 1 || maxChunkCharacters < 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChunks));
        }

        var chunks = new List<NhAiIngestionChunk>();
        for (var offset = 0; offset < document.Content.Length; offset += maxChunkCharacters)
        {
            if (chunks.Count >= maxChunks)
            {
                throw new InvalidOperationException(
                    $"AI ingestion document '{document.Id}' exceeded its chunk bound.");
            }
            var length = Math.Min(maxChunkCharacters, document.Content.Length - offset);
            var content = document.Content.Substring(offset, length);
            var hash = NhAiCanonicalJson.ComputeHash(content);
            chunks.Add(new NhAiIngestionChunk(
                NhAiIngestionIds.CreateRecordId(
                    document.SourceId,
                    document.Id,
                    document.Version,
                    chunks.Count),
                chunks.Count,
                content,
                hash,
                document.ProvenanceReferences));
        }
        return chunks;
    }
}

internal sealed class NhAiIngestionPipeline(
    IEnumerable<INhAiIngestionSource> sources,
    INhAiIngestionAuthorizationPolicy authorizationPolicy,
    INhAiIngestionVersionManager versionManager,
    INhAiDocumentChunker chunker,
    INhAiModelProfileResolver modelProfiles,
    INhAiBudgetManager budgetManager,
    IServiceProvider serviceProvider) : INhAiIngestionPipeline
{
    public async Task<TaskResult<NhAiIngestionBatchResult>> IngestBatchAsync(
        NhAiIngestionBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Documents.Count is < 1 or > 1_000
            || request.Documents.Any(document => document is null)
            || request.Documents
                .GroupBy(document => (document.SourceId, document.DocumentId))
                .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "An AI ingestion batch needs between one and one thousand unique documents.",
                nameof(request));
        }

        var items = new List<NhAiIngestionBatchItemResult>(request.Documents.Count);
        foreach (var document in request.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await IngestAsync(document, cancellationToken);
            items.Add(new NhAiIngestionBatchItemResult(
                document.SourceId,
                document.DocumentId,
                result));
        }

        var data = new NhAiIngestionBatchResult(
            items.Count(item => item.Result.Success),
            items.Count(item => !item.Result.Success),
            items);
        if (data.Failed == 0)
        {
            return TaskResult<NhAiIngestionBatchResult>.Succeeded(data);
        }

        return TaskResult<NhAiIngestionBatchResult>
            .Failed(
                "ai-ingestion-partial-failure",
                "One or more documents in the AI ingestion batch could not be processed.")
            .WithData(data);
    }

    public async Task<TaskResult<NhAiIngestionResult>> IngestAsync(
        NhAiIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var source = sources.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, request.SourceId, StringComparison.Ordinal));
        if (source is null)
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "The requested AI ingestion source is not registered.");
        }
        if (request.Replaces is not null
            && !await authorizationPolicy.CanDeleteAsync(
                request.SourceId,
                request.DocumentId,
                request.InvocationContext,
                cancellationToken))
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "ai-ingestion-replacement-authorization-denied",
                "AI ingestion source authorization denied replacement deletion.");
        }
        if (!await authorizationPolicy.CanReadAsync(
            request.SourceId,
            request.DocumentId,
            request.InvocationContext,
            cancellationToken))
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "AI ingestion source authorization denied access.");
        }

        var document = await source.GetDocumentAsync(
            request.DocumentId,
            request.InvocationContext,
            cancellationToken);
        if (document is null)
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "The requested AI ingestion document was not found.");
        }
        ValidateDocument(document, request);
        var versionLease = await versionManager.AcquireAsync(
            new NhAiIngestionVersionRequest(
                document.SourceId,
                document.Id,
                document.Version,
                document.ContentHash,
                request.IdempotencyKey),
            cancellationToken);
        if (versionLease.Decision != NhAiIngestionVersionDecisionKind.Acquired)
        {
            var failureCode = versionLease.Decision switch
            {
                NhAiIngestionVersionDecisionKind.Duplicate => "ai-ingestion-version-duplicate",
                NhAiIngestionVersionDecisionKind.Conflict => "ai-ingestion-version-conflict",
                _ => "ai-ingestion-version-denied"
            };
            return TaskResult<NhAiIngestionResult>.Failed(
                failureCode,
                "The AI ingestion document version could not be acquired for writing.");
        }
        await using var versionScope = new NhAiIngestionVersionLeaseScope(
            versionManager,
            versionLease);
        var chunks = chunker.Chunk(
            document,
            request.MaxChunks,
            request.MaxChunkCharacters);
        var profile = await modelProfiles.ResolveEmbeddingsAsync(
            new NhAiModelResolutionRequest(
                request.EmbeddingProfileName,
                NhAiModelCapability.Embeddings,
                document.Classification,
                request.InvocationContext.Purpose),
            cancellationToken);
        if (!profile.Success)
        {
            return TaskResult<NhAiIngestionResult>.Failed(profile);
        }

        var inputCharacters = chunks.Sum(chunk => (long)chunk.Content.Length);
        var estimatedInputTokens = (int)Math.Min(
            int.MaxValue,
            Math.Max(1, (inputCharacters + 3) / 4));
        if (estimatedInputTokens > profile.Data.Profile.Budget.MaxInputTokens
            || profile.Data.Profile.Budget.MaxCalls < 1)
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "ai-embedding-profile-budget-exceeded",
                "The AI ingestion embedding request exceeds its profile budget.");
        }
        if (request.InvocationContext.RemainingBudget is { } remaining
            && (estimatedInputTokens > remaining.MaxInputTokens
                || remaining.MaxCalls < 1))
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "ai-ingestion-run-budget-exceeded",
                "The AI ingestion embedding request exceeds its remaining run budget.");
        }
        var reservation = await budgetManager.ReserveAsync(
            new NhAiBudgetRequest(
                request.InvocationContext.InvocationId,
                profile.Data.Profile.Name,
                1,
                estimatedInputTokens,
                0,
                null),
            cancellationToken);
        if (!reservation.Success)
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "ai-ingestion-budget-reservation-denied",
                "The AI ingestion embedding budget could not be reserved.");
        }

        GeneratedEmbeddings<Embedding<float>> embeddings;
        try
        {
            embeddings = await profile.Data.Generator.GenerateAsync(
                chunks.Select(chunk => chunk.Content),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "ai-embedding-dependency-unavailable",
                "The AI ingestion embedding dependency is unavailable.");
        }
        if (embeddings.Count != chunks.Count)
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "ai-embedding-result-invalid",
                "The AI ingestion embedding result is invalid.");
        }

        if (embeddings.Any(embedding => embedding.Vector.Length != request.EmbeddingDimensions))
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "ai-embedding-result-invalid",
                "The AI ingestion embedding result is invalid.");
        }
        var scopeKeys = document.ExecutionScopes
            .Select(scope => $"{scope.Type}:{scope.Id}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var records = chunks.Select((chunk, index) => new NhAiVectorRecord
        {
            Id = chunk.Id,
            SourceId = document.SourceId,
            DocumentId = document.Id,
            DocumentVersion = document.Version,
            DocumentHash = document.ContentHash,
            ReplacesDocumentVersion = request.Replaces?.DocumentVersion,
            ReplacesDocumentHash = request.Replaces?.DocumentHash,
            Content = chunk.Content,
            ContentHash = chunk.ContentHash,
            IdempotencyKey = request.IdempotencyKey,
            Vector = embeddings[index].Vector,
            Classification = (int)document.Classification,
            Trust = (int)document.Trust,
            ExecutionScopeKeys = scopeKeys,
            ProvenanceReferences = chunk.ProvenanceReferences
        }).ToArray();
        var batchHash = NhAiCanonicalJson.ComputeHash(records.Select(record => new
        {
            record.Id,
            record.DocumentVersion,
            record.DocumentHash,
            record.ReplacesDocumentVersion,
            record.ReplacesDocumentHash,
            record.ContentHash,
            VectorLength = record.Vector.Length,
            record.ProvenanceReferences
        }).ToArray());
        var vectorStore = serviceProvider.GetKeyedService<VectorStore>(request.VectorStoreKey);
        if (vectorStore is null)
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "vector-store-not-configured",
                "The requested AI vector store is not configured.");
        }
        var definition = CreateVectorDefinition(request.EmbeddingDimensions);
        var collection = vectorStore.GetCollection<string, NhAiVectorRecord>(
            request.VectorCollectionName,
            definition);
        try
        {
            await collection.EnsureCollectionExistsAsync(cancellationToken);
            await collection.UpsertAsync(records, cancellationToken);
            if (request.Replaces is not null)
            {
                await collection.DeleteAsync(
                    NhAiIngestionIds.CreateRecordIds(
                        request.SourceId,
                        request.DocumentId,
                        request.Replaces.DocumentVersion,
                        request.Replaces.ChunkCount),
                    cancellationToken);
            }
            versionScope.MarkSucceeded();
        }
        catch (VectorStoreException)
        {
            return TaskResult<NhAiIngestionResult>.Failed(
                "vector-store-failure",
                "The AI vector store could not persist the ingestion batch.");
        }
        return TaskResult<NhAiIngestionResult>.Succeeded(new NhAiIngestionResult(
            document.SourceId,
            document.Id,
            document.Version,
            records.Length,
            document.ContentHash,
            batchHash,
            request.VectorCollectionName,
            request.Replaces?.DocumentVersion,
            request.Replaces?.ChunkCount ?? 0));
    }

    private sealed class NhAiIngestionVersionLeaseScope(
        INhAiIngestionVersionManager versionManager,
        NhAiIngestionVersionLease lease) : IAsyncDisposable
    {
        private NhAiOutcomeKind _outcome = NhAiOutcomeKind.TerminalFailure;

        public void MarkSucceeded()
        {
            _outcome = NhAiOutcomeKind.Succeeded;
        }

        public async ValueTask DisposeAsync()
        {
            await versionManager.CompleteAsync(
                lease,
                _outcome,
                CancellationToken.None);
        }
    }

    public async Task<TaskResult<NhAiIngestionDeletionResult>> DeleteAsync(
        NhAiIngestionDeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDeletionRequest(request);
        if (!await authorizationPolicy.CanDeleteAsync(
            request.SourceId,
            request.DocumentId,
            request.InvocationContext,
            cancellationToken))
        {
            return TaskResult<NhAiIngestionDeletionResult>.Failed(
                "AI ingestion source authorization denied deletion.");
        }

        var vectorStore = serviceProvider.GetKeyedService<VectorStore>(request.VectorStoreKey);
        if (vectorStore is null)
        {
            return TaskResult<NhAiIngestionDeletionResult>.Failed(
                "vector-store-not-configured",
                "The requested AI vector store is not configured.");
        }

        var collection = vectorStore.GetCollection<string, NhAiVectorRecord>(
            request.VectorCollectionName,
            CreateVectorDefinition(request.EmbeddingDimensions));
        var recordIds = NhAiIngestionIds.CreateRecordIds(
            request.SourceId,
            request.DocumentId,
            request.DocumentVersion,
            request.ChunkCount);
        try
        {
            await collection.DeleteAsync(recordIds, cancellationToken);
        }
        catch (VectorStoreException)
        {
            return TaskResult<NhAiIngestionDeletionResult>.Failed(
                "vector-store-failure",
                "The AI vector store could not delete the ingestion document.");
        }

        var deletionHash = NhAiCanonicalJson.ComputeHash(new
        {
            request.SourceId,
            request.DocumentId,
            request.DocumentVersion,
            request.DocumentHash,
            request.IdempotencyKey,
            RecordIds = recordIds
        });
        return TaskResult<NhAiIngestionDeletionResult>.Succeeded(new(
            request.SourceId,
            request.DocumentId,
            request.DocumentVersion,
            recordIds.Count,
            request.DocumentHash,
            deletionHash,
            request.VectorCollectionName));
    }

    private static VectorStoreCollectionDefinition CreateVectorDefinition(int dimensions)
    {
        return new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(NhAiVectorRecord.Id), typeof(string)),
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.SourceId), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.DocumentId), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.DocumentVersion), typeof(int)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.DocumentHash), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.ReplacesDocumentVersion), typeof(int?)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.ReplacesDocumentHash), typeof(string)),
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.Content), typeof(string)),
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.ContentHash), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.IdempotencyKey), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.Classification), typeof(int)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(NhAiVectorRecord.Trust), typeof(int)) { IsIndexed = true },
                new VectorStoreDataProperty(
                    nameof(NhAiVectorRecord.ExecutionScopeKeys),
                    typeof(IReadOnlyList<string>)) { IsIndexed = true },
                new VectorStoreDataProperty(
                    nameof(NhAiVectorRecord.ProvenanceReferences),
                    typeof(IReadOnlyList<string>)),
                new VectorStoreVectorProperty(
                    nameof(NhAiVectorRecord.Vector),
                    typeof(ReadOnlyMemory<float>),
                    dimensions)
            ]
        };
    }

    private static void ValidateRequest(NhAiIngestionRequest request)
    {
        NhAiNames.ValidateSegment(request.SourceId, nameof(request.SourceId));
        NhAiNames.ValidateSegment(request.EmbeddingProfileName, nameof(request.EmbeddingProfileName));
        NhAiNames.ValidateSegment(request.VectorStoreKey, nameof(request.VectorStoreKey));
        NhAiNames.ValidateSegment(request.VectorCollectionName, nameof(request.VectorCollectionName));
        if (string.IsNullOrWhiteSpace(request.DocumentId)
            || request.DocumentId.Length > 256
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Length > 256
            || request.EmbeddingDimensions is < 1 or > 1_000_000
            || request.MaxDocumentCharacters is < 1 or > 10_000_000
            || request.MaxChunks is < 1 or > 10_000
            || request.MaxChunkCharacters is < 32 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static void ValidateDocument(
        NhAiIngestionDocument document,
        NhAiIngestionRequest request)
    {
        if (!string.Equals(document.Id, request.DocumentId, StringComparison.Ordinal)
            || !string.Equals(document.SourceId, request.SourceId, StringComparison.Ordinal)
            || document.Version < 1
            || string.IsNullOrWhiteSpace(document.ContentType)
            || string.IsNullOrEmpty(document.Content)
            || document.Content.Length > request.MaxDocumentCharacters
            || document.ContentHash.Length != 64
            || !string.Equals(
                document.ContentHash,
                NhAiCanonicalJson.ComputeHash(document.Content),
                StringComparison.Ordinal)
            || document.Trust == NhAiContextTrust.TrustedApplication
            || document.ProvenanceReferences.Count is < 1 or > 32
            || document.ExecutionScopes.Count < 1
            || !document.ExecutionScopes.All(required =>
                request.InvocationContext.ExecutionScopes.Any(actual =>
                    string.Equals(actual.Type, required.Type, StringComparison.Ordinal)
                    && string.Equals(actual.Id, required.Id, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                $"AI ingestion document '{document.Id}' violates its bounded provenance or scope contract.");
        }

        if (request.Replaces is not null
            && (request.Replaces.DocumentVersion < 1
                || request.Replaces.DocumentVersion >= document.Version
                || request.Replaces.ChunkCount is < 1 or > 10_000
                || !NhAiIngestionIds.IsSha256(request.Replaces.DocumentHash)))
        {
            throw new InvalidOperationException(
                "AI ingestion replacement lineage is invalid for the source document version.");
        }
    }

    private static void ValidateDeletionRequest(NhAiIngestionDeletionRequest request)
    {
        NhAiNames.ValidateSegment(request.SourceId, nameof(request.SourceId));
        NhAiNames.ValidateSegment(request.VectorStoreKey, nameof(request.VectorStoreKey));
        NhAiNames.ValidateSegment(request.VectorCollectionName, nameof(request.VectorCollectionName));
        if (string.IsNullOrWhiteSpace(request.DocumentId)
            || request.DocumentId.Length > 256
            || request.DocumentVersion < 1
            || request.ChunkCount is < 1 or > 10_000
            || !NhAiIngestionIds.IsSha256(request.DocumentHash)
            || request.EmbeddingDimensions is < 1 or > 1_000_000
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }
}

internal static class NhAiIngestionIds
{
    internal static string CreateRecordId(
        string sourceId,
        string documentId,
        int documentVersion,
        int chunkIndex)
    {
        return NhAiCanonicalJson.ComputeHash(new
        {
            SourceId = sourceId,
            DocumentId = documentId,
            DocumentVersion = documentVersion,
            ChunkIndex = chunkIndex
        });
    }

    internal static IReadOnlyList<string> CreateRecordIds(
        string sourceId,
        string documentId,
        int documentVersion,
        int chunkCount)
    {
        return Enumerable.Range(0, chunkCount)
            .Select(index => CreateRecordId(sourceId, documentId, documentVersion, index))
            .ToArray();
    }

    internal static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(char.IsAsciiHexDigit);
    }
}
