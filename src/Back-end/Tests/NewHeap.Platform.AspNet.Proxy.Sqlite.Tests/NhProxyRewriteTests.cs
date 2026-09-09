using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite.Tests;

public sealed class NhProxyRewriteTests
{
    private static NhProxyCluster Cluster(Uri address) => new()
    {
        Id = Guid.NewGuid(), Name = "Backend", Destination = new("backend", address)
    };

    private static NhProxyRewriteRule Rule(NhProxyCluster cluster) => new()
    {
        Id = Guid.NewGuid(), Name = "Project API", ClusterId = cluster.Id,
        Match = new() { Path = "/public/{id:int}/{**rest}", Methods = ["GET"] },
        Transforms =
        [
            new NhProxyPathTransform(NhProxyPathTransformKind.Pattern, "/projects/{id}/{**rest}"),
            new NhProxyQueryTransform("id", NhProxyValueOperation.Set, "id", NhProxyValueSource.RouteValue),
            new NhProxyQueryTransform("source", NhProxyValueOperation.Append, "proxy"),
            new NhProxyHeaderTransform(NhProxyHeaderDirection.Request, "X-Proxy", NhProxyValueOperation.Set, "managed"),
            new NhProxyHeaderTransform(NhProxyHeaderDirection.Response, "X-Response", NhProxyValueOperation.Set, "managed")
        ]
    };

