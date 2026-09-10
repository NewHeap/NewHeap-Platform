using System.Buffers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;
using Yarp.ReverseProxy.Configuration;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Validates managed snapshots before persistence, publication and isolated testing.</summary>
public sealed class NhProxyConfigurationValidator(IOptions<NhProxyOptions> options, IConfigValidator? yarp = null,
    IInlineConstraintResolver? constraints = null) : INhProxyConfigurationValidator
{
    private static readonly SearchValues<char> TokenCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!#$%&'*+-.^_`|~");

    public NhProxyConfigurationValidator(IOptions<NhProxyOptions> options) : this(options, null)
    {
    }

    public async Task<TaskResult> ValidateRewritesAsync(NhProxyRewriteSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedRevision < 0 || request.Rules.IsDefault || request.Clusters.IsDefault
            || request.Rules.Length > options.Value.Limits.MaximumRulesPerEngine
            || request.Clusters.Length > options.Value.Limits.MaximumRulesPerEngine)
        {
            return InvalidRewrite();
        }

        // Offline stores use the same native validators with the default host policies.
        using var defaults = yarp is null || constraints is null ? new ServiceCollection().AddLogging().AddReverseProxy().Services.BuildServiceProvider() : null;
        var native = yarp ?? defaults!.GetRequiredService<IConfigValidator>();
        var resolver = constraints ?? defaults!.GetRequiredService<IInlineConstraintResolver>();
        var clusters = new HashSet<Guid>();
        foreach (var cluster in request.Clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cluster is null || cluster.Id == Guid.Empty || !clusters.Add(cluster.Id) || string.IsNullOrWhiteSpace(cluster.Name)
                || cluster.Destination is not { Address: { IsAbsoluteUri: true } address } destination
                || string.IsNullOrWhiteSpace(destination.Name) || !IsValidTarget(address.OriginalString)
                || address.Scheme is not ("http" or "https") || address.Query.Length != 0 || address.Fragment.Length != 0
                || (options.Value.AllowedDestinationHosts.Length > 0
                    && !options.Value.AllowedDestinationHosts.Contains(address.IdnHost, StringComparer.OrdinalIgnoreCase))
                || cluster.ActivityTimeout <= TimeSpan.Zero
                || (cluster.ActiveHealthCheck is { } active && (active.Interval <= TimeSpan.Zero || active.Timeout <= TimeSpan.Zero))
                || (cluster.PassiveHealthCheck is { } passive && passive.ReactivationPeriod <= TimeSpan.Zero))
            {
                return InvalidRewrite();
            }

            if ((await native.ValidateClusterAsync(NhProxyYarpConfiguration.Cluster(cluster))).Count > 0)
            {
                return InvalidRewrite();
            }
        }

        var ids = new HashSet<Guid>();
        var matches = new HashSet<(int Priority, RouteMatch Match)>();
        foreach (var rule in request.Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule is null || rule.Id == Guid.Empty || !ids.Add(rule.Id) || string.IsNullOrWhiteSpace(rule.Name)
                || !clusters.Contains(rule.ClusterId) || rule.Match is not { } match || rule.Transforms.IsDefault
                || match.Hosts.IsDefault || match.Methods.IsDefault || match.Headers.IsDefault || match.QueryParameters.IsDefault
                || rule.RequestTimeout <= TimeSpan.Zero || rule.RequestTimeout?.TotalMilliseconds > int.MaxValue
                || (match.Path is not null && (!match.Path.StartsWith('/') || match.Path.Any(char.IsControl)
                    || new PathString(match.Path).StartsWithSegments(NhProxyOptions.AdministrationPath, StringComparison.OrdinalIgnoreCase)))
                || match.Hosts.Any(string.IsNullOrWhiteSpace) || match.Methods.Any(method => !IsToken(method))
                || match.Headers.Any(header => header is null || !IsSafeHeader(header.Name) || !Enum.IsDefined(header.Mode)
                    || header.Values.IsDefault || header.Values.Any(value => value is null || value.Any(char.IsControl)))
                || match.QueryParameters.Any(query => query is null || string.IsNullOrWhiteSpace(query.Name) || query.Name.Any(char.IsControl) || !Enum.IsDefined(query.Mode)
                    || query.Values.IsDefault || query.Values.Any(value => value is null || value.Any(char.IsControl)))
                || rule.Transforms.Any(transform => !IsValidTransform(transform)))
            {
                return InvalidRewrite();
            }

            var route = NhProxyYarpConfiguration.Route(rule);
            if ((rule.Enabled && !matches.Add((rule.Priority, route.Match)))
                || (await native.ValidateRouteAsync(route)).Count > 0 || !HasSupportedConstraints(rule.Match.Path, resolver))
            {
                return InvalidRewrite();
            }
        }

        return TaskResult.Succeeded();
    }

    private static TaskResult InvalidRewrite() => TaskResult.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.invalid-rewrite-snapshot");

    internal static bool HasSupportedConstraints(string? path, IInlineConstraintResolver resolver)
    {
        try
        {
            return RoutePatternFactory.Parse(path ?? "/{**rest}").Parameters.SelectMany(parameter => parameter.ParameterPolicies)
                .All(policy => policy.Content is { } content && resolver.ResolveConstraint(content) is not null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsToken(string? value) => !string.IsNullOrEmpty(value)
        && !value.AsSpan().ContainsAnyExcept(TokenCharacters);

    internal static bool IsSafeHeader(string? name) => IsToken(name)
        && !new[] { "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "Host", "Content-Length", "Transfer-Encoding",
            "Connection", "Upgrade", "Keep-Alive", "TE", "Trailer", "Proxy-Authenticate", "WWW-Authenticate",
            "Content-Security-Policy", "Strict-Transport-Security", "X-Frame-Options", "X-Content-Type-Options",
            "X-Api-Key", "Api-Key", "X-Auth-Token" }
            .Contains(name, StringComparer.OrdinalIgnoreCase)
        && !name!.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidTransform(NhProxyTransform transform) => transform switch
    {
        NhProxyPathTransform path => Enum.IsDefined(path.Operation) && !string.IsNullOrEmpty(path.Value)
            && path.Value.StartsWith('/') && path.Value.IndexOfAny(['?', '#', '\\']) < 0 && !path.Value.Any(char.IsControl),
        NhProxyQueryTransform query => Enum.IsDefined(query.Operation) && Enum.IsDefined(query.Source)
            && !string.IsNullOrWhiteSpace(query.Name) && !query.Name.Any(char.IsControl)
            && (query.Operation == NhProxyValueOperation.Remove ? query.Value is null : query.Value is not null && !query.Value.Any(char.IsControl)),
        NhProxyHeaderTransform header => IsSafeHeader(header.Name) && Enum.IsDefined(header.Operation)
            && Enum.IsDefined(header.Direction) && Enum.IsDefined(header.ResponseCondition)
            && (header.Operation == NhProxyValueOperation.Remove ? header.Value is null : header.Value is not null && !header.Value.Any(char.IsControl)),
        NhProxyOriginalHostTransform => true,
        NhProxyForwardedHeadersTransform forwarded => Enum.IsDefined(forwarded.Action) && IsToken(forwarded.Prefix)
            && new[] { "For", "Host", "Proto", "Prefix" }.All(suffix => IsSafeHeader(forwarded.Prefix + suffix)
                || forwarded.Prefix.Equals("X-Forwarded-", StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    public Task<TaskResult> ValidateRedirectsAsync(NhProxyRedirectSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ExpectedRevision < 0 || request.Rules.IsDefault || request.Rules.Length > options.Value.Limits.MaximumRulesPerEngine)
        {
            return Task.FromResult(TaskResult.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.invalid-redirect-snapshot"));
        }

        var ids = new HashSet<Guid>();
        foreach (var rule in request.Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule is null || rule.Id == Guid.Empty || !ids.Add(rule.Id) || string.IsNullOrWhiteSpace(rule.Name)
                || rule.Match is null || string.IsNullOrEmpty(rule.Match.Path) || rule.Match.Path.Any(char.IsControl)
                || rule.Match.Hosts.IsDefault || rule.Match.Methods.IsDefault
                || !Enum.IsDefined(rule.Status) || !Enum.IsDefined(rule.QueryMode)
                || (rule.Match.PathMode == NhProxyRedirectPathMatchMode.Regex
                    ? !IsValidRegexRule(rule)
                    : rule.Match.PathMode != NhProxyRedirectPathMatchMode.Exact || !rule.Match.Path.StartsWith('/')
                        || rule.Match.Path.IndexOfAny(['?', '#', '\\']) >= 0 || !IsValidTarget(rule.Target)))
            {
                return Task.FromResult(TaskResult.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.invalid-literal-redirect"));
            }

            foreach (var host in rule.Match.Hosts)
            {
                if (string.IsNullOrWhiteSpace(host) || host.IndexOfAny(['/', '\\', '?', '#', '@', '*']) >= 0
                    || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl)
                    || !Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri)
                    || uri.HostNameType == UriHostNameType.Unknown || uri.AbsolutePath != "/")
                {
                    return Task.FromResult(TaskResult.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.invalid-redirect-host"));
                }
            }

            foreach (var method in rule.Match.Methods)
            {
                if (string.IsNullOrEmpty(method) || method.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)))
                {
                    return Task.FromResult(TaskResult.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.invalid-redirect-method"));
                }
            }

            if (rule.Enabled && rule.Match.PathMode == NhProxyRedirectPathMatchMode.Exact && rule.Target.StartsWith('/')
                && string.Equals(new Uri(new Uri("https://newheap.invalid"), rule.Target).AbsolutePath,
                    new PathString(rule.Match.Path).ToUriComponent(), StringComparison.Ordinal))
            {
                return Task.FromResult(TaskResult.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.self-redirect"));
            }
        }

        return Task.FromResult(TaskResult.Succeeded());
    }

    internal static Regex CreateRegex(string pattern, TimeSpan? timeout = null) => new(pattern, RegexOptions.CultureInvariant, timeout ?? TimeSpan.FromMilliseconds(50));

    private static bool IsValidRegexRule(NhProxyRedirectRule rule)
    {
        if (rule.Match.Path.Length > 2048 || string.IsNullOrEmpty(rule.Target) || rule.Target.Length > 4096)
        {
            return false;
        }

        try
        {
            _ = CreateRegex(rule.Match.Path);
            // Validate the URL structure with substitutions replaced; the actual expansion is checked again per request.
            var target = Regex.Replace(rule.Target, @"\$(?:\{[^}]*\}|[0-9]+|[$&`'+_])", "capture", RegexOptions.NonBacktracking);
            return IsValidTarget(target)
                && !Regex.IsMatch(rule.Target, @"\Ahttps?://[^/?#]*\$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsValidTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Any(char.IsControl) || target.Contains('\\'))
        {
            return false;
        }

        if (target.StartsWith('/'))
        {
            return !target.StartsWith("//", StringComparison.Ordinal)
                && Uri.IsWellFormedUriString("https://newheap.invalid" + target, UriKind.Absolute);
        }

        return Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && uri.IsWellFormedOriginalString() && (uri.Scheme == "http" || uri.Scheme == "https")
            && string.IsNullOrEmpty(uri.UserInfo);
    }
}
