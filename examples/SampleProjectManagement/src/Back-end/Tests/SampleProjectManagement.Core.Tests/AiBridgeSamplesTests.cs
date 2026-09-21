using System.IO.Pipelines;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.AspNet.Mvc;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for the API bridge sample (SPM-242, SPM-243, SPM-244, SPM-255): the project and
/// project-task controllers become governed tools that are discovered per user from the
/// controllers' own policies and execute through the API's HTTP pipeline as that user.
/// </summary>
public sealed class AiBridgeSamplesTests
{
    private const string ViewerToken = "sample-viewer-token";
    private const string ManagerToken = "sample-manager-token";
    private static readonly string[] ViewerPermissions = ["app.project.view"];
    private static readonly string[] ManagerPermissions = ["app.project.view", "app.project.manage"];

    [Fact]
    public async Task Viewer_discovers_only_read_tools_of_the_project_api()
    {
        await using var sample = await BridgeSample.StartAsync();

        var tools = await sample.DiscoverAsync(ViewerPermissions);

        Assert.Contains(tools, tool => tool.Id == "sample-api.project.get");
        Assert.Contains(tools, tool => tool.Id == "sample-api.project.get-by-id");
        Assert.Contains(tools, tool => tool.Id == "sample-api.project-task.get");
        Assert.All(tools, tool => Assert.Equal(NhAiToolEffect.ReadOnly, tool.Effect));
    }

    [Fact]
    public async Task Project_manager_also_discovers_create_and_update_tools_that_require_approval()
    {
        await using var sample = await BridgeSample.StartAsync();

        var tools = await sample.DiscoverAsync(ManagerPermissions);

        var create = Assert.Single(tools, tool => tool.Id == "sample-api.project.create");
        var update = Assert.Single(tools, tool => tool.Id == "sample-api.project.update");
        Assert.Equal(NhAiToolEffect.Mutation, create.Effect);
        Assert.Equal(NhAiToolEffect.IdempotentMutation, update.Effect);
        Assert.All(
            tools.Where(tool => tool.Effect != NhAiToolEffect.ReadOnly),
            tool =>
            {
                Assert.Equal(NhAiApprovalRequirement.Required, tool.Approval);
                Assert.Equal(NhAiIdempotencySupport.Required, tool.Idempotency);
            });
        Assert.DoesNotContain(tools, tool => tool.Id.EndsWith(".delete", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, tool => tool.Id == "sample-api.project.get-public-statuses");
    }

    [Fact]
    public async Task Viewer_mutation_call_fails_before_the_http_call()
    {
        await using var sample = await BridgeSample.StartAsync();

        var result = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api_project_create_v1",
            new { body = new { name = "Bridge sample" } });

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Empty(sample.Requests);
    }

    [Fact]
    public async Task Read_tool_calls_the_api_as_the_signed_in_user()
    {
        await using var sample = await BridgeSample.StartAsync();
        var projectId = Guid.NewGuid();

        var result = await sample.InvokeAsync(
            ManagerToken,
            ManagerPermissions,
            "sample-api_project_get-by-id_v1",
            new { id = projectId });

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(200, result.GetProperty("data").GetProperty("status").GetInt32());
        var request = Assert.Single(sample.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"http://sample.test/projects/{projectId}", request.RequestUri!.ToString());
        Assert.Equal("Bearer " + ManagerToken, request.Headers.Authorization!.ToString());
        Assert.True(request.Headers.Contains(NhAiMvcBridgeDefaults.InvocationHeaderName));
    }

