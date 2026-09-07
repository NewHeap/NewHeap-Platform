using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Pipeline entry points for native YARP. Managed redirects and administration are not implemented yet.</summary>
public static class NhProxyApplicationExtensions
{
    /// <summary>Maps and initializes native YARP for a WebApplication. Managed redirect middleware is not implemented yet.</summary>
    public static WebApplication UseNewHeapProxy(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapNewHeapProxy();

        // ponytail: no middleware is needed yet; install redirect evaluation here when rule processing is implemented.
        return app;
    }

    /// <summary>Maps YARP endpoints and loads the initial proxy configuration. Administration endpoints are not installed yet.</summary>
    public static IEndpointRouteBuilder MapNewHeapProxy(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapReverseProxy();

        return endpoints;
    }
}
