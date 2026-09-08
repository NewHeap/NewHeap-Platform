using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Publishes literal redirects as immutable snapshots. Managed rewrite activation is not implemented yet.</summary>
public sealed class NhProxyRuntime(INhProxyConfigurationValidator validator) : INhProxyRuntime
{
    private NhProxyRedirectConfiguration? _redirects;

    public NhProxyStatus GetStatus()
    {
        var snapshot = Volatile.Read(ref _redirects);
        return new NhProxyStatus(
            new NhProxyEngineStatus(NhProxyEngine.Rewrite, 0, null, NhProxyActivationState.NotInitialized),
            new NhProxyEngineStatus(NhProxyEngine.Redirect, snapshot?.Revision ?? 0, snapshot?.Revision,
                snapshot is null ? NhProxyActivationState.NotInitialized : NhProxyActivationState.Active));
    }

    public Task<TaskResult<NhProxyEngineStatus>> PublishRewritesAsync(NhProxyRewriteConfiguration configuration, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<TaskResult<NhProxyEngineStatus>> PublishRedirectsAsync(NhProxyRedirectConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.FormatVersion != 1)
        {
            return TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.unsupported-format");
        }

        var validation = await validator.ValidateRedirectsAsync(new NhProxyRedirectSaveRequest(configuration.Revision, configuration.Rules), cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<NhProxyEngineStatus>.Failed(validation);
        }

        var snapshot = configuration with
        {
            Rules = configuration.Rules.Where(rule => rule.Enabled).OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id).ToImmutableArray()
        };
        // A late publication must never replace a newer revision.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = Volatile.Read(ref _redirects);
            if (previous is not null && previous.Revision >= snapshot.Revision)
            {
                return TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.RevisionConflict, NhProxyErrorCodes.RevisionConflict);
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _redirects, snapshot, previous), previous))
            {
                return TaskResult<NhProxyEngineStatus>.Succeeded(new NhProxyEngineStatus(
                    NhProxyEngine.Redirect, snapshot.Revision, snapshot.Revision, NhProxyActivationState.Active));
            }
        }
    }

    internal bool TryRedirect(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(NhProxyOptions.AdministrationPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var snapshot = Volatile.Read(ref _redirects)
            ?? throw new InvalidOperationException("NewHeap Proxy redirects have not been initialized. Start the host before processing requests.");

        // ponytail: scan at most MaximumRulesPerEngine rules; index by literal path if the configured ceiling grows.
        foreach (var rule in snapshot.Rules)
        {
            if (!string.Equals(context.Request.Path.Value, rule.Match.Path, StringComparison.Ordinal)
                || (!rule.Match.Methods.IsEmpty && !rule.Match.Methods.Contains(context.Request.Method, StringComparer.Ordinal))
                || (!rule.Match.Hosts.IsEmpty && !rule.Match.Hosts.Any(host => MatchesHost(host, context.Request.Host))))
            {
                continue;
            }

            var location = BuildLocation(rule, context.Request.QueryString);
            context.Response.StatusCode = (int)rule.Status;
            context.Response.Headers.Location = location;
            return true;
        }

        return false;
    }

    private static bool MatchesHost(string host, HostString requestHost)
    {
        var configured = HostString.FromUriComponent(host);
        return string.Equals(configured.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase)
            && (configured.Port is null || configured.Port == requestHost.Port);
    }

    private static string BuildLocation(NhProxyRedirectRule rule, QueryString incoming)
    {
        var target = new Uri(new Uri("https://newheap.invalid"), rule.Target);
        target = new UriBuilder(target) { Host = target.IdnHost }.Uri;
        var address = rule.Target.StartsWith('/') ? target.AbsolutePath : target.GetLeftPart(UriPartial.Path);
        var query = rule.QueryMode switch
        {
            NhProxyRedirectQueryMode.Discard => QueryString.Empty,
            NhProxyRedirectQueryMode.Replace => new QueryString(target.Query),
            _ => MergeQuery(incoming, target.Query)
        };
        return address + query + target.Fragment;
    }

    private static QueryString MergeQuery(QueryString incoming, string target)
    {
        var values = QueryHelpers.ParseQuery(incoming.Value);
        foreach (var item in QueryHelpers.ParseQuery(target))
        {
            values[item.Key] = item.Value;
        }

        return QueryString.Create(values);
    }
}
