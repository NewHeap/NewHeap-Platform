using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The content-free facts of an ended run on <c>AgentRunEnded</c>: what started it, whether it was a
/// shadow run, its skill version, its model calls and its status apart from its failure code, so an
/// audit sink counts runs, runs without a model call and shadow runs per agent and skill on its own,
/// on both relational providers.
/// </summary>
public sealed class AssistantRunFactsTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Planner = "project-planner";
    private const string Variant = "project-planner-v2";
    private const string Checker = "project-checker";
    private const string Review = "review";
    private const string FollowUp = "follow-up";
    private const string Weekly = "weekly-review";
    private const string DataTrigger = "data-follow-up";
    private const string ChangeStatus = "projects_change_status_v1";
    private const string Admin = "admin-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_audit_sink_counts_runs_rule_only_runs_and_shadow_runs_per_agent_and_skill_from_ended_runs_alone(AssistantTestProvider provider)
    {
        var models = new RunFactModels();
        var proposed = Guid.NewGuid();
        models.Live
            // The planner proposes and answers: two model calls in one turn.
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId = proposed, status = "OnHold" } }, "call-live")
            .RespondWithText("I proposed to put it on hold.")
            // The failing run: each attempt calls the model once more after a transient error.
            .RespondWithProviderError("server_is_overloaded")
            .RespondWithProviderError("server_is_overloaded")
            .RespondWithProviderError("server_is_overloaded")
            .RespondWithProviderError("server_is_overloaded");
        models.Variant
            .RespondWithText("I would leave it.")
            .RespondWithText("Nothing to do.");
        await using var host = await CreateHostAsync(provider, models);

        // An event starts the planner, and its shadow variant receives the same event.
        var planned = Assert.Single(await PublishAsync(host, "plan alpha", proposed));
        await ExecuteAsync(host, planned.RunId!.Value);
        var mirrored = Assert.Single(await RunsOfAsync(host, Variant, proposed));
        await ExecuteAsync(host, mirrored.Id);

        // The checker works by rules: on an event, on a schedule in a skill and when a person starts it.
        var checkedProject = Guid.NewGuid();
        await ExecuteAsync(host, Assert.Single(await PublishAsync(host, "check beta", checkedProject)).RunId!.Value);
        var weeklyProject = Guid.NewGuid();
        host.Services.GetRequiredService<ScheduledProjects>().Ids.Add(weeklyProject);
        await ExecuteAsync(host, Assert.Single(await TickAsync(host)).RunId!.Value);
        await ExecuteAsync(host, (await StartNowAsync(host, Guid.NewGuid())).RunId!.Value);

        // A skill and a trigger defined as data start in shadow: the run works in version 1 of the skill.
        Assert.True((await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            Checker,
            FollowUp,
            new NhAssistantDataSkillRequest("Follows up changed projects by rule.", RuleHandler: "project-follow-up"),
            Admin))).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.SaveTriggerAsync(
            Checker,
            DataTrigger,
            new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, FollowUp, Event: "project.changed", Conflict: NhAssistantTriggerConflictNames.Queue),
            Admin))).Success);
        await ExecuteAsync(host, Assert.Single(await PublishAsync(host, "data gamma", Guid.NewGuid())).RunId!.Value);

        // A run that fails transiently is retried once and then fails; the model calls of both
        // attempts count.
        var failingProject = Guid.NewGuid();
        var failing = Assert.Single(await PublishAsync(host, "plan fail", failingProject)).RunId!.Value;
        await ExecuteAsync(host, failing, attempt: 1);
        await ExecuteAsync(host, failing, attempt: 2);
        await ExecuteAsync(host, Assert.Single(await RunsOfAsync(host, Variant, failingProject)).Id);

        // Every ended run carries its facts, and the model calls are the ones stored on the run, one
        // per usage record of the run's governed model calls.
        var ended = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentRunEnded).ToArray();
        Assert.Equal(8, ended.Length);
        foreach (var evt in ended)
        {
            var run = (await Runs(host).FindRunAsync(evt.RunId!.Value, CancellationToken.None))!;
            Assert.Equal(run.ModelCalls, evt.ModelCallCount);
            Assert.Equal((run.Shadow, run.SkillVersion, run.Status, run.FailureCode), (evt.Shadow!.Value, evt.SkillVersion, evt.RunStatus, evt.RunFailureCode));
            Assert.Equal(run.TriggerKind, evt.TriggerKind);
            Assert.Equal(evt.RunFailureCode is null ? evt.RunStatus : evt.RunStatus + ":" + evt.RunFailureCode, evt.ResultCode);
            var usage = host.Usage.Records.Where(record => record.CorrelationId == run.Id.ToString("N")).ToArray();
            Assert.Equal(run.ModelCalls, usage.Length);
            Assert.Equal(usage.Length, usage.Select(record => record.ModelCallId).Distinct().Count());
        }

        // What started each run.
        Assert.Equal(NhAssistantTriggerKinds.Event, Single(ended, Planner, planned.RunId.Value).TriggerKind);
        Assert.Equal(NhAssistantTriggerKinds.Event, Single(ended, Variant, mirrored.Id).TriggerKind);
        Assert.Equal(
            [NhAssistantTriggerKinds.Event, NhAssistantTriggerKinds.Manual, NhAssistantTriggerKinds.Schedule],
            ended.Where(evt => evt.AgentId == Checker && evt.SkillId != FollowUp).Select(evt => evt.TriggerKind!).Order(StringComparer.Ordinal));
        var data = Assert.Single(ended, evt => evt.AgentId == Checker && evt.SkillId == FollowUp);
        Assert.Equal((NhAssistantTriggerKinds.Event, DataTrigger, 1, true), (data.TriggerKind, data.TriggerId, data.SkillVersion, data.Shadow));

        // The failed run: its status apart from its failure code, and the calls of both attempts.
        var failed = Single(ended, Planner, failing);
        Assert.Equal((NhAssistantRunStatuses.Failed, NhAssistantErrorCodes.ModelUnavailable, 4), (failed.RunStatus, failed.RunFailureCode, failed.ModelCallCount));
        Assert.Equal(NhAssistantRunStatuses.Failed + ":" + NhAssistantErrorCodes.ModelUnavailable, failed.ResultCode);
        var completed = Single(ended, Planner, planned.RunId.Value);
        Assert.Equal((NhAssistantRunStatuses.Completed, null, 2), (completed.RunStatus, completed.RunFailureCode, completed.ModelCallCount));

        // What a sink counts per agent and skill from these events alone.
        var counts = ended
            .GroupBy(evt => (evt.AgentId, evt.SkillId))
            .ToDictionary(
                group => group.Key,
                group => (Runs: group.Count(), RuleOnly: group.Count(evt => evt.ModelCallCount == 0), Shadow: group.Count(evt => evt.Shadow == true), ModelCalls: group.Sum(evt => evt.ModelCallCount ?? 0)));
        Assert.Equal((2, 0, 0, 6), counts[(Planner, null)]);
        Assert.Equal((2, 0, 2, 2), counts[(Variant, null)]);
        Assert.Equal((2, 2, 0, 0), counts[(Checker, null)]);
        Assert.Equal((1, 1, 0, 0), counts[(Checker, Review)]);
        Assert.Equal((1, 1, 1, 0), counts[(Checker, FollowUp)]);
        Assert.Equal(5, counts.Count);
    }

    private static NhAssistantAuditEvent Single(IEnumerable<NhAssistantAuditEvent> ended, string agentId, Guid runId)
    {
        return Assert.Single(ended, evt => evt.AgentId == agentId && evt.RunId == runId);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, RunFactModels models)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            models.Live,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<ScheduledProjects>();
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<TestSkillRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddKeyedSingleton<IChatClient>("variant-chat-model", models.Variant);
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile("variant-chat", profile => profile
                    .UseKeyedClient("variant-chat-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .PermitDataClassifications(NhAiDataClassification.Internal)
                    .PermitExecutionRegions("local")
                    .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddAgent(AssistantTestData.Agent(Planner))
                .AddAgent(AssistantTestData.Agent(Variant) with { ProfileName = "variant-chat" })
                .AddAgent(AssistantTestData.Agent(Checker))
                .AddRuleHandler<TestSkillRules>("project-follow-up", "Follows up a project by rule.")
                .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                    .Description("A project changed.")
                    .When(change => change.Change.StartsWith("data", StringComparison.Ordinal))
                    .Subject(change => new NhAiSubjectRef("project", change.ProjectId.ToString("N")))
                    .Message(change => change.Change)
                    .Dedupe(change => change.EventId.ToString("N")))
                .ConfigureAgent(Planner, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .Retry(1))
                .ConfigureAgent(Variant, agent => agent
                    .ShadowOf(Planner)
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review"))
                .ConfigureAgent(Checker, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>()
                    .Skill(Review, skill => skill
                        .Description("Reviews the project by rule.")
                        .UseRuleHandler<TestSkillRules>()))
                .AddTrigger<ProjectChanged>("plan-changed", trigger => trigger
                    .StartAgent(Planner)
                    .When(evt => evt.Change.StartsWith("plan", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("check-changed", trigger => trigger
                    .StartAgent(Checker)
                    .When(evt => evt.Change.StartsWith("check", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddScheduledTrigger(Weekly, schedule => schedule
                    .StartAgent(Checker)
                    .Cron("0 7 * * 1")
                    .Skill(Review)
                    .Subjects((services, cancellationToken) => services.GetRequiredService<ScheduledProjects>().ListAsync(cancellationToken))
                    .Message(tick => "Weekly review of " + tick.Subject.Id + ".")));
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, string change, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, change));
        return receipts.Where(receipt => receipt.Outcome != NhAssistantTriggerOutcomes.Ignored).ToArray();
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> TickAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantScheduledTriggerJob>()
            .RunAsync(Weekly, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static async Task<NhAssistantTriggerReceipt> StartNowAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>().StartNowAsync(
            Checker,
            new NhAiSubjectRef("project", projectId.ToString("N")),
            "member-1",
            "Check the project.",
            CancellationToken.None);
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid runId, int attempt = 1)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(attempt);
        await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<AssistantRun>> RunsOfAsync(AssistantTestHost host, string agentId, Guid projectId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var subjectId = projectId.ToString("N");
        return await context.Runs
            .Join(
                context.Threads.Where(thread => thread.AgentId == agentId && thread.SubjectId == subjectId),
                run => run.ConversationId,
                thread => thread.ConversationId,
                (run, _) => run)
            .ToListAsync();
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private sealed class RunFactModels
    {
        public NhAiScriptedChatClient Live { get; } = new();

        public NhAiScriptedChatClient Variant { get; } = new();
    }
}
