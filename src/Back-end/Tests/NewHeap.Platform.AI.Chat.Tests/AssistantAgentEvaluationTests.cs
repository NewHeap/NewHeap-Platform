using System.Runtime.CompilerServices;
using System.Text.Json;
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
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Evaluation of agent variants: a shadow variant that receives the live agent's triggers, the
/// comparison of runs, proposals, cost and routing, its export, and replays of recorded runs.
/// </summary>
public sealed class AssistantAgentEvaluationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Planner = "project-planner";
    private const string Variant = "project-planner-v2";
    private const string ReplayTarget = "project-planner-replay";
    private const string ChangeStatus = "projects_change_status_v1";
    private const string Weekly = "weekly-plan";
    private const string Webhook = "plan-webhook";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_variant_receives_every_trigger_of_the_live_agent_once_and_nothing_it_proposes_executes(AssistantTestProvider provider)
    {
        var models = new EvaluationModels();
        var project = Guid.NewGuid();
        models.Live
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId = project, status = "OnHold" } }, "call-live")
            .RespondWithText("I proposed to put it on hold.");
        models.Variant
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId = project, status = "OnHold" } }, "call-variant")
            .RespondWithText("I would put it on hold.");
        await using var host = await CreateHostAsync(provider, models);

        // An event: the publisher reads the live receipt; the variant gets the same event once.
        var live = Assert.Single(await PublishAsync(host, "plan alpha", project));
        var variantThread = await ThreadOfAsync(host, Variant, project);
        var mirrored = Assert.Single(await RunsOfAsync(host, variantThread));
        var liveRun = (await Runs(host).FindRunAsync(live.RunId!.Value, CancellationToken.None))!;
        Assert.Equal(liveRun.TriggerId, mirrored.TriggerId);
        Assert.Equal(liveRun.TriggerEventKey, mirrored.TriggerEventKey);
        Assert.True(mirrored.Shadow);
        await ExecuteAsync(host, liveRun.Id);
        await ExecuteAsync(host, mirrored.Id);

        var liveProposal = Assert.Single(await Runs(host).GetRunApprovalsAsync(liveRun.Id, CancellationToken.None));
        var shadowProposal = Assert.Single(await Runs(host).GetRunApprovalsAsync(mirrored.Id, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, liveProposal.Status);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, shadowProposal.Status);
        Assert.Empty(host.Services.GetRequiredService<RecordingOperations>().Executions);
        var executed = (await Runs(host).FindRunAsync(mirrored.Id, CancellationToken.None))!;
        Assert.True(executed.ModelCalls >= 2);

        // A schedule tick and a webhook delivery reach the variant too; its own trigger only it.
        var weeklyProject = Guid.NewGuid();
        host.Services.GetRequiredService<ScheduledProjects>().Ids.Add(weeklyProject);
        Assert.Single(await TickAsync(host));
        Assert.Single(await RunsOfAsync(host, await ThreadOfAsync(host, Variant, weeklyProject)));
        var hookProject = Guid.NewGuid();
        await MirrorWebhookAsync(host, hookProject);
        Assert.Single(await RunsOfAsync(host, await ThreadOfAsync(host, Variant, hookProject)));
        var own = Guid.NewGuid();
        Assert.Equal("variant-changed", Assert.Single(await PublishAsync(host, "variant own", own)).TriggerId);
        Assert.Single(await RunsOfAsync(host, await ThreadOfAsync(host, Variant, own)));
        Assert.Empty(await RunsOfAsync(host, await ThreadOfAsync(host, Planner, own)));

        // Pausing the live agent leaves the variant as it is.
        await ControlAsync(host, control => control.PauseAsync(Planner, "operator-1", null));
        var paused = Guid.NewGuid();
        Assert.Equal(NhAssistantTriggerOutcomes.Waiting, Assert.Single(await PublishAsync(host, "plan beta", paused)).Outcome);
        Assert.Single(await RunsOfAsync(host, await ThreadOfAsync(host, Variant, paused)));
        var state = (await ControlAsync(host, control => control.GetAsync(Variant)))!;
        Assert.Equal(NhAssistantAgentRunStates.Active, state.RunState);
        Assert.Equal(Planner, state.ShadowOf);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_evaluation_pairs_runs_and_compares_proposals_decisions_cost_and_routing_without_content(AssistantTestProvider provider)
    {
        var models = new EvaluationModels();
        Guid[] projects = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        models.Live
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId = projects[0], status = "OnHold" } }, "call-1")
            .RespondWithText("Proposed.")
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId = projects[1], status = "OnHold" } }, "call-2")
            .RespondWithText("Proposed.")
            .RespondWithText("Nothing to do.");
        models.Variant
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId = projects[0], status = "OnHold" } }, "call-1")
            .RespondWithText("Proposed.")
            .RespondWithText("Nothing to do.")
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId = projects[2], status = "Active" } }, "call-3")
            .RespondWithText("Proposed.");
        await using var host = await CreateHostAsync(provider, models);
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var liveRuns = new List<Guid>();
        for (var index = 0; index < projects.Length; index++)
        {
            var receipt = Assert.Single(await PublishAsync(host, "plan step " + index, projects[index]));
            liveRuns.Add(receipt.RunId!.Value);
            await ExecuteAsync(host, receipt.RunId.Value);
            await ExecuteAsync(host, Assert.Single(await RunsOfAsync(host, await ThreadOfAsync(host, Variant, projects[index]))).Id);
        }

        // People reject the second live proposal and say why.
        var rejected = Assert.Single(await Runs(host).GetRunApprovalsAsync(liveRuns[1], CancellationToken.None));
        Assert.True(await Runs(host).TryTransitionApprovalAsync(
            rejected.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Rejected,
            NhAssistantApprovalTransition.None with { DecidedByActorId = "approver-1", DecidedAt = DateTimeOffset.UtcNow },
            CancellationToken.None));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var given = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalFeedbackService>().GiveAsync(
                rejected.Id,
                "approver-1",
                NhAssistantProposalFeedbackCategories.WrongValue,
                "The project is on schedule.",
                bulk: false,
                CancellationToken.None);
            Assert.True(given.Success);
        }

        var report = await CompareAsync(host, from);
        Assert.Equal(3, report.Runs.Paired);
        Assert.Equal(0, report.Runs.Replayed);
        Assert.Equal(0, report.Runs.LiveUnpaired);
        Assert.False(report.Runs.Truncated);
        var tool = Assert.Single(report.Tools);
        Assert.Equal("projects.change-status", tool.ToolId);
        Assert.Equal(1, tool.Agreed);
        Assert.Equal(1, tool.LiveOnly);
        Assert.Equal(1, tool.VariantOnly);
        Assert.Equal(1, tool.RejectedLiveNotProposed);
        Assert.Equal(0, tool.RejectedLiveAlsoProposed);
        Assert.Equal(1, tool.LiveDecisions.Pending);
        Assert.Equal(1, tool.LiveDecisions.Rejected);
        Assert.Equal(1, tool.LiveDecisions.RejectionCategories[NhAssistantProposalFeedbackCategories.WrongValue]);
        Assert.Equal(3, report.LiveCost.Runs);
        Assert.Equal(3, report.VariantCost.Runs);
        Assert.Equal(5, report.LiveCost.ModelCalls);
        Assert.Equal(2, report.LiveCost.ToolCalls);
        var routing = Assert.Single(report.Routing);
        Assert.Null(routing.LiveSkill);
        Assert.Equal(3, routing.Runs);
        Assert.Equal(2, report.Differences.Count);
        Assert.Contains(report.Differences, difference => difference.Kind == NhAssistantEvaluationDifferenceKinds.LiveOnly
            && difference.LiveStatus == NhAssistantApprovalStatuses.Rejected
            && difference.LiveRunId == liveRuns[1]);
        var json = JsonSerializer.Serialize(report, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantEvaluationDto);
        Assert.DoesNotContain("OnHold", json, StringComparison.Ordinal);
        Assert.DoesNotContain("plan step", json, StringComparison.Ordinal);
        Assert.DoesNotContain("on schedule", json, StringComparison.Ordinal);

        await using var exporting = host.Services.CreateAsyncScope();
        var records = await exporting.ServiceProvider.GetRequiredService<NhAssistantEvaluationService>()
            .ExportAsync(Planner, Variant, from, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(3, records.Count);
        Assert.Equal(["projects.change-status"], records[0].AgreedTools);
        var lines = string.Join("\n", records.Select(record => JsonSerializer.Serialize(record, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantEvaluationExportRecord)));
        Assert.DoesNotContain("OnHold", lines, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_replay_refires_recorded_inputs_oldest_first_and_stops_at_the_variants_budget(AssistantTestProvider provider)
    {
        var models = new EvaluationModels();
        models.Live.RespondWithText("Looked.").RespondWithText("Looked.").RespondWithText("Looked.");
        await using var host = await CreateHostAsync(provider, models);
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var liveRuns = new List<AssistantRun>();
        for (var index = 0; index < 3; index++)
        {
            var project = Guid.NewGuid();
            models.Replay
                .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId = project, status = "OnHold" } }, "call-" + index)
                .RespondWithText("Proposed.");
            var receipt = Assert.Single(await PublishAsync(host, "plan replay " + index, project));
            await ExecuteAsync(host, receipt.RunId!.Value);
            liveRuns.Add((await Runs(host).FindRunAsync(receipt.RunId.Value, CancellationToken.None))!);
        }

        var to = DateTimeOffset.UtcNow.AddSeconds(1);
        var started = await StartReplayAsync(host, ReplayTarget, new NhAssistantReplayRequest(Planner, from, to, 10));
        Assert.True(started.Success);
        var replayId = started.Data!.Id;
        Assert.Equal(replayId, Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Replays));
        Assert.Equal(NhAssistantErrorCodes.ReplayActive, Code(await StartReplayAsync(host, ReplayTarget, new NhAssistantReplayRequest(Planner, from, to, 10))));
        Assert.Equal(NhAssistantErrorCodes.ReplayNotShadow, Code(await StartReplayAsync(host, Planner, new NhAssistantReplayRequest(ReplayTarget, from, to, 10))));
        Assert.Equal(NhAssistantErrorCodes.MessageInvalid, Code(await StartReplayAsync(host, Variant, new NhAssistantReplayRequest(Planner, from, to, 501))));

        // One run at a time (the target runs one at once); its budget allows two tool calls a day.
        var replayed = new List<AssistantRun>();
        for (var round = 0; round < 3; round++)
        {
            await RunReplayAsync(host, replayId);
            var active = (await AllRunsOfAsync(host, ReplayTarget)).Where(run => replayed.All(done => done.Id != run.Id)).ToArray();
            foreach (var run in active)
            {
                await ExecuteAsync(host, run.Id);
                replayed.Add(run);
            }
        }

        Assert.Equal([liveRuns[0].Id, liveRuns[1].Id], replayed.Select(run => run.ReplayOfRunId!.Value).ToArray());
        var replay = (await host.Services.GetRequiredService<NhAssistantEvaluationStore>().FindReplayAsync(replayId, CancellationToken.None))!;
        Assert.Equal(NhAssistantReplayStatuses.Stopped, replay.Status);
        Assert.Equal(NhAssistantErrorCodes.BudgetExhausted, replay.StopReason);
        Assert.Equal(2, replay.RunsReplayed);
        var replayedInput = await FirstInputAsync(host, replayed[0].Id);
        Assert.Equal("plan replay 0", replayedInput.Text);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentReplayStarted && evt.ObjectId == replayId.ToString("N"));

        // The comparison pairs the replays with the runs they repeated.
        await using var scope = host.Services.CreateAsyncScope();
        var report = await scope.ServiceProvider.GetRequiredService<NhAssistantEvaluationService>()
            .CompareAsync(Planner, ReplayTarget, from, to, CancellationToken.None);
        Assert.Equal(2, report.Runs.Paired);
        Assert.Equal(2, report.Runs.Replayed);
        Assert.Equal(1, report.Runs.LiveUnpaired);
        Assert.Equal(2, Assert.Single(report.Tools).VariantOnly);
    }

    [Fact]
    public void A_variant_of_itself_of_an_unknown_or_a_shadow_agent_or_out_of_shadow_mode_fails_validation()
    {
        var self = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent(Variant))
            .ConfigureAgent(Variant, agent => agent.ShadowOf(Variant))));
        var unknown = Validate(assistant => assistant
            .AddAgent(AssistantTestData.Agent(Variant))
            .ConfigureAgent(Variant, agent => agent.ShadowOf(Planner)));
        var ofShadow = Validate(assistant => assistant
            .AddAgent(AssistantTestData.Agent(Planner))
            .AddAgent(AssistantTestData.Agent(Variant))
            .ConfigureAgent(Planner, agent => agent.Shadow())
            .ConfigureAgent(Variant, agent => agent.ShadowOf(Planner)));
        var live = Validate(assistant => assistant
            .AddAgent(AssistantTestData.Agent(Planner))
            .AddAgent(AssistantTestData.Agent(Variant))
            .ConfigureAgent(Planner, _ => { })
            .ConfigureAgent(Variant, agent => agent.ShadowOf(Planner).Shadow(false)));

        Assert.Contains("itself", self.Message);
        Assert.Contains("not configured", unknown.Message);
        Assert.Contains("not a live agent", ofShadow.Message);
        Assert.Contains("shadow mode", live.Message);
    }

    private static InvalidOperationException Validate(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(configure);
        var state = (NhAssistantRegistrationState)services.Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState)).ImplementationInstance!;
        return Assert.Throws<InvalidOperationException>(() => state.ValidateAgentRuns());
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, EvaluationModels models)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            models.Live,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<ScheduledProjects>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddKeyedSingleton<IChatClient>("variant-chat-model", models.Variant);
                services.AddKeyedSingleton<IChatClient>("replay-chat-model", models.Replay);
                services.AddNewHeapPlatformAI(ai =>
                {
                    foreach (var name in new[] { "variant-chat", "replay-chat" })
                    {
                        ai.AddChatProfile(name, profile => profile
                            .UseKeyedClient(name + "-model")
                            .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                            .WithStreaming(NhAiStreamingPolicy.Allowed)
                            .PermitDataClassifications(NhAiDataClassification.Internal)
                            .PermitExecutionRegions("local")
                            .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16));
                    }
                });
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddAgent(AssistantTestData.Agent(Planner))
                .AddAgent(AssistantTestData.Agent(Variant) with { ProfileName = "variant-chat" })
                .AddAgent(AssistantTestData.Agent(ReplayTarget) with { ProfileName = "replay-chat" })
                .ConfigureAgent(Planner, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review"))
                .ConfigureAgent(Variant, agent => agent
                    .ShadowOf(Planner)
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review"))
                .ConfigureAgent(ReplayTarget, agent => agent
                    .Shadow()
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .Budget(NhAssistantAgentBudget.PerDay(toolCalls: 2, tokens: 1_000_000), maxConcurrentRuns: 1))
                .AddTrigger<ProjectChanged>("plan-changed", trigger => trigger
                    .StartAgent(Planner)
                    .When(evt => evt.Change.StartsWith("plan", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .Dedupe(evt => evt.EventId.ToString("N"))
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("variant-changed", trigger => trigger
                    .StartAgent(Variant)
                    .When(evt => evt.Change.StartsWith("variant", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change))
                .AddScheduledTrigger(Weekly, schedule => schedule
                    .StartAgent(Planner)
                    .Cron("0 7 * * 1")
                    .Subjects((services, cancellationToken) => services.GetRequiredService<ScheduledProjects>().ListAsync(cancellationToken))
                    .Message(tick => "Weekly plan of " + tick.Subject.Id + "."))
                .AddWebhookTrigger(Webhook, webhook => webhook
                    .StartAgent(Planner)
                    .Secret(_ => new string('s', 40))
                    .Subject(body => new NhAiSubjectRef("project", body.GetProperty("projectId").GetString()!))
                    .Message(_ => "The planning changed.")));
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

    private static async Task MirrorWebhookAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var trigger = scope.ServiceProvider.GetRequiredService<NhAssistantRegistrationState>().Triggers[Webhook];
        using var document = JsonDocument.Parse($$"""{ "projectId": "{{projectId:N}}" }""");
        await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>().MirrorWebhookAsync(
            trigger,
            new NhAssistantWebhookDelivery("delivery-" + projectId.ToString("N"), document.RootElement.Clone()),
            CancellationToken.None);
    }

    private static async Task<NhAssistantEvaluationDto> CompareAsync(AssistantTestHost host, DateTimeOffset from)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantEvaluationService>()
            .CompareAsync(Planner, Variant, from, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
    }

    private static async Task<NewHeap.Platform.Common.Models.TaskResult<AssistantAgentReplay>> StartReplayAsync(
        AssistantTestHost host,
        string variantId,
        NhAssistantReplayRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantReplayService>()
            .StartAsync(variantId, request, "admin-1", CancellationToken.None);
    }

    private static string? Code(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static async Task RunReplayAsync(AssistantTestHost host, Guid replayId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantReplayOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(1);
        await operation.ExecuteAsync(new NhAssistantReplayOperationRequest(replayId), context, CancellationToken.None);
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid runId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(1);
        await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
    }

    private static async Task<Guid> ThreadOfAsync(AssistantTestHost host, string agentId, Guid projectId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var subjectId = projectId.ToString("N");
        return await context.Threads
            .Where(thread => thread.AgentId == agentId && thread.SubjectId == subjectId)
            .Select(thread => thread.ConversationId)
            .SingleOrDefaultAsync();
    }

    private static async Task<IReadOnlyList<AssistantRun>> RunsOfAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs.Where(run => run.ConversationId == conversationId).ToListAsync();
    }

    private static async Task<IReadOnlyList<AssistantRun>> AllRunsOfAsync(AssistantTestHost host, string agentId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs
            .Join(context.Threads.Where(thread => thread.AgentId == agentId), run => run.ConversationId, thread => thread.ConversationId, (run, _) => run)
            .OrderBy(run => run.CreatedAt)
            .ToListAsync();
    }

    private static async Task<AssistantQueuedInput> FirstInputAsync(AssistantTestHost host, Guid runId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.QueuedInputs.Where(input => input.ConsumedByRunId == runId).OrderBy(input => input.CreatedAt).FirstAsync();
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

    private sealed class EvaluationModels
    {
        public NhAiScriptedChatClient Live { get; } = new();

        public NhAiScriptedChatClient Variant { get; } = new();

        public NhAiScriptedChatClient Replay { get; } = new();
    }
}

/// <summary>
/// The projects a scheduled test trigger visits.
/// </summary>
public sealed class ScheduledProjects
{
    public List<Guid> Ids { get; } = [];

    public async IAsyncEnumerable<NhAiSubjectRef> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var id in Ids.ToArray())
        {
            await Task.Yield();
            yield return new NhAiSubjectRef("project", id.ToString("N"));
        }
    }
}