    [Fact]
    public async Task Flat_arguments_call_the_api_once_and_a_mixed_shape_never_reaches_it()
    {
        await using var sample = await BridgeSample.StartAsync();
        var projectId = Guid.NewGuid();

        var flat = await sample.InvokeRawAsync(
            ManagerToken,
            ManagerPermissions,
            "sample-api_project_get-by-id_v1",
            new AIFunctionArguments { ["id"] = JsonSerializer.SerializeToElement(projectId) });
        var mixed = await sample.InvokeRawAsync(
            ManagerToken,
            ManagerPermissions,
            "sample-api_project_get-by-id_v1",
            new AIFunctionArguments
            {
                ["input"] = JsonSerializer.SerializeToElement(new { id = projectId }),
                ["id"] = JsonSerializer.SerializeToElement(projectId)
            });

        Assert.True(flat.GetProperty("success").GetBoolean());
        Assert.False(mixed.GetProperty("success").GetBoolean());
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, mixed.GetProperty("code").GetString());
        var request = Assert.Single(sample.Requests);
        Assert.Equal($"http://sample.test/projects/{projectId}", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Gateway_lists_describes_and_queries_the_read_only_project_resources()
    {
        await using var sample = await BridgeSample.StartAsync();

        var resources = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_search-resources_v1",
            new { query = "project" });
        var none = await sample.InvokeAsync(
            "sample-outsider-token",
            [],
            "sample-api-gateway_search-resources_v1",
            new { query = "project" });
        var described = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_describe-resource_v1",
            new { resource = "project" });
        var rejected = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_query_v1",
            new { resource = "project", filter = new[] { new { key = "internalScore", @operator = "==", value = "1" } } });
        var query = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_query_v1",
            new { resource = "project", itemsPerPage = 5, filter = new[] { new { key = "status", @operator = "==", value = "Active" } } });
        var direct = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api_project_get_v1",
            new { itemsPerPage = 5, filter = new[] { new { key = "status", @operator = "==", value = "Active" } } });

        var names = resources.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("resource").GetString()).ToArray();
        Assert.Contains("project", names);
        Assert.Contains("project-task", names);
        Assert.Empty(none.GetProperty("data").EnumerateArray());
        var filterFields = described.GetProperty("data").GetProperty("query").GetProperty("filterFields").EnumerateArray().ToArray();
        Assert.Contains(filterFields, field => field.GetProperty("key").GetString() == "name");
        var status = Assert.Single(filterFields, field => field.GetProperty("key").GetString() == "status");
        Assert.Contains("Active", status.GetProperty("enumValues").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("id", described.GetProperty("data").GetProperty("get").GetProperty("idParameter").GetString());
        Assert.False(rejected.GetProperty("success").GetBoolean());
        Assert.True(query.GetProperty("success").GetBoolean());
        Assert.True(direct.GetProperty("success").GetBoolean());
        Assert.Equal(2, sample.Requests.Count);
        Assert.Equal(sample.Requests[0].RequestUri, sample.Requests[1].RequestUri);
    }

    [Fact]
    public async Task Gateway_queries_a_list_that_reads_its_collection_values_from_the_query_string()
    {
        await using var sample = await BridgeSample.StartAsync();
        var filter = new[] { new { key = "status", @operator = "==", value = "Active" } };

        var described = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_describe-resource_v1",
            new { resource = "project-from-query-string" });
        var query = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_query_v1",
            new { resource = "project-from-query-string", page = 2, itemsPerPage = 5, search = "roadmap", filter });
        var direct = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api_project_get-from-query-string_v1",
            new { page = 2, itemsPerPage = 5, search = "roadmap", filter });
        var rejected = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_query_v1",
            new { resource = "project-from-query-string", filter = new[] { new { key = "internalScore", @operator = "==", value = "1" } } });

        Assert.True(described.GetProperty("data").GetProperty("query").GetProperty("searchable").GetBoolean());
        Assert.True(query.GetProperty("success").GetBoolean());
        Assert.True(direct.GetProperty("success").GetBoolean());
        Assert.False(rejected.GetProperty("success").GetBoolean());
        Assert.Equal(2, sample.Requests.Count);
        Assert.Equal(sample.Requests[0].RequestUri, sample.Requests[1].RequestUri);
        Assert.Equal(
            "http://sample.test/projects/query-string?page=2&itemsPerPage=5&search=roadmap&filter="
                + Uri.EscapeDataString("[{\"key\":\"status\",\"operator\":\"==\",\"value\":\"Active\"}]"),
            sample.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Gateway_shapes_counts_and_redacts_project_results()
    {
        await using var sample = await BridgeSample.StartAsync();

        var compact = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_query_v1",
            new { resource = "project", itemsPerPage = 2 });
        var projected = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_query_v1",
            new { resource = "project", itemsPerPage = 2, fields = new[] { "name", "status" } });
        var counted = await sample.InvokeAsync(
            ViewerToken,
            ViewerPermissions,
            "sample-api-gateway_query_v1",
            new { resource = "project", countOnly = true, filter = new[] { new { key = "status", @operator = "==", value = "Active" } } });

        Assert.True(compact.GetProperty("success").GetBoolean());
        var compactBody = compact.GetProperty("data").GetProperty("body");
        Assert.Equal(42, compactBody.GetProperty("totalCount").GetInt64());
        Assert.DoesNotContain("@example.test", compactBody.GetRawText(), StringComparison.Ordinal);
        Assert.False(compactBody.GetProperty("items")[0].TryGetProperty("description", out _));
        Assert.True(projected.GetProperty("success").GetBoolean());
        var projectedItem = projected.GetProperty("data").GetProperty("body").GetProperty("items")[0];
        Assert.Equal(["name", "status"], projectedItem.EnumerateObject().Select(property => property.Name));
        Assert.True(counted.GetProperty("success").GetBoolean());
        Assert.Equal(42, counted.GetProperty("data").GetProperty("body").GetProperty("totalCount").GetInt64());
        Assert.Contains("page=1&itemsPerPage=1", sample.Requests[2].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_listing_contains_the_bridge_tools()
    {
        await using var sample = await BridgeSample.StartAsync();

        var names = await sample.ListMcpToolsAsync(ViewerToken, ViewerPermissions);

        Assert.Contains("sample-api_project_get_v1", names);
        Assert.Contains("sample-api-gateway_query_v1", names);
        Assert.DoesNotContain("sample-api_project_create_v1", names);
    }

    [Fact]
    public async Task Attested_bridge_catalog_validates_and_an_ungoverned_runtime_catalog_is_rejected()
    {
        await using var sample = await BridgeSample.StartAsync();

        await sample.AsViewerAsync(services =>
        {
            var catalog = services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
            NhAiToolCatalogAttestation.Validate(catalog, services);
            NhAiBridgeContractAssertions.AssertBoundedIdentifiers(catalog);
            Assert.Equal(catalog.AttestationHash, catalog.Manifest.SchemaHash);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                NhAiToolCatalogAttestation.Validate(new UngovernedCatalog(catalog), services));
            Assert.Contains("ungoverned function", exception.Message, StringComparison.Ordinal);
            return Task.FromResult(true);
        });
    }

    /// <summary>
    /// A runtime catalog that claims attestation but hands out plain functions that bypass
    /// <see cref="INhAiToolInvoker"/>; the attestation must reject it before export.
    /// </summary>
    private sealed class UngovernedCatalog(NhAiMvcBridgeToolCatalog source) : INhAiAttestedToolCatalog
    {
        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors => source.Descriptors;

        public NhAiToolCatalogManifest Manifest => source.Manifest;

        public string AttestationHash => source.AttestationHash;

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            return source.Descriptors
                .Select(descriptor => AIFunctionFactory.Create(
                    () => "bypasses the invoker",
                    new AIFunctionFactoryOptions { Name = descriptor.ExportName }))
                .ToArray();
        }
    }

    private sealed class BridgeSample : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly RecordingHandler _handler;

        private BridgeSample(ServiceProvider provider, RecordingHandler handler)
        {
            _provider = provider;
            _handler = handler;
        }

        public IReadOnlyList<HttpRequestMessage> Requests => _handler.Requests;

        public static async Task<BridgeSample> StartAsync()
        {
            var handler = new RecordingHandler();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SampleAiBridgeComposition.SelfBaseUrlKey] = "http://sample.test/"
                })
                .Build());
            services.AddControllers().AddApplicationPart(typeof(ProjectController).Assembly);
            services.AddAuthorization(options =>
            {
                // The same application policies the API registers in Program.cs.
                options.AddPolicy(
                    "app.project.view",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.project.view"));
                options.AddPolicy(
                    "app.project.manage",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.project.manage"));
            });
            services.AddKeyedSingleton<IChatClient>(
                "project-assistant-model",
                new NhAiDeterministicChatClient("sample-bridge-response"));
            services.AddSingleton<IProjectAiReadService, EmptyProjectAiService>();
            services.AddSingleton<IProjectAiMutationService, EmptyProjectAiService>();
            services.AddScoped<ProjectAiTools>();
            services.AddSampleProjectManagementAi();
            services.AddNewHeapPlatformAIAspNet(ai => ai.UseToolInvocationPurpose("project-assistance"));
            services.AddSampleAiBridge();
            services.AddHttpClient(NhAiMvcBridgeDefaults.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddMcpServer(options => options.ScopeRequests = false)
                .WithNewHeapPlatformAITools();

            var provider = services.BuildServiceProvider();
            foreach (var hostedService in provider.GetServices<IHostedService>())
            {
                await hostedService.StartAsync(CancellationToken.None);
            }
            return new BridgeSample(provider, handler);
        }

        public Task<IReadOnlyList<NhAiToolDescriptor>> DiscoverAsync(string[] permissions)
        {
            return AsUserAsync(null, permissions, services => services
                .GetRequiredService<INhAiToolDiscoveryService>()
                .DiscoverAsync(new NhAiToolDiscoveryRequest(
                    new NhAiInvocationContext("sample-user", "project-assistance", new Dictionary<string, string>()),
                    NhAiToolExposure.Agent))
                .AsTask());
        }

        public Task<JsonElement> InvokeAsync(string token, string[] permissions, string exportName, object input)
        {
            return AsUserAsync(token, permissions, async services =>
            {
                var function = services.GetRequiredService<NhAiMvcBridgeToolCatalog>()
                    .CreateFunctions(services)
                    .Single(item => item.Name == exportName);
                var output = await function.InvokeAsync(new AIFunctionArguments
                {
                    ["input"] = JsonSerializer.SerializeToElement(input)
                });
                return (JsonElement)output!;
            });
        }

        public Task<JsonElement> InvokeRawAsync(
            string token,
            string[] permissions,
            string exportName,
            AIFunctionArguments arguments)
        {
            return AsUserAsync(token, permissions, async services =>
            {
                var function = services.GetRequiredService<NhAiMvcBridgeToolCatalog>()
                    .CreateFunctions(services)
                    .Single(item => item.Name == exportName);
                return (JsonElement)(await function.InvokeAsync(arguments))!;
            });
        }

        public Task<string[]> ListMcpToolsAsync(string token, string[] permissions)
        {
            return AsUserAsync(token, permissions, async services =>
            {
                var clientToServer = new Pipe();
                var serverToClient = new Pipe();
                await using var server = McpServer.Create(
                    new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
                    services.GetRequiredService<IOptions<McpServerOptions>>().Value,
                    serviceProvider: services);
                _ = server.RunAsync();
                await using var client = await McpClient.CreateAsync(
                    new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
                return (await client.ListToolsAsync()).Select(tool => tool.Name).ToArray();
            });
        }

        public Task<T> AsViewerAsync<T>(Func<IServiceProvider, Task<T>> action)
        {
            return AsUserAsync(ViewerToken, ViewerPermissions, action);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
        }

        private async Task<T> AsUserAsync<T>(
            string? token,
            string[] permissions,
            Func<IServiceProvider, Task<T>> action)
        {
            await using var scope = _provider.CreateAsyncScope();
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "sample-user") };
            claims.AddRange(permissions.Select(permission => new Claim(NhPlatformClaimTypes.Permission, permission)));
            var httpContext = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "sample"))
            };
            if (token is not null)
            {
                httpContext.Request.Headers.Authorization = "Bearer " + token;
            }

            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = httpContext;
            try
            {
                return await action(scope.ServiceProvider);
            }
            finally
            {
                accessor.HttpContext = null;
            }
        }
    }

    /// <summary>Stands in for the API's HTTP pipeline and records what the bridge sends.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        // A canonical project page whose items carry nested contact details.
        private const string ProjectPage =
            "{\"page\":1,\"itemsPerPage\":2,\"totalCount\":42,\"resultCount\":2,\"filter\":[],\"orderBy\":[],\"items\":["
            + "{\"id\":\"6f0c3c1e-0000-4000-8000-000000000001\",\"key\":\"RM\",\"name\":\"Roadmap\",\"description\":null,"
            + "\"status\":\"Active\",\"owner\":{\"id\":\"u1\",\"name\":\"Owner\",\"email\":\"owner@example.test\",\"phoneNumber\":\"000\"}},"
            + "{\"id\":\"6f0c3c1e-0000-4000-8000-000000000002\",\"key\":\"OPS\",\"name\":\"Operations\",\"description\":null,"
            + "\"status\":\"Active\",\"ownerEmail\":\"ops@example.test\"}]}";

        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            var body = request.RequestUri!.AbsolutePath == "/projects" ? ProjectPage : "{\"id\":\"sample\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class EmptyProjectAiService : IProjectAiReadService, IProjectAiMutationService
    {
        public Task<IReadOnlyList<ProjectAiSearchItem>> SearchForAiAsync(
            Guid divisionId,
            string? query,
            int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProjectAiSearchItem>>([]);
        }

        public Task<TaskResult<ProjectAiStatusChangeReport>> ChangeStatusForAiAsync(
            Guid divisionId,
            Guid projectId,
            ProjectStatus status,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(TaskResult<ProjectAiStatusChangeReport>.Failed("not-used", "Not used by the bridge sample."));
        }

        public Task<ProjectStatus?> GetStatusForAiAsync(
            Guid divisionId,
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ProjectStatus?>(null);
        }
    }
}
