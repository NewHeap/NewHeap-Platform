using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace SampleProjectManagement.Core.Tests;

/// <summary>SPM-238 proves startup registration and contracts; SPM-239 tracks the remaining proxy runtime.</summary>
public sealed class ProxyContractBoundarySamplesTests
{
    [Fact]
    public async Task Consumer_can_start_a_host_and_configure_yarp_through_proxy_options()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"newheap-proxy-sample-{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var callbackCalls = 0;
        builder.Services.AddNewHeapProxy(options =>
        {
            options.Administrator.UserName = "proxy-admin";
            options.ConfigureYarp(yarp =>
            {
                yarp.ConfigureHttpClient((_, handler) => handler.ConnectTimeout = TimeSpan.FromSeconds(5));
                callbackCalls++;
            });
        }, storage => storage.DatabasePath = databasePath);

        await using var app = builder.Build();
        app.UseNewHeapProxy();
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, callbackCalls);
            Assert.Equal("proxy-admin", app.Services.GetRequiredService<IOptions<NhProxyOptions>>().Value.Administrator.UserName);
            var configuration = app.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();
            Assert.Empty(configuration.Routes);
            Assert.Empty(configuration.Clusters);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
            File.Delete(databasePath);
            File.Delete(databasePath + ".lock");
        }
    }

    [Fact]
    public async Task Consumer_can_describe_both_rule_types_and_map_the_empty_proxy()
    {
        var cluster = new NhProxyCluster
        {
            Id = Guid.NewGuid(),
            Name = "Project API",
            Destination = new NhProxyDestination("project-api", new Uri("https://backend.example/"))
        };
        var rewrite = new NhProxyRewriteRule
        {
            Id = Guid.NewGuid(),
            Name = "Project API route",
            Match = new NhProxyRewriteMatch { Path = "/api/{**remainder}" },
            ClusterId = cluster.Id,
            Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.RemovePrefix, "/api")]
        };
        var redirect = new NhProxyRedirectRule
        {
            Id = Guid.NewGuid(),
            Name = "Project landing page",
            Match = new NhProxyRedirectMatch { Path = "/old-projects" },
            Target = "/projects"
        };
        var rewriteSave = new NhProxyRewriteSaveRequest(0, [rewrite], [cluster]);
        var redirectSave = new NhProxyRedirectSaveRequest(0, [redirect]);
        var preview = new NhProxyRedirectTestRequest(
            new NhProxyRevisions(0, 0), redirect,
            new NhProxyTestRequest { Url = new Uri("https://proxy.example/old-projects") });

        Assert.Equal(cluster.Id, Assert.Single(rewriteSave.Rules).ClusterId);
        Assert.Equal(NhProxyRedirectStatus.Found, Assert.Single(redirectSave.Rules).Status);
        Assert.Equal(redirect.Id, preview.Draft.Id);

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNewHeapProxy(
            builder.Configuration.GetSection(NhProxyOptions.ConfigurationSectionName));

        await using var app = builder.Build();
        Assert.Same(app, app.UseNewHeapProxy());
    }
}
