using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Publishes redirects and their prepared regexes atomically. Managed rewrite activation is not implemented yet.</summary>
public sealed class NhProxyRuntime(INhProxyConfigurationValidator validator, IOptions<NhProxyOptions> options) : INhProxyRuntime
{
    internal const string ResolutionTimeoutFailure = "Redirect resolution exceeded its configured time limit.";
    private readonly TimeSpan _resolutionTimeout = TimeSpan.FromMilliseconds(options.Value.Limits.RedirectResolutionTimeoutMilliseconds);
    private sealed record Snapshot(long Revision, ImmutableArray<(NhProxyRedirectRule Rule, ConcurrentDictionary<int, Regex>? Patterns)> Rules);
    private Snapshot? _redirects;

    public NhProxyRuntime(INhProxyConfigurationValidator validator) : this(validator, Options.Create(new NhProxyOptions()))
    {
    }

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

        var initialTimeoutMilliseconds = Math.Max(1, (int)_resolutionTimeout.TotalMilliseconds - 1);
        var snapshot = new Snapshot(configuration.Revision, configuration.Rules.Where(rule => rule.Enabled)
            .OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id)
            .Select(rule => (rule, rule.Match.PathMode == NhProxyRedirectPathMatchMode.Regex
                ? new ConcurrentDictionary<int, Regex>([new(initialTimeoutMilliseconds, NhProxyConfigurationValidator.CreateRegex(rule.Match.Path, TimeSpan.FromMilliseconds(initialTimeoutMilliseconds)))])
                : null)).ToImmutableArray());
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

    internal bool TryRedirect(HttpContext context, out string? failure)
    {
        failure = null;
        if (context.Request.Path.StartsWithSegments(NhProxyOptions.AdministrationPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        var rule = ResolveRedirect(context, started, out var location, out failure);
        if (HasResolutionTimedOut(started))
        {
            failure = ResolutionTimeoutFailure;
            return false;
        }

        if (rule is null)
        {
            return false;
        }

        context.Response.StatusCode = (int)rule.Status;
        context.Response.Headers.Location = location;
        return true;
    }

    private NhProxyRedirectRule? ResolveRedirect(HttpContext context, long started, out string? location, out string? failure)
    {
        location = null;
        failure = null;
        var snapshot = Volatile.Read(ref _redirects)
            ?? throw new InvalidOperationException("NewHeap Proxy redirects have not been initialized. Start the host before processing requests.");

        // ponytail: scan at most MaximumRulesPerEngine rules; index by literal path if the configured ceiling grows.
        string? input = null;
        foreach (var (rule, patterns) in snapshot.Rules)
        {
            if (HasResolutionTimedOut(started))
            {
                failure = ResolutionTimeoutFailure;
                return null;
            }

            if ((!rule.Match.Methods.IsEmpty && !rule.Match.Methods.Contains(context.Request.Method, StringComparer.Ordinal))
                || (!rule.Match.Hosts.IsEmpty && !rule.Match.Hosts.Any(host => MatchesHost(host, context.Request.Host))))
            {
                continue;
            }

            if (patterns is null)
            {
                if (!string.Equals(context.Request.Path.Value, rule.Match.Path, StringComparison.Ordinal))
                {
                    continue;
                }

                if (HasResolutionTimedOut(started))
                {
                    failure = ResolutionTimeoutFailure;
                    return null;
                }

                location = BuildLocation(rule, context.Request.QueryString);
            }
            else
            {
                input ??= context.Request.Path.ToUriComponent() + context.Request.QueryString.Value;
                if (input.Length > 65536)
                {
                    failure = "Regex evaluation exceeded its request limit. Simplify the pattern or shorten the URL.";
                    return null;
                }

                Regex? pattern;
                do
                {
                    pattern = GetRegexForBudget(patterns, rule.Match.Path, _resolutionTimeout - Stopwatch.GetElapsedTime(started));
                    if (pattern is null)
                    {
                        failure = ResolutionTimeoutFailure;
                        return null;
                    }
                }
                // Preparing an uncached timeout variant also consumes the request budget.
                while (pattern.MatchTimeout > _resolutionTimeout - Stopwatch.GetElapsedTime(started));

                try
                {
                    var match = pattern.Match(input);
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (HasResolutionTimedOut(started))
                    {
                        failure = ResolutionTimeoutFailure;
                        return null;
                    }

                    var target = match.Result(rule.Target);
                    if (HasResolutionTimedOut(started))
                    {
                        failure = ResolutionTimeoutFailure;
                        return null;
                    }

                    if (target.Length > 65536 || !NhProxyConfigurationValidator.IsValidTarget(target))
                    {
                        failure = "The regex produced an invalid destination. Check the capture groups and target URL.";
                        return null;
                    }

                    location = BuildLocation(rule with { Target = target, QueryMode = NhProxyRedirectQueryMode.Replace }, QueryString.Empty);
                    if (target.StartsWith('/') && location.Split('#')[0] == context.Request.PathBase.ToUriComponent() + input)
                    {
                        failure = "The regex would redirect to the same request URL.";
                        return null;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    failure = ResolutionTimeoutFailure;
                    return null;
                }
            }

            return rule;
        }

        return null;
    }

    private bool HasResolutionTimedOut(long started)
    {
        return Stopwatch.GetElapsedTime(started) >= _resolutionTimeout;
    }

    internal static Regex? GetRegexForBudget(ConcurrentDictionary<int, Regex> patterns, string pattern, TimeSpan remaining)
    {
        var milliseconds = (int)Math.Min(remaining.TotalMilliseconds, int.MaxValue - 1);
        if (milliseconds < 1)
        {
            return null;
        }

        if (patterns.TryGetValue(milliseconds, out var cached))
        {
            return cached;
        }

        var regex = NhProxyConfigurationValidator.CreateRegex(pattern, TimeSpan.FromMilliseconds(milliseconds));
        // ponytail: retain at most 50 variants per rule, even with a large configured budget; use eviction if uncached variants become frequent.
        lock (patterns)
        {
            if (patterns.Count < 50)
            {
                return patterns.GetOrAdd(milliseconds, regex);
            }
        }

        return regex;
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
