using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// TC2 and TC5: a composition chains registered governed tools as one tool. Values flow from earlier
/// results into later steps, the declared result returns to the model, and its effect is the strongest
/// of its steps. Every step gets the decision the same call gets on its own: an approval-free change
/// executes, a deferred approval lets the chain continue, an inline approval stops it, a shadow run
/// proposes every change and executes none, a step the caller may not use stops it with that step's
/// code, and the proposal guard sees that a composition proposes. A composition is offered only where
/// the switch has compositions on and with the tools its steps call. Both relational providers.
/// </summary>
public sealed class AssistantCompositionTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string NarrowAgent = "narrow-assistant";
    private const string ServiceAccount = "approver-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_read_only_composition_chains_its_steps_with_values_flowing_and_returns_its_declared_result(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestCompositions.OverviewFunction, new { input = new { projectId } }, "call-overview")
            .RespondWithText("Two risks.");
        await using var host = await CreateChatHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync();

        await host.SendAsync(conversation.Id, "Give me the overview of the project.");

        // The composition is read-only, so it runs at once; each value flows into the next step.
        var recorder = host.Services.GetRequiredService<TestRiskToolRecorder>();
        Assert.Equal([TestRiskToolCatalog.GetProject.Id, TestRiskToolCatalog.ListRisks.Id, TestRiskToolCatalog.GetPeople.Id], recorder.RanTools);
        Assert.Equal(projectId.ToString(), recorder.InputOf(TestRiskToolCatalog.ListRisks.Id).GetProperty("projectId").GetString());
        Assert.Equal(["person-2", "person-3"], recorder.InputOf(TestRiskToolCatalog.GetPeople.Id).GetProperty("ids").EnumerateArray().Select(id => id.GetString()).ToArray());
        var descriptor = Composition(host, TestCompositions.Overview);
        Assert.Equal(NhAiToolEffect.ReadOnly, descriptor.Effect);
        Assert.Equal(NhAiCompositionFeature.Compositions, descriptor.CompositionFeature);

        // The model receives the declared result only.
        var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        Assert.True(result.GetProperty("success").GetBoolean());
        var data = result.GetProperty("data");
        Assert.Equal(projectId.ToString(), data.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal(2, data.GetProperty("risks").GetArrayLength());
        Assert.Equal("Name of person-2", data.GetProperty("owners")[0].GetProperty("name").GetString());

        // Each step is audited under the composition's call and counted against the budget.
        var rows = await AssistantToolStepTests.RowsAsync(host, conversation.Id);
        var parent = Assert.Single(rows, row => row.ToolId == TestCompositions.Overview);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, parent.Status);
        var stepAudits = host.Audit.Records.Where(record => record.Origin?.ParentInvocationId == parent.Id).ToArray();
        Assert.Equal([0, 1, 2], stepAudits.Select(record => record.Origin!.StepIndex!.Value).Order().ToArray());
        Assert.All(stepAudits, record => Assert.Equal(TestCompositions.Overview, record.Origin!.ToolId));
        Assert.Equal(NhAiOutcomeKind.Succeeded, Assert.Single(host.Audit.Records, record => record.ToolId == TestCompositions.Overview).Outcome);
        Assert.Equal(3, await ToolCallsTodayAsync(host, AssistantTestHost.UserId));
        Assert.Empty(await ApprovalsAsync(host, conversation.Id));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_inline_approval_stops_the_composition_with_what_succeeded_and_a_denied_step_stops_it_with_its_code(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestCompositions.FlagOpenRisksFunction, new { input = new { projectId } }, "call-flag")
            .RespondWithText("Flagged.")
            .RespondWithFunctionCall(TestCompositions.SecretOverviewFunction, new { input = new { projectId } }, "call-secret")
            .RespondWithText("No access.");
        await using var host = await CreateChatHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync();

        var waiting = await host.SendAsync(conversation.Id, "Flag the first risk.");

        // A person's chat decides inline: the chain stops at the change and the tag after it never ran.
        var recorder = host.Services.GetRequiredService<TestRiskToolRecorder>();
        Assert.Equal([TestRiskToolCatalog.ListRisks.Id], recorder.RanTools);
        Assert.Equal(NhAiToolEffect.IdempotentMutation, Composition(host, TestCompositions.FlagOpenRisks).Effect);
        var approval = waiting.Single<NhAssistantApprovalRequiredEvent>().Approval;
        Assert.Equal(TestRiskToolCatalog.FlagRisk.Id, approval.ToolId);

        await host.DecideAsync(conversation.Id, approval, approve: true);

        Assert.Equal([TestRiskToolCatalog.ListRisks.Id, TestRiskToolCatalog.FlagRisk.Id], recorder.RanTools);
        var stopped = ResultOf(model, "call-flag");
        Assert.False(stopped.GetProperty("success").GetBoolean());
        Assert.Equal(NhAiToolFailureCodes.ApprovalRequired, stopped.GetProperty("code").GetString());
        Assert.Equal("flag", stopped.GetProperty("data").GetProperty("stoppedAt").GetString());
        var steps = stopped.GetProperty("data").GetProperty("steps");
        Assert.Equal(["succeeded", "awaiting-approval"], steps.EnumerateArray().Select(step => step.GetProperty("status").GetString()).ToArray());
        Assert.Equal(2, steps[0].GetProperty("data").GetArrayLength());
        Assert.Equal(approval.ProposalId.ToString("D"), steps[1].GetProperty("proposalId").GetString());

        // The person may not read the secret: the step is denied and stops the chain with its code.
        await host.SendAsync(conversation.Id, "Show the secret overview.");

        var denied = JsonSerializer.SerializeToElement(model.FunctionResults.Single(result => result.CallId == "call-secret").Result);
        Assert.Equal(NhAiToolFailureCodes.AuthorizationDenied, denied.GetProperty("code").GetString());
        Assert.Equal("secret", denied.GetProperty("data").GetProperty("stoppedAt").GetString());
        Assert.Equal("succeeded", denied.GetProperty("data").GetProperty("steps")[0].GetProperty("status").GetString());
        Assert.DoesNotContain(TestRiskToolCatalog.ReadSecret.Id, recorder.RanTools);
        var rows = await AssistantToolStepTests.RowsAsync(host, conversation.Id);
        var secretCall = Assert.Single(rows, row => row.ToolId == TestCompositions.SecretOverview);
        Assert.Equal(NhAssistantToolCallStatuses.Failed, secretCall.Status);
        Assert.Equal(NhAiToolFailureCodes.AuthorizationDenied, secretCall.ResultCode);
        var audit = Assert.Single(host.Audit.Records, record => record.ToolId == TestCompositions.SecretOverview);
        Assert.Equal(NhAiOutcomeKind.TerminalFailure, audit.Outcome);
        Assert.Equal(NhAiToolFailureCodes.AuthorizationDenied, audit.ResultCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task In_a_run_a_deferred_step_lets_the_chain_continue_an_approval_free_change_executes_and_the_guard_sees_the_composition(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestCompositions.FlagOpenRisksFunction, new { input = new { projectId } }, "call-flag")
            .RespondWithText("I proposed to flag the risk and tagged it.");
        await using var host = await CreateRunHostAsync(provider, model);
        var run = await StartRunAsync(host, projectId, shadow: false);

        var turn = await RunTurnAsync(host, run);

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Outcome);
        // The flag waits for the approvers; the tag needs no approval and ran with the account's rights.
        var recorder = host.Services.GetRequiredService<TestRiskToolRecorder>();
        Assert.Equal([TestRiskToolCatalog.ListRisks.Id, TestRiskToolCatalog.TagRisk.Id], recorder.RanTools);
        Assert.Equal("risk-1", recorder.InputOf(TestRiskToolCatalog.TagRisk.Id).GetProperty("riskId").GetString());
        var approval = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal(TestRiskToolCatalog.FlagRisk.Id, approval.ToolId);
        Assert.Equal(NhAssistantApprovalModes.Deferred, approval.Mode);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, approval.Status);
        var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(nameof(NhAssistantToolStepStatus.Proposed), result.GetProperty("data").GetProperty("flagged").GetString());

        // The guard of the flag saw that a step of the composition proposed it, under the composition's call.
        var rows = await AssistantToolStepTests.RowsAsync(host, run.AgentTurn.Thread.ConversationId);
        var parent = Assert.Single(rows, row => row.ToolId == TestCompositions.FlagOpenRisks);
        Assert.NotEmpty(host.Services.GetRequiredService<TestGuardOrigins>().Seen);
        Assert.All(host.Services.GetRequiredService<TestGuardOrigins>().Seen, origin =>
        {
            Assert.Equal(NhAiToolCallOriginKind.Composition, origin?.Kind);
            Assert.Equal(TestCompositions.FlagOpenRisks, origin?.ToolId);
            Assert.Equal(parent.Id, origin?.ParentInvocationId);
            Assert.Equal(1, origin?.StepIndex);
        });
        var waitingStep = Assert.Single(rows, row => row.Id == approval.ToolInvocationId);
        Assert.Equal(NhAssistantToolStepCallIds.Of(NhAiToolCallOriginKind.Composition, parent.Id, 1), waitingStep.CallId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_shadow_run_proposes_every_change_of_a_composition_and_executes_none(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestCompositions.FlagOpenRisksFunction, new { input = new { projectId } }, "call-flag")
            .RespondWithText("Proposed.");
        await using var host = await CreateRunHostAsync(provider, model);
        var run = await StartRunAsync(host, projectId, shadow: true);

        var turn = await RunTurnAsync(host, run);

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Outcome);
        Assert.Equal([TestRiskToolCatalog.ListRisks.Id], host.Services.GetRequiredService<TestRiskToolRecorder>().RanTools);
        var proposals = await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(run.Run.Id, CancellationToken.None);
        Assert.Equal(
            [TestRiskToolCatalog.FlagRisk.Id, TestRiskToolCatalog.TagRisk.Id],
            proposals.OrderBy(proposal => proposal.CreatedAt).Select(proposal => proposal.ToolId).ToArray());
        Assert.All(proposals, proposal => Assert.Equal(NhAssistantApprovalStatuses.Shadow, proposal.Status));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task With_the_switch_off_a_composition_is_not_offered_and_a_call_is_refused_and_without_its_steps_it_is_not_offered(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestCompositions.OverviewFunction, new { input = new { projectId } }, "call-off")
            .RespondWithText("Not available.")
            .RespondWithText("Narrow.");
        await using var host = await CreateChatHostAsync(provider, model);
        host.Services.GetRequiredService<TestCompositionSwitchState>().Answer = _ => false;
        var conversation = await host.CreateConversationAsync();

        await host.SendAsync(conversation.Id, "Give me the overview.");

        Assert.DoesNotContain(TestCompositions.OverviewFunction, Offered(model, 0));
        Assert.Contains(TestRiskToolCatalog.GetProject.ExportName, Offered(model, 0));
        // A call that still names it reaches nothing: the turn does not offer it, so no step ran.
        var refused = JsonSerializer.Serialize(Assert.Single(model.FunctionResults).Result);
        Assert.DoesNotContain("\"success\":true", refused);
        Assert.DoesNotContain(await AssistantToolStepTests.RowsAsync(host, conversation.Id), row => row.ToolId == TestCompositions.Overview);
        Assert.Empty(host.Services.GetRequiredService<TestRiskToolRecorder>().Ran);

        // With the switch on, an agent that is not offered every step's tool is not offered the composition.
        host.Services.GetRequiredService<TestCompositionSwitchState>().Answer = _ => true;
        var narrow = await host.CreateConversationAsync(agentId: NarrowAgent);
        await host.SendAsync(narrow.Id, "Give me the overview.");

        var offered = Offered(model, 2);
        Assert.Contains(TestRiskToolCatalog.GetProject.ExportName, offered);
        Assert.DoesNotContain(TestCompositions.OverviewFunction, offered);
    }

    /// <summary>
    /// FB60: a Development host validates scopes, so a scoped service resolved from the root provider
    /// stops it. The composition catalog, a singleton, reads its steps' tools in a scope of its own, so
    /// the host starts next to a scoped catalog whose tool a step calls, and the step runs with the tool of
    /// the turn's own services, never with that of the disposed scope.
    /// </summary>
    [Fact]
    public async Task A_composition_starts_and_runs_next_to_a_scoped_catalog_with_scope_validation()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestScopedStatusCatalog.CompositionFunction, new { input = new { projectId } }, "call-status")
            .RespondWithText("The project is on track.");
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                services.AddSingleton<TestRiskToolRecorder>();
                services.AddSingleton(new TestCompositionSwitchState());
                services.AddSingleton<TestScopedStatusRuns>();
                services.AddScoped<TestScopedStatusRequest>();
                services.AddScoped<INhAiToolCatalog, TestScopedStatusCatalog>();
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestRiskToolCatalog>());
            },
            assistantBuilder: assistant => assistant
                .ConfigureAgentContext(AgentId, context => context.AllowCompositions())
                .UseCompositionSwitch<TestCompositionSwitch>()
                .AddComposition<ProjectRiskInput>(TestScopedStatusCatalog.CompositionId, composition => composition
                    .Describe("Show one project with its status.")
                    .Step("project", TestRiskToolCatalog.GetProject.Id, context => new { projectId = context.Input.ProjectId })
                    .Step("status", TestScopedStatusCatalog.Status.Id, context => new
                    {
                        projectId = context.Result("project").GetProperty("id").GetString()
                    })
                    .Returns(context => new { project = context.Result("project"), status = context.Result("status") })));
        using var client = app.CreateClient();
        using var created = await client.PostAsync("/api/assistant/conversations", Json(new { agentId = AgentId }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var conversation = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var conversationId = conversation.RootElement.GetProperty("id").GetString();

        using var sent = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/messages",
            Json(new { text = "How is the project doing?", clientMessageId = Guid.NewGuid().ToString("N") }));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        await ReadAllAsync(sent);

        Assert.Contains(TestScopedStatusCatalog.CompositionFunction, Offered(model, 0));
        var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal("on-track", result.GetProperty("data").GetProperty("status").GetProperty("status").GetString());
        Assert.Equal([TestRiskToolCatalog.GetProject.Id], app.Services.GetRequiredService<TestRiskToolRecorder>().RanTools);
        // The step ran with the scoped catalog of the turn, whose scope was still open.
        var run = Assert.Single(app.Services.GetRequiredService<TestScopedStatusRuns>().Ran);
        Assert.Equal(projectId.ToString(), run.ProjectId);
        Assert.False(run.ScopeDisposed);
    }

    internal static NhAiToolDescriptor Composition(AssistantTestHost host, string id)
    {
        return host.Services.GetServices<INhAiToolCatalog>()
            .SelectMany(catalog => catalog.Descriptors)
            .Single(descriptor => descriptor.Id == id);
    }

    private static IReadOnlyList<string> Offered(NhAiScriptedChatClient model, int request)
    {
        return (model.Requests[request].Options?.Tools ?? []).Select(tool => tool.Name).ToArray();
    }

    private static JsonElement ResultOf(NhAiScriptedChatClient model, string callId)
    {
        var result = model.Requests
            .SelectMany(request => request.Messages)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Last(content => content.CallId == callId)
            .Result;
        return JsonSerializer.SerializeToElement(result);
    }

    private static async Task<int> ToolCallsTodayAsync(AssistantTestHost host, string actorId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.BudgetLedgers
            .Where(ledger => ledger.ActorId == actorId)
            .SumAsync(ledger => ledger.ToolCalls);
    }

    private static async Task<List<AssistantApproval>> ApprovalsAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Approvals.Where(approval => approval.ConversationId == conversationId).ToListAsync();
    }

    private Task<AssistantTestHost> CreateChatHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton<TestRiskToolRecorder>();
                services.AddSingleton(new TestCompositionSwitchState());
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestRiskToolCatalog>());
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(NarrowAgent) with { ToolSelectors = [TestCompositions.Overview, TestRiskToolCatalog.GetProject.Id] })
                .ConfigureAgentContext(AgentId, context => context.AllowCompositions())
                .ConfigureAgentContext(NarrowAgent, context => context.AllowCompositions())
                .UseCompositionSwitch<TestCompositionSwitch>()
                .AddTestCompositions());
    }

    private Task<AssistantTestHost> CreateRunHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton<TestRiskToolRecorder>();
                services.AddSingleton<TestGuardOrigins>();
                services.AddSingleton(new TestCompositionSwitchState());
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestRiskToolCatalog>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("risk-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .AddProposalGuard<TestOriginGuard>(
                    [TestRiskToolCatalog.FlagRisk.Id],
                    NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal)
                .ConfigureAgent(AgentId, agent => agent
                    .ServiceAccount(ServiceAccount)
                    .ActWithAccountableRights()
                    .UseApprovalPolicy("risk-review"))
                .ConfigureAgentContext(AgentId, context => context.AllowCompositions())
                .UseCompositionSwitch<TestCompositionSwitch>()
                .AddTestCompositions());
    }

    internal sealed record StartedRun(AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    internal static async Task<StartedRun> StartRunAsync(AssistantTestHost host, Guid projectId, bool shadow)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId.ToString()), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadow)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "project-created",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            Shadow = shadow,
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
            null,
            thread.AccountableOwnerId,
            IsRequestTurn: false));
    }

    internal static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, StartedRun run)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                run.AgentTurn,
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, "Review the project's risks.")]),
            CancellationToken.None);
    }
}

