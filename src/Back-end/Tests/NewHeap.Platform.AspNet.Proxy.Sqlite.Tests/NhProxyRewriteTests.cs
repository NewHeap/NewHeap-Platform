using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite.Tests;

public sealed class NhProxyRewriteTests
{
    [Theory]
    [InlineData("Content-Type", null, true)]
    [InlineData("Content-Type", "application/json", true)]
    [InlineData("Content-Type", "text/plain", false)]
    [InlineData("Content-Type", "", false)]
    [InlineData("X-Mode", null, true)]
    [InlineData("X-Mode", "application/json", true)]
    [InlineData("X-Mode", "text/plain", false)]
    [InlineData("X-Mode", "", false)]
    public async Task Chain_and_preview_use_transformed_general_and_content_headers(string headerName, string? transformedValue, bool loops)
    {
        var directory = Directory.CreateTempSubdirectory("nh-chain-headers-");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(configureStorage: storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            var requests = 0;
            string? forwardedBody = null;
            app.Use(async (context, next) =>
            {
                // Bound the regression even if a broken guard forwards the loop again.
                if (Interlocked.Increment(ref requests) > 3)
                {
                    context.Response.StatusCode = 529;
                    return;
                }

                if (context.Request.Path == "/b")
                {
                    using var reader = new StreamReader(context.Request.Body);
                    forwardedBody = await reader.ReadToEndAsync();
                }

                await next(context);
            });
            app.UseNewHeapProxy();
            await app.StartAsync();
            var origin = app.Urls.Single();
            var cluster = Cluster(new(origin + "/"));
            NhProxyRewriteRule RuleFor(string path, string target) => new()
            {
                Id = Guid.NewGuid(), Name = path, ClusterId = cluster.Id,
                Match = new() { Path = path, Headers = [new(headerName, NhProxyHeaderMatchMode.Exact, ["application/json"])] },
                Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.Set, target)]
            };
            var first = RuleFor("/a", "/b");
            if (transformedValue is not null)
            {
                first = first with { Transforms = first.Transforms.Add(new NhProxyHeaderTransform(NhProxyHeaderDirection.Request,
                    headerName, transformedValue.Length == 0 ? NhProxyValueOperation.Remove : NhProxyValueOperation.Set,
                    transformedValue.Length == 0 ? null : transformedValue)) };
            }

            var configuration = app.Services.GetRequiredService<INhProxyConfigurationService>();
            Assert.True((await configuration.SaveRewritesAsync(new(0, [first, RuleFor("/b", "/a")], [cluster]))).Success);
            var tester = app.Services.GetRequiredService<INhProxyDraftTester>();
            var headers = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add("Content-Type", ["application/json"]).Add("Content-Language", ["en", "nl"]).Add("X-Multi", ["one", "two"]);
            if (headerName != "Content-Type")
            {
                headers = headers.Add(headerName, ["application/json"]);
            }

            var input = new NhProxyTestRequest { Url = new(origin + "/a"), Method = "POST", Headers = headers };
            var tested = await tester.TestSavedAsync(new(1, 0), input);
            Assert.Equal(!loops, tested.Success);
            if (loops)
            {
                Assert.Contains(tested.GetResultItems(), item => item.Name == NhProxyErrorCodes.MaximumChainDepth);
            }

            // An external synthetic origin stops chain traversal and exposes the first transform's headers.
            var preview = await tester.TestSavedAsync(new(1, 0), input with { Url = new("https://public.example/a") });
            Assert.True(preview.Success, string.Join("; ", preview.AllErrorMessages));
            var outgoing = preview.Data!.Rewrite!.SafeRequestHeaders;
            Assert.Equal(new[] { "en", "nl" }, outgoing["Content-Language"]);
            Assert.Equal(new[] { "one", "two" }, outgoing["X-Multi"]);
            if (transformedValue == "")
            {
                Assert.False(outgoing.ContainsKey(headerName));
            }
            else
            {
                Assert.Equal(new[] { transformedValue ?? "application/json" }, outgoing[headerName]);
            }

            Assert.Equal(0, requests);
            using var client = new HttpClient();
            using var message = new HttpRequestMessage(HttpMethod.Post, origin + "/a") { Content = new StringContent("{}") };
            message.Content.Headers.ContentType = new("application/json");
            if (headerName != "Content-Type")
            {
                message.Headers.Add(headerName, "application/json");
            }

