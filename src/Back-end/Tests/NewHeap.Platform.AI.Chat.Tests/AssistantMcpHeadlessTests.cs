using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// MCP servers in headless agent runs: offered only when an administrator allowed the server's
/// organization credential, authorized through a code grant or a person's rights, executed again
/// for an approved deferred proposal, and refused once the server no longer allows it. Both
/// relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantMcpHeadlessTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ServerUrl = "http://localhost/planning/mcp";
    private const string LookupFunction = "mcp_planning_lookup_order";
    private const string RescheduleFunction = "mcp_planning_reschedule";
    private const string Approver = "approver-1";
    private const string Manager = "manager-1";
    private const string Member = "member-1";
    private const string ManagePolicy = "app.project.manage";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_headless_run_offers_a_server_only_when_its_organization_credential_is_allowed(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Nothing to look up.")
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-lookup")
            .RespondWithText("Order A-100 is planned for Monday.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, model, servers);
        await ConnectPlanningServerAsync(host, ("Lookup_Order", NhAssistantMcpToolEffects.ReadOnly));
        var run = await StartRunAsync(host);
        var connectionsBeforeRun = servers.Connections.Count;

        var withoutServer = await RunTurnAsync(host, run, "Project PRJ-1 was created.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, withoutServer.Outcome);
        Assert.DoesNotContain(model.Requests[0].Options!.Tools!, tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));
        // The server is not even contacted.
        Assert.Equal(connectionsBeforeRun, servers.Connections.Count);

        await SetHeadlessUseAsync(host, NhAssistantMcpHeadlessUses.Organization);
        var withServer = await RunTurnAsync(host, run, "When is order A-100 planned?");

        Assert.Equal(NhAssistantTurnStatuses.Completed, withServer.Outcome);
        Assert.Contains(model.Requests[1].Options!.Tools!, tool => tool.Name == LookupFunction);
        Assert.Equal(["lookup-order"], servers.Calls);
        var record = Assert.Single(host.Audit.Records, item => item.ToolId == "mcp.planning.lookup-order");
        Assert.Equal(NhAiOutcomeKind.Succeeded, record.Outcome);
        Assert.Equal(NhAiGrantSource.Agent, record.GrantSource);
        var server = await FindServerAsync(host);
        Assert.NotNull(server.LastConnectAt);
        Assert.Null(server.LastConnectCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_approved_deferred_mcp_proposal_executes_once_with_the_organization_credential(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(RescheduleFunction, new { order = "A-100", day = "Friday" }, "call-reschedule")
            .RespondWithText("I proposed to move order A-100 to Friday.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, model, servers);
        await ConnectPlanningServerAsync(host, ("reschedule", NhAssistantMcpToolEffects.Mutation));
        await SetHeadlessUseAsync(host, NhAssistantMcpHeadlessUses.Organization);
        var run = await StartRunAsync(host);

        var proposed = await RunTurnAsync(host, run, "Move order A-100 to Friday.");

        Assert.Equal(1, proposed.ProposalCount);
        Assert.Empty(servers.Calls);
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal("mcp.planning.reschedule", approval.ToolId);
        Assert.Equal(NhAiGrantSource.Agent, approval.GrantSource);
        await ApproveAsync(host, approval);

        var execution = await ExecuteAsync(host, run, approval.Id);

        Assert.True(execution.Succeeded);
        Assert.Equal(["reschedule"], servers.Calls);
        var executed = Assert.Single(host.Audit.Records, item => item.ToolId == "mcp.planning.reschedule" && item.Outcome == NhAiOutcomeKind.Succeeded);
        Assert.Equal(approval.ProposalId.ToString(), executed.ProposalId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_deferred_mcp_proposal_is_refused_when_the_server_no_longer_allows_headless_use(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(RescheduleFunction, new { order = "A-100", day = "Friday" }, "call-reschedule")
            .RespondWithText("I proposed to move order A-100 to Friday.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, model, servers);
        await ConnectPlanningServerAsync(host, ("reschedule", NhAssistantMcpToolEffects.Mutation));
        await SetHeadlessUseAsync(host, NhAssistantMcpHeadlessUses.Organization);
        var run = await StartRunAsync(host);
        await RunTurnAsync(host, run, "Move order A-100 to Friday.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        await ApproveAsync(host, approval);

        await SetHeadlessUseAsync(host, NhAssistantMcpHeadlessUses.Never);
        var execution = await ExecuteAsync(host, run, approval.Id);

        Assert.False(execution.Succeeded);
        Assert.False(execution.Threw);
        Assert.Equal(NhAssistantAdminErrorCodes.McpHeadlessNotAllowed, execution.ResultCode);
        Assert.Empty(servers.Calls);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_required_policy_of_a_server_is_evaluated_for_the_person_whose_rights_the_run_uses(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Nothing to look up.")
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-lookup")
            .RespondWithText("Order A-100 is planned for Monday.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, model, servers, delegation: true, grants: false);
        await ConnectPlanningServerAsync(host, ("Lookup_Order", NhAssistantMcpToolEffects.ReadOnly), requiredPolicy: ManagePolicy);
        await SetHeadlessUseAsync(host, NhAssistantMcpHeadlessUses.Organization, requiredPolicy: ManagePolicy);

        var forMember = await RunTurnAsync(host, await StartRunAsync(host, initiator: Member), "Project PRJ-1 was created.");
        Assert.Equal(NhAssistantTurnStatuses.Completed, forMember.Outcome);
        Assert.DoesNotContain(model.Requests[0].Options!.Tools!, tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));

        var forManager = await RunTurnAsync(host, await StartRunAsync(host, initiator: Manager), "When is order A-100 planned?");

        Assert.Equal(NhAssistantTurnStatuses.Completed, forManager.Outcome);
        Assert.Contains(model.Requests[1].Options!.Tools!, tool => tool.Name == LookupFunction);
        Assert.Equal(["lookup-order"], servers.Calls);
        var record = Assert.Single(host.Audit.Records, item => item.ToolId == "mcp.planning.lookup-order");
        Assert.Equal(NhAiGrantSource.Delegated, record.GrantSource);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_credential_cannot_be_allowed_in_headless_runs(AssistantTestProvider provider)
    {
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), servers);

        var created = await WithAdministrationAsync(host, administration => administration.CreateAsync(
            Server(NhAssistantMcpAuthModes.ForwardUserToken) with { HeadlessUse = NhAssistantMcpHeadlessUses.Organization },
            "admin-1",
            CancellationToken.None));
        var unknown = await WithAdministrationAsync(host, administration => administration.CreateAsync(
            Server(NhAssistantMcpAuthModes.None) with { HeadlessUse = "always" },
            "admin-1",
            CancellationToken.None));
        var defaulted = await WithAdministrationAsync(host, administration => administration.CreateAsync(
            Server(NhAssistantMcpAuthModes.None),
            "admin-1",
            CancellationToken.None));

        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, created.GetResultItems()[0].Name);
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, unknown.GetResultItems()[0].Name);
        Assert.True(defaulted.Success);
        Assert.Equal(NhAssistantMcpHeadlessUses.Never, defaulted.Data!.HeadlessUse);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_headless_run_uses_a_persons_oauth_connection_only_with_consent_and_an_approved_proposal_fails_once_they_disconnected(
        AssistantTestProvider provider)
    {
        const string redirectUri = "https://assistant.example/api/assistant/mcp/oauth/callback";
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Nothing to look up without consent.")
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-lookup")
            .RespondWithText("Order A-100 is planned for Monday.")
            .RespondWithFunctionCall(RescheduleFunction, new { order = "A-100", day = "Friday" }, "call-reschedule")
            .RespondWithText("I proposed to move order A-100 to Friday.");
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", "test-web-secret-headless", redirectUri);
        var tools = new InMemoryMcpServers();
        NhAiTestProtectedResource? resource = null;
        await using var mcp = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools(), guard: (context, next) => resource!.InvokeAsync(context, next));
        resource = new NhAiTestProtectedResource(authorization, mcp.StreamableUrl, "/planning");
        await using var host = await CreateHostAsync(provider, model, servers: null, delegation: true, grants: false, redirectUri: redirectUri);
        var input = new NhAssistantMcpServerInput("planning", "Planning", mcp.StreamableUrl, NhAssistantMcpAuthModes.OAuth, null, null, null, true, NhAssistantMcpHeadlessUses.Personal)
        {
            ConnectionScope = NhAssistantMcpConnectionScopes.User,
            OAuth = new NhAssistantMcpOAuthInput("assistant-web", "test-web-secret-headless", null, "mcp:read", null, ConfirmAuthorization: true)
        };
        Assert.True((await WithAdministrationAsync(host, administration => administration.CreateAsync(input, "admin-1", CancellationToken.None))).Success);
        // The administrator syncs with their own account, then assigns the server.
        authorization.Options.Subject = "remote-admin";
        await AuthorizeAdministratorAsync(host);
        await ConnectPlanningServerAsync(host, ("Lookup_Order", NhAssistantMcpToolEffects.ReadOnly), input: input, created: true);
        await WithAdministrationAsync(host, administration => administration.UpdateToolAsync(
            "planning", "reschedule", true, NhAssistantMcpToolEffects.Mutation, null, "admin-1", CancellationToken.None));

        // Connected, but without consent to background use: the run leaves the server out.
        authorization.Options.Subject = "remote-member";
        await McpOAuthTestFlows.ConnectPersonAsync(host, Member, "planning");
        var withoutConsent = await RunTurnAsync(host, await StartRunAsync(host, initiator: Member), "Project PRJ-1 was created.");
        Assert.Equal(NhAssistantTurnStatuses.Completed, withoutConsent.Outcome);
        Assert.DoesNotContain(model.Requests[0].Options!.Tools ?? [], tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));

        // Connecting again with the consent: the run uses the person's own account.
        await McpOAuthTestFlows.ConnectPersonAsync(host, Member, "planning", allowBackgroundUse: true);
        var run = await StartRunAsync(host, initiator: Member);
        var withConsent = await RunTurnAsync(host, run, "When is order A-100 planned?");
        Assert.Equal(NhAssistantTurnStatuses.Completed, withConsent.Outcome);
        Assert.Equal(["lookup-order"], tools.Calls);
        Assert.Equal("remote-member", resource.Requests.Last().Subject);
        var lookup = Assert.Single(host.Audit.Records, item => item.ToolId == "mcp.planning.lookup-order");
        Assert.Equal(NhAiGrantSource.Delegated, lookup.GrantSource);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked
            && evt.ToolId == "mcp.planning.lookup-order"
            && evt.McpConnectionOwner == Member);

        // Q10: the proposal executes with the connection of the person who asked; once that person
        // disconnected, it fails as not connected, even though another person approves.
        await RunTurnAsync(host, run, "Move order A-100 to Friday.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        await ApproveAsync(host, approval);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var user = AssistantTestHost.EnterUser(scope.ServiceProvider, Member).User;
            Assert.True((await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>()
                .DisconnectAsync(Member, user, "planning", CancellationToken.None)).Success);
        }
        var execution = await ExecuteAsync(host, run, approval.Id);

        Assert.False(execution.Succeeded);
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionRequired, execution.ResultCode);
        Assert.DoesNotContain("reschedule", tools.Calls);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_started_by_hand_uses_only_the_starters_own_personal_connection(AssistantTestProvider provider)
    {
        const string redirectUri = "https://assistant.example/api/assistant/mcp/oauth/callback";
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-scheduled")
            .RespondWithText("Order A-100 is planned for Monday.")
            .RespondWithText("I cannot look that up for you.")
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-own")
            .RespondWithText("Order A-100 is planned for Monday.");
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", "test-web-secret-by-hand", redirectUri);
        var tools = new InMemoryMcpServers();
        NhAiTestProtectedResource? resource = null;
        await using var mcp = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools(), guard: (context, next) => resource!.InvokeAsync(context, next));
        resource = new NhAiTestProtectedResource(authorization, mcp.StreamableUrl, "/planning");
        // The agent answers to the approver's account and acts with its rights in headless runs.
        await using var host = await CreateHostAsync(provider, model, servers: null, grants: false, redirectUri: redirectUri, accountable: true);
        var input = new NhAssistantMcpServerInput("planning", "Planning", mcp.StreamableUrl, NhAssistantMcpAuthModes.OAuth, null, null, null, true, NhAssistantMcpHeadlessUses.Personal)
        {
            ConnectionScope = NhAssistantMcpConnectionScopes.User,
            OAuth = new NhAssistantMcpOAuthInput("assistant-web", "test-web-secret-by-hand", null, "mcp:read", null, ConfirmAuthorization: true)
        };
        Assert.True((await WithAdministrationAsync(host, administration => administration.CreateAsync(input, "admin-1", CancellationToken.None))).Success);
        authorization.Options.Subject = "remote-admin";
        await AuthorizeAdministratorAsync(host);
        await ConnectPlanningServerAsync(host, ("Lookup_Order", NhAssistantMcpToolEffects.ReadOnly), input: input, created: true);

        // Both the account the agent answers to and the member connected and allowed background use.
        authorization.Options.Subject = "remote-approver";
        await McpOAuthTestFlows.ConnectPersonAsync(host, Approver, "planning", allowBackgroundUse: true);
        authorization.Options.Subject = "remote-member";
        await McpOAuthTestFlows.ConnectPersonAsync(host, Member, "planning", allowBackgroundUse: true);

        // A run nobody started by hand acts with the accountable account's own connection (Q3).
        var scheduled = await RunTurnAsync(host, await StartRunAsync(host), "When is order A-100 planned?");
        Assert.Equal(NhAssistantTurnStatuses.Completed, scheduled.Outcome);
        Assert.Equal(["lookup-order"], tools.Calls);
        Assert.Equal("remote-approver", resource.Requests.Last().Subject);

        // The member runs the agent by hand: the run acts for the accountable account, whose personal
        // connection never serves a run someone else started, and the member's does not fit the
        // rights the run uses, so the server is left out.
        var byMember = await RunTurnAsync(host, await StartRunAsync(host, initiator: Member, triggerId: NhAssistantTriggerIds.Manual), "When is order A-100 planned?");
        Assert.Equal(NhAssistantTurnStatuses.Completed, byMember.Outcome);
        Assert.DoesNotContain(model.Requests[2].Options!.Tools ?? [], tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));
        Assert.Equal(["lookup-order"], tools.Calls);

        // Started by hand by the accountable person, the run uses that person's own connection.
        var byApprover = await RunTurnAsync(host, await StartRunAsync(host, initiator: Approver, triggerId: NhAssistantTriggerIds.Manual), "When is order A-100 planned?");
        Assert.Equal(NhAssistantTurnStatuses.Completed, byApprover.Outcome);
        Assert.Equal(["lookup-order", "lookup-order"], tools.Calls);
        Assert.Equal("remote-approver", resource.Requests.Last().Subject);

        // The tool source names why the server was left out.
        await using var scope = host.Services.CreateAsyncScope();
        var agent = (await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var subject = (await scope.ServiceProvider.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", Guid.NewGuid().ToString()), CancellationToken.None))!;
        await using var refused = await scope.ServiceProvider.GetRequiredService<INhAssistantMcpToolSource>().GetToolsAsync(
            new NhAssistantMcpToolRequest(agent)
            {
                IsHeadless = true,
                PersonActorId = Approver,
                PersonGrantSource = NhAiGrantSource.Accountable,
                Subject = subject,
                PersonalCredentialRefusal = NhAssistantAdminErrorCodes.McpInitiatorOnly
            },
            CancellationToken.None);
        Assert.Empty(refused.Functions);
        Assert.Equal(NhAssistantAdminErrorCodes.McpInitiatorOnly, refused.SkippedServers["planning"]);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_headless_run_calls_an_oauth_server_with_the_tokens_of_the_hosts_client_credentials(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-lookup")
            .RespondWithText("Order A-100 is planned for Monday.");
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-cc", "test-cc-secret-headless");
        var tools = new InMemoryMcpServers();
        NhAiTestProtectedResource? resource = null;
        await using var mcp = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools(), guard: (context, next) => resource!.InvokeAsync(context, next));
        resource = new NhAiTestProtectedResource(authorization, mcp.StreamableUrl, "/planning");
        await using var host = await CreateHostAsync(provider, model, servers: null);
        await ConnectPlanningServerAsync(
            host,
            ("Lookup_Order", NhAssistantMcpToolEffects.ReadOnly),
            input: new NhAssistantMcpServerInput(
                "planning",
                "Planning",
                mcp.StreamableUrl,
                NhAssistantMcpAuthModes.OAuthClientCredentials,
                null,
                null,
                null,
                true,
                NhAssistantMcpHeadlessUses.Organization)
            {
                OAuth = new NhAssistantMcpOAuthInput("assistant-cc", "test-cc-secret-headless", null, null, null, ConfirmAuthorization: true)
            });
        var run = await StartRunAsync(host);

        var result = await RunTurnAsync(host, run, "When is order A-100 planned?");

        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        Assert.Equal(["lookup-order"], tools.Calls);
        // The organization's token of the host's own client, for the server's address, served the run.
        Assert.NotEmpty(resource.Requests);
        Assert.All(resource.Requests, request => Assert.Equal("assistant-cc", request.Subject));
        Assert.All(authorization.TokenRequests, request => Assert.Equal(mcp.StreamableUrl, request.Resource));
        var record = Assert.Single(host.Audit.Records, item => item.ToolId == "mcp.planning.lookup-order");
        Assert.Equal(NhAiOutcomeKind.Succeeded, record.Outcome);
        Assert.Equal(NhAiGrantSource.Agent, record.GrantSource);
    }

    private Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        InMemoryMcpServers? servers,
        bool delegation = false,
        bool grants = true,
        string? redirectUri = null,
        bool accountable = false)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                if (servers is not null)
                {
                    // Without in-memory servers the assistant connects over HTTP.
                    services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers));
                }
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, ManagerPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .ConfigureMcp(mcp =>
                {
                    mcp.RequireHttps = true;
                    mcp.OAuth.RedirectUri = redirectUri;
                })
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent =>
                {
                    if (grants)
                    {
                        // The code key of headless use: the organization credential serves the agent itself.
                        agent.Grant("mcp.planning.*");
                    }
                    if (accountable)
                    {
                        // Headless runs act with the rights of the account the agent answers to.
                        agent.ServiceAccount(Approver).ActWithAccountableRights();
                    }
                    agent
                        .DelegateFromInitiator(delegation)
                        .UseApprovalPolicy("project-review");
                }));
    }

    private static NhAssistantMcpServerInput Server(string authMode)
    {
        return new NhAssistantMcpServerInput("planning", "Planning", ServerUrl, authMode, null, null, null, true);
    }

    private static async Task ConnectPlanningServerAsync(
        AssistantTestHost host,
        (string RemoteName, string Effect) tool,
        string? requiredPolicy = null,
        NhAssistantMcpServerInput? input = null,
        bool created = false)
    {
        await WithAdministrationAsync(host, async administration =>
        {
            if (!created)
            {
                Assert.True((await administration.CreateAsync(
                    input ?? Server(NhAssistantMcpAuthModes.None) with { RequiredPolicy = requiredPolicy },
                    "admin-1",
                    CancellationToken.None)).Success);
            }
            Assert.True((await administration.SyncAsync("planning", "admin-1", CancellationToken.None)).Success);
            var updated = await administration.UpdateToolAsync("planning", tool.RemoteName, true, tool.Effect, null, "admin-1", CancellationToken.None);
            Assert.True(updated.Success);
            return updated;
        });
        await using var scope = host.Services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var agent = (await catalog.FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
        var assigned = await agents.UpdateAsync(
            new NhAssistantAgentInput(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                agent.Definition.Instructions.Content,
                agent.Definition.ToolSelectors,
                ["planning"],
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None);
        Assert.True(assigned.Success);
    }

    /// <summary>The administrator connects their own account to a server each person connects to.</summary>
    private static async Task AuthorizeAdministratorAsync(AssistantTestHost host)
    {
        var start = (await WithAdministrationAsync(host, administration => administration.AuthorizeOrganizationAsync(
            "planning", "admin-1", null, CancellationToken.None))).Data!;
        var outcome = await McpOAuthTestFlows.CallbackAsync(host, await McpOAuthTestFlows.AuthorizeAsync(start.AuthorizationUrl));
        Assert.True((await McpOAuthTestFlows.CompleteAsync(host, start.FlowId, outcome.CompletionCode!, "admin-1", administrator: false)).Success);
    }

    private static async Task SetHeadlessUseAsync(AssistantTestHost host, string headlessUse, string? requiredPolicy = null)
    {
        var updated = await WithAdministrationAsync(host, administration => administration.UpdateAsync(
            Server(NhAssistantMcpAuthModes.None) with { HeadlessUse = headlessUse, RequiredPolicy = requiredPolicy },
            "admin-1",
            CancellationToken.None));
        Assert.True(updated.Success);
    }

    private static async Task<AssistantMcpServer> FindServerAsync(AssistantTestHost host)
    {
        return (await host.Services.GetRequiredService<NhAssistantAdminStore>().FindMcpServerAsync("planning", CancellationToken.None))!;
    }

    private static async Task<T> WithAdministrationAsync<T>(
        AssistantTestHost host,
        Func<NhAssistantMcpAdministration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
        return await action(scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>());
    }

    private sealed record StartedRun(AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    private static async Task<StartedRun> StartRunAsync(AssistantTestHost host, string? initiator = null, string triggerId = "project-created")
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var subject = new NhAiSubjectRef("project", Guid.NewGuid().ToString());
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = triggerId,
            InitiatorActorId = initiator,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        return new StartedRun(run, new NhAssistantAgentTurnContext(
            thread,
            info,
            options,
            run.Id,
            run.TriggerId,
            initiator,
            thread.AccountableOwnerId,
            IsRequestTurn: false));
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, StartedRun run, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
        return await runner.RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                run.AgentTurn,
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message)]),
            CancellationToken.None);
    }

    private static async Task<NhAssistantProposalExecutionResult> ExecuteAsync(AssistantTestHost host, StartedRun run, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
        return await runner.ExecuteProposalAsync(
            new NhAssistantProposalExecutionRequest(run.AgentTurn, approvalId),
            CancellationToken.None);
    }

    private static async Task ApproveAsync(AssistantTestHost host, AssistantApproval approval)
    {
        var approved = await RunStore(host).TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Approved,
            new NhAssistantApprovalTransition
            {
                DecidedByActorId = Approver,
                DecidedAt = DateTimeOffset.UtcNow,
                ApprovalExpiresAt = approval.ExpiresAt
            },
            CancellationToken.None);
        Assert.True(approved);
    }

    private static NhAssistantRunStore RunStore(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    /// <summary>
    /// Principals for headless decisions: the manager holds the permission the server requires.
    /// </summary>
    private sealed class ManagerPrincipalFactory : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actorId) };
            if (actorId == Manager)
            {
                claims.Add(new Claim("permission", ManagePolicy));
            }
            return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        }
    }
}
