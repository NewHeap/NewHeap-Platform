using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net;

namespace NewHeap.Platform.AspNet.Common.Services;

public partial class ExceptionHandlerService
{
    private readonly ILogger<ExceptionHandlerService> _logger;

    public ExceptionHandlerService(
        ILogger<ExceptionHandlerService> logger
    )
    {
        _logger = logger;
    }

    public async Task HandleExceptionAsync(HttpContext context, Exception? exception)
    {
        _logger.LogError(exception, "An error occurred.");

        var response = new { message = "An internal server error occurred." };
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(response);
    }
}