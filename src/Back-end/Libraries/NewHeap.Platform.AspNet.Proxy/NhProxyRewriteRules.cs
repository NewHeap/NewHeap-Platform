using System.Collections.Immutable;
using NewHeap.Platform.Common.Attributes;

namespace NewHeap.Platform.AspNet.Proxy;

public sealed record NhProxyRewriteRule
{
    [Filterable]
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public int Priority { get; init; }
    public required NhProxyRewriteMatch Match { get; init; }
    public required Guid ClusterId { get; init; }
    public ImmutableArray<NhProxyTransform> Transforms { get; init; } = [];
    public string? AuthorizationPolicy { get; init; }
    public string? CorsPolicy { get; init; }
    public string? RateLimiterPolicy { get; init; }
    public TimeSpan? RequestTimeout { get; init; }
}

public sealed record NhProxyRewriteMatch
{
    public ImmutableArray<string> Hosts { get; init; } = [];
    public string? Path { get; init; }
    public ImmutableArray<string> Methods { get; init; } = [];
    public ImmutableArray<NhProxyHeaderMatch> Headers { get; init; } = [];
    public ImmutableArray<NhProxyQueryMatch> QueryParameters { get; init; } = [];
}

public enum NhProxyHeaderMatchMode
{
    Exact,
    HeaderPrefix,
    Exists,
    Contains,
    NotContains,
    NotExists
}

public sealed record NhProxyHeaderMatch(
    string Name,
    NhProxyHeaderMatchMode Mode,
    ImmutableArray<string> Values,
    bool IsCaseSensitive = false);

public enum NhProxyQueryMatchMode
{
    Exact,
    Prefix,
    Exists,
    Contains,
    NotContains
}

public sealed record NhProxyQueryMatch(
    string Name,
    NhProxyQueryMatchMode Mode,
    ImmutableArray<string> Values,
    bool IsCaseSensitive = false);

/// <summary>A shared cluster has exactly one destination; balancing and affinity are deferred.</summary>
public sealed record NhProxyCluster
{
    [Filterable]
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required NhProxyDestination Destination { get; init; }
    public TimeSpan? ActivityTimeout { get; init; }
    public NhProxyActiveHealthCheck? ActiveHealthCheck { get; init; }
    public NhProxyPassiveHealthCheck? PassiveHealthCheck { get; init; }
}

public sealed record NhProxyDestination(string Name, Uri Address);

public sealed record NhProxyActiveHealthCheck(
    string Path,
    TimeSpan Interval,
    TimeSpan Timeout,
    string Policy);

public sealed record NhProxyPassiveHealthCheck(
    string Policy,
    TimeSpan ReactivationPeriod);
