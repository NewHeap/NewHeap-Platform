using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AspNet.Common.Middlewares;

public class TraceIdentifierMiddleware
{
    public const string CorrelationIdHeaderName = "X-Correlation-ID";
    public const string CorrelationIdScopeName = "correlation_id";
    public const int MaximumCorrelationIdLength = 128;
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

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            [CorrelationIdScopeName] = correlationId
        }))
        {
            await _next(context);
        }
    }

    private string GetCorrelationId(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeaderName].FirstOrDefault();

        if (IsValidCorrelationId(correlationId))
        {
            return correlationId!;
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            _logger.LogDebug("Ignored an invalid external correlation identifier.");
        }

        correlationId = Activity.Current?.TraceId.ToString();

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        return context.TraceIdentifier;
    }

    private static bool IsValidCorrelationId(string? correlationId) =>
        !string.IsNullOrWhiteSpace(correlationId)
        && correlationId.Length <= MaximumCorrelationIdLength
        && correlationId.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');
}

public static class TraceIdentifierMiddlewareExtensions
{
    public static IApplicationBuilder UseNewHeapTraceIdentifier(this IApplicationBuilder applicationBuilder)
    {
        return applicationBuilder.UseMiddleware<TraceIdentifierMiddleware>();
    }
}
