using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>SPM-238: host-owned ASP.NET cookies and bearer tokens protect independent proxy surfaces.</summary>
public sealed class ProxyHostAuthenticationSamplesTests
{
    private const string AdministrationScheme = "SampleSession";
    private const string ApiScheme = "SampleApi";

    [Fact]
    public async Task Host_cookie_and_bearer_handlers_authorize_the_panel_and_api_without_local_credentials()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-proxy-host-auth-sample-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddAuthentication()
                .AddCookie(AdministrationScheme, cookie => cookie.LoginPath = "/sample-login")
                .AddBearerToken(ApiScheme);
            builder.Services.AddAuthorization(authorization =>
            {
                authorization.AddPolicy("ProxyAdministrators", policy => policy.RequireRole("proxy-administrator"));
                authorization.AddPolicy("ProxyAutomation", policy => policy.RequireClaim("permission", "proxy.manage"));
            });
            builder.Services.AddNewHeapProxy(options =>
            {
                options.ConfigureAdministrationAuthentication(auth =>
                {
                    auth.AuthenticationScheme = AdministrationScheme;
                    auth.ChallengeScheme = AdministrationScheme;
                    auth.SignOutScheme = AdministrationScheme;
                    auth.AuthorizationPolicy = "ProxyAdministrators";
                });
                options.ConfigureApiAuthentication(auth =>
                {
                    auth.AuthenticationScheme = ApiScheme;
                    auth.AuthorizationPolicy = "ProxyAutomation";
                });
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));

            await using var app = builder.Build();
            // Host authentication must see external callbacks before either reserved proxy branch.
            app.UseAuthentication();
            app.MapProxyEndpoints().UseNewHeapProxy();
            // Test-host issuance only: a real host validates credentials/provider responses before SignInAsync.
            app.MapPost("/sample-session", async (HttpContext context) =>
            {
                await context.SignInAsync(AdministrationScheme, Principal("sample-administrator", new Claim(ClaimTypes.Role, "proxy-administrator")));
            }).AllowAnonymous();
            app.MapPost("/sample-token", async (HttpContext context) =>
            {
                await context.SignInAsync(ApiScheme, Principal("sample-automation", new Claim("permission", "proxy.manage")));
            }).AllowAnonymous();
            await app.StartAsync();

            using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
            var challenge = await browser.GetAsync(NhProxyOptions.AdministrationPath);
            Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
            Assert.Equal("/sample-login", challenge.Headers.Location!.AbsolutePath);
            await browser.PostAsync("/sample-session", null);
            var panel = await browser.GetStringAsync(NhProxyOptions.AdministrationPath);
            Assert.Contains("Sign out", panel);
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(NhProxyOptions.ApiPath + "/status")).StatusCode);

            using var automation = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = browser.BaseAddress };
            var issued = await automation.PostAsync("/sample-token", null);
            issued.EnsureSuccessStatusCode();
            var token = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
            automation.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Found, (await automation.GetAsync(NhProxyOptions.AdministrationPath)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await automation.PutAsJsonAsync(NhProxyOptions.ApiPath + "/redirects", new NhProxyRedirectSaveRequest(0, []))).StatusCode);
            var audit = await app.Services.GetRequiredService<INhProxyChangeAuditStore>().QueryAsync(new());
            Assert.Equal("sample-automation", Assert.Single(audit.Data!.Items).Actor.UserName);
            Assert.Equal(0, (await app.Services.GetRequiredService<INhProxyLoginAuditStore>().QueryAsync(new())).Data!.TotalCount);

            var antiforgery = Regex.Match(panel, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(antiforgery);
            var logout = await browser.PostAsync(NhProxyOptions.AdministrationPath + "/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(antiforgery)
            }));
            Assert.Equal(HttpStatusCode.Found, logout.StatusCode);
            Assert.Equal(HttpStatusCode.Found, (await browser.GetAsync(NhProxyOptions.AdministrationPath)).StatusCode);
            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static ClaimsPrincipal Principal(string subject, Claim permission)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject), permission], "sample-host"));
    }
}
