using Microsoft.Extensions.AI;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

[Flags]
public enum NhAiModelCapability
{
    None = 0,
    Chat = 1,
    FunctionCalling = 2,
    StructuredOutput = 4,
    Streaming = 8,
    Vision = 16,
    Embeddings = 32
}

public enum NhAiDataClassification
{
    Public = 0,
    Internal = 1,
    Confidential = 2,
    Restricted = 3
}

public enum NhAiStreamingPolicy
{
    Disabled = 0,
    Allowed = 1,
    Required = 2
}

public sealed record NhAiModelBudget(
    int MaxInputTokens,
    int MaxOutputTokens,
    int MaxCalls,
    decimal? MaxEstimatedCost);

public sealed class NhAiModelProfile
{
    internal NhAiModelProfile(
        string name,
        int version,
        object keyedClientKey,
        NhAiModelCapability capabilities,
        IReadOnlySet<NhAiDataClassification> permittedDataClassifications,
        IReadOnlySet<string> permittedExecutionRegions,
        NhAiModelBudget budget,
        NhAiStreamingPolicy streamingPolicy,
        bool retryEligible,
        TimeSpan timeout,
        IReadOnlyList<string> fallbackProfileNames,
        string? evaluationBaselineId,
        IReadOnlySet<string> routingTags,
        IReadOnlySet<string>? acceptedMediaTypes = null)
    {
        Name = name;
        AcceptedMediaTypes = acceptedMediaTypes ?? new HashSet<string>(StringComparer.Ordinal);
        Version = version;
        KeyedClientKey = keyedClientKey;
        Capabilities = capabilities;
        PermittedDataClassifications = permittedDataClassifications;
        PermittedExecutionRegions = permittedExecutionRegions;
        Budget = budget;
        StreamingPolicy = streamingPolicy;
        RetryEligible = retryEligible;
        Timeout = timeout;
        FallbackProfileNames = fallbackProfileNames;
        EvaluationBaselineId = evaluationBaselineId;
        RoutingTags = routingTags;
    }

    public string Name { get; }
    public int Version { get; }
    public object KeyedClientKey { get; }
    public NhAiModelCapability Capabilities { get; }
    public IReadOnlySet<NhAiDataClassification> PermittedDataClassifications { get; }
    public IReadOnlySet<string> PermittedExecutionRegions { get; }
    public NhAiModelBudget Budget { get; }
    public NhAiStreamingPolicy StreamingPolicy { get; }
    public bool RetryEligible { get; }
    public TimeSpan Timeout { get; }
    public IReadOnlyList<string> FallbackProfileNames { get; }
    public string? EvaluationBaselineId { get; }
    public IReadOnlySet<string> RoutingTags { get; }

    /// <summary>
    /// The media types of files the model reads itself, lowercase without parameters, such as
    /// <c>application/pdf</c> or <c>image/png</c>; empty for a model that reads no files. Declare them
    /// with <see cref="NhAiModelProfileBuilder.AcceptMediaTypes"/>.
    /// </summary>
    public IReadOnlySet<string> AcceptedMediaTypes { get; }

    /// <summary>
    /// Whether the model reads files of <paramref name="mediaType"/> itself.
    /// </summary>
    public bool AcceptsMediaType(string? mediaType)
    {
        return NhAiMediaTypes.Normalize(mediaType) is { } normalized && AcceptedMediaTypes.Contains(normalized);
    }

    internal string Fingerprint => string.Join(
        "|",
        Name,
        Version,
        KeyedClientKey,
        (int)Capabilities,
        string.Join(",", PermittedDataClassifications.Order()),
        string.Join(",", PermittedExecutionRegions.Order(StringComparer.OrdinalIgnoreCase)),
        Budget.MaxInputTokens,
        Budget.MaxOutputTokens,
        Budget.MaxCalls,
        Budget.MaxEstimatedCost,
        (int)StreamingPolicy,
        RetryEligible,
        Timeout.Ticks,
        string.Join(",", FallbackProfileNames),
        EvaluationBaselineId,
        string.Join(",", RoutingTags.Order(StringComparer.Ordinal)),
        string.Join(",", AcceptedMediaTypes.Order(StringComparer.Ordinal)));
}

public sealed record NhAiModelResolutionRequest(
    string ProfileName,
    NhAiModelCapability RequiredCapabilities,
    NhAiDataClassification DataClassification,
    string Purpose,
    string? ExecutionRegion = null);

public sealed record NhAiResolvedChatProfile(
    NhAiModelProfile Profile,
    IChatClient Client,
    IReadOnlyList<string> DecisionTrace);

public sealed record NhAiResolvedEmbeddingProfile(
    NhAiModelProfile Profile,
    IEmbeddingGenerator<string, Embedding<float>> Generator,
    IReadOnlyList<string> DecisionTrace);

public interface INhAiModelProfileRegistry
{
    IReadOnlyCollection<NhAiModelProfile> Profiles { get; }

    bool TryGet(string name, out NhAiModelProfile profile);
}

public interface INhAiModelProfileResolver
{
    Task<TaskResult<NhAiResolvedChatProfile>> ResolveChatAsync(
        NhAiModelResolutionRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskResult<NhAiResolvedEmbeddingProfile>> ResolveEmbeddingsAsync(
        NhAiModelResolutionRequest request,
        CancellationToken cancellationToken = default);
}
