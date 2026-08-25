using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AspNet.Common.Middlewares;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class TraceIdentifierMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AcceptsAValidExternalCorrelationIdentifier()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TraceIdentifierMiddleware.CorrelationIdHeaderName] = "order-42:retry_1";
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(context);

        context.TraceIdentifier.Should().Be("order-42:retry_1");
    }

    [Theory]
    [InlineData("contains a space")]
    [InlineData("contains/slash")]
    public async Task InvokeAsync_RejectsAnInvalidExternalCorrelationIdentifier(string invalidIdentifier)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "server-generated-id";
        context.Request.Headers[TraceIdentifierMiddleware.CorrelationIdHeaderName] = invalidIdentifier;
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(context);

        context.TraceIdentifier.Should().Be("server-generated-id");
    }

    [Fact]
    public async Task InvokeAsync_RejectsAnOversizedExternalCorrelationIdentifier()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "server-generated-id";
        context.Request.Headers[TraceIdentifierMiddleware.CorrelationIdHeaderName] =
            new string('a', TraceIdentifierMiddleware.MaximumCorrelationIdLength + 1);
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(context);

        context.TraceIdentifier.Should().Be("server-generated-id");
    }

    private static TraceIdentifierMiddleware CreateMiddleware() =>
        new(
            _ => Task.CompletedTask,
            NullLogger<TraceIdentifierMiddleware>.Instance);
}
