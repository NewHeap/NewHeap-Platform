namespace NewHeap.Platform.AI.Test;

public sealed class NhAiTestIngestionSource(
    string id,
    IEnumerable<NhAiIngestionDocument> documents) : INhAiIngestionSource
{
    private readonly IReadOnlyDictionary<string, NhAiIngestionDocument> _documents =
        documents.ToDictionary(document => document.Id, StringComparer.Ordinal);

    public string Id { get; } = id;

    public int Calls { get; private set; }

    public ValueTask<NhAiIngestionDocument?> GetDocumentAsync(
        string documentId,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        _documents.TryGetValue(documentId, out var document);
        return ValueTask.FromResult(document);
    }
}

public sealed class NhAiTestIngestionAuthorizationPolicy(
    bool allowed,
    bool deleteAllowed = false) :
    INhAiIngestionAuthorizationPolicy
{
    public ValueTask<bool> CanReadAsync(
        string sourceId,
        string documentId,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(allowed);
    }

    public ValueTask<bool> CanDeleteAsync(
        string sourceId,
        string documentId,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(deleteAllowed);
    }
}

public sealed class NhAiTestIngestionVersionManager : INhAiIngestionVersionManager
{
    private readonly object _sync = new();
    private readonly Dictionary<(string SourceId, string DocumentId, int Version), Entry> _entries = [];

    public ValueTask<NhAiIngestionVersionLease> AcquireAsync(
        NhAiIngestionVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var key = (request.SourceId, request.DocumentId, request.DocumentVersion);
            if (_entries.TryGetValue(key, out var existing))
            {
                var decision = string.Equals(existing.DocumentHash, request.DocumentHash, StringComparison.Ordinal)
                    && string.Equals(existing.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)
                    ? NhAiIngestionVersionDecisionKind.Duplicate
                    : NhAiIngestionVersionDecisionKind.Conflict;
                return ValueTask.FromResult(
                    new NhAiIngestionVersionLease(
                        decision,
                        decision == NhAiIngestionVersionDecisionKind.Duplicate
                            ? "ingestion-version-duplicate"
                            : "ingestion-version-conflict"));
            }

            var leaseId = Guid.NewGuid().ToString("N");
            _entries.Add(
                key,
                new Entry(request.DocumentHash, request.IdempotencyKey, leaseId));
            return ValueTask.FromResult(
                new NhAiIngestionVersionLease(
                    NhAiIngestionVersionDecisionKind.Acquired,
                    "ingestion-version-acquired",
                    leaseId));
        }
    }

    public ValueTask CompleteAsync(
        NhAiIngestionVersionLease lease,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (outcome != NhAiOutcomeKind.Succeeded && lease.LeaseId is not null)
            {
                (string SourceId, string DocumentId, int Version)? keyToRemove = null;
                foreach (var entry in _entries)
                {
                    if (!string.Equals(
                        entry.Value.LeaseId,
                        lease.LeaseId,
                        StringComparison.Ordinal))
                    {
                        continue;
                    }

                    keyToRemove = entry.Key;
                    break;
                }
                if (keyToRemove is { } key)
                {
                    _entries.Remove(key);
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    private sealed record Entry(
        string DocumentHash,
        string IdempotencyKey,
        string LeaseId);
}
