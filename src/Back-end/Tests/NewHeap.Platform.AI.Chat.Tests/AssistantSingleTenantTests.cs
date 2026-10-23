using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The single-tenant mode (<see cref="NhAssistantBuilder.UseSingleTenant"/>, ST1 and ST2 of the agent
/// runs adoption plan): in a host whose AI ASP.NET integration projects every person into one tenant,
/// people of that tenant see and decide the whole application's work, which stays stored without a
/// tenant; a caller of another tenant sees nothing, the policies still decide, and without the mode a
/// person with a tenant sees nothing of it as before. On both relational providers.
/// </summary>
public sealed class AssistantSingleTenantTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    internal const string Issuer = "https://identity.example.test";
    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string Statistics = "weekly-statistics";
    private const string Group = "reporting";
    private const string ResultsPolicy = "app.results.view";
    private const string Admin = "admin-1";
    private const string Reader = "reader-1";
    private const string Viewer = "viewer-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task People_of_the_single_tenant_see_and_decide_the_whole_applications_work(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, singleTenant: true);
        var (thread, runId, approvalId) = await SeedAsync(app);
        using var admin = app.CreateClient(Admin, admin: true);
        using var reader = app.CreateClient(Reader);

        // Every caller is projected into the tenant; the whole application's thread stays without one.
        Assert.Null(thread.TenantId);

        // The approver finds the proposal in the inbox and reads it with the right to decide it.
        var inbox = await ReadJsonAsync(await admin.GetAsync("/api/assistant/approvals/inbox"));
        Assert.Contains(approvalId, inbox.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("approvalId").GetGuid()));
        var proposal = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/approvals/{approvalId}"));
        Assert.True(proposal.GetProperty("canDecide").GetBoolean());

        // The reader sees the agent on its board with its thread, the run in the history, the thread's
        // conversation and the subject's work; only the approver may run the agent now.
        var board = await ReadJsonAsync(await reader.GetAsync($"/api/assistant/boards/{Group}"));
        var tile = Assert.Single(board.GetProperty("agents").EnumerateArray());
        Assert.Equal((Statistics, thread.ConversationId, false),
            (tile.GetProperty("agentId").GetString(), tile.GetProperty("conversationId").GetGuid(), tile.GetProperty("canStart").GetBoolean()));
        var adminTile = Assert.Single((await ReadJsonAsync(await admin.GetAsync($"/api/assistant/boards/{Group}"))).GetProperty("agents").EnumerateArray());
        Assert.True(adminTile.GetProperty("canStart").GetBoolean());
        var history = await ReadJsonAsync(await reader.GetAsync("/api/assistant/runs"));
        Assert.Contains(runId, history.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/assistant/runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/assistant/conversations/{thread.ConversationId}")).StatusCode);
        var work = await ReadJsonAsync(await reader.GetAsync("/api/assistant/subjects/nh-system/global/work"));
        Assert.Contains(Statistics, work.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("agentId").GetString()));

        // The reader follows the agent's work live; a caller of another tenant, which a resolver of the
        // application's own could return, follows none of it and finds nothing.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var watchers = services.GetRequiredService<NhAssistantAgentWorkWatchers>();
            Assert.Equal(
                [new NhAssistantWatchedWork(Statistics, NhAiSystemSubjects.GlobalId)],
                await watchers.WatchableAsync(ActorOf(Reader), Tenant, CancellationToken.None));
            Assert.Empty(await watchers.WatchableAsync(ActorOf(Reader), OtherTenant, CancellationToken.None));

            var inboxOfOther = await services.GetRequiredService<NhAssistantSystemApprovers>().InboxAsync(ActorOf(Admin), OtherTenant, CancellationToken.None);
            Assert.Empty(inboxOfOther.ApproverConversationIds);
            Assert.Empty(inboxOfOther.ViewerConversationIds);
            var boardOfOther = await services.GetRequiredService<NhAssistantAgentBoards>()
                .GetAsync(Group, null, new NhAssistantRunReader(ActorOf(Admin), OtherTenant, Principal(Admin), false), CancellationToken.None);
            Assert.Equal(NhAssistantErrorCodes.BoardNotFound, CodeOf(boardOfOther));
            Assert.Empty(await services.GetRequiredService<NhAssistantProposalReads>()
                .ReadAsync([approvalId], ActorOf(Admin), OtherTenant, Principal(Admin), _ => Task.FromResult(false), CancellationToken.None));
            var historyOfOther = await services.GetRequiredService<NhAssistantRunHistory>()
                .ListAsync(AllRuns, new NhAssistantRunReader(ActorOf(Reader), OtherTenant, Principal(Reader), false), CancellationToken.None);
            Assert.Empty(historyOfOther.Items);
            Assert.Null(await services.GetRequiredService<INhAssistantStore>()
                .FindAccessAsync(thread.ConversationId, ActorOf(Reader), OtherTenant, CancellationToken.None));
            Assert.False(NhAssistantAgentRunEndpoints.TryReadSubject(
                Statistics,
                NhAiSubjectRef.SystemType,
                NhAiSystemSubjects.GlobalId,
                OtherTenant,
                services.GetRequiredService<NhAssistantRegistrationState>(),
                services.GetRequiredService<NhAssistantSubjectAccessService>(),
                out _,
                out _));
            var decidedByOther = await services.GetRequiredService<NhAssistantProposalDecisions>()
                .DecideAsync(approvalId, ActorOf(Admin), Principal(Admin), false, null, null, CancellationToken.None, OtherTenant);
            Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(decidedByOther));

            // The policies still decide: a person of the tenant who reads the whole application but does
            // not pass the agent's view policy sees nothing of its work.
            Assert.Empty(await watchers.WatchableAsync(ActorOf(Viewer), Tenant, CancellationToken.None));
            Assert.Empty((await services.GetRequiredService<NhAssistantSystemApprovers>().InboxAsync(ActorOf(Viewer), Tenant, CancellationToken.None)).ViewerConversationIds);
            var boardOfViewer = await services.GetRequiredService<NhAssistantAgentBoards>()
                .GetAsync(Group, null, new NhAssistantRunReader(ActorOf(Viewer), Tenant, Principal(Viewer), false), CancellationToken.None);
            Assert.Equal(NhAssistantErrorCodes.BoardNotFound, CodeOf(boardOfViewer));
        }

        // A reader may not run the agent now; the approver runs it in the whole application's thread.
        var start = new { agentId = Statistics, subjectType = NhAiSubjectRef.SystemType, subjectId = NhAiSystemSubjects.GlobalId, message = "Count the open projects." };
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/assistant/threads/start", start)).StatusCode);
        var receipt = await ReadJsonAsync(await admin.PostAsJsonAsync("/api/assistant/threads/start", start), HttpStatusCode.Accepted);
        Assert.Contains(receipt.GetProperty("outcome").GetString(), new[] { NhAssistantTriggerOutcomes.Started, NhAssistantTriggerOutcomes.Queued });
        Assert.Equal(thread.ConversationId, receipt.GetProperty("conversationId").GetGuid());
        Assert.Single(app.Services.GetRequiredService<RecordingOperations>().Enqueued);

        // The approver decides the proposal; the reader may not.
        var rejection = new { decision = "reject", reason = "Not this week." };
        Assert.NotEqual(HttpStatusCode.OK, (await reader.PostAsJsonAsync($"/api/assistant/approvals/{approvalId}/decisions", rejection)).StatusCode);
        var decided = await ReadJsonAsync(await admin.PostAsJsonAsync($"/api/assistant/approvals/{approvalId}/decisions", rejection));
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, decided.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_the_mode_a_person_with_a_tenant_sees_nothing_of_the_whole_application(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, singleTenant: false);
        var (thread, runId, approvalId) = await SeedAsync(app);
        using var admin = app.CreateClient(Admin, admin: true);
        using var reader = app.CreateClient(Reader);

        // The same people, projected into the tenant, keep the behavior of a host with many tenants.
        var inbox = await ReadJsonAsync(await admin.GetAsync("/api/assistant/approvals/inbox"));
        Assert.DoesNotContain(approvalId, inbox.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("approvalId").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/assistant/approvals/{approvalId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/assistant/boards/{Group}")).StatusCode);
        Assert.Empty((await ReadJsonAsync(await reader.GetAsync("/api/assistant/runs"))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/assistant/runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/assistant/conversations/{thread.ConversationId}")).StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var watchers = scope.ServiceProvider.GetRequiredService<NhAssistantAgentWorkWatchers>();
        Assert.Empty(await watchers.WatchableAsync(ActorOf(Reader), Tenant, CancellationToken.None));
        // A caller without a tenant still takes part, by the same policies.
        Assert.Single(await watchers.WatchableAsync(ActorOf(Reader), null, CancellationToken.None));
    }

    [Fact]
    public async Task Startup_refuses_a_projection_of_another_tenant_or_of_a_tenant_claim()
    {
        var other = await Assert.ThrowsAsync<InvalidOperationException>(() => StartMinimalAsync(
            ai => ai.UseAuthenticatedClaimsForSingleTenant(Issuer, OtherTenant),
            assistant => assistant.UseSingleTenant(Tenant)));
        Assert.Contains("UseSingleTenant(\"tenant-1\")", other.Message);
        Assert.Contains("\"tenant-2\"", other.Message);

        var claim = await Assert.ThrowsAsync<InvalidOperationException>(() => StartMinimalAsync(
            ai => ai.UseAuthenticatedClaims(Issuer),
            assistant => assistant.UseSingleTenant(Tenant)));
        Assert.Contains("claim 'tenant_id'", claim.Message);

        // The same tenant and an issuer without a tenant pass the check; startup then goes on to the
        // agent runs' background operations, which this host does not register.
        var passed = await Assert.ThrowsAsync<InvalidOperationException>(() => StartMinimalAsync(
            ai => ai
                .UseAuthenticatedClaimsForSingleTenant(Issuer, Tenant)
                .UseAuthenticatedClaimsWithoutTenant("https://partners.example.test"),
            assistant => assistant
                .UseSingleTenant(Tenant)
                .ConfigureAgent(AssistantTestData.Agent().Id, run => run.WorksOnApplication())));
        Assert.Contains("background operations", passed.Message);

        // Without the mode a projection of any tenant is the host's own business.
        var withoutMode = await Assert.ThrowsAsync<InvalidOperationException>(() => StartMinimalAsync(
            ai => ai.UseAuthenticatedClaims(Issuer),
            assistant => assistant.ConfigureAgent(AssistantTestData.Agent().Id, run => run.WorksOnApplication())));
        Assert.Contains("background operations", withoutMode.Message);
    }

    [Fact]
    public void The_mode_names_one_tenant_and_only_that_tenant_takes_part()
    {
        var state = new NhAssistantRegistrationState();
        var builder = new NhAssistantBuilder(new ServiceCollection(), state);
        Assert.Throws<ArgumentException>(() => builder.UseSingleTenant(" "));
        Assert.Throws<ArgumentException>(() => builder.UseSingleTenant(" tenant-1"));
        Assert.Throws<ArgumentException>(() => builder.UseSingleTenant(new string('t', 257)));

        // Without the mode only callers without a tenant take part, and nobody is kept out where no
        // tenant was compared before.
        Assert.True(NhAssistantWholeApplicationTenancy.TakesPart(state, null));
        Assert.False(NhAssistantWholeApplicationTenancy.TakesPart(state, Tenant));
        Assert.False(NhAssistantWholeApplicationTenancy.KeepsOut(state, OtherTenant));

        builder.UseSingleTenant(Tenant).UseSingleTenant(Tenant);
        var another = Assert.Throws<InvalidOperationException>(() => builder.UseSingleTenant(OtherTenant));
        Assert.Contains("another tenant", another.Message);
        Assert.True(NhAssistantWholeApplicationTenancy.TakesPart(state, Tenant));
        Assert.True(NhAssistantWholeApplicationTenancy.TakesPart(state, null));
        Assert.False(NhAssistantWholeApplicationTenancy.TakesPart(state, OtherTenant));
        Assert.True(NhAssistantWholeApplicationTenancy.KeepsOut(state, OtherTenant));
        // The mode never keeps anyone from the application's own subjects; their tenants decide there.
        Assert.False(NhAssistantWholeApplicationTenancy.KeepsOut(state, new NhAiSubjectRef("project", "1"), OtherTenant));
        Assert.True(NhAssistantWholeApplicationTenancy.KeepsOut(state, NhAiSystemSubjects.Global, OtherTenant));

        // The whole application's work, stored without a tenant, reaches the tenant; an object of
        // another tenant never does.
        Assert.True(NhAssistantWholeApplicationTenancy.Reaches(state, null, NhAiSubjectRef.SystemType, Tenant));
        Assert.False(NhAssistantWholeApplicationTenancy.Reaches(state, null, NhAiSubjectRef.SystemType, OtherTenant));
        Assert.False(NhAssistantWholeApplicationTenancy.Reaches(state, null, "project", Tenant));
        Assert.False(NhAssistantWholeApplicationTenancy.Reaches(state, OtherTenant, "project", Tenant));
        Assert.True(NhAssistantWholeApplicationTenancy.Reaches(state, Tenant, "project", Tenant));
    }

    private static readonly NhAssistantRunQuery AllRuns = new(new NhAssistantRunFilter(null, null, null, null, null, null), 1, 20);

    /// <summary>
    /// The platform's actor id of a person of the issuer, as the AI ASP.NET integration makes it.
    /// </summary>
    internal static string ActorOf(string subject)
    {
        return "oidc:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Issuer + "\n" + subject)));
    }

    private static ClaimsPrincipal Principal(string subject)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject), new Claim("iss", Issuer)], "test"));
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, bool singleTenant)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                // Every person signs in at one issuer and belongs to one tenant, as in a host with an
                // external identity provider per customer.
                services.AddAuthentication(SingleTenantAuthentication.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, SingleTenantAuthentication>(SingleTenantAuthentication.SchemeName, _ => { });
                services.AddNewHeapPlatformAIAspNet(ai => ai.UseAuthenticatedClaimsForSingleTenant(Issuer, Tenant));
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TenantPrincipals>());
                services.AddAuthorization(options => options.AddPolicy(ResultsPolicy, policy => policy.RequireClaim("permission", ResultsPolicy)));
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                // Runs are seeded here; a run started by hand is only queued.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddAgentGroup(Group, group => group.Named(new() { ["en"] = "Reporting" }, fallbackLanguage: "en"))
                    .AddAgent(AssistantTestData.Agent(Statistics))
                    .ConfigureAgent(Statistics, run => run.WorksOnApplication().ViewPolicy(ResultsPolicy).InGroup(Group));
                if (singleTenant)
                {
                    assistant.UseSingleTenant(Tenant);
                }
            },
            provider: provider);
    }

    /// <summary>
    /// The agent's thread of the whole application with a completed run and a pending proposal of it.
    /// </summary>
    private static async Task<(AssistantThread Thread, Guid RunId, Guid ApprovalId)> SeedAsync(AssistantWebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(NhAiSystemSubjects.Global, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(Statistics, includeDisabled: false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = NhAssistantTriggerIds.Manual,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Completed,
            AgentVersion = 1,
            Summary = "Ended completed.",
            Result = "Twelve projects are open.",
            CreatedAt = createdAt,
            StartedAt = createdAt,
            EndedAt = createdAt.AddSeconds(30),
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);

        await using var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = "{}",
            ToolId = "projects.complete",
            Summary = "Complete the finished projects.",
            ArgumentsPreview = "{}",
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = createdAt.AddSeconds(20),
            ExpiresAt = createdAt.AddDays(1),
            RunId = run.Id,
            Mode = NhAssistantApprovalModes.Deferred,
            ConcurrencyStamp = Guid.NewGuid()
        };
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return (thread, run.Id, approval.Id);
    }

    /// <summary>
    /// Starts a host without a database that only runs the startup checks.
    /// </summary>
    private static async Task StartMinimalAsync(Action<NhAiAspNetBuilder> ai, Action<NhAssistantBuilder> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(NhAssistantOptions.DefaultAccessPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(NhAssistantOptions.DefaultAdminPolicy, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddSingleton<TestProjectToolRecorder>();
        builder.Services.AddKeyedSingleton<IChatClient>("project-chat-model", new NhAiScriptedChatClient());
        builder.Services.AddNewHeapPlatformAI(platform =>
        {
            platform.AddChatProfile("project-chat", profile => profile
                .UseKeyedClient("project-chat-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local"));
            platform.UseBudgetManager<NhAiTestBudgetManager>();
        });
        builder.Services.AddNewHeapPlatformAIAspNet(ai);
        builder.Services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_single_tenant", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" });
            configure(assistant);
        });
        await using var app = builder.Build();
        await app.StartAsync();
        await app.StopAsync();
    }

    private static string? CodeOf(NewHeap.Platform.Common.Models.TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.RequestUri} answered {response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The people as the whole application sees them, by their actor id at the issuer: the
    /// administrator approves, the reader reads, both see the agent's results; the viewer reads the whole
    /// application without the agent's view policy.
    /// </summary>
    private sealed class TenantPrincipals : INhAiPrincipalFactory
    {
        private static readonly Dictionary<string, string[]> Permissions = new(StringComparer.Ordinal)
        {
            [ActorOf(Admin)] = ["assistant", "assistant-admin", ResultsPolicy],
            [ActorOf(Reader)] = ["assistant", ResultsPolicy],
            [ActorOf(Viewer)] = ["assistant"]
        };

        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(Permissions.TryGetValue(actorId, out var permissions)
                ? new ClaimsPrincipal(new ClaimsIdentity(
                    permissions.Select(value => new Claim("permission", value)).Append(new Claim(ClaimTypes.NameIdentifier, actorId)),
                    "test"))
                : null);
        }
    }
}

/// <summary>
/// Signs the test client's person in at <see cref="AssistantSingleTenantTests.Issuer"/>, with the
/// issuer and subject claims the AI ASP.NET integration projects and the assistant's permissions.
/// </summary>
internal sealed class SingleTenantAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "single-tenant-test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[AssistantWebApplication.UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("iss", AssistantSingleTenantTests.Issuer),
            new("sub", user),
            new(ClaimTypes.NameIdentifier, user)
        };
        if (Request.Headers.ContainsKey(AssistantWebApplication.AccessHeader))
        {
            claims.Add(new Claim("permission", "assistant"));
        }
        if (Request.Headers.ContainsKey(AssistantWebApplication.AdminHeader))
        {
            claims.Add(new Claim("permission", "assistant-admin"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
