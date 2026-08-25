using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet;

public sealed record NhAiDurableIngestionCheckpoint(
    string SourceId,
    string DocumentId,
    int DocumentVersion,
    int ChunkCount,
    string DocumentHash,
    string BatchHash,
    string VectorCollectionName,
    string IdempotencyHash,
    string RequestHash,
    int? ReplacedDocumentVersion,
    int DeletedRecordCount,
    DateTimeOffset CompletedAt);

public interface INhAiBackgroundOperationIngestionAdapter
{
    Task<TaskResult<NhAiIngestionResult>> IngestAsync(
        INhBackgroundOperationContext operation,
        NhAiIngestionRequest request,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiBackgroundOperationIngestionAdapter(
    INhAiIngestionPipeline ingestionPipeline,
    INhAiBackgroundOperationRunAdapter runAdapter,
    INhAiIngestionAuthorizationPolicy authorizationPolicy) :
    INhAiBackgroundOperationIngestionAdapter
{
    private const string CheckpointKey = "ai-ingestion";
    private const int CheckpointSchemaVersion = 1;

    public async Task<TaskResult<NhAiIngestionResult>> IngestAsync(
        INhBackgroundOperationContext operation,
        NhAiIngestionRequest request,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        await operation.ThrowIfCancellationRequestedAsync(cancellationToken);

        var boundContext = runAdapter.BindInvocation(
            request.InvocationContext,
            operation,
            deadline);
        var idempotencyHash = NhAiCanonicalJson.ComputeHash(operation.IdempotencyKey);
        var boundRequest = request with
        {
            InvocationContext = boundContext,
            IdempotencyKey = operation.IdempotencyKey
        };
        var requestHash = CreateRequestHash(boundRequest);
        var existing = await operation.Checkpoints.GetAsync<NhAiDurableIngestionCheckpoint>(
            CheckpointKey,
            cancellationToken);
        if (existing is not null)
        {
            if (!await authorizationPolicy.CanReadAsync(
                request.SourceId,
                request.DocumentId,
                boundContext,
                cancellationToken))
            {
                return TaskResult<NhAiIngestionResult>.Failed(
                    "AI ingestion source authorization denied access.");
            }
            if (!Matches(existing.Value, idempotencyHash, requestHash))
            {
                return TaskResult<NhAiIngestionResult>.Failed(
                    "ai-ingestion-checkpoint-mismatch",
                    "The durable AI ingestion checkpoint does not match the requested document.");
            }

            return TaskResult<NhAiIngestionResult>.Succeeded(ToResult(existing.Value));
        }

        var result = await ingestionPipeline.IngestAsync(
            boundRequest,
            cancellationToken);
        if (!result.Success)
        {
            return result;
        }

        var checkpoint = new NhAiDurableIngestionCheckpoint(
            result.Data.SourceId,
            result.Data.DocumentId,
            result.Data.DocumentVersion,
            result.Data.ChunkCount,
            result.Data.DocumentHash,
            result.Data.BatchHash,
            result.Data.VectorCollectionName,
            idempotencyHash,
            requestHash,
            result.Data.ReplacedDocumentVersion,
            result.Data.DeletedRecordCount,
            DateTimeOffset.UtcNow);
        var checkpointResult = await operation.Checkpoints.SetAsync(
            CheckpointKey,
            checkpoint,
            CheckpointSchemaVersion,
            cancellationToken: cancellationToken);
        if (!checkpointResult.Success)
        {
            return TaskResult<NhAiIngestionResult>
                .Failed(checkpointResult)
                .WithData(result.Data);
        }

        return result;
    }

    private static bool Matches(
        NhAiDurableIngestionCheckpoint checkpoint,
        string idempotencyHash,
        string requestHash)
    {
        return string.Equals(checkpoint.IdempotencyHash, idempotencyHash, StringComparison.Ordinal)
            && string.Equals(checkpoint.RequestHash, requestHash, StringComparison.Ordinal);
    }

    private static string CreateRequestHash(NhAiIngestionRequest request)
    {
        return NhAiCanonicalJson.ComputeHash(new
        {
            request.SourceId,
            request.DocumentId,
            request.EmbeddingProfileName,
            request.VectorStoreKey,
            request.VectorCollectionName,
            request.EmbeddingDimensions,
            request.MaxDocumentCharacters,
            request.MaxChunks,
            request.MaxChunkCharacters,
            request.Replaces,
            request.InvocationContext.ActorId,
            request.InvocationContext.ActorKind,
            request.InvocationContext.AccountableOwnerId,
            request.InvocationContext.Purpose,
            Scope = request.InvocationContext.Scope
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToArray(),
            CapabilityGrants = request.InvocationContext.CapabilityGrants
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ExecutionScopes = request.InvocationContext.ExecutionScopes
                .OrderBy(scope => scope.Type, StringComparer.Ordinal)
                .ThenBy(scope => scope.Id, StringComparer.Ordinal)
                .ToArray()
        });
    }

    private static NhAiIngestionResult ToResult(NhAiDurableIngestionCheckpoint checkpoint)
    {
        return new NhAiIngestionResult(
            checkpoint.SourceId,
            checkpoint.DocumentId,
            checkpoint.DocumentVersion,
            checkpoint.ChunkCount,
            checkpoint.DocumentHash,
            checkpoint.BatchHash,
            checkpoint.VectorCollectionName,
            checkpoint.ReplacedDocumentVersion,
            checkpoint.DeletedRecordCount);
    }
}
