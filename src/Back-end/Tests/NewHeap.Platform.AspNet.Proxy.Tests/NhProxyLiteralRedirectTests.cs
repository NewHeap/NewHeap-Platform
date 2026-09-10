using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Xunit;

namespace NewHeap.Platform.AspNet.Proxy.Tests;

public sealed class NhProxyLiteralRedirectTests
{
    [Fact]
    public void Chain_depth_defaults_to_two_and_rejects_non_positive_values()
    {
        Assert.Equal(2, new NhProxyLimits().MaximumChainDepth);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NhProxyLimits { MaximumChainDepth = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new NhProxyLimits { MaximumChainDepth = -1 });
    }

    private static NhProxyRedirectRule Rule(string path = "/old", string target = "/new") => new()
    {
        Id = Guid.NewGuid(), Name = "Literal redirect", Match = new NhProxyRedirectMatch { Path = path }, Target = target
    };

    private static NhProxyRuntime Runtime() => new(new NhProxyConfigurationValidator(Options.Create(new NhProxyOptions())));

    private static async Task<HttpContext> Request(NhProxyRuntime runtime, string path, string method = "GET", string host = "proxy.example:8080", string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.Host = new HostString(host);
        context.Request.QueryString = new QueryString(query);
        var middleware = new NhProxyRedirectMiddleware(next =>
        {
            next.Response.StatusCode = StatusCodes.Status418ImATeapot;
            return Task.CompletedTask;
        }, runtime);
        await middleware.InvokeAsync(context);
        return context;
    }

