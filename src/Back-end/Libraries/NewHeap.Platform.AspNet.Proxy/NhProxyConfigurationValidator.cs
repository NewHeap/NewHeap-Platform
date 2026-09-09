using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Validates exact and regex redirect snapshots. Rewrite validation is not implemented yet.</summary>
public sealed class NhProxyConfigurationValidator(IOptions<NhProxyOptions> options) : INhProxyConfigurationValidator
{
    public Task<TaskResult> ValidateRewritesAsync(NhProxyRewriteSaveRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

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
