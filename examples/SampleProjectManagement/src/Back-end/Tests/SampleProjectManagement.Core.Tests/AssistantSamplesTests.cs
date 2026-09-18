using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Npgsql;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Testcontainers.PostgreSql;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for the NewHeap assistant (SPM-245, SPM-246, SPM-247): the sample
/// composition runs on a real ASP.NET Core host over PostgreSQL, a scripted model drives the
/// curated <c>projects.*</c> tools, and every call goes through the assistant HTTP API.
/// </summary>
public sealed partial class AssistantSamplesTests(AssistantSampleHost host) : IClassFixture<AssistantSampleHost>
{
    private AssistantSampleHost Host => host;

    [Fact]
    public async Task SPM_245_a_conversation_streams_a_turn_that_searches_projects()
    {
        host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap", limit = 5 } })
            .RespondWithText("One roadmap project is visible in your division."));
        using var client = host.CreateClient("spm-245-user");
        var conversationId = await host.CreateConversationAsync(client);

        var events = await host.SendAsync(client, conversationId, "Which roadmap projects exist?");

        Assert.Equal(
            ["turn.started", "tool.started", "tool.completed", "message.delta", "turn.completed"],
            events.Select(evt => evt.Name).Distinct());
        var completed = events.Single(evt => evt.Name == "tool.completed").Data;
        Assert.Equal("succeeded", completed.GetProperty("status").GetString());
        Assert.Contains("Roadmap", completed.GetProperty("resultPreview").GetString());
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Contains(host.DivisionId, host.Projects.SearchedDivisions);
        var conversation = await host.GetJsonAsync(client, $"/api/assistant/conversations/{conversationId}");
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
        Assert.Equal(2, conversation.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task SPM_246_a_status_change_pauses_for_approval_and_resumes_with_verification()
    {
        var projectId = Guid.NewGuid();
        host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall(
                "projects_change_status_v1",
                new { input = new { projectId, status = (int)ProjectStatus.Active } })
            .RespondWithText("The project is active now."));
        using var client = host.CreateClient("spm-246-user");
        var conversationId = await host.CreateConversationAsync(client);

        var paused = await host.SendAsync(client, conversationId, "Activate the project.");

        var approval = paused.Single(evt => evt.Name == "approval.required").Data;
        Assert.Equal("projects.change-status", approval.GetProperty("toolId").GetString());
        Assert.Contains($"division:{host.DivisionId}", approval.GetProperty("targets").EnumerateArray().Select(target => target.GetString()));
        Assert.Equal("waiting-for-approval", paused[^1].Data.GetProperty("status").GetString());
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == projectId);

        var resumed = await host.DecideAsync(client, conversationId, approval, "approve");

        var completed = resumed.Single(evt => evt.Name == "tool.completed").Data;
        Assert.Equal("succeeded", completed.GetProperty("status").GetString());
        Assert.Equal("completed", resumed[^1].Data.GetProperty("status").GetString());
        Assert.Equal(ProjectStatus.Active, host.Projects.StatusChanges.Single(change => change.ProjectId == projectId).Status);
        Assert.Contains(projectId, host.Projects.VerifiedProjects);
        var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>().Events;
        Assert.Contains(audit, evt => evt.Kind == NewHeap.Platform.AI.Chat.NhAssistantAuditEventKind.ApprovalApproved);
        Assert.Contains(audit, evt => evt.ToolId == "projects.change-status" && evt.ResultCode == "succeeded");
    }

    [Fact]
    public async Task SPM_247_budget_and_idempotency_ledgers_are_durable_in_postgresql()
    {
        const string user = "spm-247-user";
        var projectId = Guid.NewGuid();
        host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "backlog", limit = 5 } })
            .RespondWithFunctionCall(
                "projects_change_status_v1",
                new { input = new { projectId, status = (int)ProjectStatus.OnHold } })
            .RespondWithText("The project is on hold."));
        using var client = host.CreateClient(user);
        var conversationId = await host.CreateConversationAsync(client);
        var paused = await host.SendAsync(client, conversationId, "Put the backlog project on hold.");
        var approval = paused.Single(evt => evt.Name == "approval.required").Data;
        await host.DecideAsync(client, conversationId, approval, "approve");

        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var toolCalls = await ScalarAsync<int>(
            connection,
            """SELECT "ToolCalls" FROM nhai."AssistantBudgetLedger" WHERE "ActorId" = @actor""",
            ("actor", user));
        var leaseStatus = await ScalarAsync<string>(
            connection,
            """SELECT "Status" FROM nhai."AssistantIdempotencyLease" WHERE "ToolId" = 'projects.change-status' AND "IdempotencyKey" = @key""",
            ("key", "assistant-" + approval.GetProperty("proposalId").GetGuid().ToString("N")));
        Assert.Equal(2, toolCalls);
        Assert.Equal("completed", leaseStatus);

        await using (var exhaust = new NpgsqlCommand(
            """UPDATE nhai."AssistantBudgetLedger" SET "ToolCalls" = 100 WHERE "ActorId" = @actor""",
            connection))
        {
            exhaust.Parameters.AddWithValue("actor", user);
            await exhaust.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "backlog", limit = 5 } }));
        var exhausted = await host.SendAsync(client, conversationId, "Search once more.");

        Assert.Equal("error", exhausted[^1].Name);
        Assert.Equal("assistant-budget-exhausted", exhausted[^1].Data.GetProperty("code").GetString());
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

