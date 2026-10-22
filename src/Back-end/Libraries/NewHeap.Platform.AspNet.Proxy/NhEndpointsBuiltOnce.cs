using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>
/// Collects the endpoints that a NewHeap <c>Map…</c> extension maps through it, so that
/// <see cref="AddTo(IEndpointRouteBuilder)"/> adds each of its data sources to the application's
/// endpoints through an <see cref="NhBuiltOnceEndpointDataSource"/>.
/// </summary>
/// <remarks>
/// ASP.NET Core reads the endpoint data sources through more than one composite: the
/// application-wide <see cref="EndpointDataSource"/>, which the authorization middleware's policy
/// cache reads while the pipeline is built and which link generation and API descriptions use, and
/// the routing middleware's own composite, which it reads on the first request. Minimal API
/// endpoints, mapped directly or in a route group, are built again, request delegates included, on
/// every read, so without this collector every host start built them twice.
/// <para>
/// A copy of the helper of the same name in NewHeap.Platform.AspNet.Common, which this package does
/// not depend on; keep the copies the same.
/// </para>
/// </remarks>
internal sealed class NhEndpointsBuiltOnce(IEndpointRouteBuilder endpoints) : IEndpointRouteBuilder
{
    private readonly List<EndpointDataSource> _dataSources = [];

    public ICollection<EndpointDataSource> DataSources => _dataSources;

    public IServiceProvider ServiceProvider => endpoints.ServiceProvider;

    public IApplicationBuilder CreateApplicationBuilder()
    {
        return endpoints.CreateApplicationBuilder();
    }

    /// <summary>
    /// Adds the collected data sources to <paramref name="target"/>, each built once for every reader.
    /// </summary>
    public void AddTo(IEndpointRouteBuilder target)
    {
        foreach (var dataSource in _dataSources)
        {
            target.DataSources.Add(new NhBuiltOnceEndpointDataSource(dataSource));
        }
    }
}

/// <summary>
/// Builds the endpoints of one data source, such as a route group, once and gives every reader the
/// same endpoints until the source's change token fires.
/// </summary>
internal sealed class NhBuiltOnceEndpointDataSource : EndpointDataSource, IDisposable
{
    private readonly EndpointDataSource _source;
    private readonly Lock _gate = new();
    private Snapshot? _snapshot;

    public NhBuiltOnceEndpointDataSource(EndpointDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            if (snapshot is not null && !snapshot.ChangeToken.HasChanged)
            {
                return snapshot.Endpoints;
            }

            lock (_gate)
            {
                snapshot = _snapshot;
                if (snapshot is null || snapshot.ChangeToken.HasChanged)
                {
                    // The token is taken first, so a change while the endpoints are built rebuilds them
                    // on the next read.
                    var changeToken = _source.GetChangeToken();
                    snapshot = new Snapshot(_source.Endpoints, changeToken);
                    Volatile.Write(ref _snapshot, snapshot);
                }

                return snapshot.Endpoints;
            }
        }
    }

    public override IChangeToken GetChangeToken()
    {
        return _source.GetChangeToken();
    }

    public override IReadOnlyList<Endpoint> GetGroupedEndpoints(RouteGroupContext context)
    {
        // Mapped into an application's own route group, the endpoints are built with that group's
        // prefix and conventions, which the group passes on every read.
        return _source.GetGroupedEndpoints(context);
    }

    public void Dispose()
    {
        if (_source is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed record Snapshot(IReadOnlyList<Endpoint> Endpoints, IChangeToken ChangeToken);
}
