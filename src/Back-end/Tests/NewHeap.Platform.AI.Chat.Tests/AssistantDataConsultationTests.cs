using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Consultations defined as data: administrators let other assistants consult an agent within the
/// ceiling in code, the configuration in effect follows the data on every host, data that no longer
/// fits is left out or degraded with problems, and the generated tools appear, disappear and fail
/// closed when they went stale.
/// </summary>
public sealed class AssistantDataConsultationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string StarterId = "project-starter";
    private const string AskingAgentId = "consulting-assistant";
    private const string ReviewerId = "intake-reviewer";
    private const string PlannerId = "project-planner";
    private const string FollowerId = "project-follower";
    private const string ConsultToolId = "assistant.consult.project-starter.project";
    private const string RequestToolId = "assistant.request.project-starter.project";
    private const string ConsultTool = "consult_project-starter_project_v1";
    private const string ChangeStatus = "projects_change_status_v1";
    private const string Admin = "admin-1";
    private const string Member = "member-1";
    private const string ProjectId = "project-0001";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Saving_checks_a_consultation_against_the_ceiling_and_its_agent_and_puts_it_in_effect(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new NhAiScriptedChatClient());
        await CreateStarterAsync(host, withRunSettings: false);

        // An agent created in the administration needs run settings (AC18).
        Assert.Equal(NhAssistantErrorCodes.AgentNotConfigured, Code(await SaveAsync(host, StarterId, Read())));
        await SaveRunSettingsAsync(host, NhAssistantAccountabilities.ServiceAccount, KnownAccountPrincipalFactory.First, null);

        // Every field within the builder's bounds and the ceiling.
        Assert.Equal("routes", Field(await SaveAsync(host, StarterId, Read() with { Routes = [NhAssistantConsultationRoutes.SubjectAskerRights] }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("routes", Field(await SaveAsync(host, StarterId, Read() with { Routes = [NhAssistantConsultationRoutes.SubjectAgentRights, NhAssistantConsultationRoutes.SubjectAskerRights] }), NhAssistantFieldErrors.Invalid));
        Assert.Equal("description", Field(await SaveAsync(host, StarterId, Read() with { Description = " " }), NhAssistantFieldErrors.Required));
        Assert.Equal("description", Field(await SaveAsync(host, StarterId, Read() with { Description = new string('x', 1_001) }), NhAssistantFieldErrors.TooLong));
        Assert.Equal("instructions", Field(await SaveAsync(host, StarterId, Read() with { Instructions = new string('x', 4_001) }), NhAssistantFieldErrors.TooLong));
        Assert.Equal("askerPolicy", Field(await SaveAsync(host, StarterId, Read() with { AskerPolicy = "app.unlisted" }), NhAssistantFieldErrors.NotFound));
        Assert.Equal("minimumRole", Field(await SaveAsync(host, StarterId, Read() with { MinimumRole = "owner" }), NhAssistantFieldErrors.Invalid));
        Assert.Equal("limits", Field(await SaveAsync(host, StarterId, Read() with { MaxToolCalls = 33 }), NhAssistantFieldErrors.Invalid));
        Assert.Equal("limits", Field(await SaveAsync(host, StarterId, Read() with { MaxToolCalls = 9 }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("proposalPolicy", Field(await SaveAsync(host, StarterId, Read() with { Routes = [NhAssistantConsultationRoutes.AskerDecides], ProposalPolicy = "missing" }), NhAssistantFieldErrors.NotFound));
        Assert.Equal("proposalPolicy", Field(await SaveAsync(host, StarterId, Read() with { Routes = [NhAssistantConsultationRoutes.SubjectAgentRights], ProposalPolicy = "initiator-only" }), NhAssistantFieldErrors.Invalid));
        Assert.Equal("subjectType", Field(await SaveAsync(host, StarterId, Read(), "ghost-type"), NhAssistantFieldErrors.NotFound));
        // Code agents get consultations as data only when they opt in, and never for a type in code.
        Assert.Equal(NhAssistantErrorCodes.ConsultationsNotAllowed, Code(await SaveAsync(host, ReviewerId, Read())));
        Assert.Equal(NhAssistantErrorCodes.ConsultationInCode, Code(await SaveAsync(host, PlannerId, Read())));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await SaveAsync(host, "missing-agent", Read())));

        var saved = await SaveAsync(host, StarterId, Read() with
        {
            Routes = [NhAssistantConsultationRoutes.AskerDecides, NhAssistantConsultationRoutes.SubjectAgentRights],
            Instructions = "Say whether the draft can start.",
            AllowRequests = true,
            Reason = "Pilot"
        });
        Assert.True(saved.Success);
        var dto = saved.Data!;
        Assert.Equal((1, true, null, ConsultToolId, RequestToolId), (dto.Version, dto.InEffect, dto.Problem, dto.ConsultToolId, dto.RequestToolId));
        Assert.Equal([NhAssistantConsultationRoutes.AskerDecides, NhAssistantConsultationRoutes.SubjectAgentRights], dto.RoutesInEffect);
        // Defaults of code, capped by the ceiling: 4 tool calls, 60 seconds, 4,000 characters, 20 messages.
        Assert.Equal((4, 60, 4_000, 20), (dto.MaxToolCalls, dto.TimeoutSeconds, dto.MaxAnswerChars, dto.MaxHistoryMessages));
        // The second key: the asking agent selects the generated tool (AC5).
        Assert.Equal([AskingAgentId], dto.AskingAgents);

        var options = Assert.Single(State(host).AgentRuns[StarterId].Consultations);
        Assert.Equal((NhAssistantConsultationSources.Data, (int?)1, NhAssistantConsultationRights.Agent, true), (options.Source, options.DataVersion, options.AtSubjectRights, options.AskerDecides));
        Assert.Equal("Say whether the draft can start.", options.Instructions!.Content);
        // In code nothing changed.
        Assert.False(State(host).CodeAgentRuns.ContainsKey(StarterId));
        Assert.DoesNotContain(
            host.Services.GetServices<INhAiToolCatalog>().OfType<NhAssistantConsultationToolCatalog>().Single().Descriptors,
            descriptor => descriptor.Id == ConsultToolId);
        Assert.Equal(
            [ConsultToolId, RequestToolId],
            host.Services.GetRequiredService<NhAssistantDataConsultationToolCatalog>().Descriptors.Select(descriptor => descriptor.Id));

        // A change is the next version; a stale version is refused.
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await SaveAsync(host, StarterId, Read())));
        var changed = await SaveAsync(host, StarterId, Read() with { Routes = [NhAssistantConsultationRoutes.AskerDecides, NhAssistantConsultationRoutes.SubjectAgentRights], ExpectedVersion = 1 });
        Assert.Equal(2, changed.Data!.Version);

        // The route switches of the agent cover its data consultations (AC10).
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>();
            Assert.Equal(
                NhAssistantErrorCodes.ConsultRouteNotAllowed,
                Code(await control.SetConsultationRoutesAsync(StarterId, new NhAssistantConsultationRouteOverrides(null, null, true), Admin)));
            var switched = await control.SetConsultationRoutesAsync(StarterId, new NhAssistantConsultationRouteOverrides(null, false, null), Admin, "Incident");
            Assert.True(switched.Success);
            var agent = (await host.Services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(StarterId, includeDisabled: true, CancellationToken.None))!;
            var routing = NhAssistantConsultationRouting.Of(State(host).AgentRuns[StarterId].Consultations.Single(), agent.ConsultationRoutes);
            Assert.Equal((true, (NhAssistantConsultationRights?)null), (routing.AskerDecides, routing.AtSubjectRights));
        }

        // Run settings stay while a consultation belongs to the agent (AC18).
        Assert.Equal(NhAssistantErrorCodes.RunSettingsInUse, Code(await ConfigureAsync(host, configuration => configuration.RemoveRunSettingsAsync(StarterId, 1, Admin))));

        // Answering for itself, the agent has no rights of its own: the route with its rights is
        // dropped and the consultation keeps answering (AC11).
        await SaveRunSettingsAsync(host, NhAssistantAccountabilities.Agent, null, 1);
        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(StarterId)))!;
        var degraded = Assert.Single(view.DataConsultations);
        Assert.Equal((true, NhAssistantRunConfigurationProblems.AgentHasNoRights), (degraded.InEffect, degraded.Problem));
        Assert.Equal([NhAssistantConsultationRoutes.AskerDecides], degraded.RoutesInEffect);
        Assert.Null(State(host).AgentRuns[StarterId].Consultations.Single().AtSubjectRights);
        Assert.NotNull(view.ConsultationCeiling);
        Assert.Empty(view.CodeConsultations);

        var planner = (await ConfigureAsync(host, configuration => configuration.GetAsync(PlannerId)))!;
        Assert.Equal(["project"], planner.CodeConsultations.Select(item => item.SubjectType));
        Assert.Equal([NhAssistantConsultationRoutes.AskerDecides], planner.ConsultationCeiling!.Routes);

        // Removing takes it out of effect at once.
        Assert.Equal(NhAssistantErrorCodes.ConsultationInCode, Code(await ConfigureAsync(host, configuration => configuration.RemoveConsultationAsync(PlannerId, "project", null, Admin))));
        Assert.True((await ConfigureAsync(host, configuration => configuration.RemoveConsultationAsync(StarterId, "project", 2, Admin, "Pilot ended"))).Success);
        Assert.Equal(NhAssistantErrorCodes.ConsultationNotFound, Code(await ConfigureAsync(host, configuration => configuration.RemoveConsultationAsync(StarterId, "project", null, Admin))));
        Assert.Empty(State(host).AgentRuns[StarterId].Consultations);
        Assert.Empty(host.Services.GetRequiredService<NhAssistantDataConsultationToolCatalog>().Descriptors);

        // Audited with version and instructions hash, never the texts (AC19).
        var audited = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentConsultationDefinitionChanged).ToArray();
        Assert.Equal(["created", "changed", "removed"], audited.Select(evt => evt.ResultCode));
        Assert.Equal([1, 2, 2], audited.Select(evt => evt.ConsultationVersion ?? 0));
        Assert.All(audited, evt => Assert.Equal(("project", NhAssistantConsultationSources.Data, StarterId), (evt.SubjectType, evt.ConsultationSource, evt.AgentId)));
        Assert.Equal(NhAssistantAgentCatalog.ComputeHash("Say whether the draft can start."), audited[0].InstructionsHash);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_chat_consults_an_administration_agent_through_its_data_consultation_until_the_consultation_is_removed(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient()
            .RespondWithText("The draft can start once the budget is approved.")
            .RespondWithText("It still can start.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Can the draft start?" } }, "call-1")
            .RespondWithText("The starter says the draft can start once the budget is approved.");
        await using var host = await CreateHostAsync(provider, target, asker);
        await CreateStarterAsync(host, withRunSettings: true);
        var catalog = host.Services.GetRequiredService<NhAssistantDataConsultationToolCatalog>();
        var changes = 0;
        catalog.Changed += (_, _) => Interlocked.Increment(ref changes);

        Assert.True((await SaveAsync(host, StarterId, Read())).Success);
        Assert.Equal(1, changes);
        var descriptor = Assert.Single(catalog.Descriptors);
        Assert.Equal((ConsultToolId, ConsultTool, NhAiToolExposure.Local | NhAiToolExposure.Agent), (descriptor.Id, descriptor.ExportName, descriptor.Exposure));
        Assert.Equal(NhAssistantDataConsultationToolCatalog.CatalogId, descriptor.CatalogId);
        NhAiToolCatalogAttestation.Validate(catalog.Current, host.Services);

        // The asking agent sees the tool from its next turn on, and the starter answers with the
        // member's rights.
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
        var turn = await host.SendAsync(conversation.Id, "Can the draft project start?", Member);
        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Contains("The draft can start once the budget is approved.", asker.FunctionResults.Single().Result?.ToString());
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var consultation = await context.Consultations.SingleAsync();
            Assert.Equal((Member, (int?)1, NhAssistantConsultationStatuses.Completed), (consultation.AskerActorId, consultation.ConsultationVersion, consultation.Status));
        }
        var consulted = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentConsulted);
        Assert.Equal((NhAssistantConsultationSources.Data, (int?)1), (consulted.ConsultationSource, consulted.ConsultationVersion));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;

            // The tools act for a person: an agent that acts with its account's rights and does not
            // delegate from its initiator is never offered them, also for a data consultation (AC1).
            var authorizer = services.GetRequiredService<INhAiAgentToolAuthorizer>();
            var accountable = AgentScope() with
            {
                Accountability = NhAiAgentAccountability.ServiceAccount,
                AccountableOwnerId = KnownAccountPrincipalFactory.First,
                ActWithAccountableRights = true,
                InitiatorActorId = Member
            };
            Assert.Equal(NhAiGrantSource.None, await authorizer.AuthorizeAsync(descriptor, accountable));
            Assert.Equal(NhAiGrantSource.None, await authorizer.AuthorizeAsync(descriptor, accountable with { Grants = [new NhAiAgentGrant("assistant.*")] }));
            Assert.Equal(NhAiGrantSource.Delegated, await authorizer.AuthorizeAsync(descriptor, accountable with { DelegateFromInitiator = true }));
            using (NhAiAgentInvocationScopes.Begin(accountable))
            {
                var discovered = await services.GetRequiredService<INhAiToolDiscoveryService>().DiscoverAsync(
                    new NhAiToolDiscoveryRequest(AgentContext(), NhAiToolExposure.Agent));
                Assert.DoesNotContain(discovered, item => item.Id == ConsultToolId);
            }

            // A call bound to the consultation in effect works; one with another contract fails closed.
            var handler = services.GetRequiredService<NhAssistantConsultationToolHandler>();
            var answered = await handler.ConsultDataAsync(StarterId, "project", descriptor.ContractHash, Question(), PersonContext(), CancellationToken.None);
            Assert.True(answered.Success);
            var stale = await handler.ConsultDataAsync(StarterId, "project", new string('0', 64), Question(), PersonContext(), CancellationToken.None);
            Assert.Contains(stale.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ConsultAgentUnavailable);
        }

        // After removal the tool is gone and a call of a function built before fails closed (AC12).
        Assert.True((await ConfigureAsync(host, configuration => configuration.RemoveConsultationAsync(StarterId, "project", 1, Admin))).Success);
        Assert.Equal(2, changes);
        Assert.Empty(catalog.Descriptors);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var removed = await scope.ServiceProvider.GetRequiredService<NhAssistantConsultationToolHandler>()
                .ConsultDataAsync(StarterId, "project", descriptor.ContractHash, Question(), PersonContext(), CancellationToken.None);
            Assert.Contains(removed.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ConsultAgentUnavailable);
        }
        Assert.Equal(2, target.Requests.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Data_that_no_longer_fits_is_left_out_or_degraded_and_another_host_follows_through_the_stamp(AssistantTestProvider provider)
    {
        // Every check reads the stamp, so the test need not wait for the interval.
        await using var host = await CreateHostAsync(
            provider,
            new NhAiScriptedChatClient(),
            new NhAiScriptedChatClient(),
            timing: new NhAssistantTiming { RunConfigurationCheckInterval = TimeSpan.Zero });
        await CreateStarterAsync(host, withRunSettings: true);
        var catalog = host.Services.GetRequiredService<NhAssistantDataConsultationToolCatalog>();
        await catalog.RefreshAsync(CancellationToken.None);
        Assert.Empty(catalog.Descriptors);

        // Another host stored what this host's code no longer allows.
        var store = host.Services.GetRequiredService<NhAssistantRunConfigurationStore>();
        await StoreAsync(store, Row(StarterId, "project", row =>
        {
            row.RoutesJson = "[\"asker-decides\",\"subject-asker-rights\"]";
            row.MaxToolCalls = 20;
            row.TimeoutSeconds = 600;
            row.AllowRequests = true;
            row.ExposeToMcp = true;
        }));
        await StoreAsync(store, Row(StarterId, "order", row => row.AskerPolicy = "app.unlisted"));
        await StoreAsync(store, Row(ReviewerId, "project"));
        await StoreAsync(store, Row(PlannerId, "project"));

        // A listing on this host reads the stamp first (AC23).
        await catalog.RefreshAsync(CancellationToken.None);

        var problems = host.Services.GetRequiredService<NhAssistantRunConfigurations>().Problems
            .Where(problem => problem.Kind == NhAssistantRunConfigurationProblems.ConsultationKind)
            .Select(problem => (problem.AgentId, problem.Id, problem.Code))
            .ToArray();
        Assert.Equivalent(
            new[]
            {
                (PlannerId, "project", NhAssistantRunConfigurationProblems.ConflictsWithCode),
                (StarterId, "order", NhAssistantRunConfigurationProblems.UnknownAgentPolicy),
                (StarterId, "project", NhAssistantRunConfigurationProblems.RouteNotAllowed),
                (StarterId, "project", NhAssistantRunConfigurationProblems.LimitAboveCeiling),
                (ReviewerId, "project", NhAssistantRunConfigurationProblems.NotConsultable)
            },
            problems,
            strict: true);

        // The degraded consultation stays in effect with what still fits: no route at the subject,
        // limits at the ceiling's, requests and MCP as the ceiling allows them.
        var options = Assert.Single(State(host).AgentRuns[StarterId].Consultations);
        Assert.Equal((true, (NhAssistantConsultationRights?)null), (options.AskerDecides, options.AtSubjectRights));
        Assert.Equal((8, TimeSpan.FromSeconds(120), true), (options.MaxToolCalls, options.Timeout, options.AllowRequests));
        Assert.True(options.Exposure.HasFlag(NhAiToolExposure.Mcp));
        Assert.Equal([ConsultToolId, RequestToolId], catalog.Descriptors.Select(descriptor => descriptor.Id));
        Assert.DoesNotContain(State(host).AgentRuns[ReviewerId].Consultations, consultation => consultation.IsData);
        Assert.DoesNotContain(State(host).AgentRuns[PlannerId].Consultations, consultation => consultation.IsData);

        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(StarterId)))!;
        var order = Assert.Single(view.DataConsultations, item => item.SubjectType == "order");
        Assert.Equal((false, NhAssistantRunConfigurationProblems.UnknownAgentPolicy), (order.InEffect, order.Problem));
        var project = Assert.Single(view.DataConsultations, item => item.SubjectType == "project");
        Assert.True(project.InEffect);
        Assert.Equal([NhAssistantRunConfigurationProblems.RouteNotAllowed, NhAssistantRunConfigurationProblems.LimitAboveCeiling], project.Problems);
        Assert.Equal([NhAssistantConsultationRoutes.AskerDecides], project.RoutesInEffect);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_data_consultation_offers_the_tools_an_administrator_chose_and_fails_closed_when_they_no_longer_fit(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient()
            .RespondWithText("The draft can start.")
            .RespondWithText("The draft can start; I looked at nothing.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Can the draft start?" } }, "call-1")
            .RespondWithText("The starter says it can start.")
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Can the draft start?" } }, "call-2")
            .RespondWithText("The starter says it can start.");
        await using var host = await CreateHostAsync(provider, target, asker);
        await CreateStarterAsync(host, withRunSettings: true);

        // Saving refuses a selector outside the agent's own selectors and one that is none (CT4).
        Assert.Equal("toolSelectors", Field(await SaveAsync(host, StarterId, Read() with { ToolSelectors = ["orders.*"] }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("toolSelectors", Field(await SaveAsync(host, StarterId, Read() with { ToolSelectors = ["Projects Search"] }), NhAssistantFieldErrors.Invalid));

        var saved = await SaveAsync(host, StarterId, Read() with { ToolSelectors = ["projects.search"] });
        Assert.True(saved.Success);
        Assert.Equal(["projects.search"], saved.Data!.ToolSelectors);
        Assert.Equal(["projects.search"], saved.Data.ToolSelectorsInEffect);
        Assert.Equal(["projects.search"], State(host).AgentRuns[StarterId].Consultations.Single().ToolSelectors);

        // The consulted model sees only the chosen tool (CT1).
        await host.SendAsync((await host.CreateConversationAsync(Member, AskingAgentId)).Id, "Can the draft project start?", Member);
        Assert.Contains(ToolNames(target.Requests[0]), name => name == "projects_search_v1");
        Assert.DoesNotContain(ToolNames(target.Requests[0]), name => name == ChangeStatus);

        // The agent's own selectors change and no longer cover the selection: it drops with a problem
        // and the consultation offers no tools, never every tool of the agent (CT4).
        await UpdateStarterToolsAsync(host, [TestProjectToolCatalog.ChangeStatus.Id]);
        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(StarterId)))!;
        var degraded = Assert.Single(view.DataConsultations);
        Assert.Equal((true, NhAssistantRunConfigurationProblems.ToolsNotAllowed), (degraded.InEffect, degraded.Problem));
        Assert.Equal(["projects.search"], degraded.ToolSelectors);
        Assert.Empty(degraded.ToolSelectorsInEffect!);
        Assert.Contains(
            view.Problems,
            problem => (problem.Kind, problem.Id, problem.Code) == (NhAssistantRunConfigurationProblems.ConsultationKind, "project", NhAssistantRunConfigurationProblems.ToolsNotAllowed));
        Assert.Empty(State(host).AgentRuns[StarterId].Consultations.Single().ToolSelectors!);
        await host.SendAsync((await host.CreateConversationAsync(Member, AskingAgentId)).Id, "Can the draft project start?", Member);
        Assert.DoesNotContain(ToolNames(target.Requests[^1]), name => name.StartsWith("projects_", StringComparison.Ordinal));

        // A save refuses what a load drops; without a selection every tool of the agent is offered
        // again (CT2), as the consultation's next version.
        Assert.Equal("toolSelectors", Field(await SaveAsync(host, StarterId, Read() with { ToolSelectors = ["projects.search"], ExpectedVersion = 1 }), NhAssistantFieldErrors.NotAllowed));
        var cleared = await SaveAsync(host, StarterId, Read() with { ToolSelectors = [], ExpectedVersion = 1 });
        Assert.True(cleared.Success);
        Assert.Equal((2, (IReadOnlyList<string>?)null, (IReadOnlyList<string>?)null), (cleared.Data!.Version, cleared.Data.ToolSelectors, cleared.Data.ToolSelectorsInEffect));
        Assert.Null(State(host).AgentRuns[StarterId].Consultations.Single().ToolSelectors);
        Assert.Equal(
            [1, 2],
            host.Business.Events
                .Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentConsultationDefinitionChanged)
                .Select(evt => evt.ConsultationVersion ?? 0));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_administration_agent_with_propose_autonomy_proposes_through_its_data_consultation_on_each_route(AssistantTestProvider provider)
    {
        // AC34 (b): the starter keeps Propose autonomy. A data consultation with a route offers it the
        // tool that changes state, and its call waits as a proposal on that route until it is decided.
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithText("I proposed to start it; you decide it now.")
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-2")
            .RespondWithText("I proposed to start it for the project's approvers.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Can the draft start?" } }, "call-1")
            .RespondWithText("The project started.")
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Can the draft start?" } }, "call-2")
            .RespondWithText("The project's approvers decide it.");
        await using var host = await CreateHostAsync(provider, target, asker);
        await CreateStarterAsync(host, withRunSettings: true, NhAiAutonomyLevel.Propose);

        // A: the person who asked decides it on the card in their own chat; nothing ran before.
        Assert.True((await SaveAsync(host, StarterId, Read() with
        {
            Routes = [NhAssistantConsultationRoutes.AskerDecides],
            ProposalPolicy = "initiator-only"
        })).Success);
        var chat = await host.CreateConversationAsync(Member, AskingAgentId);
        var turn = await host.SendAsync(chat.Id, "Can the draft project start?", Member);

        Assert.Contains(target.Requests[0].Options?.Tools ?? [], tool => tool.Name == ChangeStatus);
        Assert.Empty(host.Tools.StatusChanges);
        var card = turn.Single<NhAssistantApprovalRequiredEvent>().Approval;
        var store = host.Services.GetRequiredService<INhAssistantStore>();
        var inline = (await store.FindApprovalAsync(card.ApprovalId, CancellationToken.None))!;
        Assert.Equal((NhAssistantApprovalStatuses.Pending, NhAssistantApprovalModes.Inline), (inline.Status, inline.Mode));
        await host.DecideAsync(chat.Id, card, approve: true, userId: Member);
        Assert.Equal("active", Assert.Single(host.Tools.StatusChanges).Status);
        Assert.Equal(NhAssistantApprovalStatuses.Executed, (await store.FindApprovalAsync(card.ApprovalId, CancellationToken.None))!.Status);

        // B: with the agent's own rights, those of its technical account, it waits for the project's
        // approvers and does not run.
        Assert.True((await SaveAsync(host, StarterId, Read() with
        {
            Routes = [NhAssistantConsultationRoutes.SubjectAgentRights],
            ExpectedVersion = 1
        })).Success);
        var later = await host.SendAsync((await host.CreateConversationAsync(Member, AskingAgentId)).Id, "Can the draft project start?", Member);

        Assert.Contains(target.Requests[2].Options?.Tools ?? [], tool => tool.Name == ChangeStatus);
        Assert.Empty(later.Events.OfType<NhAssistantApprovalRequiredEvent>());
        Guid consultationId;
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            consultationId = await context.Consultations
                .Where(consultation => consultation.ConsultationVersion == 2)
                .Select(consultation => consultation.Id)
                .SingleAsync();
        }
        var waiting = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>()
            .GetConsultationApprovalsAsync(consultationId, CancellationToken.None));
        Assert.Equal(
            (NhAssistantApprovalStatuses.Pending, NhAssistantApprovalModes.Deferred, NhAiGrantSource.Accountable),
            (waiting.Status, waiting.Mode, waiting.GrantSource));
        Assert.Single(host.Tools.StatusChanges);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task MCP_clients_see_a_data_consultation_only_within_the_ceiling(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new NhAiScriptedChatClient(), allowMcp: false);
        await CreateStarterAsync(host, withRunSettings: true);
        var catalog = host.Services.GetRequiredService<NhAssistantDataConsultationToolCatalog>();
        Assert.False(catalog.MayExportToMcp);

        Assert.Equal("exposeToMcp", Field(await SaveAsync(host, StarterId, Read() with { ExposeToMcp = true }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("allowRequests", Field(await SaveAsync(host, StarterId, Read() with { AllowRequests = true }), NhAssistantFieldErrors.NotAllowed));
        Assert.True((await SaveAsync(host, StarterId, Read())).Success);
        Assert.Empty(await McpToolNamesAsync(host));

        await using var open = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new NhAiScriptedChatClient(), allowMcp: true);
        await CreateStarterAsync(open, withRunSettings: true);
        Assert.True(open.Services.GetRequiredService<NhAssistantDataConsultationToolCatalog>().MayExportToMcp);
        Assert.True((await SaveAsync(open, StarterId, Read())).Success);
        // A consultation asks for MCP itself; the ceiling only allows it.
        Assert.Empty(await McpToolNamesAsync(open));
        Assert.True((await SaveAsync(open, StarterId, Read() with { ExposeToMcp = true, ExpectedVersion = 1 })).Success);
        Assert.Equal([ConsultTool], await McpToolNamesAsync(open));
        Assert.True((await ConfigureAsync(open, configuration => configuration.RemoveConsultationAsync(StarterId, "project", 2, Admin))).Success);
        Assert.Empty(await McpToolNamesAsync(open));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_consultation_that_starts_off_is_not_offered_until_an_administrator_switches_it_on(AssistantTestProvider provider)
    {
        // The application lets every new consultation start off (S8-2).
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new NhAiScriptedChatClient(), startOff: true);
        await CreateStarterAsync(host, withRunSettings: true);
        var catalog = host.Services.GetRequiredService<NhAssistantDataConsultationToolCatalog>();
        Assert.True((await ConfigureAsync(host, configuration => configuration.GetAsync(StarterId)))!.ConsultationCeiling!.StartsOff);

        // Saved, it is checked as every save checks it, and stays out of the tool lists.
        var saved = await SaveAsync(host, StarterId, Read());
        Assert.Equal((1, true, false, (string?)null), (saved.Data!.Version, saved.Data.Off, saved.Data.InEffect, saved.Data.Problem));
        Assert.Empty(State(host).AgentRuns[StarterId].Consultations);
        Assert.Empty(catalog.Descriptors);

        // Only a new consultation starts off; a change keeps it off.
        Assert.Equal("startsOff", Field(await SaveAsync(host, StarterId, Read() with { ExpectedVersion = 1, StartsOff = true }), NhAssistantFieldErrors.Invalid));
        var changed = await SaveAsync(host, StarterId, Read() with { Instructions = "Name the budget.", ExpectedVersion = 1 });
        Assert.Equal((2, true), (changed.Data!.Version, changed.Data.Off));
        Assert.Empty(catalog.Descriptors);

        // Switched on, asking agents get its tool; switched off again, a call built before fails closed.
        var on = await ConfigureAsync(host, configuration => configuration.SetConsultationStateAsync(StarterId, "project", true, Admin, "Reviewed"));
        Assert.Equal((2, false, true), (on.Data!.Version, on.Data.Off, on.Data.InEffect));
        Assert.Single(State(host).AgentRuns[StarterId].Consultations);
        var descriptor = Assert.Single(catalog.Descriptors);
        Assert.Equal(ConsultToolId, descriptor.Id);
        Assert.True((await ConfigureAsync(host, configuration => configuration.SetConsultationStateAsync(StarterId, "project", false, Admin))).Data!.Off);
        Assert.Empty(catalog.Descriptors);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var refused = await scope.ServiceProvider.GetRequiredService<NhAssistantConsultationToolHandler>()
                .ConsultDataAsync(StarterId, "project", descriptor.ContractHash, Question(), PersonContext(), CancellationToken.None);
            Assert.Contains(refused.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ConsultAgentUnavailable);
        }

        // Switching what is already so changes nothing; unknown and code consultations are refused.
        Assert.True((await ConfigureAsync(host, configuration => configuration.SetConsultationStateAsync(StarterId, "project", false, Admin))).Success);
        Assert.Equal(NhAssistantErrorCodes.ConsultationNotFound, Code(await ConfigureAsync(host, configuration => configuration.SetConsultationStateAsync(StarterId, "order", true, Admin))));
        Assert.Equal(NhAssistantErrorCodes.ConsultationInCode, Code(await ConfigureAsync(host, configuration => configuration.SetConsultationStateAsync(PlannerId, "project", true, Admin))));
        var audited = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentConsultationDefinitionChanged).ToArray();
        Assert.Equal(["created", "off", "changed", "on", "off"], audited.Select(evt => evt.ResultCode));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_the_applications_choice_a_new_consultation_is_offered_unless_its_administrator_lets_it_start_off(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new NhAiScriptedChatClient());
        await CreateStarterAsync(host, withRunSettings: true);
        Assert.False((await ConfigureAsync(host, configuration => configuration.GetAsync(StarterId)))!.ConsultationCeiling!.StartsOff);

        // As before, a consultation is offered once saved.
        var offered = await SaveAsync(host, StarterId, Read());
        Assert.Equal((false, true), (offered.Data!.Off, offered.Data.InEffect));
        Assert.True((await ConfigureAsync(host, configuration => configuration.RemoveConsultationAsync(StarterId, "project", 1, Admin))).Success);

        // An administrator lets a new one start off, as a skill may.
        var off = await SaveAsync(host, StarterId, Read() with { StartsOff = true });
        Assert.Equal((true, false), (off.Data!.Off, off.Data.InEffect));
        Assert.Empty(host.Services.GetRequiredService<NhAssistantDataConsultationToolCatalog>().Descriptors);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_consultation_as_data_takes_requests_only_and_answers_requests_where_the_ceiling_says(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new NhAiScriptedChatClient(), answerRequests: true);
        await CreateStarterAsync(host, withRunSettings: true);
        var requestsOnly = Read() with { AllowRequests = true, RequestsOnly = true };

        // Requests only keeps its requests and leaves out what only a consultation's own turn uses (FB30).
        Assert.Equal("allowRequests", Field(await SaveAsync(host, StarterId, requestsOnly with { AllowRequests = false }), NhAssistantFieldErrors.Required));
        Assert.Equal("routes", Field(await SaveAsync(host, StarterId, requestsOnly with { Routes = [NhAssistantConsultationRoutes.AskerDecides] }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("instructions", Field(await SaveAsync(host, StarterId, requestsOnly with { Instructions = "Answer briefly." }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("minimumRole", Field(await SaveAsync(host, StarterId, requestsOnly with { MinimumRole = NhAssistantSubjectRoleNames.Participant }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("proposalPolicy", Field(await SaveAsync(host, StarterId, requestsOnly with { ProposalPolicy = "initiator-only" }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("limits", Field(await SaveAsync(host, StarterId, requestsOnly with { MaxHistoryMessages = 5 }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("toolSelectors", Field(await SaveAsync(host, StarterId, requestsOnly with { ToolSelectors = ["projects.*"] }), NhAssistantFieldErrors.NotAllowed));

        var saved = await SaveAsync(host, StarterId, requestsOnly);
        Assert.True(saved.Success, Code(saved));
        var dto = saved.Data!;
        Assert.Equal((true, true, true, string.Empty, RequestToolId), (dto.InEffect, dto.RequestsOnly, dto.AnswerRequestsInAskingConversation, dto.ConsultToolId, dto.RequestToolId));
        Assert.Empty(dto.RoutesInEffect);
        // The asking agent selects consult tools only, so nobody asks this consultation yet.
        Assert.Empty(dto.AskingAgents);
        Assert.Equal(
            [RequestToolId],
            host.Services.GetRequiredService<NhAssistantDataConsultationToolCatalog>().Descriptors.Select(descriptor => descriptor.Id));
        var options = Assert.Single(State(host).AgentRuns[StarterId].Consultations);
        Assert.Equal((true, true, NhAssistantRequestAnswerRoute.AskingConversation), (options.RequestsOnly, options.AllowRequests, options.RequestAnswerRoute));
        Assert.Empty(options.AllowedRoutes);

        // A package carries the flag without the settings of a consultation's own turn, and its import
        // saves the same consultation again; only a consultation that takes requests only names it.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var row = await context.Set<AssistantAgentConsultationDefinition>().SingleAsync(item => item.AgentId == StarterId);
            Assert.True(row.RequestsOnly);
            var part = NhAssistantAgentPackageParts.ConsultationOf(row);
            Assert.Equal((true, true), (part.RequestsOnly, part.AllowRequests));
            Assert.Equal((null, null, null, null, null), (part.Instructions, part.MinimumRole, part.ProposalRole, part.MaxToolCalls, part.MaxHistoryMessages));
            Assert.Null(part.Routes);
            Assert.Equal("true", NhAssistantPackageFields.Of(row)["requestsOnly"]);
            row.RequestsOnly = false;
            Assert.False(NhAssistantPackageFields.Of(row).ContainsKey("requestsOnly"));
            Assert.Null(NhAssistantAgentPackageParts.ConsultationOf(row).RequestsOnly);
            Assert.Contains("requestsOnly", NhAssistantPackageLockFields.Consultation);

            var imported = await SaveAsync(host, StarterId, NhAssistantAgentPackageParts.RequestOf(part, 1, "Import"));
            Assert.True(imported.Success, Code(imported));
            Assert.Equal((2, true), (imported.Data!.Version, imported.Data.RequestsOnly));
        }
    }

    [Fact]
    public void Requests_answer_in_the_asking_conversation_only_when_both_ceilings_say_so()
    {
        var application = Ceiling(data => data.AllowRequests().AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation));
        var agent = new NhAssistantConsultationCeiling();
        new NhAssistantConsultationCeilingBuilder(agent).AllowRequests().AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation);
        Assert.Equal(NhAssistantRequestAnswerRoute.AskingConversation, application.Intersect(agent).RequestAnswerRoute);
        Assert.Null(agent.OutsideOf(application));

        var threadOnly = Ceiling(data => data.AllowRequests());
        Assert.Equal(NhAssistantRequestAnswerRoute.AgentThread, threadOnly.Intersect(agent).RequestAnswerRoute);
        Assert.Equal("request answers in the asking conversation", agent.OutsideOf(threadOnly));

        // An answer route needs requests, so startup fails without them.
        Assert.Contains("allows no requests", Assert.Throws<InvalidOperationException>(() => Ceiling(data => data
            .AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ceiling_of_an_agent_in_code_stays_within_the_applications_and_needs_rights_of_its_own_for_its_rights()
    {
        var state = new NhAssistantRegistrationState();
        state.AddAgent(AssistantTestData.Agent(PlannerId));
        var planner = new NhAssistantAgentRunBuilder(state.GetOrAddAgentRun(PlannerId));
        planner.ConsultableAsData(data => data
            .AllowRoutes(NhAssistantConsultationRoutes.AskerDecides, NhAssistantConsultationRoutes.SubjectAgentRights)
            .AllowRequests()
            .MaxLimits(maxToolCalls: 2, timeout: TimeSpan.FromMinutes(5)));

        // Without the application's ceiling an agent in code may not opt in.
        Assert.Contains("UseConsultationsAsData", Assert.Throws<InvalidOperationException>(state.ValidateAgentRuns).Message);

        state.ConsultationsAsData = Ceiling(data => data
            .AllowRoutes(NhAssistantConsultationRoutes.AskerDecides)
            .MaxLimits(maxToolCalls: 4, timeout: TimeSpan.FromMinutes(1)));
        Assert.Contains("beyond UseConsultationsAsData", Assert.Throws<InvalidOperationException>(state.ValidateAgentRuns).Message);

        state.ConsultationsAsData = Ceiling(data => data
            .AllowRoutes(NhAssistantConsultationRoutes.AskerDecides, NhAssistantConsultationRoutes.SubjectAgentRights, NhAssistantConsultationRoutes.SubjectAskerRights)
            .AllowRequests()
            .AllowMcp());
        // The route with the agent's own rights needs grants or a technical account.
        Assert.Contains("without grants", Assert.Throws<InvalidOperationException>(state.ValidateAgentRuns).Message);
        planner.Accountability(NhAiAgentAccountability.SubjectResponsible);
        Assert.Contains("responsible person", Assert.Throws<InvalidOperationException>(state.ValidateAgentRuns).Message);
        planner.ServiceAccount(KnownAccountPrincipalFactory.First);
        state.ValidateAgentRuns();

        // Both ceilings together: the routes of both, requests and MCP only when both allow them, and
        // the lower limits (AC3).
        var effective = state.ConsultationCeilingOf(PlannerId)!;
        Assert.Equal([NhAssistantConsultationRoutes.AskerDecides, NhAssistantConsultationRoutes.SubjectAgentRights], effective.Routes);
        Assert.Equal((true, false, 2, TimeSpan.FromMinutes(5), 32_000, 200), (effective.AllowRequests, effective.AllowMcp, effective.MaxToolCalls, effective.MaxTimeout, effective.MaxAnswerChars, effective.MaxHistoryMessages));
        // An agent created in the administration gets the application's; an agent in code without
        // ConfigureAgent none.
        Assert.Same(state.ConsultationsAsData, state.ConsultationCeilingOf("created-in-administration"));
        state.AddAgent(AssistantTestData.Agent("chat-only"));
        Assert.Null(state.ConsultationCeilingOf("chat-only"));

        Assert.Throws<ArgumentException>(() => Ceiling(data => data.AllowRoutes("anyone-decides")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ceiling(data => data.MaxLimits(maxToolCalls: 33)));
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient target,
        NhAiScriptedChatClient asker,
        NhAssistantTiming? timing = null,
        bool allowMcp = true,
        bool startOff = false,
        bool answerRequests = false)
    {
        // The agent created in the administration works with the application's chat profile, so the
        // host's model is the consulted agent's; the asking agent has a model of its own.
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            target,
            configure: services =>
            {
                if (timing is not null)
                {
                    services.AddSingleton(timing);
                }

                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
                services.AddKeyedSingleton<IChatClient>("asking-chat-model", asker);
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile("asking-chat", profile => profile
                    .UseKeyedClient("asking-chat-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .PermitDataClassifications(NhAiDataClassification.Internal)
                    .PermitExecutionRegions("local")
                    .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddSubjectType<TestOrderResolver>()
                .AddApprovalPolicy("initiator-only", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(1))
                    .Approvers(NhAiApprovers.Initiator))
                .UseConsultationsAsData(data =>
                {
                    data
                        .AllowRoutes(NhAssistantConsultationRoutes.AskerDecides, NhAssistantConsultationRoutes.SubjectAgentRights)
                        .AllowRequests(allowMcp)
                        .MaxLimits(maxToolCalls: 8, timeout: TimeSpan.FromSeconds(120), maxAnswerChars: 8_000, history: 40);
                    data.AllowMcp(allowMcp);
                    if (startOff)
                    {
                        data.StartOff();
                    }
                    if (answerRequests)
                    {
                        data.AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation);
                    }
                })
                .AddAgent(AssistantTestData.Agent(AskingAgentId) with
                {
                    ProfileName = "asking-chat",
                    ToolSelectors = ["assistant.consult.*"]
                })
                .AddAgent(AssistantTestData.Agent(ReviewerId))
                .ConfigureAgent(ReviewerId, agent => agent.AllowConsultation("project"))
                .AddAgent(AssistantTestData.Agent(PlannerId))
                .ConfigureAgent(PlannerId, agent => agent
                    .Grant("projects.*")
                    .AllowConsultation("project")
                    .ConsultableAsData(data => data.AllowRoutes(NhAssistantConsultationRoutes.AskerDecides))));
    }

    /// <summary>
    /// The project starter, created in the administration as an administrator does, with run
    /// settings that let it answer to the operations account.
    /// </summary>
    private static async Task CreateStarterAsync(
        AssistantTestHost host,
        bool withRunSettings,
        NhAiAutonomyLevel autonomy = NhAiAutonomyLevel.Execute)
    {
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(
                new NhAssistantAgentInput(
                    StarterId,
                    "Project starter",
                    "Starts draft projects.",
                    "Say whether a draft project can start.",
                    ["projects.*"],
                    [],
                    null,
                    autonomy),
                Admin,
                CancellationToken.None);
            Assert.True(created.Success);
        }

        if (withRunSettings)
        {
            await SaveRunSettingsAsync(host, NhAssistantAccountabilities.ServiceAccount, KnownAccountPrincipalFactory.First, null);
        }
    }

    /// <summary>
    /// Changes the starter's own tool selectors, as an administrator does in its editor.
    /// </summary>
    private static async Task UpdateStarterToolsAsync(AssistantTestHost host, IReadOnlyList<string> toolSelectors)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().UpdateAsync(
            new NhAssistantAgentInput(
                StarterId,
                "Project starter",
                "Starts draft projects.",
                "Say whether a draft project can start.",
                toolSelectors,
                [],
                null,
                NhAiAutonomyLevel.Execute),
            1,
            Admin,
            CancellationToken.None);
        Assert.True(updated.Success);
    }

    private static IReadOnlyList<string> ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }

    private static async Task SaveRunSettingsAsync(AssistantTestHost host, string accountability, string? serviceAccount, int? expectedVersion)
    {
        var saved = await ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(
            StarterId,
            new NhAssistantAgentRunSettingsRequest(accountability, serviceAccount, ExpectedVersion: expectedVersion),
            Admin));
        Assert.True(saved.Success);
    }

    private static NhAssistantDataConsultationRequest Read()
    {
        return new NhAssistantDataConsultationRequest("Knows whether a draft project can start.");
    }

    private static Task<TaskResult<NhAssistantDataConsultationDto>> SaveAsync(
        AssistantTestHost host,
        string agentId,
        NhAssistantDataConsultationRequest request,
        string subjectType = "project")
    {
        return ConfigureAsync(host, configuration => configuration.SaveConsultationAsync(agentId, subjectType, request, Admin));
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static AssistantAgentConsultationDefinition Row(
        string agentId,
        string subjectType,
        Action<AssistantAgentConsultationDefinition>? configure = null)
    {
        var row = new AssistantAgentConsultationDefinition
        {
            AgentId = agentId,
            SubjectType = subjectType,
            Description = "Knows the subject.",
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = Admin
        };
        configure?.Invoke(row);
        return row;
    }

    private static async Task StoreAsync(NhAssistantRunConfigurationStore store, AssistantAgentConsultationDefinition row)
    {
        Assert.True((await store.SaveConsultationAsync(row, null, CancellationToken.None)).Succeeded);
    }

    private static async Task<IReadOnlyList<string>> McpToolNamesAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Member)], "test")),
            RequestServices = scope.ServiceProvider
        };
        var tools = await scope.ServiceProvider.GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(scope.ServiceProvider, PersonContext());
        return tools
            .Select(tool => tool.ProtocolTool.Name)
            .Where(name => name.StartsWith("consult_", StringComparison.Ordinal) || name.StartsWith("request_", StringComparison.Ordinal))
            .Where(name => name.Contains(StarterId, StringComparison.Ordinal))
            .ToArray();
    }

    private static NhAssistantConsultationCeiling Ceiling(Action<NhAssistantConsultationCeilingBuilder> configure)
    {
        var services = new ServiceCollection();
        NhAssistantConsultationCeiling? ceiling = null;
        var state = new NhAssistantRegistrationState();
        new NhAssistantBuilder(services, state).UseConsultationsAsData(configure);
        ceiling = state.ConsultationsAsData;
        return ceiling!;
    }

    private static NhAssistantRegistrationState State(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRegistrationState>();
    }

    private static NhAiInvocationContext PersonContext()
    {
        return new NhAiInvocationContext(Member, "project-assistance", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Human
        };
    }

    private static NhAiInvocationContext AgentContext()
    {
        return new NhAiInvocationContext("assistant-agent:" + FollowerId, "assistant", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent
        };
    }

    private static NhAiAgentInvocationScope AgentScope()
    {
        return new NhAiAgentInvocationScope(
            FollowerId,
            "assistant-agent:" + FollowerId,
            1,
            new NhAiSubjectInfo(new NhAiSubjectRef("project", ProjectId), NhAiSubjectInfo.CreateScopeKey(null, null)),
            "assistant-agent:" + FollowerId,
            Guid.NewGuid().ToString(),
            "assistant");
    }

    private static JsonElement Question()
    {
        return JsonSerializer.SerializeToElement(new { subjectId = ProjectId, question = "Can the draft start?" });
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The one field a validation failure names, with the error it expects.
    /// </summary>
    private static string Field(TaskResult result, string error)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(result));
        var fields = NhAssistantResults.FieldErrorsOf(result);
        var field = Assert.Single(fields);
        Assert.Equal([error], field.Value);
        return field.Key;
    }
}

/// <summary>
/// Orders as a second subject type that everybody of <see cref="TestSubjects"/> may read.
/// </summary>
public sealed class TestOrderResolver(TestSubjects subjects) : INhAiSubjectAccessResolver
{
    public string SubjectType => "order";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, null))
        {
            DisplayName = "Order " + subject.Id
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(subjects.Roles.TryGetValue(actorId, out var role)
            ? new NhAiSubjectAccess(role)
            : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (actorId, role) in subjects.Roles)
        {
            await Task.Yield();
            yield return new NhAiSubjectMember(actorId, role) { DisplayName = actorId };
        }
    }
}