/// <summary>
/// The compositions of the tests: an overview that only reads and is exposed over MCP, a chain that
/// flags and tags a risk, and an overview with a read the person may not do.
/// </summary>
internal static class TestCompositions
{
    public const string Overview = "projects.overview";
    public const string OverviewFunction = "projects_overview_v1";
    public const string FlagOpenRisks = "projects.flag-open-risks";
    public const string FlagOpenRisksFunction = "projects_flag_open_risks_v1";
    public const string SecretOverview = "projects.secret-overview";
    public const string SecretOverviewFunction = "projects_secret_overview_v1";

    public static NhAssistantBuilder AddTestCompositions(this NhAssistantBuilder assistant)
    {
        return assistant
            .AddComposition<ProjectRiskInput>(Overview, composition => composition
                .Describe("Show one project with its open risks and the people who own them.")
                .ExposeOverMcp()
                .Step("project", TestRiskToolCatalog.GetProject.Id, context => new { projectId = context.Input.ProjectId })
                .Step("risks", TestRiskToolCatalog.ListRisks.Id, context => new { projectId = context.Result("project").GetProperty("id").GetString() })
                .Step("owners", TestRiskToolCatalog.GetPeople.Id, context => new
                {
                    ids = context.Result("risks").EnumerateArray().Select(risk => risk.GetProperty("ownerId").GetString()).Distinct().ToArray()
                })
                .Returns(context => new
                {
                    project = context.Result("project"),
                    risks = context.Result("risks"),
                    owners = context.Result("owners")
                }))
            .AddComposition<ProjectRiskInput>(FlagOpenRisks, composition => composition
                .Describe("Flag the first open risk of one project and tag it.")
                .Step("risks", TestRiskToolCatalog.ListRisks.Id, context => new { projectId = context.Input.ProjectId })
                .Step("flag", TestRiskToolCatalog.FlagRisk.Id, context => new { riskId = context.Result("risks")[0].GetProperty("id").GetString() })
                .Step("tag", TestRiskToolCatalog.TagRisk.Id, context => new { riskId = context.Result("risks")[0].GetProperty("id").GetString() })
                .Returns(context => new
                {
                    flagged = context.StepOf("flag")?.Status.ToString(),
                    tagged = context.TryGetResult("tag", out var tag) ? tag : (JsonElement?)null
                }))
            .AddComposition<ProjectRiskInput>(SecretOverview, composition => composition
                .Describe("Show one project with its secret.")
                .Step("project", TestRiskToolCatalog.GetProject.Id, context => new { projectId = context.Input.ProjectId })
                .Step("secret", TestRiskToolCatalog.ReadSecret.Id, context => new { projectId = context.Input.ProjectId })
                .Returns(context => new { project = context.Result("project"), secret = context.Result("secret") }));
    }
}

