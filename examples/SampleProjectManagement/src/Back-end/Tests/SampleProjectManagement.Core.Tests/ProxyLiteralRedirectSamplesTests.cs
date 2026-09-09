using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>SPM-238: persist exact and regex redirects, restart storage, and serve them through the two-call host API.</summary>
public sealed class ProxyLiteralRedirectSamplesTests
{
    [Fact]
    public async Task Consumer_loads_a_persisted_literal_redirect_before_handling_requests()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-proxy-sample-");
        var databasePath = Path.Combine(directory.FullName, "proxy.db");
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            // Lower-level alternative: offline setup uses the public storage contract before the host starts.
            await using (var store = new NhProxySqliteConfigurationStore(Options.Create(new NhProxySqliteOptions { DatabasePath = databasePath })))
            {
                await store.InitializeAsync(cancellationToken);
                var configuration = await store.LoadRedirectsAsync(cancellationToken);
                var save = await store.SaveRedirectsAsync(new NhProxyRedirectSaveRequest(configuration.Revision,
                [
                    new NhProxyRedirectRule
                    {
                        Id = Guid.NewGuid(), Name = "Project landing page",
                        Match = new NhProxyRedirectMatch { Path = "/old-projects" },
                        Target = "/projects?source=proxy"
                    },
                    new NhProxyRedirectRule
                    {
                        Id = Guid.NewGuid(), Name = "Project details with explicit query captures",
                        Match = new NhProxyRedirectMatch
                        {
                            PathMode = NhProxyRedirectPathMatchMode.Regex,
                            Path = @"^/legacy-projects/(?<id>[0-9]+)(?<query>\?.*)?$"
                        },
                        Target = "/projects/${id}${query}"
                        // QueryMode is ignored for regex rules: only captured query values are carried over.
                    }
                ]), cancellationToken);
                Assert.True(save.Success, string.Join("; ", save.AllErrorMessages));
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(configureStorage: options => options.DatabasePath = databasePath);
            await using var app = builder.Build();
            app.UseNewHeapProxy();
            await app.StartAsync(cancellationToken);
            try
            {
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var response = await client.GetAsync(app.Urls.Single() + "/old-projects?campaign=sample", cancellationToken);
                Assert.Equal(HttpStatusCode.Found, response.StatusCode);
                Assert.Equal("/projects?campaign=sample&source=proxy", response.Headers.Location!.OriginalString);
                Assert.Equal(1, app.Services.GetRequiredService<INhProxyRuntime>().GetStatus().Redirect.ActiveRevision);

                using var regex = await client.GetAsync(app.Urls.Single() + "/legacy-projects/42?tag=a%26b&tag=c", cancellationToken);
                Assert.Equal(HttpStatusCode.Found, regex.StatusCode);
                Assert.Equal("/projects/42?tag=a%26b&tag=c", regex.Headers.Location!.OriginalString);
                var persisted = await app.Services.GetRequiredService<INhProxyConfigurationService>().GetRedirectsAsync(cancellationToken);
                Assert.Contains(persisted.Rules, rule => rule.Match.PathMode == NhProxyRedirectPathMatchMode.Regex);

                using var unmatched = await client.GetAsync(app.Urls.Single() + "/old-projects/child", cancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, unmatched.StatusCode);
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