    [Theory]
    [InlineData("/old", 302)]
    [InlineData("/old/", 418)]
    [InlineData("/old/child", 418)]
    [InlineData("/OLD", 418)]
    public async Task Matching_is_exact_case_sensitive_and_ignores_query(string path, int status)
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration { Rules = [Rule()] })).Success);
        Assert.Equal(status, (await Request(runtime, path, query: "?x=1")).Response.StatusCode);
    }

    [Fact]
    public async Task Regex_metacharacters_are_literal_and_reserved_paths_bypass_redirects()
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule("/a.*"), Rule("/newheap-proxy"), Rule("/newheap-proxy/login"), Rule("/newheap-proxy-other")]
        })).Success);
        Assert.Equal(302, (await Request(runtime, "/a.*")).Response.StatusCode);
        Assert.Equal(418, (await Request(runtime, "/abc")).Response.StatusCode);
        Assert.Equal(418, (await Request(runtime, "/newheap-proxy")).Response.StatusCode);
        Assert.Equal(418, (await Request(runtime, "/newheap-proxy/login")).Response.StatusCode);
        Assert.Equal(302, (await Request(runtime, "/newheap-proxy-other")).Response.StatusCode);
    }

    [Fact]
    public async Task Disabled_rules_priority_id_ties_and_host_method_filters_are_respected()
    {
        var first = Rule(target: "/first") with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var later = Rule(target: "/later") with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var filtered = Rule(target: "/filtered") with
        {
            Priority = -1,
            Match = new NhProxyRedirectMatch { Path = "/old", Hosts = ["PROXY.example:8080"], Methods = ["POST"] }
        };
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [later, filtered, first, Rule(target: "/disabled") with { Enabled = false, Priority = -2 }]
        })).Success);
        Assert.Equal("/first", (await Request(runtime, "/old")).Response.Headers.Location.ToString());
        Assert.Equal("/filtered", (await Request(runtime, "/old", "POST")).Response.Headers.Location.ToString());
        Assert.Equal("/first", (await Request(runtime, "/old", "POST", "proxy.example:8081")).Response.Headers.Location.ToString());
        Assert.Equal("/first", (await Request(runtime, "/old", "POST", "other.example:8080")).Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData(NhProxyRedirectQueryMode.Preserve, "/new?keep=1&keep=2&x=target&x=second#section")]
    [InlineData(NhProxyRedirectQueryMode.Replace, "/new?x=target&x=second#section")]
    [InlineData(NhProxyRedirectQueryMode.Discard, "/new#section")]
    public async Task Query_modes_preserve_duplicates_and_fragments(NhProxyRedirectQueryMode mode, string expected)
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule(target: "/new?x=target&x=second#section") with { QueryMode = mode }]
        })).Success);
        var context = await Request(runtime, "/old", query: "?keep=1&keep=2&x=incoming");
        Assert.Equal(expected, context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData(NhProxyRedirectStatus.MovedPermanently)]
    [InlineData(NhProxyRedirectStatus.Found)]
    [InlineData(NhProxyRedirectStatus.SeeOther)]
    [InlineData(NhProxyRedirectStatus.TemporaryRedirect)]
    [InlineData(NhProxyRedirectStatus.PermanentRedirect)]
    public async Task Configured_status_and_absolute_target_are_used_without_reflecting_host(NhProxyRedirectStatus status)
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule(target: "https://destination.example/new?source=proxy#part") with { Status = status }]
        })).Success);
        var context = await Request(runtime, "/old", "POST", "untrusted.example", "?tag=a%26b&tag=c");
        Assert.Equal((int)status, context.Response.StatusCode);
        var location = new Uri(context.Response.Headers.Location.ToString());
        Assert.Equal("destination.example", location.Host);
        Assert.Equal(new[] { "a&b", "c" }, QueryHelpers.ParseQuery(location.Query)["tag"].ToArray());
    }

    [Theory]
    [InlineData("//evil.example/path")]
    [InlineData("/\\evil.example/path")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/new\r\nX-Injected: value")]
    [InlineData("relative/path")]
    [InlineData("/old")]
    public async Task Unsafe_or_self_targets_fail_validation_without_losing_active_snapshot(string target)
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration { Revision = 1, Rules = [Rule()] })).Success);
        var invalid = await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration { Revision = 2, Rules = [Rule(target: target)] });
        Assert.False(invalid.Success);
        Assert.Equal(NhProxyErrorCodes.Validation, Assert.Single(invalid.GetResultItems()).Name);
        Assert.Equal(1, runtime.GetStatus().Redirect.ActiveRevision);
        Assert.Equal("/new", (await Request(runtime, "/old")).Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Unicode_targets_are_safe_for_the_location_header()
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule(target: "https://bük.example/é?q=é#é")]
        })).Success);
        Assert.Equal("https://xn--bk-xka.example/%C3%A9?q=%C3%A9#%C3%A9",
            (await Request(runtime, "/old")).Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Snapshot_validation_rejects_duplicate_ids_invalid_statuses_and_missing_collections()
    {
        var rule = Rule();
        Assert.False((await Runtime().PublishRedirectsAsync(new NhProxyRedirectConfiguration { Rules = [rule, rule] })).Success);
        Assert.False((await Runtime().PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [rule with { Status = (NhProxyRedirectStatus)200 }]
        })).Success);
        Assert.False((await Runtime().PublishRedirectsAsync(new NhProxyRedirectConfiguration { Rules = default })).Success);
        Assert.False((await Runtime().PublishRedirectsAsync(new NhProxyRedirectConfiguration { FormatVersion = 99 })).Success);
    }

    [Theory]
    [InlineData(NhProxyRedirectPathMatchMode.Prefix)]
    [InlineData(NhProxyRedirectPathMatchMode.RouteTemplate)]
    public async Task Unsupported_match_modes_are_rejected(NhProxyRedirectPathMatchMode mode)
    {
        var rule = Rule() with { Match = new NhProxyRedirectMatch { Path = "/old", PathMode = mode } };
        Assert.False((await Runtime().PublishRedirectsAsync(new NhProxyRedirectConfiguration { Rules = [rule] })).Success);
    }

    [Fact]
    public async Task Publication_is_atomic_and_cannot_roll_back_to_an_older_revision()
    {
        var runtime = Runtime();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Request(runtime, "/old"));
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration { Revision = 2, Rules = [Rule()] })).Success);
        Assert.False((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration { Revision = 1, Rules = [] })).Success);
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration { Revision = 3, Rules = [] })).Success);
        Assert.Equal(418, (await Request(runtime, "/old")).Response.StatusCode);
        Assert.Equal(NhProxyActivationState.NotInitialized, runtime.GetStatus().Rewrite.State);
    }

    [Theory]
    [InlineData(NhProxyRedirectQueryMode.Preserve)]
    [InlineData(NhProxyRedirectQueryMode.Discard)]
    [InlineData(NhProxyRedirectQueryMode.Replace)]
    public async Task Regex_captures_build_the_entire_target_without_applying_query_modes(NhProxyRedirectQueryMode mode)
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule(target: "/projects/${id}?${query}&source=proxy#details") with
            {
                QueryMode = mode,
                Match = new NhProxyRedirectMatch { PathMode = NhProxyRedirectPathMatchMode.Regex, Path = @"^/old/(?<id>[^?]+)\?(?<query>.*)$" }
            }]
        })).Success);
        Assert.Equal("/projects/42?tag=a%26b&tag=c&drop=1&source=proxy#details",
            (await Request(runtime, "/old/42", query: "?tag=a%26b&tag=c&drop=1")).Response.Headers.Location.ToString());
        Assert.Equal(418, (await Request(runtime, "/OLD/42", query: "?tag=x")).Response.StatusCode);
    }

    [Theory]
    [InlineData(@"^/old/([^?]+)(\?.*)?$", "/new/$1$2", "/old/café", "?tag=a%26b&tag=c", "/new/caf%C3%A9?tag=a%26b&tag=c")]
    [InlineData(@"^/old/([^?]+)(\?.*)?$", "/new/$1$2", "/old/42", "", "/new/42")]
    [InlineData(@"^/old/([^?]+)", "/new/$1?fixed=1", "/old/42", "?discard=1", "/new/42?fixed=1")]
    [InlineData(@"(?i)^/old/(?<id>[^?]+)", "https://example.com/new/${id}?cost=$$5", "/OLD/42", "?discard=1", "https://example.com/new/42?cost=$5")]
    public async Task Regex_uses_escaped_path_and_query_and_dotnet_substitutions(string pattern, string target, string path, string query, string expected)
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule(target: target) with { Match = new NhProxyRedirectMatch { PathMode = NhProxyRedirectPathMatchMode.Regex, Path = pattern } }]
        })).Success);
        Assert.Equal(expected, (await Request(runtime, path, query: query)).Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("(", "/new/$1")]
    [InlineData("[", "/new")]
    [InlineData("^/old$", "javascript:$1")]
    [InlineData("^/old$", "//example.com/$1")]
    [InlineData("^/old/(.*)$", "https://$1/")]
    [InlineData("^/old/(?<host>.*)$", "https://${host}/")]
    public async Task Invalid_regex_does_not_replace_an_active_snapshot(string pattern, string target)
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration { Revision = 1, Rules = [Rule()] })).Success);
        Assert.False((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Revision = 2, Rules = [Rule(target: target) with { Match = new NhProxyRedirectMatch { PathMode = NhProxyRedirectPathMatchMode.Regex, Path = pattern } }]
        })).Success);
        Assert.Equal(1, runtime.GetStatus().Redirect.ActiveRevision);
        Assert.Equal("/new", (await Request(runtime, "/old")).Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/old//evil.example", "/$1", 418)]
    [InlineData("/old/value", "/old/$1", 508)]
    public async Task Unsafe_expansions_pass_through_and_self_redirects_are_refused(string path, string target, int expectedStatus)
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule(target: target) with { Match = new NhProxyRedirectMatch { PathMode = NhProxyRedirectPathMatchMode.Regex, Path = "^/old/(.*)$" } }]
        })).Success);
        var response = (await Request(runtime, path)).Response;
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.False(response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task Regex_timeout_returns_503_and_the_administration_branch_is_always_reserved()
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule() with { Match = new NhProxyRedirectMatch { PathMode = NhProxyRedirectPathMatchMode.Regex, Path = "^/(a+)+$" } }]
        })).Success);
        var timedOut = await Request(runtime, "/" + new string('a', 10000) + "!");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, timedOut.Response.StatusCode);
        Assert.Equal("no-store", timedOut.Response.Headers.CacheControl.ToString());
        Assert.False(timedOut.Response.Headers.ContainsKey("Location"));
        Assert.Equal(302, (await Request(runtime, "/aaa")).Response.StatusCode);
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Revision = 1, Rules = [Rule() with { Match = new NhProxyRedirectMatch { PathMode = NhProxyRedirectPathMatchMode.Regex, Path = ".*" } }]
        })).Success);
        Assert.Equal(418, (await Request(runtime, "/newheap-proxy/Edit")).Response.StatusCode);
    }

    [Theory]
    [InlineData("/old", 1, 60)]
    [InlineData("/missing", 1, 60)]
    [InlineData("/missing", 10, 20)]
    public async Task Whole_resolver_deadline_covers_exact_hits_misses_and_accumulated_rule_work(string path, int ruleCount, int delayMilliseconds)
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = Enumerable.Range(0, ruleCount).Select(_ => Rule() with
            {
                Match = new NhProxyRedirectMatch { Path = "/old", Methods = ["GET"] }
            }).ToImmutableArray()
        })).Success);

        var feature = new SlowMethodRequestFeature(delayMilliseconds) { Path = path };
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestFeature>(feature);
        var middleware = new NhProxyRedirectMiddleware(_ => throw new InvalidOperationException("Timed-out resolution must not reach the backend."), runtime);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.InRange(feature.MethodReads, 1, Math.Min(ruleCount, 3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(49)]
    [InlineData(75)]
    public void Remaining_budget_is_enforced_inside_the_regex_engine(int remainingMilliseconds)
    {
        var variants = new ConcurrentDictionary<int, Regex>();
        var remaining = TimeSpan.FromMilliseconds(remainingMilliseconds + 0.9);
        var regex = NhProxyRuntime.GetRegexForBudget(variants, "^/(a+)+$", remaining)!;
        Assert.Equal(TimeSpan.FromMilliseconds(remainingMilliseconds), regex.MatchTimeout);
        Assert.Matches(regex, "/aaa");

        // This exception comes from the synchronous regex engine, not a timer that abandons running work.
        var exception = Assert.Throws<RegexMatchTimeoutException>(() => regex.Match("/" + new string('a', 10000) + "!"));

        Assert.Equal(TimeSpan.FromMilliseconds(remainingMilliseconds), exception.MatchTimeout);
        Assert.Same(regex, NhProxyRuntime.GetRegexForBudget(variants, "^/(a+)+$", remaining));
        Assert.Matches(regex, "/aaa");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0.9)]
    public void Exhausted_budget_does_not_prepare_or_start_a_regex(double remainingMilliseconds)
    {
        var variants = new ConcurrentDictionary<int, Regex>();

        Assert.Null(NhProxyRuntime.GetRegexForBudget(variants, "^/(a+)+$", TimeSpan.FromMilliseconds(remainingMilliseconds)));

        Assert.Empty(variants);
    }

    [Fact]
    public async Task Expired_filter_work_returns_timeout_before_redirecting()
    {
        var runtime = Runtime();
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule() with
            {
                Match = new NhProxyRedirectMatch { Path = "^/(a+)+$", PathMode = NhProxyRedirectPathMatchMode.Regex, Methods = ["GET"] }
            }]
        })).Success);
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestFeature>(new SlowMethodRequestFeature(60) { Path = "/aaa" });

        Assert.False(runtime.TryRedirect(context, out var failure));

        Assert.Equal(NhProxyRuntime.ResolutionTimeoutFailure, failure);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    [Theory]
    [InlineData(20, 503)]
    [InlineData(500, 302)]
    public async Task Configured_resolution_timeout_controls_the_whole_resolver(int timeoutMilliseconds, int expectedStatus)
    {
        var options = Options.Create(new NhProxyOptions
        {
            Limits = new NhProxyLimits { RedirectResolutionTimeoutMilliseconds = timeoutMilliseconds }
        });
        var runtime = new NhProxyRuntime(new NhProxyConfigurationValidator(options), options);
        Assert.True((await runtime.PublishRedirectsAsync(new NhProxyRedirectConfiguration
        {
            Rules = [Rule() with { Match = new NhProxyRedirectMatch { Path = "/old", Methods = ["GET"] } }]
        })).Success);
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestFeature>(new SlowMethodRequestFeature(60) { Path = "/old" });
        var middleware = new NhProxyRedirectMiddleware(_ => throw new InvalidOperationException("Must not reach the backend."), runtime);

        await middleware.InvokeAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2147483647)]
    public void Invalid_resolution_timeouts_are_rejected(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NhProxyLimits { RedirectResolutionTimeoutMilliseconds = milliseconds });
    }

    [Fact]
    public void Larger_timeouts_do_not_grow_the_regex_cache_without_limit()
    {
        var variants = new ConcurrentDictionary<int, Regex>();
        for (var milliseconds = 1; milliseconds <= 75; milliseconds++)
        {
            var regex = NhProxyRuntime.GetRegexForBudget(variants, "^/old$", TimeSpan.FromMilliseconds(milliseconds))!;
            Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), regex.MatchTimeout);
        }

        Assert.Equal(50, variants.Count);
    }

    private sealed class SlowMethodRequestFeature(int delayMilliseconds) : HttpRequestFeature, IHttpRequestFeature
    {
        public int MethodReads { get; private set; }

        string IHttpRequestFeature.Method
        {
            get
            {
                MethodReads++;
                Thread.Sleep(delayMilliseconds);
                return "GET";
            }
            set => base.Method = value;
        }
    }
}
