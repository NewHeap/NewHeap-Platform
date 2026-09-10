using System.Text.Json.Serialization;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Ordered, typed transforms. Unknown kinds must be rejected at the boundary.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NhProxyPathTransform), "path")]
[JsonDerivedType(typeof(NhProxyQueryTransform), "query")]
[JsonDerivedType(typeof(NhProxyHeaderTransform), "header")]
[JsonDerivedType(typeof(NhProxyOriginalHostTransform), "original-host")]
[JsonDerivedType(typeof(NhProxyForwardedHeadersTransform), "forwarded-headers")]
public abstract record NhProxyTransform;

public enum NhProxyPathTransformKind
{
    AddPrefix,
    RemovePrefix,
    Set,
    Pattern
}

public sealed record NhProxyPathTransform(
    NhProxyPathTransformKind Operation,
    string Value) : NhProxyTransform;

public enum NhProxyValueOperation
{
    Set,
    Append,
    Remove
}

public enum NhProxyValueSource
{
    Literal,
    RouteValue
}

/// <summary>Value is absent for Remove; RouteValue names a captured route parameter.</summary>
public sealed record NhProxyQueryTransform(
    string Name,
    NhProxyValueOperation Operation,
    string? Value = null,
    NhProxyValueSource Source = NhProxyValueSource.Literal) : NhProxyTransform;

public enum NhProxyHeaderDirection
{
    Request,
    Response
}

public enum NhProxyResponseHeaderCondition
{
    Success,
    Always
}

/// <summary>Secret, framing, and protected security headers are rejected during validation.</summary>
public sealed record NhProxyHeaderTransform(
    NhProxyHeaderDirection Direction,
    string Name,
    NhProxyValueOperation Operation,
    string? Value = null,
    NhProxyResponseHeaderCondition ResponseCondition = NhProxyResponseHeaderCondition.Success) : NhProxyTransform;

public sealed record NhProxyOriginalHostTransform(bool Enabled) : NhProxyTransform;

public enum NhProxyForwardedHeaderAction
{
    Set,
    Append,
    Remove,
    Off
}

/// <summary>Outbound X-Forwarded behavior; inbound proxy trust remains host-owned.</summary>
public sealed record NhProxyForwardedHeadersTransform(
    NhProxyForwardedHeaderAction Action,
    string Prefix = "X-Forwarded-") : NhProxyTransform;
