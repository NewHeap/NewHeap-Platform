using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// One assistant consults another agent about a subject: with the asker's rights only, reading what
/// the asker may read, proposing instead of changing, recorded without content and bounded in depth,
/// number and budget. A request starts a run of the agent with the asker as initiator.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantConsultationTests(AssistantDatabaseFixture database)
{
    private const string TargetAgentId = "project-assistant";
    private const string AskingAgentId = "consulting-assistant";
    private const string ConsultTool = "consult_project-assistant_project_v1";
    private const string RequestTool = "request_project-assistant_project_v1";
    private const string Member = "member-1";
    private const string ProjectId = "project-0001";

    public sealed record TestMemory(IReadOnlyList<string>? Checks, string? Verdict);

    public sealed record SubjectTouched(Guid EventId, string SubjectId);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_participant_gets_an_answer_from_the_agents_memory_and_its_thread_gets_no_message(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient()
            .RespondWithText("Checks done: owner. The verdict is still pending.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "What is still missing?" } }, "call-1")
            .RespondWithText("The intake reviewer says the verdict is still pending.");
        await using var host = await CreateHostAsync(provider, target, asker);
        var thread = await StartThreadAsync(host, ProjectId);
        Assert.True(await Runs(host).TryUpdateMemoryAsync(
            thread.ConversationId,
            thread.MemoryConcurrencyStamp,
            "{\"checks\":[\"owner\"],\"verdict\":\"pending\"}",
            1,
            CancellationToken.None));
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        var turn = await host.SendAsync(conversation.Id, "What is missing for the project?", Member);

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);
        var result = asker.FunctionResults.Single().Result?.ToString();
        Assert.Contains("Checks done: owner. The verdict is still pending.", result);
        Assert.Contains("consultationId", result, StringComparison.OrdinalIgnoreCase);

        // The consulted agent answered from its memory, as data, with the question last.
        var request = target.Requests.Single();
        var system = string.Join("\n", request.Messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
        Assert.Contains("<working-memory schema-version=\"1\">{\"checks\":[\"owner\"],\"verdict\":\"pending\"}</working-memory>", system);
        Assert.Contains("You are being consulted", system);
        Assert.Contains("A consultation does not change it.", system);
        Assert.Equal("What is still missing?", request.Messages[^1].Text);
        Assert.Contains(ToolNames(request), name => name == "memory_get");
        Assert.DoesNotContain(ToolNames(request), name => name == "memory_update");

        var consultation = await SingleConsultationAsync(host);
        Assert.Equal(NhAssistantConsultationStatuses.Completed, consultation.Status);
        Assert.Equal(thread.ConversationId, consultation.ConversationId);
        Assert.Equal(Member, consultation.AskerActorId);
        Assert.Equal(AskingAgentId, consultation.AskingAgentId);
        Assert.Equal(conversation.Id, consultation.AskingConversationId);
        Assert.Equal(1, consultation.Depth);
        Assert.NotNull(consultation.EndedAt);

        Assert.Empty(await host.Services.GetRequiredService<INhAssistantStore>()
            .GetMessagesAsync(thread.ConversationId, 100, CancellationToken.None));
        var audited = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentConsulted);
        Assert.Equal(consultation.Id, audited.ConsultationId);
        Assert.Equal(NhAssistantConsultationStatuses.Completed, audited.ResultCode);
        Assert.Equal(AskingAgentId, audited.AskingAgentId);
        Assert.Equal(Member, audited.ActorId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_consultation_reads_the_subject_briefing_before_the_agents_memory(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient().RespondWithText("Two tasks are open.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "What is open?" } }, "call-1")
            .RespondWithText("Two tasks are open.");
        await using var host = await CreateHostAsync(provider, target, asker, configure: services =>
        {
            services.AddSingleton(new TestBriefings
            {
                Briefing = _ => new NhAssistantSubjectBriefing([new NhAssistantBriefingSection("Open tasks", "2 open tasks.")])
            });
            services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAssistantSubjectBriefingProvider, TestBriefingProvider>());
        });
        await StartThreadAsync(host, ProjectId);
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        await host.SendAsync(conversation.Id, "What is open in the project?", Member);

        var context = Assert.Single(host.Services.GetRequiredService<TestBriefings>().Contexts);
        Assert.Equal(NhAssistantBriefingPurposes.Consultation, context.Purpose);
        Assert.Empty(context.Inputs);
        var messages = target.Requests.Single().Messages;
        Assert.Contains("<section title=\"Open tasks\">", messages[0].Text);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Single(messages, message => message.Text.Contains("<subject-briefing>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Someone_without_a_role_on_the_subject_is_refused_as_for_an_unknown_subject(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient();
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "What is missing?" } }, "call-1")
            .RespondWithText("I may not ask about that project.");
        await using var host = await CreateHostAsync(provider, target, asker);
        var conversation = await host.CreateConversationAsync(agentId: AskingAgentId);

        await host.SendAsync(conversation.Id, "What is missing for the project?");

        Assert.Contains(NhAssistantErrorCodes.ConsultSubjectUnavailable, asker.FunctionResults.Single().Result?.ToString());
        Assert.Empty(target.Requests);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Empty(await context.Consultations.ToListAsync());
        Assert.Empty(await context.Threads.ToListAsync());
        var audited = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentConsulted);
        Assert.Equal(NhAssistantErrorCodes.ConsultSubjectUnavailable, audited.ResultCode);
        Assert.Null(audited.ConsultationId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_of_a_consultation_waits_for_approvers_and_executes_with_the_askers_rights_only(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "on-hold" } }, "c-2")
            .RespondWithText("I proposed two status changes.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Activate it if it is ready." } }, "call-1")
            .RespondWithText("The reviewer proposed it.");
        await using var host = await CreateHostAsync(provider, target, asker);
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        await host.SendAsync(conversation.Id, "Ask the reviewer to activate the project.", Member);

        Assert.Empty(host.Tools.StatusChanges);
        var consultation = await SingleConsultationAsync(host);
        Assert.Equal(2, consultation.ProposalCount);

        // The consultation created the thread and filled its member snapshot for names and notifications.
        var members = await Runs(host).GetMembersAsync(consultation.ScopeKey, new NhAiSubjectRef("project", ProjectId), CancellationToken.None);
        Assert.Contains(members, member => member.ActorId == Member);
        var approvals = await Runs(host).GetConsultationApprovalsAsync(consultation.Id, CancellationToken.None);
        Assert.Equal(2, approvals.Count);
        Assert.All(approvals, approval =>
        {
            Assert.Equal(consultation.ConversationId, approval.ConversationId);
            Assert.Equal(NhAssistantApprovalStatuses.Pending, approval.Status);
            Assert.Equal(NhAssistantApprovalModes.Deferred, approval.Mode);
            Assert.Null(approval.RunId);
            Assert.Equal(Member, approval.InitiatorActorId);
            Assert.Equal(NhAiGrantSource.Delegated, approval.GrantSource);
        });
        var store = host.Services.GetRequiredService<INhAssistantStore>();
        Assert.NotNull(await store.FindToolInvocationAsync(approvals[0].ToolInvocationId, CancellationToken.None));

        // Approved, the first executes with the asker's rights.
        await ApproveAndExecuteAsync(host, approvals[0]);
        var executed = Assert.Single(host.Tools.StatusChanges);
        Assert.Equal("active", executed.Status);
        Assert.Equal(NhAiGrantSource.Delegated, host.Tools.Contexts.Last().GrantSource);
        Assert.Equal(NhAssistantApprovalStatuses.Executed, (await store.FindApprovalAsync(approvals[0].Id, CancellationToken.None))!.Status);

        // Once the asker may no longer sign in, the second fails instead of using the agent's own grant.
        host.Services.GetRequiredService<ConsultationPrincipals>().Revoked.TryAdd(Member, true);
        await ApproveAndExecuteAsync(host, approvals[1]);
        Assert.Single(host.Tools.StatusChanges);
        Assert.Equal(NhAssistantApprovalStatuses.ExecutionFailed, (await store.FindApprovalAsync(approvals[1].Id, CancellationToken.None))!.Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_reader_gets_a_read_only_consultation_without_what_the_agent_obtained_with_its_own_grant(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithText("Search notes saved.")
            .RespondWithText("I know of no open points.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Any open points?" } }, "call-1")
            .RespondWithText("None.");
        await using var host = await CreateHostAsync(
            provider,
            target,
            asker,
            configure: services => services.AddAuthorization(options =>
                // Only people with the read permission may search; the agent searches with its grant.
                options.AddPolicy(TestProjectToolCatalog.ReadPolicy, policy => policy.RequireClaim("permission", "projects.read"))));
        host.Services.GetRequiredService<TestSubjects>().Roles["reader-1"] = NhAiSubjectRole.Reader;
        host.Tools.SaveSearchArtifact = true;
        var thread = await StartThreadAsync(host, ProjectId);
        await RunAgentTurnAsync(host, thread, "Search and note.");
        Assert.Single(await host.Services.GetRequiredService<NhAssistantArtifactStore>().ListAsync(thread.ConversationId, 10, CancellationToken.None));
        var conversation = await host.CreateConversationAsync("reader-1", AskingAgentId);

        await host.SendAsync(conversation.Id, "Does the project have open points?", "reader-1");

        var request = target.Requests[^1];
        var tools = ToolNames(request);
        Assert.DoesNotContain(tools, name => name == "projects_search_v1");
        Assert.DoesNotContain(tools, name => name == "projects_change_status_v1");
        Assert.DoesNotContain(request.Messages, message => message.Text.Contains("search-notes", StringComparison.Ordinal));
        Assert.Contains("I know of no open points.", asker.FunctionResults.Single().Result?.ToString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_consultation_never_nests_deeper_than_allowed_or_asks_an_agent_already_asking(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(
            provider,
            new NhAiScriptedChatClient(),
            new NhAiScriptedChatClient(),
            limits: limits => limits.MaxConsultationDepth = 3);
        var options = host.Services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[TargetAgentId].Consultations.Single();
        var viewer = new AllowAllViewer();
        var outer = new NhAssistantConsultationScope(Guid.NewGuid(), options, [AskingAgentId, TargetAgentId], 1, true, AskingAgentId, viewer);

        Assert.False(outer.MayOffer(options.ConsultToolId, 3));
        Assert.False(outer.MayOffer(options.RequestToolId, 3));
        Assert.True(outer.MayOffer("assistant.consult.work-preparation.project", 3));
        Assert.False(outer.MayOffer("assistant.consult.work-preparation.project", 1));

        await using var scope = host.Services.CreateAsyncScope();
        using var entered = NhAssistantExecutionScope.EnterTurn(new NhAssistantTurnScope
        {
            ConversationId = Guid.NewGuid(),
            TurnId = Guid.NewGuid(),
            Agent = AssistantTestData.Agent(TargetAgentId),
            OwnerActorId = Member,
            AccountableOwnerId = Member,
            ModelProfileName = "project-chat",
            PromptVersion = "1",
            PromptHash = "hash",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(2),
            Limits = new NhAssistantLimits(),
            Consultation = outer
        });
        var refused = await scope.ServiceProvider.GetRequiredService<NhAssistantConsultationService>().ConsultAsync(
            new NhAssistantConsultationRequest(options, ProjectId, "Ask yourself.", Member, false, "en", viewer),
            CancellationToken.None);

        Assert.Contains(refused.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ConsultDepthExceeded);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_daily_limit_per_person_refuses_the_next_consultation(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient()
            .RespondWithText("All checks are done.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Done?" } }, "call-1")
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Really done?" } }, "call-2")
            .RespondWithText("The second question was not asked.");
        await using var host = await CreateHostAsync(provider, target, asker, limits: limits => limits.MaxConsultationsPerActorPerDay = 1);
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        await host.SendAsync(conversation.Id, "Is the project done?", Member);

        Assert.Single(target.Requests);
        Assert.Contains(NhAssistantErrorCodes.ConsultLimitReached, asker.FunctionResults[1].Result?.ToString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Consultations_are_stored_listed_and_counted_in_the_agents_daily_usage(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new NhAiScriptedChatClient());
        var thread = await StartThreadAsync(host, ProjectId);
        var runs = Runs(host);
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var before = await runs.GetAgentUsageSinceAsync(TargetAgentId, thread.ScopeKey, since, CancellationToken.None);
        var first = Consultation(thread, Member);
        var second = Consultation(thread, "approver-1");
        await runs.AddConsultationAsync(first, CancellationToken.None);
        await runs.AddConsultationAsync(second, CancellationToken.None);

        Assert.True(await runs.EndConsultationAsync(first.Id, NhAssistantConsultationStatuses.Completed, null, 3, 100, 20, 1, CancellationToken.None));
        Assert.False(await runs.EndConsultationAsync(first.Id, NhAssistantConsultationStatuses.Failed, "late", 9, 9, 9, 9, CancellationToken.None));

        var usage = await runs.GetAgentUsageSinceAsync(TargetAgentId, thread.ScopeKey, since, CancellationToken.None);
        Assert.Equal(before.ToolCalls + 3, usage.ToolCalls);
        Assert.Equal(before.Tokens + 120, usage.Tokens);
        Assert.Equal(1, await runs.CountConsultationsSinceAsync(Member, since, CancellationToken.None));
        var (items, total) = await runs.ListConsultationsAsync(thread.ConversationId, 1, 1, CancellationToken.None);
        Assert.Equal(2, total);
        Assert.Single(items);

        // Forgetting the subject deletes its consultations with the thread.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INhAiSubjectRetention>()
                .ForgetSubjectAsync(new NhAiSubjectRef("project", ProjectId));
        }

        Assert.Equal(0, await runs.CountConsultationsSinceAsync(Member, since, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_request_starts_a_run_with_the_asker_as_initiator_and_a_reader_may_not_request(AssistantTestProvider provider)
    {
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(RequestTool, new { input = new { subjectId = ProjectId, message = "Review the intake again." } }, "call-1")
            .RespondWithText("The reviewer will answer in the project's thread.")
            .RespondWithFunctionCall(RequestTool, new { input = new { subjectId = ProjectId, message = "Review it." } }, "call-2")
            .RespondWithText("I may not ask that.");
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), asker);
        host.Services.GetRequiredService<TestSubjects>().Roles["reader-1"] = NhAiSubjectRole.Reader;

        await host.SendAsync((await host.CreateConversationAsync(Member, AskingAgentId)).Id, "Ask for a new review.", Member);
        await host.SendAsync((await host.CreateConversationAsync("reader-1", AskingAgentId)).Id, "Ask for a new review.", "reader-1");

        Assert.Contains(NhAssistantTriggerOutcomes.Started, asker.FunctionResults[0].Result?.ToString());
        Assert.Contains(NhAssistantErrorCodes.ConsultSubjectUnavailable, asker.FunctionResults[1].Result?.ToString());
        var (runId, _) = Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Enqueued);
        var run = (await Runs(host).FindRunAsync(runId, CancellationToken.None))!;
        Assert.Equal(NhAssistantTriggerIds.AgentRequest, run.TriggerId);
        Assert.Equal(Member, run.InitiatorActorId);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var input = await context.QueuedInputs.SingleAsync(item => item.ConversationId == run.ConversationId);
        Assert.Equal(Member, input.AuthorActorId);
        Assert.Equal(NhAssistantQueuedInputKinds.Message, input.Kind);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentRequested && evt.RunId == runId);

        var recipients = await new NhAssistantDefaultAgentNotificationPolicy().SelectRecipientsAsync(
            new NhAssistantAgentNotificationContext(
                NhAssistantAgentNotificationKind.RequestAnswered,
                TargetAgentId,
                run.ConversationId,
                new NhAiSubjectRef("project", ProjectId),
                null,
                run.Id,
                null,
                Member,
                [new NhAiSubjectMember("approver-1", NhAiSubjectRole.Approver)]));
        Assert.Equal(new[] { Member }, recipients);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Every_consultation_registration_generates_governed_tools_that_no_agent_grant_covers(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(
            provider,
            new NhAiScriptedChatClient(),
            new NhAiScriptedChatClient(),
            consult: consult => consult.Exposure(NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp));
        var catalog = host.Services.GetServices<INhAiToolCatalog>().OfType<NhAssistantConsultationToolCatalog>().Single();

        var consult = Assert.Single(catalog.Descriptors, descriptor => descriptor.Id == "assistant.consult.project-assistant.project");
        var request = Assert.Single(catalog.Descriptors, descriptor => descriptor.Id == "assistant.request.project-assistant.project");
        Assert.Equal(ConsultTool, consult.ExportName);
        Assert.Equal(RequestTool, request.ExportName);
        Assert.Equal(NhAiToolEffect.ReadOnly, consult.Effect);
        Assert.Equal(NhAiToolEffect.IdempotentMutation, request.Effect);
        Assert.All(catalog.Descriptors, descriptor =>
        {
            Assert.False(descriptor.AllowAgentGrant);
            Assert.Equal(NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp, descriptor.Exposure);
            // Without its own policy a consultation requires the assistant's access policy.
            Assert.Equal(new[] { AssistantTestHost.AccessPolicy }, descriptor.AuthorizationPolicies);
        });
        NhAiToolCatalogAttestation.Validate(catalog, host.Services);

        // The platform's startup validation accepts the generated descriptors.
        var platformValidator = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Single(service => service.GetType().Name == "NhAiStartupValidator");
        await platformValidator.StartAsync(CancellationToken.None);

        var options = host.Services.GetRequiredService<NhAssistantRegistrationState>().GetOrAddAgentRun("other-agent");
        new NhAssistantAgentRunBuilder(options).AllowConsultation("project");
        Assert.Throws<InvalidOperationException>(() => new NhAssistantAgentRunBuilder(options).AllowConsultation("project"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NhAssistantAgentRunBuilder(options)
            .AllowConsultation("order", builder => builder.Exposure(NhAiToolExposure.None)));
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient target,
        NhAiScriptedChatClient asker,
        Action<NhAssistantLimits>? limits = null,
        Action<NhAssistantConsultationBuilder>? consult = null,
        Action<IServiceCollection>? configure = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            target,
            limits,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.AddSingleton<ConsultationPrincipals>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(provider => provider.GetRequiredService<ConsultationPrincipals>()));
                services.AddKeyedSingleton<IChatClient>("asking-chat-model", asker);
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile("asking-chat", profile => profile
                    .UseKeyedClient("asking-chat-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .PermitDataClassifications(NhAiDataClassification.Internal)
                    .PermitExecutionRegions("local")
                    .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16)));
                configure?.Invoke(services);
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(AskingAgentId) with
                {
                    ProfileName = "asking-chat",
                    ToolSelectors = ["assistant.consult.*", "assistant.request.*"]
                })
                .AddSubjectType<TestSubjectResolver>()
                .ConfigureAgent(TargetAgentId, agent => agent
                    .Grant("projects.*")
                    .Memory<TestMemory>(schemaVersion: 1)
                    .AllowConsultation("project", builder =>
                    {
                        builder
                            .Description("Knows the intake review of a project.")
                            .AllowRequests();
                        consult?.Invoke(builder);
                    }))
                .AddTrigger<SubjectTouched>("subject-touched", trigger => trigger
                    .StartAgent(TargetAgentId)
                    .Subject(evt => new NhAiSubjectRef("project", evt.SubjectId))
                    .Message(evt => "The project changed.")
                    .Dedupe(evt => evt.EventId.ToString("N"))
                    .OnBusy(NhAssistantTriggerConflict.Queue)));
    }

    /// <summary>
    /// Starts the agent on the subject once, so its thread exists.
    /// </summary>
    private static async Task<AssistantThread> StartThreadAsync(AssistantTestHost host, string subjectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipt = Assert.Single(await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new SubjectTouched(Guid.NewGuid(), subjectId)));
        return (await Runs(host).FindThreadByConversationAsync(receipt.ConversationId!.Value, CancellationToken.None))!;
    }

    private static async Task RunAgentTurnAsync(AssistantTestHost host, AssistantThread thread, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var subject = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), CancellationToken.None))!;
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[TargetAgentId];
        var run = (await Runs(host).GetOpenRunsAsync(thread.ConversationId, CancellationToken.None)).First();
        var result = await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                new NhAssistantAgentTurnContext(thread, subject, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false),
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, message)]),
            CancellationToken.None);
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
    }

    /// <summary>
    /// Approves a proposal and runs its execution in a background scope, without a request.
    /// </summary>
    private static async Task ApproveAndExecuteAsync(AssistantTestHost host, AssistantApproval approval)
    {
        Assert.True(await Runs(host).TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Approved,
            new NhAssistantApprovalTransition
            {
                DecidedByActorId = "approver-1",
                DecidedAt = DateTimeOffset.UtcNow,
                ApprovalExpiresAt = approval.ExpiresAt
            },
            CancellationToken.None));
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var result = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalExecutor>().ExecuteAsync(
            approval.Id,
            Substitute.For<INhBackgroundOperationContext>(),
            fenced: false,
            CancellationToken.None);
        Assert.True(result.Success);
    }

    private static AssistantConsultation Consultation(AssistantThread thread, string asker)
    {
        return new AssistantConsultation
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            ScopeKey = thread.ScopeKey,
            AgentId = TargetAgentId,
            SubjectType = thread.SubjectType,
            SubjectId = thread.SubjectId,
            AskerActorId = asker,
            Depth = 1,
            Status = NhAssistantConsultationStatuses.Running,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task<AssistantConsultation> SingleConsultationAsync(AssistantTestHost host)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Consultations.SingleAsync();
    }

    private static IReadOnlyList<string> ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private sealed class AllowAllViewer : INhAssistantConsultationViewer
    {
        public ValueTask<bool> MayViewAsync(NhAiGrantSource grantSource, string toolId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }
    }
}

/// <summary>
/// Rebuilds a principal for anyone who may still sign in; revoked people get none.
/// </summary>
public sealed class ConsultationPrincipals : INhAiPrincipalFactory
{
    public ConcurrentDictionary<string, bool> Revoked { get; } = new(StringComparer.Ordinal);

    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<ClaimsPrincipal?>(Revoked.ContainsKey(actorId)
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test")));
    }
}