/// <summary>
/// One PostgreSQL container and one Kestrel host with the sample assistant composition.
/// Project data is served by an in-memory fake so the evidence focuses on the assistant.
/// Derived hosts add compositions (such as the API bridge) through the protected hooks.
/// </summary>
public class AssistantSampleHost : IAsyncLifetime
{
    protected const string ManagePolicy = "app.active-division.project.manage";

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private WebApplication? _app;
    private Uri? _baseAddress;

    public Guid DivisionId { get; } = Guid.NewGuid();

    public SwitchableChatClient Model { get; } = new();

    public FakeProjects Projects { get; } = new();

    public string ConnectionString => _database.GetConnectionString();

    public IServiceProvider Services => _app!.Services;

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        ConfigureLogging(builder.Logging);
        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionStringName"] = "DefaultConnection",
            ["ConnectionStrings:DefaultConnection"] = _database.GetConnectionString(),
            ["NewHeap:AI:Assistant:Enabled"] = "true"
        };
        ConfigureSettings(settings);
        builder.Configuration.AddInMemoryCollection(settings);
        var services = builder.Services;
        services.AddAuthentication(SampleTestAuthenticationHandler.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SampleTestAuthenticationHandler>(SampleTestAuthenticationHandler.Scheme, _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(SampleAssistantComposition.AccessPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(SampleAssistantComposition.AdminPolicy, policy => policy.RequireClaim("permission", "app.project.manage"));
            ConfigureAuthorization(options);
        });
        services.AddSingleton(Projects);
        services.AddSingleton<IProjectAiReadService>(Projects);
        services.AddSingleton<IProjectAiMutationService>(Projects);
        services.AddSingleton<IProjectAiContextService>(Projects);
        services.AddScoped<ProjectAiTools>();
        services.AddKeyedSingleton<IChatClient>(SampleAssistantComposition.ModelKey, Model);

        // The same AI composition order as SampleProjectManagement.Api/Program.cs.
        services.AddSampleProjectManagementAi();
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .UseToolInvocationPurpose("project-assistance")
            .AddActiveDivisionScope(SampleAssistantComposition.AccessPolicy)
            .AddCapabilityGrant(ProjectAiTools.ReadCapability, SampleAssistantComposition.AccessPolicy)
            .AddCapabilityGrant(ProjectAiTools.ManageCapability, ManagePolicy));
        ConfigureServices(services);
        services.AddSampleAssistant();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapSampleAssistant();
        await _app.StartAsync();
        _baseAddress = new Uri(_app.Urls.First());
    }

    /// <summary>Adds logging providers; the base host logs nothing.</summary>
    protected virtual void ConfigureLogging(ILoggingBuilder logging)
    {
    }

    /// <summary>Adds configuration values before the host is built.</summary>
    protected virtual void ConfigureSettings(IDictionary<string, string?> settings)
    {
    }

    /// <summary>
    /// Registers the manage policy. The base host lets every signed-in user manage projects of
    /// the active division; derived hosts can tie it to a permission.
    /// </summary>
    protected virtual void ConfigureAuthorization(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        options.AddPolicy(ManagePolicy, policy => policy.RequireAuthenticatedUser());
    }

    /// <summary>
    /// Adds services after <c>AddNewHeapPlatformAIAspNet</c> and before <c>AddSampleAssistant</c>,
    /// the position of <c>AddSampleAiBridge</c> in Program.cs.
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        await _database.DisposeAsync();
    }

    public HttpClient CreateClient(string user, bool admin = false, params string[] permissions)
    {
        var client = new HttpClient { BaseAddress = _baseAddress, Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.Add(SampleTestAuthenticationHandler.UserHeader, user);
        // The bridge forwards the caller's bearer token to the API; the test scheme ignores it.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "token-of-" + user);
        if (admin)
        {
            client.DefaultRequestHeaders.Add(SampleTestAuthenticationHandler.AdminHeader, "true");
        }
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add(SampleTestAuthenticationHandler.PermissionsHeader, string.Join(',', permissions));
        }
        client.DefaultRequestHeaders.Add("X-NH-ActiveDivisionId", DivisionId.ToString());
        return client;
    }

    public async Task<Guid> CreateConversationAsync(HttpClient client)
    {
        using var response = await client.PostAsync(
            "/api/assistant/conversations",
            Json(new { agentId = SampleAssistantComposition.AgentId }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    public async Task<IReadOnlyList<SampleServerSentEvent>> SendAsync(HttpClient client, Guid conversationId, string text)
    {
        using var response = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/messages",
            Json(new { text, clientMessageId = Guid.NewGuid().ToString("N") }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadEventsAsync(response);
    }

    public async Task<IReadOnlyList<SampleServerSentEvent>> DecideAsync(
        HttpClient client,
        Guid conversationId,
        JsonElement approval,
        string decision)
    {
        using var response = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/approvals/{approval.GetProperty("approvalId").GetGuid()}/decide",
            Json(new { decision, expectedProposalHash = approval.GetProperty("proposalHash").GetString() }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadEventsAsync(response);
    }

    public async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return await ReadJsonAsync(response);
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<SampleServerSentEvent>> ReadEventsAsync(HttpResponseMessage response)
    {
        var events = new List<SampleServerSentEvent>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        string? name = null;
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && name is not null)
            {
                using var document = JsonDocument.Parse(line["data: ".Length..]);
                events.Add(new SampleServerSentEvent(name, document.RootElement.Clone()));
                name = null;
            }
        }
        return events;
    }

    public static StringContent Json(object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}

public sealed record SampleServerSentEvent(string Name, JsonElement Data);

/// <summary>
/// In-memory project data for the assistant evidence.
/// </summary>
public sealed class FakeProjects : IProjectAiReadService, IProjectAiMutationService, IProjectAiContextService
{
    public Task<IReadOnlyList<ProjectAiContextDocument>> SearchContextForAiAsync(
        Guid divisionId,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ProjectAiContextDocument>>([]);
    }

    private readonly ConcurrentDictionary<Guid, ProjectStatus> _statuses = new();

    public ConcurrentQueue<Guid> SearchedDivisions { get; } = new();

    public ConcurrentQueue<(Guid ProjectId, ProjectStatus Status)> StatusChanges { get; } = new();

    public ConcurrentQueue<Guid> VerifiedProjects { get; } = new();

    public Task<IReadOnlyList<ProjectAiSearchItem>> SearchForAiAsync(
        Guid divisionId,
        string? query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        SearchedDivisions.Enqueue(divisionId);
        IReadOnlyList<ProjectAiSearchItem> items = [new ProjectAiSearchItem(Guid.NewGuid(), "PRJ-1", "Roadmap")];
        return Task.FromResult(items);
    }

    public Task<TaskResult<ProjectAiStatusChangeReport>> ChangeStatusForAiAsync(
        Guid divisionId,
        Guid projectId,
        ProjectStatus status,
        CancellationToken cancellationToken = default)
    {
        var previous = _statuses.GetValueOrDefault(projectId, ProjectStatus.Draft);
        _statuses[projectId] = status;
        StatusChanges.Enqueue((projectId, status));
        return Task.FromResult(TaskResult<ProjectAiStatusChangeReport>.Succeeded(
            new ProjectAiStatusChangeReport(projectId, previous, status, true)));
    }

    public Task<ProjectStatus?> GetStatusForAiAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        VerifiedProjects.Enqueue(projectId);
        return Task.FromResult<ProjectStatus?>(_statuses.TryGetValue(projectId, out var status) ? status : null);
    }
}

/// <summary>
/// Lets each test install its own scripted model on the shared host.
/// </summary>
public sealed class SwitchableChatClient : IChatClient
{
    private IChatClient _current = new NhAiScriptedChatClient();

    public void Use(IChatClient client)
    {
        _current = client;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _current.GetResponseAsync(messages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _current.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}

internal sealed class SampleTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "sample-test";
    public const string UserHeader = "X-Sample-User";
    public const string AdminHeader = "X-Sample-Admin";
    public const string PermissionsHeader = "X-Sample-Permissions";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
        if (Request.Headers.ContainsKey(AdminHeader))
        {
            claims.Add(new Claim("permission", "app.project.manage"));
        }
        foreach (var permission in Request.Headers[PermissionsHeader].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            claims.Add(new Claim("permission", permission));
        }
        var identity = new ClaimsIdentity(claims, Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
