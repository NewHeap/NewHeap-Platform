using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace NewHeap.Platform.AspNet.Proxy.Tests;

public sealed class NhProxyChainPreflightTests
{
    [Theory]
    [InlineData("http://proxy.example", "http://backend.example/", true)]
    [InlineData("http://proxy.example", "https://proxy.example/", true)]
    [InlineData("http://proxy.example:8080", "http://proxy.example:8081/", true)]
    [InlineData("http://proxy.example", "http://proxy.example/", false)]
    [InlineData("http://PROXY.example:80", "http://proxy.example/", false)]
    [InlineData("https://proxy.example", "https://PROXY.example:443/", false)]
    [InlineData("http://[::1]:8080", "http://[::1]:8080/", false)]
    [InlineData("http://[::1]:8080", "http://[::1]:8081/", true)]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example/", false)]
    public async Task Only_external_destinations_skip_request_copying(string origin, string destination, bool skips)
    {
        await using var app = CreateApplication();
        var runtime = app.Services.GetRequiredService<NhProxyRuntime>();
        Assert.True((await runtime.PublishRedirectsAsync(new())).Success);
        Assert.True((await runtime.PublishRewritesAsync(Configuration(destination, 1))).Success);
        var (context, headers) = Request(app, origin);

        var failure = await runtime.CheckChainAsync(context);

        Assert.Equal(skips ? null : NhProxyRuntime.ChainDepthFailure, failure);
        Assert.Equal(skips ? 0 : 1, headers.Enumerations);
    }

    [Fact]
    public async Task Active_redirects_keep_chain_checks_and_empty_snapshots_remain_pinned()
    {
        await using var app = CreateApplication();
        var runtime = app.Services.GetRequiredService<NhProxyRuntime>();
        Assert.True((await runtime.PublishRedirectsAsync(new())).Success);
        Assert.True((await runtime.PublishRewritesAsync(Configuration("https://backend.example/", 1))).Success);
        var (before, beforeHeaders) = Request(app, "http://proxy.example");
        Assert.Null(await runtime.CheckChainAsync(before));
        Assert.Equal(0, beforeHeaders.Enumerations);

        NhProxyRedirectRule Redirect(string path, string target) => new()
        {
            Id = Guid.NewGuid(), Name = path, Match = new() { Path = path }, Target = target
        };
        Assert.True((await runtime.PublishRedirectsAsync(new()
        {
            Revision = 1, Rules = [Redirect("/loop", "/again"), Redirect("/again", "/loop")]
        })).Success);

        // A request that passed the shortcut must still execute its captured empty snapshot.
        Assert.False(runtime.TryRedirect(before, out var failure));
        Assert.Null(failure);
        var (after, afterHeaders) = Request(app, "http://proxy.example");
        Assert.Equal(NhProxyRuntime.ChainDepthFailure, await runtime.CheckChainAsync(after));
        Assert.Equal(1, afterHeaders.Enumerations);
    }

    [Fact]
    public async Task Reload_disables_shortcut_until_filtered_configuration_is_applied()
    {
        var filter = new ReloadFilter();
        await using var app = CreateApplication(filter);
        var runtime = app.Services.GetRequiredService<NhProxyRuntime>();
        var rewrites = app.Services.GetRequiredService<NhProxyRewriteRuntime>();
        var origin = new Uri("http://proxy.example");
        Assert.True((await runtime.PublishRedirectsAsync(new())).Success);
        var configuration = Configuration("https://backend.example/", 1);
        Assert.True((await runtime.PublishRewritesAsync(configuration)).Success);
        Assert.True(rewrites.HasOnlyExternalDestinations(origin));

        filter.Destination = origin.AbsoluteUri;
        filter.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        filter.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = runtime.PublishRewritesAsync(configuration with { Revision = 2 });
        try
        {
            await filter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(rewrites.HasOnlyExternalDestinations(origin));
            var (during, headers) = Request(app, origin.AbsoluteUri);
            Assert.Null(await runtime.CheckChainAsync(during));
            Assert.Equal(1, headers.Enumerations);
        }
        finally
        {
            filter.Release.TrySetResult();
        }

        Assert.True((await publication).Success);
        Assert.False(rewrites.HasOnlyExternalDestinations(origin));
        var (local, _) = Request(app, origin.AbsoluteUri);
        Assert.Equal(NhProxyRuntime.ChainDepthFailure, await runtime.CheckChainAsync(local));

        filter.Destination = null;
        filter.Reject = true;
        Assert.False((await runtime.PublishRewritesAsync(configuration with { Revision = 3 })).Success);
        Assert.False(rewrites.HasOnlyExternalDestinations(origin));
        var (failed, _) = Request(app, origin.AbsoluteUri);
        Assert.Equal(NhProxyRuntime.ChainDepthFailure, await runtime.CheckChainAsync(failed));

        filter.Reject = false;
        Assert.True((await runtime.PublishRewritesAsync(configuration with { Revision = 4 })).Success);
        var (external, externalHeaders) = Request(app, origin.AbsoluteUri);
        Assert.Null(await runtime.CheckChainAsync(external));
        Assert.Equal(0, externalHeaders.Enumerations);
    }

