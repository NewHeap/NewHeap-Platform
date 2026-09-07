using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Xunit;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite.Tests;

public sealed class NhProxySqliteContractTests
{
    [Fact]
    public async Task Registration_starts_an_empty_host_and_invokes_callbacks_once()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var optionCalls = 0;
        var yarpCalls = 0;
        var services = builder.Services.AddNewHeapProxy(options =>
        {
            optionCalls++;
            options.Administrator.UserName = "proxy-admin";
            options.ConfigureYarp(yarp =>
            {
                yarpCalls++;
                Assert.Same(builder.Services, yarp.Services);
                Assert.Contains(yarp.Services, descriptor => descriptor.ServiceType == typeof(IProxyConfigProvider));
                yarp.ConfigureHttpClient((_, handler) => handler.ConnectTimeout = TimeSpan.FromSeconds(3));
            });
        }, storage => storage.BusyTimeout = TimeSpan.FromSeconds(8));

        Assert.Same(builder.Services, services);
        await using var app = builder.Build();
        app.UseNewHeapProxy();
        app.MapGet("/health", () => "healthy");
        Assert.Equal("proxy-admin", app.Services.GetRequiredService<IOptions<NhProxyOptions>>().Value.Administrator.UserName);
        Assert.Equal(TimeSpan.FromSeconds(8), app.Services.GetRequiredService<IOptions<NhProxySqliteOptions>>().Value.BusyTimeout);
        Assert.NotNull(app.Services.GetRequiredService<IHttpForwarder>());
        var config = Assert.Single(app.Services.GetServices<IProxyConfigProvider>()).GetConfig();
        Assert.Empty(config.Routes);
        Assert.Empty(config.Clusters);

        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            Assert.Equal("healthy", await client.GetStringAsync(app.Urls.Single() + "/health"));
            using var response = await client.GetAsync(app.Urls.Single() + "/unconfigured");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }

        Assert.Equal(1, optionCalls);
        Assert.Equal(1, yarpCalls);
    }

    [Fact]
    public void Configuration_overload_binds_the_supplied_section_and_sqlite_child_at_registration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeapProxy:Administrator:UserName"] = "proxy-admin",
            ["NewHeapProxy:IpAllowlist:Enabled"] = "true",
            ["NewHeapProxy:IpAllowlist:Entries:0"] = "192.0.2.0/24",
            ["NewHeapProxy:Sqlite:DatabasePath"] = "App_Data/custom-proxy.db",
            ["NewHeapProxy:Sqlite:BusyTimeout"] = "00:00:09"
        }).Build();
        var services = new ServiceCollection();
        services.AddNewHeapProxy(configuration.GetSection(NhProxyOptions.ConfigurationSectionName));
        configuration["NewHeapProxy:Administrator:UserName"] = "changed-after-registration";

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NhProxyOptions>>().Value;
        var storage = provider.GetRequiredService<IOptions<NhProxySqliteOptions>>().Value;
        Assert.Equal("proxy-admin", options.Administrator.UserName);
        Assert.True(options.IpAllowlist.Enabled);
        Assert.Equal("192.0.2.0/24", Assert.Single(options.IpAllowlist.Entries));
        Assert.Equal("App_Data/custom-proxy.db", storage.DatabasePath);
        Assert.Equal(TimeSpan.FromSeconds(9), storage.BusyTimeout);
    }

    [Fact]
    public async Task Parameterless_registration_builds_with_defaults()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNewHeapProxy();
        await using var app = builder.Build();

        Assert.Null(app.Services.GetRequiredService<IOptions<NhProxyOptions>>().Value.YarpConfiguration);
        Assert.Equal("App_Data/newheap-proxy.db", app.Services.GetRequiredService<IOptions<NhProxySqliteOptions>>().Value.DatabasePath);
        Assert.Empty(app.Services.GetRequiredService<IProxyConfigProvider>().GetConfig().Routes);
    }

    [Fact]
    public async Task Storage_operations_fail_explicitly_without_claiming_persistence()
    {
        var options = Options.Create(new NhProxySqliteOptions());
        var store = new NhProxySqliteConfigurationStore(options);
        var audit = new NhProxySqliteLoginAuditStore(options);

        await Assert.ThrowsAsync<NotImplementedException>(() => store.InitializeAsync());
        await Assert.ThrowsAsync<NotImplementedException>(() => store.LoadRewritesAsync());
        await Assert.ThrowsAsync<NotImplementedException>(() => store.LoadRedirectsAsync());
        await Assert.ThrowsAsync<NotImplementedException>(() => store.SaveRewritesAsync(new NhProxyRewriteSaveRequest(0, [], [])));
        await Assert.ThrowsAsync<NotImplementedException>(() => store.SaveRedirectsAsync(new NhProxyRedirectSaveRequest(0, [])));
        await Assert.ThrowsAsync<NotImplementedException>(() => audit.AppendAsync(new NhProxyLoginAuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAtUtc = DateTimeOffset.UtcNow,
            ClientIp = "192.0.2.1",
            Outcome = NhProxyLoginOutcome.InvalidCredentials,
            CorrelationId = "contract-check"
        }));
        await Assert.ThrowsAsync<NotImplementedException>(() => audit.QueryAsync(new NhProxyLoginAuditQuery()));
        await Assert.ThrowsAsync<NotImplementedException>(() => audit.DeleteExpiredAsync(DateTimeOffset.UtcNow, 10));
        await Assert.ThrowsAsync<NotImplementedException>(() => store.DisposeAsync().AsTask());
    }
}
