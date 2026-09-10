using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Xunit;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite.Tests;

public sealed class NhProxySqliteContractTests
{
    [Fact]
    public void Password_configuration_is_bound_hashed_once_and_cleared_from_options()
    {
        const string password = " test-only-password with spaces ";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeapProxy:Administrator:UserName"] = "proxy-admin",
            ["NewHeapProxy:Administrator:Password"] = password
        }).Build();
        var services = new ServiceCollection();
        services.AddNewHeapProxy(configuration.GetSection("NewHeapProxy"));
        using var provider = services.BuildServiceProvider();
        var administrator = provider.GetRequiredService<IOptions<NhProxyOptions>>().Value.Administrator;
        Assert.DoesNotContain(password, System.Text.Json.JsonSerializer.Serialize(administrator));

        var administration = provider.GetRequiredService<INhProxyAdministrationService>();
        Assert.Empty(administrator.Password);
        var hash = administrator.PasswordHash;
        var hasher = new PasswordHasher<string>();
        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(administrator.UserName, hash, password));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(administrator.UserName, hash, password.Trim()));
        Assert.Same(administration, provider.GetRequiredService<INhProxyAdministrationService>());
        Assert.Equal(hash, administrator.PasswordHash);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("whitespace")]
    [InlineData("too-long")]
    public void Invalid_password_configuration_is_rejected_without_disclosing_credentials(string scenario)
    {
        var services = new ServiceCollection();
        services.AddNewHeapProxy(options =>
        {
            options.Administrator.UserName = "proxy-admin";
            options.Administrator.Password = scenario switch
            {
                "whitespace" => "   ",
                "too-long" => new string('x', 1025),
                _ => "test-only-password"
            };
            options.Administrator.PasswordHash = scenario == "both" ? "test-only-hash" : "";
        });
        using var provider = services.BuildServiceProvider();
        var failure = Assert.Throws<ArgumentException>(() => provider.GetRequiredService<INhProxyAdministrationService>());
        Assert.DoesNotContain("test-only-password", failure.Message);
        Assert.DoesNotContain("test-only-hash", failure.Message);
    }

    [Fact]
    public async Task Registration_starts_an_empty_host_and_invokes_callbacks_once()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"newheap-proxy-{Guid.NewGuid():N}.db");
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
        }, storage =>
        {
            storage.BusyTimeout = TimeSpan.FromSeconds(8);
            storage.DatabasePath = databasePath;
        });

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
        await app.DisposeAsync();
        File.Delete(databasePath);
        File.Delete(databasePath + ".lock");
    }

    [Fact]
    public void Configuration_overload_binds_the_supplied_section_and_sqlite_child_at_registration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeapProxy:Administrator:UserName"] = "proxy-admin",
            ["NewHeapProxy:Limits:RedirectResolutionTimeoutMilliseconds"] = "125",
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
        Assert.Equal(125, options.Limits.RedirectResolutionTimeoutMilliseconds);
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
        Assert.Equal(50, app.Services.GetRequiredService<IOptions<NhProxyOptions>>().Value.Limits.RedirectResolutionTimeoutMilliseconds);
        Assert.Equal("App_Data/newheap-proxy.db", app.Services.GetRequiredService<IOptions<NhProxySqliteOptions>>().Value.DatabasePath);
        Assert.Empty(app.Services.GetRequiredService<IProxyConfigProvider>().GetConfig().Routes);
    }

    [Fact]
    public async Task Rewrite_storage_requires_initialization()
    {
        await using var store = new NhProxySqliteConfigurationStore(Options.Create(new NhProxySqliteOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadRewritesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveRewritesAsync(new NhProxyRewriteSaveRequest(0, [], [])));
    }
}
