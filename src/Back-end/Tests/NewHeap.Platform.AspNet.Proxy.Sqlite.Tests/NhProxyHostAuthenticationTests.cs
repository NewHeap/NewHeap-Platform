using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite.Tests;

public sealed class NhProxyHostAuthenticationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Panel_and_api_migrate_independently_without_accepting_other_identities(bool customPanel, bool customApi)
    {
        var directory = Directory.CreateTempSubdirectory("newheap-host-auth-");
        try
        {
            var builder = CreateBuilder();
            builder.Services.AddNewHeapProxy(options => Configure(options, customPanel, customApi),
                storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.MapProxyEndpoints().UseNewHeapProxy();
            app.MapGet("/host", (HttpContext context) => context.User.Identity!.Name).RequireAuthorization();
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };

            var anonymousPanel = await client.GetAsync(NhProxyOptions.AdministrationPath);
            Assert.Equal(HttpStatusCode.Found, anonymousPanel.StatusCode);
            Assert.Equal(customPanel ? "/host-login" : "/newheap-proxy/Login", anonymousPanel.Headers.Location!.OriginalString);
            var anonymousApi = await client.GetAsync(NhProxyOptions.ApiPath + "/status");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousApi.StatusCode);
            Assert.Null(anonymousApi.Headers.Location);

            client.DefaultRequestHeaders.Add("X-Test-Host", "admin");
            Assert.Equal("Host", await client.GetStringAsync("/host"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(NhProxyOptions.ApiPath + "/status")).StatusCode);
            Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(NhProxyOptions.AdministrationPath)).StatusCode);
            Assert.Equal("Host", (await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync())!.Name);

            if (customPanel)
            {
                client.DefaultRequestHeaders.Add("X-Test-Panel", "reader");
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(NhProxyOptions.AdministrationPath)).StatusCode);
                client.DefaultRequestHeaders.Remove("X-Test-Panel");
                client.DefaultRequestHeaders.Add("X-Test-Panel", "admin");
            }
            else
            {
                var login = await client.GetStringAsync(NhProxyOptions.AdministrationPath + "/Login");
                Assert.Equal(HttpStatusCode.Found, (await client.PostAsync(NhProxyOptions.AdministrationPath + "/Login",
                    Form(login, new() { ["UserName"] = "administrator", ["Password"] = "test-password" }))).StatusCode);
            }

            var panel = await client.GetStringAsync(NhProxyOptions.AdministrationPath);
            Assert.Contains("Your first redirect starts here", panel);
            // A panel identity (including its real cookie) must never grant API access.
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(NhProxyOptions.ApiPath + "/status")).StatusCode);

            if (customApi)
            {
                client.DefaultRequestHeaders.Add("X-Test-Api", "reader");
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(NhProxyOptions.ApiPath + "/status")).StatusCode);
                client.DefaultRequestHeaders.Remove("X-Test-Api");
                client.DefaultRequestHeaders.Add("X-Test-Api", "admin");
            }
            else
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("administrator:test-password")));
            }

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(NhProxyOptions.ApiPath + "/status")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(NhProxyOptions.ApiPath + "/redirects", new NhProxyRedirectSaveRequest(0, []))).StatusCode);
            var audit = await app.Services.GetRequiredService<INhProxyChangeAuditStore>().QueryAsync(new());
            Assert.Equal(customApi ? "Api-id" : "administrator", Assert.Single(audit.Data!.Items).Actor.UserName);

            if (customPanel)
            {
                Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(NhProxyOptions.AdministrationPath + "/Login",
                    Form(panel, new() { ["UserName"] = "administrator", ["Password"] = "test-password" }))).StatusCode);
                Assert.Equal(0, (await app.Services.GetRequiredService<INhProxyLoginAuditStore>().QueryAsync(new())).Data!.TotalCount);
                var logout = await client.PostAsync(NhProxyOptions.AdministrationPath + "/Logout", Form(panel, new()));
                Assert.Equal(HttpStatusCode.Found, logout.StatusCode);
                Assert.Equal("/host-signed-out", logout.Headers.Location!.OriginalString);
            }

            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("scheme")]
    [InlineData("policy")]
    [InlineData("different-policy-scheme")]
    [InlineData("challenge")]
    [InlineData("sign-out")]
    public async Task Invalid_host_registrations_fail_before_serving_requests(string invalid)
    {
        var builder = CreateBuilder();
        builder.Services.AddAuthorization(options => options.AddPolicy("OtherScheme", policy =>
            policy.AddAuthenticationSchemes("Host").RequireAuthenticatedUser()));
        builder.Services.AddNewHeapProxy(options =>
        {
            Configure(options, true, true);
            var panel = options.AdministrationAuthentication!;
            if (invalid == "scheme")
            {
                panel.AuthenticationScheme = "Missing";
            }
            if (invalid == "policy")
            {
                panel.AuthorizationPolicy = "Missing";
            }
            if (invalid == "different-policy-scheme")
            {
                panel.AuthorizationPolicy = "OtherScheme";
            }
            if (invalid == "challenge")
            {
                panel.ChallengeScheme = "Missing";
            }
            if (invalid == "sign-out")
            {
                panel.SignOutScheme = "Missing";
            }
        });
        await using var app = builder.Build();
        app.MapProxyEndpoints().UseNewHeapProxy();
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
    }

    [Theory]
    [InlineData("Development", true, false)]
    [InlineData("Production", false, false)]
    [InlineData("Development", false, true)]
    public async Task Custom_authentication_preserves_ip_https_and_origin_boundaries(string environment, bool denyIp, bool origin)
    {
        var directory = Directory.CreateTempSubdirectory("newheap-host-boundaries-");
        try
        {
            var builder = CreateBuilder(environment);
            builder.Services.AddNewHeapProxy(options =>
            {
                Configure(options, true, true);
                options.IpAllowlist.Enabled = denyIp;
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.MapProxyEndpoints().UseNewHeapProxy();
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-Test-Api", "admin");
            client.DefaultRequestHeaders.Add("X-Test-Panel", "admin");
            if (origin)
            {
                client.DefaultRequestHeaders.Add("Origin", "https://example.com");
            }

            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(NhProxyOptions.ApiPath + "/status")).StatusCode);
            if (!origin)
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(NhProxyOptions.AdministrationPath)).StatusCode);
            }

            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static WebApplicationBuilder CreateBuilder(string environment = "Development")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthentication("Host")
            .AddScheme<AuthenticationSchemeOptions, HostHandler>("Host", _ => { })
            .AddScheme<AuthenticationSchemeOptions, HostHandler>("Panel", _ => { })
            .AddScheme<AuthenticationSchemeOptions, HostHandler>("Api", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("Administrators", policy => policy.RequireRole("admin")));
        return builder;
    }

    private static void Configure(NhProxyOptions options, bool panel, bool api)
    {
        if (!panel || !api)
        {
            options.Administrator.UserName = "administrator";
            options.Administrator.Password = "test-password";
        }

        options.Administrator.ApiAuthenticationFailureLimit = 20;
        if (panel)
        {
            options.ConfigureAdministrationAuthentication(auth =>
            {
                auth.AuthenticationScheme = "Panel";
                auth.ChallengeScheme = "Panel";
                auth.SignOutScheme = "Panel";
                auth.AuthorizationPolicy = "Administrators";
            });
        }

        if (api)
        {
            options.ConfigureApiAuthentication(auth =>
            {
                auth.AuthenticationScheme = "Api";
                auth.AuthorizationPolicy = "Administrators";
            });
        }
    }

    private static FormUrlEncodedContent Form(string html, Dictionary<string, string> values)
    {
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(token);
        return new FormUrlEncodedContent(values);
    }

    private sealed class HostHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationSignOutHandler
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-" + Scheme.Name].ToString();
            if (role.Length == 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, Scheme.Name),
                new Claim(ClaimTypes.NameIdentifier, Scheme.Name + "-id"), new Claim(ClaimTypes.Role, role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/host-login");
            return Task.CompletedTask;
        }

        public Task SignOutAsync(AuthenticationProperties? properties)
        {
            Response.Redirect("/host-signed-out");
            return Task.CompletedTask;
        }
    }
}
