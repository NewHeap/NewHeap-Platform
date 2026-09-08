using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Xunit;

namespace NewHeap.Platform.AspNet.Proxy.Tests;

public sealed class NhProxyLiteralRedirectTests
{
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
}
