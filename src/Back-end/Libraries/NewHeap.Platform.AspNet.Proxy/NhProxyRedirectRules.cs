using System.Collections.Immutable;
using NewHeap.Platform.Common.Attributes;

namespace NewHeap.Platform.AspNet.Proxy;

public sealed record NhProxyRedirectRule
{
    [Filterable]
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public int Priority { get; init; }
    public required NhProxyRedirectMatch Match { get; init; }
    public required string Target { get; init; }
    public NhProxyRedirectStatus Status { get; init; } = NhProxyRedirectStatus.Found;
    public NhProxyRedirectQueryMode QueryMode { get; init; } = NhProxyRedirectQueryMode.Preserve;
}

public sealed record NhProxyRedirectMatch
{
    public ImmutableArray<string> Hosts { get; init; } = [];
    public ImmutableArray<string> Methods { get; init; } = [];
    public required string Path { get; init; }
    public NhProxyRedirectPathMatchMode PathMode { get; init; } = NhProxyRedirectPathMatchMode.Exact;
}

public enum NhProxyRedirectPathMatchMode
{
    Exact,
    Prefix,
    RouteTemplate
}

public enum NhProxyRedirectStatus
{
    MovedPermanently = 301,
    Found = 302,
    SeeOther = 303,
    TemporaryRedirect = 307,
    PermanentRedirect = 308
}

public enum NhProxyRedirectQueryMode
{
    /// <summary>Merge incoming query values with target values; target keys replace matching incoming keys.</summary>
    Preserve,
    /// <summary>Remove all query values, including those in the target template.</summary>
    Discard,
    /// <summary>Use only the target template's query values.</summary>
    Replace
}
