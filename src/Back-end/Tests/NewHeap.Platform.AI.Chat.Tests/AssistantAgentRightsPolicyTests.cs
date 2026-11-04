using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// <c>UseAgentRightsPolicy</c>: a person with the administration policy but without this one builds and operates
/// agents, and changes nothing of whose rights an agent uses, who reaches it, who decides its proposals or what
/// runs without approval, in the screens, through the agent builder and through an agent package alike. Saving a
/// value again, what only narrows and application code stay open, and without the policy nothing changes.
/// <c>WithoutRightsChanges</c> of <c>AddAgentBuilder</c> keeps the same settings out of the builder for everyone
/// and leaves them in the screens (FB97). On both relational providers where the store is touched.
/// </summary>
public sealed class AssistantAgentRightsPolicyTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string RightsPolicy = "app.assistant.agent-rights";
    private const string Manager = "manager-1";
    private const string Owner = "owner-1";
    private const string Weekly = "weekly-count";
    private const string CodeAgent = "project-assistant";
    private const string Builder = NhAssistantAgentBuilderIds.AgentId;

    /// <summary>A person the platform rebuilds outside a request, who does not pass the policy.</summary>
    private const string OtherPerson = "approver-1";

    /// <summary>An actor the platform knows as no person: application code, such as a deploy.</summary>
    private const string Job = "job-1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void What_counts_as_a_change_compares_with_what_is_stored_and_a_new_part_with_its_defaults()
    {
        // New plain run settings change nothing; anyone's rights, delegation and an approval policy do.
        Assert.Empty(NhAssistantAgentRights.OfRunSettings(null, RunSettings()));
        Assert.Equal(
            ["accountability", "serviceAccountActorId", "delegateFromInitiator", "allowGatewayReads", "elevatedReads", "approvalPolicy"],
            NhAssistantAgentRights.OfRunSettings(null, new AssistantAgentRunConfiguration
            {
                AgentId = Weekly,
                Accountability = NhAssistantAccountabilities.ServiceAccount,
                ServiceAccountActorId = "service-1",
                DelegateFromInitiator = true,
                AllowGatewayReads = true,
                ElevatedReads = NhAssistantElevatedReadNames.Deny,
                ApprovalPolicyId = "review"
            }));

        // The same settings again change nothing, whatever else differs.
        var stored = RunSettings();
        stored.Accountability = NhAssistantAccountabilities.SubjectResponsible;
        var next = RunSettings();
        next.Accountability = NhAssistantAccountabilities.SubjectResponsible;
        next.MaxConcurrentRuns = 9;
        Assert.Empty(NhAssistantAgentRights.OfRunSettings(stored, next));
        Assert.Equal(["accountability"], NhAssistantAgentRights.OfRunSettings(stored, RunSettings()));

        // A new skill and a new consultation with their defaults change nothing either.
        Assert.Empty(NhAssistantAgentRights.OfSkill(null, new AssistantAgentSkillDefinition { AgentId = Weekly, SkillId = "s" }));
        Assert.Equal(["approvalPolicy"], NhAssistantAgentRights.OfSkill(null, new AssistantAgentSkillDefinition { AgentId = Weekly, SkillId = "s", ApprovalPolicyId = "review" }));
        Assert.Empty(NhAssistantAgentRights.OfConsultation(null, Consultation()));
        var open = Consultation();
        open.AskerPolicy = "askers";
        open.MinimumRole = NhAssistantSubjectRoleNames.Participant;
        open.RoutesJson = NhAssistantDataConsultationRoutes.Write([NhAssistantConsultationRoutes.AskerDecides]);
        open.ProposalRole = NhAssistantSubjectRoleNames.Approver;
        open.ProposalPolicyId = "review";
        open.ExposeToMcp = true;
        Assert.Equal(
            ["routes", "askerPolicy", "minimumRole", "proposalRole", "proposalPolicy", "exposeToMcp"],
            NhAssistantAgentRights.OfConsultation(null, open));
    }

    [Fact]
    public void A_new_agent_may_narrow_who_reaches_it_and_more_tools_only_count_on_an_agent_that_lends_rights()
    {
        var plain = Agent(["projects.*"]);

        // A new agent may name a required policy, which only narrows; its run and view policies and servers count.
        Assert.Empty(NhAssistantAgentRights.OfDefinition(null, Agent(["*.read"], requiredPolicy: "narrow"), null, lendsRights: false));
        Assert.Equal(["runPolicy", "viewPolicy", "mcpServerIds"], NhAssistantAgentRights.OfDefinition(null, Agent([], runPolicy: "run", viewPolicy: "view"), ["planning"], lendsRights: false));

        // On an agent that exists every change of a policy counts, also one that looks narrower.
        Assert.Equal(["requiredPolicy"], NhAssistantAgentRights.OfDefinition(plain, Agent(["projects.*"], requiredPolicy: "narrow"), null, lendsRights: false));
        Assert.Empty(NhAssistantAgentRights.OfDefinition(plain, Agent(["projects.*"]), null, lendsRights: false));

        // An added server counts; a removed one and keeping them do not.
        var assigned = Agent(["projects.*"]);
        assigned.McpServers.Add(new AssistantAgentMcpServer { AgentId = Weekly, McpServerId = "planning" });
        Assert.Equal(["mcpServerIds"], NhAssistantAgentRights.OfDefinition(assigned, Agent(["projects.*"]), ["planning", "finance"], lendsRights: false));
        Assert.Empty(NhAssistantAgentRights.OfDefinition(assigned, Agent(["projects.*"]), [], lendsRights: false));
        Assert.Empty(NhAssistantAgentRights.OfDefinition(assigned, Agent(["projects.*"]), null, lendsRights: false));

        // More tools or autonomy count only when another account's rights get more use, and a narrower selector
        // under one the agent had is no more tools.
        var wider = Agent(["projects.*", "finance.*"], autonomy: NhAiAutonomyLevel.Execute);
        Assert.Empty(NhAssistantAgentRights.OfDefinition(plain, wider, null, lendsRights: false));
        Assert.Equal(["toolSelectors", "autonomy"], NhAssistantAgentRights.OfDefinition(plain, wider, null, lendsRights: true));
        Assert.Empty(NhAssistantAgentRights.OfDefinition(plain, Agent(["projects.search"], autonomy: NhAiAutonomyLevel.Observe), null, lendsRights: true));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_without_the_policy_builds_an_agent_but_changes_none_of_its_rights_in_the_screens(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(new NhAiScriptedChatClient(), provider);
        using var manager = app.CreateClient(Manager, admin: true);
        using var owner = app.CreateClient(Owner, admin: true);

        // The administration can say so beforehand.
        Assert.False((await CatalogAsync(manager)).GetProperty("mayChangeAgentRights").GetBoolean());
        Assert.True((await CatalogAsync(owner)).GetProperty("mayChangeAgentRights").GetBoolean());

        // Creating an agent with plain run settings is open.
        await SucceedsAsync(await manager.PostAsync("/api/assistant/admin/agents", Json(Definition(id: Weekly))), HttpStatusCode.Created);
        var runSettings = $"/api/assistant/admin/agents/{Weekly}/run-settings";
        await SucceedsAsync(await manager.PutAsync(runSettings, Json(new { accountability = "agent" })));

        // Lending an account's rights is refused, says what, and stores nothing.
        await RefusedAsync(
            await manager.PutAsync(runSettings, Json(new { accountability = "service-account", serviceAccountActorId = KnownAccountPrincipalFactory.First, expectedVersion = 1 })),
            "accountability",
            "serviceAccountActorId");
        Assert.Equal(NhAssistantAccountabilities.Agent, (await ConfigurationAsync(app)).RunSettings!.Accountability);

        // Saving the same rights again with something else is open.
        await SucceedsAsync(await manager.PutAsync(runSettings, Json(new { accountability = "agent", maxConcurrentRuns = 2, expectedVersion = 1 })));

        // A required policy on an agent that exists is refused; with the policy it goes ahead.
        var agent = $"/api/assistant/admin/agents/{Weekly}";
        await RefusedAsync(await manager.PutAsync(agent, Json(Definition(id: Weekly, requiredPolicy: RightsPolicy, expectedVersion: 1))), "requiredPolicy");
        await SucceedsAsync(await owner.PutAsync(agent, Json(Definition(id: Weekly, requiredPolicy: RightsPolicy, expectedVersion: 1))));
        await SucceedsAsync(await owner.PutAsync(agent, Json(Definition(id: Weekly, expectedVersion: 2))));

        // Once the agent answers to a technical account, more tools and more autonomy put those rights to more
        // use; fewer tools stay open.
        await SucceedsAsync(await owner.PutAsync(runSettings, Json(new { accountability = "service-account", serviceAccountActorId = KnownAccountPrincipalFactory.First, expectedVersion = 2 })));
        await RefusedAsync(await manager.PutAsync(agent, Json(Definition(id: Weekly, selectors: ["projects.*", "tasks.*"], expectedVersion: 3))), "toolSelectors");
        await RefusedAsync(await manager.PutAsync(agent, Json(Definition(id: Weekly, autonomy: "execute", expectedVersion: 3))), "autonomy");
        await SucceedsAsync(await manager.PutAsync(agent, Json(Definition(id: Weekly, selectors: ["projects.search"], expectedVersion: 3))));

        // Resetting an agent in code changes who reaches it when an override stands in the way.
        var inCode = $"/api/assistant/admin/agents/{CodeAgent}";
        var overridden = await ReadJsonAsync(await owner.PutAsync(inCode, Json(new
        {
            id = CodeAgent,
            displayName = "Project assistant",
            description = "Helps with projects.",
            instructions = "Help with projects.",
            toolSelectors = new[] { "projects.*" },
            requiredPolicy = RightsPolicy,
            autonomy = "propose",
            isEnabled = true,
            expectedVersion = 1
        })));
        Assert.Equal(RightsPolicy, overridden.GetProperty("requiredPolicy").GetString());
        await RefusedAsync(await manager.PostAsync($"{inCode}/reset", null), "requiredPolicy");
        await SucceedsAsync(await owner.PostAsync($"{inCode}/reset", null));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Mcp_servers_take_the_policy_to_connect_and_assign_and_stay_open_to_take_away(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(new NhAiScriptedChatClient(), provider);
        using var manager = app.CreateClient(Manager, admin: true);
        using var owner = app.CreateClient(Owner, admin: true);
        const string Servers = "/api/assistant/admin/mcp-servers";
        var server = new { id = "planning", displayName = "Planning", url = "https://planning.example.org/mcp", authMode = "none", isEnabled = true };

        await RefusedAsync(await manager.PostAsync(Servers, Json(server)), NhAssistantAgentRights.McpServer);
        await SucceedsAsync(await owner.PostAsync(Servers, Json(server)), HttpStatusCode.Created);
        await RefusedAsync(await manager.PutAsync($"{Servers}/planning", Json(server)), NhAssistantAgentRights.McpServer);

        // An assignment gives an agent the tools; the same one again changes nothing and removing it takes away.
        var assignment = $"{Servers}/planning/agents/{CodeAgent}";
        await RefusedAsync(await manager.PutAsync(assignment, null), NhAssistantAgentRights.McpServerIds);
        await SucceedsAsync(await owner.PutAsync(assignment, null));
        await SucceedsAsync(await manager.PutAsync(assignment, null));
        await SucceedsAsync(await manager.DeleteAsync(assignment));

        // Enabling a tool is refused; disabling one is open.
        var tool = $"{Servers}/planning/tools/search";
        await RefusedAsync(await manager.PutAsync(tool, Json(new { isEnabled = true, effect = "read-only" })), NhAssistantAgentRights.McpTool);
        await SucceedsAsync(await manager.PutAsync(tool, Json(new { isEnabled = false, effect = "mutation" })));

        // The organization's account takes the policy; a person's own connection lends nobody's rights, so it
        // gets past the policy to the check of the server, which has no authorization to start here.
        await RefusedAsync(await manager.PostAsync($"{Servers}/planning/connection/authorize", null), NhAssistantAgentRights.McpServer);
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantAdminStore>().AddMcpServerAsync(
            new AssistantMcpServer
            {
                Id = "personal",
                DisplayName = "Personal",
                Url = "https://personal.example.org/mcp",
                AuthMode = NhAssistantMcpAuthModes.None,
                ConnectionScope = NhAssistantMcpConnectionScopes.User,
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            CancellationToken.None);
        using var own = await manager.PostAsync($"{Servers}/personal/connection/authorize", null);
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionNotSupported, (await ReadJsonAsync(own)).GetProperty("code").GetString());

        // Using a server without a person is refused for a person outside a request as well; never stays open.
        var administration = scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>();
        var headless = await administration.SetHeadlessUseAsync("planning", NhAssistantMcpHeadlessUses.Organization, OtherPerson, CancellationToken.None);
        Assert.Equal(NhAssistantErrorCodes.AgentRightsForbidden, NhAssistantResults.CodeOf(headless));
        Assert.True((await administration.SetHeadlessUseAsync("planning", NhAssistantMcpHeadlessUses.Never, OtherPerson, CancellationToken.None)).Success);

        // Application code decides for itself.
        Assert.True((await administration.SetHeadlessUseAsync("planning", NhAssistantMcpHeadlessUses.Organization, Job, CancellationToken.None)).Success);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_tool_going_live_another_account_and_other_routes_take_the_policy_for_a_person_and_not_for_application_code(AssistantTestProvider provider)
    {
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestTaskToolRecorder>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestTaskToolCatalog>());
                services.AddAuthorizationBuilder().AddPolicy(RightsPolicy, policy => policy.RequireAssertion(context =>
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier) == KnownAccountPrincipalFactory.First));
            },
            assistantBuilder: assistant => assistant
                .UseAgentRightsPolicy(RightsPolicy)
                .AddSubjectType<TestSubjectResolver>()
                .ConfigureAgent(CodeAgent, agent => agent
                    .ServiceAccount(KnownAccountPrincipalFactory.First)
                    .ActWithAccountableRights()
                    .AllowConsultation("project", consult => consult.Proposals(proposals => proposals.AtSubject(NhAssistantConsultationRights.Agent)))));
        await using var scope = host.Services.CreateAsyncScope();

        // Shadow and off stay open to everyone; live, where nobody approves any more, takes the policy.
        var switches = scope.ServiceProvider.GetRequiredService<INhAssistantWithoutApprovalSwitches>();
        var tool = TestTaskToolCatalog.Complete.Id;
        Assert.True((await switches.SetAsync(CodeAgent, tool, new NhAssistantWithoutApprovalRequest("shadow", 0), OtherPerson)).Success);
        var notLive = await switches.SetAsync(CodeAgent, tool, new NhAssistantWithoutApprovalRequest("live", 1), OtherPerson);
        Assert.Equal(NhAssistantErrorCodes.AgentRightsForbidden, NhAssistantResults.CodeOf(notLive));
        Assert.True((await switches.SetAsync(CodeAgent, tool, new NhAssistantWithoutApprovalRequest("live", 1), KnownAccountPrincipalFactory.First)).Success);
        Assert.True((await switches.SetAsync(CodeAgent, tool, new NhAssistantWithoutApprovalRequest("off", 2), OtherPerson)).Success);

        // Binding another account is refused before the binding's own check; the same account changes nothing.
        var control = scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>();
        var notBound = await control.SetServiceAccountAsync(CodeAgent, KnownAccountPrincipalFactory.Second, OtherPerson);
        Assert.Equal(NhAssistantErrorCodes.AgentRightsForbidden, NhAssistantResults.CodeOf(notBound));
        Assert.True((await control.SetServiceAccountAsync(CodeAgent, null, OtherPerson)).Success);
        Assert.True((await control.SetServiceAccountAsync(CodeAgent, KnownAccountPrincipalFactory.Second, Job)).Success);

        // The routes of its consultations decide who decides a proposal and with whose rights it executes.
        var routes = new NhAssistantConsultationRouteOverrides(null, false, null);
        var notSwitched = await control.SetConsultationRoutesAsync(CodeAgent, routes, OtherPerson);
        Assert.Equal(NhAssistantErrorCodes.AgentRightsForbidden, NhAssistantResults.CodeOf(notSwitched));
        Assert.True((await control.SetConsultationRoutesAsync(CodeAgent, routes, KnownAccountPrincipalFactory.First)).Success);
        Assert.True((await control.SetConsultationRoutesAsync(CodeAgent, routes, OtherPerson)).Success);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_refuses_a_change_of_rights_before_a_card_and_still_makes_every_other_change(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            // The person without the policy: the catalog says so, the change of rights gets no card, the other does.
            .RespondWithFunctionCall("builder_read_catalog", new { input = new { query = "none" } })
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Weekly, accountability = "service-account", serviceAccountActorId = KnownAccountPrincipalFactory.First, expectedVersion = 1 } })
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Weekly, maxConcurrentRuns = 2, expectedVersion = 1 } })
            .RespondWithText("Only the concurrency changed; someone with the permission changes whose rights it uses.")
            // The person with the policy gets the card.
            .RespondWithFunctionCall("builder_read_catalog", new { input = new { query = "none" } })
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Weekly, accountability = "service-account", serviceAccountActorId = KnownAccountPrincipalFactory.First, expectedVersion = 2 } })
            .RespondWithText("It answers to the technical account now.");
        await using var app = await StartAsync(model, provider);
        using var manager = app.CreateClient(Manager, admin: true);
        using var owner = app.CreateClient(Owner, admin: true);
        await SucceedsAsync(await owner.PostAsync("/api/assistant/admin/agents", Json(Definition(id: Weekly))), HttpStatusCode.Created);
        await SucceedsAsync(await owner.PutAsync($"/api/assistant/admin/agents/{Weekly}/run-settings", Json(new { accountability = "agent" })));

        var chat = await CreateConversationAsync(manager, Builder);
        var asked = await SendAsync(manager, chat, "Let the weekly count answer to the operations account and run two at a time.");

        Assert.False(FunctionResult(model, 0).GetProperty("data").GetProperty("mayChangeAgentRights").GetBoolean());
        var refused = FunctionResult(model, 1).GetRawText();
        Assert.Contains(NhAssistantErrorCodes.AgentRightsForbidden, refused, StringComparison.Ordinal);
        Assert.Contains("accountability", refused, StringComparison.Ordinal);
        var approval = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
        await DecideAsync(manager, chat, approval, "approve");
        var after = (await ConfigurationAsync(app)).RunSettings!;
        Assert.Equal((2, NhAssistantAccountabilities.Agent, 2), (after.Version, after.Accountability, after.MaxConcurrentRuns));

        var other = await CreateConversationAsync(owner, Builder);
        var answered = await SendAsync(owner, other, "Let the weekly count answer to the operations account.");
        Assert.True(FunctionResult(model, 3).GetProperty("data").GetProperty("mayChangeAgentRights").GetBoolean());
        await DecideAsync(owner, other, Assert.Single(answered, evt => evt.Name == "approval.required").Data, "approve");
        Assert.Equal(NhAssistantAccountabilities.ServiceAccount, (await ConfigurationAsync(app)).RunSettings!.Accountability);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task WithoutRightsChanges_keeps_rights_out_of_the_builder_and_in_the_screens_for_everyone(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_read_catalog", new { input = new { query = "none" } })
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Weekly, accountability = "service-account", serviceAccountActorId = KnownAccountPrincipalFactory.First, expectedVersion = 1 } })
            .RespondWithFunctionCall("builder_update_agent", new { input = new { agentId = Weekly, requiredPolicy = RightsPolicy, expectedVersion = 1 } })
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Weekly, maxConcurrentRuns = 2, expectedVersion = 1 } })
            .RespondWithText("I changed the concurrency; whose rights it uses you change on the agent's page.")
            // Once the agent uses another account's rights, more tools count; fewer do not.
            .RespondWithFunctionCall("builder_update_agent", new { input = new { agentId = Weekly, toolSelectors = new[] { "projects.*", "tasks.*" }, expectedVersion = 1 } })
            .RespondWithFunctionCall("builder_update_agent", new { input = new { agentId = Weekly, toolSelectors = new[] { "projects.search" }, expectedVersion = 1 } })
            .RespondWithText("It has fewer tools now.");
        await using var app = await StartAsync(model, provider, withPolicy: false, builder: options => options.WithoutRightsChanges());
        using var administrator = app.CreateClient(Owner, admin: true);
        await SucceedsAsync(await administrator.PostAsync("/api/assistant/admin/agents", Json(Definition(id: Weekly))), HttpStatusCode.Created);
        var runSettings = $"/api/assistant/admin/agents/{Weekly}/run-settings";
        await SucceedsAsync(await administrator.PutAsync(runSettings, Json(new { accountability = "agent" })));

        // The builder knows it may not, and where the person can: nobody sees a card for it.
        var chat = await CreateConversationAsync(administrator, Builder);
        var asked = await SendAsync(administrator, chat, "Let the weekly count answer to the operations account for managers only, and run two at a time.");
        var catalog = FunctionResult(model, 0).GetProperty("data");
        Assert.False(catalog.GetProperty("mayChangeAgentRights").GetBoolean());
        Assert.True(catalog.GetProperty("agentRightsInScreens").GetBoolean());
        foreach (var (index, property) in new[] { (1, "accountability"), (2, "requiredPolicy") })
        {
            var refused = FunctionResult(model, index).GetRawText();
            Assert.Contains(property, refused, StringComparison.Ordinal);
            Assert.Contains("screens", refused, StringComparison.Ordinal);
        }

        // What the builder is good at still works after the approval.
        var approval = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
        await DecideAsync(administrator, chat, approval, "approve");
        var after = (await ConfigurationAsync(app)).RunSettings!;
        Assert.Equal((2, NhAssistantAccountabilities.Agent, 2), (after.Version, after.Accountability, after.MaxConcurrentRuns));

        // The same person changes the rights in the screens: this is a ceiling on the builder, not on them.
        await SucceedsAsync(await administrator.PutAsync(runSettings, Json(new { accountability = "service-account", serviceAccountActorId = KnownAccountPrincipalFactory.First, expectedVersion = 2 })));
        Assert.Equal(NhAssistantAccountabilities.ServiceAccount, (await ConfigurationAsync(app)).RunSettings!.Accountability);

        var narrower = await SendAsync(administrator, chat, "Give it the finance tools, or otherwise only the project search.");
        Assert.Contains("toolSelectors", FunctionResult(model, 4).GetRawText(), StringComparison.Ordinal);
        await DecideAsync(administrator, chat, Assert.Single(narrower, evt => evt.Name == "approval.required").Data, "approve");
        await using var scope = app.Services.CreateAsyncScope();
        var agent = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentDirectory>().GetAsync(Weekly);
        Assert.Equal(["projects.search"], agent!.ToolSelectors);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_package_that_changes_rights_takes_the_policy_and_says_so_in_its_preview(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(new NhAiScriptedChatClient(), provider);
        using var owner = app.CreateClient(Owner, admin: true);
        await SucceedsAsync(await owner.PostAsync("/api/assistant/admin/agents", Json(Definition(id: Weekly))), HttpStatusCode.Created);
        await SucceedsAsync(await owner.PutAsync($"/api/assistant/admin/agents/{Weekly}/run-settings", Json(new { accountability = "subject-responsible" })));

        await using var scope = app.Services.CreateAsyncScope();
        var packages = scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>();
        var exported = await packages.ExportAsync([Weekly], new NhAssistantAgentPackageExportOptions(), Job);
        Assert.True(exported.Success, string.Join(", ", exported.AllErrorMessages));
        var package = exported.Data!.Package;
        var options = new NhAssistantAgentPackageImportOptions();
        await SucceedsAsync(await owner.DeleteAsync($"/api/assistant/admin/agents/{Weekly}"), HttpStatusCode.NoContent);

        // The preview says what a part changes, and the import refuses all of it for a person without the policy.
        var preview = await packages.PreviewImportAsync(package, options);
        Assert.Equal(["accountability"], Assert.Single(preview.Data!.Parts, part => part.Part == NhAssistantPackagePartKinds.RunSettings).RightsChanges);
        var refused = await packages.ImportAsync(package, options, OtherPerson);
        Assert.Equal(NhAssistantErrorCodes.AgentRightsForbidden, NhAssistantResults.CodeOf(refused));
        Assert.Contains("parts.runSettings.accountability", NhAssistantResults.FieldErrorsOf(refused).Keys);
        Assert.Null(await ConfigurationAsync(app));

        // A deploy with application code decides for itself.
        var imported = await packages.ImportAsync(package, options, Job);
        Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));
        Assert.Equal(NhAssistantAccountabilities.SubjectResponsible, (await ConfigurationAsync(app))!.RunSettings!.Accountability);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_adds_no_tools_a_technical_account_could_use_for_a_person_who_may_not_before_a_card(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_update_agent", new { input = new { agentId = Weekly, toolSelectors = new[] { "projects.*" }, expectedVersion = 1 } })
            .RespondWithFunctionCall("builder_update_agent", new { input = new { agentId = Weekly, description = "Counts the changes of last week.", expectedVersion = 1 } })
            .RespondWithText("I changed the description; someone who holds those rights adds the tool.");
        await using var app = await StartAsync(
            model,
            provider,
            withPolicy: false,
            configure: services =>
            {
                // Only the technical account may manage projects, not the person who chats.
                services.AddAuthorization(options => options.AddPolicy(
                    TestProjectToolCatalog.ManagePolicy,
                    policy => policy.RequireClaim(ClaimTypes.Role, "project-manager")));
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, ManagingAccountPrincipalFactory>());
            });
        using var administrator = app.CreateClient(Manager, admin: true);
        await SucceedsAsync(await administrator.PostAsync("/api/assistant/admin/agents", Json(Definition(id: Weekly, selectors: ["projects.search"]))), HttpStatusCode.Created);
        await SucceedsAsync(await administrator.PutAsync(
            $"/api/assistant/admin/agents/{Weekly}/run-settings",
            Json(new { accountability = "service-account", serviceAccountActorId = KnownAccountPrincipalFactory.First })));

        var chat = await CreateConversationAsync(administrator, Builder);
        var asked = await SendAsync(administrator, chat, "Give the weekly count all project tools, and a better description.");

        Assert.Contains(NhAssistantErrorCodes.ServiceAccountNotPermitted, FunctionResult(model, 0).GetRawText(), StringComparison.Ordinal);
        await DecideAsync(administrator, chat, Assert.Single(asked, evt => evt.Name == "approval.required").Data, "approve");
        await using var scope = app.Services.CreateAsyncScope();
        var agent = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentDirectory>().GetAsync(Weekly);
        Assert.Equal((2, ["projects.search"]), (agent!.Version, agent.ToolSelectors.ToArray()), new VersionAndSelectors());
    }

    [Theory]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_the_policy_the_administration_policy_covers_it_all(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(new NhAiScriptedChatClient(), provider, withPolicy: false);
        using var manager = app.CreateClient(Manager, admin: true);

        Assert.True((await CatalogAsync(manager)).GetProperty("mayChangeAgentRights").GetBoolean());
        await SucceedsAsync(await manager.PostAsync("/api/assistant/admin/agents", Json(Definition(id: Weekly))), HttpStatusCode.Created);
        await SucceedsAsync(await manager.PutAsync(
            $"/api/assistant/admin/agents/{Weekly}/run-settings",
            Json(new { accountability = "service-account", serviceAccountActorId = KnownAccountPrincipalFactory.First })));
    }

    [Theory]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_policy_the_application_did_not_register_stops_the_start(AssistantTestProvider provider)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = await StartAsync(new NhAiScriptedChatClient(), provider, more: assistant => assistant.UseAgentRightsPolicy("app.unknown"));
        });

        Assert.Contains("app.unknown", failure.Message, StringComparison.Ordinal);
    }

    private Task<AssistantWebApplication> StartAsync(
        NhAiScriptedChatClient model,
        AssistantTestProvider provider,
        bool withPolicy = true,
        Action<IServiceCollection>? configure = null,
        Func<NhAssistantBuilder, NhAssistantBuilder>? more = null,
        Action<NhAssistantAgentBuilderOptions>? builder = null)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
                services.AddAuthorizationBuilder().AddPolicy(RightsPolicy, policy => policy.RequireAssertion(context =>
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier) == Owner));
                configure?.Invoke(services);
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddAgentBuilder(builder)
                    .ConfigureMcp(mcp => mcp.AllowLoopback = true);
                if (withPolicy)
                {
                    assistant.UseAgentRightsPolicy(RightsPolicy);
                }

                more?.Invoke(assistant);
            },
            provider: provider);
    }

    private static AssistantAgentRunConfiguration RunSettings()
    {
        return new AssistantAgentRunConfiguration
        {
            AgentId = Weekly,
            Accountability = NhAssistantAccountabilities.Agent,
            ElevatedReads = NhAssistantElevatedReadNames.Restricted
        };
    }

    private static AssistantAgentConsultationDefinition Consultation()
    {
        return new AssistantAgentConsultationDefinition
        {
            AgentId = Weekly,
            SubjectType = "project",
            MinimumRole = NhAssistantSubjectRoleNames.Reader,
            ProposalRole = NhAssistantSubjectRoleNames.Participant,
            RoutesJson = NhAssistantDataConsultationRoutes.Write([])
        };
    }

    private static AssistantAgent Agent(
        string[] selectors,
        string? requiredPolicy = null,
        string? runPolicy = null,
        string? viewPolicy = null,
        NhAiAutonomyLevel autonomy = NhAiAutonomyLevel.Propose)
    {
        return new AssistantAgent
        {
            Id = Weekly,
            ToolSelectorsJson = JsonSerializer.Serialize(selectors),
            RequiredPolicy = requiredPolicy,
            RunPolicy = runPolicy,
            ViewPolicy = viewPolicy,
            Autonomy = autonomy
        };
    }

    private static object Definition(
        string id,
        string? requiredPolicy = null,
        string[]? selectors = null,
        string autonomy = "propose",
        int? expectedVersion = null)
    {
        return new
        {
            id,
            displayName = "Weekly count",
            description = "Counts last week's changes.",
            instructions = "Count the changes.",
            toolSelectors = selectors ?? ["projects.*"],
            requiredPolicy,
            autonomy,
            isEnabled = true,
            expectedVersion
        };
    }

    private static async Task<JsonElement> CatalogAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/assistant/admin/run-catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<NhAssistantAgentConfigurationDto> ConfigurationAsync(AssistantWebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().GetAsync(Weekly))!;
    }

    private static async Task SucceedsAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    private static async Task RefusedAsync(HttpResponseMessage response, params string[] fields)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {text}");
            using var document = JsonDocument.Parse(text);
            Assert.Equal(NhAssistantErrorCodes.AgentRightsForbidden, document.RootElement.GetProperty("code").GetString());
            var errors = document.RootElement.GetProperty("errors");
            foreach (var field in fields)
            {
                Assert.Equal(NhAssistantFieldErrors.RightsRequired, errors.GetProperty(field)[0].GetString());
            }
        }
    }

    private static JsonElement FunctionResult(NhAiScriptedChatClient model, int index)
    {
        var result = model.FunctionResults[index].Result;
        return result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client, string agentId)
    {
        using var response = await client.PostAsync("/api/assistant/conversations", Json(new { agentId }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<IReadOnlyList<ServerSentEvent>> SendAsync(HttpClient client, Guid conversationId, string text)
    {
        using var response = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/messages",
            Json(new { text, clientMessageId = Guid.NewGuid().ToString("N") }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAllAsync(response)).Events;
    }

    private static async Task DecideAsync(HttpClient client, Guid conversationId, JsonElement approval, string decision)
    {
        using var response = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/approvals/{approval.GetProperty("approvalId").GetString()}/decide",
            Json(new { decision, expectedProposalHash = approval.GetProperty("proposalHash").GetString() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ReadAllAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static StringContent Json(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");
    }

    private sealed class VersionAndSelectors : IEqualityComparer<(int Version, string[] Selectors)>
    {
        public bool Equals((int Version, string[] Selectors) x, (int Version, string[] Selectors) y)
        {
            return x.Version == y.Version && x.Selectors.SequenceEqual(y.Selectors);
        }

        public int GetHashCode((int Version, string[] Selectors) obj)
        {
            return obj.Version;
        }
    }

    /// <summary>
    /// The technical account, rebuilt with the right to manage projects that the people of these tests do not have.
    /// </summary>
    private sealed class ManagingAccountPrincipalFactory : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            ClaimsPrincipal? principal = actorId == KnownAccountPrincipalFactory.First
                ? new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, actorId), new Claim(ClaimTypes.Role, "project-manager")],
                    "test"))
                : null;
            return ValueTask.FromResult(principal);
        }
    }
}