            using var response = await client.SendAsync(message);
            Assert.Equal(loops ? 508 : 404, (int)response.StatusCode);
            Assert.Equal(loops ? 1 : 2, requests);
            Assert.Equal(loops ? null : "{}", forwardedBody);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Local_rule_chains_are_refused_before_redirecting_or_forwarding(int maximumDepth)
    {
        var directory = Directory.CreateTempSubdirectory("nh-chain-depth-");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NewHeapProxy:Limits:MaximumChainDepth"] = maximumDepth.ToString(),
                ["NewHeapProxy:Sqlite:DatabasePath"] = Path.Combine(directory.FullName, "proxy.db")
            });
            builder.Services.AddNewHeapProxy(builder.Configuration.GetSection("NewHeapProxy"));
            await using var app = builder.Build();
            app.UseNewHeapProxy();
            var terminalRequests = 0;
            app.MapGet("/terminal", () => { terminalRequests++; return "done"; });
            await app.StartAsync();
            var origin = app.Urls.Single();
            var configuration = app.Services.GetRequiredService<INhProxyConfigurationService>();
            NhProxyRedirectRule Redirect(string path, string target) => new()
            {
                Id = Guid.NewGuid(), Name = path, Match = new() { Path = path }, Target = target
            };
            Assert.True((await configuration.SaveRedirectsAsync(new(0,
            [
                Redirect("/a", "/b"), Redirect("/b", "/terminal"), Redirect("/c", "/a"),
                Redirect("/loop-a", "/loop-b"), Redirect("/loop-b", "/loop-a"),
                Redirect("/absolute", origin + "/absolute"), Redirect("/external", "https://other.example/a"),
                Redirect("/regex", "/regex/x$1") with { Match = new() { Path = "^/regex/(.*)$", PathMode = NhProxyRedirectPathMatchMode.Regex } }
            ]))).Success);
            var cluster = Cluster(new(origin + "/"));
            var rewrite = new NhProxyRewriteRule
            {
                Id = Guid.NewGuid(), Name = "Local forwarding", ClusterId = cluster.Id,
                Match = new() { Path = "/proxy/{**rest}" },
                Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.RemovePrefix, "/proxy")]
            };
            var self = rewrite with { Id = Guid.NewGuid(), Match = new() { Path = "/self/{**rest}" }, Transforms = [] };
            Assert.True((await configuration.SaveRewritesAsync(new(0, [rewrite, self], [cluster]))).Success);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var tester = app.Services.GetRequiredService<INhProxyDraftTester>();
            foreach (var (path, steps) in new[] { ("/a", 2), ("/c", 3), ("/proxy/a", 3), ("/loop-a", int.MaxValue), ("/absolute", int.MaxValue), ("/regex/a", int.MaxValue), ("/self/a", int.MaxValue), ("/external", 1) })
            {
                using var response = await client.GetAsync(origin + path);
                var tested = await tester.TestSavedAsync(new(1, 1), new() { Url = new(origin + path) });
                if (steps > maximumDepth)
                {
                    Assert.Equal(508, (int)response.StatusCode);
                    Assert.Null(response.Headers.Location);
                    Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                    Assert.False(tested.Success);
                    Assert.Contains(tested.GetResultItems(), item => item.Name == "newheap-proxy.maximum-chain-depth");
                }
                else
                {
                    Assert.Equal(HttpStatusCode.Found, response.StatusCode);
                    Assert.True(tested.Success, string.Join("; ", tested.AllErrorMessages));
                }
            }

            Assert.Equal(0, terminalRequests);
            Assert.Equal(1, (await configuration.GetRedirectsAsync()).Revision);
            Assert.Equal(1, (await configuration.GetRewritesAsync()).Revision);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

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
                Assert.Equal("newheap-proxy.path-mismatch", Assert.Single(Assert.Single(noMatch.Data.Matches).Reasons).Code);
                Assert.False((await tester.TestRewriteAsync(new(new(0, 0), rule, new() { Url = new("https://public.example/public/42/a") }))).Success);
                var disabled = await tester.TestRewriteAsync(new(new(1, 0), rule with { Enabled = false }, new() { Url = new("https://public.example/public/42/a") }));
                Assert.Equal(NhProxyTestOutcome.NoMatch, disabled.Data!.Outcome);
                Assert.Equal("newheap-proxy.rule-disabled", Assert.Single(Assert.Single(disabled.Data.Matches).Reasons).Code);
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
            var matched = (await tester.TestRewriteAsync(new(new(1, 0), rule, input))).Data!;
            Assert.Equal(NhProxyTestOutcome.Rewrite, matched.Outcome);
            Assert.True(Assert.Single(matched.Matches).Matched);
            Assert.True(Assert.Single(matched.Matches).Selected);
            Assert.Empty(Assert.Single(matched.Matches).Reasons);
            foreach (var (invalid, reason, field) in new (NhProxyTestRequest, string, string?)[]
            {
                (input with { Method = "GET" }, "method-mismatch", null),
                (input with { Headers = input.Headers.Clear() }, "header-mismatch", "X-Mode"),
                (input with { Url = new("https://public.example/public/42/a?preview=yes") }, "host-mismatch", null),
                (input with { Url = new("https://public.example:8443/public/42/a") }, "query-mismatch", "preview")
            })
            {
                var tested = (await tester.TestRewriteAsync(new(new(1, 0), rule, invalid))).Data!;
                Assert.Equal(NhProxyTestOutcome.NoMatch, tested.Outcome);
                var diagnostic = Assert.Single(tested.Matches);
                Assert.Equal(rule.Id, diagnostic.RuleId);
                Assert.Equal(rule.Name, diagnostic.RuleName);
                Assert.Equal(rule.Match.Path, diagnostic.RulePath);
                Assert.False(diagnostic.Matched);
                Assert.False(diagnostic.Selected);
                var issue = Assert.Single(diagnostic.Reasons);
                Assert.Equal("newheap-proxy." + reason, issue.Code);
                Assert.Equal(issue.Code, issue.LocalizationKey);
                Assert.Equal(field, issue.Field);
            }

            var redirect = new NhProxyRedirectRule { Id = Guid.NewGuid(), Name = "Moved", Match = new() { Path = "/public/42/a" }, Target = "/moved" };
            var live = proxy.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();
            Assert.True((await configuration.SaveRedirectsAsync(new(0, [redirect]))).Success);
            Assert.Same(live, proxy.Services.GetRequiredService<IProxyConfigProvider>().GetConfig());
            var shadowed = await tester.TestRewriteAsync(new(new(1, 1), rule, input));
            Assert.True(shadowed.Success);
            Assert.Equal(NhProxyTestOutcome.Redirect, shadowed.Data!.Outcome);
            Assert.Equal(redirect.Id, shadowed.Data.SelectedRuleId);
            Assert.Equal(2, shadowed.Data.Matches.Length);
            Assert.Equal("newheap-proxy.redirect-precedence", Assert.Single(shadowed.Data.Matches.Single(item => item.RuleId == rule.Id).Reasons).Code);
            var reserved = await tester.TestRewriteAsync(new(new(1, 1), rule, input with { Url = new("https://public.example/newheap-proxy/Edit") }));
            Assert.Equal(NhProxyTestOutcome.ReservedAdministrationPath, reserved.Data!.Outcome);
            Assert.All(reserved.Data.Matches, item => Assert.Equal("newheap-proxy.reserved-path", Assert.Single(item.Reasons).Code));
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
    public async Task Saved_test_lists_redirect_rejections_skips_and_matching_rewrite_alternatives()
    {
        var directory = Directory.CreateTempSubdirectory("nh-proxy-diagnostics-");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(configureStorage: storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.UseNewHeapProxy();
            await app.StartAsync();
            var configuration = app.Services.GetRequiredService<INhProxyConfigurationService>();
            var tester = app.Services.GetRequiredService<INhProxyDraftTester>();
            NhProxyRedirectRule Redirect(int priority, NhProxyRedirectMatch match) => new()
            {
                Id = Guid.NewGuid(), Name = "Redirect " + priority, Priority = priority, Match = match, Target = "https://other.example/target"
            };
            var rules = new[]
            {
                Redirect(0, new() { Path = "/value" }) with { Enabled = false },
                Redirect(1, new() { Path = "/value", Methods = ["POST"] }),
                Redirect(2, new() { Path = "/value", Hosts = ["other.example"] }),
                Redirect(3, new() { Path = "/Value" }),
                Redirect(4, new() { Path = "^/value\\?yes=1$", PathMode = NhProxyRedirectPathMatchMode.Regex }),
                Redirect(5, new() { Path = "/value" }),
                Redirect(6, new() { Path = "/value", Hosts = ["public.example"] })
            };
            Assert.True((await configuration.SaveRedirectsAsync(new(0, rules.Reverse().ToImmutableArray()))).Success);
            var cluster = Cluster(new("https://backend.example/"));
            var catchAll = Rule(cluster) with { Match = new() { Path = "/{**rest}" }, Transforms = [] };
            var specific = catchAll with { Id = Guid.NewGuid(), Name = "Specific", Match = new() { Path = "/value" } };
            Assert.True((await configuration.SaveRewritesAsync(new(0, [catchAll, specific], [cluster]))).Success);
            var input = new NhProxyTestRequest { Url = new("https://public.example/value") };
            var redirected = await tester.TestSavedAsync(new(1, 1), input);
            Assert.True(redirected.Success);
            Assert.Equal(rules.Select(rule => rule.Id), redirected.Data!.Matches.Take(rules.Length).Select(item => item.RuleId));
            var reasons = new[] { "rule-disabled", "method-mismatch", "host-mismatch", "path-mismatch", "regex-mismatch", null, "earlier-redirect" };
            for (var index = 0; index < rules.Length; index++)
            {
                var diagnostic = redirected.Data.Matches[index];
                Assert.Equal(index == 5, diagnostic.Selected);
                Assert.Equal(index == 5, diagnostic.Matched);
                if (reasons[index] is { } reason)
                {
                    Assert.Equal("newheap-proxy." + reason, Assert.Single(diagnostic.Reasons).Code);
                }
            }

            Assert.True((await configuration.SaveRedirectsAsync(new(1, []))).Success);
            var rewritten = await tester.TestSavedAsync(new(1, 2), input);
            Assert.True(rewritten.Success);
            Assert.Equal(specific.Id, rewritten.Data!.SelectedRuleId);
            Assert.All(rewritten.Data.Matches, item => Assert.True(item.Matched));
            Assert.Equal("newheap-proxy.rewrite-precedence", Assert.Single(rewritten.Data.Matches.Single(item => item.RuleId == catchAll.Id).Reasons).Code);
            await app.StopAsync();
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
