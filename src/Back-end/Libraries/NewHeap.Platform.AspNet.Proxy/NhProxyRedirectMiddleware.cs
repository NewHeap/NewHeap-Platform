using Microsoft.AspNetCore.Http;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Evaluates the active redirect snapshot before proxy and host endpoints, without accessing storage.</summary>
public sealed class NhProxyRedirectMiddleware(RequestDelegate next, NhProxyRuntime runtime)
{
    public Task InvokeAsync(HttpContext context)
    {
        return runtime.TryRedirect(context) ? Task.CompletedTask : next(context);
    }
}
