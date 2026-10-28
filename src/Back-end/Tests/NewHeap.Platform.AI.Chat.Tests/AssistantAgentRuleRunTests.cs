using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
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
using NewHeap.Platform.AI.Scripting;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Rule runs and proposal deduplication: an agent's rules propose through the governed flow without
/// a model, hand a run over to the model when needed, and neither rules nor the model pile up the
/// same proposal, on both relational providers.
/// </summary>
public sealed class AssistantAgentRuleRunTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusTool = "projects.change-status";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Approver = "approver-1";

    /// <summary>Agents of the script tool's host, each with the rules of <see cref="TestRules"/>.</summary>
    private const string ProposingAgent = "project-proposer";
    private const string ScriptingAgent = "project-scripter";
    private const string ObservingAgent = "project-observer";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_run_proposes_without_calling_the_model_and_the_proposal_executes_after_approval(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient();
        await using var host = await CreateHostAsync(provider, model);
        var script = Script(host);
        RuleNotice? payload = null;
        script.Run = async (context, cancellationToken) =>
        {
            payload = context.Inputs.Single().GetPayload<RuleNotice>();
            var proposed = await context.ProposeAsync(
                ChangeStatusTool,
                new ProjectStatusInput(projectId, "Active"),
                culture => culture.TwoLetterISOLanguageName == "nl" ? "De intake is compleet." : "The intake is complete.",
                cancellationToken);
            script.Proposals.Enqueue(proposed);
            return NhAssistantRuleResult.Done("Proposed to activate the project.");
        };
        var run = await StartRunAsync(host, projectId);

        var result = await RunTurnAsync(host, run, "Project PRJ-1 was created.", JsonSerializer.Serialize(new RuleNotice("intake-complete")));

        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        Assert.Equal(NhAssistantRunKinds.Rule, result.Kind);
        // The answer per supported culture, for the run's result: the summary in one language with the
        // rule's reason in each.
        Assert.NotNull(result.LocalizedAnswer);
        Assert.Equal("Proposed to activate the project.\n\n- De intake is compleet.", result.LocalizedAnswer!["nl"]);
        Assert.Equal("Proposed to activate the project.\n\n- The intake is complete.", result.LocalizedAnswer["en"]);
        Assert.Equal(0, result.InputTokens + result.OutputTokens);
        Assert.Equal(1, result.ProposalCount);
        Assert.Equal(1, result.ToolCalls);
        Assert.Empty(model.Requests);
        Assert.Equal(new RuleNotice("intake-complete"), payload);
        var proposed = Assert.Single(script.Proposals);
        Assert.Equal(NhAssistantProposeOutcome.Created, proposed.Outcome);
        Assert.Null(proposed.NotNeededKind);
        Assert.Empty(proposed.Explanations);
        var context = Assert.Single(script.Contexts);
        Assert.Equal(run.Thread.SubjectId, context.Subject.Subject.Id);
        Assert.Equal("project-created", context.TriggerId);
        // The agent's own rules run outside a skill.
        Assert.Equal((null, null, null), (context.SkillId, context.SkillVersion, context.SkillCandidate));

        // The proposal waits deferred like a model's, with the rule's reason in every culture.
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal(proposed.ApprovalId, approval.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, approval.Status);
        Assert.Equal(NhAssistantApprovalModes.Deferred, approval.Mode);
        Assert.NotNull(approval.ArgumentsHash);
        Assert.Equal("De intake is compleet.", NhAssistantStaleExplanations.Pick(approval.RationalesJson, CultureInfo.GetCultureInfo("nl-NL")));
        Assert.Equal("The intake is complete.", NhAssistantStaleExplanations.Pick(approval.RationalesJson, CultureInfo.GetCultureInfo("en-US")));

        // The thread shows the rule's message with the reason, written by the agent's rules.
        var message = (await host.Services.GetRequiredService<INhAssistantStore>().GetMessagesAsync(run.Thread.ConversationId, 20, CancellationToken.None))
            .Single(item => item.Role == NhAssistantMessageRoles.Assistant);
        Assert.Equal("rule:" + AgentId, message.AuthorActorId);
        var text = NhAssistantContent.TextOf(NhAssistantContent.DeserializeParts(message.PartsJson));
        Assert.Contains("Proposed to activate the project.", text);
        Assert.Contains("intake is compleet", text, StringComparison.OrdinalIgnoreCase);
        var culture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var conversation = (await scope.ServiceProvider.GetRequiredService<INhAssistantStore>()
                .FindShareableConversationAsync(run.Thread.ConversationId, CancellationToken.None))!;
            var view = await scope.ServiceProvider.GetRequiredService<NhAssistantConversationReader>()
                .CreateViewAsync(conversation, CancellationToken.None);
            var ruleMessage = view.Messages.Single(item => item.Id == message.Id);
            Assert.Equal(NhAssistantMessageAuthorKinds.Rule, ruleMessage.AuthorKind);
            Assert.Equal("The intake is complete.", ruleMessage.Parts.OfType<NhAssistantApprovalView>().Single().Rationale);
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
        }

        // People decide it as any proposal, and it executes after approval.
        Assert.True((await DecideAsync(host, approval.Id, approve: true)).Success);
        await ExecuteAsync(host, approval.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Executed, (await FindAsync(host, approval.Id)).Status);
        Assert.Equal(new ProjectStatusInput(projectId, "Active"), Assert.Single(host.Tools.StatusChanges));
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_in_the_thread_a_trigger_for_the_model_and_a_hand_over_reach_the_model(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Because the intake is complete.")
            .RespondWithText("The trigger asked for the model.")
            .RespondWithText("I looked at the unclear case.");
        await using var host = await CreateHostAsync(provider, model);
        var script = Script(host);

        // A person's turn in the thread always uses the agent's model.
        var personal = await StartRunAsync(host, projectId, isRequestTurn: true);
        var answered = await RunTurnAsync(host, personal, "Why do you propose this?");
        Assert.Equal(NhAssistantRunKinds.Model, answered.Kind);
        Assert.Single(model.Requests);

        // A trigger that asks for the model skips the rules.
        var modelTrigger = await StartRunAsync(host, projectId, triggerId: ModelTriggerId);
        var modelRun = await RunTurnAsync(host, modelTrigger, "Look at the project.");
        Assert.Equal(NhAssistantRunKinds.Model, modelRun.Kind);
        Assert.Equal(2, model.Requests.Count);
        Assert.Empty(script.Contexts);

        // Rules hand an unclear case over; the model continues the same run and reads why.
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, "OnHold"), null, cancellationToken));
            return NhAssistantRuleResult.HandOver("The project failed its intake three times.");
        };
        var handedOver = await RunTurnAsync(host, await StartRunAsync(host, projectId), "Project changed.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, handedOver.Outcome);
        Assert.Equal(NhAssistantRunKinds.RuleThenModel, handedOver.Kind);
        Assert.Equal(1, handedOver.ProposalCount);
        Assert.Equal(3, model.Requests.Count);
        Assert.Contains(
            model.Requests[^1].Messages,
            message => message.Role == ChatRole.System && message.Text.Contains("failed its intake three times", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_same_proposal_is_recorded_once_for_rules_and_for_the_model(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-1")
            .RespondWithText("A proposal waits.");
        await using var host = await CreateHostAsync(provider, model);
        var script = Script(host);
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, "Active"), null, cancellationToken));
            return NhAssistantRuleResult.Done("Checked the project.");
        };

        var first = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, first, "Daily round.");
        var second = await StartRunAsync(host, projectId);
        var secondResult = await RunTurnAsync(host, second, "Daily round.");

        var outcomes = script.Proposals.ToArray();
        Assert.Equal(NhAssistantProposeOutcome.Created, outcomes[0].Outcome);
        Assert.Equal(NhAssistantProposeOutcome.AlreadyOpen, outcomes[1].Outcome);
        Assert.Equal(outcomes[0].ApprovalId, outcomes[1].ApprovalId);
        Assert.Equal(0, secondResult.ProposalCount);
        Assert.Empty(await RunStore(host).GetRunApprovalsAsync(second.Run.Id, CancellationToken.None));
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalDeduplicated);
        Assert.Equal(outcomes[0].ApprovalId, audit.ApprovalId);
        Assert.Equal(NhAssistantErrorCodes.ProposalAlreadyOpen, audit.ResultCode);

        // A model that proposes the same change is told it already waits.
        var modelRun = await StartRunAsync(host, projectId, triggerId: ModelTriggerId);
        var modelResult = await RunTurnAsync(host, modelRun, "Look at the project.");
        Assert.Equal(0, modelResult.ProposalCount);
        Assert.Contains(NhAssistantErrorCodes.ProposalAlreadyOpen, JsonSerializer.Serialize(Assert.Single(model.FunctionResults).Result));
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Equal(1, await context.Approvals.CountAsync(item => item.ConversationId == first.Thread.ConversationId));
        var call = await context.ToolInvocations.SingleAsync(item => item.TurnId == modelResult.TurnId);
        Assert.Equal(NhAssistantToolCallStatuses.NotExecuted, call.Status);
        Assert.Equal(NhAssistantErrorCodes.ProposalAlreadyOpen, call.ResultCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_guard_that_checks_at_proposal_time_keeps_work_already_done_from_being_proposed(AssistantTestProvider provider)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        try
        {
            var projectId = Guid.NewGuid();
            var model = new NhAiScriptedChatClient()
                .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-1")
                .RespondWithText("The project is already active.");
            await using var host = await CreateHostAsync(
                provider,
                model,
                guardStages: NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal);
            host.Services.GetRequiredService<TestProjectStates>().Statuses[projectId] = "Active";
            var script = Script(host);
            script.Run = async (context, cancellationToken) =>
            {
                script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, "Active"), null, cancellationToken));
                return NhAssistantRuleResult.Done("Checked the project.");
            };

            // The rules learn that the work is done, with the guard's reason, kind and explanation.
            var ruleRun = await StartRunAsync(host, projectId);
            var ruleResult = await RunTurnAsync(host, ruleRun, "Daily round.");
            var proposed = Assert.Single(script.Proposals);
            Assert.Equal(
                (NhAssistantProposeOutcome.NotNeeded, (Guid?)null, ProjectStatusGuard.ReasonCode, (NhAiStaleKind?)NhAiStaleKind.AlreadyDone, "Already set to Active by Henk."),
                (proposed.Outcome, proposed.ApprovalId, proposed.Code, proposed.NotNeededKind, proposed.Explanation));
            Assert.Equal(0, ruleResult.ProposalCount);
            // The explanation in every supported UI culture, the run's culture first, for a result in
            // each reader's language.
            Assert.Equal(["en", "nl"], proposed.Explanations.Keys);
            Assert.Equal("Already set to Active by Henk.", proposed.Explanations["en"]);
            Assert.Equal("Al op Active gezet door Henk.", proposed.Explanations["NL"]);

            // The model reads the same as its tool result and continues.
            var modelRun = await StartRunAsync(host, projectId, triggerId: ModelTriggerId);
            var modelResult = await RunTurnAsync(host, modelRun, "Look at the project.");
            Assert.Equal(NhAssistantTurnStatuses.Completed, modelResult.Outcome);
            Assert.Equal(0, modelResult.ProposalCount);
            var toolResult = JsonSerializer.Serialize(Assert.Single(model.FunctionResults).Result);
            Assert.Contains(NhAssistantErrorCodes.ProposalNotNeeded, toolResult);
            Assert.Contains("Already set to Active by Henk.", toolResult);

            // Nothing waits for people and no proposal is audited.
            await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
            Assert.False(await context.Approvals.AnyAsync(item => item.ConversationId == ruleRun.Thread.ConversationId));
            Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRequested);
            var call = await context.ToolInvocations.SingleAsync(item => item.TurnId == modelResult.TurnId);
            Assert.Equal((NhAssistantToolCallStatuses.NotExecuted, NhAssistantErrorCodes.ProposalNotNeeded), (call.Status, call.ResultCode));
            Assert.Empty(host.Tools.StatusChanges);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_guard_without_the_proposal_stage_does_not_check_when_rules_propose(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient());
        host.Services.GetRequiredService<TestProjectStates>().Statuses[projectId] = "Active";
        var script = Script(host);
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, "Active"), null, cancellationToken));
            return NhAssistantRuleResult.Done("Checked the project.");
        };

        await RunTurnAsync(host, await StartRunAsync(host, projectId), "Daily round.");

        // The guard knows the work is done, but checks only at revalidation, decision and execution.
        Assert.Equal(NhAssistantProposeOutcome.Created, Assert.Single(script.Proposals).Outcome);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_newer_proposal_for_the_same_objects_replaces_the_pending_one_but_never_an_approved_one(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient());
        var script = Script(host);
        var status = "Active";
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, status), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed a status.");
        };

        await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 1.");
        status = "OnHold";
        await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 2.");

        var outcomes = script.Proposals.ToArray();
        Assert.Equal(NhAssistantProposeOutcome.Created, outcomes[1].Outcome);
        Assert.Equal(outcomes[0].ApprovalId, Assert.Single(outcomes[1].Superseded));
        var replaced = await FindAsync(host, outcomes[0].ApprovalId!.Value);
        Assert.Equal(NhAssistantApprovalStatuses.Withdrawn, replaced.Status);
        Assert.Equal(NhAssistantErrorCodes.ProposalSuperseded, replaced.ExecutionResultCode);
        Assert.Equal(outcomes[1].ApprovalId, replaced.SupersededByApprovalId);
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalSuperseded);
        Assert.Equal(replaced.Id, audit.ApprovalId);
        Assert.Equal(outcomes[1].ApprovalId!.Value.ToString("D"), audit.ObjectId);

        // Clients see the objects and the successor.
        var thread = (await RunStore(host).FindThreadByConversationAsync(replaced.ConversationId, CancellationToken.None))!;
        var dto = Assert.Single(await NhAssistantAgentRunDtoMapper.CompleteAsync(
            [NhAssistantAgentRunDtoMapper.ToDto(replaced, thread)],
            RunStore(host),
            host.Services.GetRequiredService<NhAssistantSubjectAccessService>(),
            CancellationToken.None));
        Assert.Equal(outcomes[1].ApprovalId, dto.SupersededByApprovalId);
        Assert.Equal(new NhAssistantProposalObjectDto(ProjectStatusGuard.KeyType, projectId.ToString()), Assert.Single(dto.Objects));

        // An approved proposal for the same objects is never replaced.
        Assert.True((await DecideAsync(host, outcomes[1].ApprovalId!.Value, approve: true)).Success);
        status = "Closed";
        await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 3.");
        var third = script.Proposals.ToArray()[2];
        Assert.Equal(NhAssistantProposeOutcome.AlreadyOpen, third.Outcome);
        Assert.Equal(outcomes[1].ApprovalId, third.ApprovalId);
        Assert.Equal(NhAssistantApprovalStatuses.Approved, (await FindAsync(host, outcomes[1].ApprovalId!.Value)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer, NhAiDuplicateProposals.Skip)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiDuplicateProposals.Allow)]
    public async Task A_policy_skips_or_allows_a_newer_proposal_for_the_same_objects(
        AssistantTestProvider provider,
        NhAiDuplicateProposals duplicates)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), policy => policy.Duplicates(duplicates));
        var script = Script(host);
        var status = "Active";
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, status), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed a status.");
        };

        await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 1.");
        status = "OnHold";
        await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 2.");

        var outcomes = script.Proposals.ToArray();
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var pending = await context.Approvals.CountAsync(item => item.Status == NhAssistantApprovalStatuses.Pending);
        if (duplicates == NhAiDuplicateProposals.Skip)
        {
            Assert.Equal(NhAssistantProposeOutcome.AlreadyOpen, outcomes[1].Outcome);
            Assert.Equal(outcomes[0].ApprovalId, outcomes[1].ApprovalId);
            Assert.Equal(1, pending);
        }
        else
        {
            Assert.Equal(NhAssistantProposeOutcome.Created, outcomes[1].Outcome);
            Assert.Empty(outcomes[1].Superseded);
            Assert.Equal(2, pending);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rejected_proposal_is_not_proposed_again_within_the_cooldown(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient());
        var script = Script(host);
        var status = "Active";
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, status), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed a status.");
        };

        await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 1.");
        var first = script.Proposals.Single();
        Assert.True((await DecideAsync(host, first.ApprovalId!.Value, approve: false)).Success);

        await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 2.");
        // A newer value for the same project was rejected as well, through the object.
        status = "OnHold";
        var secondRun = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, secondRun, "Round 3.");

        var outcomes = script.Proposals.ToArray();
        Assert.Equal(NhAssistantProposeOutcome.RecentlyRejected, outcomes[1].Outcome);
        Assert.Equal(first.ApprovalId, outcomes[1].ApprovalId);
        Assert.NotNull(outcomes[1].RejectedAt);
        Assert.Equal(NhAssistantProposeOutcome.RecentlyRejected, outcomes[2].Outcome);
        var context = script.Contexts.ToArray()[2];
        Assert.Equal(first.ApprovalId, Assert.Single(context.RecentRejections).ApprovalId);

        // Without a cooldown the rule may propose again at once.
        await using var withoutCooldown = await CreateHostAsync(provider, new NhAiScriptedChatClient(), policy => policy.RejectionCooldown(TimeSpan.Zero));
        var again = Script(withoutCooldown);
        again.Run = script.Run;
        script.Proposals.Clear();
        status = "Active";
        await RunTurnAsync(withoutCooldown, await StartRunAsync(withoutCooldown, projectId), "Round 1.");
        Assert.True((await DecideAsync(withoutCooldown, script.Proposals.Single().ApprovalId!.Value, approve: false)).Success);
        await RunTurnAsync(withoutCooldown, await StartRunAsync(withoutCooldown, projectId), "Round 2.");
        Assert.Equal(NhAssistantProposeOutcome.Created, script.Proposals.ToArray()[1].Outcome);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_hand_over_tells_the_model_what_became_of_the_rules_proposals_and_which_rejections_they_saw(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient().RespondWithText("I looked at the unclear case.");
        await using var host = await CreateHostAsync(provider, model);
        var script = Script(host);
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, "Active"), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed a status.");
        };
        await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 1.");
        var rejected = script.Proposals.Single();
        Assert.True((await DecideAsync(host, rejected.ApprovalId!.Value, approve: false)).Success);

        // The rules propose the rejected change again and another one, then hand the case over.
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, "Active"), null, cancellationToken));
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(otherProjectId, "OnHold"), null, cancellationToken));
            return NhAssistantRuleResult.HandOver("The project changed twice in a day.");
        };
        var handedOver = await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 2.");

        Assert.Equal(NhAssistantRunKinds.RuleThenModel, handedOver.Kind);
        var outcomes = script.Proposals.ToArray();
        Assert.Equal(NhAssistantProposeOutcome.RecentlyRejected, outcomes[1].Outcome);
        Assert.Equal(NhAssistantProposeOutcome.Created, outcomes[2].Outcome);
        var briefing = Assert.Single(
            Assert.Single(model.Requests).Messages,
            message => message.Role == ChatRole.System && message.Text.Contains("<subject-briefing>", StringComparison.Ordinal)).Text;
        var decidedAt = (await FindAsync(host, rejected.ApprovalId!.Value)).DecidedAt!.Value;
        var day = decidedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Contains("They already recorded 1 proposal(s) in this run; do not propose those again.", briefing);
        Assert.Contains($"- {ChangeStatusTool} on this subject: not recorded; people rejected the same proposal recently ({day}).", briefing);
        Assert.Contains($"- {ChangeStatusTool} on this subject: recorded; it waits for approval.", briefing);
        Assert.Contains($"- {day} {ChangeStatusTool}: rejected by people", briefing);
        // The rules' reason and work come first, before what the application's briefing supplies.
        Assert.True(
            briefing.IndexOf("changed twice in a day", StringComparison.Ordinal)
            < briefing.IndexOf("rejected by people", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Rules_propose_only_changes_with_the_agents_tools_and_stop_with_the_agent(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient());
        var script = Script(host);
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync("projects.search", new { query = "roadmap", limit = 5 }, null, cancellationToken));
            script.Proposals.Enqueue(await context.ProposeAsync("billing.pay", new { amount = 10 }, null, cancellationToken));
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new { projectId = "not-a-guid", status = "Active" }, null, cancellationToken));
            return NhAssistantRuleResult.Done("Tried three proposals.");
        };

        var result = await RunTurnAsync(host, await StartRunAsync(host, projectId), "Round 1.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        Assert.All(script.Proposals, proposal => Assert.Equal(NhAssistantProposeOutcome.Refused, proposal.Outcome));
        Assert.Equal(NhAssistantErrorCodes.ProposalRefused, script.Proposals.ToArray()[0].Code);
        Assert.Equal(0, result.ProposalCount);
        Assert.Empty(host.Tools.StatusChanges);

        // The kill switch stops rule runs as it stops model runs.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>()
                .StopAsync(AgentId, "admin-1")).Success);
        }
        script.Contexts.Clear();
        var stopped = await RunTurnAsync(host, await StartRunAsync(host, projectId, agentEnabled: false), "Round 2.");
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, stopped.ErrorCode);
        Assert.Empty(script.Contexts);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Rules_propose_from_propose_autonomy_on_whether_the_turn_withholds_or_offers_the_script_tool(AssistantTestProvider provider)
    {
        // Every effect of a rule turn is a proposal, so the rules of a Propose agent propose with its
        // tools that change state. The host registers the script tool, which needs no selector: the
        // turn withholds it from an agent that is not allowed scripts, and offers it, next to read
        // tools, to one that is. Neither may cost the rules their tools. An Observe agent only reads,
        // also by rules.
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), scriptTool: true);
        var switchState = host.Services.GetRequiredService<TestCompositionSwitchState>();
        var script = Script(host);
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(
                ChangeStatusTool,
                new ProjectStatusInput(Guid.Parse(context.Subject.Subject.Id), "Active"),
                null,
                cancellationToken));
            return NhAssistantRuleResult.Done("Proposed to activate the project.");
        };

        var proposed = new Dictionary<string, NhAssistantRuleProposal>(StringComparer.Ordinal);
        var runs = new Dictionary<string, StartedRun>(StringComparer.Ordinal);
        foreach (var agentId in new[] { ProposingAgent, ScriptingAgent, ObservingAgent })
        {
            script.Proposals.Clear();
            var run = await StartRunAsync(host, Guid.NewGuid(), agentId: agentId);
            var result = await RunTurnAsync(host, run, "Project PRJ-1 was created.");

            Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
            Assert.Equal(NhAssistantRunKinds.Rule, result.Kind);
            proposed[agentId] = Assert.Single(script.Proposals);
            runs[agentId] = run;
        }

        // Withheld: code does not allow the agent scripts, so the switch is not even asked.
        Assert.Equal(NhAssistantProposeOutcome.Created, proposed[ProposingAgent].Outcome);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, Assert.Single(await RunStore(host).GetRunApprovalsAsync(runs[ProposingAgent].Run.Id, CancellationToken.None)).Status);
        Assert.DoesNotContain(switchState.Asked, asked => asked.AgentId == ProposingAgent);

        // Offered: the switch has scripts on for the run, and the rules still propose with the status tool.
        Assert.Equal(NhAssistantProposeOutcome.Created, proposed[ScriptingAgent].Outcome);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, Assert.Single(await RunStore(host).GetRunApprovalsAsync(runs[ScriptingAgent].Run.Id, CancellationToken.None)).Status);
        Assert.Contains(
            switchState.Asked,
            asked => (asked.AgentId, asked.Feature, asked.Channel) == (ScriptingAgent, NhAiCompositionFeature.Scripts, NhAssistantCompositionChannel.Run));

        // An Observe agent's rules are offered no tool that changes state, so nothing waits for people.
        Assert.Equal(NhAssistantProposeOutcome.Refused, proposed[ObservingAgent].Outcome);
        Assert.Equal(NhAssistantErrorCodes.ProposalRefused, proposed[ObservingAgent].Code);
        Assert.Empty(await RunStore(host).GetRunApprovalsAsync(runs[ObservingAgent].Run.Id, CancellationToken.None));
        Assert.Empty(host.Tools.StatusChanges);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_shadow_rule_run_records_a_shadow_proposal_that_notifies_nobody(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), shadow: true);
        var script = Script(host);
        script.Run = async (context, cancellationToken) =>
        {
            Assert.True(context.Shadow);
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, "Active"), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed in shadow.");
        };

        // The triggers of an agent in shadow start its runs in the shadow thread of the subject.
        var run = await StartRunAsync(host, projectId, shadow: true);
        await RunTurnAsync(host, run, "Round 1.");

        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, approval.Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_shadow_proposal_counts_as_open_within_its_lifetime_as_a_live_one_would(AssistantTestProvider provider)
    {
        // LS9: a shadow proposal never waits, so without this every shadow run proposed an unchanged
        // finding again; the evaluation then compared more than the live agent would propose.
        var projectId = Guid.NewGuid();
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 6, 2, 9, 0, 0, TimeSpan.Zero));
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), shadow: true, clock: clock);
        var script = Script(host);
        var status = "Active";
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, status), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed in shadow.");
        };

        var first = await StartRunAsync(host, projectId, shadow: true);
        await RunTurnAsync(host, first, "Round 1.");
        var second = await StartRunAsync(host, projectId, shadow: true);
        var secondResult = await RunTurnAsync(host, second, "Round 2.");
        // Another value is another finding, which replaces the first for the same project (FB3).
        status = "OnHold";
        await RunTurnAsync(host, await StartRunAsync(host, projectId, shadow: true), "Round 3.");
        // Once the first proposal's lifetime (three days) ended, the unchanged finding is proposed again.
        status = "Active";
        clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        var fourth = await StartRunAsync(host, projectId, shadow: true);
        await RunTurnAsync(host, fourth, "Round 4.");

        var outcomes = script.Proposals.ToArray();
        Assert.Equal(NhAssistantProposeOutcome.Created, outcomes[0].Outcome);
        Assert.Equal((NhAssistantProposeOutcome.AlreadyOpen, outcomes[0].ApprovalId), (outcomes[1].Outcome, outcomes[1].ApprovalId));
        Assert.Equal(0, secondResult.ProposalCount);
        Assert.Empty(await RunStore(host).GetRunApprovalsAsync(second.Run.Id, CancellationToken.None));
        Assert.Equal(NhAssistantProposeOutcome.Created, outcomes[2].Outcome);
        Assert.Equal(outcomes[0].ApprovalId, Assert.Single(outcomes[2].Superseded));
        Assert.Equal(NhAssistantProposeOutcome.Created, outcomes[3].Outcome);
        Assert.Empty(outcomes[3].Superseded);
        Assert.NotEqual(outcomes[0].ApprovalId, outcomes[3].ApprovalId);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, Assert.Single(await RunStore(host).GetRunApprovalsAsync(fourth.Run.Id, CancellationToken.None)).Status);
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalDeduplicated);
        Assert.Equal((outcomes[0].ApprovalId, NhAssistantErrorCodes.ProposalAlreadyOpen), (audit.ApprovalId, audit.ResultCode));
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Equal(3, await context.Approvals.CountAsync(item => item.ConversationId == first.Thread.ConversationId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_newer_shadow_proposal_for_the_same_objects_replaces_the_older_one_within_its_lifetime(AssistantTestProvider provider)
    {
        // FB3: shadow proposals name their objects as live ones do, so the policy's Supersede keeps one
        // shadow proposal per object, as live would, instead of piling one up per changed argument.
        var projectId = Guid.NewGuid();
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 6, 2, 9, 0, 0, TimeSpan.Zero));
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), shadow: true, clock: clock);
        var script = Script(host);
        var status = "Active";
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, status), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed in shadow.");
        };

        await RunTurnAsync(host, await StartRunAsync(host, projectId, shadow: true), "Round 1.");
        status = "OnHold";
        var second = await StartRunAsync(host, projectId, shadow: true);
        var secondResult = await RunTurnAsync(host, second, "Round 2.");

        var outcomes = script.Proposals.ToArray();
        Assert.Equal((NhAssistantProposeOutcome.Created, NhAssistantProposeOutcome.Created), (outcomes[0].Outcome, outcomes[1].Outcome));
        Assert.Equal(outcomes[0].ApprovalId, Assert.Single(outcomes[1].Superseded));
        Assert.Equal(1, secondResult.ProposalCount);
        var replaced = await FindAsync(host, outcomes[0].ApprovalId!.Value);
        Assert.Equal(
            (NhAssistantApprovalStatuses.ShadowSuperseded, (string?)NhAssistantErrorCodes.ProposalSuperseded, outcomes[1].ApprovalId, (DateTimeOffset?)null, (string?)null),
            (replaced.Status, replaced.ExecutionResultCode, replaced.SupersededByApprovalId, replaced.DecidedAt, replaced.DecidedByActorId));
        var newer = await FindAsync(host, outcomes[1].ApprovalId!.Value);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, newer.Status);
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalSuperseded);
        Assert.Equal((replaced.Id, outcomes[1].ApprovalId!.Value.ToString("D")), (audit.ApprovalId, audit.ObjectId));

        // Both name the project they change, as a live proposal does.
        var keys = await RunStore(host).GetApprovalKeysAsync([replaced.Id, newer.Id], CancellationToken.None);
        var key = new NhAiProposalKey(ProjectStatusGuard.KeyType, projectId.ToString());
        Assert.Equal(key, Assert.Single(keys[replaced.Id]));
        Assert.Equal(key, Assert.Single(keys[newer.Id]));

        // The identity rule stays first: the same finding again is already open.
        await RunTurnAsync(host, await StartRunAsync(host, projectId, shadow: true), "Round 3.");
        var third = script.Proposals.ToArray()[2];
        Assert.Equal((NhAssistantProposeOutcome.AlreadyOpen, newer.Id), (third.Outcome, third.ApprovalId));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalDeduplicated && evt.ApprovalId == newer.Id);

        // Outside the newer one's lifetime (three days) another finding is recorded next to it.
        status = "Closed";
        clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        await RunTurnAsync(host, await StartRunAsync(host, projectId, shadow: true), "Round 4.");
        var fourth = script.Proposals.ToArray()[3];
        Assert.Equal(NhAssistantProposeOutcome.Created, fourth.Outcome);
        Assert.Empty(fourth.Superseded);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, (await FindAsync(host, newer.Id)).Status);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, (await FindAsync(host, fourth.ApprovalId!.Value)).Status);
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalSuperseded);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer, NhAiDuplicateProposals.Skip)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiDuplicateProposals.Skip)]
    [InlineData(AssistantTestProvider.SqlServer, NhAiDuplicateProposals.Allow)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiDuplicateProposals.Allow)]
    public async Task A_policy_skips_or_allows_a_newer_shadow_proposal_for_the_same_objects(
        AssistantTestProvider provider,
        NhAiDuplicateProposals duplicates)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), policy => policy.Duplicates(duplicates), shadow: true);
        var script = Script(host);
        var status = "Active";
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, status), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed in shadow.");
        };

        var first = await StartRunAsync(host, projectId, shadow: true);
        await RunTurnAsync(host, first, "Round 1.");
        status = "OnHold";
        var second = await StartRunAsync(host, projectId, shadow: true);
        var secondResult = await RunTurnAsync(host, second, "Round 2.");

        var outcomes = script.Proposals.ToArray();
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var statuses = await context.Approvals
            .Where(item => item.ConversationId == first.Thread.ConversationId)
            .Select(item => item.Status)
            .ToListAsync();
        if (duplicates == NhAiDuplicateProposals.Skip)
        {
            // The older one stays; the newer one is reported as already open and recorded nowhere.
            Assert.Equal((NhAssistantProposeOutcome.AlreadyOpen, outcomes[0].ApprovalId), (outcomes[1].Outcome, outcomes[1].ApprovalId));
            Assert.Equal(0, secondResult.ProposalCount);
            Assert.Empty(await RunStore(host).GetRunApprovalsAsync(second.Run.Id, CancellationToken.None));
            Assert.Equal([NhAssistantApprovalStatuses.Shadow], statuses);
            var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalDeduplicated);
            Assert.Equal((outcomes[0].ApprovalId, NhAssistantErrorCodes.ProposalAlreadyOpen), (audit.ApprovalId, audit.ResultCode));
        }
        else
        {
            Assert.Equal(NhAssistantProposeOutcome.Created, outcomes[1].Outcome);
            Assert.Empty(outcomes[1].Superseded);
            Assert.Equal([NhAssistantApprovalStatuses.Shadow, NhAssistantApprovalStatuses.Shadow], statuses);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Shadow_proposals_with_object_keys_stay_out_of_revalidation_and_counts(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), shadow: true);
        var script = Script(host);
        var status = "Active";
        script.Run = async (context, cancellationToken) =>
        {
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, status), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed in shadow.");
        };
        await RunTurnAsync(host, await StartRunAsync(host, projectId, shadow: true), "Round 1.");
        status = "OnHold";
        await RunTurnAsync(host, await StartRunAsync(host, projectId, shadow: true), "Round 2.");
        var outcomes = script.Proposals.ToArray();
        Assert.Equal(outcomes[0].ApprovalId, Assert.Single(outcomes[1].Superseded));

        // The project got the status of the newer one: a live proposal for it would close as stale,
        // but neither shadow proposal, the replaced one included, is revalidated.
        host.Services.GetRequiredService<TestProjectStates>().Statuses[projectId] = "OnHold";
        var key = new NhAiProposalKey(ProjectStatusGuard.KeyType, projectId.ToString());
        NhAssistantRevalidationResult byObject;
        NhAssistantRevalidationResult bySubject;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var revalidation = scope.ServiceProvider.GetRequiredService<INhAssistantProposalRevalidation>();
            byObject = await revalidation.RevalidateKeysAsync([key], null, CancellationToken.None);
            bySubject = await revalidation.RevalidateSubjectAsync(Subject(projectId), CancellationToken.None);
        }

        Assert.Equal((0, 0), (byObject.Checked, bySubject.Checked));
        Assert.Empty(byObject.Invalidated);
        Assert.False(await RunStore(host).HasOpenProposalsByKeysAsync(NhAiSubjectInfo.CreateScopeKey(null, null), [key], CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.ShadowSuperseded, (await FindAsync(host, outcomes[0].ApprovalId!.Value)).Status);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, (await FindAsync(host, outcomes[1].ApprovalId!.Value)).Status);

        // The project's approver counts nothing in their inbox.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var person = new NhAssistantDecidingPerson(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Approver)], "test")),
                Approver,
                null);
            var counts = await scope.ServiceProvider.GetRequiredService<INhAssistantProposalCounts>()
                .CountAsync(person, NhAssistantInboxStatuses.All, CancellationToken.None);
            Assert.All(NhAssistantInboxStatuses.All, inboxStatus => Assert.Equal(0, counts[inboxStatus]));
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_trigger_stores_the_payload_its_rules_read(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), recordRuns: true);
        var projectId = Guid.NewGuid();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
                .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, "intake-complete"));
            // The payload trigger starts a run or joins the one the other trigger started.
            Assert.NotEqual(NhAssistantTriggerOutcomes.Rejected, Assert.Single(receipts, receipt => receipt.TriggerId == PayloadTriggerId).Outcome);
        }

        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var input = await context.QueuedInputs.SingleAsync(item => item.TriggerId == PayloadTriggerId);
        Assert.Equal(new RuleNotice("intake-complete"), JsonSerializer.Deserialize<RuleNotice>(input.PayloadJson!, JsonSerializerOptions.Web));
    }

    private const string ModelTriggerId = "model-trigger";
    private const string PayloadTriggerId = "payload-trigger";

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        Action<NhAiApprovalPolicyBuilder>? policy = null,
        bool shadow = false,
        bool recordRuns = false,
        NhAiProposalGuardStages guardStages = NhAiProposalGuardStages.All,
        TimeProvider? clock = null,
        bool scriptTool = false)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestProjectStates>();
                services.AddSingleton<TestRuleScript>();
                if (recordRuns)
                {
                    services.AddSingleton<RecordingOperations>();
                    services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                }
                else
                {
                    services.AddSingleton<RevalidationOperations>();
                    services.AddSingleton(provider => provider.GetRequiredService<RevalidationOperations>().Service);
                }
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                if (scriptTool)
                {
                    services.AddNewHeapPlatformAIScripting();
                    services.AddSingleton(new TestCompositionSwitchState());
                }
                services.Configure<Microsoft.AspNetCore.Builder.RequestLocalizationOptions>(options =>
                {
                    options.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("en");
                    options.SupportedCultures = [CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("nl")];
                    options.SupportedUICultures = [CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("nl")];
                });
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<TestSubjectResolver>()
                    .AddProposalGuard<ProjectStatusGuard>([ChangeStatusTool], guardStages)
                    .AddApprovalPolicy("project-review", builder =>
                    {
                        builder
                            .Lifetime(TimeSpan.FromDays(3))
                            .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                            .Mode(NhAiApprovalMode.Deferred)
                            .BindTo(NhAiApprovalBinding.ToolContract);
                        policy?.Invoke(builder);
                    })
                    .AddTrigger<ProjectChanged>(ModelTriggerId, trigger => trigger
                        .StartAgent(AgentId)
                        .Subject(evt => Subject(evt.ProjectId))
                        .Message(evt => evt.Change)
                        .Dedupe(evt => "model:" + evt.EventId.ToString("N"))
                        .UseModel())
                    .AddTrigger<ProjectChanged>(PayloadTriggerId, trigger => trigger
                        .StartAgent(AgentId)
                        .Subject(evt => Subject(evt.ProjectId))
                        .Message(evt => "The project changed.")
                        .Dedupe(evt => "payload:" + evt.EventId.ToString("N"))
                        .Payload(evt => new RuleNotice(evt.Change)))
                    .ConfigureAgent(AgentId, agent => agent
                        .Grant("projects.*")
                        .UseApprovalPolicy("project-review")
                        .UseRuleHandler<TestRules>()
                        .Shadow(shadow));
                if (scriptTool)
                {
                    AddScriptToolAgents(assistant);
                }
            });
    }

    /// <summary>
    /// Three rule agents next to the script tool, which needs no selector: a Propose agent that names
    /// only the status tool and is not allowed scripts, a Propose agent over every project tool that is
    /// allowed scripts under a switch that has them on, and an Observe agent that names only the status
    /// tool. Each run may discover the script tool, as one acting with an account's rights does, so the
    /// turn withholds it or offers it.
    /// </summary>
    private static void AddScriptToolAgents(NhAssistantBuilder assistant)
    {
        assistant
            .UseCompositionSwitch<TestCompositionSwitch>()
            .AddAgent(AssistantTestData.Agent(ProposingAgent, autonomy: NhAiAutonomyLevel.Propose) with { ToolSelectors = [ChangeStatusTool] })
            .AddAgent(AssistantTestData.Agent(ScriptingAgent, autonomy: NhAiAutonomyLevel.Propose))
            .AddAgent(AssistantTestData.Agent(ObservingAgent, autonomy: NhAiAutonomyLevel.Observe) with { ToolSelectors = [ChangeStatusTool] })
            .ConfigureAgentContext(ScriptingAgent, context => context.AllowScripts());
        foreach (var agentId in new[] { ProposingAgent, ScriptingAgent, ObservingAgent })
        {
            assistant.ConfigureAgent(agentId, agent => agent
                .Grant("projects.*", NhAiScriptTool.ToolId)
                .UseApprovalPolicy("project-review")
                .UseRuleHandler<TestRules>());
        }
    }

    private sealed record StartedRun(AssistantThread Thread, AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    private static async Task<StartedRun> StartRunAsync(
        AssistantTestHost host,
        Guid projectId,
        string triggerId = "project-created",
        bool isRequestTurn = false,
        bool agentEnabled = true,
        bool shadow = false,
        string agentId = AgentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(Subject(projectId), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: !agentEnabled, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadow)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = triggerId,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            Shadow = shadow,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[agentId];
        var agentTurn = new NhAssistantAgentTurnContext(
            thread,
            info,
            options,
            isRequestTurn ? null : run.Id,
            triggerId,
            isRequestTurn ? "member-1" : null,
            thread.AccountableOwnerId,
            IsRequestTurn: isRequestTurn);
        return new StartedRun(thread, run, agentTurn);
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(
        AssistantTestHost host,
        StartedRun run,
        string message,
        string? payloadJson = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                run.AgentTurn,
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message) { PayloadJson = payloadJson }]),
            CancellationToken.None);
    }

    private static async Task<TaskResult<AssistantApproval>> DecideAsync(AssistantTestHost host, Guid approvalId, bool approve)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approvalId,
            Approver,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Approver)], "test")),
            approve,
            expectedProposalHash: null,
            reason: null,
            CancellationToken.None);
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var result = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalExecution>().ExecuteAsync(
            approvalId,
            Substitute.For<INhBackgroundOperationContext>(),
            fenced: false,
            CancellationToken.None);
        Assert.True(result.Success);
    }

    private static async Task<AssistantApproval> FindAsync(AssistantTestHost host, Guid approvalId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
    }

    private static TestRuleScript Script(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<TestRuleScript>();
    }

    private static NhAssistantRunStore RunStore(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private static NhAiSubjectRef Subject(Guid projectId)
    {
        return new NhAiSubjectRef("project", projectId.ToString());
    }
}

public sealed record RuleNotice(string Change);

/// <summary>
/// What the test rule handler does in the current test, and what it saw and got.
/// </summary>
public sealed class TestRuleScript
{
    public Func<NhAssistantRuleContext, CancellationToken, Task<NhAssistantRuleResult>> Run { get; set; } =
        (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Nothing to do."));

    public ConcurrentQueue<NhAssistantRuleContext> Contexts { get; } = new();

    public ConcurrentQueue<NhAssistantRuleProposal> Proposals { get; } = new();
}

public sealed class TestRules(TestRuleScript script) : INhAssistantAgentRuleHandler
{
    public Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        script.Contexts.Enqueue(context);
        return script.Run(context, cancellationToken);
    }
}
