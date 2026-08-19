using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sentry;

namespace NewHeap.Platform.AspNet.Common.Middlewares;

public class TraceIdentifierMiddleware
{
    public const string CorrelationIdHeaderName = "X-Correlation-ID";
    private const string SentryTagName = "correlation_id";
    private readonly ILogger<TraceIdentifierMiddleware> _logger;
    private readonly RequestDelegate _next;

    public TraceIdentifierMiddleware(
        RequestDelegate next,
        ILogger<TraceIdentifierMiddleware> logger
    )
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetCorrelationId(context);
        context.TraceIdentifier = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationIdHeaderName] = correlationId;
            return Task.CompletedTask;
        });

        SentrySdk.ConfigureScope(scope => scope.SetTag(SentryTagName, correlationId));

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            [SentryTagName] = correlationId,
            ["CorrelationId"] = correlationId
        }))
        {
            await _next(context);
        }
    }

    private static string GetCorrelationId(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeaderName].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        correlationId = Activity.Current?.TraceId.ToString();

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        return context.TraceIdentifier;
    }
}

public static class TraceIdentifierMiddlewareExtensions
{
    public static IApplicationBuilder UseNewHeapTraceIdentifier(this IApplicationBuilder applicationBuilder)
    {
        return applicationBuilder.UseMiddleware<TraceIdentifierMiddleware>();
    }
}
