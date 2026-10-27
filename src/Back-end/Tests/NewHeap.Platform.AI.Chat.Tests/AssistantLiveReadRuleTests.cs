using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Live updates follow the read rule of the conversation's snapshot at the moment they are sent. An
/// agent thread streams only to people whom the subject's resolver gives a role now, also when an old
/// participant row still names someone whose role ended; a chat streams to a participant only while
/// the platform, rebuilding the person as a headless run does, says they pass the agent's policy; and a
/// participant hears about a chat only on connections signed in for the chat's tenant. On both
/// relational providers, over the real hub.
/// </summary>
public sealed class AssistantLiveReadRuleTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string AgentActor = "assistant-agent:project-assistant";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_thread_streams_only_to_people_whose_role_on_the_subject_still_holds(AssistantTestProvider provider)
    {
        var projects = new TwoTenantProjects();
        await using var app = await StartAsync(provider, new LiveReadPeople(), projects);
        var alice = ActorOf("alice");
        var bob = ActorOf("bob");
        projects.Roles[alice] = NhAiSubjectRole.Participant;
        projects.Roles[bob] = NhAiSubjectRole.Participant;
        var projectId = Guid.NewGuid().ToString("D");
        projects.Tenants[projectId] = TenantA;
        var thread = await CreateThreadAsync(app, projectId);
        var id = thread.ConversationId;

        // Both open the thread while they have a role, which records them as its participants.
        using var aliceClient = CreateClient(app, "alice", TenantA);
        using var bobClient = CreateClient(app, "bob", TenantA);
        Assert.Equal(HttpStatusCode.OK, (await aliceClient.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bobClient.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        await using var aliceLive = await ConnectAsync(app, "alice", TenantA);
        await using var bobLive = await ConnectAsync(app, "bob", TenantA);

        // Bob leaves the project. His participant row stays until he opens the thread again.
        projects.Roles.TryRemove(bob, out _);
        Assert.Contains(await ParticipantsAsync(app, id), participant => participant.ActorId == bob);

        await PublishAsync(app, new NhAssistantLiveTurnEvent(id, AgentActor, new NhAssistantMessageDeltaEvent(Guid.NewGuid(), "Only for the project's people.")));
        await PublishAsync(app, new NhAssistantLiveTurnEvent(id, AgentActor, new NhAssistantToolStartedEvent(Guid.NewGuid(), "projects.change-status", 1, "Change status", "{\"status\":\"closed\"}")));
        await PublishAsync(app, new NhAssistantLiveMessageCreated(
            id,
            alice,
            new NhAssistantMessageView(Guid.NewGuid(), NhAssistantMessageRoles.User, DateTimeOffset.UtcNow, [new NhAssistantTextPartView("A colleague's message.")])
            {
                Sequence = 9,
                AuthorActorId = alice
            }));

        await aliceLive.WaitForEventAsync(evt => evt.Type == "message.created");
        Assert.Equal(["message.delta", "tool.started", "message.created"], aliceLive.Events.Select(evt => evt.Type));
        await bobLive.SyncAsync(app, bob);
        Assert.Empty(bobLive.Events);

        // The snapshot refuses him as well, and only then removes his row.
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.DoesNotContain(await ParticipantsAsync(app, id), participant => participant.ActorId == bob);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_chat_streams_to_a_participant_only_while_the_platform_says_they_pass_the_agents_policy(AssistantTestProvider provider)
    {
        var people = new LiveReadPeople();
        await using var app = await StartAsync(provider, people, new TwoTenantProjects());
        var olivia = ActorOf("olivia");
        var pat = ActorOf("pat");
        var quinn = ActorOf("quinn");
        foreach (var manager in new[] { olivia, pat, quinn })
        {
            people.Managers[manager] = true;
        }
        using var owner = CreateClient(app, "olivia", TenantA, LiveReadAuthentication.ManagerPermission);
        var id = await CreateChatAsync(owner, "project-manager");
        foreach (var participant in new[] { "pat", "quinn" })
        {
            using var client = CreateClient(app, participant, TenantA, LiveReadAuthentication.ManagerPermission);
            await JoinAsync(owner, client, id);
        }
        await using var oliviaLive = await ConnectAsync(app, "olivia", TenantA);
        await using var patLive = await ConnectAsync(app, "pat", TenantA);
        await using var quinnLive = await ConnectAsync(app, "quinn", TenantA);

        // The application revokes the agent's permission of one participant; the owner keeps reading
        // her conversation, as its snapshot answers her.
        people.Managers.TryRemove(pat, out _);
        people.Managers.TryRemove(olivia, out _);

        await PublishAsync(app, new NhAssistantLiveTurnEvent(id, quinn, new NhAssistantMessageDeltaEvent(Guid.NewGuid(), "Budget figures.")));

        await oliviaLive.WaitForEventAsync(evt => evt.Type == "message.delta");
        await quinnLive.WaitForEventAsync(evt => evt.Type == "message.delta");
        await patLive.SyncAsync(app, pat);
        Assert.Empty(patLive.Events);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_participant_hears_about_a_chat_only_on_connections_signed_in_for_its_tenant(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new LiveReadPeople(), new TwoTenantProjects());
        var pat = ActorOf("pat");
        using var owner = CreateClient(app, "olivia", TenantA);
        var id = await CreateChatAsync(owner, "project-assistant");
        using var patInA = CreateClient(app, "pat", TenantA);
        using var patInB = CreateClient(app, "pat", TenantB);
        await JoinAsync(owner, patInA, id);
        await using var ownLive = await ConnectAsync(app, "pat", TenantA);
        await using var otherLive = await ConnectAsync(app, "pat", TenantB);

        await PublishAsync(app, new NhAssistantLiveTurnEvent(id, ActorOf("olivia"), new NhAssistantMessageDeltaEvent(Guid.NewGuid(), "Tenant A only.")));

        Assert.Equal("Tenant A only.", (await ownLive.WaitForEventAsync(evt => evt.Type == "message.delta")).Data.GetProperty("text").GetString());
        await otherLive.SyncAsync(app, pat);
        Assert.Empty(otherLive.Events);
        // The snapshot answers the same: the chat is found in tenant A only.
        Assert.Equal(HttpStatusCode.OK, (await patInA.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await patInB.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, LiveReadPeople people, TwoTenantProjects projects)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                // People sign in at one issuer whose tokens name their tenant, so one person keeps one
                // actor id in every tenant.
                services.AddAuthentication(LiveReadAuthentication.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, LiveReadAuthentication>(LiveReadAuthentication.SchemeName, _ => { });
                services.AddNewHeapPlatformAIAspNet(ai => ai.UseAuthenticatedClaims(AssistantTenantBindingTests.Issuer));
                services.AddSingleton(projects);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(people));
                // The publisher keeps its decisions about people for a few seconds; these tests change
                // people between two updates and see each change at once.
                services.Replace(ServiceDescriptor.Singleton(provider => new NhAssistantConversationAudience(
                    provider.GetRequiredService<NhAssistantDbContextFactory>())
                {
                    ReaderLifetime = TimeSpan.Zero
                }));
            },
            assistantBuilder: assistant => assistant.AddSubjectType<TwoTenantProjectResolver>(),
            provider: provider);
    }

    private static string ActorOf(string person)
    {
        return AssistantTenantBindingTests.ActorOf(person);
    }

    private static HttpClient CreateClient(AssistantWebApplication app, string person, string tenantId, string? permission = null)
    {
        var client = app.CreateClient(person);
        foreach (var (name, value) in LiveReadAuthentication.Headers(person, tenantId, permission))
        {
            client.DefaultRequestHeaders.Remove(name);
            client.DefaultRequestHeaders.Add(name, value);
        }
        return client;
    }

    private static Task<AssistantLiveListener> ConnectAsync(AssistantWebApplication app, string person, string tenantId)
    {
        return AssistantLiveListener.ConnectAsync(app, LiveReadAuthentication.Headers(person, tenantId, null));
    }

    private static async Task PublishAsync(AssistantWebApplication app, NhAssistantLiveEvent evt)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantLivePublisher>().PublishAsync(evt, CancellationToken.None);
    }

    private static async Task<AssistantThread> CreateThreadAsync(AssistantWebApplication app, string projectId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId), CancellationToken.None);
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync("project-assistant", includeDisabled: false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info!, CancellationToken.None)).Data!;
    }

    private static async Task<IReadOnlyList<AssistantConversationParticipant>> ParticipantsAsync(AssistantWebApplication app, Guid conversationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().GetParticipantsAsync(conversationId, CancellationToken.None);
    }

    private static async Task<Guid> CreateChatAsync(HttpClient owner, string agentId)
    {
        using var created = await owner.PostAsync("/api/assistant/conversations", Json(new { agentId }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ReadJsonAsync(created)).GetProperty("id").GetGuid();
    }

    private static async Task JoinAsync(HttpClient owner, HttpClient participant, Guid id)
    {
        using var link = await owner.PostAsync($"/api/assistant/conversations/{id}/share-link", null);
        var token = (await ReadJsonAsync(link)).GetProperty("token").GetString();
        using var joined = await participant.PostAsync($"/api/assistant/conversations/{id}/join", Json(new { token }));
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} answered {response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// The people as the platform knows them now, for the principal factory a headless run uses: who
/// holds the manager permission that the <c>project-manager</c> agent requires, and who the platform
/// no longer resolves.
/// </summary>
internal sealed class LiveReadPeople : INhAiPrincipalFactory
{
    public ConcurrentDictionary<string, bool> Managers { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, bool> Unresolved { get; } = new(StringComparer.Ordinal);

    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        if (Unresolved.ContainsKey(actorId))
        {
            return ValueTask.FromResult<ClaimsPrincipal?>(null);
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actorId), new("permission", "assistant") };
        if (Managers.ContainsKey(actorId))
        {
            claims.Add(new Claim("permission", LiveReadAuthentication.ManagerPermission));
        }
        return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
    }
}

/// <summary>
/// Signs the test client's person in at <see cref="AssistantTenantBindingTests.Issuer"/> with the tenant
/// of <see cref="TenantHeader"/> and the permission of <see cref="PermissionHeader"/>, and with the
/// expiry of <see cref="ExpiresHeader"/> as a bearer token's lifetime ends.
/// </summary>
internal sealed class LiveReadAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "live-read-test";
    public const string TenantHeader = "X-Test-Tenant";
    public const string PermissionHeader = "X-Test-Permission";
    public const string ManagerPermission = "manager";
    public const string ExpiresHeader = "X-Test-Expires";

    public static IReadOnlyDictionary<string, string> Headers(string person, string tenantId, string? permission)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AssistantWebApplication.UserHeader] = person,
            [AssistantWebApplication.AccessHeader] = "true",
            [TenantHeader] = tenantId
        };
        if (permission is not null)
        {
            headers[PermissionHeader] = permission;
        }
        return headers;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[AssistantWebApplication.UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("iss", AssistantTenantBindingTests.Issuer),
            new("sub", user),
            new(ClaimTypes.NameIdentifier, user),
            new("name", user)
        };
        var tenant = Request.Headers[TenantHeader].ToString();
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            claims.Add(new Claim("tenant_id", tenant));
        }
        if (Request.Headers.ContainsKey(AssistantWebApplication.AccessHeader))
        {
            claims.Add(new Claim("permission", "assistant"));
        }
        var permission = Request.Headers[PermissionHeader].ToString();
        if (!string.IsNullOrWhiteSpace(permission))
        {
            claims.Add(new Claim("permission", permission));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        var properties = new AuthenticationProperties();
        if (DateTimeOffset.TryParse(Request.Headers[ExpiresHeader].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires))
        {
            properties.ExpiresUtc = expires;
        }
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, SchemeName)));
    }
}
