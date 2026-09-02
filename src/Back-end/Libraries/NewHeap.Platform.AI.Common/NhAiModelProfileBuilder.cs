namespace NewHeap.Platform.AI;

public sealed class NhAiModelProfileBuilder
{
    private readonly string _name;
    private object? _keyedClientKey;
    private int _version = 1;
    private NhAiModelCapability _capabilities = NhAiModelCapability.Chat;
    private readonly HashSet<NhAiDataClassification> _classifications = [NhAiDataClassification.Public];
    private readonly HashSet<string> _regions = new(StringComparer.OrdinalIgnoreCase);
    private NhAiModelBudget _budget = new(4096, 2048, 8, null);
    private NhAiStreamingPolicy _streamingPolicy = NhAiStreamingPolicy.Disabled;
    private bool _retryEligible;
    private TimeSpan _timeout = TimeSpan.FromSeconds(60);
    private readonly List<string> _fallbackProfiles = [];
    private string? _evaluationBaselineId;
    private readonly HashSet<string> _routingTags = new(StringComparer.Ordinal);

    internal NhAiModelProfileBuilder(string name)
    {
        NhAiNames.ValidateSegment(name, nameof(name));
        _name = name;
    }

    public NhAiModelProfileBuilder WithVersion(int version)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }
        _version = version;
        return this;
    }

    public NhAiModelProfileBuilder UseKeyedClient(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key is string text)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
        }
        _keyedClientKey = key;
        return this;
    }

    public NhAiModelProfileBuilder RequireCapabilities(params NhAiModelCapability[] capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        foreach (var capability in capabilities)
        {
            _capabilities |= capability;
        }
        return this;
    }

    public NhAiModelProfileBuilder PermitDataClassifications(params NhAiDataClassification[] classifications)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        foreach (var classification in classifications)
        {
            _classifications.Add(classification);
        }
        return this;
    }

    public NhAiModelProfileBuilder PermitExecutionRegions(params string[] regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        foreach (var region in regions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(region);
            _regions.Add(region.Trim());
        }
        return this;
    }

    public NhAiModelProfileBuilder WithBudget(
        int maxInputTokens,
        int maxOutputTokens,
        int maxCalls,
        decimal? maxEstimatedCost = null)
    {
        if (maxInputTokens < 1 || maxOutputTokens < 1 || maxCalls < 1 || maxEstimatedCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInputTokens));
        }
        _budget = new NhAiModelBudget(maxInputTokens, maxOutputTokens, maxCalls, maxEstimatedCost);
        return this;
    }

    public NhAiModelProfileBuilder WithStreaming(NhAiStreamingPolicy policy)
    {
        _streamingPolicy = policy;
        if (policy != NhAiStreamingPolicy.Disabled)
        {
            _capabilities |= NhAiModelCapability.Streaming;
        }
        return this;
    }

    public NhAiModelProfileBuilder WithTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        _timeout = timeout;
        return this;
    }

    public NhAiModelProfileBuilder AllowSafeRetry(bool retryEligible = true)
    {
        _retryEligible = retryEligible;
        return this;
    }

    public NhAiModelProfileBuilder WithFallbackProfiles(params string[] profileNames)
    {
        ArgumentNullException.ThrowIfNull(profileNames);
        foreach (var profileName in profileNames)
        {
            NhAiNames.ValidateSegment(profileName, nameof(profileNames));
            if (!_fallbackProfiles.Contains(profileName, StringComparer.Ordinal))
            {
                _fallbackProfiles.Add(profileName);
            }
        }
        return this;
    }

    public NhAiModelProfileBuilder WithEvaluationBaseline(string baselineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineId);
        _evaluationBaselineId = baselineId.Trim();
        return this;
    }

    public NhAiModelProfileBuilder WithRoutingTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        foreach (var tag in tags)
        {
            NhAiNames.ValidateSegment(tag, nameof(tags));
            _routingTags.Add(tag);
        }
        return this;
    }

    internal NhAiModelProfile Build()
    {
        if (_keyedClientKey is null)
        {
            throw new InvalidOperationException($"AI model profile '{_name}' has no keyed IChatClient.");
        }
        return new NhAiModelProfile(
            _name,
            _version,
            _keyedClientKey,
            _capabilities,
            _classifications.ToHashSet(),
            _regions.ToHashSet(StringComparer.OrdinalIgnoreCase),
            _budget,
            _streamingPolicy,
            _retryEligible,
            _timeout,
            _fallbackProfiles.ToArray(),
            _evaluationBaselineId,
            _routingTags.ToHashSet(StringComparer.Ordinal));
    }
}

internal static class NhAiNames
{
    public static bool IsSegment(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value[0] != '-'
            && value[^1] != '-'
            && !value.Contains("--", StringComparison.Ordinal)
            && value.All(character => character == '-'
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9'));
    }

    public static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!IsSegment(value))
        {
            throw new ArgumentException("AI identifiers must use lowercase dash-case.", parameterName);
        }
    }
}
