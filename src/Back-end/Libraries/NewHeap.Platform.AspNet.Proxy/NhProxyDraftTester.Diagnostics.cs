using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Matching;
using Yarp.ReverseProxy.Model;

namespace NewHeap.Platform.AspNet.Proxy;

public sealed partial class NhProxyDraftTester
{
    private static async Task<ImmutableArray<NhProxyRuleMatchDiagnostic>> DiagnoseAsync(
        NhProxyRewriteConfiguration rewrites, NhProxyRedirectConfiguration redirects, NhProxyTestRequest input,
        NhProxyRuleTestResult result, List<NhProxyRuleMatchDiagnostic> redirectDiagnostics, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<NhProxyRuleMatchDiagnostic>();
        var reserved = result.Outcome == NhProxyTestOutcome.ReservedAdministrationPath;
        var testedRedirects = redirectDiagnostics.ToDictionary(item => item.RuleId);
        foreach (var rule in redirects.Rules.OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id))
        {
            var diagnostic = testedRedirects.GetValueOrDefault(rule.Id)
                ?? new(NhProxyEngine.Redirect, rule.Id, false, false,
                    [Issue(reserved ? "reserved-path" : !rule.Enabled ? "rule-disabled" : "earlier-redirect")]);
            diagnostics.Add(diagnostic with { RuleName = rule.Name, RulePath = rule.Match.Path });
        }

        var skipRewrites = reserved || result.Outcome == NhProxyTestOutcome.Redirect;
        var probes = new List<NhProxyRewriteRule>();
        var checks = new Dictionary<Guid, List<(string RouteId, NhProxyIssue Issue)>>();
        foreach (var rule in rewrites.Rules.Where(rule => rule.Enabled && !skipRewrites))
        {
            var conditions = new List<(string, NhProxyIssue)>();
            checks.Add(rule.Id, conditions);
            void Probe(NhProxyRewriteMatch match, string reason, string? field = null)
            {
                var probe = rule with
                {
                    Id = Guid.NewGuid(), Priority = 0, Transforms = [],
                    Match = match with { Path = match.Path ?? "/{**rest}" }
                };
                probes.Add(probe);
                conditions.Add((NhProxyYarpConfiguration.Key(probe.Id), Issue(reason, field)));
            }

            Probe(rule.Match, "no-matching-rule");
            if (rule.Match.Path is not null)
            {
                Probe(new() { Path = rule.Match.Path }, "path-mismatch");
            }
            if (!rule.Match.Hosts.IsEmpty)
            {
                Probe(new() { Hosts = rule.Match.Hosts }, "host-mismatch");
            }
            if (!rule.Match.Methods.IsEmpty)
            {
                Probe(new() { Methods = rule.Match.Methods }, "method-mismatch");
            }
            foreach (var header in rule.Match.Headers)
            {
                Probe(new() { Headers = [header] }, "header-mismatch", header.Name);
            }
            foreach (var query in rule.Match.QueryParameters)
            {
                Probe(new() { QueryParameters = [query] }, "query-mismatch", query.Name);
            }
        }

        var selector = new DiagnosticEndpointSelector();
        if (probes.Count > 0)
        {
            // Evaluate isolated conditions in one native routing pass; never execute a proxy endpoint.
            await using var app = CreatePreviewApplication(rewrites with { Rules = probes.ToImmutableArray() }, selector);
            app.UseRouting();
            app.Run(_ => Task.CompletedTask);
            var context = CreateContext(input, cancellationToken);
            context.RequestServices = app.Services;
            await ((IApplicationBuilder)app).Build()(context);
        }

        foreach (var rule in rewrites.Rules.OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = result.Outcome == NhProxyTestOutcome.Rewrite && result.SelectedRuleId == rule.Id;
            var matched = selected;
            ImmutableArray<NhProxyIssue> reasons;
            if (skipRewrites || !rule.Enabled)
            {
                reasons = [Issue(reserved ? "reserved-path" : !rule.Enabled ? "rule-disabled" : "redirect-precedence")];
            }
            else
            {
                var conditions = checks[rule.Id];
                matched = selector.MatchedRoutes.Contains(conditions[0].RouteId);
                reasons = matched
                    ? selected ? [] : [Issue("rewrite-precedence")]
                    : conditions.Skip(1).Where(check => !selector.MatchedRoutes.Contains(check.RouteId))
                        .Select(check => check.Issue).ToImmutableArray();
                if (!matched && reasons.IsEmpty)
                {
                    reasons = [Issue("no-matching-rule")];
                }
            }

            diagnostics.Add(new(NhProxyEngine.Rewrite, rule.Id, matched, selected, reasons)
            {
                RuleName = rule.Name, RulePath = rule.Match.Path
            });
        }

        return diagnostics.ToImmutable();
    }

    private static NhProxyIssue Issue(string reason, string? field = null)
    {
        var key = "newheap-proxy." + reason;
        return new(key, key, field);
    }

    private sealed class DiagnosticEndpointSelector : EndpointSelector
    {
        public HashSet<string> MatchedRoutes { get; } = [];

        public override Task SelectAsync(HttpContext httpContext, CandidateSet candidates)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                if (candidates.IsValidCandidate(index)
                    && candidates[index].Endpoint.Metadata.GetMetadata<RouteModel>() is { } route)
                {
                    MatchedRoutes.Add(route.Config.RouteId);
                }
            }

            return Task.CompletedTask;
        }
    }
}
