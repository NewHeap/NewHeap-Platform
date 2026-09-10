using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace NewHeap.Platform.AspNet.Proxy;

internal static class NhProxyYarpConfiguration
{
    internal static string Key(Guid id) => $"newheap-managed-{id:N}";

    internal static RouteConfig Route(NhProxyRewriteRule rule) => new()
    {
        RouteId = Key(rule.Id), ClusterId = Key(rule.ClusterId), Order = rule.Priority,
        AuthorizationPolicy = rule.AuthorizationPolicy, CorsPolicy = rule.CorsPolicy,
        RateLimiterPolicy = rule.RateLimiterPolicy, Timeout = rule.RequestTimeout,
        Match = new RouteMatch
        {
            Path = rule.Match.Path, Hosts = rule.Match.Hosts, Methods = rule.Match.Methods,
            Headers = rule.Match.Headers.Select(header => new RouteHeader
            {
                Name = header.Name, Mode = header.Mode == NhProxyHeaderMatchMode.Exact ? HeaderMatchMode.ExactHeader : Enum.Parse<HeaderMatchMode>(header.Mode.ToString()),
                Values = header.Values, IsCaseSensitive = header.IsCaseSensitive
            }).ToArray(),
            QueryParameters = rule.Match.QueryParameters.Select(query => new RouteQueryParameter
            {
                Name = query.Name, Mode = Enum.Parse<QueryParameterMatchMode>(query.Mode.ToString()),
                Values = query.Values, IsCaseSensitive = query.IsCaseSensitive
            }).ToArray()
        },
        Transforms = rule.Transforms.Select(Transform).ToArray()
    };

    internal static ClusterConfig Cluster(NhProxyCluster cluster) => new()
    {
        ClusterId = Key(cluster.Id),
        Destinations = new Dictionary<string, DestinationConfig>
        {
            [cluster.Destination.Name] = new() { Address = cluster.Destination.Address.AbsoluteUri }
        },
        HttpRequest = new ForwarderRequestConfig { ActivityTimeout = cluster.ActivityTimeout },
        HealthCheck = new HealthCheckConfig
        {
            Active = cluster.ActiveHealthCheck is { } active ? new ActiveHealthCheckConfig
            {
                Enabled = true, Path = active.Path, Interval = active.Interval, Timeout = active.Timeout, Policy = active.Policy
            } : null,
            Passive = cluster.PassiveHealthCheck is { } passive ? new PassiveHealthCheckConfig
            {
                Enabled = true, Policy = passive.Policy, ReactivationPeriod = passive.ReactivationPeriod
            } : null
        }
    };

    private static IReadOnlyDictionary<string, string> Transform(NhProxyTransform transform)
    {
        switch (transform)
        {
            case NhProxyPathTransform path:
                var key = path.Operation switch
                {
                    NhProxyPathTransformKind.AddPrefix => "PathPrefix",
                    NhProxyPathTransformKind.RemovePrefix => "PathRemovePrefix",
                    NhProxyPathTransformKind.Set => "PathSet",
                    NhProxyPathTransformKind.Pattern => "PathPattern",
                    _ => throw new ArgumentOutOfRangeException(nameof(transform))
                };
                return new Dictionary<string, string> { [key] = path.Value };
            case NhProxyQueryTransform query:
                if (query.Operation == NhProxyValueOperation.Remove)
                {
                    return new Dictionary<string, string> { ["QueryRemoveParameter"] = query.Name };
                }

                return new Dictionary<string, string>
                {
                    [query.Source == NhProxyValueSource.RouteValue ? "QueryRouteParameter" : "QueryValueParameter"] = query.Name,
                    [query.Operation.ToString()] = query.Value!
                };
            case NhProxyHeaderTransform header:
                var direction = header.Direction.ToString();
                var result = header.Operation == NhProxyValueOperation.Remove
                    ? new Dictionary<string, string> { [direction + "HeaderRemove"] = header.Name }
                    : new Dictionary<string, string> { [direction + "Header"] = header.Name, [header.Operation.ToString()] = header.Value! };
                if (header.Direction == NhProxyHeaderDirection.Response)
                {
                    result["When"] = header.ResponseCondition.ToString();
                }

                return result;
            case NhProxyOriginalHostTransform host:
                return new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = host.Enabled.ToString() };
            case NhProxyForwardedHeadersTransform forwarded:
                return new Dictionary<string, string> { ["X-Forwarded"] = forwarded.Action.ToString(), ["HeaderPrefix"] = forwarded.Prefix };
            default:
                throw new ArgumentException("Unsupported transform.", nameof(transform));
        }
    }
}
