using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite.Tests;

public sealed class NhProxyApiTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Rule_patterns_are_built_before_save_even_when_disabled(bool invalidTransform, bool enabled)
    {
        var directory = Directory.CreateTempSubdirectory("newheap-api-preflight-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                options.Administrator.UserName = "api-admin";
                options.Administrator.Password = "password";
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.MapProxyEndpoints().UseNewHeapProxy();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + NhProxyOptions.ApiPath + "/") };
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:password");
            var cluster = new NhProxyCluster { Id = Guid.NewGuid(), Name = "Backend", Destination = new("backend", new Uri("https://backend.example/")) };
            var validRule = new NhProxyRewriteRule
            {
                Id = Guid.NewGuid(), Name = "Rewrite", ClusterId = cluster.Id,
                Match = new() { Path = "/{value:regex(^[a-z]+$)}" },
                Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.Pattern, "/api/{value}")]
            };
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("rewrites", new NhProxyRewriteSaveRequest(0, [validRule], [cluster]))).StatusCode);
            var candidate = invalidTransform
                ? validRule with { Enabled = enabled, Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.Pattern, "/{broken")] }
                : validRule with { Enabled = enabled, Match = new() { Path = "/{value:regex([)}" } };
            var request = new NhProxyRewriteSaveRequest(1, [candidate], [cluster]);
            var expectedKey = invalidTransform ? "newheap-proxy.invalid-rewrite-transform-pattern" : "newheap-proxy.invalid-rewrite-route-pattern";

            // Offline consumers use the same native preflight, not only the HTTP registration.
            var offline = new NhProxyConfigurationValidator(Options.Create(new NhProxyOptions()));
            Assert.False((await offline.ValidateRewritesAsync(request)).Success);

            var rejected = await client.PutAsJsonAsync("rewrites", request);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var issue = Assert.Single((await rejected.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxySaveResult>>())!.Issues);
            Assert.Equal(expectedKey, issue.LocalizationKey);
            Assert.Contains(invalidTransform ? "transform" : "regex", issue.Message);
            var preview = await client.PostAsJsonAsync("rewrites/test", new NhProxyRewriteTestRequest(new(1, 0), candidate,
                new NhProxyTestRequest { Url = new Uri("https://public.example/old") }));
            Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
            Assert.Equal(expectedKey, Assert.Single((await preview.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxyRuleTestResult>>())!.Issues).LocalizationKey);

            var saved = (await client.GetFromJsonAsync<NhProxyRewriteConfiguration>("rewrites"))!;
            Assert.Equal(1, saved.Revision);
            Assert.Equal(validRule.Match.Path, Assert.Single(saved.Rules).Match.Path);
            Assert.Equal("/api/{value}", Assert.IsType<NhProxyPathTransform>(Assert.Single(saved.Rules[0].Transforms)).Value);
            Assert.Equal(1, (await client.GetFromJsonAsync<NhProxyStatus>("status"))!.Rewrite.ActiveRevision);
            Assert.Equal(1, (await client.GetFromJsonAsync<NhProxyApiResult<NhProxyChangeAuditPage>>("audit"))!.Data!.TotalCount);

            var validPreview = await client.PostAsJsonAsync("test", new NhProxySavedTestRequest(new(1, 0),
                new NhProxyTestRequest { Url = new Uri("https://public.example/old") }));
            Assert.Equal(HttpStatusCode.OK, validPreview.StatusCode);
            Assert.Equal("https://backend.example/api/old", (await validPreview.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxyRuleTestResult>>())!.Data!.Rewrite!.TargetUrl.AbsoluteUri);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Invalid_rule_input_returns_descriptions_without_saving_or_auditing()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-api-validation-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                options.Administrator.UserName = "api-admin";
                options.Administrator.Password = "password";
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.MapProxyEndpoints().UseNewHeapProxy();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + NhProxyOptions.ApiPath + "/") };
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:password");
            var cluster = new NhProxyCluster { Id = Guid.NewGuid(), Name = "Backend", Destination = new("backend", new Uri("https://backend.example/")) };
            var rewrite = new NhProxyRewriteRule
            {
                Id = Guid.NewGuid(), Name = "Rewrite", ClusterId = cluster.Id, Match = new() { Path = "/old" },
                Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.AddPrefix, "/api")]
            };

            foreach (var draft in new[] { false, true })
            {
                var body = draft
                    ? JsonSerializer.SerializeToNode(new NhProxyRewriteTestRequest(new(0, 0), rewrite,
                        new NhProxyTestRequest { Url = new Uri("https://public.example/old") }), JsonSerializerOptions.Web)!
                    : JsonSerializer.SerializeToNode(new NhProxyRewriteSaveRequest(0, [rewrite], [cluster]), JsonSerializerOptions.Web)!;
                var transform = (draft ? body["draft"] : body["rules"]![0])!["transforms"]![0]!.AsObject();
                transform.Remove("kind");
                var path = draft ? "$.draft.transforms[0]" : "$.rules[0].transforms[0]";
                foreach (var kind in new string?[] { null, "unsupported" })
                {
                    if (kind is not null)
                    {
                        transform.Insert(0, "kind", kind);
                    }

                    var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    var response = draft ? await client.PostAsync("rewrites/test", content) : await client.PutAsync("rewrites", content);
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    var result = (await response.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxySaveResult>>())!;
                    Assert.False(result.Success);
                    var issue = Assert.Single(result.Issues);
                    Assert.Equal(NhProxyErrorCodes.Validation, issue.Code);
                    Assert.StartsWith(path, issue.Field);
                    Assert.Contains(kind is null ? "must specify 'kind'" : "discriminator", issue.Message);
                }
            }

            foreach (var body in new[] { "{", "null", "{\"rules\":[]}", "{\"expectedRevision\":0,\"rules\":[],\"unknown\":true}" })
            {
                var response = await client.PutAsync("redirects", new StringContent(body, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                var issue = Assert.Single((await response.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxySaveResult>>())!.Issues);
                Assert.False(string.IsNullOrWhiteSpace(issue.Message));
                Assert.False(string.IsNullOrWhiteSpace(issue.Field));
            }

            var selfRedirect = new NhProxyRedirectRule { Id = Guid.NewGuid(), Name = "Loop", Match = new() { Path = "/loop" }, Target = "/loop" };
            var invalidRule = await client.PutAsJsonAsync("redirects", new NhProxyRedirectSaveRequest(0, [selfRedirect]));
            Assert.Equal(HttpStatusCode.BadRequest, invalidRule.StatusCode);
            var error = Assert.Single((await invalidRule.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxySaveResult>>())!.Issues);
            Assert.Contains("cannot target its own", error.Message);
            Assert.Equal(0, (await client.GetFromJsonAsync<NhProxyRedirectConfiguration>("redirects"))!.Revision);
            Assert.Equal(0, (await client.GetFromJsonAsync<NhProxyRewriteConfiguration>("rewrites"))!.Revision);
            Assert.Equal(0, (await client.GetFromJsonAsync<NhProxyApiResult<NhProxyChangeAuditPage>>("audit"))!.Data!.TotalCount);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("\"Found\"", "\"AddPrefix\"", HttpStatusCode.OK)]
    [InlineData("302", "0", HttpStatusCode.OK)]
    [InlineData("\"Unknown\"", "\"Unknown\"", HttpStatusCode.BadRequest)]
    [InlineData("999", "999", HttpStatusCode.BadRequest)]
    public async Task Put_accepts_enum_names_and_numbers_and_rejects_invalid_values(string status, string operation, HttpStatusCode expected)
    {
        var directory = Directory.CreateTempSubdirectory("newheap-api-enums-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.AddNewHeapProxy(options =>
            {
                options.Administrator.UserName = "api-admin";
                options.Administrator.Password = "password";
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.MapProxyEndpoints().UseNewHeapProxy();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + NhProxyOptions.ApiPath + "/") };
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:password");

            var redirects = $$$"""
                {"expectedRevision":0,"rules":[{"id":"11111111-1111-1111-1111-111111111111","name":"Moved",
                "match":{"path":"/old","pathMode":0},"target":"/new","status":{{{status}}},"queryMode":"Preserve"}]}
                """;
            var rewrites = $$$"""
                {"expectedRevision":0,"rules":[{"id":"22222222-2222-2222-2222-222222222222","name":"Backend",
                "clusterId":"33333333-3333-3333-3333-333333333333","match":{"path":"/{**rest}"},
                "transforms":[{"kind":"path","operation":{{{operation}}},"value":"/api"}]}],
                "clusters":[{"id":"33333333-3333-3333-3333-333333333333","name":"Backend",
                "destination":{"name":"backend","address":"https://backend.example/"}}]}
                """;
            foreach (var (engine, body) in new[] { ("redirects", redirects), ("rewrites", rewrites) })
            {
                var response = await client.PutAsync(engine, new StringContent(body, Encoding.UTF8, "application/json"));
                Assert.Equal(expected, response.StatusCode);
                var snapshot = JsonNode.Parse(await client.GetStringAsync(engine))!;
                Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, snapshot["revision"]!.GetValue<int>());
                if (expected == HttpStatusCode.OK)
                {
                    // GET follows the host's string-enum settings; its rules must remain valid PUT input.
                    var replacement = new JsonObject
                    {
                        ["expectedRevision"] = snapshot["revision"]!.DeepClone(),
                        ["rules"] = snapshot["rules"]!.DeepClone()
                    };
                    if (engine == "rewrites")
                    {
                        replacement["clusters"] = snapshot["clusters"]!.DeepClone();
                    }

                    Assert.Equal(HttpStatusCode.OK, (await client.PutAsync(engine,
                        new StringContent(replacement.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
                }
            }

            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(true, "/mounted")]
    public async Task Api_is_opt_in_and_manages_both_engines_without_cookies(bool enabled, string pathBase)
    {
        var directory = Directory.CreateTempSubdirectory("newheap-api-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                options.Administrator.UserName = "api-admin";
                options.Administrator.Password = "test:password";
                options.Administrator.LoginAttemptLimit = 1;
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.UsePathBase(pathBase);
            if (enabled)
            {
                app.MapProxyEndpoints();
            }

            app.UseNewHeapProxy();
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(app.Urls.Single() + pathBase + NhProxyOptions.ApiPath + "/")
            };
            if (!enabled)
            {
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("redirects")).StatusCode);
                return;
            }

            var anonymous = await client.GetAsync("redirects");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("Basic", Assert.Single(anonymous.Headers.WwwAuthenticate).Scheme);
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:test:password");
            var initial = await client.GetAsync("redirects");
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            Assert.False(initial.Headers.Contains("Set-Cookie"));
            Assert.True(initial.Headers.CacheControl!.NoStore);
            Assert.Equal(0, (await initial.Content.ReadFromJsonAsync<NhProxyRedirectConfiguration>())!.Revision);

            var rule = new NhProxyRedirectRule { Id = Guid.NewGuid(), Name = "Moved", Match = new() { Path = "/old" }, Target = "/new" };
            var draft = await client.PostAsJsonAsync("redirects/test", new NhProxyRedirectTestRequest(new(0, 0), rule,
                new NhProxyTestRequest { Url = new Uri("https://public.example/old") }));
            Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
            Assert.Null(draft.Headers.Location);
            Assert.Equal("/new", (await draft.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxyRuleTestResult>>())!.Data!.Redirect!.Location);
            Assert.Equal(0, (await client.GetFromJsonAsync<NhProxyRedirectConfiguration>("redirects"))!.Revision);

            var saved = await client.PutAsJsonAsync("redirects", new NhProxyRedirectSaveRequest(0, [rule]));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(1, (await saved.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxySaveResult>>())!.Data!.SavedRevision);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("redirects", new NhProxyRedirectSaveRequest(0, []))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("redirects", new NhProxyRedirectSaveRequest(1, [rule with { Enabled = false }]))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("redirects", new NhProxyRedirectSaveRequest(2, []))).StatusCode);

            var cluster = new NhProxyCluster { Id = Guid.NewGuid(), Name = "Backend", Destination = new("backend", new Uri("https://backend.example/")) };
            var rewrite = new NhProxyRewriteRule { Id = Guid.NewGuid(), Name = "Catch all", ClusterId = cluster.Id, Priority = int.MinValue, Match = new() { Path = "/{**rest}" } };
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("rewrites", new NhProxyRewriteSaveRequest(0, [rewrite], [cluster]))).StatusCode);
            var preview = await client.PostAsJsonAsync("test", new NhProxySavedTestRequest(new(1, 3), new() { Url = new Uri("https://public.example/projects") }));
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal("https://backend.example/projects", (await preview.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxyRuleTestResult>>())!.Data!.Rewrite!.TargetUrl.AbsoluteUri);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("unknown")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("rewrites/activate", null)).StatusCode);
            Assert.Equal(1, (await client.GetFromJsonAsync<NhProxyRewriteConfiguration>("rewrites"))!.Revision);
            Assert.Equal(3, (await client.GetFromJsonAsync<NhProxyStatus>("status"))!.Redirect.ActiveRevision);

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("redirects", new StringContent("{\"rules\":[]}", Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("redirects", new StringContent("{\"expectedRevision\":3,\"rules\":[],\"unknown\":true}", Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PutAsync("redirects", new StringContent("{}"))).StatusCode);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PutAsync("redirects", new StringContent(new string(' ', 65537), Encoding.UTF8, "application/json"))).StatusCode);
            client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("redirects/activate", null)).StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("redirects")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "invalid-base64");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("redirects")).StatusCode);
            Assert.Equal(3, (await app.Services.GetRequiredService<INhProxyConfigurationService>().GetRedirectsAsync()).Revision);
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:test:password");
            var auditResponse = await client.GetAsync("audit");
            var auditJson = await auditResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("test:password", auditJson);
            Assert.DoesNotContain("Authorization", auditJson);
            var audit = (await auditResponse.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxyChangeAuditPage>>())!.Data!;
            Assert.Equal(5, audit.TotalCount);
            Assert.All(audit.Items, entry =>
            {
                Assert.Equal("api-admin", entry.Actor.UserName);
                Assert.Equal("127.0.0.1", entry.Actor.ClientIp);
                Assert.False(string.IsNullOrWhiteSpace(entry.Actor.CorrelationId));
            });
            Assert.Equal(NhProxyChangeAuditAction.ActivationRequested, audit.Items[0].Action);
            Assert.Null(audit.Items[0].Revision);
            var disabled = (await client.GetFromJsonAsync<NhProxyApiResult<NhProxyChangeAuditPage>>("audit?engine=Redirect&pageSize=1&offset=1"))!.Data!;
            Assert.Equal(3, disabled.TotalCount);
            var changed = Assert.Single(disabled.Items);
            Assert.Equal(1, changed.PreviousRevision);
            Assert.Equal(2, changed.Revision);
            Assert.True(changed.Before!.Value.GetProperty("rules")[0].GetProperty("enabled").GetBoolean());
            Assert.False(changed.After!.Value.GetProperty("rules")[0].GetProperty("enabled").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("audit?pageSize=101")).StatusCode);
            Assert.Equal(0, (await app.Services.GetRequiredService<INhProxyLoginAuditStore>().QueryAsync(new())).Data!.TotalCount);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("Production", false, 100, false, HttpStatusCode.Forbidden)]
    [InlineData("Development", true, 100, false, HttpStatusCode.Forbidden)]
    [InlineData("Development", false, 1, false, HttpStatusCode.OK)]
    [InlineData("Development", false, 100, true, HttpStatusCode.OK)]
    public async Task Api_enforces_access_without_using_login_limits_or_login_audit(string environment, bool denyIp, int limit, bool failAudit, HttpStatusCode expected)
    {
        var directory = Directory.CreateTempSubdirectory("newheap-api-security-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                options.Administrator.UserName = "api-admin";
                options.Administrator.Password = "password";
                options.Administrator.LoginAttemptLimit = limit;
                options.IpAllowlist.Enabled = denyIp;
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            if (failAudit)
            {
                builder.Services.AddSingleton<INhProxyLoginAuditStore, FailingAudit>();
            }

            await using var app = builder.Build();
            app.MapProxyEndpoints().UseNewHeapProxy();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:password");
            await client.GetAsync(NhProxyOptions.ApiPath + "/status");
            var response = await client.GetAsync(NhProxyOptions.ApiPath + "/status");
            Assert.Equal(expected, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.DoesNotContain("private diagnostic", await response.Content.ReadAsStringAsync());
            var configuration = app.Services.GetRequiredService<INhProxyConfigurationService>();
            Assert.True((await configuration.SaveRedirectsAsync(new(0,
                [new NhProxyRedirectRule { Id = Guid.NewGuid(), Name = "Moved", Match = new() { Path = "/old" }, Target = "/new" }]))).Success);
            using var traffic = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.Found, (await traffic.GetAsync(app.Urls.Single() + "/old")).StatusCode);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static AuthenticationHeaderValue Basic(string credentials) => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));

    [Fact]
    public async Task Committed_activation_failure_remains_visible_and_retry_does_not_increment_revision()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-api-retry-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                options.Administrator.UserName = "api-admin";
                options.Administrator.Password = "password";
                options.Administrator.LoginAttemptLimit = 30;
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            builder.Services.AddSingleton<INhProxyRuntime>(services => new FailingRuntime(services.GetRequiredService<NhProxyRuntime>()));
            await using var app = builder.Build();
            app.MapProxyEndpoints().UseNewHeapProxy();
            await app.StartAsync();
            var runtime = (FailingRuntime)app.Services.GetRequiredService<INhProxyRuntime>();
            runtime.Fail = true;
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + NhProxyOptions.ApiPath + "/") };
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:password");
            var response = await client.PutAsJsonAsync("redirects", new NhProxyRedirectSaveRequest(0, []));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxySaveResult>>())!;
            Assert.False(result.Success);
            Assert.Equal(1, result.Data!.SavedRevision);
            Assert.Equal(0, result.Data.Activation.ActiveRevision);
            Assert.Equal(NhProxyErrorCodes.ActivationFailed, Assert.Single(result.Issues).Code);
            Assert.Equal(1, (await client.GetFromJsonAsync<NhProxyRedirectConfiguration>("redirects"))!.Revision);
            runtime.Fail = false;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("redirects/activate", null)).StatusCode);
            Assert.Equal(1, (await client.GetFromJsonAsync<NhProxyStatus>("status"))!.Redirect.ActiveRevision);
            var audit = (await client.GetFromJsonAsync<NhProxyApiResult<NhProxyChangeAuditPage>>("audit"))!.Data!;
            Assert.Equal(2, audit.TotalCount);
            var committed = Assert.Single(audit.Items.Where(entry => entry.Action == NhProxyChangeAuditAction.ConfigurationSaved));
            Assert.Equal(1, committed.Revision);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Bad_api_credentials_have_a_separate_budget_and_do_not_create_logins()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-api-auth-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                options.Administrator.UserName = "api-admin";
                options.Administrator.Password = "password";
                options.Administrator.LoginAttemptLimit = 1;
                options.Administrator.ApiAuthenticationFailureLimit = 1;
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.MapProxyEndpoints().UseNewHeapProxy();
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:password");
            for (var index = 0; index < 6; index++)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(NhProxyOptions.ApiPath + "/status")).StatusCode);
            }

            client.DefaultRequestHeaders.Authorization = Basic("api-admin:incorrect");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(NhProxyOptions.ApiPath + "/status")).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(NhProxyOptions.ApiPath + "/status")).StatusCode);
            Assert.Equal(0, (await app.Services.GetRequiredService<INhProxyLoginAuditStore>().QueryAsync(new())).Data!.TotalCount);

            var login = await client.GetStringAsync("/newheap-proxy/Login");
            var token = System.Text.RegularExpressions.Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(token);
            var signedIn = await client.PostAsync("/newheap-proxy/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token), ["UserName"] = "api-admin", ["Password"] = "password"
            }));
            Assert.Equal(HttpStatusCode.Found, signedIn.StatusCode);
            Assert.Equal(1, (await app.Services.GetRequiredService<INhProxyLoginAuditStore>().QueryAsync(new())).Data!.TotalCount);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Audit_insert_failure_rolls_back_both_engines_and_blocks_activation_retry()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-api-atomic-audit-");
        try
        {
            var database = Path.Combine(directory.FullName, "proxy.db");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                options.Administrator.UserName = "api-admin";
                options.Administrator.Password = "password";
            }, storage => storage.DatabasePath = database);
            await using var app = builder.Build();
            app.MapProxyEndpoints().UseNewHeapProxy();
            await app.StartAsync();
            await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER RejectAudit BEFORE INSERT ON NhProxyChangeAudit BEGIN SELECT RAISE(ABORT, 'private audit failure'); END;";
            await command.ExecuteNonQueryAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + NhProxyOptions.ApiPath + "/") };
            client.DefaultRequestHeaders.Authorization = Basic("api-admin:password");
            var failedSave = await client.PutAsJsonAsync("redirects", new NhProxyRedirectSaveRequest(0, []));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failedSave.StatusCode);
            Assert.DoesNotContain("private audit failure", await failedSave.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PutAsJsonAsync("rewrites", new NhProxyRewriteSaveRequest(0, [], []))).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("redirects/activate", null)).StatusCode);
            Assert.Equal(0, (await client.GetFromJsonAsync<NhProxyRedirectConfiguration>("redirects"))!.Revision);
            Assert.Equal(0, (await client.GetFromJsonAsync<NhProxyRewriteConfiguration>("rewrites"))!.Revision);
            var status = (await client.GetFromJsonAsync<NhProxyStatus>("status"))!;
            Assert.Equal(0, status.Redirect.ActiveRevision);
            Assert.Equal(0, status.Rewrite.ActiveRevision);
            Assert.Equal(0, (await client.GetFromJsonAsync<NhProxyApiResult<NhProxyChangeAuditPage>>("audit"))!.Data!.TotalCount);
            command.CommandText = "DROP TRIGGER RejectAudit;";
            await command.ExecuteNonQueryAsync();
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("redirects", new NhProxyRedirectSaveRequest(0, []))).StatusCode);
            Assert.Equal(1, (await client.GetFromJsonAsync<NhProxyApiResult<NhProxyChangeAuditPage>>("audit"))!.Data!.TotalCount);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Schema_upgrade_keeps_old_configuration_and_login_audit_and_new_change_audit_survives_restart()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-change-audit-upgrade-");
        try
        {
            var database = Path.Combine(directory.FullName, "proxy.db");
            var options = Options.Create(new NhProxySqliteOptions { DatabasePath = database });
            await using (var original = new NhProxySqliteConfigurationStore(options))
            {
                await original.InitializeAsync();
                Assert.True((await original.SaveRedirectsAsync(new(0, []))).Success);
                await new NhProxySqliteLoginAuditStore(options).AppendAsync(new NhProxyLoginAuditEvent
                {
                    Id = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow, Outcome = NhProxyLoginOutcome.Success,
                    AuthenticatedUserName = "administrator", CorrelationId = "original-login"
                });
            }

            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE NhProxyChangeAudit; PRAGMA user_version=3;";
                await command.ExecuteNonQueryAsync();
            }

            var actor = new NhProxyChangeAuditContext("automation", "192.0.2.1", "sample-change");
            await using (var upgraded = new NhProxySqliteConfigurationStore(options))
            {
                await upgraded.InitializeAsync();
                Assert.Equal(1, (await upgraded.LoadRedirectsAsync()).Revision);
                Assert.Equal(1, (await new NhProxySqliteLoginAuditStore(options).QueryAsync(new())).Data!.TotalCount);
                Assert.True((await upgraded.SaveRedirectsAsync(new(1, []) { Audit = actor })).Success);
            }

            await using var restarted = new NhProxySqliteConfigurationStore(options);
            await restarted.InitializeAsync();
            var audit = new NhProxySqliteChangeAuditStore(options);
            var entry = Assert.Single((await audit.QueryAsync(new())).Data!.Items);
            Assert.Equal(actor, entry.Actor);
            Assert.Equal(1, entry.Before!.Value.GetProperty("revision").GetInt64());
            Assert.Equal(2, entry.After!.Value.GetProperty("revision").GetInt64());
            Assert.Equal(0, (await audit.QueryAsync(new() { FromUtc = DateTimeOffset.UtcNow.AddDays(1) })).Data!.TotalCount);
            Assert.False((await audit.QueryAsync(new() { Offset = -1 })).Success);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class FailingRuntime(INhProxyRuntime inner) : INhProxyRuntime
    {
        public bool Fail { get; set; }
        public NhProxyStatus GetStatus() => inner.GetStatus();
        public Task<TaskResult<NhProxyEngineStatus>> PublishRewritesAsync(NhProxyRewriteConfiguration configuration, CancellationToken cancellationToken = default) =>
            inner.PublishRewritesAsync(configuration, cancellationToken);
        public Task<TaskResult<NhProxyEngineStatus>> PublishRedirectsAsync(NhProxyRedirectConfiguration configuration, CancellationToken cancellationToken = default) =>
            Fail ? Task.FromResult(TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.ActivationFailed, NhProxyErrorCodes.ActivationFailed))
                : inner.PublishRedirectsAsync(configuration, cancellationToken);
    }

    private sealed class FailingAudit : INhProxyLoginAuditStore
    {
        public Task AppendAsync(NhProxyLoginAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            throw new IOException("private diagnostic");
        public Task<TaskResult<NhProxyLoginAuditPage>> QueryAsync(NhProxyLoginAuditQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