/// <summary>
/// FB60: what the scoped status tool ran, and whether the scope its catalog came from was disposed then.
/// </summary>
public sealed class TestScopedStatusRuns
{
    public ConcurrentQueue<(string? ProjectId, bool ScopeDisposed)> Ran { get; } = new();
}

/// <summary>
/// FB60: a service of one scope, which the scoped catalog takes; it knows when its scope was disposed.
/// </summary>
public sealed class TestScopedStatusRequest : IDisposable
{
    public bool Disposed { get; private set; }

    public void Dispose()
    {
        Disposed = true;
    }
}

/// <summary>
/// FB60: a catalog registered as a scoped service, as an application registers one that takes a scoped
/// service. Its descriptors stay as they are while the host runs, so a composition step may call its
/// tool; the tool records whether the scope of the catalog that created it was disposed.
/// </summary>
public sealed class TestScopedStatusCatalog(TestScopedStatusRequest request, TestScopedStatusRuns runs) : INhAiToolCatalog
{
    public const string CompositionId = "projects.scoped-status";
    public const string CompositionFunction = "projects_scoped_status_v1";

    public static readonly NhAiToolDescriptor Status = new(
        "projects.status",
        1,
        "Test tool projects.status.",
        typeof(JsonElement),
        typeof(JsonElement),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        true,
        [TestProjectToolCatalog.ReadPolicy])
    {
        ExportName = "projects_status_v1",
        CatalogId = "project-status",
        ContractHash = new string('9', 64),
        SchemaHash = new string('9', 64),
        Approval = NhAiApprovalRequirement.PolicyControlled,
        AllowAgentGrant = true
    };

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [Status];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "project-status",
        1,
        new string('8', 64),
        [new NhAiToolManifestEntry(Status.Id, 1, Status.SchemaHash, Status.ContractHash) { ExportName = Status.ExportName }]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        Func<JsonElement, CancellationToken, Task<TaskResult<JsonElement?>>> invoke =
            (input, cancellationToken) => invoker.InvokeAsync(
                Status,
                input,
                (_, _) =>
                {
                    var projectId = input.GetProperty("projectId").GetString();
                    runs.Ran.Enqueue((projectId, request.Disposed));
                    return Task.FromResult(TaskResult<JsonElement?>.Succeeded(JsonSerializer.SerializeToElement(new { projectId, status = "on-track" })));
                },
                cancellationToken);
        return
        [
            NhAiGovernedAIFunction.Create(
                Status,
                AIFunctionFactory.Create(invoke, new AIFunctionFactoryOptions
                {
                    Name = Status.ExportName,
                    Description = Status.Description,
                    ExcludeResultSchema = true,
                    MarshalResult = NhAiToolResult.CreateMarshaller<JsonElement?>()
                }),
                services)
        ];
    }
}

/// <summary>
/// The origins proposal guards saw.
/// </summary>
public sealed class TestGuardOrigins
{
    public ConcurrentQueue<NhAiToolCallOrigin?> Seen { get; } = new();
}

/// <summary>
/// A proposal guard that records the origin of every proposal a call makes and lets it through.
/// </summary>
public sealed class TestOriginGuard(TestGuardOrigins origins) : INhAiProposalGuard
{
    public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(NhAiProposalGuardContext context, CancellationToken cancellationToken = default)
    {
        if (context.Invocation is not null)
        {
            origins.Seen.Enqueue(context.Origin);
        }

        return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>([]);
    }

    public ValueTask<NhAiPreconditionResult> CheckAsync(NhAiProposalGuardContext context, CancellationToken cancellationToken = default)
    {
        if (context.Invocation is not null)
        {
            origins.Seen.Enqueue(context.Origin);
        }

        return ValueTask.FromResult(NhAiPreconditionResult.Satisfied);
    }
}
