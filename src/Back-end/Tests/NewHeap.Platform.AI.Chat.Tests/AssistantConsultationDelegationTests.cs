using System.Text.Json;
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
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The consult and request tools run with a person's own rights only. A headless run of an agent
/// that acts with the rights of the account it answers to, started by an event that names a person,
/// never consults or requests with that person's rights unless the agent delegates from its initiator.
/// </summary>
public sealed class AssistantConsultationDelegationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AskingAgentId = "project-follower";
    private const string TargetAgentId = "intake-reviewer";
    private const string ConsultTool = "consult_intake-reviewer_project_v1";
    private const string RequestTool = "request_intake-reviewer_project_v1";
    private const string ConsultToolId = "assistant.consult.intake-reviewer.project";
    private const string RequestToolId = "assistant.request.intake-reviewer.project";
    private const string EventName = "project.opened";
    private const string TriggerId = "project-opened";
    private const string Member = "member-1";
    private const string Admin = "admin-1";
    private const string ProjectId = "project-0001";

    public sealed record ProjectOpened(Guid EventId, string ProjectId, string OpenedBy);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_administration_agent_that_acts_with_its_accounts_rights_never_consults_or_requests_with_its_initiators_rights(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient().RespondWithText("The intake review is still open.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "What is still open?" } }, "call-1")
            .RespondWithFunctionCall(RequestTool, new { input = new { subjectId = ProjectId, message = "Review the intake again." } }, "call-2")
            .RespondWithText("I could not ask the reviewer.");
        await using var host = await CreateHostAsync(provider, asker, target);
        await CreateAskingAgentAsync(host, delegateFromInitiator: false);

        // The event names the person who opened the project; the agent does not delegate from them.
        var run = await PublishAndExecuteAsync(host);

        // The technical account passes the tools' policies, yet the turn is offered neither tool and
        // runs neither: not with the account's rights and not with the rights of the person who
        // opened the project.
        Assert.Equal(NhAssistantRunStatuses.Completed, run.Status);
        Assert.Equal(Member, run.InitiatorActorId);
        Assert.DoesNotContain(asker.Requests[0].Options?.Tools ?? [], tool => tool.Name is ConsultTool or RequestTool);
        Assert.Empty(target.Requests);
        Assert.Equal(2, asker.FunctionResults.Count);
        Assert.All(asker.FunctionResults, result => Assert.DoesNotContain("consultationId", result.Result?.ToString(), StringComparison.Ordinal));
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Empty(await context.Consultations.ToListAsync());
        Assert.DoesNotContain(await context.Runs.ToListAsync(), stored => stored.TriggerId == NhAssistantTriggerIds.AgentRequest);
        Assert.DoesNotContain(
            await context.ToolInvocations.Where(invocation => invocation.ConversationId == run.ConversationId).ToListAsync(),
            invocation => invocation.Status == NhAssistantToolCallStatuses.Succeeded);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentConsulted
            && evt.ResultCode == NhAssistantConsultationStatuses.Completed);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_administration_agent_that_delegates_from_its_initiator_consults_and_requests_with_the_initiators_rights(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient().RespondWithText("The intake review is still open.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "What is still open?" } }, "call-1")
            .RespondWithFunctionCall(RequestTool, new { input = new { subjectId = ProjectId, message = "Review the intake again." } }, "call-2")
            .RespondWithText("The reviewer says the intake review is still open.");
        await using var host = await CreateHostAsync(provider, asker, target);
        await CreateAskingAgentAsync(host, delegateFromInitiator: true);

        var run = await PublishAndExecuteAsync(host);

        Assert.Equal(NhAssistantRunStatuses.Completed, run.Status);
        Assert.Contains(asker.Requests[0].Options?.Tools ?? [], tool => tool.Name == ConsultTool);
        Assert.Contains("The intake review is still open.", asker.FunctionResults[0].Result?.ToString());
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var consultation = await context.Consultations.SingleAsync();
        Assert.Equal(NhAssistantConsultationStatuses.Completed, consultation.Status);
        Assert.Equal(Member, consultation.AskerActorId);
        Assert.Equal(AskingAgentId, consultation.AskingAgentId);
        var calls = await context.ToolInvocations
            .Where(invocation => invocation.ConversationId == run.ConversationId)
            .ToListAsync();
        Assert.All(
            calls.Where(invocation => invocation.ToolId is ConsultToolId or RequestToolId),
            invocation => Assert.Equal(NhAiGrantSource.Delegated, invocation.GrantSource));
        var requested = Assert.Single(await context.Runs.Where(stored => stored.TriggerId == NhAssistantTriggerIds.AgentRequest).ToListAsync());
        Assert.Equal(Member, requested.InitiatorActorId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_delegating_administration_agent_whose_trigger_is_still_in_shadow_proposes_its_request_and_starts_no_run(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient().RespondWithText("The intake review is still open.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(RequestTool, new { input = new { subjectId = ProjectId, message = "Review the intake again." } }, "call-1")
            .RespondWithText("I would ask the reviewer to review the intake again.");
        await using var host = await CreateHostAsync(provider, asker, target);
        await CreateAskingAgentAsync(host, delegateFromInitiator: true, live: false);

        var run = await PublishAndExecuteAsync(host);

        // The new trigger works in shadow: the request the initiator's rights would allow is an effect,
        // so it becomes a shadow proposal before the request tool runs, and the reviewer gets no run.
        Assert.Equal(NhAssistantRunStatuses.Completed, run.Status);
        Assert.True(run.Shadow);
        Assert.Contains(asker.Requests[0].Options?.Tools ?? [], tool => tool.Name == RequestTool);
        Assert.Empty(target.Requests);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.DoesNotContain(await context.Runs.ToListAsync(), stored => stored.TriggerId == NhAssistantTriggerIds.AgentRequest);
        var proposal = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(run.Id, CancellationToken.None));
        Assert.Equal((RequestToolId, NhAssistantApprovalStatuses.Shadow), (proposal.ToolId, proposal.Status));
        Assert.DoesNotContain(
            await context.ToolInvocations.Where(invocation => invocation.ConversationId == run.ConversationId).ToListAsync(),
            invocation => invocation.Status == NhAssistantToolCallStatuses.Succeeded);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_shadow_run_of_a_delegating_agent_consults_and_requests_no_live_agent(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient().RespondWithText("The intake review is still open.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "What is still open?" } }, "call-1")
            .RespondWithFunctionCall(RequestTool, new { input = new { subjectId = ProjectId, message = "Review the intake again." } }, "call-2")
            .RespondWithText("I could not ask the reviewer.");
        await using var host = await CreateHostAsync(provider, asker, target);
        // A new data trigger starts in shadow, so the event's run is evaluated in the shadow thread.
        await CreateAskingAgentAsync(host, delegateFromInitiator: true, live: false);

        var run = await PublishAndExecuteAsync(host);

        // The live reviewer is neither consulted nor asked to run. The consult tool reads, so it reaches
        // its handler, which refuses a shadow run; the request is an effect, so the shadow run's effect
        // decision records it as a shadow proposal before its handler runs. Nothing waits for the
        // reviewer's approvers and nobody is notified.
        Assert.True(run.Shadow);
        Assert.Equal(NhAssistantRunStatuses.Completed, run.Status);
        Assert.Empty(target.Requests);
        Assert.Equal(2, asker.FunctionResults.Count);
        Assert.Contains(NhAssistantErrorCodes.ConsultAgentUnavailable, asker.FunctionResults[0].Result?.ToString(), StringComparison.Ordinal);
        Assert.Contains(NhAssistantToolCallInterceptor.ProposalRecordedCode, asker.FunctionResults[1].Result?.ToString(), StringComparison.Ordinal);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Empty(await context.Consultations.ToListAsync());
        Assert.DoesNotContain(await context.Runs.ToListAsync(), stored => stored.TriggerId == NhAssistantTriggerIds.AgentRequest);
        Assert.Empty(await context.Threads.Where(thread => thread.AgentId == TargetAgentId).ToListAsync());
        var proposal = Assert.Single(await context.Approvals.ToListAsync());
        Assert.Equal((RequestToolId, NhAssistantApprovalStatuses.Shadow, run.ConversationId), (proposal.ToolId, proposal.Status, proposal.ConversationId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_a_delegating_initiator_or_a_person_outside_an_agent_turn_uses_the_consult_and_request_tools(AssistantTestProvider provider)
    {
        var target = new NhAiScriptedChatClient().RespondWithText("The intake review is still open.");
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), target);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var catalog = services.GetServices<INhAiToolCatalog>().OfType<NhAssistantConsultationToolCatalog>().Single();
        var consult = catalog.Descriptors.Single(descriptor => descriptor.Id == ConsultToolId);
        var request = catalog.Descriptors.Single(descriptor => descriptor.Id == RequestToolId);
        var authorizer = services.GetRequiredService<INhAiAgentToolAuthorizer>();
        var accountable = AgentScope() with
        {
            Accountability = NhAiAgentAccountability.ServiceAccount,
            AccountableOwnerId = KnownAccountPrincipalFactory.First,
            ActWithAccountableRights = true,
            InitiatorActorId = Member
        };

        // The gate: neither the accountable account's rights nor an agent grant allows them, and an
        // initiator whose rights the turn does not use, or who may no longer sign in, lends nothing.
        foreach (var descriptor in new[] { consult, request })
        {
            Assert.Equal(NhAiGrantSource.None, await authorizer.AuthorizeAsync(descriptor, accountable));
            Assert.Equal(NhAiGrantSource.None, await authorizer.AuthorizeAsync(descriptor, accountable with { Grants = [new NhAiAgentGrant("assistant.*")] }));
            Assert.Equal(NhAiGrantSource.None, await authorizer.AuthorizeAsync(descriptor, accountable with { DelegateFromInitiator = true, InitiatorActorId = "former-member" }));
            Assert.Equal(NhAiGrantSource.Delegated, await authorizer.AuthorizeAsync(descriptor, accountable with { DelegateFromInitiator = true }));
        }

        // The handler: in an agent turn whose call was not delegated from its initiator nobody asks.
        var handler = services.GetRequiredService<NhAssistantConsultationToolHandler>();
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[TargetAgentId].Consultations.Single();
        var agentCall = new NhAiInvocationContext("assistant-agent:" + AskingAgentId, "assistant", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            GrantSource = NhAiGrantSource.Accountable
        };
        TaskResult<NhAssistantConsultationAnswer> consulted;
        TaskResult<NhAssistantAgentRequestReceipt> requested;
        using (NhAiAgentInvocationScopes.Begin(accountable))
        {
            consulted = await handler.ConsultAsync(options, Input(new { subjectId = ProjectId, question = "What is still open?" }), agentCall, CancellationToken.None);
            requested = await handler.RequestAsync(options, Input(new { subjectId = ProjectId, message = "Review it." }), agentCall, CancellationToken.None);
        }

        Assert.Contains(consulted.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ConsultSubjectUnavailable);
        Assert.Contains(requested.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ConsultSubjectUnavailable);
        Assert.Empty(target.Requests);

        // A person outside an agent turn, as an MCP client calls it, consults with their own rights.
        var person = new NhAiInvocationContext(Member, "project-assistance", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Human
        };
        var answered = await handler.ConsultAsync(options, Input(new { subjectId = ProjectId, question = "What is still open?" }), person, CancellationToken.None);

        Assert.True(answered.Success);
        Assert.Equal("The intake review is still open.", answered.Data!.Answer);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Equal(Member, (await context.Consultations.SingleAsync()).AskerActorId);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient asker,
        NhAiScriptedChatClient target)
    {
        // The agent created in the administration works with the application's chat profile, so the
        // host's model is the asker's; the consulted agent has a model of its own.
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            asker,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
                services.AddKeyedSingleton<IChatClient>("consulted-chat-model", target);
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile("consulted-chat", profile => profile
                    .UseKeyedClient("consulted-chat-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .PermitDataClassifications(NhAiDataClassification.Internal)
                    .PermitExecutionRegions("local")
                    .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddAgent(AssistantTestData.Agent(TargetAgentId) with { ProfileName = "consulted-chat" })
                .ConfigureAgent(TargetAgentId, agent => agent
                    .AllowConsultation("project", consult => consult
                        .Description("Knows the intake review of a project.")
                        .AllowRequests()))
                .AddTriggerEvent<ProjectOpened>(EventName, evt => evt
                    .Description("A project was opened.")
                    .Subject(opened => new NhAiSubjectRef("project", opened.ProjectId))
                    .Initiator(opened => opened.OpenedBy)
                    .Message(_ => "The project was opened.")
                    .Dedupe(opened => opened.EventId.ToString("N"))));
    }

    /// <summary>
    /// An agent created in the administration that selects the consult and request tools, answers to
    /// a technical account and runs from the event of an opened project: live, or with
    /// <paramref name="live"/> <see langword="false"/> in the shadow its new data trigger starts in.
    /// </summary>
    private static async Task CreateAskingAgentAsync(AssistantTestHost host, bool delegateFromInitiator, bool live = true)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var created = await services.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(
            new NhAssistantAgentInput(
                AskingAgentId,
                "Project follower",
                "Follows up opened projects.",
                "Ask the intake reviewer what is still open.",
                ["assistant.consult.*", "assistant.request.*"],
                [],
                null,
                NhAiAutonomyLevel.Execute),
            Admin,
            CancellationToken.None);
        Assert.True(created.Success);

        var configuration = services.GetRequiredService<INhAssistantAgentConfiguration>();
        var settings = await configuration.SaveRunSettingsAsync(
            AskingAgentId,
            new NhAssistantAgentRunSettingsRequest(
                NhAssistantAccountabilities.ServiceAccount,
                KnownAccountPrincipalFactory.First,
                DelegateFromInitiator: delegateFromInitiator,
                Reason: "Follow up opened projects"),
            Admin);
        Assert.True(settings.Success);
        Assert.True((await configuration.SaveTriggerAsync(
            AskingAgentId,
            TriggerId,
            new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, Event: EventName, Conflict: NhAssistantTriggerConflictNames.Queue),
            Admin)).Success);
        if (live)
        {
            // A new data trigger starts in shadow.
            Assert.True((await configuration.SetTriggerShadowAsync(AskingAgentId, TriggerId, false, Admin)).Success);
        }
    }

    /// <summary>
    /// Publishes that <see cref="Member"/> opened the project and runs the agent's run as the
    /// background operation does.
    /// </summary>
    private static async Task<AssistantRun> PublishAndExecuteAsync(AssistantTestHost host)
    {
        Guid runId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
                .PublishAsync(new ProjectOpened(Guid.NewGuid(), ProjectId, Member));
            runId = Assert.Single(receipts, receipt => receipt.TriggerId == TriggerId).RunId!.Value;
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
            var context = Substitute.For<INhBackgroundOperationContext>();
            context.AttemptNumber.Returns(1);
            await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
        }

        return (await host.Services.GetRequiredService<NhAssistantRunStore>().FindRunAsync(runId, CancellationToken.None))!;
    }

    private static NhAiAgentInvocationScope AgentScope()
    {
        return new NhAiAgentInvocationScope(
            AskingAgentId,
            "assistant-agent:" + AskingAgentId,
            1,
            new NhAiSubjectInfo(new NhAiSubjectRef("project", ProjectId), NhAiSubjectInfo.CreateScopeKey(null, null)),
            "assistant-agent:" + AskingAgentId,
            Guid.NewGuid().ToString(),
            "assistant");
    }

    private static JsonElement Input(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }
}
