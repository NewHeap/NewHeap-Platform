using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
/// Hand-offs between agents: from rules and from a model's tool, the target's own run and
/// governance, waiting for a paused target, the refusals, chains, limits and the follow-up to the
/// source.
/// </summary>
public sealed class AssistantAgentHandOffTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Planner = "project-planner";
    private const string Closer = "project-closer";
    private const string Auditor = "project-auditor";
    private const string ShadowPlanner = "shadow-planner";
    private const string ChangeStatusTool = "projects.change-status";
    private const string HandOffFunction = "handoff_project-closer_project";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_hands_a_project_to_the_closer_which_proposes_in_its_own_thread_and_the_planner_gets_its_summary(AssistantTestProvider provider)
    {
        var models = new HandOffModels();
        await using var host = await CreateHostAsync(provider, models);
        var planner = host.Services.GetRequiredService<TestRuleScript>();
        var closer = host.Services.GetRequiredService<TestSkillRuleScript>();
        var receipts = new ConcurrentQueue<NhAssistantHandOffReceipt>();
        var results = new ConcurrentQueue<NhAssistantHandOffResult>();
        planner.Run = async (context, cancellationToken) =>
        {
            if (context.TriggerId == NhAssistantTriggerIds.HandOffEnded)
            {
                results.Enqueue(context.Inputs.Single().GetPayload<NhAssistantHandOffResult>()!);
                return NhAssistantRuleResult.Done("The closer answered.");
            }

            receipts.Enqueue(await context.HandOffAsync(Closer, context.Subject.Subject, "All tasks are done.", new HandOffNote(3), cancellationToken));
            return NhAssistantRuleResult.Done("Handed the project to the closer.");
        };
        closer.Run = async (context, cancellationToken) =>
        {
            var projectId = Guid.ParseExact(context.Subject.Subject.Id, "N");
            await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, "Completed"), null, cancellationToken);
            return NhAssistantRuleResult.Done("Proposed to complete the project.");
        };

        var source = Assert.Single(await PublishAsync(host, "plan done", Guid.NewGuid()));
        await ExecuteAsync(host, source.RunId!.Value);
        var handed = Assert.Single(receipts);
        Assert.Equal(NhAssistantHandOffOutcomes.Accepted, handed.Outcome);
        await ExecuteAsync(host, handed.RunId!.Value);

        // The closer works in its own thread, without an initiator, under its own approval policy.
        var target = (await Runs(host).FindRunAsync(handed.RunId.Value, CancellationToken.None))!;
        Assert.Equal(NhAssistantRunStatuses.Completed, target.Status);
        Assert.Equal(NhAssistantTriggerIds.HandOff, target.TriggerId);
        Assert.Null(target.InitiatorActorId);
        Assert.Equal(Planner, target.RequestedByAgentId);
        Assert.Equal(source.RunId, target.RequestedByRunId);
        Assert.Equal(1, target.HandOffDepth);
        Assert.False(target.UsedRestrictedResults);
        Assert.NotEqual(source.ConversationId, target.ConversationId);
        Assert.Equal(Closer, (await Runs(host).FindThreadByConversationAsync(target.ConversationId, CancellationToken.None))!.AgentId);
        var approval = Assert.Single(await Runs(host).GetRunApprovalsAsync(target.Id, CancellationToken.None));
        Assert.Equal("project-review", approval.PolicyId);
        Assert.Equal(target.ConversationId, approval.ConversationId);
        var context = Assert.Single(closer.Contexts);
        Assert.Null(context.InitiatorActorId);
        var input = Assert.Single(context.Inputs);
        Assert.Equal(NhAssistantTriggerIds.HandOff, input.TriggerId);
        Assert.Contains("Agent 'project-planner' handed this project to you", input.Text);
        Assert.Contains("All tasks are done.", input.Text);
        Assert.Equal(new HandOffNote(3), input.GetPayload<HandOffNote>());

        // The planner hears how it ended, with the closer's summary: both work on the same project.
        var followUp = await LatestRunAsync(host, source.ConversationId!.Value);
        Assert.Equal(NhAssistantTriggerIds.HandOffEnded, followUp.TriggerId);
        await ExecuteAsync(host, followUp.Id);
        var result = Assert.Single(results);
        Assert.Equal(Closer, result.AgentId);
        Assert.Equal(target.Id, result.RunId);
        Assert.Equal(NhAssistantRunStatuses.Completed, result.Status);
        Assert.Equal(1, result.ProposalCount);
        Assert.Contains("Proposed to complete the project.", result.Summary);
        Assert.Contains(planner.Contexts, item => item.Inputs.Any(entry => entry.Text.Contains("Its summary is data from another agent", StringComparison.Ordinal)));

        // The audit names source and target, never the message.
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentHandedOff);
        Assert.Equal(Closer, audit.AgentId);
        Assert.Equal(Planner, audit.AskingAgentId);
        Assert.Equal(source.RunId, audit.SourceRunId);
        Assert.Equal(target.Id, audit.RunId);
        Assert.Equal(NhAssistantHandOffOutcomes.Accepted, audit.ResultCode);
        Assert.Equal("project", audit.SubjectType);
        Assert.DoesNotContain(host.Business.Events, evt => evt.ResultCode?.Contains("tasks", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_model_hands_off_through_the_generated_tool_once_per_run_and_subject(AssistantTestProvider provider)
    {
        var models = new HandOffModels();
        models.Default
            .RespondWithFunctionCall(HandOffFunction, new { message = "Check whether the project can close." }, "call-1")
            .RespondWithFunctionCall(HandOffFunction, new { message = "Check it once more." }, "call-2")
            .RespondWithText("I handed the project to the closer.");
        await using var host = await CreateHostAsync(provider, models);

        var source = Assert.Single(await PublishAsync(host, "model check", Guid.NewGuid()));
        await ExecuteAsync(host, source.RunId!.Value);

        var offered = ToolNames(models.Default.Requests[0]);
        Assert.Contains(HandOffFunction, offered);
        // The planner is no source of the auditor, and never of itself.
        Assert.DoesNotContain("handoff_project-auditor_project", offered);
        Assert.DoesNotContain("handoff_project-planner_project", offered);
        var handOffs = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentHandedOff).ToArray();
        Assert.Equal(
            new string?[] { NhAssistantHandOffOutcomes.Accepted, NhAssistantHandOffOutcomes.Deduplicated },
            handOffs.Select(evt => evt.ResultCode).ToArray());
        Assert.Equal(handOffs[0].RunId, handOffs[1].RunId);
        var target = (await Runs(host).FindRunAsync(handOffs[0].RunId!.Value, CancellationToken.None))!;
        await ExecuteAsync(host, target.Id);
        Assert.Single(await RunsOfAsync(host, target.ConversationId));
        var input = Assert.Single(host.Services.GetRequiredService<TestSkillRuleScript>().Contexts).Inputs.Single();
        Assert.Contains("Check whether the project can close.", input.Text);
        // The closer's own turns get no hand-off tool: it is nobody's source here.
        Assert.Empty(models.Closer.Requests);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_hand_off_to_a_paused_target_waits_and_runs_after_it_resumes(AssistantTestProvider provider)
    {
        var models = new HandOffModels();
        await using var host = await CreateHostAsync(provider, models);
        var receipts = HandOffByRule(host, Closer, "Please close it.");
        await ControlAsync(host, control => control.PauseAsync(Closer, "operator-1", "Import running"));

        var source = Assert.Single(await PublishAsync(host, "plan paused", Guid.NewGuid()));
        await ExecuteAsync(host, source.RunId!.Value);
        var waiting = Assert.Single(receipts);
        Assert.Equal(NhAssistantHandOffOutcomes.Waiting, waiting.Outcome);
        Assert.Equal(NhAssistantErrorCodes.AgentPaused, waiting.Code);
        Assert.Null(waiting.RunId);
        var operations = host.Services.GetRequiredService<RecordingOperations>();
        Assert.Single(operations.Enqueued);

        await ControlAsync(host, control => control.ResumeAsync(Closer, "operator-1", null));
        var (runId, _) = operations.Enqueued.Last();
        await ExecuteAsync(host, runId);

        var target = (await Runs(host).FindRunAsync(runId, CancellationToken.None))!;
        Assert.Equal(NhAssistantRunStatuses.Completed, target.Status);
        Assert.Equal(NhAssistantTriggerIds.HandOff, target.TriggerId);
        Assert.Equal(Planner, target.RequestedByAgentId);
        Assert.Equal(source.RunId, target.RequestedByRunId);
        Assert.Equal(NhAssistantTriggerIds.HandOffEnded, (await LatestRunAsync(host, source.ConversationId!.Value)).TriggerId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Hand_offs_the_target_does_not_accept_or_that_leave_the_scope_are_refused_with_their_codes(AssistantTestProvider provider)
    {
        var models = new HandOffModels();
        await using var host = await CreateHostAsync(provider, models);
        var receipts = new ConcurrentQueue<(string Case, NhAssistantHandOffReceipt Receipt)>();
        host.Services.GetRequiredService<TestRuleScript>().Run = async (context, cancellationToken) =>
        {
            async Task HandAsync(string name, string target, NhAiSubjectRef subject) =>
                receipts.Enqueue((name, await context.HandOffAsync(target, subject, "Look at this.", null, cancellationToken)));

            if (context.AgentId == ShadowPlanner)
            {
                await HandAsync("shadow", Closer, context.Subject.Subject);
                return NhAssistantRuleResult.Done("Tried.");
            }

            await HandAsync("not-listed", Auditor, context.Subject.Subject);
            await HandAsync("not-configured", "unknown-agent", context.Subject.Subject);
            await HandAsync("other-scope", Closer, new NhAiSubjectRef("order", "x" + Guid.NewGuid().ToString("N")[1..]));
            await HandAsync("missing", Closer, new NhAiSubjectRef("order", "missing"));
            await HandAsync("same-scope", Closer, new NhAiSubjectRef("order", "o" + Guid.NewGuid().ToString("N")[1..]));
            return NhAssistantRuleResult.Done("Tried.");
        };

        var first = Assert.Single(await PublishAsync(host, "plan refusals", Guid.NewGuid()));
        await ExecuteAsync(host, first.RunId!.Value);
        await ControlAsync(host, control => control.StopAsync(Closer, "operator-1", "Wrong proposals"));
        receipts.Enqueue(("stopped", await HandOffDirectAsync(host, first.RunId.Value, Closer)));
        await ControlAsync(host, control => control.ResumeAsync(Closer, "operator-1", null));
        var shadow = Assert.Single(await PublishAsync(host, "shadow plan", Guid.NewGuid()));
        await ExecuteAsync(host, shadow.RunId!.Value);

        var byCase = receipts.ToDictionary(item => item.Case, item => item.Receipt);
        Assert.Equal(NhAssistantErrorCodes.HandOffNotAccepted, byCase["not-listed"].Code);
        Assert.Equal(NhAssistantErrorCodes.AgentNotConfigured, byCase["not-configured"].Code);
        Assert.Equal(NhAssistantErrorCodes.HandOffSubjectUnavailable, byCase["other-scope"].Code);
        Assert.Equal(NhAssistantErrorCodes.HandOffSubjectUnavailable, byCase["missing"].Code);
        Assert.Equal(NhAssistantHandOffOutcomes.Accepted, byCase["same-scope"].Outcome);
        Assert.Equal(NhAssistantErrorCodes.AgentStopped, byCase["stopped"].Code);
        Assert.Equal(NhAssistantErrorCodes.HandOffShadowToLive, byCase["shadow"].Code);
        Assert.All(byCase.Where(item => item.Key != "same-scope"), item => Assert.Equal(NhAssistantHandOffOutcomes.Refused, item.Value.Outcome));
        Assert.Equal(6, host.Business.Events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AgentHandedOff && evt.RunId is null));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_chain_never_returns_to_an_agent_in_it_and_ends_at_its_depth(AssistantTestProvider provider)
    {
        var models = new HandOffModels();
        await using var host = await CreateHostAsync(provider, models, limits => limits.MaxHandOffDepth = 1);
        var plannerReceipts = HandOffByRule(host, Closer, "Please close it.");
        var closerReceipts = new ConcurrentQueue<(string Target, NhAssistantHandOffReceipt Receipt)>();
        host.Services.GetRequiredService<TestSkillRuleScript>().Run = async (context, cancellationToken) =>
        {
            foreach (var target in new[] { Planner, Auditor })
            {
                closerReceipts.Enqueue((target, await context.HandOffAsync(target, context.Subject.Subject, "Over to you.", null, cancellationToken)));
            }

            return NhAssistantRuleResult.Done("Passed it on.");
        };

        var source = Assert.Single(await PublishAsync(host, "plan chain", Guid.NewGuid()));
        await ExecuteAsync(host, source.RunId!.Value);
        await ExecuteAsync(host, Assert.Single(plannerReceipts).RunId!.Value);

        // Back to the planner: it is in the chain. On to the auditor: one hand-off is the limit here.
        var byTarget = closerReceipts.ToDictionary(item => item.Target, item => item.Receipt);
        Assert.Equal(NhAssistantErrorCodes.HandOffChainLimit, byTarget[Planner].Code);
        Assert.Equal(NhAssistantErrorCodes.HandOffChainLimit, byTarget[Auditor].Code);

        await using var deeper = await CreateHostAsync(provider, new HandOffModels());
        var deeperPlanner = HandOffByRule(deeper, Closer, "Please close it.");
        var deeperCloser = new ConcurrentQueue<(string Target, NhAssistantHandOffReceipt Receipt)>();
        deeper.Services.GetRequiredService<TestSkillRuleScript>().Run = async (context, cancellationToken) =>
        {
            foreach (var target in new[] { Planner, Auditor })
            {
                deeperCloser.Enqueue((target, await context.HandOffAsync(target, context.Subject.Subject, "Over to you.", null, cancellationToken)));
            }

            return NhAssistantRuleResult.Done("Passed it on.");
        };
        var deeperSource = Assert.Single(await PublishAsync(deeper, "plan chain", Guid.NewGuid()));
        await ExecuteAsync(deeper, deeperSource.RunId!.Value);
        await ExecuteAsync(deeper, Assert.Single(deeperPlanner).RunId!.Value);

        // With the default depth of two the auditor takes it; the planner still never gets it back.
        var deeperByTarget = deeperCloser.ToDictionary(item => item.Target, item => item.Receipt);
        Assert.Equal(NhAssistantErrorCodes.HandOffChainLimit, deeperByTarget[Planner].Code);
        Assert.Equal(NhAssistantHandOffOutcomes.Accepted, deeperByTarget[Auditor].Outcome);
        var auditorRun = deeperByTarget[Auditor].RunId!.Value;
        await ExecuteAsync(deeper, auditorRun);
        var audited = (await Runs(deeper).FindRunAsync(auditorRun, CancellationToken.None))!;
        Assert.Equal(Closer, audited.RequestedByAgentId);
        Assert.Equal(2, audited.HandOffDepth);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_daily_limit_per_source_target_and_subject_holds(AssistantTestProvider provider)
    {
        var models = new HandOffModels();
        await using var host = await CreateHostAsync(provider, models);
        var receipts = HandOffByRule(host, Closer, "Please close it.");
        var projectId = Guid.NewGuid();

        for (var index = 0; index < 3; index++)
        {
            var source = Assert.Single(await PublishAsync(host, "plan limit " + index, projectId));
            await ExecuteAsync(host, source.RunId!.Value);
        }

        Assert.Equal(
            [NhAssistantHandOffOutcomes.Accepted, NhAssistantHandOffOutcomes.Accepted, NhAssistantHandOffOutcomes.Refused],
            receipts.Select(receipt => receipt.Outcome).ToArray());
        Assert.Equal(NhAssistantErrorCodes.HandOffLimitReached, receipts.Last().Code);
        var other = Assert.Single(await PublishAsync(host, "plan limit other", Guid.NewGuid()));
        await ExecuteAsync(host, other.RunId!.Value);
        Assert.Equal(NhAssistantHandOffOutcomes.Accepted, receipts.Last().Outcome);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_follow_up_carries_no_summary_for_another_subject_or_after_a_restricted_read(AssistantTestProvider provider)
    {
        var models = new HandOffModels();
        models.Closer
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "open tasks" } }, "call-search")
            .RespondWithText("Two tasks are open, so the project cannot close yet.");
        await using var host = await CreateHostAsync(provider, models);
        var results = new ConcurrentQueue<NhAssistantHandOffResult>();
        var receipts = new ConcurrentQueue<NhAssistantHandOffReceipt>();
        host.Services.GetRequiredService<TestRuleScript>().Run = async (context, cancellationToken) =>
        {
            if (context.TriggerId == NhAssistantTriggerIds.HandOffEnded)
            {
                results.Enqueue(context.Inputs.Single().GetPayload<NhAssistantHandOffResult>()!);
                return NhAssistantRuleResult.Done("Noted.");
            }

            var subject = context.Inputs.Single().Text.StartsWith("plan order", StringComparison.Ordinal)
                ? new NhAiSubjectRef("order", "o" + Guid.NewGuid().ToString("N")[1..])
                : context.Subject.Subject;
            receipts.Enqueue(await context.HandOffAsync(Closer, subject, "Please look.", null, cancellationToken));
            return NhAssistantRuleResult.Done("Handed off.");
        };
        host.Services.GetRequiredService<TestSkillRuleScript>().Run = (context, _) => Task.FromResult(
            context.Subject.Subject.Type == "order"
                ? NhAssistantRuleResult.Done("The order is fine.")
                : NhAssistantRuleResult.HandOver("Look at the open tasks."));

        var order = Assert.Single(await PublishAsync(host, "plan order", Guid.NewGuid()));
        await ExecuteAsync(host, order.RunId!.Value);
        await ExecuteAsync(host, receipts.Last().RunId!.Value);
        await ExecuteAsync(host, (await LatestRunAsync(host, order.ConversationId!.Value)).Id);

        var restricted = Assert.Single(await PublishAsync(host, "plan read", Guid.NewGuid()));
        await ExecuteAsync(host, restricted.RunId!.Value);
        var readRun = receipts.Last().RunId!.Value;
        await ExecuteAsync(host, readRun);
        await ExecuteAsync(host, (await LatestRunAsync(host, restricted.ConversationId!.Value)).Id);

        Assert.True((await Runs(host).FindRunAsync(readRun, CancellationToken.None))!.UsedRestrictedResults);
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(NhAssistantRunStatuses.Completed, result.Status);
            Assert.Null(result.Summary);
        });
        Assert.Equal("order", results.First().SubjectType);
    }

    [Fact]
    public void Hand_offs_from_the_agent_itself_without_sources_or_from_an_unknown_agent_fail_validation()
    {
        var self = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent(Closer))
            .ConfigureAgent(Closer, agent => agent.AcceptHandOffs("project", handOff => handOff.From(Closer)))));
        var none = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent(Closer))
            .ConfigureAgent(Closer, agent => agent.AcceptHandOffs("project", handOff => handOff.Description("Closes projects.")))));
        var unknown = new ServiceCollection();
        unknown.AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent(Closer))
            .ConfigureAgent(Closer, agent => agent.AcceptHandOffs("project", handOff => handOff.From(Planner))));

        var missing = Assert.Throws<InvalidOperationException>(() => State(unknown).ValidateAgentRuns());
        Assert.Contains("itself", self.Message);
        Assert.Contains("From", none.Message);
        Assert.Contains(Planner, missing.Message);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        HandOffModels models,
        Action<NhAssistantLimits>? limits = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            models.Default,
            limits,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<TestSkillRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddKeyedSingleton<IChatClient>("closer-chat-model", models.Closer);
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile("closer-chat", profile => profile
                    .UseKeyedClient("closer-chat-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .PermitDataClassifications(NhAiDataClassification.Internal)
                    .PermitExecutionRegions("local")
                    .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddSubjectType<OrderSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddAgent(AssistantTestData.Agent(Planner) with { ToolSelectors = ["projects.*", "assistant.handoff.*"] })
                .AddAgent(AssistantTestData.Agent(Closer) with { ProfileName = "closer-chat", ToolSelectors = ["projects.*", "assistant.handoff.*"] })
                .AddAgent(AssistantTestData.Agent(Auditor))
                .AddAgent(AssistantTestData.Agent(ShadowPlanner) with { ToolSelectors = ["projects.*", "assistant.handoff.*"] })
                .ConfigureAgent(Planner, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>()
                    .AcceptHandOffs("project", handOff => handOff.From(Closer)))
                .ConfigureAgent(Closer, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestSkillRules>()
                    .AcceptHandOffs("project", handOff => handOff
                        .From(Planner, ShadowPlanner)
                        .Description("Checks whether a project can be completed.")
                        .NotifySource()
                        .Limits(maxPerSubjectPerDay: 2))
                    .AcceptHandOffs("order", handOff => handOff.From(Planner).NotifySource()))
                .ConfigureAgent(Auditor, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>()
                    .AcceptHandOffs("project", handOff => handOff.From(Closer)))
                .ConfigureAgent(ShadowPlanner, agent => agent
                    .Shadow()
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>())
                .AddTrigger<ProjectChanged>("plan-changed", trigger => trigger
                    .StartAgent(Planner)
                    .When(evt => evt.Change.StartsWith("plan", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("model-changed", trigger => trigger
                    .StartAgent(Planner)
                    .When(evt => evt.Change.StartsWith("model", StringComparison.Ordinal))
                    .UseModel()
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("shadow-changed", trigger => trigger
                    .StartAgent(ShadowPlanner)
                    .When(evt => evt.Change.StartsWith("shadow", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue)));
    }

    /// <summary>
    /// Makes the planner's rules hand the run's project to <paramref name="target"/> and returns the
    /// receipts; a follow-up run ends without handing off again.
    /// </summary>
    private static ConcurrentQueue<NhAssistantHandOffReceipt> HandOffByRule(AssistantTestHost host, string target, string message)
    {
        var receipts = new ConcurrentQueue<NhAssistantHandOffReceipt>();
        host.Services.GetRequiredService<TestRuleScript>().Run = async (context, cancellationToken) =>
        {
            if (context.TriggerId == NhAssistantTriggerIds.HandOffEnded || context.AgentId != Planner)
            {
                return NhAssistantRuleResult.Done("Noted.");
            }

            receipts.Enqueue(await context.HandOffAsync(target, context.Subject.Subject, message, null, cancellationToken));
            return NhAssistantRuleResult.Done("Handed off.");
        };
        return receipts;
    }

    /// <summary>
    /// Hands off as the planner's finished run <paramref name="sourceRunId"/>, as its rules would.
    /// </summary>
    private static async Task<NhAssistantHandOffReceipt> HandOffDirectAsync(AssistantTestHost host, Guid sourceRunId, string target)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var run = (await services.GetRequiredService<NhAssistantRunStore>().FindRunAsync(sourceRunId, CancellationToken.None))!;
        var turn = (await services.GetRequiredService<NhAssistantAgentTurnFactory>().CreateAsync(
            run.ConversationId,
            run.Id,
            run.TriggerId,
            null,
            run.AccountableOwnerId,
            CancellationToken.None)).Data!;
        return await services.GetRequiredService<INhAssistantHandOffs>().HandOffAsync(
            new NhAssistantHandOffRequest(turn, null, target, turn.Subject.Subject, "Look at this.", null),
            CancellationToken.None);
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, string change, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, change));
        return receipts.Where(receipt => receipt.Outcome != NhAssistantTriggerOutcomes.Ignored).ToArray();
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid runId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(1);
        await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
    }

    private static async Task<AssistantRun> LatestRunAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs.Where(run => run.ConversationId == conversationId).OrderByDescending(run => run.CreatedAt).FirstAsync();
    }

    private static async Task<IReadOnlyList<AssistantRun>> RunsOfAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs.Where(run => run.ConversationId == conversationId).ToListAsync();
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

    private static NhAssistantRegistrationState State(IServiceCollection services)
    {
        return (NhAssistantRegistrationState)services.Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState)).ImplementationInstance!;
    }

    private static string[] ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }

    private sealed class HandOffModels
    {
        public NhAiScriptedChatClient Default { get; } = new();

        public NhAiScriptedChatClient Closer { get; } = new();
    }
}

public sealed record HandOffNote(int CompletedTasks);

/// <summary>
/// Orders: an id starting with <c>x</c> belongs to another tenant, <c>missing</c> does not exist.
/// </summary>
public sealed class OrderSubjectResolver : INhAiSubjectAccessResolver
{
    public string SubjectType => "order";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        if (subject.Id == "missing")
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(null);
        }

        var tenant = subject.Id.StartsWith('x') ? "other-tenant" : null;
        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(tenant, null))
        {
            DisplayName = "Order " + subject.Id[..Math.Min(8, subject.Id.Length)]
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
