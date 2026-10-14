using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Proposal guards and revalidation: open proposals close as stale once the work was done another
/// way, approvals of such proposals are refused, execution checks again, waiting runs continue, and
/// people and the next run learn why, on both relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantProposalRevalidationTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Approver = "approver-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Revalidation_closes_only_proposals_that_no_longer_fit_and_a_second_call_changes_nothing(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, ProposeStatus(projectId, "Active"));
        var run = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));

        // The guard named the object the proposal changes, within the thread's scope.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var key = await context.ApprovalKeys.SingleAsync(item => item.ApprovalId == approval.Id);
            Assert.Equal(run.Thread.ScopeKey, key.ScopeKey);
            Assert.Equal(ProjectStatusGuard.KeyType, key.KeyType);
            Assert.Equal(projectId.ToString(), key.KeyId);
        }

        var unchanged = await RevalidateSubjectAsync(host, projectId);
        Assert.Empty(unchanged.Invalidated);
        Assert.Equal(1, unchanged.Checked);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await FindAsync(host, approval.Id)).Status);

        // A person activates the project by hand.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAiSubjectAccess>().RefreshAsync(Subject(projectId))).Success);
        }
        var live = host.Services.GetRequiredService<RecordedLiveUpdates>();
        live.Clear();
        States(host).Statuses[projectId] = "Active";
        var closed = await RevalidateSubjectAsync(host, projectId);

        var invalidated = Assert.Single(closed.Invalidated);
        Assert.Equal(approval.Id, invalidated.ApprovalId);
        Assert.Equal(NhAssistantStaleKinds.AlreadyDone, invalidated.Kind);
        Assert.Equal(ProjectStatusGuard.ReasonCode, invalidated.ReasonCode);
        var stale = await FindAsync(host, approval.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Stale, stale.Status);
        Assert.Equal(NhAssistantStaleKinds.AlreadyDone, stale.StaleKind);
        Assert.Equal(ProjectStatusGuard.ReasonCode, stale.ExecutionResultCode);
        Assert.Null(stale.DecidedByActorId);
        Assert.NotNull(stale.DecidedAt);
        Assert.Null(stale.ExecutedAt);
        Assert.Equal("Already set to Active by Henk.", NhAssistantStaleExplanations.Pick(stale.StaleExplanationsJson, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("Al op Active gezet door Henk.", NhAssistantStaleExplanations.Pick(stale.StaleExplanationsJson, CultureInfo.GetCultureInfo("nl-NL")));
        var invocation = await host.Services.GetRequiredService<INhAssistantStore>()
            .FindToolInvocationAsync(approval.ToolInvocationId, CancellationToken.None);
        Assert.Equal(NhAssistantToolCallStatuses.NotExecuted, invocation!.Status);
        Assert.Empty(host.Tools.StatusChanges);

        // The people on the project learn at once that the proposal closed, without content.
        var update = Assert.Single(live.Sent, sent => sent.Event is NhAssistantLiveAgentWorkChanged);
        var work = (NhAssistantLiveAgentWorkChanged)update.Event;
        Assert.Equal(NhAssistantAgentWorkReasons.Proposal, work.Reason);
        Assert.Equal(approval.Id, work.ApprovalId);
        Assert.Contains(Approver, update.ActorIds);

        // The audit event names the stage, kind and reason, never the explanation.
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalInvalidated);
        Assert.Equal(approval.Id, audit.ApprovalId);
        Assert.Equal(NhAssistantInvalidationStages.Revalidation, audit.ObjectId);
        Assert.Equal($"{NhAssistantStaleKinds.AlreadyDone}:{ProjectStatusGuard.ReasonCode}", audit.ResultCode);
        Assert.Equal(NhAssistantProposalInvalidation.SystemActorId, audit.ActorId);
        Assert.DoesNotContain("Henk", JsonSerializer.Serialize(audit));

        // People read the kind and the explanation in their own language.
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            var dto = NhAssistantAgentRunDtoMapper.ToDto(stale, run.Thread);
            Assert.Equal(NhAssistantStaleKinds.AlreadyDone, dto.StaleKind);
            Assert.Equal("Al op Active gezet door Henk.", dto.StaleExplanation);
            Assert.Equal(ProjectStatusGuard.ReasonCode, dto.ExecutionResultCode);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        var again = await RevalidateSubjectAsync(host, projectId);
        Assert.Empty(again.Invalidated);
        Assert.Equal(0, again.Checked);
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalInvalidated);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Revalidation_by_object_finds_proposals_of_another_subjects_thread_but_never_in_another_scope(AssistantTestProvider provider)
    {
        var threadProject = Guid.NewGuid();
        var changedProject = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, ProposeStatus(changedProject, "Closed"));
        var run = await StartRunAsync(host, threadProject);
        await RunTurnAsync(host, run, "Close the related project.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        States(host).Statuses[changedProject] = "Closed";
        var key = new NhAiProposalKey(ProjectStatusGuard.KeyType, changedProject.ToString());

        var otherScope = await RevalidateKeysAsync(host, [key], NhAiSubjectInfo.CreateScopeKey(null, Guid.NewGuid()));
        var otherObject = await RevalidateKeysAsync(host, [new NhAiProposalKey(ProjectStatusGuard.KeyType, threadProject.ToString())], null);
        var bySubject = await RevalidateSubjectAsync(host, changedProject);

        Assert.Equal(0, otherScope.Checked);
        Assert.Equal(0, otherObject.Checked);
        Assert.Equal(0, bySubject.Checked);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await FindAsync(host, approval.Id)).Status);

        // Without a scope key, revalidation uses the scope of subjects without tenant or division.
        var byObject = await RevalidateKeysAsync(host, [key], null);
        Assert.Equal(approval.Id, Assert.Single(byObject.Invalidated).ApprovalId);
        Assert.Equal(NhAssistantApprovalStatuses.Stale, (await FindAsync(host, approval.Id)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Approving_a_proposal_that_no_longer_fits_is_refused_and_a_failing_guard_keeps_it_open(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-1")
            .RespondWithText("I proposed to activate the project.")
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "OnHold" } }, "call-2")
            .RespondWithText("I proposed to hold the project.");
        await using var host = await CreateHostAsync(provider, model);
        var first = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, first, "Project PRJ-1 was created.");
        var activate = Assert.Single(await RunStore(host).GetRunApprovalsAsync(first.Run.Id, CancellationToken.None));

        // A guard that fails refuses the approval for now and closes nothing.
        States(host).Throws = true;
        var failed = await DecideAsync(host, activate.Id, approve: true);
        Assert.Equal(NhAssistantErrorCodes.ProposalCheckFailed, Code(failed));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await FindAsync(host, activate.Id)).Status);
        var failedRevalidation = await RevalidateSubjectAsync(host, projectId);
        Assert.Equal(1, failedRevalidation.Failed);
        Assert.Empty(failedRevalidation.Invalidated);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await FindAsync(host, activate.Id)).Status);
        States(host).Throws = false;

        // A person did the work: the approval is refused and the proposal closes.
        States(host).Statuses[projectId] = "Active";
        var refused = await DecideAsync(host, activate.Id, approve: true);
        Assert.Equal(NhAssistantErrorCodes.ProposalStale, Code(refused));
        var stale = await FindAsync(host, activate.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Stale, stale.Status);
        Assert.Null(stale.DecidedByActorId);
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalInvalidated);
        Assert.Equal(NhAssistantInvalidationStages.Decision, audit.ObjectId);
        Assert.Equal(Approver, audit.ActorId);
        Assert.Empty(host.Tools.StatusChanges);

        // A proposal that still fits is approved as before, and a rejection needs no check.
        await RunStore(host).TrySetRunStatusAsync(first.Run.Id, [NhAssistantRunStatuses.Queued], NhAssistantRunStatuses.Completed, CancellationToken.None);
        var second = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, second, "The project changed.");
        var hold = Assert.Single(await RunStore(host).GetRunApprovalsAsync(second.Run.Id, CancellationToken.None));
        Assert.True((await DecideAsync(host, hold.Id, approve: true)).Success);
        Assert.Equal(NhAssistantApprovalStatuses.Approved, (await FindAsync(host, hold.Id)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_decision_racing_a_revalidation_ends_in_exactly_one_terminal_state(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, ProposeStatus(projectId, "Active"));
        var run = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        States(host).Statuses[projectId] = "Active";

        var decision = DecideAsync(host, approval.Id, approve: true);
        var revalidation = RevalidateSubjectAsync(host, projectId);
        await Task.WhenAll(decision, revalidation);

        Assert.False((await decision).Success);
        Assert.Equal(NhAssistantApprovalStatuses.Stale, (await FindAsync(host, approval.Id)).Status);
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalInvalidated);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Guards_run_only_for_their_tools_and_a_guard_without_revalidation_leaves_open_proposals_to_the_decision(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(
            provider,
            ProposeStatus(projectId, "Active"),
            stages: NhAiProposalGuardStages.Decision | NhAiProposalGuardStages.Execution);
        var run = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        States(host).Statuses[projectId] = "Active";

        // Keys are named for every guard of the tool, whatever its stages.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            Assert.True(await context.ApprovalKeys.AnyAsync(item => item.ApprovalId == approval.Id));
        }

        // Only the second guard runs at revalidation, and it finds nothing; the guard that knows the
        // work is done waits for the decision.
        var revalidated = await RevalidateSubjectAsync(host, projectId);
        Assert.Equal(1, revalidated.Checked);
        Assert.Empty(revalidated.Invalidated);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await FindAsync(host, approval.Id)).Status);

        Assert.Equal(NhAssistantErrorCodes.ProposalStale, Code(await DecideAsync(host, approval.Id, approve: true)));
        Assert.Equal(NhAssistantApprovalStatuses.Stale, (await FindAsync(host, approval.Id)).Status);

        // The second guard of the tool ran too; the guard of another tool never did.
        Assert.True(States(host).SecondGuardChecks > 0);
        Assert.Equal(0, States(host).OtherToolGuardCalls);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_waiting_inline_run_is_woken_and_continues_with_not_executed(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Closed" } }, "call-close")
            .RespondWithText("Someone already closed the project.");
        await using var host = await CreateHostAsync(provider, model, NhAiApprovalMode.Inline);
        var run = await StartRunAsync(host, projectId);
        var waiting = await RunTurnAsync(host, run, "Close project PRJ-1.");
        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, waiting.Outcome);
        var approvalId = waiting.PendingApprovalId!.Value;

        States(host).Statuses[projectId] = "Closed";
        var closed = await RevalidateKeysAsync(host, [new NhAiProposalKey(ProjectStatusGuard.KeyType, projectId.ToString())], null);

        Assert.Equal(approvalId, Assert.Single(closed.Invalidated).ApprovalId);
        Assert.Equal(approvalId, Assert.Single(host.Services.GetRequiredService<RecordingWaker>().Woken));

        // The background operation then resumes the turn, which tells the model why.
        NhAssistantAgentTurnResult resumed;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            resumed = await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().ResumeAgentTurnAsync(
                new NhAssistantAgentResumeRequest(run.AgentTurn, approvalId),
                CancellationToken.None);
        }

        Assert.Equal(NhAssistantTurnStatuses.Completed, resumed.Outcome);
        Assert.Empty(host.Tools.StatusChanges);
        var result = JsonSerializer.Serialize(model.FunctionResults[^1].Result);
        Assert.Contains(NhAssistantErrorCodes.ProposalStale, result);
        Assert.Contains(NhAssistantStaleKinds.AlreadyDone, result);
        Assert.Contains("Henk", result);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(run.Thread.ConversationId, "assistant-agent:" + AgentId)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Execution_checks_the_guards_again_before_an_approved_proposal_runs(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, ProposeStatus(projectId, "Active"));
        var run = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.True((await DecideAsync(host, approval.Id, approve: true)).Success);

        // The decision won; the work is done by hand before the execution starts.
        States(host).Statuses[projectId] = "Active";
        await ExecuteAsync(host, approval.Id);

        var stale = await FindAsync(host, approval.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Stale, stale.Status);
        Assert.Equal(NhAssistantStaleKinds.AlreadyDone, stale.StaleKind);
        Assert.NotNull(stale.ExecutedAt);
        Assert.Equal(Approver, stale.DecidedByActorId);
        Assert.Empty(host.Tools.StatusChanges);
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalInvalidated);
        Assert.Equal(NhAssistantInvalidationStages.Execution, audit.ObjectId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_failing_guard_at_execution_keeps_the_proposal_approved_for_a_later_attempt(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, ProposeStatus(projectId, "Active"));
        var run = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.True((await DecideAsync(host, approval.Id, approve: true)).Success);

        States(host).Throws = true;
        await ExecuteAsync(host, approval.Id);

        var kept = await FindAsync(host, approval.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Approved, kept.Status);
        Assert.Null(kept.ExecutionOperationId);
        Assert.Equal(1, kept.ExecutionAttempts);
        Assert.Empty(host.Tools.StatusChanges);

        States(host).Throws = false;
        await ExecuteAsync(host, approval.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Executed, (await FindAsync(host, approval.Id)).Status);
        Assert.Single(host.Tools.StatusChanges);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_published_event_queues_a_revalidation_that_closes_the_proposal(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, ProposeStatus(projectId, "Active"));
        var run = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        States(host).Statuses[projectId] = "Active";

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
                .PublishAsync(new ProjectStatusSet(projectId, "Active"));
        }

        // The revalidation is a background operation, so it runs only once the publisher committed.
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await FindAsync(host, approval.Id)).Status);
        var operations = host.Services.GetRequiredService<RevalidationOperations>();
        var (request, options) = Assert.Single(operations.Revalidations);
        Assert.Equal(NhAssistantProposalInvalidation.SystemActorId, options.OwnerAgentKey);
        Assert.Null(request.Subject);
        Assert.Equal(new NhAiProposalKey(ProjectStatusGuard.KeyType, projectId.ToString()), Assert.Single(request.Keys));
        Assert.Equal(run.Thread.ScopeKey, request.ScopeKey);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var handler = ActivatorUtilities.CreateInstance<NhAssistantRevalidateProposalsOperation>(scope.ServiceProvider);
            var handled = await handler.ExecuteAsync(request, Substitute.For<INhBackgroundOperationContext>(), CancellationToken.None);
            Assert.True(handled.Success);
        }

        Assert.Equal(NhAssistantApprovalStatuses.Stale, (await FindAsync(host, approval.Id)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_report_counts_invalidations_apart_and_the_next_run_knows_them(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.")
            .RespondWithText("The project is already active.");
        await using var host = await CreateHostAsync(provider, model);
        var first = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, first, "Project PRJ-1 was created.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(first.Run.Id, CancellationToken.None));
        States(host).Statuses[projectId] = "Active";
        await RevalidateSubjectAsync(host, projectId);

        var aggregates = await host.Services.GetRequiredService<NhAssistantProposalFeedbackStore>().GetAggregatesAsync(
            AgentId,
            new NhAssistantFeedbackFilter(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(1), null, null, null, null),
            CancellationToken.None);
        var invalidation = Assert.Single(aggregates.Invalidations);
        Assert.Equal(NhAssistantStaleKinds.AlreadyDone, invalidation.Kind);
        Assert.Equal(ProjectStatusGuard.ReasonCode, invalidation.ReasonCode);
        Assert.False(invalidation.AfterApproval);
        Assert.Equal(1, invalidation.Count);
        Assert.Empty(aggregates.Categories);

        await RunStore(host).TrySetRunStatusAsync(first.Run.Id, [NhAssistantRunStatuses.Queued], NhAssistantRunStatuses.Completed, CancellationToken.None);
        var second = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, second, "The project changed.");

        var continuity = model.Requests[^1].Messages
            .Where(message => message.Role == ChatRole.System)
            .Select(message => message.Text)
            .Single(text => text.Contains("<agent-continuity>", StringComparison.Ordinal));
        Assert.Contains("<invalidated-proposals>", continuity);
        Assert.Contains(approval.ToolId, continuity);
        Assert.Contains(NhAssistantStaleKinds.AlreadyDone, continuity);
        Assert.Contains("Henk", continuity);
        Assert.DoesNotContain("<rejected-proposals>", continuity);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_persons_own_chat_proposal_is_checked_when_they_approve_it(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("The project is now active.");
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton<TestProjectStates>();
                ConfigureCultures(services);
            },
            assistantBuilder: assistant => assistant.AddProposalGuard<ProjectStatusGuard>("projects.change-status"));
        var conversation = await host.CreateConversationAsync();
        var paused = await host.SendAsync(conversation.Id, "Activate the project.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;

        // A chat proposal waits in the conversation, so it has no keys and revalidation leaves it alone.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            Assert.False(await context.ApprovalKeys.AnyAsync(item => item.ApprovalId == approval.ApprovalId));
        }

        States(host).Statuses[projectId] = "Active";
        var refused = await host.DecideAsync(conversation.Id, approval, approve: true);

        Assert.Equal(NhAssistantErrorCodes.ProposalStale, refused.StartCode);
        Assert.Empty(host.Tools.StatusChanges);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(conversation.Id)).Status);
        var view = await host.ReadViewAsync(conversation.Id);
        var part = view.Messages
            .SelectMany(message => message.Parts)
            .OfType<NhAssistantApprovalView>()
            .Single(item => item.ApprovalId == approval.ApprovalId);
        Assert.Equal(NhAssistantApprovalStatuses.Stale, part.Status);
        Assert.Equal(NhAssistantStaleKinds.AlreadyDone, part.StaleKind);
        Assert.NotNull(part.StaleExplanation);
        var invocation = view.Messages
            .SelectMany(message => message.Parts)
            .OfType<NhAssistantToolCallPartView>()
            .Single(item => item.ToolId == approval.ToolId);
        Assert.Equal(NhAssistantToolCallStatuses.NotExecuted, invocation.Status);

        // The person can talk to the assistant again.
        Assert.Null((await host.SendAsync(conversation.Id, "Thanks.")).StartCode);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        NhAiApprovalMode mode = NhAiApprovalMode.Deferred,
        NhAiProposalGuardStages stages = NhAiProposalGuardStages.All)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestProjectStates>();
                services.AddSingleton<RecordingWaker>();
                services.Replace(ServiceDescriptor.Scoped<INhAssistantInlineRunWaker>(
                    provider => provider.GetRequiredService<RecordingWaker>()));
                services.AddSingleton<RecordedLiveUpdates>();
                services.Replace(ServiceDescriptor.Singleton<INhAssistantLiveUpdateTransport>(
                    provider => provider.GetRequiredService<RecordedLiveUpdates>()));
                services.AddSingleton<RevalidationOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RevalidationOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                ConfigureCultures(services);
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddProposalGuard<ProjectStatusGuard>(["projects.change-status"], stages)
                .AddProposalGuard<SecondProjectGuard>("projects.*")
                .AddProposalGuard<OtherToolGuard>("billing.*")
                .AddProposalRevalidation<ProjectStatusSet>(revalidation => revalidation
                    .Keys(evt => [new NhAiProposalKey(ProjectStatusGuard.KeyType, evt.ProjectId.ToString())])
                    .ScopeKey(_ => NhAiSubjectInfo.CreateScopeKey(null, null)))
                .AddApprovalPolicy("project-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(mode)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")));
    }

    private static void ConfigureCultures(IServiceCollection services)
    {
        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.DefaultRequestCulture = new RequestCulture("en");
            options.SupportedCultures = [CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("nl")];
            options.SupportedUICultures = [CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("nl")];
        });
    }

    private static NhAiScriptedChatClient ProposeStatus(Guid projectId, string status)
    {
        return new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status } }, "call-status")
            .RespondWithText("I proposed a status.");
    }

    private sealed record StartedRun(AssistantThread Thread, AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    private static async Task<StartedRun> StartRunAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(Subject(projectId), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "project-created",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var agentTurn = new NhAssistantAgentTurnContext(thread, info, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false);
        return new StartedRun(thread, run, agentTurn);
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, StartedRun run, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(run.AgentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message)]),
            CancellationToken.None);
    }

    private static async Task<NhAssistantRevalidationResult> RevalidateSubjectAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantProposalRevalidation>()
            .RevalidateSubjectAsync(Subject(projectId));
    }

    private static async Task<NhAssistantRevalidationResult> RevalidateKeysAsync(
        AssistantTestHost host,
        IReadOnlyCollection<NhAiProposalKey> keys,
        string? scopeKey)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantProposalRevalidation>()
            .RevalidateKeysAsync(keys, scopeKey);
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
        var result = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalExecutor>().ExecuteAsync(
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

    private static TestProjectStates States(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<TestProjectStates>();
    }

    private static string? Code(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
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

public sealed record ProjectStatusSet(Guid ProjectId, string Status);

/// <summary>
/// The project statuses people set by hand, and switches to make the guards fail.
/// </summary>
public sealed class TestProjectStates
{
    private int _secondGuardChecks;
    private int _otherToolGuardCalls;

    public ConcurrentDictionary<Guid, string> Statuses { get; } = new();

    public bool Throws { get; set; }

    public int SecondGuardChecks => _secondGuardChecks;

    public int OtherToolGuardCalls => _otherToolGuardCalls;

    public void CountSecondGuardCheck() => Interlocked.Increment(ref _secondGuardChecks);

    public void CountOtherToolGuardCall() => Interlocked.Increment(ref _otherToolGuardCalls);
}

/// <summary>
/// A status proposal is done once the project already has the proposed status.
/// </summary>
public sealed class ProjectStatusGuard(TestProjectStates states) : INhAiProposalGuard
{
    public const string KeyType = "project-status";
    public const string ReasonCode = "status-already-set";

    public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        var (projectId, _) = Read(context.Arguments);
        return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>([new NhAiProposalKey(KeyType, projectId.ToString())]);
    }

    public ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        if (states.Throws)
        {
            throw new InvalidOperationException("The project register is unavailable.");
        }

        var (projectId, status) = Read(context.Arguments);
        return ValueTask.FromResult(
            states.Statuses.TryGetValue(projectId, out var current) && string.Equals(current, status, StringComparison.OrdinalIgnoreCase)
                ? NhAiPreconditionResult.AlreadyDone(
                    ReasonCode,
                    culture => culture.TwoLetterISOLanguageName == "nl"
                        ? $"Al op {status} gezet door Henk."
                        : $"Already set to {status} by Henk.")
                : NhAiPreconditionResult.Satisfied);
    }

    internal static (Guid ProjectId, string Status) Read(JsonElement arguments)
    {
        var input = arguments.TryGetProperty("input", out var nested) ? nested : arguments;
        var projectId = (input.TryGetProperty("projectId", out var id) ? id : input.GetProperty("ProjectId")).GetGuid();
        var status = (input.TryGetProperty("status", out var value) ? value : input.GetProperty("Status")).GetString()!;
        return (projectId, status);
    }
}

