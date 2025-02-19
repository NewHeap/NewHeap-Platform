using Microsoft.AspNetCore.Http;

namespace NewHeap.Platform.AspNet.Common.Middlewares;

public partial class ResponseHeaderDisallowNoFollowMiddleware
{
    private const string ROBOTS_KEY = "X-Robots-Tag";
    private readonly RequestDelegate _next;

    public ResponseHeaderDisallowNoFollowMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Response != null)
        {
            if (!context.Response.Headers.ContainsKey(ROBOTS_KEY))
            {
                context.Response.Headers[ROBOTS_KEY] = "noindex, nofollow";
            }
        }

        await _next(context);
    }
}