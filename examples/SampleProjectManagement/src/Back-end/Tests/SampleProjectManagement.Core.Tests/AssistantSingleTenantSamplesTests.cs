using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-302: a deployment for one customer, whose people sign in at the customer's identity provider and
/// all belong to one tenant (<see cref="SampleSingleTenantComposition"/>). With the assistant's
/// single-tenant mode they see the agents of the whole application on the project management board,
/// open their threads, which stay stored without a tenant, and find its proposals in the inbox; who may
/// run an agent and who reads it still follow the policies.
/// </summary>
public sealed class AssistantSingleTenantSamplesTests(AssistantSingleTenantSampleHost host)
    : IClassFixture<AssistantSingleTenantSampleHost>
{
    [Fact]
    public async Task People_of_the_one_tenant_see_the_whole_applications_agent_on_the_board_and_read_its_thread()
    {
        using var manager = host.CreateClient(AssistantSingleTenantSampleHost.Manager);
        using var colleague = host.CreateClient(AssistantSingleTenantSampleHost.Colleague);
        using var stranger = host.CreateClient("someone-the-application-does-not-know");
        var board = $"/api/assistant/boards/{SampleAgentGroupComposition.ProjectManagement}";

        // The manager sees the weekly report on the board and may run it for the whole application; the
        // colleague reads it without running it; someone the application does not know sees no board.
        var managerTile = Assert.Single((await ReadJsonAsync(await manager.GetAsync(board, TestContext.Current.CancellationToken)))
            .GetProperty("agents").EnumerateArray());
        Assert.Equal((AssistantSingleTenantSampleHost.WeeklyReport, NhAiSubjectRef.SystemType, true),
            (managerTile.GetProperty("agentId").GetString(), managerTile.GetProperty("subjectType").GetString(), managerTile.GetProperty("canStart").GetBoolean()));
        var colleagueTile = Assert.Single((await ReadJsonAsync(await colleague.GetAsync(board, TestContext.Current.CancellationToken)))
            .GetProperty("agents").EnumerateArray());
        Assert.False(colleagueTile.GetProperty("canStart").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(board, TestContext.Current.CancellationToken)).StatusCode);

        // The manager opens the report's thread of the whole application; the colleague reads its
        // conversation, although the thread belongs to no tenant and every person to the customer's.
        using var open = await manager.PostAsync(
            "/api/assistant/threads/open",
            AssistantSampleHost.Json(new
            {
                agentId = AssistantSingleTenantSampleHost.WeeklyReport,
                subjectType = NhAiSubjectRef.SystemType,
                subjectId = NhAiSystemSubjects.GlobalId
            }),
            TestContext.Current.CancellationToken);
        var thread = (await ReadJsonAsync(open)).GetProperty("thread");
        var conversationId = thread.GetProperty("conversationId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync($"/api/assistant/conversations/{conversationId}", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/assistant/conversations/{conversationId}", TestContext.Current.CancellationToken)).StatusCode);

        // The approval inbox includes the whole application's proposals for its approvers.
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/assistant/approvals/inbox", TestContext.Current.CancellationToken)).StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.RequestMessage?.RequestUri} answered {response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// The sample assistant as one customer's deployment: people sign in at the customer's identity provider,
/// the AI ASP.NET integration projects them into the customer's tenant, and the assistant serves that one
/// tenant. An administrator created a weekly report for the whole application in the administration,
/// in the project management group.
/// </summary>
public sealed class AssistantSingleTenantSampleHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
{
    public const string Issuer = "https://identity.customer.example";
    public const string TenantId = "customer-1";
    public const string Manager = "manager-1";
    public const string Colleague = "colleague-1";
    public const string WeeklyReport = "sample-weekly-report";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await using var scope = Services.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
        const string administrator = "sample-administrator";
        var created = await configuration.CreateAgentAsync(
            new NhAssistantAgentRequest(
                WeeklyReport,
                "Weekly report",
                "Reports on the projects of the application every week.",
                "You report on the projects of the application. Count the open projects and name the ones that need attention.",
                [ProjectAiTools.ChangeStatusToolId],
                NhAiAutonomyLevel.Propose,
                OfferedInChat: false,
                WorksOnApplication: true),
            administrator,
            TestContext.Current.CancellationToken);
        Assert.True(created.Success);
        Assert.True((await configuration.SaveRunSettingsAsync(
            WeeklyReport,
            new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent),
            administrator,
            TestContext.Current.CancellationToken)).Success);
        Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentGroups>().SetMembershipAsync(
            WeeklyReport,
            [SampleAgentGroupComposition.ProjectManagement],
            administrator,
            TestContext.Current.CancellationToken)).Success);
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        // People sign in at the customer's identity provider; the test reads who from a header.
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, CustomerIdentityAuthentication>(
            CustomerIdentityAuthentication.SchemeName,
            _ => { });
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = CustomerIdentityAuthentication.SchemeName;
            options.DefaultAuthenticateScheme = CustomerIdentityAuthentication.SchemeName;
            options.DefaultChallengeScheme = CustomerIdentityAuthentication.SchemeName;
            options.DefaultForbidScheme = CustomerIdentityAuthentication.SchemeName;
        });
        // The application rebuilds its people's rights for decisions outside their request.
        services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, CustomerPeople>());
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        services.AddSampleSingleTenant(Issuer, TenantId);
        services.AddNewHeapAssistant(assistant => assistant.AddSampleProjectManagementGroup());
    }

    /// <summary>
    /// The platform's actor id of a person of the customer's identity provider.
    /// </summary>
    public static string ActorOf(string subject)
    {
        return "oidc:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Issuer + "\n" + subject)));
    }

    /// <summary>
    /// The customer's people as the application knows them: the manager may change projects, the
    /// colleague reads them; nobody else may sign in.
    /// </summary>
    private sealed class CustomerPeople : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            string[]? permissions = actorId == ActorOf(Manager)
                ? ["app.project.manage"]
                : actorId == ActorOf(Colleague) ? [] : null;
            return ValueTask.FromResult(permissions is null
                ? null
                : new ClaimsPrincipal(new ClaimsIdentity(
                    permissions.Select(permission => new Claim("permission", permission)).Append(new Claim(ClaimTypes.NameIdentifier, actorId)),
                    "customer-identity")));
        }
    }
}

/// <summary>
/// Signs the test client's person in as the customer's identity provider would: its issuer and subject,
/// and for an administrator the project manage permission.
/// </summary>
internal sealed class CustomerIdentityAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "customer-identity";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[SampleTestAuthenticationHandler.UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("iss", AssistantSingleTenantSampleHost.Issuer),
            new("sub", user),
            new(ClaimTypes.NameIdentifier, user)
        };
        if (user == AssistantSingleTenantSampleHost.Manager)
        {
            claims.Add(new Claim("permission", "app.project.manage"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