/// <summary>
/// A second guard of every project tool; it never finds a proposal stale.
/// </summary>
public sealed class SecondProjectGuard(TestProjectStates states) : INhAiProposalGuard
{
    public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>([]);
    }

    public ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        states.CountSecondGuardCheck();
        return ValueTask.FromResult(NhAiPreconditionResult.Satisfied);
    }
}

/// <summary>
/// A guard of another tool, which must never run for project proposals.
/// </summary>
public sealed class OtherToolGuard(TestProjectStates states) : INhAiProposalGuard
{
    public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        states.CountOtherToolGuardCall();
        return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>([]);
    }

    public ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        states.CountOtherToolGuardCall();
        return ValueTask.FromResult(NhAiPreconditionResult.NotApplicable("other-tool"));
    }
}

internal sealed class RecordingWaker : INhAssistantInlineRunWaker
{
    public ConcurrentQueue<Guid> Woken { get; } = new();

    public Task WakeAsync(AssistantApproval approval, CancellationToken cancellationToken)
    {
        Woken.Enqueue(approval.Id);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A background-operation service that records queued revalidations and accepts executions.
/// </summary>
public sealed class RevalidationOperations
{
    public RevalidationOperations()
    {
        Service = Substitute.For<INhBackgroundOperationService>();
        Service.EnqueueAsync(Arg.Any<NhAssistantRevalidateProposalsRequest>(), Arg.Any<NhBackgroundOperationEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Revalidations.Enqueue((call.ArgAt<NhAssistantRevalidateProposalsRequest>(0), call.ArgAt<NhBackgroundOperationEnqueueOptions>(1)));
                return Task.FromResult(TaskResult<NhBackgroundOperationViewModel>.Succeeded(new NhBackgroundOperationViewModel { Id = Guid.NewGuid() }));
            });
        Service.EnqueueAsync(Arg.Any<NhAssistantExecuteProposalRequest>(), Arg.Any<NhBackgroundOperationEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TaskResult<NhBackgroundOperationViewModel>.Succeeded(new NhBackgroundOperationViewModel { Id = Guid.NewGuid() })));
    }

    public INhBackgroundOperationService Service { get; }

    public ConcurrentQueue<(NhAssistantRevalidateProposalsRequest Request, NhBackgroundOperationEnqueueOptions Options)> Revalidations { get; } = new();
}
