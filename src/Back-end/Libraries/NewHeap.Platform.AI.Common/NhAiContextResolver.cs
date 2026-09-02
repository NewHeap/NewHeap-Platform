namespace NewHeap.Platform.AI;

internal sealed class NhAiContextResolver(
    IEnumerable<INhAiContextSource> sources,
    INhAiContextAuthorizationPolicy authorizationPolicy,
    INhAiContextRanker ranker,
    INhAiContextConflictResolver conflictResolver,
    IEnumerable<INhAiContextTraceSink> traceSinks) : INhAiContextResolver
{
    private const int MaxSources = 32;

    public async ValueTask<NhAiContextResolution> ResolveAsync(
        NhAiContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.InvocationContext);
        ValidateRequest(request);

        var trace = new List<NhAiContextTraceEntry>();
        var candidates = new List<NhAiContextItem>();
        foreach (var source in sources
            .OrderBy(item => item.Descriptor.Id, StringComparer.Ordinal)
            .Take(MaxSources))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSource(source.Descriptor);
            if (!await authorizationPolicy.CanAccessSourceAsync(
                source.Descriptor,
                request.InvocationContext,
                cancellationToken))
            {
                trace.Add(new NhAiContextTraceEntry(
                    source.Descriptor.Id,
                    "source-authorization-denied"));
                continue;
            }

            var retrieved = await source.RetrieveAsync(request, cancellationToken);
            if (retrieved.Count > source.Descriptor.MaxItems)
            {
                throw new InvalidOperationException(
                    $"AI context source '{source.Descriptor.Id}' exceeded its item bound.");
            }
            var accepted = retrieved
                .Where(item => IsValidItem(item, source.Descriptor, request))
                .ToArray();
            candidates.AddRange(accepted);
            trace.Add(new NhAiContextTraceEntry(
                source.Descriptor.Id,
                accepted.Length == retrieved.Count
                    ? "source-accepted"
                    : "source-items-filtered",
                accepted.Length));
        }

        var resolvedConflicts = await conflictResolver.ResolveAsync(
            request,
            candidates,
            cancellationToken);
        if (resolvedConflicts.Count < candidates.Count)
        {
            trace.Add(new NhAiContextTraceEntry(
                "context-resolver",
                "duplicates-or-conflicts-resolved",
                candidates.Count - resolvedConflicts.Count));
        }

        var ranked = await ranker.RankAsync(
            request,
            resolvedConflicts,
            cancellationToken);
        var selected = new List<NhAiContextRankedItem>();
        var characters = 0;
        foreach (var item in ranked)
        {
            if (selected.Count >= request.MaxItems
                || characters + ContentCharacters(item.Item) > request.MaxCharacters)
            {
                continue;
            }
            var estimatedTokens = EstimateTokens(characters + ContentCharacters(item.Item));
            if (estimatedTokens > request.MaxEstimatedTokens)
            {
                continue;
            }
            selected.Add(item);
            characters += ContentCharacters(item.Item);
        }
        if (selected.Count < ranked.Count)
        {
            trace.Add(new NhAiContextTraceEntry(
                "context-resolver",
                "context-budget-applied",
                ranked.Count - selected.Count));
        }

        var contextHash = NhAiCanonicalJson.ComputeHash(selected.Select(item => new
        {
            item.Item.Id,
            item.Item.Version,
            item.Item.SourceId,
            item.Item.LogicalKey,
            item.Item.ContentType,
            ContentHash = item.Item.ContentHash
                ?? NhAiCanonicalJson.ComputeHash(item.Item.ContentReference ?? item.Item.Content),
            item.Item.Classification,
            item.Item.Trust,
            item.Item.ProvenanceReferences
        }).ToArray());
        var resolution = new NhAiContextResolution(
            selected,
            trace.Take(128).ToArray(),
            characters,
            EstimateTokens(characters),
            contextHash);
        var redactedTrace = new NhAiContextResolutionTrace(
            request.InvocationContext.InvocationId,
            request.InvocationContext.Purpose,
            resolution.Trace,
            resolution.Items.Count,
            resolution.TotalCharacters,
            resolution.EstimatedTokens,
            resolution.ContextHash);
        foreach (var sink in traceSinks)
        {
            await sink.WriteAsync(redactedTrace, cancellationToken);
        }
        return resolution;
    }

    private static bool IsValidItem(
        NhAiContextItem item,
        NhAiContextSourceDescriptor source,
        NhAiContextRequest request)
    {
        if (!string.Equals(item.SourceId, source.Id, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(item.Id)
            || item.Id.Length > 256
            || string.IsNullOrWhiteSpace(item.LogicalKey)
            || item.LogicalKey.Length > 256
            || string.IsNullOrWhiteSpace(item.ContentType)
            || item.ContentType.Length > 128
            || (string.IsNullOrEmpty(item.Content)
                && string.IsNullOrWhiteSpace(item.ContentReference))
            || (!string.IsNullOrEmpty(item.Content)
                && !string.IsNullOrWhiteSpace(item.ContentReference))
            || item.Content.Length > source.MaxContentCharacters
            || item.Version < 1
            || item.CreatedAt > request.Now
            || item.EffectiveAt > request.Now
            || item.ExpiresAt <= request.Now
            || item.ObservedAt > request.Now
            || item.Classification > source.MaximumClassification
            || item.Classification > request.MaximumClassification
            || item.Confidence is < 0 or > 1
            || item.ProvenanceReferences.Count > 16
            || item.AllowedDescendantScopes.Count > 32
            || item.Priority is < -1_000 or > 1_000
            || item.ConflictGroup?.Length > 256
            || item.Residency?.Length > 64
            || item.Purpose?.Length > 128
            || item.ContentReference?.Length > 1_024
            || item.ContentHash?.Length > 128
            || item.AuditCorrelationId?.Length > 128)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(item.Purpose)
            && !string.Equals(
                item.Purpose,
                request.InvocationContext.Purpose,
                StringComparison.Ordinal))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(item.ContentHash)
            && !string.IsNullOrEmpty(item.Content)
            && !string.Equals(
                item.ContentHash,
                NhAiCanonicalJson.ComputeHash(item.Content),
                StringComparison.Ordinal))
        {
            return false;
        }

        var scopeAllowed = item.ExecutionScopes.Count > 0
            && item.ExecutionScopes.All(required =>
                request.InvocationContext.ExecutionScopes.Any(actual =>
                    string.Equals(actual.Type, required.Type, StringComparison.Ordinal)
                    && string.Equals(actual.Id, required.Id, StringComparison.Ordinal)));
        if (!scopeAllowed)
        {
            return false;
        }
        if (item.OwnerScope is null)
        {
            return true;
        }
        return request.InvocationContext.ExecutionScopes.Any(actual =>
                SameScope(actual, item.OwnerScope))
            || item.AllowedDescendantScopes.Any(allowed =>
                request.InvocationContext.ExecutionScopes.Any(actual =>
                    SameScope(actual, allowed)));
    }

    private static bool SameScope(
        NhAiExecutionScopeEntry left,
        NhAiExecutionScopeEntry right)
    {
        return string.Equals(left.Type, right.Type, StringComparison.Ordinal)
            && string.Equals(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static int ContentCharacters(NhAiContextItem item)
    {
        return string.IsNullOrEmpty(item.Content)
            ? item.ContentReference?.Length ?? 0
            : item.Content.Length;
    }

    private static int EstimateTokens(int characters)
    {
        return (characters + 3) / 4;
    }

    private static void ValidateRequest(NhAiContextRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query)
            || request.Query.Length > 4_096
            || request.MaxItems < 1
            || request.MaxItems > 256
            || request.MaxCharacters < 1
            || request.MaxCharacters > 1_000_000
            || request.MaxEstimatedTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static void ValidateSource(NhAiContextSourceDescriptor descriptor)
    {
        NhAiNames.ValidateSegment(descriptor.Id, nameof(descriptor.Id));
        if (string.IsNullOrWhiteSpace(descriptor.Description)
            || descriptor.Description.Length > 512
            || descriptor.MaxItems < 1
            || descriptor.MaxItems > 10_000
            || descriptor.MaxContentCharacters < 1
            || descriptor.MaxContentCharacters > 1_000_000)
        {
            throw new InvalidOperationException(
                $"AI context source '{descriptor.Id}' has invalid bounds.");
        }
    }
}
