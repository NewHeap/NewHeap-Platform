using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB19: the application's <see cref="INhAssistantApprovalPolicySelector"/> chooses the approval policy of
/// each proposal after code resolved it. Its choice is kept in the proposal's policy copy and the decision
/// checks it; <see langword="null"/> or the resolved id keeps the resolved policy; an unknown id, a selector
/// that throws and one past its time limit refuse the proposal with their own codes. It is asked for every
/// source of proposals of the governed path: runs, rule runs, a person's messages in an agent's thread,
/// steps of compositions and consultations, also when the asker leaves a proposal for the subject's
/// approvers. Both relational providers, because the proposals persist.
/// </summary>
public sealed class AssistantApprovalPolicySelectorTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusTool = "projects.change-status";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string RiskPolicy = "risk-review";
    private const string RiskFourEyesPolicy = "risk-review-four-eyes";
    private const string Member = "member-1";
    private const string Approver = "approver-1";
    private const string SecondApprover = "approver-2";
    private const string ReviewPolicy = "project-review";
    private const string FourEyesPolicy = "project-review-four-eyes";
    private const string TriggerId = "project-created";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_chosen_policy_is_kept_in_the_proposals_copy_and_the_decision_checks_it(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.");
        await using var host = await CreateHostAsync(provider, model);
        var selections = Selections(host);
        selections.Choose = (_, _) => ValueTask.FromResult<string?>(FourEyesPolicy);
        var run = await StartRunAsync(host, projectId, initiator: Approver);

        var result = await RunTurnAsync(host, run, "Project PRJ-1 was created.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        var approval = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal((FourEyesPolicy, NhAssistantApprovalModes.Deferred, Approver), (approval.PolicyId, approval.Mode, approval.InitiatorActorId));
        Assert.True(NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson!).FourEyes);

        // The selector read the proposal as code resolved it, without prompt or result content.
        var selection = Assert.Single(selections.Seen);
        Assert.Equal(
            (AgentId, ChangeStatusTool, new NhAiSubjectRef("project", projectId.ToString()), ReviewPolicy, NhAssistantApprovalPolicySource.Run),
            (selection.AgentId, selection.ToolId, selection.Subject, selection.ResolvedPolicyId, selection.Source));
        Assert.Equal((TriggerId, Approver, run.Run.Id, (string?)null, (NhAiToolCallOrigin?)null), (selection.TriggerId, selection.InitiatorActorId, selection.RunId, selection.SkillId, selection.Origin));
        Assert.Equal(projectId.ToString(), selection.Arguments.GetProperty("projectId").GetString());

        // The resolved policy would let the initiator decide; the chosen one keeps them out. A participant
        // is no approver under either, and the second approver decides.
        Assert.Equal(NhAssistantErrorCodes.FourEyesRequired, CodeOf(await DecideAsync(host, approval, Approver)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyDenied, CodeOf(await DecideAsync(host, approval, Member)));
        var facts = await FactsAsync(host, approval, SecondApprover);
        Assert.True(facts.Data!.FourEyes);
        Assert.True(facts.Data.CanDecide);
        // A later choice of the selector never changes a waiting proposal.
        selections.Choose = (_, _) => ValueTask.FromResult<string?>(ReviewPolicy);
        Assert.Equal(NhAssistantErrorCodes.FourEyesRequired, CodeOf(await DecideAsync(host, approval, Approver)));
        var decided = await DecideAsync(host, approval, SecondApprover);
        Assert.True(decided.Success);
        Assert.Equal(NhAssistantApprovalStatuses.Approved, decided.Data!.Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Null_or_the_resolved_id_keeps_the_resolved_policy(AssistantTestProvider provider)
    {
        Guid[] projects = [Guid.NewGuid(), Guid.NewGuid()];
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = projects[0], status = "Active" } }, "call-1")
            .RespondWithText("Proposed.")
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = projects[1], status = "Active" } }, "call-2")
            .RespondWithText("Proposed.");
        await using var host = await CreateHostAsync(provider, model);
        var selections = Selections(host);

        selections.Choose = (_, _) => ValueTask.FromResult<string?>(null);
        var keptByNull = await ProposeInRunAsync(host, projects[0]);
        selections.Choose = (selection, _) => ValueTask.FromResult<string?>(selection.ResolvedPolicyId);
        var keptById = await ProposeInRunAsync(host, projects[1]);

        foreach (var approval in new[] { keptByNull, keptById })
        {
            Assert.Equal(ReviewPolicy, approval.PolicyId);
            Assert.False(NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson!).FourEyes);
            // The resolved policy has no four eyes: the initiator decides.
            Assert.True((await DecideAsync(host, approval, Approver)).Success);
        }
        Assert.Equal(2, selections.Seen.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer, SelectorFailure.Unknown)]
    [InlineData(AssistantTestProvider.PostgreSql, SelectorFailure.Unknown)]
    [InlineData(AssistantTestProvider.SqlServer, SelectorFailure.Throws)]
    [InlineData(AssistantTestProvider.PostgreSql, SelectorFailure.Throws)]
    [InlineData(AssistantTestProvider.SqlServer, SelectorFailure.MissesTimeLimit)]
    [InlineData(AssistantTestProvider.PostgreSql, SelectorFailure.MissesTimeLimit)]
    [InlineData(AssistantTestProvider.SqlServer, SelectorFailure.IgnoresTimeLimit)]
    [InlineData(AssistantTestProvider.PostgreSql, SelectorFailure.IgnoresTimeLimit)]
    public async Task An_unknown_policy_a_throwing_selector_and_one_past_its_time_limit_refuse_the_proposal(
        AssistantTestProvider provider,
        SelectorFailure failure)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("Nothing was proposed.");
        await using var host = await CreateHostAsync(provider, model, timeLimit: TimeSpan.FromMilliseconds(200));
        var release = new TaskCompletionSource();
        Selections(host).Choose = failure switch
        {
            SelectorFailure.Unknown => (_, _) => ValueTask.FromResult<string?>("not-registered"),
            SelectorFailure.Throws => (_, _) => throw new InvalidOperationException("selector-secret-detail"),
            SelectorFailure.MissesTimeLimit => async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return FourEyesPolicy;
            },
            // A selector that ignores its token is cut off by the time limit as well.
            _ => async (_, _) =>
            {
                await release.Task;
                return FourEyesPolicy;
            }
        };
        var expected = failure switch
        {
            SelectorFailure.Unknown => NhAssistantErrorCodes.ApprovalPolicyUnknown,
            SelectorFailure.Throws => NhAssistantErrorCodes.ApprovalPolicySelectorFailed,
            _ => NhAssistantErrorCodes.ApprovalPolicySelectorTimedOut
        };
        var run = await StartRunAsync(host, projectId, initiator: Approver);

        try
        {
            var result = await RunTurnAsync(host, run, "Project PRJ-1 was created.");

            // Nothing waits for people; the run ends and the model read why.
            Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
            Assert.Equal(0, result.ProposalCount);
            Assert.Empty(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
            Assert.Contains(expected, Assert.Single(model.FunctionResults).Result?.ToString());
            var call = await CallAsync(host, run.Thread.ConversationId);
            Assert.Equal((NhAssistantToolCallStatuses.NotExecuted, expected, (Guid?)null), (call.Status, call.ResultCode, call.ApprovalId));
            Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked
                && evt.ResultCode == expected
                && evt.InvocationId == call.Id);
            Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRequested);
            Assert.Empty(host.Tools.StatusChanges);

            // The log names the selector and, for an exception, its type only.
            var logged = Assert.Single(host.Logs.Entries, entry => entry.Category.EndsWith(nameof(NhAssistantApprovalPolicies), StringComparison.Ordinal));
            Assert.Equal(LogLevel.Warning, logged.Level);
            Assert.Contains(nameof(TestPolicySelector), logged.Message);
            Assert.DoesNotContain("selector-secret-detail", logged.Message);
            if (failure == SelectorFailure.Throws)
            {
                Assert.Contains(typeof(InvalidOperationException).FullName!, logged.Message);
            }
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_persons_message_in_the_agents_thread_asks_the_selector_and_decides_inline_under_the_chosen_policy(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status");
        await using var host = await CreateHostAsync(provider, model);
        var selections = Selections(host);
        selections.Choose = (_, _) => ValueTask.FromResult<string?>(FourEyesPolicy);
        var thread = await OpenThreadAsync(host, projectId, Member, Approver);

        var turn = await RequestTurnAsync(host, thread, Member, "Activate the project.");

        var card = turn.Single<NhAssistantApprovalRequiredEvent>().Approval;
        var approval = await FindAsync(host, card.ApprovalId);
        // A person typing decides inline, under the chosen policy.
        Assert.Equal((FourEyesPolicy, NhAssistantApprovalModes.Inline, Member), (approval.PolicyId, approval.Mode, approval.InitiatorActorId));
        var selection = Assert.Single(selections.Seen);
        Assert.Equal(
            (NhAssistantApprovalPolicySource.Request, ReviewPolicy, Member, (Guid?)null, (string?)null),
            (selection.Source, selection.ResolvedPolicyId, selection.InitiatorActorId, selection.RunId, selection.TriggerId));

        await using var scope = host.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<NhAssistantApproverCheck>();
        Assert.Equal(NhAssistantErrorCodes.FourEyesRequired, CodeOf(await check.CheckAsync(approval, thread, Member, Principal(Member), CancellationToken.None)));
        Assert.True((await check.CheckAsync(approval, thread, Approver, Principal(Approver), CancellationToken.None)).Success);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_composition_step_in_a_run_asks_the_selector_with_its_origin(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestCompositions.FlagOpenRisksFunction, new { input = new { projectId } }, "call-flag")
            .RespondWithText("I proposed to flag the risk.")
            .RespondWithFunctionCall(TestCompositions.FlagOpenRisksFunction, new { input = new { projectId } }, "call-flag-2")
            .RespondWithText("Nothing was proposed.");
        await using var host = await CreateCompositionHostAsync(provider, model);
        var selections = Selections(host);
        selections.Choose = (_, _) => ValueTask.FromResult<string?>(RiskFourEyesPolicy);
        var run = await AssistantCompositionTests.StartRunAsync(host, projectId, shadow: false);

        await AssistantCompositionTests.RunTurnAsync(host, run);

        // The composition's flag step proposes under the chosen policy, and the selector knows the step.
        var runs = host.Services.GetRequiredService<NhAssistantRunStore>();
        var approval = Assert.Single(await runs.GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal((TestRiskToolCatalog.FlagRisk.Id, RiskFourEyesPolicy), (approval.ToolId, approval.PolicyId));
        var selection = Assert.Single(selections.Seen);
        var origin = Assert.IsType<NhAiToolCallOrigin>(selection.Origin);
        Assert.Equal((NhAiToolCallOriginKind.Composition, TestCompositions.FlagOpenRisks, 1), (origin.Kind, origin.ToolId, origin.StepIndex));
        Assert.Equal(
            (TestRiskToolCatalog.FlagRisk.Id, NhAssistantApprovalPolicySource.Run, RiskPolicy),
            (selection.ToolId, selection.Source, selection.ResolvedPolicyId));
        var result = JsonSerializer.SerializeToElement(model.FunctionResults[0].Result);
        Assert.Equal(nameof(NhAssistantToolStepStatus.Proposed), result.GetProperty("data").GetProperty("flagged").GetString());

        // A refusal fails the step with its code, and nothing is proposed.
        selections.Choose = (_, _) => ValueTask.FromResult<string?>("not-registered");
        var second = await AssistantCompositionTests.StartRunAsync(host, projectId, shadow: false);
        await AssistantCompositionTests.RunTurnAsync(host, second);
        Assert.Empty(await runs.GetRunApprovalsAsync(second.Run.Id, CancellationToken.None));
        var rows = await AssistantToolStepTests.RowsAsync(host, second.AgentTurn.Thread.ConversationId);
        Assert.Contains(rows, row => row.ToolId == TestRiskToolCatalog.FlagRisk.Id
            && row.Status == NhAssistantToolCallStatuses.NotExecuted
            && row.ResultCode == NhAssistantErrorCodes.ApprovalPolicyUnknown);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_run_proposes_under_the_chosen_policy_and_reads_a_refusals_code(AssistantTestProvider provider)
    {
        Guid[] projects = [Guid.NewGuid(), Guid.NewGuid()];
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), rules: true);
        var selections = Selections(host);
        var script = host.Services.GetRequiredService<TestRuleScript>();
        script.Run = async (context, cancellationToken) =>
        {
            var projectId = Guid.Parse(context.Subject.Subject.Id);
            script.Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(projectId, "Active"), null, cancellationToken));
            return NhAssistantRuleResult.Done("Done.");
        };

        selections.Choose = (_, _) => ValueTask.FromResult<string?>(FourEyesPolicy);
        var chosen = await StartRunAsync(host, projects[0], initiator: Approver);
        await RunTurnAsync(host, chosen, "Project PRJ-1 was created.");
        selections.Choose = (_, _) => ValueTask.FromResult<string?>("not-registered");
        var refused = await StartRunAsync(host, projects[1], initiator: Approver);
        await RunTurnAsync(host, refused, "Project PRJ-2 was created.");

        var proposals = script.Proposals.ToArray();
        Assert.Equal(NhAssistantProposeOutcome.Created, proposals[0].Outcome);
        Assert.Equal(FourEyesPolicy, (await FindAsync(host, proposals[0].ApprovalId!.Value)).PolicyId);
        Assert.Equal((NhAssistantProposeOutcome.Refused, NhAssistantErrorCodes.ApprovalPolicyUnknown, (Guid?)null), (proposals[1].Outcome, proposals[1].Code, proposals[1].ApprovalId));
        Assert.All(selections.Seen, selection => Assert.Equal(NhAssistantApprovalPolicySource.Run, selection.Source));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_consultations_proposal_and_the_one_its_asker_leaves_for_the_approvers_ask_the_selector(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithText("Proposed.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("consult_project-assistant_project_v1", new { input = new { subjectId = "project-0001", question = "Activate it." } }, "call-1")
            .RespondWithText("The project's approvers decide it now.");
        await using var host = await CreateConsultationHostAsync(provider, target, asker);
        var selections = Selections(host);
        selections.Choose = (selection, _) => ValueTask.FromResult<string?>(
            selection.Source == NhAssistantApprovalPolicySource.Consultation ? ChosenConsultationPolicy : null);
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        var card = (await host.SendAsync(conversation.Id, "Activate it.", Member)).Single<NhAssistantApprovalRequiredEvent>().Approval;

        // The asker decides under the chosen policy, which the route keeps inline.
        var asked = await FindAsync(host, card.ApprovalId);
        Assert.Equal((ChosenConsultationPolicy, NhAssistantApprovalModes.Inline), (asked.PolicyId, asked.Mode));
        var first = Assert.Single(selections.Seen);
        Assert.Equal(
            (NhAssistantApprovalPolicySource.Consultation, AskerConsultationPolicy, Member, AgentId, (Guid?)null),
            (first.Source, first.ResolvedPolicyId, first.InitiatorActorId, first.AgentId, first.RunId));

        var left = await host.LeaveForApproversAsync(conversation.Id, card, Member);

        // The proposal at the subject is a new proposal: the selector chooses its policy again.
        Assert.Equal(NhAssistantTurnStatuses.Completed, left.Single<NhAssistantTurnCompletedEvent>().Status);
        var moved = await FindAsync(host, (await FindAsync(host, card.ApprovalId)).SupersededByApprovalId!.Value);
        Assert.Equal((ChosenConsultationPolicy, NhAssistantApprovalModes.Deferred, Member), (moved.PolicyId, moved.Mode, moved.InitiatorActorId));
        Assert.Equal(2, selections.Seen.Count);
        Assert.Equal(NhAssistantApprovalPolicySource.Consultation, selections.Seen.Last().Source);
        Assert.Equal(projectId.ToString(), selections.Seen.Last().Arguments.GetProperty("projectId").GetString());
    }

    [Fact]
    public void A_selectors_time_limit_is_longer_than_zero_and_at_most_thirty_seconds()
    {
        var services = new ServiceCollection();

        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_selector", options => options.Schema = "nhai")
                .UseChatProfile("project-chat");
            Assert.Throws<ArgumentOutOfRangeException>(() => assistant.UseApprovalPolicySelector<TestPolicySelector>(TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => assistant.UseApprovalPolicySelector<TestPolicySelector>(TimeSpan.FromSeconds(31)));
            assistant.UseApprovalPolicySelector<TestPolicySelector>(NhAssistantApprovalPolicySelection.MaxTimeLimit);
            // A later selector replaces the earlier one.
            assistant.UseApprovalPolicySelector<AnotherPolicySelector>();
        });

        var selector = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(INhAssistantApprovalPolicySelector));
        Assert.Equal((typeof(AnotherPolicySelector), ServiceLifetime.Scoped), (selector.ImplementationType, selector.Lifetime));
    }

    public enum SelectorFailure
    {
        Unknown,
        Throws,
        MissesTimeLimit,
        IgnoresTimeLimit
    }

    private const string AskingAgentId = "consulting-assistant";
    private const string AskerConsultationPolicy = "consultation-asker";
    private const string ChosenConsultationPolicy = "consultation-chosen";

    private Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        TimeSpan? timeLimit = null,
        bool rules = false)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                var subjects = new TestSubjects();
                subjects.Roles[SecondApprover] = NhAiSubjectRole.Approver;
                services.AddSingleton(subjects);
                services.AddSingleton<TestPolicySelections>();
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<TestSubjectResolver>()
                    .AddApprovalPolicy(ReviewPolicy, policy => policy
                        .Lifetime(TimeSpan.FromDays(3))
                        .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                        .Mode(NhAiApprovalMode.Deferred)
                        .BindTo(NhAiApprovalBinding.ToolContract))
                    .AddApprovalPolicy(FourEyesPolicy, policy => policy
                        .Lifetime(TimeSpan.FromDays(3))
                        .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                        .Mode(NhAiApprovalMode.Deferred)
                        .FourEyes()
                        .BindTo(NhAiApprovalBinding.ToolContract))
                    .ConfigureAgent(AgentId, agent =>
                    {
                        agent
                            .Grant("projects.*")
                            .UseApprovalPolicy(ReviewPolicy);
                        if (rules)
                        {
                            agent.UseRuleHandler<TestRules>();
                        }
                    });
                if (timeLimit is { } limit)
                {
                    assistant.UseApprovalPolicySelector<TestPolicySelector>(limit);
                }
                else
                {
                    assistant.UseApprovalPolicySelector<TestPolicySelector>();
                }
            });
    }

    /// <summary>
    /// A host whose agent answers to a technical account and runs the test compositions, as the
    /// composition tests run them: the flag step proposes, the tag step needs no approval.
    /// </summary>
    private Task<AssistantTestHost> CreateCompositionHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton<TestRiskToolRecorder>();
                services.AddSingleton(new TestCompositionSwitchState());
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestPolicySelections>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestRiskToolCatalog>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy(RiskPolicy, policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .AddApprovalPolicy(RiskFourEyesPolicy, policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .FourEyes()
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent => agent
                    .ServiceAccount(Approver)
                    .ActWithAccountableRights()
                    .UseApprovalPolicy(RiskPolicy))
                .ConfigureAgentContext(AgentId, context => context.AllowCompositions())
                .UseCompositionSwitch<TestCompositionSwitch>()
                .UseApprovalPolicySelector<TestPolicySelector>()
                .AddTestCompositions());
    }

    /// <summary>
    /// A host where a consulting assistant asks the project assistant about a project; the project
    /// assistant's proposals are the asker's to decide, or the subject's approvers' when the asker leaves
    /// them there.
    /// </summary>
    private Task<AssistantTestHost> CreateConsultationHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient target,
        NhAiScriptedChatClient asker)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            target,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestPolicySelections>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.AddSingleton<CountingPrincipals>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(provider => provider.GetRequiredService<CountingPrincipals>()));
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
                .AddAgent(AssistantTestData.Agent(AskingAgentId) with
                {
                    ProfileName = "asking-chat",
                    ToolSelectors = ["assistant.consult.*"]
                })
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy(AskerConsultationPolicy, policy => policy
                    .Approvers(NhAiApprovers.Initiator, NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Lifetime(TimeSpan.FromDays(1)))
                .AddApprovalPolicy(ChosenConsultationPolicy, policy => policy
                    .Approvers(NhAiApprovers.Initiator, NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Lifetime(TimeSpan.FromDays(2)))
                .UseApprovalPolicySelector<TestPolicySelector>()
                .ConfigureAgent(AgentId, run => run
                    .Grant("projects.*")
                    .AllowConsultation("project", consult => consult
                        .Description("Knows the intake review of a project.")
                        .Proposals(proposals => proposals
                            .AskerDecides()
                            .AtSubject(NhAssistantConsultationRights.Asker)
                            .Policy(AskerConsultationPolicy)))));
    }

    private sealed record StartedRun(AssistantThread Thread, AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    private static async Task<StartedRun> StartRunAsync(AssistantTestHost host, Guid projectId, string initiator)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId.ToString()), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = TriggerId,
            InitiatorActorId = initiator,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var agentTurn = new NhAssistantAgentTurnContext(
            thread,
            info,
            services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId],
            run.Id,
            TriggerId,
            initiator,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        return new StartedRun(thread, run, agentTurn);
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, StartedRun run, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(run.AgentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message)]),
            CancellationToken.None);
    }

    private static async Task<AssistantApproval> ProposeInRunAsync(AssistantTestHost host, Guid projectId)
    {
        var run = await StartRunAsync(host, projectId, initiator: Approver);
        await RunTurnAsync(host, run, "Project changed.");
        return Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
    }

    private static async Task<AssistantThread> OpenThreadAsync(AssistantTestHost host, Guid projectId, params string[] people)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId.ToString()), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var threads = services.GetRequiredService<NhAssistantThreadService>();
        var thread = (await threads.GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        foreach (var person in people)
        {
            Assert.NotNull(await threads.ResolveAccessAsync(thread.ConversationId, person, person, CancellationToken.None));
        }

        return thread;
    }

    /// <summary>
    /// A person's message in the agent's thread, as the conversation endpoint starts it.
    /// </summary>
    private static async Task<TurnResult> RequestTurnAsync(AssistantTestHost host, AssistantThread thread, string actorId, string text)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var httpContext = AssistantTestHost.EnterUser(services, actorId);
        var caller = (await services.GetRequiredService<INhAiAuthenticatedInvocationContextResolver>().ResolveAsync(httpContext)).Data;
        var agentTurn = (await services.GetRequiredService<NhAssistantAgentTurnFactory>().CreateAsync(
            thread.ConversationId,
            null,
            NhAssistantConversationKinds.Chat,
            actorId,
            thread.AccountableOwnerId,
            CancellationToken.None)).Data! with { IsRequestTurn = true };
        var started = await services.GetRequiredService<INhAssistantTurnRunner>().StartMessageTurnAsync(
            new NhAssistantMessageTurnRequest(thread.ConversationId, caller!, text, Guid.NewGuid().ToString("N"), CancellationToken.None)
            {
                AgentTurn = agentTurn
            },
            CancellationToken.None);
        Assert.True(started.Success);
        var events = new List<NhAssistantTurnEvent>();
        await foreach (var evt in started.Data!.Events.ReadAllAsync())
        {
            events.Add(evt);
        }
        await started.Data.Completion;
        return new TurnResult(started, events);
    }

    private static async Task<TaskResult<AssistantApproval>> DecideAsync(AssistantTestHost host, AssistantApproval approval, string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approval.Id,
            actorId,
            Principal(actorId),
            approve: true,
            approval.ProposalHash,
            reason: null,
            CancellationToken.None);
    }

    private static async Task<TaskResult<NhAssistantProposalDecisionFacts>> FactsAsync(AssistantTestHost host, AssistantApproval approval, string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().FactsAsync(
            approval.Id,
            actorId,
            Principal(actorId),
            CancellationToken.None);
    }

    private static async Task<AssistantApproval> FindAsync(AssistantTestHost host, Guid approvalId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
    }

    private static async Task<AssistantToolInvocation> CallAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.ToolInvocations.SingleAsync(item => item.ConversationId == conversationId && item.ToolId == ChangeStatusTool);
    }

    private static TestPolicySelections Selections(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<TestPolicySelections>();
    }

    private static ClaimsPrincipal Principal(string actorId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"));
    }

    private static string? CodeOf(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }
}

/// <summary>
/// What the test's approval policy selector answers, and what it was asked.
/// </summary>
public sealed class TestPolicySelections
{
    public Func<NhAssistantApprovalPolicySelection, CancellationToken, ValueTask<string?>> Choose { get; set; } =
        (_, _) => ValueTask.FromResult<string?>(null);

    public ConcurrentQueue<NhAssistantApprovalPolicySelection> Seen { get; } = new();
}

public sealed class TestPolicySelector(TestPolicySelections selections) : INhAssistantApprovalPolicySelector
{
    public ValueTask<string?> SelectAsync(NhAssistantApprovalPolicySelection selection, CancellationToken cancellationToken)
    {
        selections.Seen.Enqueue(selection);
        return selections.Choose(selection, cancellationToken);
    }
}

public sealed class AnotherPolicySelector : INhAssistantApprovalPolicySelector
{
    public ValueTask<string?> SelectAsync(NhAssistantApprovalPolicySelection selection, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<string?>(null);
    }
}
