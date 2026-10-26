using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using Xunit.Abstractions;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB1: the Request Delegate Generator writes the request delegates of the assistant's endpoints when
/// the library is built, so a host start compiles none of them with ASP.NET Core's
/// <c>RequestDelegateFactory</c>. The hosts that only read the endpoints are built and mapped but never
/// started, so they need no database.
/// </summary>
public sealed class AssistantRequestDelegateTests(AssistantDatabaseFixture database, ITestOutputHelper output)
    : IClassFixture<AssistantDatabaseFixture>
{
    private const string Prefix = "/api/assistant";

    /// <summary>The namespace of the code that the Request Delegate Generator writes.</summary>
    private const string GeneratedNamespace = "Microsoft.AspNetCore.Http.Generated";

    [Fact]
    public async Task Every_assistant_endpoint_runs_a_request_delegate_generated_at_build_time()
    {
        await using var app = CreateHost();
        app.MapNewHeapAssistant(Prefix);

        var endpoints = ReadAssistantEndpoints(app);

        Assert.NotEmpty(endpoints);
        Assert.Empty(endpoints.Where(endpoint => !IsGenerated(endpoint)).Select(Describe));
    }

    /// <summary>
    /// The query values that ASP.NET Core binds, not the handler, keep the factory's answers: a malformed
    /// number, date, flag or id answers 400 without a body before the handler runs, a well-formed one
    /// reaches it, and a disabled assistant answers 404 also then, because its filter runs after the
    /// failed binding. The host runs as production does, without <c>ThrowOnBadRequest</c>.
    /// </summary>
    [Fact]
    public async Task Malformed_query_values_answer_400_before_the_handler_and_404_when_the_assistant_is_off()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
                services.AddSingleton<AssistantSwitch>();
                services.Replace(ServiceDescriptor.Singleton<IOptionsMonitor<NhAssistantOptions>>(provider =>
                    provider.GetRequiredService<AssistantSwitch>()));
            });
        using var client = app.CreateClient();
        string[] malformed =
        [
            "/api/assistant/conversations?page=first",
            "/api/assistant/runs?from=yesterday",
            "/api/assistant/approvals/inbox?grouped=perhaps",
            "/api/assistant/approvals/inbox?runId=latest"
        ];
        string[] wellFormed =
        [
            "/api/assistant/conversations?page=1&itemsPerPage=5",
            "/api/assistant/runs?from=2026-10-26T08:00:00Z&to=2026-10-26T10:00:00%2B01:00",
            $"/api/assistant/approvals/inbox?grouped=true&runId={Guid.NewGuid()}"
        ];

        foreach (var path in malformed)
        {
            using var response = await client.GetAsync(path);
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{path} answered {(int)response.StatusCode}");
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        foreach (var path in wellFormed)
        {
            using var response = await client.GetAsync(path);
            Assert.True(HttpStatusCode.OK == response.StatusCode, $"{path} answered {(int)response.StatusCode}");
        }

        app.Services.GetRequiredService<AssistantSwitch>().Enabled = false;
        foreach (var path in malformed)
        {
            using var response = await client.GetAsync(path);
            Assert.True(HttpStatusCode.NotFound == response.StatusCode, $"{path} answered {(int)response.StatusCode}");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal(NhAssistantErrorCodes.Disabled, body.RootElement.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// Measures what mapping and building the assistant's endpoints costs a host: the wall-clock and
    /// process CPU time of <c>MapNewHeapAssistant</c> plus the first read of every endpoint, for the
    /// first host of the process and for 20 hosts after three warm-up hosts, as a test process starts
    /// them. Run it alone in a Release build:
    /// <c>NH_MEASURE_ENDPOINTS=1 dotnet test -c Release --filter
    /// FullyQualifiedName~Measure_mapping_and_building_the_assistant_endpoints --logger "console;verbosity=detailed"</c>.
    /// Process CPU time advances in steps of the operating
    /// system's clock tick, about 15.6 ms on Windows, so compare its mean.
    /// </summary>
    [EndpointMeasurementFact]
    public async Task Measure_mapping_and_building_the_assistant_endpoints()
    {
        const int WarmUpHosts = 3;
        const int MeasuredHosts = 20;
        var wall = new List<double>();
        var cpu = new List<double>();
        var firstWall = 0d;
        var firstCpu = 0d;
        var endpointCount = 0;
        var generatedCount = 0;

        for (var index = 0; index < WarmUpHosts + MeasuredHosts; index++)
        {
            await using var app = CreateHost();
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var watch = Stopwatch.StartNew();

            app.MapNewHeapAssistant(Prefix);
            var endpoints = ReadAssistantEndpoints(app);

            watch.Stop();
            var cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;
            endpointCount = endpoints.Count;
            generatedCount = endpoints.Count(IsGenerated);
            if (index == 0)
            {
                firstWall = watch.Elapsed.TotalMilliseconds;
                firstCpu = (cpuAfter - cpuBefore).TotalMilliseconds;
            }
            else if (index >= WarmUpHosts)
            {
                wall.Add(watch.Elapsed.TotalMilliseconds);
                cpu.Add((cpuAfter - cpuBefore).TotalMilliseconds);
            }
        }

        wall.Sort();
        cpu.Sort();
        output.WriteLine(
            $"{endpointCount} endpoints, {generatedCount} generated, {endpointCount - generatedCount} compiled by the factory; "
            + $"{MeasuredHosts} hosts after {WarmUpHosts} warm-up hosts");
        output.WriteLine($"first host: wall ms {firstWall:F1}, cpu ms {firstCpu:F1}");
        output.WriteLine($"wall ms: median {Median(wall):F1}, mean {wall.Average():F1}, min {wall[0]:F1}, max {wall[^1]:F1}");
        output.WriteLine($"cpu ms:  median {Median(cpu):F1}, mean {cpu.Average():F1}, min {cpu[0]:F1}, max {cpu[^1]:F1}");
        Assert.True(endpointCount > 0);
    }

    private static WebApplication CreateHost()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(NhAssistantOptions.DefaultAccessPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(NhAssistantOptions.DefaultAdminPolicy, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddSingleton<TestProjectToolRecorder>();
        builder.Services.AddKeyedSingleton<IChatClient>("project-chat-model", new NhAiScriptedChatClient());
        builder.Services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("project-chat", profile => profile
                .UseKeyedClient("project-chat-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local"));
            ai.AddGeneratedToolCatalog<TestProjectToolCatalog>();
            ai.UseBudgetManager<NhAiTestBudgetManager>();
        });
        builder.Services.AddNewHeapPlatformAIAspNet(_ => { });
        builder.Services.AddNewHeapAssistant(assistant => assistant
            .UsePostgreSql("Host=localhost;Database=nh_assistant_request_delegates", options => options.Schema = "nhai")
            .UseChatProfile("project-chat")
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" }));
        return builder.Build();
    }

    /// <summary>
    /// Reads every endpoint of the host's data sources, which builds them and their request delegates,
    /// and returns the route handlers under the assistant's prefix.
    /// </summary>
    private static IReadOnlyList<RouteEndpoint> ReadAssistantEndpoints(WebApplication app)
    {
        var endpoints = new List<RouteEndpoint>();
        foreach (var dataSource in ((IEndpointRouteBuilder)app).DataSources)
        {
            foreach (var endpoint in dataSource.Endpoints)
            {
                if (endpoint is RouteEndpoint route
                    && route.RoutePattern.RawText?.StartsWith(Prefix, StringComparison.Ordinal) == true
                    && route.Metadata.GetMetadata<System.Reflection.MethodInfo>() is not null)
                {
                    endpoints.Add(route);
                }
            }
        }

        return endpoints;
    }

    /// <summary>
    /// A generated request delegate is declared in the generator's namespace inside the assistant's
    /// own assembly; one that <c>RequestDelegateFactory</c> compiled is declared in ASP.NET Core or is
    /// a dynamic method without a declaring type.
    /// </summary>
    private static bool IsGenerated(RouteEndpoint endpoint)
    {
        var declaringType = endpoint.RequestDelegate?.Method.DeclaringType;
        return declaringType is not null
            && declaringType.Assembly == typeof(NhAssistantEndpointRouteBuilderExtensions).Assembly
            && declaringType.Namespace == GeneratedNamespace;
    }

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
        var declaringType = endpoint.RequestDelegate?.Method.DeclaringType?.FullName ?? "dynamic method";
        return $"{string.Join(',', methods)} {endpoint.RoutePattern.RawText} ({declaringType})";
    }

    private static double Median(List<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}

/// <summary>
/// A fact that runs only when the environment variable <c>NH_MEASURE_ENDPOINTS</c> is <c>1</c>, so a
/// measurement stays out of the normal test run.
/// </summary>
public sealed class EndpointMeasurementFactAttribute : FactAttribute
{
    public const string Variable = "NH_MEASURE_ENDPOINTS";

    public EndpointMeasurementFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"A measurement: set {Variable}=1 to run it.";
        }
    }
}
