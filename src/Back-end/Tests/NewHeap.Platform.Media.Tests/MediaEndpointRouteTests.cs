using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Media.Tests;

public sealed class MediaEndpointRouteTests
{
    [Fact]
    public async Task RoutingAndTheApplicationWideEndpointsShareTheMediaEndpointsBuiltOnce()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(Substitute.For<IMediaLibraryService>());
        await using var app = builder.Build();
        app.MapNhMediaEndpoints("media", configureOptions: null);
        await app.StartAsync();

        // The authorization middleware's policy cache reads the application-wide endpoints while the
        // pipeline is built; the routing middleware reads a composite of its own on the first request.
        var applicationWide = app.Services.GetRequiredService<EndpointDataSource>();
        using var routing = new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources);

        Assert.Same(FindEndpoint(applicationWide, "List folder contents"), FindEndpoint(routing, "List folder contents"));
        Assert.Same(FindEndpoint(applicationWide, "Upload file"), FindEndpoint(routing, "Upload file"));
        await app.StopAsync();
    }

    private static Endpoint FindEndpoint(EndpointDataSource source, string name)
    {
        return source.Endpoints.Single(endpoint =>
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
    }
}
