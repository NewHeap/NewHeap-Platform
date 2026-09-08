using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Validates literal redirect snapshots. Rewrite validation is not implemented yet.</summary>
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
                || rule.Match is null || rule.Match.PathMode != NhProxyRedirectPathMatchMode.Exact
                || string.IsNullOrEmpty(rule.Match.Path) || !rule.Match.Path.StartsWith('/')
                || rule.Match.Path.IndexOfAny(['?', '#', '\\']) >= 0 || rule.Match.Path.Any(char.IsControl)
                || rule.Match.Hosts.IsDefault || rule.Match.Methods.IsDefault
                || !Enum.IsDefined(rule.Status) || !Enum.IsDefined(rule.QueryMode)
                || !IsValidTarget(rule.Target))
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

            if (rule.Enabled && rule.Target.StartsWith('/')
                && string.Equals(new Uri(new Uri("https://newheap.invalid"), rule.Target).AbsolutePath,
                    new PathString(rule.Match.Path).ToUriComponent(), StringComparison.Ordinal))
            {
                return Task.FromResult(TaskResult.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.self-redirect"));
            }
        }

        return Task.FromResult(TaskResult.Succeeded());
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
