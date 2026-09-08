using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>SPM-238: host-owned credentials and the preferred save-and-activate service.</summary>
public sealed class ProxyAdministrationSamplesTests
{
    [Fact]
    public async Task Consumer_enables_the_panel_and_activates_changes_without_restart()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-proxy-administration-sample-");
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                // Test credential only. Real hosts load a precomputed hash from their secret provider.
                options.Administrator.UserName = "administrator";
                options.Administrator.PasswordHash = new PasswordHasher<string>().HashPassword("administrator", "sample-test-only-password");
                options.IpAllowlist.Enabled = true;
                options.IpAllowlist.Entries = ["127.0.0.1/32", "::1/128"];
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.UseNewHeapProxy();
            await app.StartAsync(cancellationToken);
            try
            {
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
                Assert.Contains("Sign in", await client.GetStringAsync("/newheap-proxy/Login", cancellationToken));
                Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/newheap-proxy", cancellationToken)).StatusCode);

                var configuration = app.Services.GetRequiredService<INhProxyConfigurationService>();
                var snapshot = await configuration.GetRedirectsAsync(cancellationToken);
                var saved = await configuration.SaveRedirectsAsync(new(snapshot.Revision,
                [
                    new NhProxyRedirectRule
                    {
                        Id = Guid.NewGuid(), Name = "Moved project overview",
                        Match = new NhProxyRedirectMatch { Path = "/old-projects" }, Target = "/projects"
                    }
                ]), cancellationToken);
                Assert.True(saved.Success, string.Join("; ", saved.AllErrorMessages));
                Assert.Equal(NhProxyActivationState.Active, saved.Data!.Activation.State);
                var redirect = await client.GetAsync("/old-projects", cancellationToken);
                Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
                Assert.Equal("/projects", redirect.Headers.Location!.OriginalString);
                Assert.False((await configuration.SaveRedirectsAsync(new(snapshot.Revision, []), cancellationToken)).Success);
            }
            finally
            {
                await app.StopAsync(cancellationToken);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
