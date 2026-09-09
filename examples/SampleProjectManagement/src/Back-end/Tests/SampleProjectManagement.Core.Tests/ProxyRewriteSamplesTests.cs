using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>SPM-239: validate an unsaved managed rewrite, publish it, and compare preview with real forwarding.</summary>
public sealed class ProxyRewriteSamplesTests
{
    [Fact]
    public async Task Consumer_tests_saves_and_executes_a_managed_rewrite_without_changing_redirects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("newheap-rewrite-sample-");
        try
        {
            var backendRequests = 0;
            var backendBuilder = WebApplication.CreateBuilder();
            backendBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var backend = backendBuilder.Build();
            backend.MapGet("/projects/{id:int}", (int id, HttpContext context) =>
            {
                Interlocked.Increment(ref backendRequests);
                return $"Project {id}, source {context.Request.Query["source"]}";
            }).AllowAnonymous();
            await backend.StartAsync(cancellationToken);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddNewHeapProxy(configureStorage: storage => storage.DatabasePath = Path.Combine(directory.FullName, "proxy.db"));
            await using var app = builder.Build();
            app.UseNewHeapProxy();
            await app.StartAsync(cancellationToken);

            var configuration = app.Services.GetRequiredService<INhProxyConfigurationService>();
            var tester = app.Services.GetRequiredService<INhProxyDraftTester>();
            var rewrites = await configuration.GetRewritesAsync(cancellationToken);
            var redirects = await configuration.GetRedirectsAsync(cancellationToken);
            var cluster = new NhProxyCluster
            {
                Id = Guid.NewGuid(), Name = "Project service",
                Destination = new("projects", new Uri(backend.Urls.Single() + "/"))
            };
            var rule = new NhProxyRewriteRule
            {
                Id = Guid.NewGuid(), Name = "Public project details", ClusterId = cluster.Id,
                Match = new() { Path = "/api/projects/{id:int}", Methods = ["GET"] },
                Transforms =
                [
                    new NhProxyPathTransform(NhProxyPathTransformKind.RemovePrefix, "/api"),
                    new NhProxyQueryTransform("source", NhProxyValueOperation.Set, "managed-proxy")
                ]
            };

            // Candidate clusters replace the saved cluster list only inside this isolated test.
            var tested = await tester.TestRewriteAsync(new(new(rewrites.Revision, redirects.Revision), rule,
                new() { Url = new("https://public.example/api/projects/42?source=incoming") })
            {
                DraftClusters = rewrites.Clusters.Add(cluster)
            }, cancellationToken);
            Assert.True(tested.Success, string.Join("; ", tested.AllErrorMessages));
            Assert.Equal(NhProxyTestOutcome.Rewrite, tested.Data!.Outcome);
            Assert.Equal("42", tested.Data.RouteValues["id"]);
            Assert.Equal("/projects/42?source=managed-proxy", tested.Data.Rewrite!.TargetUrl.PathAndQuery);
            Assert.Equal(0, backendRequests);
            Assert.Equal(rewrites.Revision, (await configuration.GetRewritesAsync(cancellationToken)).Revision);

            // The configuration service commits and independently confirms native YARP activation.
            var saved = await configuration.SaveRewritesAsync(new(rewrites.Revision, rewrites.Rules.Add(rule), rewrites.Clusters.Add(cluster)), cancellationToken);
            Assert.True(saved.Success, string.Join("; ", saved.AllErrorMessages));
            Assert.Equal(NhProxyActivationState.Active, saved.Data!.Activation.State);
            Assert.Equal(redirects.Revision, configuration.GetStatus().Redirect.ActiveRevision);
            // The top-bar URL tester uses the same API to test saved rules without a draft.
            var revisions = new NhProxyRevisions(saved.Data.SavedRevision, redirects.Revision);
            var savedTest = await tester.TestSavedAsync(revisions,
                new() { Url = new("https://public.example/api/projects/42?source=incoming") }, cancellationToken);
            Assert.True(savedTest.Success, string.Join("; ", savedTest.AllErrorMessages));
            Assert.Equal(rule.Id, savedTest.Data!.SelectedRuleId);
            Assert.Equal(tested.Data.Rewrite.TargetUrl, savedTest.Data.Rewrite!.TargetUrl);
            Assert.Equal(0, backendRequests);
            Assert.Equal(revisions.Rewrite, (await configuration.GetRewritesAsync(cancellationToken)).Revision);
            var noMatch = await tester.TestSavedAsync(revisions, new() { Url = new("https://public.example/missing") }, cancellationToken);
            Assert.True(noMatch.Success);
            Assert.Equal(NhProxyTestOutcome.NoMatch, noMatch.Data!.Outcome);
            Assert.Null(noMatch.Data.SelectedRuleId);
            var staleTest = await tester.TestSavedAsync(new(rewrites.Revision, redirects.Revision),
                new() { Url = new("https://public.example/api/projects/42") }, cancellationToken);
            Assert.False(staleTest.Success);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var response = await client.GetAsync(app.Urls.Single() + "/api/projects/42?source=incoming", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Equal("Project 42, source managed-proxy", await response.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(1, backendRequests);
            await app.StopAsync(cancellationToken);
            await backend.StopAsync(cancellationToken);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
