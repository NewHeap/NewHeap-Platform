using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>SPM-238/SPM-239: an opt-in Basic API manages persisted rules and previews them without backend traffic.</summary>
public sealed class ProxyApiSamplesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_client_saves_redirects_and_rewrites_using_revision_checked_basic_api(bool stringEnums)
    {
        var requestJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        if (stringEnums)
        {
            requestJson.Converters.Add(new JsonStringEnumConverter());
        }

        var directory = Directory.CreateTempSubdirectory("newheap-proxy-api-sample-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddOpenApi();
            builder.Services.AddNewHeapProxy(options =>
            {
                // Test-only credentials; deployments inject these settings through secrets.
                options.Administrator.UserName = "automation";
                options.Administrator.Password = "sample-api-password";
                options.Administrator.LoginAttemptLimit = 1;
                options.IpAllowlist.Enabled = true;
                options.IpAllowlist.Entries = ["127.0.0.1"];
            }, storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.MapProxyEndpoints();
            app.UseNewHeapProxy();
            app.MapOpenApi();
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
            const string api = NhProxyOptions.ApiPath;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(api + "/status")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("automation:sample-api-password")));

            var redirects = (await client.GetFromJsonAsync<NhProxyRedirectConfiguration>(api + "/redirects"))!;
            var redirect = new NhProxyRedirectRule
            {
                Id = Guid.NewGuid(), Name = "Moved projects", Match = new() { Path = "/old-projects" }, Target = "/projects"
            };
            var save = await client.PutAsJsonAsync(api + "/redirects", new NhProxyRedirectSaveRequest(redirects.Revision, redirects.Rules.Add(redirect)), requestJson);
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.False(save.Headers.Contains("Set-Cookie"));
            var result = (await save.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxySaveResult>>())!;
            Assert.True(result.Success);
            Assert.Equal(result.Data!.SavedRevision, result.Data.Activation.ActiveRevision);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(api + "/redirects", new NhProxyRedirectSaveRequest(redirects.Revision, []))).StatusCode);

            var rewrites = (await client.GetFromJsonAsync<NhProxyRewriteConfiguration>(api + "/rewrites"))!;
            var destination = new NhProxyCluster { Id = Guid.NewGuid(), Name = "Projects", Destination = new("projects", new Uri("https://backend.example/")) };
            var rewrite = new NhProxyRewriteRule
            {
                Id = Guid.NewGuid(), Name = "Project API", ClusterId = destination.Id, Match = new() { Path = "/projects/{**rest}" },
                Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.AddPrefix, "/api")]
            };
            // Missing transform kinds are client errors with a field and an actionable description.
            var invalidRewrite = JsonSerializer.SerializeToNode(new NhProxyRewriteSaveRequest(rewrites.Revision, [rewrite], [destination]), requestJson)!;
            invalidRewrite["rules"]![0]!["transforms"]![0]!.AsObject().Remove("kind");
            var rejected = await client.PutAsync(api + "/rewrites", new StringContent(invalidRewrite.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var invalidInput = Assert.Single((await rejected.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxySaveResult>>())!.Issues);
            Assert.Equal("$.rules[0].transforms[0]", invalidInput.Field);
            Assert.Contains("must specify 'kind'", invalidInput.Message);

            // Native route and transform construction runs before persistence, even for disabled drafts.
            foreach (var invalidPattern in new[]
            {
                rewrite with { Enabled = false, Transforms = [new NhProxyPathTransform(NhProxyPathTransformKind.Pattern, "/{broken")] },
                rewrite with { Match = new() { Path = "/{value:regex([)}" } }
            })
            {
                var failedSave = await client.PutAsJsonAsync(api + "/rewrites",
                    new NhProxyRewriteSaveRequest(rewrites.Revision, [invalidPattern], [destination]), requestJson);
                Assert.Equal(HttpStatusCode.BadRequest, failedSave.StatusCode);
                Assert.False(string.IsNullOrWhiteSpace(Assert.Single((await failedSave.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxySaveResult>>())!.Issues).Message));
                Assert.Equal(rewrites.Revision, (await client.GetFromJsonAsync<NhProxyRewriteConfiguration>(api + "/rewrites"))!.Revision);
            }

            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(api + "/rewrites", new NhProxyRewriteSaveRequest(rewrites.Revision, [rewrite], [destination]), requestJson)).StatusCode);
            var preview = await client.PostAsJsonAsync(api + "/test", new NhProxySavedTestRequest(new(1, 1),
                new NhProxyTestRequest { Url = new Uri("https://public.example/projects/42") }));
            var previewResult = (await preview.Content.ReadFromJsonAsync<NhProxyApiResult<NhProxyRuleTestResult>>())!;
            Assert.True(previewResult.Success);
            Assert.Equal("https://backend.example/api/projects/42", previewResult.Data!.Rewrite!.TargetUrl.AbsoluteUri);

            // API reads/previews and authentication do not create logins or change-audit noise.
            var audit = (await client.GetFromJsonAsync<NhProxyApiResult<NhProxyChangeAuditPage>>(api + "/audit"))!.Data!;
            Assert.Equal(2, audit.TotalCount);
            Assert.All(audit.Items, entry => Assert.Equal("automation", entry.Actor.UserName));
            var redirectChange = Assert.Single(audit.Items.Where(entry => entry.Engine == NhProxyEngine.Redirect));
            Assert.Equal(0, redirectChange.PreviousRevision);
            Assert.Equal(1, redirectChange.Revision);
            Assert.Equal("/projects", redirectChange.After!.Value.GetProperty("rules")[0].GetProperty("target").GetString());
            Assert.Equal(0, (await app.Services.GetRequiredService<INhProxyLoginAuditStore>().QueryAsync(new())).Data!.TotalCount);

            // API descriptions retain the public prefix even though routing lives in an isolated branch.
            using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
            var paths = document.RootElement.GetProperty("paths");
            Assert.True(paths.TryGetProperty(api + "/audit", out _));
            Assert.True(paths.TryGetProperty(api + "/redirects", out var redirectOperations));
            Assert.True(redirectOperations.TryGetProperty("put", out var put));
            Assert.False(string.IsNullOrWhiteSpace(put.GetProperty("summary").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(put.GetProperty("description").GetString()));
            foreach (var status in new[] { "200", "400", "401", "403", "409", "503" })
            {
                Assert.True(put.GetProperty("responses").TryGetProperty(status, out _), $"Missing HTTP {status}: {put}");
            }

            await app.StopAsync();
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
