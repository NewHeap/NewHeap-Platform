using Microsoft.AspNetCore.Http;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Evaluates the active redirect snapshot before proxy and host endpoints, without accessing storage.</summary>
public sealed class NhProxyRedirectMiddleware(RequestDelegate next, NhProxyRuntime runtime)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (runtime.TryRedirect(context, out var failure))
        {
            return Task.CompletedTask;
        }

        if (failure == NhProxyRuntime.ResolutionTimeoutFailure)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }

        return next(context);
    }
}
