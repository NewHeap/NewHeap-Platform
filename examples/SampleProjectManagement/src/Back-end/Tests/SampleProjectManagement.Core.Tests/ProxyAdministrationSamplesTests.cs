using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>SPM-238: host-owned credentials and the preferred save-and-activate service.</summary>
public sealed class ProxyAdministrationSamplesTests
{
    [Fact]
    public async Task Consumer_enables_the_panel_and_activates_changes_without_restart()
    {
        var directory = Directory.CreateTempSubdirectory("newheap-proxy-administration-sample-");
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(options =>
            {
                // Test credential only. Real hosts load a precomputed hash from their secret provider.
                options.Administrator.UserName = "info@newheap.com";
                options.Administrator.PasswordHash = new PasswordHasher<string>().HashPassword("info@newheap.com", "sample-test-only-password");
                options.IpAllowlist.Enabled = true;
                options.IpAllowlist.Entries = ["127.0.0.1/32", "::1/128"];
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.UseNewHeapProxy();
            await app.StartAsync(cancellationToken);
            try
            {
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
                using var login = await client.GetAsync("/newheap-proxy/Login", cancellationToken);
                var loginHtml = await login.Content.ReadAsStringAsync(cancellationToken);
                Assert.Contains("Sign in", loginHtml);
                Assert.Contains("img-src data:", Assert.Single(login.Headers.GetValues("Content-Security-Policy")));
                var logo = System.Text.RegularExpressions.Regex.Match(loginHtml, "<img src=\"data:image/webp;base64,([^\"]+)\" alt=\"NewHeap\"");
                Assert.True(logo.Success, "The official logo must be embedded without host static-file setup or external requests.");
                var logoBytes = Convert.FromBase64String(WebUtility.HtmlDecode(logo.Groups[1].Value));
                Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(logoBytes, 0, 4));
                Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(logoBytes, 8, 4));
                Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/newheap-proxy", cancellationToken)).StatusCode);

                var configuration = app.Services.GetRequiredService<INhProxyConfigurationService>();
                var snapshot = await configuration.GetRedirectsAsync(cancellationToken);
                var saved = await configuration.SaveRedirectsAsync(new(snapshot.Revision,
                [
                    new NhProxyRedirectRule
                    {
                        Id = Guid.NewGuid(), Name = "Moved project overview",
                        Match = new NhProxyRedirectMatch { Path = "/old-projects", Hosts = [client.BaseAddress.Authority] }, Target = "/projects"
                    }
                ]), cancellationToken);
                Assert.True(saved.Success, string.Join("; ", saved.AllErrorMessages));
                Assert.Equal(NhProxyActivationState.Active, saved.Data!.Activation.State);
                var redirect = await client.GetAsync("/old-projects", cancellationToken);
                Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
                Assert.Equal("/projects", redirect.Headers.Location!.OriginalString);
                // SPM-239: a root-relative tester input inherits the proxy origin, including its assigned port.
                var loginToken = System.Text.RegularExpressions.Regex.Match(loginHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
                Assert.True(loginToken.Success);
                using var signedIn = await client.PostAsync("/newheap-proxy/Login", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["UserName"] = "info@newheap.com", ["Password"] = "sample-test-only-password",
                    ["__RequestVerificationToken"] = WebUtility.HtmlDecode(loginToken.Groups[1].Value)
                }), cancellationToken);
                Assert.Equal(HttpStatusCode.Found, signedIn.StatusCode);
                var rewriteEditor = await client.GetStringAsync("/newheap-proxy/RewriteEdit", cancellationToken);
                foreach (var reference in new[] { "config-files", "transforms", "dests-health-checks", "timeouts" })
                {
                    Assert.Contains($"href=\"https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/{reference}?view=aspnetcore-10.0\" target=\"_blank\" rel=\"noopener noreferrer\"", rewriteEditor);
                }
                Assert.Contains("Use the NewHeap JSON structure shown here", rewriteEditor);

                // SPM-239: editing a saved rule exposes its complete configuration, including the first path transform.
                var cluster = new NhProxyCluster { Id = Guid.NewGuid(), Name = "Projects backend", Destination = new("backend", new("https://backend.example/")) };
                var rewrite = new NhProxyRewriteRule
                {
                    Id = Guid.NewGuid(), Name = "Projects", ClusterId = cluster.Id, Match = new() { Path = "/api/{**rest}" },
                    Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.RemovePrefix, "/api")]
                };
                Assert.True((await configuration.SaveRewritesAsync(new(0, [rewrite], [cluster]), cancellationToken)).Success);
                using var editorResponse = await client.GetAsync("/newheap-proxy/RewriteEdit/" + rewrite.Id, cancellationToken);
                var editorHtml = await editorResponse.Content.ReadAsStringAsync(cancellationToken);
                var jsonField = System.Text.RegularExpressions.Regex.Match(editorHtml, "<textarea[^>]*id=\"AdvancedRuleJson\"[^>]*>(.*?)</textarea>", System.Text.RegularExpressions.RegexOptions.Singleline);
                Assert.True(jsonField.Success);
                using var displayedRule = System.Text.Json.JsonDocument.Parse(WebUtility.HtmlDecode(jsonField.Groups[1].Value));
                Assert.Equal("RemovePrefix", displayedRule.RootElement.GetProperty("transforms")[0].GetProperty("operation").GetString());
                Assert.Equal("/api", displayedRule.RootElement.GetProperty("transforms")[0].GetProperty("value").GetString());
                var policy = Assert.Single(editorResponse.Headers.GetValues("Content-Security-Policy"));
                var nonce = System.Text.RegularExpressions.Regex.Match(policy, "script-src 'nonce-([^']+)'");
                Assert.True(nonce.Success);
                var script = System.Text.RegularExpressions.Regex.Match(editorHtml, "<script nonce=\"([^\"]+)\">");
                Assert.True(script.Success);
                Assert.Equal(nonce.Groups[1].Value, WebUtility.HtmlDecode(script.Groups[1].Value));

                var panelHtml = await client.GetStringAsync("/newheap-proxy", cancellationToken);
                var panelToken = System.Text.RegularExpressions.Regex.Match(panelHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
                Assert.True(panelToken.Success);
                using var preview = await client.PostAsync("/newheap-proxy/TestUrl", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["url"] = "/old-projects?source=quick-test",
                    ["__RequestVerificationToken"] = WebUtility.HtmlDecode(panelToken.Groups[1].Value)
                }), cancellationToken);
                Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
                Assert.Null(preview.Headers.Location);
                var previewHtml = await preview.Content.ReadAsStringAsync(cancellationToken);
                Assert.Contains("GET " + app.Urls.Single() + "/old-projects?source=quick-test", previewHtml);
                Assert.Contains("Redirect match", previewHtml);
                Assert.Contains("/projects?source=quick-test", previewHtml);
                Assert.False((await configuration.SaveRedirectsAsync(new(snapshot.Revision, []), cancellationToken)).Success);
            }
            finally
            {
                await app.StopAsync(cancellationToken);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
