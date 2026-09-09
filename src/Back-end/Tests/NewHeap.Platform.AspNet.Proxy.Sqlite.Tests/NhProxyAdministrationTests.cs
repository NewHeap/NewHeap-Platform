using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite.Tests;

public sealed class NhProxyAdministrationTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("/mounted", false)]
    [InlineData("", true)]
    [InlineData("/mounted", true)]
    public async Task Panel_authenticates_tests_saves_conflicts_and_deletes_without_restarting(string pathBase, bool isRegex)
    {
        var directory = Directory.CreateTempSubdirectory("newheap-panel-test-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                options.Administrator.UserName = "administrator";
                options.Administrator.PasswordHash = new PasswordHasher<string>().HashPassword("administrator", "test-only-password");
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            if (pathBase.Length > 0)
            {
                app.UsePathBase(pathBase);
            }

            app.UseNewHeapProxy();
            app.MapFallback(() => "host-fallback");
            await app.StartAsync();
            try
            {
                var root = app.Urls.Single() + pathBase;
                var panel = root + "/newheap-proxy";
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                var anonymous = await client.GetAsync(panel);
                Assert.Equal(HttpStatusCode.Found, anonymous.StatusCode);
                Assert.Equal(pathBase + "/newheap-proxy/Login", anonymous.Headers.Location!.OriginalString);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(panel + "/unknown")).StatusCode);
                Assert.Equal("host-fallback", await client.GetStringAsync(root + "/unmatched"));
                Assert.Null(await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync());

                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(panel + "/Login", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["UserName"] = "administrator", ["Password"] = "test-only-password"
                }))).StatusCode);
                var login = await client.GetStringAsync(panel + "/Login");
                var failed = await client.PostAsync(panel + "/Login", Form(login, new() { ["UserName"] = "unknown", ["Password"] = "incorrect" }));
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
                var signedIn = await client.PostAsync(panel + "/Login", Form(login, new() { ["UserName"] = "administrator", ["Password"] = "test-only-password" }));
                Assert.Equal(HttpStatusCode.Found, signedIn.StatusCode);
                Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("NewHeapProxy.Session=") && cookie.Contains("path=" + pathBase + "/newheap-proxy") && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
                var list = await client.GetStringAsync(panel);
                Assert.Contains("Your first redirect starts here", list);
                var edit = await client.GetStringAsync(panel + "/Edit");
                var id = Field(edit, "Id");
                var values = new Dictionary<string, string>
                {
                    ["Id"] = id, ["Revision"] = "0", ["IsNew"] = "true", ["Name"] = "Moved <project>",
                    ["Path"] = "/old", ["Target"] = "/new?source=proxy", ["Enabled"] = "true",
                    ["Status"] = "302", ["QueryMode"] = "Preserve", ["Priority"] = "0",
                    ["TestUrl"] = "https://example.com/old?campaign=sample", ["TestMethod"] = "GET", ["operation"] = "test"
                };
                if (isRegex)
                {
                    values["IsRegex"] = "true";
                    values["Path"] = @"^/old/([^?]+)(\?.*)?$";
                    values["Target"] = "/new/$1$2";
                    values["TestUrl"] = "https://example.com/old/42?campaign=sample";
                    var invalidValues = new Dictionary<string, string>(values) { ["Path"] = "(" };
                    var invalid = await client.PostAsync(panel + "/Edit/" + id, Form(edit, invalidValues));
                    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                    Assert.Contains("valid .NET regex", await invalid.Content.ReadAsStringAsync());
                    var unsafeValues = new Dictionary<string, string>(values)
                    {
                        ["Target"] = "/$1", ["TestUrl"] = "https://example.com/old//evil.example"
                    };
                    var unsafePreview = await client.PostAsync(panel + "/Edit/" + id, Form(edit, unsafeValues));
                    Assert.Equal(HttpStatusCode.OK, unsafePreview.StatusCode);
                    Assert.Contains("invalid destination", await unsafePreview.Content.ReadAsStringAsync());
                }

                var tested = await client.PostAsync(panel + "/Edit/" + id, Form(edit, values));
                var testedHtml = await tested.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
                Assert.Contains("Match: 302", testedHtml);
                Assert.Contains("campaign=sample", testedHtml);
                Assert.Empty((await app.Services.GetRequiredService<INhProxyConfigurationService>().GetRedirectsAsync()).Rules);
                values["operation"] = "save";
                var saved = await client.PostAsync(panel + "/Edit/" + id, Form(edit, values));
                Assert.Equal(HttpStatusCode.Found, saved.StatusCode);
                var redirected = await client.GetAsync(root + (isRegex ? "/old/42?campaign=sample" : "/old?campaign=sample"));
                Assert.Equal(HttpStatusCode.Found, redirected.StatusCode);
                Assert.Equal(isRegex ? "/new/42?campaign=sample" : "/new?campaign=sample&source=proxy", redirected.Headers.Location!.OriginalString);
                if (isRegex)
                {
                    var reopened = await client.GetStringAsync(panel + "/Edit/" + id);
                    Assert.Matches("<input(?=[^>]*name=\"IsRegex\")(?=[^>]*checked)[^>]*>", reopened);
                    Assert.Equal(NhProxyRedirectPathMatchMode.Regex,
                        Assert.Single((await app.Services.GetRequiredService<INhProxyConfigurationService>().GetRedirectsAsync()).Rules).Match.PathMode);
                }
                Assert.Contains("Moved &lt;project&gt;", await client.GetStringAsync(panel));
                var stale = await client.PostAsync(panel + "/Edit/" + id, Form(edit, values));
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                var delete = await client.GetStringAsync(panel + "/Delete/" + id);
                Assert.Equal(HttpStatusCode.Found, (await client.PostAsync(panel + "/Delete/" + id, Form(delete, new() { ["revision"] = "1" }))).StatusCode);
                Assert.Equal("host-fallback", await client.GetStringAsync(root + "/old"));
                var events = await app.Services.GetRequiredService<INhProxyLoginAuditStore>().QueryAsync(new());
                Assert.True(events.Success);
                Assert.Equal(2, events.Data!.TotalCount);
                Assert.Contains(events.Data.Items, item => item.Outcome == NhProxyLoginOutcome.InvalidCredentials && item.AuthenticatedUserName is null && item.ClientIp == "127.0.0.1");
                Assert.Contains("Login activity", await client.GetStringAsync(panel + "/Activity"));
                Assert.Equal(HttpStatusCode.Found, (await client.PostAsync(panel + "/Logout", Form(list, new()))).StatusCode);
                Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(panel)).StatusCode);
                login = await client.GetStringAsync(panel + "/Login");
                Assert.Equal(HttpStatusCode.Found, (await client.PostAsync(panel + "/Login", Form(login, new() { ["UserName"] = "administrator", ["Password"] = "test-only-password" }))).StatusCode);
                app.Services.GetRequiredService<IOptions<NhProxyOptions>>().Value.Administrator.CredentialVersion = "rotated";
                Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(panel)).StatusCode);
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Host_implicit_authentication_default_is_preserved()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication().AddCookie("HostCookie");
        builder.Services.AddNewHeapProxy();
        await using var app = builder.Build();
        Assert.Equal("HostCookie", (await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync())!.Name);
    }

    [Theory]
    [InlineData("missing-account", 503)]
    [InlineData("https-required", 403)]
    [InlineData("ip-denied", 403)]
    [InlineData("empty-allowlist", 403)]
    [InlineData("throttled", 429)]
    [InlineData("audit-failure", 503)]
    public async Task Security_failures_never_issue_an_administrator_session(string scenario, int expectedStatus)
    {
        var directory = Directory.CreateTempSubdirectory("newheap-panel-security-");
        try
        {
            var database = Path.Combine(directory.FullName, "proxy.db");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = scenario == "https-required" ? "Production" : "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                if (scenario != "missing-account")
                {
                    options.Administrator.UserName = "administrator";
                    options.Administrator.PasswordHash = new PasswordHasher<string>().HashPassword("administrator", "test-only-password");
                }

                options.Administrator.LoginAttemptLimit = 1;
                options.IpAllowlist.Enabled = scenario is "ip-denied" or "empty-allowlist";
                options.IpAllowlist.Entries = scenario == "ip-denied" ? ["192.0.2.0/24"] : [];
            }, storage => storage.DatabasePath = database);
            await using var app = builder.Build();
            app.UseNewHeapProxy();
            await app.StartAsync();
            try
            {
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
                HttpResponseMessage response;
                if (scenario is "throttled" or "audit-failure")
                {
                    var login = await client.GetStringAsync("/newheap-proxy/Login");
                    if (scenario == "audit-failure")
                    {
                        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False");
                        await connection.OpenAsync();
                        using var command = connection.CreateCommand();
                        command.CommandText = "DROP TABLE NhProxyLoginAudit;";
                        await command.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/newheap-proxy/Login", Form(login, new() { ["UserName"] = "administrator", ["Password"] = "incorrect" }))).StatusCode);
                    }

                    response = await client.PostAsync("/newheap-proxy/Login", Form(login, new() { ["UserName"] = "administrator", ["Password"] = "test-only-password" }));
                }
                else
                {
                    response = await client.PostAsync("/newheap-proxy/Login", new FormUrlEncodedContent(new Dictionary<string, string>()));
                }

                Assert.Equal(expectedStatus, (int)response.StatusCode);
                Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(cookie => cookie.StartsWith("NewHeapProxy.Session=")));
                if (scenario is "ip-denied" or "empty-allowlist" or "throttled")
                {
                    var audit = await app.Services.GetRequiredService<INhProxyLoginAuditStore>().QueryAsync(new());
                    Assert.True(audit.Success);
                    Assert.Contains(audit.Data!.Items, item => item.Outcome == (scenario == "throttled" ? NhProxyLoginOutcome.Throttled : NhProxyLoginOutcome.IpDenied));
                }
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Audit_is_idempotent_paged_filtered_and_retained_after_schema_upgrade()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-panel-upgrade-");
        try
        {
            var database = Path.Combine(directory.FullName, "proxy.db");
            var options = Options.Create(new NhProxySqliteOptions { DatabasePath = database });
            await using (var original = new NhProxySqliteConfigurationStore(options))
            {
                await original.InitializeAsync();
                Assert.True((await original.SaveRedirectsAsync(new(0, [new NhProxyRedirectRule
                {
                    Id = Guid.NewGuid(), Name = "Existing rule", Match = new NhProxyRedirectMatch { Path = "/old" }, Target = "/new"
                }]))).Success);
            }

            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE NhProxyLoginAudit; PRAGMA user_version=1;";
                await command.ExecuteNonQueryAsync();
            }

            await using var upgraded = new NhProxySqliteConfigurationStore(options);
            await upgraded.InitializeAsync();
            Assert.Equal("Existing rule", Assert.Single((await upgraded.LoadRedirectsAsync()).Rules).Name);
            var audit = new NhProxySqliteLoginAuditStore(options);
            var earlier = new NhProxyLoginAuditEvent
            {
                Id = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow.AddDays(-100), Outcome = NhProxyLoginOutcome.InvalidCredentials,
                CorrelationId = "sample-event", ClientIp = "192.0.2.1"
            };
            await audit.AppendAsync(earlier);
            await audit.AppendAsync(earlier);
            await audit.AppendAsync(earlier with { Id = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow, Outcome = NhProxyLoginOutcome.Success });
            var page = await audit.QueryAsync(new NhProxyLoginAuditQuery { PageSize = 1 });
            Assert.True(page.Success);
            Assert.Equal(2, page.Data!.TotalCount);
            Assert.Equal(NhProxyLoginOutcome.Success, Assert.Single(page.Data.Items).Outcome);
            var filtered = await audit.QueryAsync(new NhProxyLoginAuditQuery { Outcome = NhProxyLoginOutcome.InvalidCredentials, ClientIp = "192.0.2.1" });
            Assert.Equal(earlier.Id, Assert.Single(filtered.Data!.Items).Id);
            Assert.Equal(1, await audit.DeleteExpiredAsync(DateTimeOffset.UtcNow.AddDays(-90), 1));
            Assert.False((await audit.QueryAsync(new NhProxyLoginAuditQuery { PageSize = 101 })).Success);
            Assert.Equal(1, (await audit.QueryAsync(new())).Data!.TotalCount);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static FormUrlEncodedContent Form(string html, Dictionary<string, string> values)
    {
        values["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken");
        return new FormUrlEncodedContent(values);
    }

    [Fact]
    public async Task Activation_failure_preserves_the_saved_revision_and_can_be_retried()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-panel-activation-");
        try
        {
            await using var store = new NhProxySqliteConfigurationStore(Options.Create(new NhProxySqliteOptions { DatabasePath = Path.Combine(directory.FullName, "proxy.db") }));
            await store.InitializeAsync();
            var runtime = new RecoverableRuntime();
            var configuration = new NhProxyConfigurationService(store, runtime, NullLogger<NhProxyConfigurationService>.Instance);
            var result = await configuration.SaveRedirectsAsync(new(0, []));
            Assert.False(result.Success);
            Assert.Equal(1, result.Data!.SavedRevision);
            Assert.Equal(1, (await store.LoadRedirectsAsync()).Revision);
            Assert.Null(configuration.GetStatus().Redirect.ActiveRevision);
            runtime.Fail = false;
            Assert.True((await configuration.RetryActivationAsync(NhProxyEngine.Redirect)).Success);
            Assert.Equal(1, configuration.GetStatus().Redirect.ActiveRevision);
            Assert.True((await configuration.RetryActivationAsync(NhProxyEngine.Redirect)).Success);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Every_panel_action_has_documented_responses_and_authorization_intent()
    {
        var controller = typeof(NhProxyAdminController);
        Assert.NotEmpty(controller.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true));
        foreach (var method in controller.GetMethods().Where(method => method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true).Length != 0))
        {
            Assert.NotEmpty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Http.EndpointSummaryAttribute), true));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Http.EndpointDescriptionAttribute), true));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute), true));
        }
    }

    private sealed class RecoverableRuntime : INhProxyRuntime
    {
        private readonly NhProxyRuntime _runtime = new(new NhProxyConfigurationValidator(Options.Create(new NhProxyOptions())));
        public bool Fail { get; set; } = true;
        public NhProxyStatus GetStatus() => _runtime.GetStatus();
        public Task<TaskResult<NhProxyEngineStatus>> PublishRewritesAsync(NhProxyRewriteConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<TaskResult<NhProxyEngineStatus>> PublishRedirectsAsync(NhProxyRedirectConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            if (Fail)
            {
                return Task.FromResult(TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.ActivationFailed, NhProxyErrorCodes.ActivationFailed));
            }

            return _runtime.PublishRedirectsAsync(configuration, cancellationToken);
        }
    }

    private static string Field(string html, string name)
    {
        var match = Regex.Match(html, "<input[^>]*name=\"" + name + "\"[^>]*value=\"([^\"]*)\"");
        Assert.True(match.Success, "Expected form field: " + name + "\n" + html);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