    [Fact]
    public async Task Shortcut_preserves_request_cancellation()
    {
        await using var app = CreateApplication();
        var runtime = app.Services.GetRequiredService<NhProxyRuntime>();
        Assert.True((await runtime.PublishRedirectsAsync(new())).Success);
        var (context, _) = Request(app, "http://proxy.example");
        context.RequestAborted = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CheckChainAsync(context));
    }

    private static WebApplication CreateApplication(IProxyConfigFilter? filter = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.Configure<NhProxyOptions>(options => options.Limits.MaximumChainDepth = 1);
        builder.Services.AddSingleton<INhProxyConfigurationValidator, NhProxyConfigurationValidator>();
        builder.Services.AddSingleton<NhProxyRuntime>();
        builder.Services.AddReverseProxy().LoadFromMemory([], []);
        builder.Services.AddSingleton(provider => new NhProxyRewriteRuntime(
            provider.GetRequiredService<INhProxyConfigurationValidator>(),
            (InMemoryConfigProvider)provider.GetRequiredService<IProxyConfigProvider>(), provider));
        builder.Services.AddSingleton<IConfigChangeListener>(provider => provider.GetRequiredService<NhProxyRewriteRuntime>());
        if (filter is not null)
        {
            builder.Services.AddSingleton(filter);
        }

        var app = builder.Build();
        app.MapNewHeapProxy();
        // Materialize the real YARP configuration without starting a server or sending requests.
        _ = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        return app;
    }

    private static NhProxyRewriteConfiguration Configuration(string destination, long revision)
    {
        var cluster = new NhProxyCluster
        {
            Id = Guid.NewGuid(), Name = "Backend", Destination = new("backend", new(destination))
        };
        return new()
        {
            Revision = revision, Clusters = [cluster],
            Rules = [new()
            {
                Id = Guid.NewGuid(), Name = "Loop candidate", ClusterId = cluster.Id,
                Match = new() { Path = "/loop" }
            }]
        };
    }

    private static (DefaultHttpContext Context, CountingHeaders Headers) Request(WebApplication app, string origin)
    {
        var uri = new Uri(origin);
        var headers = new CountingHeaders { ["Host"] = uri.Authority, ["X-Request-Id"] = "test" };
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Scheme = uri.Scheme, Path = "/loop", Method = "GET", Headers = headers
        });
        return (context, headers);
    }

    private sealed class CountingHeaders : HeaderDictionary, IEnumerable<KeyValuePair<string, StringValues>>
    {
        public int Enumerations { get; private set; }

        IEnumerator<KeyValuePair<string, StringValues>> IEnumerable<KeyValuePair<string, StringValues>>.GetEnumerator()
        {
            Enumerations++;
            return GetEnumerator();
        }
    }

    private sealed class ReloadFilter : IProxyConfigFilter
    {
        public string? Destination { get; set; }
        public bool Reject { get; set; }
        public TaskCompletionSource? Entered { get; set; }
        public TaskCompletionSource? Release { get; set; }

        public async ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancellationToken)
        {
            if (Release is not null)
            {
                Entered!.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            if (Reject)
            {
                throw new InvalidOperationException("Deliberate configuration rejection.");
            }

            return Destination is null ? cluster : cluster with
            {
                Destinations = new Dictionary<string, DestinationConfig> { ["backend"] = new() { Address = Destination } }
            };
        }

        public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancellationToken)
            => ValueTask.FromResult(route);
    }
}
