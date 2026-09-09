using System.Collections.Immutable;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Synthetic input only; never populated from administrator cookies or credentials.</summary>
public sealed record NhProxyTestRequest
{
    public required Uri Url { get; init; }
    public string Method { get; init; } = "GET";
    public ImmutableDictionary<string, ImmutableArray<string>> Headers { get; init; } =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty;
}

public sealed record NhProxyRevisions(long Rewrite, long Redirect);

public sealed record NhProxyRewriteTestRequest(
    NhProxyRevisions ExpectedRevisions,
    NhProxyRewriteRule Draft,
    NhProxyTestRequest Request)
{
    /// <summary>Null uses saved clusters; otherwise replaces them in the isolated candidate.</summary>
    public ImmutableArray<NhProxyCluster>? DraftClusters { get; init; }
    public bool SimulateEnabled { get; init; }
}

public sealed record NhProxyRedirectTestRequest(
    NhProxyRevisions ExpectedRevisions,
    NhProxyRedirectRule Draft,
    NhProxyTestRequest Request,
    bool SimulateEnabled = false);

public enum NhProxyTestOutcome
{
    NoMatch,
    Rewrite,
    Redirect,
    ReservedAdministrationPath
}

public sealed record NhProxyRuleMatchDiagnostic(
    NhProxyEngine Engine,
    Guid RuleId,
    bool Matched,
    bool Selected,
    ImmutableArray<NhProxyIssue> Reasons);

public sealed record NhProxyRewritePreview(
    Guid ClusterId,
    Uri DestinationAddress,
    Uri TargetUrl,
    ImmutableDictionary<string, ImmutableArray<string>> SafeRequestHeaders,
    ImmutableArray<NhProxyHeaderTransform> UnevaluatedResponseHeaderTransforms);

/// <summary>Returned as diagnostic data, never an actual redirect response from the test endpoint.</summary>
public sealed record NhProxyRedirectPreview(
    NhProxyRedirectStatus Status,
    string Location,
    bool PreservesMethod);

public sealed record NhProxyRuleTestResult
{
    public required NhProxyRevisions TestedRevisions { get; init; }
    public required NhProxyStatus LiveStatus { get; init; }
    public required NhProxyTestOutcome Outcome { get; init; }
    public Guid? SelectedRuleId { get; init; }
    public bool SimulatedEnabled { get; init; }
    public ImmutableDictionary<string, string> RouteValues { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<NhProxyRuleMatchDiagnostic> Matches { get; init; } = [];
    public ImmutableArray<NhProxyIssue> Warnings { get; init; } = [];
    public NhProxyRewritePreview? Rewrite { get; init; }
    public NhProxyRedirectPreview? Redirect { get; init; }
}

/// <summary>
/// No persistence, live publication, outbound requests, or health probes. Failed validation and
/// stale revisions return failed TaskResult values. No-match is a successful diagnostic result.
/// Preview does not prove connectivity, authorization, destination health, or upstream responses.
/// </summary>
public interface INhProxyDraftTester
{
    /// <summary>Tests the saved managed rules without replacing a rule or enabling disabled rules.</summary>
    Task<TaskResult<NhProxyRuleTestResult>> TestSavedAsync(NhProxyRevisions expectedRevisions, NhProxyTestRequest request, CancellationToken cancellationToken = default);
    Task<TaskResult<NhProxyRuleTestResult>> TestRewriteAsync(NhProxyRewriteTestRequest request, CancellationToken cancellationToken = default);
    Task<TaskResult<NhProxyRuleTestResult>> TestRedirectAsync(NhProxyRedirectTestRequest request, CancellationToken cancellationToken = default);
}
