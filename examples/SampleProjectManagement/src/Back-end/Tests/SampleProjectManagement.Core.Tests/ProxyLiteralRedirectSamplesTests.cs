using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>SPM-238: persist exact and regex redirects, restart storage, and serve them through the two-call host API.</summary>
public sealed class ProxyLiteralRedirectSamplesTests
{
    private const int DeadlineBudgetMilliseconds = 75;
    private static readonly TimeSpan RecoveryBound = TimeSpan.FromSeconds(30);

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
                    },
                    new NhProxyRedirectRule
                    {
                        Id = Guid.NewGuid(), Name = "Archived project pages with a capture that admits any path",
                        Match = new NhProxyRedirectMatch
                        {
                            PathMode = NhProxyRedirectPathMatchMode.Regex,
                            Path = @"^/archived-projects/([^?]+)(\?.*)?$"
                        },
                        // Each expansion is checked as the Location it resolves to, so captured dot
                        // segments can never turn this host-relative target into //other-host.
                        Target = "/projects/$1$2"
                    },
                    new NhProxyRedirectRule
                    {
                        Id = Guid.NewGuid(), Name = "Deliberately unsafe pattern demonstrating the resolver deadline",
                        Match = new NhProxyRedirectMatch
                        {
                            PathMode = NhProxyRedirectPathMatchMode.Regex,
                            Path = @"^/timeout/(a+)+$"
                        },
                        Target = "/projects"
                    }
                ]), cancellationToken);
                Assert.True(save.Success, string.Join("; ", save.AllErrorMessages));
            }

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            await using (var app = await StartHostAsync(databasePath, ProxySampleTestOptions.GenerousRedirectBudgetMilliseconds, cancellationToken))
            {
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

                using var archived = await client.GetAsync(app.Urls.Single() + "/archived-projects/42?tag=new", cancellationToken);
                Assert.Equal(HttpStatusCode.Found, archived.StatusCode);
                Assert.Equal("/projects/42?tag=new", archived.Headers.Location!.OriginalString);

                // Kestrel decodes %252e once, so the capture holds %2e%2e; /projects/%2e%2e//evil.example would
                // resolve to //evil.example, another origin. The expansion is skipped and the request passes through.
                using var networkPath = await client.GetAsync(
                    app.Urls.Single() + "/archived-projects/%252e%252e//evil.example", cancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, networkPath.StatusCode);
                Assert.Null(networkPath.Headers.Location);

                await app.StopAsync(cancellationToken);
            }

            // The deadline proof restarts on the same storage with a short budget, captured at startup.
            await using (var app = await StartHostAsync(databasePath, DeadlineBudgetMilliseconds, cancellationToken))
            {
                using var timedOut = await client.GetAsync(app.Urls.Single() + "/timeout/" + new string('a', 1000) + "!", cancellationToken);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
                Assert.Null(timedOut.Headers.Location);
                Assert.True(timedOut.Headers.CacheControl!.NoStore);

                using var afterTimeout = await WaitForAnswerOtherThanTimeoutAsync(client, app.Urls.Single() + "/timeout/aaa", cancellationToken);
                Assert.Equal(HttpStatusCode.Found, afterTimeout.StatusCode);
                Assert.Equal("/projects", afterTimeout.Headers.Location!.OriginalString);

                await app.StopAsync(cancellationToken);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task<WebApplication> StartHostAsync(string databasePath, int redirectBudgetMilliseconds, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeapProxy:Limits:RedirectResolutionTimeoutMilliseconds"] = redirectBudgetMilliseconds.ToString(),
            ["NewHeapProxy:Sqlite:DatabasePath"] = databasePath
        });
        builder.Services.AddNewHeapProxy(builder.Configuration.GetSection(NhProxyOptions.ConfigurationSectionName));
        var app = builder.Build();
        Assert.Equal(redirectBudgetMilliseconds, app.Services.GetRequiredService<IOptions<NhProxyOptions>>().Value.Limits.RedirectResolutionTimeoutMilliseconds);
        app.UseNewHeapProxy();
        await app.StartAsync(cancellationToken);

        return app;
    }

    /// <summary>
    /// A timeout ends one request, not the rule. The short budget also covers the request that proves this, and a
    /// descheduled request ends in 503 as well, so only a lasting 503 or a different answer fails the test.
    /// </summary>
    private static async Task<HttpResponseMessage> WaitForAnswerOtherThanTimeoutAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var response = await client.GetAsync(url, cancellationToken);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                return response;
            }

            response.Dispose();
            Assert.True(Stopwatch.GetElapsedTime(started) < RecoveryBound,
                $"{url} still answered 503 after {RecoveryBound.TotalSeconds:0} s: a redirect timeout must end only its own request.");
        }
    }
}
