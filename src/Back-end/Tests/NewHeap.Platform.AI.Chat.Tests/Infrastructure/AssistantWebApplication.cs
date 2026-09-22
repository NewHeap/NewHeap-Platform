using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Test;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// A real ASP.NET Core host with the assistant endpoints on a test server.
/// </summary>
internal sealed class AssistantWebApplication : IAsyncDisposable
{
    public const string UserHeader = "X-Test-User";
    public const string AccessHeader = "X-Test-Access";
    public const string AdminHeader = "X-Test-Admin";
    public const string AccessPolicy = "app.assistant.access";

    private readonly WebApplication _app;

    private AssistantWebApplication(WebApplication app)
    {
        _app = app;
    }

    public IServiceProvider Services => _app.Services;

    public TestProjectToolRecorder Tools => _app.Services.GetRequiredService<TestProjectToolRecorder>();

    public static async Task<AssistantWebApplication> StartAsync(
        AssistantDatabaseFixture database,
        IChatClient model,
        bool enabled = true,
        Action<NhAssistantLimits>? limits = null,
        Action<IServiceCollection>? configure = null,
        Action<NhAssistantBuilder>? assistantBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:AI:Assistant:Enabled"] = enabled ? "true" : "false"
        });
        var services = builder.Services;
        services.AddAuthentication(TestAuthenticationHandler.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.Scheme, _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(TestProjectToolCatalog.ReadPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(TestProjectToolCatalog.ManagePolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AccessPolicy, policy => policy.RequireClaim("permission", "assistant"));
            options.AddPolicy("app.assistant.admin", policy => policy.RequireClaim("permission", "assistant-admin"));
        });
        services.AddSingleton<TestProjectToolRecorder>();
        services.AddKeyedSingleton("project-chat-model", model);
        services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("project-chat", profile => profile
                .UseKeyedClient("project-chat-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local")
                .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16));
            ai.AddGeneratedToolCatalog<TestProjectToolCatalog>();
            ai.UseBudgetManager<NhAiTestBudgetManager>();
        });
        services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
        services.AddNewHeapPlatformAIAspNet(ai => ai.UseToolInvocationPurpose("project-assistance"));
        var schema = AssistantDatabaseFixture.UniqueSchema();
        services.AddNewHeapAssistant(assistant => assistant
            .UsePostgreSql(
                database.ConnectionString(AssistantTestProvider.PostgreSql),
                options =>
                {
                    options.Schema = schema;
                    options.RunMigrations = true;
                })
            .UseChatProfile("project-chat")
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" })
            .AddAgent(AssistantTestData.Agent("project-manager", requiredPolicy: TestAuthenticationHandler.ManagerPolicy))
            .WithLimits(configured => limits?.Invoke(configured)));
        if (assistantBuilder is not null)
        {
            services.AddNewHeapAssistant(assistantBuilder);
        }
        configure?.Invoke(services);
        services.AddAuthorizationBuilder()
            .AddPolicy(TestAuthenticationHandler.ManagerPolicy, policy => policy.RequireClaim("permission", "manager"));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapNewHeapAssistant("/api/assistant");
        await app.StartAsync();
        return new AssistantWebApplication(app);
    }

    public HttpClient CreateClient(string? user = "user-1", bool access = true, bool admin = false)
    {
        var client = _app.GetTestClient();
        if (admin)
        {
            client.DefaultRequestHeaders.Add(AdminHeader, "true");
        }
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(UserHeader, user);
        }
        if (access)
        {
            client.DefaultRequestHeaders.Add(AccessHeader, "true");
        }
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "test";
    public const string ManagerPolicy = "app.project.manage";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[AssistantWebApplication.UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
        if (Request.Headers.ContainsKey(AssistantWebApplication.AccessHeader))
        {
            claims.Add(new Claim("permission", "assistant"));
        }
        if (Request.Headers.ContainsKey(AssistantWebApplication.AdminHeader))
        {
            claims.Add(new Claim("permission", "assistant-admin"));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
}

/// <summary>
/// Parses a <c>text/event-stream</c> body into events.
/// </summary>
internal static class ServerSentEventReader
{
    public sealed record ServerSentEvent(string Name, JsonElement Data);

    public static async Task<(IReadOnlyList<ServerSentEvent> Events, IReadOnlyList<string> Comments)> ReadAllAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        var events = new List<ServerSentEvent>();
        var comments = new List<string>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? name = null;
        string? data = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (name is not null && data is not null)
                {
                    using var document = JsonDocument.Parse(data);
                    events.Add(new ServerSentEvent(name, document.RootElement.Clone()));
                }
                name = null;
                data = null;
                continue;
            }
            if (line.StartsWith(':'))
            {
                comments.Add(line);
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line["data: ".Length..];
            }
        }
        return (events, comments);
    }

    public static HttpContent Json(object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}