    [Fact]
    public async Task Rejected_yarp_reload_retains_last_active_revision_and_does_not_block_redirect_publication()
    {
        var directory = Directory.CreateTempSubdirectory("nh-rewrite-activation-");
        try
        {
            var filter = new RejectingFilter();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IProxyConfigFilter>(filter);
            builder.Services.AddNewHeapProxy(configureStorage: storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.UseNewHeapProxy();
            await app.StartAsync();
            var configuration = app.Services.GetRequiredService<INhProxyConfigurationService>();
            var cluster = Cluster(new("https://backend.example/"));
            var rule = Rule(cluster);
            filter.Reject = true;
            var saved = await configuration.SaveRewritesAsync(new(0, [rule], [cluster]));
            Assert.False(saved.Success);
            Assert.Equal(1, saved.Data!.SavedRevision);
            Assert.Equal(0, configuration.GetStatus().Rewrite.ActiveRevision);
            Assert.Equal(NhProxyActivationState.Failed, configuration.GetStatus().Rewrite.State);
            Assert.Equal(1, (await configuration.GetRewritesAsync()).Revision);
            Assert.True((await configuration.SaveRedirectsAsync(new(0, []))).Success);
            filter.Reject = false;
            Assert.True((await configuration.RetryActivationAsync(NhProxyEngine.Rewrite)).Success);
            Assert.Equal(1, configuration.GetStatus().Rewrite.ActiveRevision);
            Assert.Equal(1, configuration.GetStatus().Redirect.ActiveRevision);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class RejectingFilter : IProxyConfigFilter
    {
        public bool Reject { get; set; }
        public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancellationToken) => ValueTask.FromResult(cluster);
        public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancellationToken)
        {
            if (Reject)
            {
                throw new InvalidOperationException("Deliberate host filter rejection.");
            }

            return ValueTask.FromResult(route);
        }
    }

    [Fact]
    public async Task Stored_rewrites_forward_with_native_transforms_and_preview_without_outbound_requests_or_publication()
    {
        var directory = Directory.CreateTempSubdirectory("nh-rewrite-");
        try
        {
            var requests = 0;
            var backendBuilder = WebApplication.CreateBuilder();
            backendBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var backend = backendBuilder.Build();
            backend.Run(async context =>
            {
                Interlocked.Increment(ref requests);
                await context.Response.WriteAsync(context.Request.Path + context.Request.QueryString + "|" + context.Request.Headers["X-Proxy"]);
            });
            await backend.StartAsync();
            var cluster = Cluster(new Uri(backend.Urls.Single() + "/base/"));
            var rule = Rule(cluster);
            var path = Path.Combine(directory.FullName, "proxy.db");
            await using (var store = new NhProxySqliteConfigurationStore(Options.Create(new NhProxySqliteOptions { DatabasePath = path })))
            {
                await store.InitializeAsync();
                Assert.True((await store.SaveRewritesAsync(new(0, [rule], [cluster]))).Success);
                Assert.False((await store.SaveRewritesAsync(new(0, [], []))).Success);
            }

            for (var run = 0; run < 2; run++)
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddNewHeapProxy(configureStorage: storage => storage.DatabasePath = path);
                await using var proxy = builder.Build();
                proxy.UseNewHeapProxy();
                await proxy.StartAsync();
                var configuration = proxy.Services.GetRequiredService<INhProxyConfigurationService>();
                Assert.Equal(1, configuration.GetStatus().Rewrite.ActiveRevision);
                Assert.Equal(0, configuration.GetStatus().Redirect.ActiveRevision);
                var tester = proxy.Services.GetRequiredService<INhProxyDraftTester>();
                var provider = proxy.Services.GetRequiredService<IProxyConfigProvider>();
                var live = provider.GetConfig();
                var before = requests;
                var preview = await tester.TestRewriteAsync(new(new(1, 0), rule,
                    new() { Url = new("https://public.example/public/42/a%20b?source=user&keep=1&keep=2") }));
                Assert.True(preview.Success, string.Join(", ", preview.GetResultItems().Select(item => item.Name)));
                Assert.Equal(NhProxyTestOutcome.Rewrite, preview.Data!.Outcome);
                Assert.Equal("42", preview.Data.RouteValues["id"]);
                Assert.Equal(before, requests);
                Assert.Same(live, provider.GetConfig());
                Assert.Equal(1, (await configuration.GetRewritesAsync()).Revision);
                Assert.Single(preview.Data.Rewrite!.UnevaluatedResponseHeaderTransforms);

                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var response = await client.GetAsync(proxy.Urls.Single() + "/public/42/a%20b?source=user&keep=1&keep=2");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("managed", response.Headers.GetValues("X-Response").Single());
                Assert.Equal(PathString.FromUriComponent(preview.Data.Rewrite.TargetUrl) + preview.Data.Rewrite.TargetUrl.Query + "|managed",
                    await response.Content.ReadAsStringAsync());

                var noMatch = await tester.TestRewriteAsync(new(new(1, 0), rule, new() { Url = new("https://public.example/public/not-an-int/a") }));
                Assert.True(noMatch.Success);
                Assert.Equal(NhProxyTestOutcome.NoMatch, noMatch.Data!.Outcome);
                Assert.False((await tester.TestRewriteAsync(new(new(0, 0), rule, new() { Url = new("https://public.example/public/42/a") }))).Success);
                var disabled = await tester.TestRewriteAsync(new(new(1, 0), rule with { Enabled = false }, new() { Url = new("https://public.example/public/42/a") }));
                Assert.Equal(NhProxyTestOutcome.NoMatch, disabled.Data!.Outcome);
                var simulated = await tester.TestRewriteAsync(new(new(1, 0), rule with { Enabled = false }, new() { Url = new("https://public.example/public/42/a") }) { SimulateEnabled = true });
                Assert.Equal(NhProxyTestOutcome.Rewrite, simulated.Data!.Outcome);
                var ambiguous = await tester.TestRewriteAsync(new(new(1, 0), rule with
                {
                    Id = Guid.NewGuid(), Match = rule.Match with { Path = "/public/{number:int}/{**rest}" }
                }, new() { Url = new("https://public.example/public/42/a") }));
                Assert.False(ambiguous.Success);
                await proxy.StopAsync();
            }

            await backend.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Header_query_host_and_method_matching_redirect_precedence_and_independent_hot_reload()
    {
        var directory = Directory.CreateTempSubdirectory("nh-rewrite-match-");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(configureStorage: storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var proxy = builder.Build();
            proxy.UseNewHeapProxy();
            await proxy.StartAsync();
            var configuration = proxy.Services.GetRequiredService<INhProxyConfigurationService>();
            var tester = proxy.Services.GetRequiredService<INhProxyDraftTester>();
            var cluster = Cluster(new("https://backend.example/"));
            var rule = Rule(cluster) with
            {
                Match = new()
                {
                    Path = "/public/{id:int}/{**rest}", Hosts = ["*.example:8443"], Methods = ["PUT"],
                    Headers = [new("X-Mode", NhProxyHeaderMatchMode.Exact, ["test"])],
                    QueryParameters = [new("preview", NhProxyQueryMatchMode.Exact, ["yes"])]
                }
            };
            var saved = await configuration.SaveRewritesAsync(new(0, [rule], [cluster]));
            Assert.True(saved.Success);
            Assert.Equal(NhProxyActivationState.Active, saved.Data!.Activation.State);
            var input = new NhProxyTestRequest
            {
                Url = new("https://public.example:8443/public/42/a?preview=yes"), Method = "PUT",
                Headers = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("X-Mode", ["test"])
            };
            Assert.Equal(NhProxyTestOutcome.Rewrite, (await tester.TestRewriteAsync(new(new(1, 0), rule, input))).Data!.Outcome);
            foreach (var invalid in new[] { input with { Method = "GET" }, input with { Headers = input.Headers.Clear() },
                input with { Url = new("https://public.example/public/42/a?preview=yes") }, input with { Url = new("https://public.example:8443/public/42/a") } })
            {
                Assert.Equal(NhProxyTestOutcome.NoMatch, (await tester.TestRewriteAsync(new(new(1, 0), rule, invalid))).Data!.Outcome);
            }

            var redirect = new NhProxyRedirectRule { Id = Guid.NewGuid(), Name = "Moved", Match = new() { Path = "/public/42/a" }, Target = "/moved" };
            var live = proxy.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();
            Assert.True((await configuration.SaveRedirectsAsync(new(0, [redirect]))).Success);
            Assert.Same(live, proxy.Services.GetRequiredService<IProxyConfigProvider>().GetConfig());
            var shadowed = await tester.TestRewriteAsync(new(new(1, 1), rule, input));
            Assert.True(shadowed.Success);
            Assert.Equal(NhProxyTestOutcome.Redirect, shadowed.Data!.Outcome);
            Assert.Equal(redirect.Id, shadowed.Data.SelectedRuleId);
            var reserved = await tester.TestRewriteAsync(new(new(1, 1), rule, input with { Url = new("https://public.example/newheap-proxy/Edit") }));
            Assert.Equal(NhProxyTestOutcome.ReservedAdministrationPath, reserved.Data!.Outcome);
            Assert.True((await configuration.SaveRewritesAsync(new(1, [], []))).Success);
            Assert.Equal(2, configuration.GetStatus().Rewrite.ActiveRevision);
            Assert.Equal(1, configuration.GetStatus().Redirect.ActiveRevision);
            var first = rule with { Match = new() { Path = "/shadow/{id}" } };
            var second = first with { Id = Guid.NewGuid(), Match = new() { Path = "/shadow/{name}" } };
            Assert.True((await configuration.SaveRewritesAsync(new(2, [first, second], [cluster]))).Success);
            Assert.True((await configuration.SaveRedirectsAsync(new(1, [redirect with { Match = new() { Path = "/shadow/42" } }]))).Success);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var response = await client.GetAsync(proxy.Urls.Single() + "/shadow/42");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal("/moved", response.Headers.Location!.OriginalString);
            await proxy.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Invalid_rules_and_unsafe_destinations_or_headers_are_rejected_before_persistence()
    {
        var validator = new NhProxyConfigurationValidator(Options.Create(new NhProxyOptions { AllowedDestinationHosts = ["backend.example"] }));
        var cluster = Cluster(new("https://backend.example/"));
        var rule = Rule(cluster);
        Assert.True((await validator.ValidateRewritesAsync(new(0, [rule], [cluster]))).Success);
        foreach (var invalid in new[]
        {
            rule with { ClusterId = Guid.NewGuid() }, rule with { Match = new() { Path = "/{broken" } },
            rule with { Match = new() { Path = "/value/{id:not-supported}" } },
            rule with { Match = new() { Path = "/newheap-proxy/Edit" } },
            rule with { Transforms = [new NhProxyHeaderTransform(NhProxyHeaderDirection.Request, "Authorization", NhProxyValueOperation.Set, "secret")] },
            rule with { Transforms = [new NhProxyHeaderTransform(NhProxyHeaderDirection.Response, "X-Test", NhProxyValueOperation.Set, "injected\r\nheader")] },
            rule with { AuthorizationPolicy = "missing-policy" }, rule with { RequestTimeout = TimeSpan.Zero }
        })
        {
            Assert.False((await validator.ValidateRewritesAsync(new(0, [invalid], [cluster]))).Success);
        }

        Assert.False((await validator.ValidateRewritesAsync(new(0, [rule, rule], [cluster]))).Success);
        Assert.False((await validator.ValidateRewritesAsync(new(0, [rule, rule with { Id = Guid.NewGuid() }], [cluster]))).Success);
        Assert.False((await validator.ValidateRewritesAsync(new(0, [rule], [cluster with { Destination = new("backend", new("https://other.example/")) }]))).Success);
    }
}
