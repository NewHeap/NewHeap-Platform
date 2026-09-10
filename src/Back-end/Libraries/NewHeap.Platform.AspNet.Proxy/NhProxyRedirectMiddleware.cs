using Microsoft.AspNetCore.Http;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Evaluates the active redirect snapshot before proxy and host endpoints, without accessing storage.</summary>
public sealed class NhProxyRedirectMiddleware(RequestDelegate next, NhProxyRuntime runtime)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var chainFailure = await runtime.CheckChainAsync(context);
        if (chainFailure == NhProxyRuntime.ChainDepthFailure)
        {
            context.Response.StatusCode = StatusCodes.Status508LoopDetected;
            context.Response.Headers.CacheControl = "no-store";
            return;
        }

        if (chainFailure == NhProxyRuntime.ResolutionTimeoutFailure)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            return;
        }

        if (runtime.TryRedirect(context, out var failure))
        {
            return;
        }

        if (failure == NhProxyRuntime.ResolutionTimeoutFailure)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            return;
        }

        await next(context);
    }
}
