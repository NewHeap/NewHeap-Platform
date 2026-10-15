using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
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
/// Skills of an agent: routing by trigger, rules and model triage, use_skill, and the budget and
/// state of a skill.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantAgentSkillTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string FollowUp = "follow-up";
    private const string Risk = "risk";
    private const string Notes = "notes";
    private const string RiskInstructions = "Judge whether the project misses its deadline.";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_trigger_with_a_skill_runs_that_skill_by_its_rules_and_records_it(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        await using var host = await CreateHostAsync(provider, models);
        var skillRules = host.Services.GetRequiredService<TestSkillRuleScript>();

        var receipt = Assert.Single(await PublishAsync(host, "task done"));
        await ExecuteAsync(host, receipt.RunId!.Value);

        var run = await Runs(host).FindRunAsync(receipt.RunId.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Completed, run!.Status);
        Assert.Equal(FollowUp, run.SkillId);
        Assert.Equal(NhAssistantSkillChoosers.Trigger, run.SkillChosenBy);
        Assert.Equal(NhAssistantRunKinds.Rule, run.Kind);
        Assert.Single(skillRules.Contexts);
        Assert.Empty(host.Services.GetRequiredService<TestRuleScript>().Contexts);
        Assert.Empty(models.Default.Requests);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerAccepted && evt.SkillId == FollowUp);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentRunEnded && evt.SkillId == FollowUp);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Rules_route_a_run_to_a_model_skill_with_its_tools_instructions_and_profile(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Skill.RespondWithText("The deadline is at risk.");
        await using var host = await CreateHostAsync(provider, models);
        host.Services.GetRequiredService<TestRuleScript>().Run = (_, _) =>
            Task.FromResult(NhAssistantRuleResult.UseSkill(Risk, "The deadline is in two days."));

        var receipt = Assert.Single(await PublishAsync(host, "rule check"));
        await ExecuteAsync(host, receipt.RunId!.Value);

        var run = await Runs(host).FindRunAsync(receipt.RunId.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Completed, run!.Status);
        Assert.Equal(Risk, run.SkillId);
        Assert.Equal(NhAssistantSkillChoosers.Rules, run.SkillChosenBy);
        Assert.Equal(NhAssistantRunKinds.RuleThenModel, run.Kind);
        Assert.Empty(models.Default.Requests);
        var request = Assert.Single(models.Skill.Requests);
        Assert.Contains(RiskInstructions, request.Options!.Instructions);
        var tools = ToolNames(request);
        Assert.Contains("projects_change_status_v1", tools);
        Assert.DoesNotContain("projects_search_v1", tools);
        var briefing = Assert.Single(request.Messages, message => message.Text.Contains("<subject-briefing>", StringComparison.Ordinal)).Text;
        Assert.Contains("You work in skill 'risk'", briefing);
        Assert.Contains("The deadline is in two days.", briefing);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Triage_routes_a_run_to_a_skill_or_ends_it_with_nothing_to_do(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Triage
            .RespondWithText("""{"skill":"notes","reason":"The project needs a note."}""")
            .RespondWithText("""{"skill":null,"reason":"Nothing changed that matters."}""");
        models.Default.RespondWithText("I wrote the note.");
        await using var host = await CreateHostAsync(provider, models);

        var routed = Assert.Single(await PublishAsync(host, "triage first"));
        await ExecuteAsync(host, routed.RunId!.Value);
        var quiet = Assert.Single(await PublishAsync(host, "triage second"));
        await ExecuteAsync(host, quiet.RunId!.Value);

        var first = await Runs(host).FindRunAsync(routed.RunId.Value, CancellationToken.None);
        Assert.Equal(Notes, first!.SkillId);
        Assert.Equal(NhAssistantSkillChoosers.Triage, first.SkillChosenBy);
        Assert.Equal(NhAssistantRunStatuses.Completed, first.Status);
        var triage = models.Triage.Requests[0];
        Assert.Empty(ToolNames(triage));
        Assert.Contains($"- {Notes}: ", triage.Messages[^1].Text);
        Assert.Contains($"- {Risk}: ", triage.Messages[^1].Text);
        Assert.Contains("triage first", triage.Messages[^1].Text);
        Assert.Single(models.Default.Requests);

        var second = await Runs(host).FindRunAsync(quiet.RunId.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Completed, second!.Status);
        Assert.Null(second.SkillId);
        Assert.Contains("Nothing changed that matters.", second.Summary);
        Assert.Single(models.Default.Requests);
        Assert.Equal(2, models.Triage.Requests.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_model_switches_skills_within_a_run_at_most_twice(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Default
            .RespondWithFunctionCall("use_skill", new { skill = Notes, reason = "A note is needed." }, "call-notes")
            .RespondWithFunctionCall("use_skill", new { skill = Risk, reason = "The deadline matters too." }, "call-risk");
        models.Skill.RespondWithText("Both are handled.");
        await using var host = await CreateHostAsync(provider, models);
        host.Services.GetRequiredService<TestRuleScript>().Run = (_, _) =>
            Task.FromResult(NhAssistantRuleResult.HandOver("The rules cannot tell."));

        var receipt = Assert.Single(await PublishAsync(host, "rule unclear"));
        await ExecuteAsync(host, receipt.RunId!.Value);

        var run = await Runs(host).FindRunAsync(receipt.RunId.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Completed, run!.Status);
        Assert.Equal(Risk, run.SkillId);
        Assert.Equal(NhAssistantSkillChoosers.Model, run.SkillChosenBy);
        Assert.Equal(2, models.Default.Requests.Count);
        Assert.Contains("use_skill", ToolNames(models.Default.Requests[0]));
        Assert.Contains(models.Default.Requests[1].Messages, message => message.Text.Contains("You now work in skill 'notes'", StringComparison.Ordinal));
        var last = Assert.Single(models.Skill.Requests);
        Assert.Contains(last.Messages, message => message.Text.Contains("You now work in skill 'risk'", StringComparison.Ordinal));
        // After two switches the model is offered no further switch.
        Assert.DoesNotContain("use_skill", ToolNames(last));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_persons_turn_starts_without_a_skill_and_loads_one(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Default.RespondWithFunctionCall("use_skill", new { skill = Risk, reason = "The person asks about the deadline." }, "call-risk");
        models.Skill.RespondWithText("The deadline is safe.");
        await using var host = await CreateHostAsync(provider, models);
        var thread = await ThreadAsync(host, Guid.NewGuid());

        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), CancellationToken.None))!;
        var result = await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                new NhAssistantAgentTurnContext(
                    thread,
                    info,
                    services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId],
                    null,
                    NhAssistantTriggerIds.QueuedMessage,
                    "member-1",
                    thread.AccountableOwnerId,
                    IsRequestTurn: true),
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), "member-1", NhAssistantTriggerIds.QueuedMessage, "Will we make the deadline?")]),
            CancellationToken.None);

        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        Assert.Equal(Risk, result.SkillId);
        var first = Assert.Single(models.Default.Requests);
        Assert.Contains("use_skill", ToolNames(first));
        Assert.Contains("projects_search_v1", ToolNames(first));
        Assert.Single(models.Skill.Requests);
        Assert.Empty(models.Triage.Requests);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_skills_state_and_budget_stop_its_work_while_the_other_skills_keep_working(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Skill
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = Guid.NewGuid(), status = "OnHold" } }, "call-1")
            .RespondWithText("Proposed.");
        await using var host = await CreateHostAsync(provider, models);
        var rules = host.Services.GetRequiredService<TestRuleScript>();
        rules.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.UseSkill(Risk, "Check the deadline."));

        // Stopping the follow-up skill refuses its trigger before any lambda; the agent works on.
        var stopped = await ControlAsync(host, control => control.StopSkillAsync(AgentId, FollowUp, "operator-1", "Wrong reminders"));
        Assert.True(stopped.Success);
        Assert.Equal(NhAssistantAgentRunStates.Stopped, stopped.Data!.RunState);
        var refused = Assert.Single(await PublishAsync(host, "task done"));
        Assert.Equal(NhAssistantErrorCodes.SkillStopped, refused.ReasonCode);

        // Pausing the risk skill lets a run that its rules route there wait for it.
        await ControlAsync(host, control => control.PauseSkillAsync(AgentId, Risk, "operator-1", "Import"));
        var waiting = Assert.Single(await PublishAsync(host, "rule check"));
        await ExecuteAsync(host, waiting.RunId!.Value);
        var cancelled = await Runs(host).FindRunAsync(waiting.RunId.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Cancelled, cancelled!.Status);
        Assert.Equal(NhAssistantErrorCodes.SkillPaused, cancelled.FailureCode);
        var state = await ControlAsync(host, control => control.GetAsync(AgentId));
        Assert.Equal(1, state!.Skills.Single(skill => skill.SkillId == Risk).WaitingInputs);
        Assert.Equal(NhAssistantAgentRunStates.Stopped, state.Skills.Single(skill => skill.SkillId == FollowUp).RunState);
        Assert.Equal(NhAssistantAgentRunStates.Active, state.Skills.Single(skill => skill.SkillId == Notes).RunState);

        // Resuming it starts one run in the skill for the waiting event.
        var resumed = await ControlAsync(host, control => control.ResumeSkillAsync(AgentId, Risk, "operator-1"));
        Assert.Equal(1, resumed.Data!.StartedRuns);
        var resumedRun = await LatestRunAsync(host, waiting.ConversationId!.Value);
        Assert.Equal(Risk, resumedRun.SkillId);
        await ExecuteAsync(host, resumedRun.Id);
        Assert.Equal(NhAssistantRunStatuses.Completed, (await Runs(host).FindRunAsync(resumedRun.Id, CancellationToken.None))!.Status);

        // The risk skill allows one tool call a day: the next run routed there fails on its budget.
        var budget = Assert.Single(await PublishAsync(host, "rule again"));
        await ExecuteAsync(host, budget.RunId!.Value);
        var exhausted = await Runs(host).FindRunAsync(budget.RunId.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Failed, exhausted!.Status);
        Assert.Equal(NhAssistantErrorCodes.BudgetExhausted, exhausted.FailureCode);
        var usage = (await ControlAsync(host, control => control.GetAsync(AgentId)))!.Skills.Single(skill => skill.SkillId == Risk);
        Assert.Equal(1, usage.ToolCallsToday);
        Assert.Equal(1, usage.DailyToolCalls);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentPaused && evt.SkillId == Risk);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentRunsStopped && evt.SkillId == FollowUp);
    }

    [Fact]
    public void A_skill_outside_the_agents_tools_or_a_trigger_for_an_unknown_skill_fails_validation()
    {
        var outside = new ServiceCollection();
        outside.AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent())
            .ConfigureAgent(AgentId, agent => agent.Skill("orders", skill => skill
                .Description("Handles orders.")
                .Tools("orders.*"))));
        var unknown = new ServiceCollection();
        unknown.AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent())
            .ConfigureAgent(AgentId, agent => agent.Skill(Notes, skill => skill.Description("Writes notes.")))
            .AddTrigger<ProjectChanged>("unknown-skill", trigger => trigger
                .StartAgent(AgentId)
                .Skill("missing")
                .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                .Message(evt => evt.Change)));

        var tools = Assert.Throws<InvalidOperationException>(() => State(outside).ValidateAgentRuns());
        var trigger = Assert.Throws<InvalidOperationException>(() => State(unknown).ValidateAgentRuns());
        var both = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent())
            .ConfigureAgent(AgentId, agent => agent.Skill(Risk, skill => skill
                .Description("Judges risks.")
                .UseProfile("skill-chat")
                .UseRuleHandler<TestRules>()))));

        Assert.Contains("orders.*", tools.Message);
        Assert.Contains("missing", trigger.Message);
        Assert.Contains("rules or a model profile", both.Message);
    }

    private static NhAssistantRegistrationState State(IServiceCollection services)
    {
        return (NhAssistantRegistrationState)services.Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState)).ImplementationInstance!;
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, SkillModels models)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            models.Default,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<TestSkillRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddKeyedSingleton<IChatClient>("skill-chat-model", models.Skill);
                services.AddKeyedSingleton<IChatClient>("triage-chat-model", models.Triage);
                services.AddNewHeapPlatformAI(ai =>
                {
                    ai.AddChatProfile("skill-chat", profile => profile
                        .UseKeyedClient("skill-chat-model")
                        .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                        .WithStreaming(NhAiStreamingPolicy.Allowed)
                        .PermitDataClassifications(NhAiDataClassification.Internal)
                        .PermitExecutionRegions("local")
                        .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16));
                    ai.AddChatProfile("triage-chat", profile => profile
                        .UseKeyedClient("triage-chat-model")
                        .WithStreaming(NhAiStreamingPolicy.Allowed)
                        .PermitDataClassifications(NhAiDataClassification.Internal)
                        .PermitExecutionRegions("local")
                        .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 256, maxCalls: 2));
                });
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>()
                    .Triage(triage => triage.UseProfile("triage-chat"))
                    .Skill(FollowUp, skill => skill
                        .Description("Follows up completed tasks by rule.")
                        .UseRuleHandler<TestSkillRules>())
                    .Skill(Risk, skill => skill
                        .Description("Judges deadline risks and proposes a status change.")
                        .UseProfile("skill-chat")
                        .Instructions(RiskInstructions)
                        .Tools("projects.change-status")
                        .Budget(NhAssistantAgentBudget.PerDay(toolCalls: 1, tokens: 1_000_000)))
                    .Skill(Notes, skill => skill
                        .Description("Writes notes about the project.")
                        .Tools("projects.search")))
                .AddTrigger<ProjectChanged>("task-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => evt.Change.StartsWith("task", StringComparison.Ordinal))
                    .Skill(FollowUp)
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("rule-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => evt.Change.StartsWith("rule", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("triage-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => evt.Change.StartsWith("triage", StringComparison.Ordinal))
                    .UseModel()
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue)));
    }

    /// <summary>
    /// Publishes a change of one new project and returns the receipt of the trigger it is for. A
    /// stopped skill's trigger refuses every event before its filter runs.
    /// </summary>
    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, string change)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), change));
        // The trigger the change is for: task, rule or triage.
        var triggerId = change.Split(' ')[0] + "-changed";
        return receipts.Where(receipt => receipt.TriggerId == triggerId).ToArray();
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid runId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(1);
        await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
    }

    private static async Task<AssistantThread> ThreadAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId.ToString("N")), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
    }

    private static async Task<AssistantRun> LatestRunAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs.Where(run => run.ConversationId == conversationId).OrderByDescending(run => run.CreatedAt).FirstAsync();
    }

    private static async Task<T> ControlAsync<T>(AssistantTestHost host, Func<INhAssistantAgentControl, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>());
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private static string[] ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }

    private sealed class SkillModels
    {
        public NhAiScriptedChatClient Default { get; } = new();

        public NhAiScriptedChatClient Skill { get; } = new();

        public NhAiScriptedChatClient Triage { get; } = new();
    }
}

public sealed class TestSkillRuleScript
{
    public Func<NhAssistantRuleContext, CancellationToken, Task<NhAssistantRuleResult>> Run { get; set; } =
        (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Followed up the task."));

    public ConcurrentQueue<NhAssistantRuleContext> Contexts { get; } = new();
}

public sealed class TestSkillRules(TestSkillRuleScript script) : INhAssistantAgentRuleHandler
{
    public Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        script.Contexts.Enqueue(context);
        return script.Run(context, cancellationToken);
    }
}
