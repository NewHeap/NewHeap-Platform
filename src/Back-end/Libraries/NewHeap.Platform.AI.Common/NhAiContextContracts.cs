using System.Text.Json;

namespace NewHeap.Platform.AI;

public enum NhAiContextTrust
{
    TrustedApplication = 0,
    VerifiedExternal = 1,
    UntrustedRetrieved = 2
}

public sealed record NhAiContextSourceDescriptor(
    string Id,
    string Description,
    NhAiDataClassification MaximumClassification,
    int MaxItems,
    int MaxContentCharacters);

public sealed record NhAiContextRequest(
    NhAiInvocationContext InvocationContext,
    string Query,
    NhAiDataClassification MaximumClassification,
    int MaxItems,
    int MaxCharacters,
    int MaxEstimatedTokens)
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record NhAiContextItem(
    string Id,
    string LogicalKey,
    string SourceId,
    string ContentType,
    string Content,
    NhAiDataClassification Classification,
    NhAiContextTrust Trust,
    IReadOnlyList<NhAiExecutionScopeEntry> ExecutionScopes,
    DateTimeOffset CreatedAt)
{
    public int Version { get; init; } = 1;
    public NhAiExecutionScopeEntry? OwnerScope { get; init; }
    public IReadOnlyList<NhAiExecutionScopeEntry> AllowedDescendantScopes { get; init; } = [];
    public DateTimeOffset? EffectiveAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public string? ReplacesItemId { get; init; }
    public string? ConflictGroup { get; init; }
    public string? AuthorReference { get; init; }
    public string? VerifierReference { get; init; }
    public decimal? Confidence { get; init; }
    public int Priority { get; init; }
    public string? Residency { get; init; }
    public string? Purpose { get; init; }
    public NhAiRetentionCategory RetentionCategory { get; init; } = NhAiRetentionCategory.Operational;
    public string? ContentReference { get; init; }
    public string? ContentHash { get; init; }
    public string? AuditCorrelationId { get; init; }
    public IReadOnlyList<string> ProvenanceReferences { get; init; } = [];
}

public sealed record NhAiContextRankedItem(
    NhAiContextItem Item,
    decimal Score,
    string ReasonCode);

public sealed record NhAiContextTraceEntry(
    string SourceId,
    string OutcomeCode,
    int ItemCount = 0);

public sealed record NhAiContextResolution(
    IReadOnlyList<NhAiContextRankedItem> Items,
    IReadOnlyList<NhAiContextTraceEntry> Trace,
    int TotalCharacters,
    int EstimatedTokens,
    string ContextHash);

public sealed record NhAiContextResolutionTrace(
    Guid InvocationId,
    string Purpose,
    IReadOnlyList<NhAiContextTraceEntry> Entries,
    int SelectedItems,
    int TotalCharacters,
    int EstimatedTokens,
    string ContextHash);

public enum NhAiPromptBlockRole
{
    SystemInstructions = 0,
    TrustedPolicyMetadata = 10,
    RetrievedData = 20
}

public sealed record NhAiPromptBlock(
    NhAiPromptBlockRole Role,
    NhAiContextTrust Trust,
    bool InstructionAuthority,
    string Content,
    string ContentHash);

public interface INhAiContextSource
{
    NhAiContextSourceDescriptor Descriptor { get; }

    ValueTask<IReadOnlyList<NhAiContextItem>> RetrieveAsync(
        NhAiContextRequest request,
        CancellationToken cancellationToken = default);
}

public interface INhAiContextAuthorizationPolicy
{
    ValueTask<bool> CanAccessSourceAsync(
        NhAiContextSourceDescriptor source,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);
}

public interface INhAiContextRanker
{
    ValueTask<IReadOnlyList<NhAiContextRankedItem>> RankAsync(
        NhAiContextRequest request,
        IReadOnlyList<NhAiContextItem> items,
        CancellationToken cancellationToken = default);
}

public interface INhAiContextConflictResolver
{
    ValueTask<IReadOnlyList<NhAiContextItem>> ResolveAsync(
        NhAiContextRequest request,
        IReadOnlyList<NhAiContextItem> items,
        CancellationToken cancellationToken = default);
}

public interface INhAiContextTraceSink
{
    ValueTask WriteAsync(
        NhAiContextResolutionTrace trace,
        CancellationToken cancellationToken = default);
}

