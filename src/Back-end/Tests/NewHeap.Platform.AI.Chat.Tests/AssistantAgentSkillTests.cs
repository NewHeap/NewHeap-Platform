using System.Collections.Concurrent;
using NewHeap.Platform.Common.Models;
using System.Security.Claims;
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
public sealed class AssistantAgentSkillTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string FollowUp = "follow-up";
    private const string Risk = "risk";
    private const string Notes = "notes";
    private const string RiskInstructions = "Judge whether the project misses its deadline.";
    private const string Intake = "intake";
    private const string IntakeInstructions = "Decide whether the order belongs to the project.";
    private const string FollowUpInstructions = "Say which task comes next.";

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

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_skill_that_is_off_ignores_its_triggers_and_rules_and_triage_do_not_route_to_it(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Triage.RespondWithText("""{"skill":null,"reason":"Nothing changed that matters."}""");
        await using var host = await CreateHostAsync(provider, models);
        host.Services.GetRequiredService<TestRuleScript>().Run = (_, _) =>
            Task.FromResult(NhAssistantRuleResult.UseSkill(Risk, "Check the deadline."));
        var off = await ControlAsync(host, control => control.SwitchOffSkillAsync(AgentId, FollowUp, "operator-1", "Not yet"));
        await ControlAsync(host, control => control.SwitchOffSkillAsync(AgentId, Risk, "operator-1"));
        await ControlAsync(host, control => control.SwitchOffSkillAsync(AgentId, Notes, "operator-1"));
        var auditsBefore = host.Business.Events.Count;

        // A trigger that names the skill is ignored, without a thread, an input or an audit.
        var ignored = Assert.Single(await PublishAsync(host, "task done"));
        // Rules route nothing to a skill that is off: the run ends and nothing waits for the skill.
        var ruled = Assert.Single(await PublishAsync(host, "rule check"));
        await ExecuteAsync(host, ruled.RunId!.Value);
        // Triage only offers the skills that are on.
        var triaged = Assert.Single(await PublishAsync(host, "triage first"));
        await ExecuteAsync(host, triaged.RunId!.Value);
        // The agent's other skills keep working.
        var intake = Assert.Single(await PublishAsync(host, "intake new"));

        Assert.Equal(NhAssistantAgentRunStates.Off, off.Data!.RunState);
        Assert.Equal((NhAssistantTriggerOutcomes.Ignored, NhAssistantTriggerReasons.SkillOff), (ignored.Outcome, ignored.ReasonCode));
        Assert.Null(ignored.ConversationId);
        var ended = await Runs(host).FindRunAsync(ruled.RunId.Value, CancellationToken.None);
        Assert.Equal((NhAssistantRunStatuses.Cancelled, NhAssistantErrorCodes.SkillOff), (ended!.Status, ended.FailureCode));
        Assert.Empty(models.Skill.Requests);
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            Assert.False(await context.QueuedInputs.AnyAsync(input => input.ConversationId == ruled.ConversationId && input.ConsumedAt == null));
        }
        var catalog = Assert.Single(models.Triage.Requests).Messages[^1].Text;
        Assert.Contains($"- {Intake}: ", catalog);
        Assert.DoesNotContain($"- {Risk}: ", catalog);
        Assert.DoesNotContain($"- {Notes}: ", catalog);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, intake.Outcome);
        Assert.DoesNotContain(host.Business.Events.Skip(auditsBefore), evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerRejected);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSwitchedOff && evt.SkillId == FollowUp);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Rules_of_a_skill_hand_over_to_its_own_model_with_its_tools_instructions_profile_and_ceiling(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Skill
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = Guid.NewGuid(), status = "Active" } }, "call-1")
            .RespondWithText("Not reached: the skill's ceiling ends the run first.");
        await using var host = await CreateHostAsync(provider, models);
        var intake = host.Services.GetRequiredService<TestIntakeRuleScript>();
        intake.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.HandOver("The contractor of the order is unknown."));

        var receipt = Assert.Single(await PublishAsync(host, "intake order"));
        await ExecuteAsync(host, receipt.RunId!.Value);

        // The run stays in the skill: its rules ran, then its own profile, tools and instructions.
        var run = await Runs(host).FindRunAsync(receipt.RunId.Value, CancellationToken.None);
        Assert.Equal(Intake, run!.SkillId);
        Assert.Equal(NhAssistantSkillChoosers.Trigger, run.SkillChosenBy);
        Assert.Equal(NhAssistantRunKinds.RuleThenModel, run.Kind);
        Assert.Single(intake.Contexts);
        Assert.Empty(models.Default.Requests);
        var request = Assert.Single(models.Skill.Requests);
        Assert.Contains(IntakeInstructions, request.Options!.Instructions);
        Assert.Contains("## Skill: intake", request.Options.Instructions);
        var tools = ToolNames(request);
        Assert.Contains("projects_change_status_v1", tools);
        Assert.DoesNotContain("projects_search_v1", tools);
        var briefing = Assert.Single(request.Messages, message => message.Text.Contains("<subject-briefing>", StringComparison.Ordinal)).Text;
        Assert.Contains("handed this run over to you", briefing);
        Assert.Contains("The contractor of the order is unknown.", briefing);
        // The skill's run ceiling applies after the hand-over, and its budget counts the model's tokens.
        Assert.Equal(NhAssistantRunStatuses.Failed, run.Status);
        Assert.Equal(NhAssistantErrorCodes.RunTokenLimit, run.FailureCode);
        var usage = (await ControlAsync(host, control => control.GetAsync(AgentId)))!.Skills.Single(skill => skill.SkillId == Intake);
        Assert.True(usage.TokensToday > 0);

        // A paused skill lets its event wait before its rules run.
        await ControlAsync(host, control => control.PauseSkillAsync(AgentId, Intake, "operator-1", "Import"));
        var waiting = Assert.Single(await PublishAsync(host, "intake later"));
        Assert.Equal(NhAssistantTriggerOutcomes.Waiting, waiting.Outcome);
        Assert.Single(intake.Contexts);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Rules_of_a_skill_without_a_profile_hand_over_to_the_agents_profile_with_the_skills_tools(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Default.RespondWithText("The release notes come next.");
        await using var host = await CreateHostAsync(provider, models);
        host.Services.GetRequiredService<TestSkillRuleScript>().Run = (_, _) =>
            Task.FromResult(NhAssistantRuleResult.HandOver("Two tasks could come next."));

        var receipt = Assert.Single(await PublishAsync(host, "task done"));
        await ExecuteAsync(host, receipt.RunId!.Value);

        var run = await Runs(host).FindRunAsync(receipt.RunId.Value, CancellationToken.None);
        Assert.Equal(NhAssistantRunStatuses.Completed, run!.Status);
        Assert.Equal(FollowUp, run.SkillId);
        Assert.Empty(models.Skill.Requests);
        var request = Assert.Single(models.Default.Requests);
        Assert.Contains(FollowUpInstructions, request.Options!.Instructions);
        var tools = ToolNames(request);
        Assert.Contains("projects_search_v1", tools);
        Assert.DoesNotContain("projects_change_status_v1", tools);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_follow_up_run_continues_in_the_skill_of_the_run_whose_proposal_people_decided(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        await using var host = await CreateHostAsync(provider, models);
        var intake = host.Services.GetRequiredService<TestIntakeRuleScript>();
        var proposals = new ConcurrentQueue<Guid>();
        intake.Run = async (context, cancellationToken) =>
        {
            if (context.Inputs.Any(input => input.TriggerId is NhAssistantTriggerIds.ProposalExecuted or NhAssistantTriggerIds.ProposalDecided))
            {
                return NhAssistantRuleResult.Done("Linked the order to the project.");
            }
            var proposed = await context.ProposeAsync(
                "projects.change-status",
                new ProjectStatusInput(Guid.Parse(context.Subject.Subject.Id), "Active"),
                null,
                cancellationToken);
            proposals.Enqueue(proposed.ApprovalId!.Value);
            return NhAssistantRuleResult.Done("Proposed to approve the order.");
        };

        // An executed proposal follows up in the skill that made it, chosen as a follow-up.
        var first = Assert.Single(await PublishAsync(host, "intake first"));
        await ExecuteAsync(host, first.RunId!.Value);
        Assert.True((await DecideAsync(host, proposals.Last(), approve: true)).Success);
        await ExecuteProposalAsync(host, proposals.Last());
        var executed = await LatestRunAsync(host, first.ConversationId!.Value);
        Assert.Equal(NhAssistantTriggerIds.ProposalExecuted, executed.TriggerId);
        Assert.Equal(Intake, executed.SkillId);
        Assert.Equal(NhAssistantSkillChoosers.FollowUp, executed.SkillChosenBy);
        await ExecuteAsync(host, executed.Id);
        Assert.Equal(NhAssistantRunStatuses.Completed, (await Runs(host).FindRunAsync(executed.Id, CancellationToken.None))!.Status);
        Assert.Contains(intake.Contexts, context => context.Inputs.Any(input => input.TriggerId == NhAssistantTriggerIds.ProposalExecuted));

        // A rejected one as well.
        var second = Assert.Single(await PublishAsync(host, "intake second"));
        await ExecuteAsync(host, second.RunId!.Value);
        Assert.True((await DecideAsync(host, proposals.Last(), approve: false)).Success);
        var rejected = await LatestRunAsync(host, second.ConversationId!.Value);
        Assert.Equal(NhAssistantTriggerIds.ProposalDecided, rejected.TriggerId);
        Assert.Equal(Intake, rejected.SkillId);
        Assert.Equal(NhAssistantSkillChoosers.FollowUp, rejected.SkillChosenBy);

        // A stopped skill refuses the follow-up; the thread gets no new run.
        var third = Assert.Single(await PublishAsync(host, "intake third"));
        await ExecuteAsync(host, third.RunId!.Value);
        await ControlAsync(host, control => control.StopSkillAsync(AgentId, Intake, "operator-1", "Wrong approvals"));
        Assert.True((await DecideAsync(host, proposals.Last(), approve: false)).Success);
        Assert.Equal(third.RunId, (await LatestRunAsync(host, third.ConversationId!.Value)).Id);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerRejected
            && evt.SkillId == Intake
            && evt.ResultCode == NhAssistantErrorCodes.SkillStopped);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_follow_up_of_a_run_without_a_skill_lets_the_agent_route_it_as_before(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        await using var host = await CreateHostAsync(provider, models);
        var approvals = new ConcurrentQueue<Guid>();
        host.Services.GetRequiredService<TestRuleScript>().Run = async (context, cancellationToken) =>
        {
            if (context.Inputs.Any(input => input.TriggerId == NhAssistantTriggerIds.ProposalDecided))
            {
                return NhAssistantRuleResult.Done("Noted the decision.");
            }
            var proposed = await context.ProposeAsync(
                "projects.change-status",
                new ProjectStatusInput(Guid.Parse(context.Subject.Subject.Id), "OnHold"),
                null,
                cancellationToken);
            approvals.Enqueue(proposed.ApprovalId!.Value);
            return NhAssistantRuleResult.Done("Proposed to hold the project.");
        };

        var receipt = Assert.Single(await PublishAsync(host, "rule hold"));
        await ExecuteAsync(host, receipt.RunId!.Value);
        Assert.Null((await Runs(host).FindRunAsync(receipt.RunId.Value, CancellationToken.None))!.SkillId);
        Assert.True((await DecideAsync(host, approvals.Single(), approve: false)).Success);

        var followUp = await LatestRunAsync(host, receipt.ConversationId!.Value);
        Assert.Equal(NhAssistantTriggerIds.ProposalDecided, followUp.TriggerId);
        Assert.Null(followUp.SkillId);
        Assert.Null(followUp.SkillChosenBy);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_follow_up_run_waits_thirty_seconds_on_the_applications_clock(AssistantTestProvider provider)
    {
        // Years away from the system clock, so a debounce taken from the system clock shows.
        var clock = new FixedClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        await using var host = await CreateHostAsync(provider, new SkillModels(), clock);
        var approvals = new ConcurrentQueue<Guid>();
        host.Services.GetRequiredService<TestRuleScript>().Run = async (context, cancellationToken) =>
        {
            if (context.Inputs.Any(input => input.TriggerId == NhAssistantTriggerIds.ProposalDecided))
            {
                return NhAssistantRuleResult.Done("Noted the decision.");
            }
            var proposed = await context.ProposeAsync(
                "projects.change-status",
                new ProjectStatusInput(Guid.Parse(context.Subject.Subject.Id), "OnHold"),
                null,
                cancellationToken);
            approvals.Enqueue(proposed.ApprovalId!.Value);
            return NhAssistantRuleResult.Done("Proposed to hold the project.");
        };

        var receipt = Assert.Single(await PublishAsync(host, "rule hold"));
        await ExecuteAsync(host, receipt.RunId!.Value);
        Assert.True((await DecideAsync(host, approvals.Single(), approve: false)).Success);

        // The follow-up coalesces what people decide within thirty seconds of the application's
        // clock; the background-operation dispatcher reads the same clock.
        var followUp = await LatestRunAsync(host, receipt.ConversationId!.Value);
        Assert.Equal(NhAssistantTriggerIds.ProposalDecided, followUp.TriggerId);
        var enqueued = Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Enqueued, item => item.RunId == followUp.Id);
        Assert.Equal(clock.GetUtcNow().AddSeconds(30), enqueued.Options.NotBefore);
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
        // A skill may work by rules and use its own profile for the model after a hand-over.
        var both = new ServiceCollection();
        both.AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent())
            .ConfigureAgent(AgentId, agent => agent.Skill(Risk, skill => skill
                .Description("Judges risks.")
                .UseProfile("skill-chat")
                .UseRuleHandler<TestRules>())));

        Assert.Contains("orders.*", tools.Message);
        Assert.Contains("missing", trigger.Message);
        var risk = State(both).AgentRuns[AgentId].Skills[Risk];
        Assert.Equal("skill-chat", risk.ProfileName);
        Assert.Equal(typeof(TestRules), risk.RuleHandlerType);
    }

    private static NhAssistantRegistrationState State(IServiceCollection services)
    {
        return (NhAssistantRegistrationState)services.Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState)).ImplementationInstance!;
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, SkillModels models, TimeProvider? clock = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            models.Default,
            configure: services =>
            {
                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<TestSkillRuleScript>();
                services.AddSingleton<TestIntakeRuleScript>();
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
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .StartFollowUpRun())
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>()
                    .Triage(triage => triage.UseProfile("triage-chat"))
                    .Skill(FollowUp, skill => skill
                        .Description("Follows up completed tasks by rule.")
                        .UseRuleHandler<TestSkillRules>()
                        .Instructions(FollowUpInstructions)
                        .Tools("projects.search"))
                    .Skill(Intake, skill => skill
                        .Description("Takes in orders by rule and asks its model when in doubt.")
                        .UseRuleHandler<TestIntakeRules>()
                        .UseProfile("skill-chat")
                        .Instructions(IntakeInstructions)
                        .Tools("projects.change-status")
                        .MaxTokensPerRun(1))
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
                .AddTrigger<ProjectChanged>("intake-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => evt.Change.StartsWith("intake", StringComparison.Ordinal))
                    .Skill(Intake)
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

    private static async Task<TaskResult<AssistantApproval>> DecideAsync(AssistantTestHost host, Guid approvalId, bool approve)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approvalId,
            "approver-1",
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "approver-1")], "test")),
            approve,
            expectedProposalHash: null,
            reason: null,
            CancellationToken.None);
    }

    private static async Task ExecuteProposalAsync(AssistantTestHost host, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var result = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalExecutor>().ExecuteAsync(
            approvalId,
            Substitute.For<INhBackgroundOperationContext>(),
            fenced: false,
            CancellationToken.None);
        Assert.True(result.Success);
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

    /// <summary>
    /// The application's clock, standing still at one moment.
    /// </summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class SkillModels
    {
        public NhAiScriptedChatClient Default { get; } = new();

        public NhAiScriptedChatClient Skill { get; } = new();

        public NhAiScriptedChatClient Triage { get; } = new();
    }
}

public sealed class TestIntakeRuleScript
{
    public Func<NhAssistantRuleContext, CancellationToken, Task<NhAssistantRuleResult>> Run { get; set; } =
        (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Took in the order."));

    public ConcurrentQueue<NhAssistantRuleContext> Contexts { get; } = new();
}

public sealed class TestIntakeRules(TestIntakeRuleScript script) : INhAssistantAgentRuleHandler
{
    public Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        script.Contexts.Enqueue(context);
        return script.Run(context, cancellationToken);
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
