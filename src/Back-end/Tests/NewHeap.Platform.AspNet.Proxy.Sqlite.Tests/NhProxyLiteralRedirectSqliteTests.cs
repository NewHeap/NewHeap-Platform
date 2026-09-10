using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite.Tests;

public sealed class NhProxyLiteralRedirectSqliteTests
{
    private static NhProxyRedirectRule Rule(string target = "/new") => new()
    {
        Id = Guid.NewGuid(), Name = "Literal ' redirect", Match = new NhProxyRedirectMatch { Path = "/old" }, Target = target
    };

    private static NhProxySqliteConfigurationStore Store(string path) => new(Options.Create(new NhProxySqliteOptions { DatabasePath = path }));

    [Fact]
    public async Task Real_sqlite_creates_an_empty_store_persists_atomically_and_rejects_stale_writes()
    {
        var directory = Directory.CreateTempSubdirectory("nh-proxy-storage-");
        var path = Path.Combine(directory.FullName, "nested", "proxy.db");
        try
        {
            await using (var store = Store(path))
            {
                Assert.False(File.Exists(path));
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadRedirectsAsync());
                await store.InitializeAsync();
                Assert.True(File.Exists(path));
                Assert.Equal(0, (await store.LoadRedirectsAsync()).Revision);
                var saves = await Task.WhenAll(
                    store.SaveRedirectsAsync(new NhProxyRedirectSaveRequest(0, [Rule()])),
                    store.SaveRedirectsAsync(new NhProxyRedirectSaveRequest(0, [Rule("/alternative")])));
                Assert.Single(saves, result => result.Success);
                Assert.Equal(NhProxyErrorCodes.RevisionConflict, Assert.Single(saves.Single(result => !result.Success).GetResultItems()).Name);
                var snapshot = await store.LoadRedirectsAsync();
                Assert.Equal(1, snapshot.Revision);
                Assert.Equal("Literal ' redirect", Assert.Single(snapshot.Rules).Name);
                Assert.False((await store.SaveRedirectsAsync(new NhProxyRedirectSaveRequest(1,
                    [Rule("//unsafe.example")]))).Success);
                Assert.Equal(1, (await store.LoadRedirectsAsync()).Revision);

                await using var competing = Store(path);
                await Assert.ThrowsAsync<InvalidOperationException>(() => competing.InitializeAsync());
                await store.InitializeAsync();
                Assert.Equal(1, (await store.LoadRedirectsAsync()).Revision);
            }

            await using var reopened = Store(path);
            await reopened.InitializeAsync();
            Assert.Equal(1, (await reopened.LoadRedirectsAsync()).Revision);
            Assert.Single((await reopened.LoadRedirectsAsync()).Rules);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Host_loads_before_listening_redirects_before_endpoints_and_never_reads_storage_per_request()
    {
        var directory = Directory.CreateTempSubdirectory("nh-proxy-host-");
        var path = Path.Combine(directory.FullName, "App_Data", "proxy.db");
        try
        {
            await using (var seed = Store(path))
            {
                await seed.InitializeAsync();
                Assert.True((await seed.SaveRedirectsAsync(new NhProxyRedirectSaveRequest(0, [Rule("/new?source=target#part")]))).Success);
            }

            // The second host proves that persistence survives a complete host shutdown.
            for (var run = 0; run < 2; run++)
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory.FullName });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = true);
                builder.Services.AddNewHeapProxy(configureStorage: options => options.DatabasePath = "App_Data/proxy.db");
                await using var app = builder.Build();
                app.UseNewHeapProxy();
                app.MapGet("/old", () => "must not run");
                app.MapGet("/health", () => "healthy");
                await app.StartAsync();
                try
                {
                    Assert.Equal(1, app.Services.GetRequiredService<INhProxyRuntime>().GetStatus().Redirect.ActiveRevision);
                    // Closing the only storage connection makes a request-time database read fail.
                    await app.Services.GetRequiredService<INhProxyConfigurationStore>().DisposeAsync();
                    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                    using var redirect = await client.GetAsync(app.Urls.Single() + "/old?keep=1&source=incoming");
                    Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
                    Assert.Equal("/new?keep=1&source=target#part", redirect.Headers.Location!.OriginalString);
                    Assert.Equal(string.Empty, await redirect.Content.ReadAsStringAsync());
                    Assert.Equal("healthy", await client.GetStringAsync(app.Urls.Single() + "/health"));
                    using var noMatch = await client.GetAsync(app.Urls.Single() + "/old/child");
                    Assert.Equal(HttpStatusCode.NotFound, noMatch.StatusCode);
                }
                finally
                {
                    await app.StopAsync();
                }
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("UPDATE NhProxyConfiguration SET Document='{}';")]
    [InlineData("UPDATE NhProxyConfiguration SET FormatVersion=99;")]
    [InlineData("DELETE FROM NhProxyConfiguration;")]
    [InlineData("PRAGMA user_version=99;")]
    public async Task Invalid_existing_data_fails_startup_without_resetting_the_database(string damage)
    {
        var directory = Directory.CreateTempSubdirectory("nh-proxy-corrupt-");
        var path = Path.Combine(directory.FullName, "proxy.db");
        try
        {
            await using (var store = Store(path))
            {
                await store.InitializeAsync();
                Assert.True((await store.SaveRedirectsAsync(new NhProxyRedirectSaveRequest(0, [Rule()]))).Success);
            }

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = damage;
                command.ExecuteNonQuery();
            }

            var before = File.ReadAllBytes(path);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(configureStorage: options => options.DatabasePath = path);
            await using (var app = builder.Build())
            {
                app.UseNewHeapProxy();
                await Assert.ThrowsAsync<InvalidDataException>(() => app.StartAsync());
                Assert.Equal(NhProxyActivationState.NotInitialized, app.Services.GetRequiredService<INhProxyRuntime>().GetStatus().Redirect.State);
            }

            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Storage_rejects_webroot_and_cancellation_does_not_create_a_database()
    {
        var directory = Directory.CreateTempSubdirectory("nh-proxy-location-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "wwwroot"));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory.FullName, WebRootPath = "wwwroot" });
            var options = Options.Create(new NhProxySqliteOptions { DatabasePath = "wwwroot/proxy.db" });
            Assert.Throws<ArgumentException>(() => new NhProxySqliteConfigurationStore(options, builder.Environment));
            var path = Path.Combine(directory.FullName, "cancelled.db");
            await using var store = Store(path);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InitializeAsync(new CancellationToken(true)));
            Assert.False(File.Exists(path));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
