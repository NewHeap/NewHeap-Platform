using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Proxy;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace NewHeap.Platform.AspNet.Proxy.Tests;

public sealed class NhProxyContractTests
{
    [Fact]
    public void Rewrite_editor_shows_complete_configuration_and_replaces_the_first_path_transform_once()
    {
        var cluster = new NhProxyCluster { Id = Guid.NewGuid(), Name = "Backend", Destination = new("backend", new("https://backend.example/")) };
        var rule = new NhProxyRewriteRule
        {
            Id = Guid.NewGuid(), Name = "Projects", ClusterId = cluster.Id, Match = new() { Path = "/api/{**rest}" },
            Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.RemovePrefix, "/api"),
                new NhProxyPathTransform(NhProxyPathTransformKind.AddPrefix, "/v2"),
                new NhProxyQueryTransform("source", NhProxyValueOperation.Set, "proxy")]
        };
        var editor = NhProxyRewriteEditorModel.FromRule(rule, cluster, 1, 0);
        var displayed = JsonSerializer.Deserialize<NhProxyRewriteRule>(editor.AdvancedRuleJson, NhProxyRewriteEditorModel.JsonOptions)!;
        Assert.Equal(rule.Transforms.ToArray(), displayed.Transforms.ToArray());
        Assert.Equal(rule.Transforms.ToArray(), editor.ToRule().Transforms.ToArray());
        Assert.Equal(cluster, editor.ToCluster());

        editor.PathValue = "/changed";
        var edited = editor.ToRule();
        Assert.Equal(3, edited.Transforms.Length);
        Assert.Equal(new NhProxyPathTransform(NhProxyPathTransformKind.RemovePrefix, "/changed"), edited.Transforms[0]);
        Assert.Equal(rule.Transforms.Skip(1), edited.Transforms.Skip(1));

        editor.PathOperation = null;
        Assert.Equal(rule.Transforms.Skip(1), editor.ToRule().Transforms);
        editor.AdvancedRuleJson = "{";
        Assert.Throws<JsonException>(() => editor.ToRule());
        Assert.Equal("{", editor.AdvancedRuleJson);
    }

    [Fact]
    public void Yarp_callback_accepts_the_native_builder_and_is_excluded_from_json()
    {
        var options = new NhProxyOptions();
        var services = new ServiceCollection();
        var calls = 0;
        Assert.Null(options.YarpConfiguration);
        options.ConfigureYarp(_ => Assert.Fail("The previous callback must be replaced."));

        options.ConfigureYarp(yarp =>
        {
            Assert.Same(services, yarp.Services);
            yarp.ConfigureHttpClient((_, handler) => handler.ConnectTimeout = TimeSpan.FromSeconds(5));
            calls++;
        });

        Assert.Equal(0, calls);
        Assert.Throws<ArgumentNullException>(() => options.ConfigureYarp(null!));
        options.YarpConfiguration!(services.AddReverseProxy());

        Assert.Equal(1, calls);
        var json = JsonSerializer.Serialize(options);
        Assert.DoesNotContain(nameof(NhProxyOptions.YarpConfiguration), json);
        Assert.Null(JsonSerializer.Deserialize<NhProxyOptions>(json)!.YarpConfiguration);
    }

    [Fact]
    public void Rewrite_configuration_preserves_ordered_typed_transforms_in_json()
    {
        var clusterId = Guid.NewGuid();
        var configuration = new NhProxyRewriteConfiguration
        {
            Revision = 7,
            Clusters = [new NhProxyCluster
            {
                Id = clusterId,
                Name = "Backend",
                Destination = new NhProxyDestination("backend", new Uri("https://backend.example/"))
            }],
            Rules = [new NhProxyRewriteRule
            {
                Id = Guid.NewGuid(),
                Name = "API",
                ClusterId = clusterId,
                Match = new NhProxyRewriteMatch { Path = "/api/{**remainder}" },
                Transforms =
                [
                    new NhProxyPathTransform(NhProxyPathTransformKind.RemovePrefix, "/api"),
                    new NhProxyQueryTransform("source", NhProxyValueOperation.Set, "proxy"),
                    new NhProxyHeaderTransform(NhProxyHeaderDirection.Response, "X-Proxy", NhProxyValueOperation.Set, "NewHeap"),
                    new NhProxyOriginalHostTransform(false),
                    new NhProxyForwardedHeadersTransform(NhProxyForwardedHeaderAction.Set)
                ]
            }]
        };

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(configuration, options);
        var restored = JsonSerializer.Deserialize<NhProxyRewriteConfiguration>(json, options)!;

        Assert.Equal(7, restored.Revision);
        Assert.Equal(clusterId, Assert.Single(restored.Clusters).Id);
        Assert.Equal<NhProxyTransform>(configuration.Rules[0].Transforms, restored.Rules[0].Transforms);
        Assert.Contains("\"kind\":\"path\"", json);
    }

    [Fact]
    public void Unknown_transform_kind_is_rejected_instead_of_discarded()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NhProxyTransform>("""
            { "kind": "script", "body": "unsupported" }
            """));
    }

    [Fact]
    public void Redirect_defaults_and_independent_revisions_survive_json()
    {
        var rule = new NhProxyRedirectRule
        {
            Id = Guid.NewGuid(),
            Name = "Moved page",
            Match = new NhProxyRedirectMatch { Path = "/old" },
            Target = "/new"
        };
        var configuration = new NhProxyRedirectConfiguration { Revision = 2, Rules = [rule] };
        var restored = JsonSerializer.Deserialize<NhProxyRedirectConfiguration>(JsonSerializer.Serialize(configuration))!;
        var status = new NhProxyStatus(
            new NhProxyEngineStatus(NhProxyEngine.Rewrite, 7, 6, NhProxyActivationState.Failed),
            new NhProxyEngineStatus(NhProxyEngine.Redirect, 2, 2, NhProxyActivationState.Active));

        Assert.Equal(302, (int)Assert.Single(restored.Rules).Status);
        Assert.Equal(NhProxyRedirectQueryMode.Preserve, restored.Rules[0].QueryMode);
        Assert.Equal(NhProxyActivationState.Active, status.Redirect.State);
        Assert.NotEqual(status.Rewrite.DesiredRevision, status.Redirect.DesiredRevision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Combined_use_and_standalone_map_initialize_native_yarp_routes(bool useCombinedEntryPoint)
    {
        var builder = WebApplication.CreateBuilder();
        // This mapping-only test assembles neutral services; the SQLite Add entry point registers MVC in real hosts.
        builder.Services.AddControllersWithViews();
        builder.Services.AddReverseProxy().LoadFromMemory(
            [new RouteConfig
            {
                RouteId = "probe", ClusterId = "backend", Match = new RouteMatch { Path = "/probe/{**remainder}" }
            }],
            [new ClusterConfig
            {
                ClusterId = "backend",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["backend"] = new() { Address = "https://backend.example/" }
                }
            }]);
        await using var app = builder.Build();

        Assert.Throws<ArgumentNullException>(() => NhProxyApplicationExtensions.UseNewHeapProxy(null!));
        Assert.Throws<ArgumentNullException>(() => NhProxyApplicationExtensions.MapNewHeapProxy(null!));
        if (useCombinedEntryPoint)
        {
            WebApplication configuredApp = app.UseNewHeapProxy();
            Assert.Same(app, configuredApp);
        }
        else
        {
            Assert.Same(app, app.MapNewHeapProxy());
        }

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), endpoint => endpoint.RoutePattern.RawText == "/probe/{**remainder}");
        Assert.Equal("/probe/{**remainder}", Assert.IsType<RouteEndpoint>(endpoint).RoutePattern.RawText);
    }
}