public interface INhAiContextResolver
{
    ValueTask<NhAiContextResolution> ResolveAsync(
        NhAiContextRequest request,
        CancellationToken cancellationToken = default);
}

public interface INhAiContextFormatter
{
    string FormatAsData(NhAiContextResolution resolution);
}

public interface INhAiPromptAssembler
{
    IReadOnlyList<NhAiPromptBlock> Assemble(
        string systemInstructions,
        string trustedPolicyMetadata,
        NhAiContextResolution resolution);
}

internal sealed class NhAiDenyContextAuthorizationPolicy : INhAiContextAuthorizationPolicy
{
    public ValueTask<bool> CanAccessSourceAsync(
        NhAiContextSourceDescriptor source,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

internal sealed class NhAiDeterministicContextRanker : INhAiContextRanker
{
    public ValueTask<IReadOnlyList<NhAiContextRankedItem>> RankAsync(
        NhAiContextRequest request,
        IReadOnlyList<NhAiContextItem> items,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var terms = request.Query.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ranked = items
            .Select(item => new NhAiContextRankedItem(
                item,
                terms.Count(term => item.Content.Contains(
                    term,
                    StringComparison.OrdinalIgnoreCase)),
                "deterministic-term-match"))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Item.Version)
            .ThenByDescending(item => item.Item.CreatedAt)
            .ThenBy(item => item.Item.Id, StringComparer.Ordinal)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<NhAiContextRankedItem>>(ranked);
    }
}

internal sealed class NhAiDeterministicContextConflictResolver : INhAiContextConflictResolver
{
    public ValueTask<IReadOnlyList<NhAiContextItem>> ResolveAsync(
        NhAiContextRequest request,
        IReadOnlyList<NhAiContextItem> items,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = items
            .GroupBy(
                item => item.ConflictGroup ?? item.LogicalKey,
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.Priority)
                .ThenByDescending(item => item.Version)
                .ThenByDescending(item => item.EffectiveAt ?? item.CreatedAt)
                .ThenByDescending(item => item.CreatedAt)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .First())
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<NhAiContextItem>>(resolved);
    }
}

internal sealed class NhAiNoOpContextTraceSink : INhAiContextTraceSink
{
    public ValueTask WriteAsync(
        NhAiContextResolutionTrace trace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class NhAiJsonContextFormatter : INhAiContextFormatter
{
    public string FormatAsData(NhAiContextResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return JsonSerializer.Serialize(
            new
            {
                role = "retrieved-data",
                instructionAuthority = false,
                contextHash = resolution.ContextHash,
                items = resolution.Items.Select(item => new
                {
                    id = item.Item.Id,
                    source = item.Item.SourceId,
                    trust = item.Item.Trust.ToString(),
                    classification = item.Item.Classification.ToString(),
                    provenance = item.Item.ProvenanceReferences,
                    contentType = item.Item.ContentType,
                    content = item.Item.Content,
                    contentReference = item.Item.ContentReference,
                    contentHash = item.Item.ContentHash
                })
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}

internal sealed class NhAiRoleSeparatedPromptAssembler(
    INhAiContextFormatter formatter) : INhAiPromptAssembler
{
    public IReadOnlyList<NhAiPromptBlock> Assemble(
        string systemInstructions,
        string trustedPolicyMetadata,
        NhAiContextResolution resolution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemInstructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPolicyMetadata);
        ArgumentNullException.ThrowIfNull(resolution);
        var retrievedData = formatter.FormatAsData(resolution);
        return
        [
            Create(
                NhAiPromptBlockRole.SystemInstructions,
                NhAiContextTrust.TrustedApplication,
                true,
                systemInstructions),
            Create(
                NhAiPromptBlockRole.TrustedPolicyMetadata,
                NhAiContextTrust.TrustedApplication,
                true,
                trustedPolicyMetadata),
            Create(
                NhAiPromptBlockRole.RetrievedData,
                NhAiContextTrust.UntrustedRetrieved,
                false,
                retrievedData)
        ];
    }

    private static NhAiPromptBlock Create(
        NhAiPromptBlockRole role,
        NhAiContextTrust trust,
        bool instructionAuthority,
        string content)
    {
        if (content.Length > 1_000_000)
        {
            throw new InvalidOperationException("An AI prompt block exceeded its content bound.");
        }
        return new NhAiPromptBlock(
            role,
            trust,
            instructionAuthority,
            content,
            NhAiCanonicalJson.ComputeHash(content));
    }
}
