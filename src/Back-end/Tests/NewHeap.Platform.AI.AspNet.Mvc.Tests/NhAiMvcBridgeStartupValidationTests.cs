using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// When the startup checks read the bridge catalog: a web host checks it once its application
/// pipeline is configured, so ApiExplorer sees every endpoint; a host without a web pipeline checks
/// it while its hosted services start.
/// </summary>
public sealed class NhAiMvcBridgeStartupValidationTests
{
    private const string Published = "AI API bridge 'test-api' published";

    [Fact]
    public async Task Api_descriptions_keep_the_minimal_api_endpoints_of_a_web_application_with_the_bridge()
    {
        var logs = new CapturedLogs();
        await using var app = CreateWebApplication(BridgeApiFactory.DefaultBridge, logs);

        await app.StartAsync();

        var descriptions = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .ToArray();
        Assert.Contains(descriptions, description => description.RelativePath == "minimal-probe");
        Assert.Contains(descriptions, description => description.RelativePath == "orders");
        Assert.Single(logs.Entries, entry => entry.Contains(Published, StringComparison.Ordinal));
    }

    [Fact]
    public void A_host_that_starts_its_server_before_other_hosted_services_checks_the_catalog_once()
    {
        using var factory = new BridgeApiFactory();

        Assert.NotEmpty(factory.Services.GetRequiredService<NhAiMvcBridgeToolCatalog>().Descriptors);
        Assert.Single(factory.Logs.Entries, entry => entry.Contains(Published, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_bridge_catalog_fails_the_start_of_a_web_application()
    {
        await using var app = CreateWebApplication(CollisionBridge, new CapturedLogs());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.StartAsync());

        AssertCollision(exception);
    }

    [Fact]
    public async Task An_invalid_bridge_catalog_fails_the_start_of_a_host_without_a_web_pipeline()
    {
        // Production: the MVC services that need a web host environment are never resolved here.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        BridgeApiFactory.AddTestApi(builder.Services, CollisionBridge);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync());

        AssertCollision(exception);
    }

    /// <summary>
    /// A web application in the minimal hosting model: its hosted services start before the
    /// server, and its endpoints join the application-wide endpoints while the pipeline is built.
    /// </summary>
    private static WebApplication CreateWebApplication(Action<NhAiMvcBridgeBuilder> bridge, CapturedLogs logs)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        BridgeApiFactory.AddTestApi(builder.Services, bridge);
        builder.Services.AddEndpointsApiExplorer();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/minimal-probe", () => "ok");
        return app;
    }

    private static void CollisionBridge(NhAiMvcBridgeBuilder bridge)
    {
        bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Collision");
    }

    private static void AssertCollision(InvalidOperationException exception)
    {
        Assert.Contains("test-api.collision.get-by-id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GET /collision/first/{id}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GET /collision/second/{id}", exception.Message, StringComparison.Ordinal);
    }
}
