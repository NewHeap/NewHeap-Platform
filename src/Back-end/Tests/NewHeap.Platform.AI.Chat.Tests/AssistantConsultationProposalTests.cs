using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
/// The routes of a consultation's proposals: the person who asked decides it in their own
/// conversation (A), it waits at the subject and executes with the asker's rights (today's route) or
/// with the consulted agent's own rights under four-eyes (B), or it is refused. No route rebuilds a
/// person's principal outside that person's request, except today's.
/// </summary>
public sealed class AssistantConsultationProposalTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string TargetAgentId = "project-assistant";
    private const string AskingAgentId = "consulting-assistant";
    private const string ConsultTool = "consult_project-assistant_project_v1";
    private const string ProposingAgentId = "proposing-assistant";
    private const string ProposingConsultTool = "consult_proposing-assistant_project_v1";
    private const string ChangeStatus = "projects_change_status_v1";
    private const string Search = "projects_search_v1";
    private const string Member = "member-1";
    private const string Approver = "approver-1";
    private const string ProjectId = "project-0001";
    private const string AskerPolicy = "consultation-asker";
    private const string FourEyesPolicy = "consultation-four-eyes";
    private const string ApproverPolicy = "consultation-approvers";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_asker_decides_the_proposal_in_their_own_chat_and_it_executes_with_their_live_rights(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithText("I proposed to activate the project.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Activate it if it is ready." } }, "call-1")
            .RespondWithText("The project is active now.");
        await using var host = await CreateHostAsync(provider, target, asker, proposals => proposals.AskerDecides().Policy(AskerPolicy));
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        var turn = await host.SendAsync(conversation.Id, "Activate the project if it is ready.", Member);

        // The asking turn waits for its person, as for its own inline proposal, with the origin on the card.
        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, turn.Single<NhAssistantTurnCompletedEvent>().Status);
        var card = turn.Single<NhAssistantApprovalRequiredEvent>().Approval;
        Assert.NotNull(card.Consultation);
        Assert.Equal(TargetAgentId, card.Consultation!.AgentId);
        Assert.Equal(ProjectId, card.Consultation.SubjectId);
        Assert.Null(card.Consultation.LeaveRoute);
        Assert.Contains("I proposed to activate the project.", card.Consultation.Answer);
        Assert.Contains("the person who asked decides it right away", target.FunctionResults.Single().Result?.ToString());
        var consultation = await SingleConsultationAsync(host);
        var approval = await StoredAsync(host, card.ApprovalId);
        Assert.Equal(consultation.ConversationId, approval.ConversationId);
        Assert.Equal(NhAssistantApprovalModes.Inline, approval.Mode);
        Assert.Equal(consultation.Id, approval.ConsultationId);
        Assert.Equal(Member, approval.InitiatorActorId);
        Assert.True(approval.ExpiresAt <= approval.CreatedAt.AddMinutes(16));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRequested
            && evt.ApprovalId == approval.Id
            && evt.ConsultationRoute == NhAssistantConsultationRoutes.AskerDecides);

        // The card survives a reload, and the consulted agent's thread names who decides it.
        var view = await host.ReadViewAsync(conversation.Id, Member);
        Assert.Equal(approval.Id, view.PendingApproval!.ApprovalId);
        Assert.Equal(TargetAgentId, view.PendingApproval.Consultation!.AgentId);

        var decided = await host.DecideAsync(conversation.Id, card, approve: true, userId: Member);

        // It executed in the asker's request with the asker's own rights; no principal was rebuilt.
        Assert.Equal(NhAssistantTurnStatuses.Completed, decided.Single<NhAssistantTurnCompletedEvent>().Status);
        var executed = Assert.Single(host.Tools.StatusChanges);
        Assert.Equal("active", executed.Status);
        Assert.Equal(NhAiGrantSource.Delegated, host.Tools.Contexts.Last().GrantSource);
        Assert.Empty(host.Services.GetRequiredService<CountingPrincipals>().People);
        Assert.Equal(NhAssistantApprovalStatuses.Executed, (await StoredAsync(host, approval.Id)).Status);
        var result = asker.FunctionResults.Last().Result?.ToString();
        Assert.Contains("\"outcome\":\"executed\"", result);
        Assert.Contains("\"decision\":\"asker\"", result);
        Assert.Contains("I proposed to activate the project.", result);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved
            && evt.ApprovalId == approval.Id
            && evt.ConsultationRoute == NhAssistantConsultationRoutes.AskerDecides);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalExecuted
            && evt.ApprovalId == approval.Id
            && evt.ConsultationRoute == NhAssistantConsultationRoutes.AskerDecides);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_the_asker_decides_and_a_rejection_is_no_rejection_of_the_agents_work(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithText("Proposed.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Activate it." } }, "call-1")
            .RespondWithText("You declined.");
        await using var host = await CreateHostAsync(provider, target, asker, proposals => proposals.AskerDecides().Policy(AskerPolicy));
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
        var card = (await host.SendAsync(conversation.Id, "Activate it.", Member)).Single<NhAssistantApprovalRequiredEvent>().Approval;

        // The thread's approvers and the inbox cannot decide it.
        var thread = (await host.Services.GetRequiredService<NhAssistantRunStore>().FindThreadByConversationAsync((await SingleConsultationAsync(host)).ConversationId, CancellationToken.None))!;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var decisions = scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>();
            var refused = await decisions.DecideAsync(card.ApprovalId, Approver, Principal(Approver), true, null, null, CancellationToken.None);
            Assert.Contains(refused.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ConsultAskerDecides);
        }
        var other = await host.DecideAsync(conversation.Id, card, approve: true, userId: Approver);
        Assert.NotNull(other.StartCode);

        var rejected = await host.DecideAsync(conversation.Id, card, approve: false, userId: Member);

        Assert.Equal(NhAssistantTurnStatuses.Completed, rejected.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Empty(host.Tools.StatusChanges);
        Assert.Contains("\"outcome\":\"rejected\"", asker.FunctionResults.Last().Result?.ToString());
        var stored = await StoredAsync(host, card.ApprovalId);
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, stored.Status);
        // The asker declined their own request: no cooldown and no rejection context for later runs.
        var runs = host.Services.GetRequiredService<NhAssistantRunStore>();
        Assert.Null(await runs.FindRecentRejectionAsync(thread.ScopeKey, TargetAgentId, stored.ToolId, stored.ArgumentsHash!, DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));
        Assert.Empty(await host.Services.GetRequiredService<NhAssistantProposalFeedbackStore>()
            .GetRecentRejectionsAsync(thread.ConversationId, DateTimeOffset.UtcNow.AddDays(-1), 10, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_asker_leaves_the_proposal_for_approvers_who_decide_it_and_it_executes_with_the_agents_rights(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithText("Proposed.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Activate it." } }, "call-1")
            .RespondWithText("The project's approvers decide it now.");
        var channel = new RecordingChannel();
        await using var host = await CreateHostAsync(
            provider,
            target,
            asker,
            proposals => proposals.AskerDecides().AtSubject(NhAssistantConsultationRights.Agent).Policy(AskerPolicy),
            channel: channel);
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
        var card = (await host.SendAsync(conversation.Id, "Activate it.", Member)).Single<NhAssistantApprovalRequiredEvent>().Approval;
        Assert.Equal(NhAssistantConsultationRoutes.SubjectAgentRights, card.Consultation!.LeaveRoute);

        var left = await host.LeaveForApproversAsync(conversation.Id, card, Member);

        Assert.Equal(NhAssistantTurnStatuses.Completed, left.Single<NhAssistantTurnCompletedEvent>().Status);
        var closed = await StoredAsync(host, card.ApprovalId);
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, closed.Status);
        Assert.Equal(NhAssistantErrorCodes.ConsultProposalMoved, closed.ExecutionResultCode);
        Assert.Equal(Member, closed.DecidedByActorId);
        var moved = await StoredAsync(host, closed.SupersededByApprovalId!.Value);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, moved.Status);
        Assert.Equal(NhAssistantApprovalModes.Deferred, moved.Mode);
        Assert.Equal(NhAiGrantSource.Agent, moved.GrantSource);
        Assert.Equal(Member, moved.InitiatorActorId);
        Assert.True(NhAssistantApprovalPolicySnapshot.Deserialize(moved.PolicyJson!).FourEyes);
        Assert.Contains("\"outcome\":\"moved\"", asker.FunctionResults.Last().Result?.ToString());
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRejected
            && evt.ApprovalId == closed.Id
            && evt.ResultCode == NhAssistantErrorCodes.ConsultProposalMoved);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRequested
            && evt.ApprovalId == moved.Id
            && evt.ConsultationRoute == NhAssistantConsultationRoutes.SubjectAgentRights);

        // An approver other than the asker approves it; it executes with the agent's own grant.
        await ApproveAndExecuteAsync(host, moved, Approver);
        Assert.Equal(NhAiGrantSource.Agent, host.Tools.Contexts.Last().GrantSource);
        Assert.Empty(host.Services.GetRequiredService<CountingPrincipals>().People);
        var told = Assert.Single(channel.Deliveries, delivery => delivery.Context.Kind == NhAssistantAgentNotificationKind.ConsultationProposalDecided);
        Assert.Equal([Member], told.Recipients);
        Assert.Equal(NhAssistantApprovalStatuses.Executed, told.Context.ProposalStatus);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_at_the_subject_with_the_agents_rights_is_four_eyes_and_never_rebuilds_the_asker(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithText("Proposed for the approvers.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Activate it." } }, "call-1")
            .RespondWithText("The approvers decide it.");
        await using var host = await CreateHostAsync(
            provider,
            target,
            asker,
            proposals => proposals.AtSubject(NhAssistantConsultationRights.Agent),
            agent: agent => agent.DelegateFromInitiator());
        // The asker approves the project's work too, yet may not approve their own consultation's proposal.
        host.Services.GetRequiredService<TestSubjects>().Roles[Member] = NhAiSubjectRole.Approver;
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        var turn = await host.SendAsync(conversation.Id, "Activate it.", Member);

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Contains("\"decision\":\"subject\"", asker.FunctionResults.Single().Result?.ToString());
        var consultation = await SingleConsultationAsync(host);
        var approval = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>().GetConsultationApprovalsAsync(consultation.Id, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalModes.Deferred, approval.Mode);
        Assert.Equal(NhAiGrantSource.Agent, approval.GrantSource);
        Assert.Equal(Member, approval.InitiatorActorId);
        var proposal = Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
        Assert.Equal(AssistantTestData.Agent(TargetAgentId).ActorId, proposal.AccountableOwnerId);
        Assert.True(NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson!).FourEyes);

        var thread = (await host.Services.GetRequiredService<NhAssistantRunStore>().FindThreadByConversationAsync(approval.ConversationId, CancellationToken.None))!;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var check = scope.ServiceProvider.GetRequiredService<NhAssistantApproverCheck>();
            var asAsker = await check.CheckAsync(approval, thread, Member, Principal(Member), CancellationToken.None);
            Assert.Contains(asAsker.GetResultItems(), item => item.Name == NhAssistantErrorCodes.FourEyesRequired);
            Assert.True((await check.CheckAsync(approval, thread, Approver, Principal(Approver), CancellationToken.None)).Success);
        }

        // The agent's runs delegate, but this proposal never borrows the asker's rights.
        await ApproveAndExecuteAsync(host, approval, Approver);
        Assert.Equal("active", Assert.Single(host.Tools.StatusChanges).Status);
        Assert.Equal(NhAiGrantSource.Agent, host.Tools.Contexts.Last().GrantSource);
        Assert.Empty(host.Services.GetRequiredService<CountingPrincipals>().People);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalExecuted
            && evt.ApprovalId == approval.Id
            && evt.ConsultationRoute == NhAssistantConsultationRoutes.SubjectAgentRights);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_asker_of_todays_route_hears_how_its_proposals_ended(AssistantTestProvider provider)
    {
        // CP28 (b): today's route tells its asker too, as the route with the agent's rights does.
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "on-hold" } }, "c-2")
            .RespondWithText("Proposed both for the approvers.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Change it." } }, "call-1")
            .RespondWithText("The approvers decide.");
        var channel = new RecordingChannel();
        await using var host = await CreateHostAsync(
            provider,
            target,
            asker,
            proposals => proposals.AtSubject(NhAssistantConsultationRights.Asker),
            channel: channel);
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
        await host.SendAsync(conversation.Id, "Change the project.", Member);
        var approvals = await host.Services.GetRequiredService<NhAssistantRunStore>()
            .GetConsultationApprovalsAsync((await SingleConsultationAsync(host)).Id, CancellationToken.None);
        Assert.Equal(2, approvals.Count);
        Assert.All(approvals, approval => Assert.Equal(NhAiGrantSource.Delegated, approval.GrantSource));

        // An approver rejects the first.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var rejected = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>()
                .DecideAsync(approvals[0].Id, Approver, Principal(Approver), false, null, "Not now.", CancellationToken.None);
            Assert.True(rejected.Success);
        }

        // The second is approved, but this application rebuilds nobody's principal, so it fails.
        await ApproveAndExecuteAsync(host, approvals[1], Approver);

        var told = channel.Deliveries
            .Where(delivery => delivery.Context.Kind == NhAssistantAgentNotificationKind.ConsultationProposalDecided)
            .ToArray();
        Assert.Equal(2, told.Length);
        Assert.All(told, delivery => Assert.Equal([Member], delivery.Recipients));
        Assert.Equal(
            [NhAssistantApprovalStatuses.ExecutionFailed, NhAssistantApprovalStatuses.Rejected],
            told.Select(delivery => delivery.Context.ProposalStatus).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Each_call_takes_the_first_route_that_applies_or_is_refused_with_a_reason(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        // Four-eyes keeps the asker out, so the asker's route does not apply: today's route takes it.
        var fallback = await ProposeOnceAsync(provider, projectId, proposals => proposals.AskerDecides().AtSubject(NhAssistantConsultationRights.Asker).Policy(FourEyesPolicy));
        Assert.Equal(NhAssistantApprovalModes.Deferred, fallback.Approval!.Mode);
        Assert.Equal(NhAiGrantSource.Delegated, fallback.Approval.GrantSource);

        // Without a route at the subject the call is refused, and the model reads why.
        var fourEyes = await ProposeOnceAsync(provider, projectId, proposals => proposals.AskerDecides().Policy(FourEyesPolicy));
        Assert.Null(fourEyes.Approval);
        Assert.Contains(NhAssistantErrorCodes.ConsultProposalRefused, fourEyes.InnerResult);
        Assert.Contains(NhAssistantConsultationRefusalReasons.FourEyes, fourEyes.InnerResult);
        Assert.Equal(NhAssistantConsultationRefusalReasons.FourEyes, fourEyes.Refusal!.ConsultationRefusalReason);

        // A participant is no approver under a policy of the subject's approvers.
        var notApprover = await ProposeOnceAsync(provider, projectId, proposals => proposals.AskerDecides().Policy(ApproverPolicy));
        Assert.Contains(NhAssistantConsultationRefusalReasons.AskerNotApprover, notApprover.InnerResult);

        // The agent's own rights do not allow the call.
        var notGranted = await ProposeOnceAsync(provider, projectId, proposals => proposals.AtSubject(NhAssistantConsultationRights.Agent), grant: false);
        Assert.Contains(NhAssistantConsultationRefusalReasons.AgentNotGranted, notGranted.InnerResult);

        // The asker already has as many proposals with the agent's rights open as allowed.
        var limited = await ProposeOnceAsync(
            provider,
            projectId,
            proposals => proposals.AtSubject(NhAssistantConsultationRights.Agent),
            limits: limits => limits.MaxOpenConsultationProposalsPerAsker = 1,
            proposeTwice: true);
        Assert.NotNull(limited.Approval);
        Assert.Contains(NhAssistantConsultationRefusalReasons.OpenLimit, limited.InnerResult);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Maintenance_expires_the_askers_proposal_frees_their_chat_and_expires_and_tells_about_one_with_the_agents_rights(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        var projectId = Guid.NewGuid();
        // One consultation leaves two proposals: the first the asker decides, the second, which cannot
        // wait for the asker too, waits at the subject with the agent's rights.
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "on-hold" } }, "c-2")
            .RespondWithText("Proposed both.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Activate it." } }, "call-1");
        var channel = new RecordingChannel();
        await using var host = await CreateHostAsync(
            provider,
            target,
            asker,
            proposals => proposals.AskerDecides().AtSubject(NhAssistantConsultationRights.Agent).Policy(AskerPolicy),
            channel: channel,
            clock: clock);
        var waiting = await host.CreateConversationAsync(Member, AskingAgentId);
        var card = (await host.SendAsync(waiting.Id, "Activate it.", Member)).Single<NhAssistantApprovalRequiredEvent>().Approval;
        Assert.Contains(NhAssistantConsultationRoutes.SubjectAgentRights, target.FunctionResults[1].Result?.ToString());
        var atSubject = (await host.Services.GetRequiredService<NhAssistantRunStore>()
                .GetConsultationApprovalsAsync((await SingleConsultationAsync(host)).Id, CancellationToken.None))
            .Single(approval => approval.Mode == NhAssistantApprovalModes.Deferred);
        var maintenance = host.Services.GetServices<IHostedService>().OfType<NhAssistantRunMaintenance>().Single();

        // The asker's proposal lives the application's approval lifetime at most; then the job closes it.
        clock.Advance(TimeSpan.FromMinutes(16));
        await maintenance.RunOnceAsync(CancellationToken.None);

        Assert.Equal(NhAssistantApprovalStatuses.Expired, (await StoredAsync(host, card.ApprovalId)).Status);
        var freed = await host.ReloadAsync(waiting.Id, Member);
        Assert.Equal(NhAssistantConversationStatuses.Idle, freed.Status);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalExpired
            && evt.ApprovalId == card.ApprovalId
            && evt.ConsultationRoute == NhAssistantConsultationRoutes.AskerDecides);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await StoredAsync(host, atSubject.Id)).Status);

        // The proposal at the subject expires at the end of its policy's lifetime, and its asker hears it.
        clock.Advance(TimeSpan.FromDays(1));
        await maintenance.RunOnceAsync(CancellationToken.None);

        Assert.Equal(NhAssistantApprovalStatuses.Expired, (await StoredAsync(host, atSubject.Id)).Status);
        var told = Assert.Single(channel.Deliveries, delivery => delivery.Context.Kind == NhAssistantAgentNotificationKind.ConsultationProposalDecided);
        Assert.Equal([Member], told.Recipients);
        Assert.Equal(NhAssistantApprovalStatuses.Expired, told.Context.ProposalStatus);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Administrators_switch_routes_within_the_ceiling_in_code(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithText("I can only describe it.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Activate it." } }, "call-1")
            .RespondWithText("Nothing was proposed.");
        await using var host = await CreateHostAsync(provider, target, asker, proposals => proposals.AtSubject(NhAssistantConsultationRights.Agent));
        await using var scope = host.Services.CreateAsyncScope();
        var control = scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>();

        var beyond = await control.SetConsultationRoutesAsync(TargetAgentId, new NhAssistantConsultationRouteOverrides(true, null, null), "admin-1");
        Assert.Contains(beyond.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ConsultRouteNotAllowed);

        var switched = await control.SetConsultationRoutesAsync(TargetAgentId, new NhAssistantConsultationRouteOverrides(null, false, null), "admin-1", "Pilot first.");
        Assert.True(switched.Success);
        var routes = switched.Data!.ConsultationRoutes!;
        var agentRights = Assert.Single(routes.Routes, route => route.Route == NhAssistantConsultationRoutes.SubjectAgentRights);
        Assert.True(agentRights.Allowed);
        Assert.False(agentRights.On);
        Assert.Equal(false, agentRights.Override);
        Assert.Equal(["project"], agentRights.SubjectTypes);
        Assert.False(Assert.Single(routes.Routes, route => route.Route == NhAssistantConsultationRoutes.AskerDecides).Allowed);
        Assert.Equal("admin-1", routes.ChangedBy);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentConsultationRoutesChanged
            && evt.ResultCode == "asker-decides=code;subject-agent-rights=off;subject-asker-rights=code");

        // With its only route off the consultation reads only: the change is not even offered.
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);
        await host.SendAsync(conversation.Id, "Activate it.", Member);
        Assert.DoesNotContain(target.Requests[0].Options?.Tools ?? [], tool => tool.Name == ChangeStatus);
        Assert.Empty(host.Tools.StatusChanges);
        Assert.Empty(await host.Services.GetRequiredService<NhAssistantRunStore>()
            .GetConsultationApprovalsAsync((await SingleConsultationAsync(host)).Id, CancellationToken.None));

        // Back to the code setting.
        var reset = await control.SetConsultationRoutesAsync(TargetAgentId, NhAssistantConsultationRouteOverrides.None, "admin-1");
        Assert.True(Assert.Single(reset.Data!.ConsultationRoutes!.Routes, route => route.Route == NhAssistantConsultationRoutes.SubjectAgentRights).On);
    }

    public static TheoryData<AssistantTestProvider, string> ProposingRoutes()
    {
        var data = new TheoryData<AssistantTestProvider, string>();
        foreach (var provider in new[] { AssistantTestProvider.SqlServer, AssistantTestProvider.PostgreSql })
        {
            data.Add(provider, NhAssistantConsultationRoutes.AskerDecides);
            data.Add(provider, NhAssistantConsultationRoutes.SubjectAskerRights);
            data.Add(provider, NhAssistantConsultationRoutes.SubjectAgentRights);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ProposingRoutes))]
    public async Task A_propose_agent_consulted_with_a_route_is_offered_changes_and_each_waits_as_a_proposal_for_its_decision(AssistantTestProvider provider, string route)
    {
        // AC34 (b): an agent with Propose autonomy is offered the tools that change state in a
        // consultation with a proposal route, and its call becomes a proposal on that route; nothing
        // executes before the proposal is decided.
        var projectId = Guid.NewGuid();
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1")
            .RespondWithText("I proposed to activate the project.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ProposingConsultTool, new { input = new { subjectId = ProjectId, question = "Activate it if it is ready." } }, "call-1")
            .RespondWithText("The starter proposed it.");
        Action<NhAssistantConsultationProposalBuilder> proposals = route switch
        {
            NhAssistantConsultationRoutes.AskerDecides => builder => builder.AskerDecides().Policy(AskerPolicy),
            NhAssistantConsultationRoutes.SubjectAskerRights => builder => builder.AtSubject(NhAssistantConsultationRights.Asker),
            _ => builder => builder.AtSubject(NhAssistantConsultationRights.Agent)
        };
        await using var host = await CreateHostAsync(provider, target, asker, proposals, consulted: Proposing(NhAiAutonomyLevel.Propose));
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        var turn = await host.SendAsync(conversation.Id, "Activate the project if it is ready.", Member);

        Assert.Contains(target.Requests[0].Options?.Tools ?? [], tool => tool.Name == ChangeStatus);
        Assert.Empty(host.Tools.StatusChanges);
        var approval = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>()
            .GetConsultationApprovalsAsync((await SingleConsultationAsync(host)).Id, CancellationToken.None));
        Assert.Equal((NhAssistantApprovalStatuses.Pending, TestProjectToolCatalog.ChangeStatus.Id), (approval.Status, approval.ToolId));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRequested
            && evt.ApprovalId == approval.Id
            && evt.ConsultationRoute == route);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalExecuted);

        if (route == NhAssistantConsultationRoutes.AskerDecides)
        {
            // The asker decides it on the card in their own chat; then it executes.
            Assert.Equal(NhAssistantApprovalModes.Inline, approval.Mode);
            var card = turn.Single<NhAssistantApprovalRequiredEvent>().Approval;
            Assert.Equal(approval.Id, card.ApprovalId);
            await host.DecideAsync(conversation.Id, card, approve: true, userId: Member);
        }
        else
        {
            Assert.Equal(NhAssistantApprovalModes.Deferred, approval.Mode);
            Assert.Empty(turn.Events.OfType<NhAssistantApprovalRequiredEvent>());
            if (route == NhAssistantConsultationRoutes.SubjectAskerRights)
            {
                // It waits for the subject's approvers; this application rebuilds nobody's principal.
                Assert.Equal(NhAiGrantSource.Delegated, approval.GrantSource);
                Assert.Equal(NhAssistantApprovalStatuses.Pending, (await StoredAsync(host, approval.Id)).Status);
                return;
            }

            Assert.Equal(NhAiGrantSource.Agent, approval.GrantSource);
            await ApproveAndExecuteAsync(host, approval, Approver);
        }

        Assert.Equal("active", Assert.Single(host.Tools.StatusChanges).Status);
        Assert.Equal(NhAssistantApprovalStatuses.Executed, (await StoredAsync(host, approval.Id)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Explain_agents_consultations_that_only_answer_and_a_propose_agents_own_turns_offer_no_changes(AssistantTestProvider provider)
    {
        // An agent that only explains reads, also in a consultation with a proposal route.
        var explaining = new NhAiScriptedChatClient().RespondWithText("It can start once the budget is approved.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ProposingConsultTool, new { input = new { subjectId = ProjectId, question = "Can it start?" } }, "call-1")
            .RespondWithText("It can start once the budget is approved.");
        await using (var host = await CreateHostAsync(
                         provider,
                         explaining,
                         asker,
                         proposals => proposals.AskerDecides().Policy(AskerPolicy),
                         consulted: Proposing(NhAiAutonomyLevel.Explain)))
        {
            await host.SendAsync((await host.CreateConversationAsync(Member, AskingAgentId)).Id, "Can the project start?", Member);

            var tools = OfferedTools(explaining);
            Assert.Contains(tools, tool => tool.Name == Search);
            Assert.DoesNotContain(tools, tool => tool.Name == ChangeStatus);
        }

        // A consultation that only answers offers a Propose agent no changes, and neither does a turn
        // of its own.
        var proposing = new NhAiScriptedChatClient()
            .RespondWithText("It can start once the budget is approved.")
            .RespondWithText("I can only describe the change.");
        var readOnlyAsker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ProposingConsultTool, new { input = new { subjectId = ProjectId, question = "Can it start?" } }, "call-1")
            .RespondWithText("It can start once the budget is approved.");
        await using (var host = await CreateHostAsync(provider, proposing, readOnlyAsker, null, consulted: Proposing(NhAiAutonomyLevel.Propose)))
        {
            await host.SendAsync((await host.CreateConversationAsync(Member, AskingAgentId)).Id, "Can the project start?", Member);
            await host.SendAsync((await host.CreateConversationAsync(Member, ProposingAgentId)).Id, "Activate the project.", Member);

            Assert.Equal(2, proposing.Requests.Count);
            Assert.All(proposing.Requests, request =>
            {
                Assert.Contains(request.Options?.Tools ?? [], tool => tool.Name == Search);
                Assert.DoesNotContain(request.Options?.Tools ?? [], tool => tool.Name == ChangeStatus);
            });
            Assert.Empty(host.Tools.StatusChanges);
        }

        static IList<AITool> OfferedTools(NhAiScriptedChatClient client)
        {
            return Assert.Single(client.Requests).Options?.Tools ?? [];
        }
    }

    [Fact]
    public void Only_a_consultation_with_a_route_offers_changes_below_execute_autonomy()
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent(TargetAgentId))
            .ConfigureAgent(TargetAgentId, agent => agent.AllowConsultation("project")));
        var state = (NhAssistantRegistrationState)services.Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState)).ImplementationInstance!;
        var options = state.AgentRuns[TargetAgentId].Consultations.Single();
        NhAssistantConsultationScope Consultation(bool mayPropose)
        {
            return new NhAssistantConsultationScope(Guid.NewGuid(), options, [AskingAgentId, TargetAgentId], 1, mayPropose, AskingAgentId, null!);
        }

        foreach (var autonomy in Enum.GetValues<NhAiAutonomyLevel>())
        {
            var agent = AssistantTestData.Agent(TargetAgentId, autonomy: autonomy);

            // The agent's own turns, runs and rules: execute autonomy only, as before.
            Assert.Equal(autonomy == NhAiAutonomyLevel.Execute, NhAssistantTurnRunner.OffersEffects(agent, null));
            // A consultation without a route in effect for the call only answers.
            Assert.False(NhAssistantTurnRunner.OffersEffects(agent, Consultation(mayPropose: false)));
            // With a route every effect is a proposal: Propose, Simulate and Execute are offered them.
            Assert.Equal(autonomy >= NhAiAutonomyLevel.Propose, NhAssistantTurnRunner.OffersEffects(agent, Consultation(mayPropose: true)));
        }
    }

    [Fact]
    public void Startup_refuses_the_agents_rights_where_no_one_but_a_person_answers_or_only_the_asker_approves()
    {
        static NhAssistantRegistrationState State(Action<NhAssistantBuilder> configure)
        {
            var services = new ServiceCollection();
            services.AddNewHeapAssistant(configure);
            return (NhAssistantRegistrationState)services.Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState)).ImplementationInstance!;
        }

        var person = State(assistant => assistant
            .AddAgent(AssistantTestData.Agent(TargetAgentId))
            .ConfigureAgent(TargetAgentId, agent => agent
                .Accountability(NhAiAgentAccountability.SubjectResponsible)
                .AllowConsultation("project", consult => consult.Proposals(proposals => proposals.AtSubject(NhAssistantConsultationRights.Agent)))));
        var initiatorOnly = State(assistant => assistant
            .AddAgent(AssistantTestData.Agent(TargetAgentId))
            .AddApprovalPolicy(AskerPolicy, policy => policy.Approvers(NhAiApprovers.Initiator))
            .ConfigureAgent(TargetAgentId, agent => agent
                .AllowConsultation("project", consult => consult.Proposals(proposals => proposals
                    .AtSubject(NhAssistantConsultationRights.Agent)
                    .Policy(AskerPolicy)))));
        var unknownPolicy = State(assistant => assistant
            .AddAgent(AssistantTestData.Agent(TargetAgentId))
            .ConfigureAgent(TargetAgentId, agent => agent
                .AllowConsultation("project", consult => consult.Proposals(proposals => proposals.AskerDecides().Policy("missing")))));

        Assert.Contains("responsible person", Assert.Throws<InvalidOperationException>(person.ValidateAgentRuns).Message);
        Assert.Contains("only the initiator", Assert.Throws<InvalidOperationException>(initiatorOnly.ValidateAgentRuns).Message);
        Assert.Contains("missing", Assert.Throws<InvalidOperationException>(unknownPolicy.ValidateAgentRuns).Message);
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent(TargetAgentId))
            .ConfigureAgent(TargetAgentId, agent => agent
                .AllowConsultation("project", consult => consult.Proposals(_ => { })))));

        // Proposals(bool) keeps today's route; Proposals(false) has none.
        var options = State(assistant => assistant
                .AddAgent(AssistantTestData.Agent(TargetAgentId))
                .ConfigureAgent(TargetAgentId, agent => agent.AllowConsultation("project")))
            .AgentRuns[TargetAgentId].Consultations.Single();
        Assert.Equal([NhAssistantConsultationRoutes.SubjectAskerRights], options.AllowedRoutes);
        Assert.True(options.AllowProposals);
    }

    private async Task<ProposedOnce> ProposeOnceAsync(
        AssistantTestProvider provider,
        Guid projectId,
        Action<NhAssistantConsultationProposalBuilder> proposals,
        bool grant = true,
        Action<NhAssistantLimits>? limits = null,
        bool proposeTwice = false)
    {
        var target = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "active" } }, "c-1");
        if (proposeTwice)
        {
            target.RespondWithFunctionCall(ChangeStatus, new { input = new { projectId, status = "on-hold" } }, "c-2");
        }
        target.RespondWithText("Done.");
        var asker = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConsultTool, new { input = new { subjectId = ProjectId, question = "Change it." } }, "call-1")
            .RespondWithText("Answered.");
        await using var host = await CreateHostAsync(provider, target, asker, proposals, grant: grant, limits: limits);
        var conversation = await host.CreateConversationAsync(Member, AskingAgentId);

        await host.SendAsync(conversation.Id, "Change the project.", Member);

        var consultation = await SingleConsultationAsync(host);
        var approval = (await host.Services.GetRequiredService<NhAssistantRunStore>().GetConsultationApprovalsAsync(consultation.Id, CancellationToken.None))
            .FirstOrDefault();
        var inner = string.Join("\n", target.FunctionResults.Select(result => result.Result?.ToString()));
        var refusal = host.Business.Events.FirstOrDefault(evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked
            && evt.ResultCode == NhAssistantErrorCodes.ConsultProposalRefused);
        return new ProposedOnce(approval, inner, refusal);
    }

    private sealed record ProposedOnce(AssistantApproval? Approval, string InnerResult, NhAssistantAuditEvent? Refusal);

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient target,
        NhAiScriptedChatClient asker,
        Action<NhAssistantConsultationProposalBuilder>? proposals,
        bool grant = true,
        Action<NhAssistantAgentRunBuilder>? agent = null,
        Action<NhAssistantLimits>? limits = null,
        RecordingChannel? channel = null,
        TimeProvider? clock = null,
        NhAssistantAgentDefinition? consulted = null)
    {
        // The consulted agent is the host's project assistant unless the test brings its own; without
        // proposals the consultation only answers.
        var consultedId = consulted?.Id ?? TargetAgentId;
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            target,
            limits,
            configure: services =>
            {
                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.AddSingleton(channel ?? new RecordingChannel());
                // The application never rebuilds a person's principal: every request for one is counted.
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
            assistantBuilder: assistant =>
            {
                assistant
                    .AddAgent(AssistantTestData.Agent(AskingAgentId) with
                    {
                        ProfileName = "asking-chat",
                        ToolSelectors = ["assistant.consult.*"]
                    })
                    .AddSubjectType<TestSubjectResolver>()
                    .AddAgentNotificationChannel<RecordingChannelAdapter>()
                    .AddApprovalPolicy(AskerPolicy, policy => policy
                        .Approvers(NhAiApprovers.Initiator, NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                        .Lifetime(TimeSpan.FromDays(1)))
                    .AddApprovalPolicy(FourEyesPolicy, policy => policy
                        .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                        .FourEyes())
                    .AddApprovalPolicy(ApproverPolicy, policy => policy
                        .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)));
                if (consulted is not null)
                {
                    assistant.AddAgent(consulted);
                }

                assistant.ConfigureAgent(consultedId, run =>
                {
                    if (grant)
                    {
                        run.Grant("projects.*");
                    }
                    agent?.Invoke(run);
                    run.AllowConsultation("project", consult =>
                    {
                        consult.Description("Knows the intake review of a project.");
                        if (proposals is null)
                        {
                            consult.Proposals(false);
                        }
                        else
                        {
                            consult.Proposals(proposals);
                        }
                    });
                });
            });
    }

    /// <summary>
    /// A consulted agent next to the host's project assistant, with the autonomy the test gives it.
    /// </summary>
    private static NhAssistantAgentDefinition Proposing(NhAiAutonomyLevel autonomy)
    {
        return AssistantTestData.Agent(ProposingAgentId, autonomy: autonomy);
    }

    private static async Task ApproveAndExecuteAsync(AssistantTestHost host, AssistantApproval approval, string approver)
    {
        Assert.True(await host.Services.GetRequiredService<NhAssistantRunStore>().TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Approved,
            new NhAssistantApprovalTransition
            {
                DecidedByActorId = approver,
                DecidedAt = host.Services.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow,
                ApprovalExpiresAt = approval.ExpiresAt
            },
            CancellationToken.None));
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var result = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalExecution>().ExecuteAsync(
            approval.Id,
            Substitute.For<INhBackgroundOperationContext>(),
            fenced: false,
            CancellationToken.None);
        Assert.True(result.Success);
    }

    private static async Task<AssistantApproval> StoredAsync(AssistantTestHost host, Guid approvalId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
    }

    private static async Task<AssistantConsultation> SingleConsultationAsync(AssistantTestHost host)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Consultations.SingleAsync();
    }

    private static ClaimsPrincipal Principal(string actorId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"));
    }
}

/// <summary>
/// An application that never rebuilds a person's principal for a subject's work outside that person's
/// request: it refuses those and counts who was asked for. It still resolves its people for the whole
/// application, the question the assistant asks before it notifies someone, so notifications reach them.
/// </summary>
public sealed class CountingPrincipals : INhAiPrincipalFactory
{
    public ConcurrentQueue<string> People { get; } = new();

    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        if (NhAiSystemSubjects.IsGlobal(subject.Subject))
        {
            return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test")));
        }

        People.Enqueue(actorId);
        return ValueTask.FromResult<ClaimsPrincipal?>(null);
    }
}
