using System.Collections.Immutable;

namespace NewHeap.Platform.AspNet.Proxy;

public enum NhProxyEngine
{
    Rewrite,
    Redirect
}

/// <summary>Revision zero represents a newly initialized empty store.</summary>
public sealed record NhProxyRewriteConfiguration
{
    public long Revision { get; init; }
    public int FormatVersion { get; init; } = 1;
    public ImmutableArray<NhProxyRewriteRule> Rules { get; init; } = [];
    public ImmutableArray<NhProxyCluster> Clusters { get; init; } = [];
}

public sealed record NhProxyRedirectConfiguration
{
    public long Revision { get; init; }
    public int FormatVersion { get; init; } = 1;
    public ImmutableArray<NhProxyRedirectRule> Rules { get; init; } = [];
}

/// <summary>Replaces one engine atomically; the store assigns the next revision.</summary>
public sealed record NhProxyRewriteSaveRequest(
    long ExpectedRevision,
    ImmutableArray<NhProxyRewriteRule> Rules,
    ImmutableArray<NhProxyCluster> Clusters);

public sealed record NhProxyRedirectSaveRequest(
    long ExpectedRevision,
    ImmutableArray<NhProxyRedirectRule> Rules);

public enum NhProxyActivationState
{
    NotInitialized,
    Pending,
    Active,
    Failed,
    Unconfirmed
}

/// <summary>Codes and localization keys are safe; exceptions and diagnostics stay in logs.</summary>
public sealed record NhProxyIssue(string Code, string LocalizationKey, string? Field = null);

public sealed record NhProxyEngineStatus(
    NhProxyEngine Engine,
    long DesiredRevision,
    long? ActiveRevision,
    NhProxyActivationState State,
    NhProxyIssue? Issue = null);

public sealed record NhProxyStatus(NhProxyEngineStatus Rewrite, NhProxyEngineStatus Redirect);

/// <summary>
/// A committed save can have pending or failed activation. Callers must inspect Activation.
/// Validation and stale-write failures use failed TaskResult values with safe error keys.
/// After a committed save, propagate an activation failure as a failed TaskResult with this
/// result retained in Data so the caller knows persistence succeeded. Never discard a nested failure.
/// </summary>
public sealed record NhProxySaveResult(long SavedRevision, NhProxyEngineStatus Activation);

public static class NhProxyErrorCodes
{
    public const string Validation = "newheap-proxy.validation";
    public const string MaximumChainDepth = "newheap-proxy.maximum-chain-depth";
    public const string RevisionConflict = "newheap-proxy.revision-conflict";
    public const string NotFound = "newheap-proxy.not-found";
    public const string ActivationFailed = "newheap-proxy.activation-failed";
    public const string AccessDenied = "newheap-proxy.access-denied";
    public const string InvalidCredentials = "newheap-proxy.invalid-credentials";
    public const string Throttled = "newheap-proxy.throttled";
}
