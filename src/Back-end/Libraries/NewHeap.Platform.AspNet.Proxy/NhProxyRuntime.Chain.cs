using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace NewHeap.Platform.AspNet.Proxy;

public sealed partial class NhProxyRuntime
{
    internal const string ChainDepthFailure = NhProxyErrorCodes.MaximumChainDepth;
    private readonly int _maximumChainDepth = options.Value.Limits.MaximumChainDepth;
    private RequestDelegate? _matchRewrite;

    internal void ConfigureChainRouting(IEndpointRouteBuilder endpoints)
    {
        var source = new ProxyEndpoints(endpoints.DataSources.ToArray());
        var pipeline = new ApplicationBuilder(endpoints.ServiceProvider);
        pipeline.UseRouting();
        // Select native routes, but never run their endpoints or contact a destination.
        pipeline.Run(_ => Task.CompletedTask);
        pipeline.UseEndpoints(routes => routes.DataSources.Add(source));
        _matchRewrite = pipeline.Build();
    }

    internal async Task<string?> CheckChainAsync(HttpContext input)
    {
        var preparationStarted = Stopwatch.GetTimestamp();
        var snapshot = Volatile.Read(ref _redirects)
            ?? throw new InvalidOperationException("Initialize redirects before checking rule chains.");
        // Keep redirect execution on the same immutable snapshot as its preflight check.
        input.Items[typeof(Snapshot)] = snapshot;
        var request = CopyRequest(input);
        if (HasResolutionTimedOut(preparationStarted))
        {
            return ResolutionTimeoutFailure;
        }
        var origin = new Uri($"{(input.Request.Scheme.Length == 0 ? "http" : input.Request.Scheme)}://{(input.Request.Host.HasValue ? input.Request.Host.ToUriComponent() : "newheap.invalid")}");
        long depth = 0;
        while (!request.Request.Path.StartsWithSegments(NhProxyOptions.AdministrationPath, StringComparison.OrdinalIgnoreCase))
        {
            input.RequestAborted.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            var redirect = ResolveRedirect(snapshot, request, started, out var location, out var failure, allowSelfRedirect: true);
            if (failure is not null || HasResolutionTimedOut(started))
            {
                return failure ?? ResolutionTimeoutFailure;
            }

            Uri target;
            if (redirect is not null)
            {
                if (++depth > _maximumChainDepth)
                {
                    return ChainDepthFailure;
                }

                target = new Uri(origin, location!);
                if ((redirect.Status == NhProxyRedirectStatus.SeeOther && !HttpMethods.IsHead(request.Request.Method))
                    || ((int)redirect.Status is 301 or 302 && HttpMethods.IsPost(request.Request.Method)))
                {
                    request.Request.Method = "GET";
                }
            }
            else
            {
                if (_matchRewrite is null)
                {
                    return null;
                }

                request.SetEndpoint(null);
                request.Request.RouteValues = new RouteValueDictionary();
                await _matchRewrite(request);
                var route = request.GetEndpoint()?.Metadata.GetMetadata<RouteModel>();
                if (route?.Cluster?.Model.Config.Destinations is not { Count: 1 } destinations
                    || !route.Config.RouteId.StartsWith("newheap-managed-", StringComparison.Ordinal))
                {
                    return null;
                }

                if (++depth > _maximumChainDepth)
                {
                    return ChainDepthFailure;
                }

                var destination = destinations.Single().Value.Address;
                if (!SameOrigin(new Uri(destination), origin))
                {
                    return null;
                }

                using var message = new HttpRequestMessage { Method = new HttpMethod(request.Request.Method) };
                await route.Transformer.TransformRequestAsync(request, message, destination, request.RequestAborted);
                target = message.RequestUri ?? RequestUtilities.MakeDestinationAddress(destination, request.Request.Path, request.Request.QueryString);
                request.Request.Method = message.Method.Method;
                request.Request.Headers.Clear();
                foreach (var header in message.Headers.Where(header => NhProxyConfigurationValidator.IsSafeHeader(header.Key)))
                {
                    request.Request.Headers[header.Key] = new StringValues(header.Value.ToArray());
                }
            }

            // ponytail: only this origin is known to be served here; aliases and external/backend responses need explicit ownership information.
            if (!SameOrigin(target, origin))
            {
                return null;
            }

            var path = PathString.FromUriComponent(target);
            if (input.Request.PathBase.HasValue && !path.StartsWithSegments(input.Request.PathBase, out path))
            {
                return null;
            }

            request.Request.Path = path;
            request.Request.Host = input.Request.Host;
            request.Request.QueryString = new QueryString(target.Query);
        }

        return null;
    }

    private static bool SameOrigin(Uri target, Uri origin) => string.Equals(target.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(target.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) && target.Port == origin.Port;

    private static DefaultHttpContext CopyRequest(HttpContext input)
    {
        var copy = new DefaultHttpContext { RequestServices = input.RequestServices, RequestAborted = input.RequestAborted };
        copy.Request.Scheme = input.Request.Scheme;
        copy.Request.Host = input.Request.Host;
        copy.Request.PathBase = input.Request.PathBase;
        copy.Request.Path = input.Request.Path;
        copy.Request.QueryString = input.Request.QueryString;
        copy.Request.Method = input.Request.Method;
        foreach (var header in input.Request.Headers.Where(header => NhProxyConfigurationValidator.IsSafeHeader(header.Key)))
        {
            copy.Request.Headers[header.Key] = header.Value;
        }

        return copy;
    }

    private sealed class ProxyEndpoints(IEnumerable<EndpointDataSource> sources) : EndpointDataSource, IDisposable
    {
        private readonly CompositeEndpointDataSource _source = new(sources);
        public override IReadOnlyList<Endpoint> Endpoints => _source.Endpoints.Where(endpoint =>
            endpoint.Metadata.GetMetadata<RouteModel>() is not null).ToArray();
        public override IChangeToken GetChangeToken() => _source.GetChangeToken();
        public void Dispose() => _source.Dispose();
    }
}
