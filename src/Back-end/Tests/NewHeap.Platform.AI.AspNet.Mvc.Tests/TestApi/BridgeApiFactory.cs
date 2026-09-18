using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;

/// <summary>
/// Hosts the minimal test API with the bridge over its own controllers. The bridge's named
/// HTTP client is routed into the in-memory test server, so self-HTTP calls pass through the
/// real authentication, authorization and MVC pipeline.
/// </summary>
public sealed class BridgeApiFactory : WebApplicationFactory<BridgeApiFactory>
{
    private readonly Action<NhAiMvcBridgeBuilder> _configureBridge;
    private readonly Action<IServiceCollection>? _configureServices;

    static BridgeApiFactory()
    {
        // Point WebApplicationFactory at the test output: this API has no project content root.
        Environment.SetEnvironmentVariable(
            "ASPNETCORE_TEST_CONTENTROOT_NEWHEAP_PLATFORM_AI_ASPNET_MVC_TESTS",
            AppContext.BaseDirectory);
    }

    public BridgeApiFactory(
        Action<NhAiMvcBridgeBuilder>? configureBridge = null,
        Action<IServiceCollection>? configureServices = null)
    {
        _configureBridge = configureBridge ?? DefaultBridge;
        _configureServices = configureServices;
    }

    public CapturedLogs Logs { get; } = new();

    public static void DefaultBridge(NhAiMvcBridgeBuilder bridge)
    {
        bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Order", "Probe", "UnnamedPolicy")
            .EnableMcpExposure();
    }

    protected override IHostBuilder CreateHostBuilder()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web => web
                .ConfigureServices(ConfigureApiServices)
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });
    }

    private void ConfigureApiServices(IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(BridgeApiFactory).Assembly);
        services.AddAuthentication(TestTokenHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestTokenHandler>(TestTokenHandler.SchemeName, _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(TestPolicies.OrderView, policy => policy.RequireClaim("permission", TestPolicies.OrderView));
            options.AddPolicy(TestPolicies.OrderManage, policy => policy.RequireClaim("permission", TestPolicies.OrderManage));
        });
        services.AddNewHeapPlatformAIAspNet(_ => { });
        services.AddNewHeapPlatformAI(ai => ai
            .UseBudgetManager<NhAiTestBudgetManager>(ServiceLifetime.Singleton)
            .UseIdempotencyManager<TestIdempotencyManager>(ServiceLifetime.Singleton));
        services.AddSingleton<NhAiCapturedAuditSink>();
        services.AddSingleton<INhAiAuditSink>(provider => provider.GetRequiredService<NhAiCapturedAuditSink>());
        services.AddNewHeapPlatformAIMvcBridge(_configureBridge);
        services.AddHttpClient(NhAiMvcBridgeDefaults.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(provider =>
                ((TestServer)provider.GetRequiredService<IServer>()).CreateHandler());
        services.AddMcpServer(options => options.ScopeRequests = false)
            .WithNewHeapPlatformAITools();
        _configureServices?.Invoke(services);
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside a scope whose current HTTP context belongs to the
    /// user of <paramref name="token"/>, as an incoming assistant or MCP request would.
    /// </summary>
    public async Task<T> AsUserAsync<T>(
        string? token,
        Func<IServiceProvider, Task<T>> action,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = TestTokenHandler.CreatePrincipal(token) ?? new ClaimsPrincipal(new ClaimsIdentity())
        };
        if (token is not null)
        {
            httpContext.Request.Headers.Authorization = "Bearer " + token;
        }
        httpContext.Request.Headers.AcceptLanguage = "nl-NL";
        httpContext.Request.Headers.Cookie = "session=must-not-be-forwarded";
        foreach (var header in headers ?? new Dictionary<string, string>())
        {
            httpContext.Request.Headers[header.Key] = header.Value;
        }
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

    /// <summary>
    /// Connects an MCP client over an in-memory transport to the application's
    /// <c>WithNewHeapPlatformAITools</c> server while the given user is the current request user.
    /// </summary>
    public Task<T> WithMcpClientAsync<T>(
        string? token,
        Func<McpClient, Task<T>> action,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return AsUserAsync(token, async services =>
        {
            var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            await using var server = McpServer.Create(
                new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
                options,
                serviceProvider: services);
            _ = server.RunAsync();
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
            return await action(client);
        }, headers);
    }

    /// <summary>Invokes a bridge tool through its governed function as the given user.</summary>
    public Task<BridgeToolResult> InvokeAsync(string? token, string exportName, object input)
    {
        return AsUserAsync(token, async services =>
        {
            var catalog = services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
            var function = catalog.CreateFunctions(services).Single(item => item.Name == exportName);
            var output = await function.InvokeAsync(new AIFunctionArguments
            {
                ["input"] = JsonSerializer.SerializeToElement(input)
            });
            var envelope = output is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(output, function.JsonSerializerOptions);
            return new BridgeToolResult(envelope);
        });
    }
}

public sealed class BridgeToolResult(JsonElement envelope)
{
    public JsonElement Envelope { get; } = envelope;

    public bool Success => Property("success").GetBoolean();

    public JsonElement Data => Property("data");

    public string Errors => Envelope.GetRawText();

    public JsonElement Property(string name)
    {
        foreach (var property in Envelope.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        throw new KeyNotFoundException(name);
    }
}

/// <summary>
/// Test bearer tokens: <c>user:&lt;id&gt;:&lt;permission&gt;,&lt;permission&gt;</c>.
/// </summary>
public sealed class TestTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "test-token";
    public const string Viewer = "user:viewer:order.view";
    public const string Manager = "user:manager:order.view,order.manage";
    public const string Outsider = "user:outsider:";

    public static ClaimsPrincipal? CreatePrincipal(string? token)
    {
        if (token is null || !token.StartsWith("user:", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = token.Split(':');
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, parts[1]) };
        if (parts.Length > 2)
        {
            claims.AddRange(parts[2]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(permission => new Claim("permission", permission)));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = CreatePrincipal(header["Bearer ".Length..]);
        return Task.FromResult(principal is null
            ? AuthenticateResult.Fail("Unknown test token.")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

public sealed class TestIdempotencyManager : INhAiIdempotencyManager
{
    public ConcurrentQueue<NhAiIdempotencyRequest> Requests { get; } = new();

    public ValueTask<NhAiIdempotencyLease> AcquireAsync(
        NhAiIdempotencyRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        return ValueTask.FromResult(new NhAiIdempotencyLease(
            NhAiIdempotencyDecisionKind.Acquired,
            "acquired",
            request.IdempotencyKey));
    }

    public ValueTask CompleteAsync(
        NhAiIdempotencyLease lease,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>Allows every effect so mutation execution can be tested without an approval flow.</summary>
public sealed class AllowAllEffectPolicy : INhAiEffectPolicy
{
    public ValueTask<NhAiEffectDecision> EvaluateAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new NhAiEffectDecision(NhAiEffectDecisionKind.Allow, "test-allow"));
    }
}

public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(categoryName, _entries);
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(category + ": " + formatter(state, exception) + " " + exception);
        }
    }
}

/// <summary>A distinct context for executor-level tests.</summary>
public static class TestContexts
{
    public static NhAiInvocationContext Create(string? idempotencyKey = null)
    {
        return new NhAiInvocationContext("actor-1", "assistant", new Dictionary<string, string>())
        {
            IdempotencyKey = idempotencyKey
        };
    }
}
