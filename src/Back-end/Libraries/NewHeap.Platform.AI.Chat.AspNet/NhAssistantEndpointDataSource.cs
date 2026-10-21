using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Builds the endpoints of one assistant route group once and gives every reader the same endpoints
/// until the group's change token fires.
/// </summary>
/// <remarks>
/// ASP.NET Core reads the endpoint data sources through more than one composite: the
/// application-wide <see cref="EndpointDataSource"/>, which the authorization middleware's policy
/// cache reads while the pipeline is built and which link generation and API descriptions use, and
/// the routing middleware's own composite, which it reads on the first request. A route group builds
/// its minimal API endpoints, request delegates included, again on every read, so without this
/// source every host start built the assistant's endpoints twice.
/// </remarks>
internal sealed class NhAssistantEndpointDataSource : EndpointDataSource, IDisposable
{
    private readonly EndpointDataSource _group;
    private readonly Lock _gate = new();
    private Snapshot? _snapshot;

    public NhAssistantEndpointDataSource(EndpointDataSource group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _group = group;
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
                    var changeToken = _group.GetChangeToken();
                    snapshot = new Snapshot(_group.Endpoints, changeToken);
                    Volatile.Write(ref _snapshot, snapshot);
                }

                return snapshot.Endpoints;
            }
        }
    }

    public override IChangeToken GetChangeToken()
    {
        return _group.GetChangeToken();
    }

    public override IReadOnlyList<Endpoint> GetGroupedEndpoints(RouteGroupContext context)
    {
        // Mapped into an application's own route group, the assistant is built with that group's
        // prefix and conventions, which the group passes on every read.
        return _group.GetGroupedEndpoints(context);
    }

    public void Dispose()
    {
        if (_group is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed record Snapshot(IReadOnlyList<Endpoint> Endpoints, IChangeToken ChangeToken);
}

/// <summary>
/// Collects the data sources of the assistant's route groups, so that
/// <see cref="AddTo(IEndpointRouteBuilder)"/> can add each of them to the application's endpoints
/// through an <see cref="NhAssistantEndpointDataSource"/>.
/// </summary>
internal sealed class NhAssistantEndpointGroups(IEndpointRouteBuilder endpoints) : IEndpointRouteBuilder
{
    private readonly List<EndpointDataSource> _dataSources = [];

    public ICollection<EndpointDataSource> DataSources => _dataSources;

    public IServiceProvider ServiceProvider => endpoints.ServiceProvider;

    public IApplicationBuilder CreateApplicationBuilder()
    {
        return endpoints.CreateApplicationBuilder();
    }

    public void AddTo(IEndpointRouteBuilder target)
    {
        foreach (var dataSource in _dataSources)
        {
            target.DataSources.Add(new NhAssistantEndpointDataSource(dataSource));
        }
    }
}
