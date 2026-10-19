using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Resources;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using System.Security.Claims;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Live;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AI;
using SampleProjectManagement.Api.Controllers;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.AI.AspNet.Mvc;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Events;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.Core.Utilities;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using NSubstitute;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-268 to SPM-271: the intake reviewer starts on its own when a project is created, answers
/// for its runs itself and records proposals that people on the project decide. Runs and approved
/// proposals execute as background operations through Hangfire, as in Program.cs.
/// </summary>
public sealed class AssistantAgentRunSamplesTests(AssistantAgentRunSampleHost host)
    : IClassFixture<AssistantAgentRunSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How the planner's shadow variant describes its deadline skill to triage (SPM-288).
    /// </summary>
    private const string VariantDeadlineSkill = "only when less than a third";

    [Fact]
    public async Task A_created_project_starts_the_intake_reviewer_whose_proposal_a_second_approver_executes()
    {
        // SPM-268
        var project = await host.CreateProjectAsync("INTAKE", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);

        var receipts = await host.PublishProjectCreatedAsync(project, createdBy: host.OwnerId);

        var receipt = Assert.Single(receipts);
        Assert.Equal(SampleIntakeReviewerComposition.TriggerId, receipt.TriggerId);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
        var conversationId = receipt.ConversationId!.Value;
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());

        var proposal = await WaitForProposalAsync(owner, conversationId, NhAssistantApprovalStatuses.Pending);
        Assert.Equal(ProjectAiTools.ChangeStatusToolId, proposal.ToolId);
        Assert.Equal(NhAssistantApprovalModes.Deferred, proposal.Mode);
        Assert.Equal(SampleIntakeReviewerComposition.ApprovalPolicyId, proposal.PolicyId);
        Assert.Equal("agent", proposal.GrantSource);
        Assert.Equal(receipt.RunId, proposal.RunId);
        // Approvers read the application's localized explanation instead of the tool input.
        var resources = new ResourceManager(typeof(SampleAssistantToolPresenter));
        var presentation = Assert.IsType<NhAssistantApprovalPresentationDto>(proposal.Presentation);
        Assert.Contains(
            presentation.ToolDisplayName,
            new[] { "en-US", "nl-NL" }.Select(culture =>
                resources.GetString("ToolProjectStatusChange", CultureInfo.GetCultureInfo(culture))));
        Assert.DoesNotContain("ApprovalProjectStatusSummary", presentation.Summary);
        Assert.Equal(3, presentation.Fields.Count);
        // The proposal is recorded during the run's turn; the run ends after the model's answer.
        var run = Assert.Single(await WaitForRunCountAsync(owner, conversationId, 1));
        Assert.Equal(NhAssistantRunStatuses.Completed, run.Status);
        Assert.Equal(1, run.ProposalCount);
        Assert.Equal(SampleIntakeReviewerComposition.AgentId, proposal.AgentId);

        // The run's background operation belongs to the agent, not to the person who created the
        // project, and nothing changed before an approver decided.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
            var operation = await dbContext.Set<NhBackgroundOperation>()
                .AsNoTracking()
                .SingleAsync(item => item.Id == run.BackgroundOperationId);
            Assert.Equal(NhAssistantAgentRunOperationTypes.Run, operation.OperationType);
            Assert.Equal(NhBackgroundOperationOwnerKind.Agent, operation.OwnerKind);
            Assert.Null(operation.OwnerUserId);
            Assert.Equal(ProjectSubjects.Type, operation.DomainObjectType);
            Assert.Equal(project.ToString("D"), operation.DomainObjectId);
        }
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == project);

        // People outside the project do not see the thread; the creator may not approve.
        var hidden = await outsider.GetAsync($"/api/assistant/threads/{conversationId}/proposals");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var ownDecision = await DecideAsync(owner, proposal, "approve");
        Assert.Equal(HttpStatusCode.Forbidden, ownDecision.StatusCode);
        Assert.Contains(NhAssistantErrorCodes.FourEyesRequired, await ownDecision.Content.ReadAsStringAsync());

        // SPM-277: the assistant panel reads from the thread itself who decides each proposal: the
        // second approver may, the creator is kept out by four-eyes, and the trigger's message
        // reads as an event instead of a person.
        var colleagueThread = await ReadConversationAsync(colleague, conversationId);
        Assert.Equal(NhAssistantConversationKinds.Thread, colleagueThread.Kind);
        Assert.Equal("approver", colleagueThread.Thread!.Role);
        var colleagueRight = Assert.Single(colleagueThread.Thread.Decisions);
        Assert.Equal(proposal.ApprovalId, colleagueRight.ApprovalId);
        Assert.Equal(proposal.RunId, colleagueRight.RunId);
        Assert.True(colleagueRight.CanDecide);
        var ownerRight = Assert.Single((await ReadConversationAsync(owner, conversationId)).Thread!.Decisions);
        Assert.False(ownerRight.CanDecide);
        Assert.Equal(NhAssistantErrorCodes.FourEyesRequired, ownerRight.ReasonCode);
        var trigger = colleagueThread.Messages.First(message => message.Role == NhAssistantMessageRoles.User);
        Assert.Equal(NhAssistantMessageAuthorKinds.Trigger, trigger.AuthorKind);

        var inbox = await colleague.GetFromJsonAsync<NhAssistantInboxDto>("/api/assistant/approvals/inbox", Json);
        var waiting = Assert.Single(inbox!.Items, item => item.ApprovalId == proposal.ApprovalId);
        // SPM-301: the inbox and the conversation name the project type and link to the project.
        Assert.Equal("Project", waiting.SubjectTypeName);
        Assert.Equal($"/management/intake-reviews?project={project:D}", waiting.SubjectLink);
        Assert.Equal(waiting.SubjectLink, colleagueThread.Thread.SubjectLink);
        var decision = await DecideAsync(colleague, proposal, "approve");
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);

        var executed = await WaitForProposalAsync(owner, conversationId, NhAssistantApprovalStatuses.Executed);
        Assert.Equal(host.ColleagueId.ToString(), executed.DecidedByActorId);
        Assert.Contains(host.Projects.StatusChanges, change => change.ProjectId == project && change.Status == ProjectStatus.Active);
        Assert.Empty((await ReadConversationAsync(colleague, conversationId)).Thread!.Decisions);
    }

    [Fact]
    public async Task A_redelivered_project_event_starts_one_run()
    {
        // SPM-269
        var project = await host.CreateProjectAsync("REDELIVER", ownerId: host.OwnerId);
        var evt = new ProjectCreatedEvent { ProjectId = project, ProjectKey = "REDELIVER", CreatedByUserId = host.OwnerId };

        // The outbox delivers at least once: the sample's CAP consumer receives the event twice.
        await host.DeliverAsync(evt);
        await host.DeliverAsync(evt);
        var third = Assert.Single(await host.PublishAsync(evt));

        Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, third.Outcome);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var lookup = await owner.GetFromJsonAsync<NhAssistantThreadLookupDto>(
            $"/api/assistant/threads?agentId={SampleIntakeReviewerComposition.AgentId}&subjectType={ProjectSubjects.Type}&subjectId={project:D}",
            Json);
        Assert.Equal("approver", lookup!.Role);
        var run = Assert.Single(await WaitForRunCountAsync(owner, lookup.Thread!.ConversationId, 1));
        Assert.Equal(third.RunId, run.Id);
        Assert.Contains(host.Services.GetRequiredService<SampleEventLog>().Events, item => item.EventId == evt.EventId);
    }

    [Fact]
    public async Task Project_roles_decide_what_members_may_do_and_a_removed_member_loses_access_at_once()
    {
        // SPM-270
        var project = await host.CreateProjectAsync("ROLES", ownerId: host.OwnerId);
        var readerMembership = await host.AddMemberAsync(project, host.ReaderId, ProjectMemberRole.Reader);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        var receipt = Assert.Single(await host.PublishProjectCreatedAsync(project, createdBy: null));
        var conversationId = receipt.ConversationId!.Value;
        using var reader = host.CreateClient(host.ReaderId.ToString());
        using var participant = host.CreateClient(host.ParticipantId.ToString());

        var proposal = await WaitForProposalAsync(reader, conversationId, NhAssistantApprovalStatuses.Pending);
        var lookup = await reader.GetFromJsonAsync<NhAssistantThreadLookupDto>(
            $"/api/assistant/threads?agentId={SampleIntakeReviewerComposition.AgentId}&subjectType={ProjectSubjects.Type}&subjectId={project:D}",
            Json);
        Assert.Equal("reader", lookup!.Role);
        Assert.Equal(conversationId, lookup.Thread!.ConversationId);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(reader, proposal, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(participant, proposal, "approve")).StatusCode);

        // Duplicate members and people from another division are refused.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var members = scope.ServiceProvider.GetRequiredService<ProjectMemberService>();
            var duplicate = await members.CreateAsync(new ProjectMemberMutateModel
            {
                ProjectId = project,
                UserId = host.ReaderId,
                Role = ProjectMemberRole.Approver
            });
            var outsider = await members.CreateAsync(new ProjectMemberMutateModel
            {
                ProjectId = project,
                UserId = host.OutsiderId,
                Role = ProjectMemberRole.Reader
            });
            Assert.False(duplicate.Success);
            Assert.False(outsider.Success);
        }

        await host.RemoveMemberAsync(readerMembership);

        var afterRemoval = await reader.GetAsync($"/api/assistant/threads/{conversationId}/proposals");
        Assert.Equal(HttpStatusCode.NotFound, afterRemoval.StatusCode);
    }

    [Fact]
    public async Task An_approved_proposal_for_a_project_edited_since_closes_as_stale()
    {
        // SPM-271
        var project = await host.CreateProjectAsync("STALE", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        var receipt = Assert.Single(await host.PublishProjectCreatedAsync(project, createdBy: host.OwnerId));
        var conversationId = receipt.ConversationId!.Value;
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        var proposal = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Pending);

        await host.TouchProjectAsync(project);
        var decision = await DecideAsync(colleague, proposal, "approve");
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);

        var stale = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Stale);
        Assert.Equal(proposal.ApprovalId, stale.ApprovalId);
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == project);
    }

    [Fact]
    public async Task Proposals_that_people_overtake_by_hand_close_on_their_own_with_the_reason()
    {
        // SPM-282
        var project = await host.CreateProjectAsync("OVERTAKEN", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        var taskId = await host.CreateTaskAsync(project, "Collect the signed charter");
        var receipt = Assert.Single(await host.PublishProjectCreatedAsync(project, createdBy: host.OwnerId));
        var conversationId = receipt.ConversationId!.Value;
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        colleague.DefaultRequestHeaders.AcceptLanguage.Clear();
        colleague.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var statusProposal = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Pending, ProjectAiTools.ChangeStatusToolId);

        // The owner asks the reviewer to tick off the task; the proposal waits for the colleague.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var started = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().StartAsync(
                SampleIntakeReviewerComposition.AgentId,
                ProjectSubjects.For(project),
                host.OwnerId.ToString("D"),
                $"Complete task {taskId:D} of project {project:D}.");
            Assert.NotEqual(NhAssistantTriggerOutcomes.Rejected, started.Outcome);
        }
        var taskProposal = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Pending, ProjectTaskAiTools.CompleteToolId);
        Assert.Contains("Collect the signed charter", taskProposal.Presentation!.Summary);

        // The colleague ticks the task off by hand. CAP delivers the update once the task's
        // transaction committed; the declared revalidation finds the proposal by its task and
        // closes it, with who did the work, in each reader's language.
        var completed = await host.CompleteTaskByHandAsync(taskId, host.ColleagueId);
        Assert.Equal(host.DivisionId, completed.DivisionId);
        await host.DeliverAsync(completed);
        var closed = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Stale, ProjectTaskAiTools.CompleteToolId);
        Assert.Equal(taskProposal.ApprovalId, closed.ApprovalId);
        Assert.Equal(NhAssistantStaleKinds.AlreadyDone, closed.StaleKind);
        Assert.Equal(ProjectTaskProposalGuard.CompletedCode, closed.ExecutionResultCode);
        Assert.StartsWith("Already completed by colleague@sample.localhost", closed.StaleExplanation);
        using (var dutchColleague = host.CreateClient(host.ColleagueId.ToString()))
        {
            dutchColleague.DefaultRequestHeaders.AcceptLanguage.Clear();
            dutchColleague.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");
            var dutch = Assert.Single(
                (await dutchColleague.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json))!,
                item => item.ApprovalId == taskProposal.ApprovalId);
            Assert.StartsWith("Al afgevinkt door colleague@sample.localhost", dutch.StaleExplanation);
        }

        // A client that hears about the change reads the proposal by its id, not through the
        // thread, and a list refreshes several proposals with one lookup, each with the reader's
        // own right to decide it. People outside the project learn nothing about them.
        var single = await colleague.GetFromJsonAsync<NhAssistantSubjectProposalDto>($"/api/assistant/approvals/{taskProposal.ApprovalId}", Json);
        Assert.Equal(NhAssistantApprovalStatuses.Stale, single!.Proposal.Status);
        Assert.StartsWith("Already completed by colleague@sample.localhost", single.Proposal.StaleExplanation);
        Assert.Equal(host.ColleagueId.ToString(), single.Proposal.StaleByActorId);
        Assert.False(single.CanDecide);
        var looked = await colleague.PostAsJsonAsync(
            "/api/assistant/approvals/lookup",
            new NhAssistantProposalLookupRequest([statusProposal.ApprovalId, taskProposal.ApprovalId]),
            Json);
        var lookup = (await looked.Content.ReadFromJsonAsync<NhAssistantProposalLookupDto>(Json))!;
        Assert.Equal([statusProposal.ApprovalId, taskProposal.ApprovalId], lookup.Items.Select(item => item.Proposal.ApprovalId));
        Assert.True(lookup.Items[0].CanDecide);
        using (var owner = host.CreateClient(host.OwnerId.ToString()))
        {
            var ownRight = Assert.Single(
                (await (await owner.PostAsJsonAsync("/api/assistant/approvals/lookup", new NhAssistantProposalLookupRequest([statusProposal.ApprovalId]), Json))
                    .Content.ReadFromJsonAsync<NhAssistantProposalLookupDto>(Json))!.Items);
            Assert.False(ownRight.CanDecide);
            Assert.Equal(NhAssistantErrorCodes.FourEyesRequired, ownRight.ReasonCode);
        }
        using (var outsider = host.CreateClient(host.OutsiderId.ToString()))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/assistant/approvals/{taskProposal.ApprovalId}")).StatusCode);
            var nothing = await outsider.PostAsJsonAsync(
                "/api/assistant/approvals/lookup",
                new NhAssistantProposalLookupRequest([statusProposal.ApprovalId, taskProposal.ApprovalId]),
                Json);
            Assert.Empty((await nothing.Content.ReadFromJsonAsync<NhAssistantProposalLookupDto>(Json))!.Items);
        }

        // Approving it afterwards is refused, and nothing ran.
        var refused = await DecideAsync(colleague, taskProposal, "approve");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var task = await scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>().ProjectTasks
                .AsNoTracking()
                .SingleAsync(item => item.Id == taskId);
            Assert.Equal(host.ColleagueId, task.CompletedByUserId);
        }

        // Setting the status by hand closes the status proposal through subject revalidation.
        var statusChanged = await host.SetStatusByHandAsync(project, ProjectStatus.Active, host.ColleagueId);
        await host.DeliverAsync(statusChanged);
        var statusClosed = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Stale, ProjectAiTools.ChangeStatusToolId);
        Assert.Equal(statusProposal.ApprovalId, statusClosed.ApprovalId);
        Assert.Equal(ProjectStatusProposalGuard.StatusSetCode, statusClosed.ExecutionResultCode);
        Assert.Equal("The project is already Active.", statusClosed.StaleExplanation);
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == project);
        Assert.Contains(host.Services.GetRequiredService<SampleAssistantAuditLog>().Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalInvalidated
            && evt.ApprovalId == taskProposal.ApprovalId
            && evt.ObjectId == NhAssistantInvalidationStages.Revalidation);
    }

    [Fact]
    public async Task A_run_waiting_for_a_proposal_that_a_person_overtook_continues_without_it()
    {
        // SPM-282: the status advisor's run waits for the participant's inline approval. Nobody
        // decides; the owner sets the status by hand, and the platform closes the proposal and
        // wakes the waiting run itself.
        var project = await host.CreateProjectAsync("WAITING", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        var started = await participant.PostAsJsonAsync(
            "/api/assistant/threads/start",
            new NhAssistantStartAgentRequest(
                SampleStatusAdvisorComposition.AgentId,
                ProjectSubjects.Type,
                project.ToString("D"),
                $"Set project {project:D} to onhold."),
            Json);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var conversationId = (await started.Content.ReadFromJsonAsync<NhAssistantTriggerReceiptDto>(Json))!.ConversationId!.Value;
        var proposal = await WaitForProposalAsync(participant, conversationId, NhAssistantApprovalStatuses.Pending);
        Assert.Equal(NhAssistantApprovalModes.Inline, proposal.Mode);
        var waiting = await WaitForRunAsync(participant, conversationId, NhAssistantRunStatuses.WaitingForApproval);
        await host.WaitForOperationAsync(waiting.BackgroundOperationId!.Value, NhBackgroundOperationStatus.WaitingForSignal);

        var changed = await host.SetStatusByHandAsync(project, ProjectStatus.OnHold, host.OwnerId);
        await host.DeliverAsync(changed);

        var closed = await WaitForProposalAsync(participant, conversationId, NhAssistantApprovalStatuses.Stale);
        Assert.Equal(proposal.ApprovalId, closed.ApprovalId);
        Assert.Equal(NhAssistantStaleKinds.AlreadyDone, closed.StaleKind);
        var completed = await WaitForRunAsync(participant, conversationId, NhAssistantRunStatuses.Completed);
        Assert.Equal(waiting.Id, completed.Id);
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == project);
    }

    [Fact]
    public async Task The_project_closer_proposes_by_rule_once_stays_quiet_after_a_rejection_and_explains_why()
    {
        // SPM-283
        var project = await host.CreateProjectAsync("CLOSER", ownerId: host.OwnerId, status: ProjectStatus.Active);
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        var documentation = await host.CreateTaskAsync(project, "Hand over the documentation");
        var budget = await host.CreateTaskAsync(project, "Close the budget");
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        colleague.DefaultRequestHeaders.AcceptLanguage.Clear();
        colleague.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        using var participant = host.CreateClient(host.ParticipantId.ToString());

        // A task is completed while another one is open: the rules look and propose nothing.
        await host.DeliverAsync(await host.CompleteTaskByHandAsync(documentation, host.ParticipantId));
        var conversationId = await WaitForThreadAsync(colleague, project, SampleProjectCloserComposition.AgentId);
        var quiet = Assert.Single(await WaitForRunCountAsync(colleague, conversationId, 1));
        Assert.Equal(NhAssistantRunKinds.Rule, quiet.Kind);
        Assert.Equal(0, quiet.ProposalCount);

        // The last task completes the work. The rules propose to complete the project without a
        // model and without tokens; approvers read the rules' reason in their own language.
        await host.DeliverAsync(await host.CompleteTaskByHandAsync(budget, host.ParticipantId));
        var proposal = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Pending);
        Assert.Equal(ProjectAiTools.ChangeStatusToolId, proposal.ToolId);
        Assert.Equal(NhAssistantApprovalModes.Deferred, proposal.Mode);
        Assert.Equal(SampleProjectCloserComposition.ApprovalPolicyId, proposal.PolicyId);
        Assert.Equal("All 2 tasks of the project are completed.", proposal.Rationale);
        var proposing = Assert.Single(await WaitForRunCountAsync(colleague, conversationId, 2), run => run.Id == proposal.RunId);
        Assert.Equal(NhAssistantRunKinds.Rule, proposing.Kind);
        Assert.Equal(0, proposing.InputTokens + proposing.OutputTokens);
        Assert.Equal(1, proposing.ProposalCount);
        var thread = await ReadConversationAsync(colleague, conversationId);
        var ruleMessage = Assert.Single(
            thread.Messages,
            message => message.Parts.OfType<NhAssistantApprovalPartDto>().Any(part => part.ApprovalId == proposal.ApprovalId));
        Assert.Equal(NhAssistantMessageAuthorKinds.Rule, ruleMessage.AuthorKind);
        Assert.Null(ruleMessage.AuthorActorId);
        Assert.Equal("All 2 tasks of the project are completed.", ruleMessage.Parts.OfType<NhAssistantApprovalPartDto>().Single().Rationale);

        // The same task saved again does not add a second proposal.
        await host.DeliverAsync(await host.CompleteTaskByHandAsync(budget, host.ParticipantId));
        await WaitForRunCountAsync(colleague, conversationId, 3);
        var proposals = await colleague.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json);
        Assert.Equal(proposal.ApprovalId, Assert.Single(proposals!).ApprovalId);
        Assert.Contains(host.Services.GetRequiredService<SampleAssistantAuditLog>().Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalDeduplicated
            && evt.ApprovalId == proposal.ApprovalId);

        // A participant asks the closer why. A person's question goes to the agent's model, which
        // answers from the reason the rules wrote; headless runs write in the application's default
        // language, Dutch.
        var dutch = CultureInfo.GetCultureInfo("nl-NL");
        var reason = string.Format(
            dutch,
            new ResourceManager(typeof(SampleAssistantToolPresenter)).GetString("CloserReason", dutch)!,
            2);
        Assert.Contains("- " + reason, TextOf(ruleMessage));
        await host.SendAsync(participant, conversationId, "Why do you propose to complete this project?");
        var answer = (await ReadConversationAsync(participant, conversationId)).Messages
            .Last(message => message.Role == NhAssistantMessageRoles.Assistant);
        Assert.NotEqual(NhAssistantMessageAuthorKinds.Rule, answer.AuthorKind);
        Assert.Equal("My rules proposed it because: " + reason, TextOf(answer));

        // The approver rejects it; however often tasks change, the rules stay quiet for the cooldown.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(colleague, proposal, "reject")).StatusCode);
        await host.DeliverAsync(await host.CompleteTaskByHandAsync(documentation, host.ParticipantId));
        var runs = await WaitForRunCountAsync(colleague, conversationId, 4);
        Assert.All(runs, run => Assert.Equal(NhAssistantRunKinds.Rule, run.Kind));
        proposals = await colleague.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json);
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, Assert.Single(proposals!).Status);
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == project);
    }

    [Fact]
    public async Task An_operator_puts_the_project_closer_and_a_planner_skill_in_shadow_while_the_team_evaluates_them()
    {
        // SPM-293
        const string closer = SampleProjectCloserComposition.AgentId;
        const string planner = SampleProjectPlannerComposition.AgentId;
        const string skill = SampleProjectPlannerComposition.TaskFollowUpSkill;
        var evaluated = await host.CreateProjectAsync("SHADOW", ownerId: host.OwnerId, status: ProjectStatus.Active);
        var live = await host.CreateProjectAsync("LIVE", ownerId: host.OwnerId, status: ProjectStatus.Active);
        await host.AddMemberAsync(evaluated, host.ColleagueId, ProjectMemberRole.Approver);
        await host.AddMemberAsync(live, host.ColleagueId, ProjectMemberRole.Approver);
        var report = await host.CreateTaskAsync(evaluated, "Write the report");
        var handover = await host.CreateTaskAsync(live, "Hand the project over");
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        try
        {
            // While the team evaluates the closer's rules and the planner's follow-ups, the operator
            // puts the closer and the planner's task follow-up skill in shadow.
            var closerState = await ChangeShadowAsync(closer, SampleShadowChange.Shadow, "Evaluate the closing rules");
            Assert.True(closerState.Shadow!.Shadow);
            Assert.True(closerState.Shadow.Override);
            var skillState = await ChangeSkillShadowAsync(planner, skill, SampleShadowChange.Shadow, "Evaluate the follow-ups");
            Assert.True(skillState.Shadow.Shadow);
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(report, host.ParticipantId));

            // The closer proposes in its shadow thread, which only administrators see; the proposal
            // never executes and the approver hears nothing of it.
            var closerShadow = await WaitForShadowThreadAsync(administrator, evaluated, closer);
            var shadowProposal = await WaitForProposalAsync(administrator, closerShadow, NhAssistantApprovalStatuses.Shadow);
            Assert.Equal(ProjectAiTools.ChangeStatusToolId, shadowProposal.ToolId);
            var plannerShadow = await WaitForShadowThreadAsync(administrator, evaluated, planner);
            var plannerRun = Assert.Single(await WaitForRunCountAsync(administrator, plannerShadow, 1));
            Assert.True(plannerRun.Shadow);
            Assert.Equal(skill, plannerRun.Skill);
            var memberWork = (await colleague.GetFromJsonAsync<NhAssistantSubjectWorkDto>($"/api/assistant/subjects/project/{evaluated:D}/work", Json))!;
            Assert.All(memberWork.Agents, agent => Assert.Null(agent.ShadowThread));
            Assert.DoesNotContain(memberWork.Agents, agent => agent.AgentId == closer && agent.Thread is not null);
            Assert.DoesNotContain(memberWork.OpenProposals, proposal => proposal.Proposal.ConversationId == closerShadow);
            Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync($"/api/assistant/conversations/{closerShadow}")).StatusCode);
            Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == evaluated);

            // The rules convinced the team: the operator lets the code decide again, and the closer
            // proposes live, in the live thread the approver decides in.
            var liveState = await ChangeShadowAsync(closer, SampleShadowChange.Code, "The closing rules are good");
            Assert.False(liveState.Shadow!.Shadow);
            Assert.Null(liveState.Shadow.Override);
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(handover, host.ParticipantId));
            var liveThread = await WaitForThreadAsync(colleague, live, closer);
            var liveProposal = await WaitForProposalAsync(colleague, liveThread, NhAssistantApprovalStatuses.Pending);
            Assert.Equal(ProjectAiTools.ChangeStatusToolId, liveProposal.ToolId);
        }
        finally
        {
            // Other samples share the host, its closer and its planner.
            await ChangeShadowAsync(closer, SampleShadowChange.Code, null);
            await ChangeSkillShadowAsync(planner, skill, SampleShadowChange.Code, null);
        }
    }

    [Fact]
    public async Task An_operator_replaces_the_technical_account_of_the_project_closer_for_its_new_runs()
    {
        // SPM-294
        const string closer = SampleProjectCloserComposition.AgentId;
        var configured = host.TechnicalAccountId.ToString("D");
        var operations = host.OperationsAccountId.ToString("D");
        var before = await host.CreateProjectAsync("ACCOUNT", ownerId: host.OwnerId, status: ProjectStatus.Active);
        var after = await host.CreateProjectAsync("ACCOUNT", ownerId: host.OwnerId, status: ProjectStatus.Active);
        await host.AddMemberAsync(before, host.ColleagueId, ProjectMemberRole.Approver);
        await host.AddMemberAsync(after, host.ColleagueId, ProjectMemberRole.Approver);
        var first = await host.CreateTaskAsync(before, "Close the books");
        var second = await host.CreateTaskAsync(after, "Close the books");
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        colleague.DefaultRequestHeaders.AcceptLanguage.Clear();
        colleague.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        try
        {
            // The closer answers to the technical account in the application's configuration.
            var initial = (await AgentStateAsync(closer)).ServiceAccount!;
            Assert.Equal(configured, initial.ActorId);
            Assert.Equal(configured, initial.ConfiguredActorId);
            Assert.False(initial.Overridden);
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(first, host.ParticipantId));
            var beforeThread = await WaitForThreadAsync(colleague, before, closer);
            var beforeProposal = await WaitForProposalAsync(colleague, beforeThread, NhAssistantApprovalStatuses.Pending);
            Assert.Equal(configured, Assert.Single(await RunsAsync(colleague, beforeThread)).AccountableOwnerId);

            // An account that does not exist is refused; the operations account takes over.
            var unknown = await ChangeServiceAccountAsync(closer, Guid.NewGuid(), "A typo");
            Assert.Contains(unknown.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ServiceAccountNotFound);
            var changed = await ChangeServiceAccountAsync(closer, host.OperationsAccountId, "The operations account takes over");
            Assert.True(changed.Success);
            Assert.Equal(operations, changed.Data!.ServiceAccount!.ActorId);
            Assert.True(changed.Data.ServiceAccount.Overridden);
            Assert.Equal("The operations account takes over", changed.Data.ServiceAccount.Reason);

            // New runs answer to the operations account; the earlier run keeps its account, and its
            // proposal still executes after the approval.
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(second, host.ParticipantId));
            var afterThread = await WaitForThreadAsync(colleague, after, closer);
            await WaitForProposalAsync(colleague, afterThread, NhAssistantApprovalStatuses.Pending);
            Assert.Equal(operations, Assert.Single(await RunsAsync(colleague, afterThread)).AccountableOwnerId);
            Assert.Equal(configured, Assert.Single(await RunsAsync(colleague, beforeThread)).AccountableOwnerId);
            Assert.Equal(HttpStatusCode.OK, (await DecideAsync(colleague, beforeProposal, "approve")).StatusCode);
            await WaitForProposalAsync(colleague, beforeThread, NhAssistantApprovalStatuses.Executed);
            Assert.Contains(host.Projects.StatusChanges, change => change.ProjectId == before && change.Status == ProjectStatus.Completed);
        }
        finally
        {
            var back = await ChangeServiceAccountAsync(closer, null, null);
            Assert.Equal(configured, back.Data!.ServiceAccount!.ActorId);
        }
    }

    [Fact]
    public async Task An_administrator_adds_a_progress_note_to_the_planner_without_a_deploy_and_puts_it_live_after_shadow()
    {
        // SPM-295
        const string planner = SampleProjectPlannerComposition.AgentId;
        const string note = SampleAgentConfigurationService.ProgressNoteSkill;
        var evaluated = await host.CreateProjectAsync("NOTE-SHADOW", ownerId: host.OwnerId, status: ProjectStatus.Active);
        var live = await host.CreateProjectAsync("NOTE-LIVE", ownerId: host.OwnerId, status: ProjectStatus.Active);
        await host.AddMemberAsync(live, host.ColleagueId, ProjectMemberRole.Approver);
        var draft = await host.CreateTaskAsync(evaluated, "Draft the plan");
        await host.CreateTaskAsync(evaluated, "Review the plan");
        var handover = await host.CreateTaskAsync(live, "Hand the plan over");
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        try
        {
            // The application imports the progress note: a skill and its trigger, made of building
            // blocks the planner's composition registered. Both start in shadow.
            Assert.True((await ConfigureAgentsAsync(service => service.AddProgressNoteAsync(host.OwnerId, CancellationToken.None))).Success);
            var configured = (await administrator.GetFromJsonAsync<NhAssistantAgentConfigurationDto>($"/api/assistant/admin/agents/{planner}/configuration", Json))!;
            var skill = Assert.Single(configured.DataSkills);
            // Versions continue after an earlier note was removed; the live one is in effect.
            Assert.Equal((note, SampleProjectPlannerComposition.ProgressNoteRules, true), (skill.SkillId, skill.RuleHandler, skill.InEffect));
            var trigger = Assert.Single(configured.DataTriggers);
            Assert.Equal((SampleProjectPlannerComposition.TaskCompletedEvent, note, true), (trigger.Event, trigger.SkillId, trigger.Shadow));
            Assert.True(Assert.Single((await AgentStateAsync(planner)).Skills, state => state.SkillId == note).Shadow.Shadow);

            // A completed task starts the note in the planner's shadow thread, which only
            // administrators see; the planner's own follow-up stays live.
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(draft, host.ParticipantId));
            var shadowThread = await WaitForShadowThreadAsync(administrator, evaluated, planner);
            var shadowNote = await WaitForRunWithTriggerAsync(administrator, shadowThread, SampleAgentConfigurationService.ProgressNoteTriggerId);
            Assert.Equal((NhAssistantRunStatuses.Completed, note, true, NhAssistantRunKinds.Rule), (shadowNote.Status, shadowNote.Skill, shadowNote.Shadow, shadowNote.Kind));
            Assert.Contains("1", shadowNote.Summary);

            // The team agrees with the notes: the operator puts the trigger and the skill live, and
            // the next completed task gets its note in the live thread after the follow-up.
            Assert.True((await ConfigureAgentsAsync(service => service.PutProgressNoteLiveAsync(host.OwnerId, "The notes are right", CancellationToken.None))).Success);
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(handover, host.ParticipantId));
            var liveThread = await WaitForThreadAsync(colleague, live, planner);
            var liveNote = await WaitForRunWithTriggerAsync(colleague, liveThread, SampleAgentConfigurationService.ProgressNoteTriggerId);
            Assert.Equal((NhAssistantRunStatuses.Completed, note, false), (liveNote.Status, liveNote.Skill, liveNote.Shadow));
        }
        finally
        {
            // Other samples share the host and its planner.
            Assert.True((await ConfigureAgentsAsync(service => service.RemoveProgressNoteAsync(host.OwnerId, CancellationToken.None))).Success);
        }
    }

    [Fact]
    public async Task A_changed_progress_note_is_evaluated_in_shadow_next_to_the_live_note_and_promoted()
    {
        // SPM-297
        const string planner = SampleProjectPlannerComposition.AgentId;
        const string trigger = SampleAgentConfigurationService.ProgressNoteTriggerId;
        var project = await host.CreateProjectAsync("NOTE-CANDIDATE", ownerId: host.OwnerId, status: ProjectStatus.Active);
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        var first = await host.CreateTaskAsync(project, "Draft the plan");
        var second = await host.CreateTaskAsync(project, "Review the plan");
        await host.CreateTaskAsync(project, "Hand the plan over");
        await host.CreateTaskAsync(project, "Close the plan");
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        try
        {
            // The progress note is live; its change is saved for shadow as the note's candidate.
            Assert.True((await ConfigureAgentsAsync(service => service.AddProgressNoteAsync(host.OwnerId, CancellationToken.None))).Success);
            Assert.True((await ConfigureAgentsAsync(service => service.PutProgressNoteLiveAsync(host.OwnerId, "The notes are right", CancellationToken.None))).Success);
            var candidate = await ConfigureAgentsAsync(service => service.EvaluateProgressPercentageAsync(host.OwnerId, CancellationToken.None));
            Assert.True(candidate.Success);
            var live = candidate.Data!.Version - 1;

            // One completed task: the live note in the live thread, the candidate in the shadow thread.
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(first, host.ParticipantId));
            var liveThread = await WaitForThreadAsync(colleague, project, planner);
            var liveNote = await WaitForRunWithTriggerAsync(colleague, liveThread, trigger);
            Assert.Equal((live, false, false), (liveNote.SkillVersion, liveNote.SkillCandidate, liveNote.Shadow));
            Assert.DoesNotContain("%", liveNote.Summary);
            var shadowThread = await WaitForShadowThreadAsync(administrator, project, planner);
            var candidateNote = await WaitForRunWithTriggerAsync(administrator, shadowThread, trigger);
            Assert.Equal((candidate.Data.Version, true, true), (candidateNote.SkillVersion, candidateNote.SkillCandidate, candidateNote.Shadow));
            Assert.Contains("25%", candidateNote.Summary);

            var comparison = (await administrator.GetFromJsonAsync<NhAssistantSkillComparisonDto>(
                $"/api/assistant/admin/agents/{planner}/skills/{SampleAgentConfigurationService.ProgressNoteSkill}/candidate/comparison", Json))!;
            Assert.Equal((1, 1, 1), (comparison.Live.Runs, comparison.Candidate.Runs, comparison.PairedEvents));

            // Promoted, the candidate notes the next task live.
            var promoted = await ConfigureAgentsAsync(service => service.PromoteProgressNoteAsync(host.OwnerId, "Percentages read better", CancellationToken.None));
            Assert.True(promoted.Success);
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(second, host.ParticipantId));
            var runs = await WaitForRunCountAsync(colleague, liveThread, 4);
            var promotedNote = runs.Where(run => run.TriggerId == trigger).OrderBy(run => run.CreatedAt).Last();
            Assert.Equal((candidate.Data.Version, false), (promotedNote.SkillVersion, promotedNote.SkillCandidate));
            Assert.Contains("50%", promotedNote.Summary);
        }
        finally
        {
            Assert.True((await ConfigureAgentsAsync(service => service.RemoveProgressNoteAsync(host.OwnerId, CancellationToken.None))).Success);
        }
    }

    [Fact]
    public async Task An_administrator_creates_a_project_starter_that_proposes_with_the_rights_of_its_technical_account()
    {
        // SPM-296
        const string starter = "sample-project-starter";
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        using var created = await administrator.PostAsJsonAsync("/api/assistant/admin/agents", new
        {
            id = starter,
            displayName = "Project starter",
            description = "Starts projects that were created as drafts.",
            instructions = "Explain why you proposed to start the project.",
            toolSelectors = new[] { ProjectAiTools.ChangeStatusToolId },
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = "execute",
            isEnabled = true
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        try
        {
            // The agent answers to the operations account and works by the kickoff rules; it has no
            // grants, so it proposes with that account's rights.
            Assert.True((await ConfigureAgentsAsync(service => service.ConfigureStarterAsync(
                starter, host.OperationsAccountId, live: true, host.OwnerId, CancellationToken.None))).Success);
            var configured = (await administrator.GetFromJsonAsync<NhAssistantAgentConfigurationDto>($"/api/assistant/admin/agents/{starter}/configuration", Json))!;
            Assert.Equal((NhAssistantAgentSources.Admin, NhAssistantAccountabilities.ServiceAccount, host.OperationsAccountId.ToString("D")),
                (configured.Source, configured.RunSettings!.Accountability, configured.RunSettings.ServiceAccountActorId));
            Assert.False(Assert.Single(configured.DataTriggers).Shadow);

            var project = await host.CreateProjectAsync("STARTER", ownerId: host.OwnerId);
            await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
            var receipt = Assert.Single(
                await host.PublishAsync(new ProjectCreatedEvent { ProjectId = project, ProjectKey = "STARTER", CreatedByUserId = host.OwnerId }),
                item => item.TriggerId == SampleAgentConfigurationService.StarterTriggerId);
            Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);

            using var colleague = host.CreateClient(host.ColleagueId.ToString());
            var proposal = await WaitForProposalAsync(colleague, receipt.ConversationId!.Value, NhAssistantApprovalStatuses.Pending);
            Assert.Equal(ProjectAiTools.ChangeStatusToolId, proposal.ToolId);
            var run = Assert.Single(await WaitForRunCountAsync(colleague, receipt.ConversationId.Value, 1));
            Assert.Equal((host.OperationsAccountId.ToString("D"), NhAssistantRunKinds.Rule), (run.AccountableOwnerId, run.Kind));
            var conversation = await ReadConversationAsync(colleague, receipt.ConversationId.Value);
            var call = conversation.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartDto>().Single();
            Assert.Equal("accountable", call.GrantSource);
        }
        finally
        {
            Assert.True((await ConfigureAgentsAsync(service => service.RemoveStarterAsync(starter, host.OwnerId, CancellationToken.None))).Success);
            using var deleted = await administrator.DeleteAsync($"/api/assistant/admin/agents/{starter}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    [Fact]
    public async Task A_runtime_agent_created_as_data_starts_projects_without_being_offered_in_the_chat()
    {
        // SPM-298
        const string starter = SampleRuntimeAgentSeeder.AgentId;
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        try
        {
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.True((await SampleRuntimeAgentSeeder.SeedAsync(scope.ServiceProvider, host.OperationsAccountId, host.OwnerId, CancellationToken.None)).Success);
                // A second start finds the agent and changes nothing.
                Assert.True((await SampleRuntimeAgentSeeder.SeedAsync(scope.ServiceProvider, host.OperationsAccountId, host.OwnerId, CancellationToken.None)).Success);
            }

            var configured = (await administrator.GetFromJsonAsync<NhAssistantAgentConfigurationDto>($"/api/assistant/admin/agents/{starter}/configuration", Json))!;
            Assert.Equal((NhAssistantAgentSources.Admin, NhAssistantAccountabilities.ServiceAccount, host.OperationsAccountId.ToString("D")),
                (configured.Source, configured.RunSettings!.Accountability, configured.RunSettings.ServiceAccountActorId));
            Assert.Equal(SampleRuntimeAgentSeeder.KickoffSkill, Assert.Single(configured.DataSkills).SkillId);
            Assert.False(Assert.Single(configured.DataTriggers).Shadow);
            var agents = (await administrator.GetFromJsonAsync<JsonElement>("/api/assistant/admin/agents", Json)).EnumerateArray();
            Assert.False(agents.Single(agent => agent.GetProperty("id").GetString() == starter).GetProperty("offeredInChat").GetBoolean());

            // People do not find it in the chat.
            var status = await colleague.GetFromJsonAsync<JsonElement>("/api/assistant/status", Json);
            Assert.DoesNotContain(status.GetProperty("agents").EnumerateArray(), agent => agent.GetProperty("id").GetString() == starter);
            using var chat = await colleague.PostAsJsonAsync("/api/assistant/conversations", new { agentId = starter });
            Assert.Equal(HttpStatusCode.NotFound, chat.StatusCode);

            // A draft project gets the kickoff proposal, with the operations account's rights.
            var project = await host.CreateProjectAsync("RUNTIME", ownerId: host.OwnerId);
            await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
            var receipt = Assert.Single(
                await host.PublishAsync(new ProjectCreatedEvent { ProjectId = project, ProjectKey = "RUNTIME", CreatedByUserId = host.OwnerId }),
                item => item.TriggerId == SampleRuntimeAgentSeeder.TriggerId);
            Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
            var proposal = await WaitForProposalAsync(colleague, receipt.ConversationId!.Value, NhAssistantApprovalStatuses.Pending);
            Assert.Equal(ProjectAiTools.ChangeStatusToolId, proposal.ToolId);
            var run = Assert.Single(await WaitForRunCountAsync(colleague, receipt.ConversationId.Value, 1));
            Assert.Equal((host.OperationsAccountId.ToString("D"), SampleRuntimeAgentSeeder.KickoffSkill),
                (run.AccountableOwnerId, run.Skill));
        }
        finally
        {
            await using var scope = host.Services.CreateAsyncScope();
            var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
            var actor = host.OwnerId.ToString("D");
            await configuration.RemoveTriggerAsync(starter, SampleRuntimeAgentSeeder.TriggerId, null, actor, cancellationToken: CancellationToken.None);
            await configuration.RemoveSkillAsync(starter, SampleRuntimeAgentSeeder.KickoffSkill, null, actor, cancellationToken: CancellationToken.None);
            await configuration.RemoveRunSettingsAsync(starter, null, actor, cancellationToken: CancellationToken.None);
            using var deleted = await administrator.DeleteAsync($"/api/assistant/admin/agents/{starter}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    [Fact]
    public async Task An_operator_pauses_the_project_closer_for_an_import_and_stops_it_without_losing_approved_work()
    {
        // SPM-284
        const string closer = SampleProjectCloserComposition.AgentId;
        var first = await host.CreateProjectAsync("IMPORT", ownerId: host.OwnerId, status: ProjectStatus.Active);
        var second = await host.CreateProjectAsync("IMPORT", ownerId: host.OwnerId, status: ProjectStatus.Active);
        await host.AddMemberAsync(first, host.ColleagueId, ProjectMemberRole.Approver);
        await host.AddMemberAsync(second, host.ColleagueId, ProjectMemberRole.Approver);
        Guid[] imported =
        [
            await host.CreateTaskAsync(first, "Import the planning"),
            await host.CreateTaskAsync(first, "Import the budget"),
            await host.CreateTaskAsync(second, "Import the contacts")
        ];
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        colleague.DefaultRequestHeaders.AcceptLanguage.Clear();
        colleague.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        try
        {
            // Before the import the operator pauses the closer. The import completes three tasks of
            // two projects; their events wait in the projects' threads instead of starting runs.
            var paused = await ChangeAgentAsync(closer, SampleAgentStateChange.Pause, "Task import");
            Assert.Equal(NhAssistantAgentRunStates.Paused, paused.RunState);
            Assert.Equal("Task import", paused.Reason);
            foreach (var task in imported)
            {
                await host.DeliverAsync(await host.CompleteTaskByHandAsync(task, host.ParticipantId));
            }

            var firstThread = await WaitForThreadAsync(colleague, first, closer);
            var secondThread = await WaitForThreadAsync(colleague, second, closer);
            Assert.True((await AgentStateAsync(closer)).WaitingInputs >= 3);
            Assert.Empty(await RunsAsync(colleague, firstThread));
            Assert.Empty(await RunsAsync(colleague, secondThread));

            // Resumed, each project gets one run that sees all its completed tasks.
            var resumed = await ChangeAgentAsync(closer, SampleAgentStateChange.Resume, "Import finished");
            Assert.True(resumed.StartedRuns >= 2);
            var firstProposal = await WaitForProposalAsync(colleague, firstThread, NhAssistantApprovalStatuses.Pending);
            var secondProposal = await WaitForProposalAsync(colleague, secondThread, NhAssistantApprovalStatuses.Pending);
            Assert.Single(await WaitForRunCountAsync(colleague, firstThread, 1));
            Assert.Single(await WaitForRunCountAsync(colleague, secondThread, 1));
            Assert.Equal("All 2 tasks of the project are completed.", firstProposal.Rationale);
            Assert.Equal("All 1 tasks of the project are completed.", secondProposal.Rationale);

            // The operator stops the closer. The approver still approves the first proposal, but
            // its execution waits: nothing is queued while the closer is stopped.
            var stopped = await ChangeAgentAsync(closer, SampleAgentStateChange.Stop, "Check the closer's proposals");
            Assert.Equal(NhAssistantAgentRunStates.Stopped, stopped.RunState);
            Assert.Equal(HttpStatusCode.OK, (await DecideAsync(colleague, firstProposal, "approve")).StatusCode);
            var held = await colleague.GetFromJsonAsync<NhAssistantSubjectProposalDto>($"/api/assistant/approvals/{firstProposal.ApprovalId}", Json);
            Assert.Equal(NhAssistantApprovalStatuses.Approved, held!.Proposal.Status);
            Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == first);

            // Resumed, the held execution runs and completes the project.
            await ChangeAgentAsync(closer, SampleAgentStateChange.Resume, null);
            await WaitForProposalAsync(colleague, firstThread, NhAssistantApprovalStatuses.Executed);
            Assert.Contains(host.Projects.StatusChanges, change => change.ProjectId == first && change.Status == ProjectStatus.Completed);
            Assert.Equal(NhAssistantApprovalStatuses.Pending, (await WaitForProposalAsync(colleague, secondThread, NhAssistantApprovalStatuses.Pending)).Status);
            Assert.Equal(NhAssistantAgentRunStates.Active, (await AgentStateAsync(closer)).RunState);
        }
        finally
        {
            // Other samples share the host and its closer.
            await ChangeAgentAsync(closer, SampleAgentStateChange.Resume, null);
        }
    }

    [Fact]
    public async Task The_intake_reviewer_starts_from_a_briefing_of_the_project_and_stops_at_its_token_ceiling()
    {
        // SPM-285
        var project = await host.CreateProjectAsync("BRIEFING", ownerId: host.OwnerId, deadline: DateTimeOffset.UtcNow.AddDays(10));
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        var budget = await host.CreateTaskAsync(project, "Close the budget");
        await host.CreateTaskAsync(project, "Hand over the documentation");
        await host.DeliverAsync(await host.CompleteTaskByHandAsync(budget, host.ParticipantId));
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        var model = new NhAiScriptedChatClient()
            .RespondWithText("One of two tasks is still open, so the intake is not complete.")
            .RespondWithText("The documentation hand-over is still open.");
        host.Model.Use(model);
        try
        {
            // The reviewer's run starts from the project's briefing and reads no task through a tool.
            var receipt = Assert.Single(await host.PublishProjectCreatedAsync(project, createdBy: host.OwnerId));
            var conversationId = receipt.ConversationId!.Value;
            var first = Assert.Single(await WaitForRunCountAsync(owner, conversationId, 1));
            Assert.Equal(NhAssistantRunStatuses.Completed, first.Status);
            Assert.Equal(0, first.ToolCalls);
            var briefing = model.Requests[0].Messages
                .Select(message => message.Text)
                .Single(text => text.Contains("<subject-briefing>", StringComparison.Ordinal));
            Assert.Contains("Status draft", briefing);
            Assert.Contains("Deadline ", briefing);
            Assert.Contains("1 of 2 tasks are open.", briefing);
            Assert.Contains("- open: Hand over the documentation", briefing);
            Assert.Contains(": Close the budget", briefing);
            Assert.Contains("The project is not active, so the project closer leaves it alone.", briefing);

            // A participant's question in the project's thread reads the same briefing.
            await host.SendAsync(participant, conversationId, "What is still open?");
            Assert.Contains(model.Requests[1].Messages, message => message.Text.Contains("1 of 2 tasks are open.", StringComparison.Ordinal));

            // A run that keeps calling tools stops before its next model call once it used its
            // ceiling of 60,000 tokens; an approver may resume it.
            host.Model.Use(new FixedTokenChatClient(
                new NhAiScriptedChatClient()
                    .RespondWithFunctionCall("projects_draft_intake_note_v1", new { input = new { projectId = project } }, "call-note-1")
                    .RespondWithFunctionCall("projects_draft_intake_note_v1", new { input = new { projectId = project } }, "call-note-2"),
                tokensPerCall: 30_000));
            var restart = await participant.PostAsJsonAsync(
                "/api/assistant/threads/start",
                new NhAssistantStartAgentRequest(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type, project.ToString("D"), "Draft the intake note."),
                Json);
            Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
            var stopped = Assert.Single(await WaitForRunCountAsync(owner, conversationId, 2), run => run.Id != first.Id);
            Assert.Equal(NhAssistantRunStatuses.Failed, stopped.Status);
            Assert.Equal(NhAssistantRunFailureClasses.Budget, stopped.FailureClass);
            Assert.Equal(NhAssistantErrorCodes.RunTokenLimit, stopped.FailureCode);
            Assert.Equal(60_000, stopped.InputTokens + stopped.OutputTokens);
            // The first call's tool ran; the second call's tool was refused at the ceiling.
            Assert.Equal(1, stopped.ToolCalls);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    [Fact]
    public async Task The_project_planner_follows_up_tasks_by_rule_and_triage_sends_its_deadline_check_to_a_skill()
    {
        // SPM-286
        const string planner = SampleProjectPlannerComposition.AgentId;
        var project = await host.CreateProjectAsync("PLANNER", ownerId: host.OwnerId, status: ProjectStatus.Active, deadline: DateTimeOffset.UtcNow.AddDays(3));
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        var plan = await host.CreateTaskAsync(project, "Plan the release");
        var notes = await host.CreateTaskAsync(project, "Write the release notes");
        await host.CreateTaskAsync(project, "Train the support desk");
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        var projectText = project.ToString("D");
        var triage = new NhAiScriptedChatClient()
            .RespondWithText("""{"skill":"deadline-risk","reason":"Two of three tasks are open three days before the deadline."}""")
            .RespondWithText("""{"skill":null,"reason":"The deadline check needs no skill that is available."}""");
        var deadline = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = project, status = "OnHold" } }, "call-hold")
            .RespondWithText("The project will miss its deadline; I proposed to put it on hold.");
        host.Model.Use(new RoutingChatClient(
            new SampleAssistantChatClient(),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, "You route the work of an assistant agent")
                && RoutingChatClient.Mentions(messages, options, projectText)
                && !RoutingChatClient.Mentions(messages, options, VariantDeadlineSkill), triage),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, "## Skill: deadline-risk")
                && RoutingChatClient.Mentions(messages, options, projectText), deadline)));
        try
        {
            // A completed task starts the follow-up skill through its trigger; its rules answer
            // without a model.
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(plan, host.ParticipantId));
            var conversationId = await WaitForThreadAsync(colleague, project, planner);
            var followUp = Assert.Single(await WaitForRunCountAsync(colleague, conversationId, 1));
            Assert.Equal(NhAssistantRunStatuses.Completed, followUp.Status);
            Assert.Equal(SampleProjectPlannerComposition.TaskFollowUpSkill, followUp.Skill);
            Assert.Equal(NhAssistantSkillChoosers.Trigger, followUp.SkillChosenBy);
            Assert.Equal(NhAssistantRunKinds.Rule, followUp.Kind);
            Assert.Equal(0, followUp.InputTokens + followUp.OutputTokens);

            // The daily deadline check names no skill: triage sends it to the deadline skill, whose
            // model sees only the status tool and proposes to put the project on hold.
            host.Services.GetRequiredService<IRecurringJobManager>()
                .Trigger("nh-assistant-trigger-" + SampleProjectPlannerComposition.DeadlineCheckTriggerId);
            var proposal = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Pending);
            Assert.Equal(ProjectAiTools.ChangeStatusToolId, proposal.ToolId);
            var risk = Assert.Single(await WaitForRunCountAsync(colleague, conversationId, 2), run => run.Id == proposal.RunId);
            Assert.Equal(SampleProjectPlannerComposition.DeadlineRiskSkill, risk.Skill);
            Assert.Equal(NhAssistantSkillChoosers.Triage, risk.SkillChosenBy);
            var routed = triage.Requests[0];
            Assert.Contains(routed.Messages, message => message.Text.Contains("- deadline-risk:", StringComparison.Ordinal));
            Assert.Contains(routed.Messages, message => message.Text.Contains("2 of 3 tasks are open.", StringComparison.Ordinal));
            var tools = deadline.Requests[0].Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
            Assert.Contains("projects_change_status_v1", tools);
            Assert.DoesNotContain(tools, name => name.Contains("intake", StringComparison.Ordinal));

            // Stopping the deadline skill keeps the task follow-up working, and triage no longer
            // offers the stopped skill: the next check ends with nothing to do.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var stopped = await scope.ServiceProvider.GetRequiredService<SampleAgentMaintenanceService>().ChangeSkillAsync(
                    planner,
                    SampleProjectPlannerComposition.DeadlineRiskSkill,
                    SampleAgentStateChange.Stop,
                    host.OwnerId,
                    "Too many holds",
                    CancellationToken.None);
                Assert.Equal(NhAssistantAgentRunStates.Stopped, stopped!.Data!.RunState);
            }
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(notes, host.ParticipantId));
            var afterStop = await WaitForRunCountAsync(colleague, conversationId, 3);
            Assert.Equal(2, afterStop.Count(run => run.Skill == SampleProjectPlannerComposition.TaskFollowUpSkill && run.Status == NhAssistantRunStatuses.Completed));
            // A tick of the schedule runs once per minute, so the approver asks for the next check.
            var asked = await colleague.PostAsJsonAsync(
                "/api/assistant/threads/start",
                new NhAssistantStartAgentRequest(planner, ProjectSubjects.Type, projectText, $"Check the deadline of project {projectText} again."),
                Json);
            Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
            var quiet = Assert.Single(await WaitForRunCountAsync(colleague, conversationId, 4), run => afterStop.All(earlier => earlier.Id != run.Id));
            Assert.Equal(NhAssistantRunStatuses.Completed, quiet.Status);
            Assert.Null(quiet.Skill);
            Assert.Contains("needs no skill that is available", quiet.Summary);
            Assert.DoesNotContain(triage.Requests[1].Messages, message => message.Text.Contains("- deadline-risk:", StringComparison.Ordinal));
            // The deadline skill's model worked in one run only: its proposal and its answer.
            Assert.Equal(2, deadline.Requests.Count);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SampleAgentMaintenanceService>().ChangeSkillAsync(
                planner,
                SampleProjectPlannerComposition.DeadlineRiskSkill,
                SampleAgentStateChange.Resume,
                host.OwnerId,
                null,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_project_planner_hands_a_finished_project_to_the_closer_and_hears_its_answer()
    {
        // SPM-287
        const string planner = SampleProjectPlannerComposition.AgentId;
        const string closer = SampleProjectCloserComposition.AgentId;
        var project = await host.CreateProjectAsync("HANDOFF", ownerId: host.OwnerId, status: ProjectStatus.Active, deadline: DateTimeOffset.UtcNow.AddDays(5));
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        var design = await host.CreateTaskAsync(project, "Approve the design");
        var delivery = await host.CreateTaskAsync(project, "Deliver the release");
        using var colleague = host.CreateClient(host.ColleagueId.ToString());

        // The last task completes the project's work. The planner's rules hand the project to the
        // closer instead of proposing the closer's work themselves.
        await host.DeliverAsync(await host.CompleteTaskByHandAsync(design, host.ParticipantId));
        await host.DeliverAsync(await host.CompleteTaskByHandAsync(delivery, host.ParticipantId));
        var plannerThread = await WaitForThreadAsync(colleague, project, planner);
        var closerThread = await WaitForThreadAsync(colleague, project, closer);

        // The closer works in its own thread, without an initiator, and proposes under its own
        // policy; the run names the planner and links to its thread for a reader of both.
        var handedOff = await WaitForRunWithTriggerAsync(colleague, closerThread, NhAssistantTriggerIds.HandOff);
        Assert.Equal(NhAssistantRunStatuses.Completed, handedOff.Status);
        Assert.Equal(planner, handedOff.RequestedByAgentId);
        Assert.Equal(plannerThread, handedOff.RequestedByConversationId);
        Assert.Equal(1, handedOff.HandOffDepth);
        Assert.Null(handedOff.InitiatorActorId);
        Assert.Equal(NhAssistantRunKinds.Rule, handedOff.Kind);
        var proposal = await WaitForProposalAsync(colleague, closerThread, NhAssistantApprovalStatuses.Pending);
        Assert.Equal(ProjectAiTools.ChangeStatusToolId, proposal.ToolId);
        Assert.Equal(SampleProjectCloserComposition.ApprovalPolicyId, proposal.PolicyId);
        // The closer's own task trigger and the hand-off record one proposal together.
        var proposals = await colleague.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{closerThread}/proposals", Json);
        Assert.Single(proposals!);

        // The planner hears how the closer's run ended, with its summary: both work on the project.
        var answered = await WaitForRunWithTriggerAsync(colleague, plannerThread, NhAssistantTriggerIds.HandOffEnded);
        Assert.Equal(NhAssistantRunStatuses.Completed, answered.Status);
        Assert.Equal(SampleProjectPlannerComposition.TaskFollowUpSkill, answered.Skill);
        var dutch = CultureInfo.GetCultureInfo("nl-NL");
        var prefix = string.Format(
            dutch,
            new ResourceManager(typeof(SampleAssistantToolPresenter)).GetString("PlannerCloserAnswered", dutch)!,
            string.Empty).Trim();
        Assert.Contains(prefix, answered.Summary);
        var audit = Assert.Single(
            host.Services.GetRequiredService<SampleAssistantAuditLog>().Events,
            evt => evt.Kind == NhAssistantAuditEventKind.AgentHandedOff
                && evt.SubjectId == project.ToString("D")
                && evt.AskingAgentId == planner);
        Assert.Equal(closer, audit.AgentId);
        Assert.Equal(planner, audit.AskingAgentId);
        Assert.Equal(NhAssistantHandOffOutcomes.Accepted, audit.ResultCode);
    }

    [Fact]
    public async Task A_variant_of_the_project_planner_runs_in_the_shadow_and_is_compared_with_the_live_planner()
    {
        // SPM-288
        const string planner = SampleProjectPlannerComposition.AgentId;
        const string variant = SampleProjectPlannerVariantComposition.AgentId;
        var from = DateTimeOffset.UtcNow.AddSeconds(-1);
        var project = await host.CreateProjectAsync("VARIANT", ownerId: host.OwnerId, status: ProjectStatus.Active, deadline: DateTimeOffset.UtcNow.AddDays(3));
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        await host.CreateTaskAsync(project, "Design the release");
        await host.CreateTaskAsync(project, "Ship the release");
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        using var admin = host.CreateClient(host.ColleagueId.ToString(), admin: true);
        var projectText = project.ToString("D");
        var liveTriage = new NhAiScriptedChatClient()
            .RespondWithText("""{"skill":"deadline-risk","reason":"No task is completed three days before the deadline."}""");
        var variantTriage = new NhAiScriptedChatClient()
            .RespondWithText("""{"skill":null,"reason":"No task is completed yet, so it is too early to judge."}""")
            .RespondWithText("""{"skill":null,"reason":"No task is completed yet, so it is too early to judge."}""");
        var deadline = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = project, status = "OnHold" } }, "call-hold")
            .RespondWithText("The project will miss its deadline; I proposed to put it on hold.");
        host.Model.Use(new RoutingChatClient(
            new SampleAssistantChatClient(),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, "You route the work of an assistant agent")
                && RoutingChatClient.Mentions(messages, options, projectText)
                && RoutingChatClient.Mentions(messages, options, VariantDeadlineSkill), variantTriage),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, "You route the work of an assistant agent")
                && RoutingChatClient.Mentions(messages, options, projectText), liveTriage),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, "## Skill: deadline-risk")
                && RoutingChatClient.Mentions(messages, options, projectText), deadline)));
        try
        {
            // The daily deadline check fires for the live planner and for its shadow variant. The
            // planner's triage sends it to the deadline skill, which proposes to put the project on
            // hold; the variant's stricter triage finds it too early and proposes nothing.
            host.Services.GetRequiredService<IRecurringJobManager>()
                .Trigger("nh-assistant-trigger-" + SampleProjectPlannerComposition.DeadlineCheckTriggerId);
            var plannerThread = await WaitForThreadAsync(colleague, project, planner);
            var proposal = await WaitForProposalAsync(colleague, plannerThread, NhAssistantApprovalStatuses.Pending);
            var variantThread = await WaitForThreadAsync(admin, project, variant);
            var shadowRun = await WaitForRunWithTriggerAsync(admin, variantThread, SampleProjectPlannerComposition.DeadlineCheckTriggerId);
            Assert.True(shadowRun.Shadow);
            Assert.Null(shadowRun.Skill);
            Assert.Contains("too early to judge", shadowRun.Summary);
            // People who are no administrators do not see the variant's thread.
            Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync($"/api/assistant/threads/{variantThread}/runs")).StatusCode);

            // The approver rejects the live proposal.
            Assert.Equal(HttpStatusCode.OK, (await DecideAsync(colleague, proposal, "reject")).StatusCode);

            // The comparison: the variant did not propose what people rejected.
            var period = $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddSeconds(1).ToString("O"))}";
            var report = (await admin.GetFromJsonAsync<NhAssistantEvaluationDto>(
                $"/api/assistant/admin/agents/{planner}/evaluation?variant={variant}&{period}",
                Json))!;
            Assert.True(report.Runs.Paired >= 1);
            var tool = Assert.Single(report.Tools, row => row.ToolId == ProjectAiTools.ChangeStatusToolId);
            Assert.True(tool.RejectedLiveNotProposed >= 1);
            Assert.Contains(report.Differences, difference => difference.LiveConversationId == plannerThread
                && difference.Kind == NhAssistantEvaluationDifferenceKinds.LiveOnly
                && difference.LiveStatus == NhAssistantApprovalStatuses.Rejected);

            // A replay fires the planner's recorded runs of the period into the variant.
            var started = await admin.PostAsJsonAsync(
                $"/api/assistant/admin/agents/{variant}/replay",
                new NhAssistantReplayRequest(planner, from, DateTimeOffset.UtcNow.AddSeconds(1), 20),
                Json);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var replay = await WaitForReplayAsync(admin, variant, (await started.Content.ReadFromJsonAsync<NhAssistantReplayDto>(Json))!.Id);
            Assert.Equal(NhAssistantReplayStatuses.Completed, replay.Status);
            Assert.True(replay.RunsReplayed >= 1);
            await WaitForRunCountAsync(admin, variantThread, 2);

            // The export pairs the live run with the variant's run and with its replay, without content.
            var export = await admin.GetStringAsync(
                $"/api/assistant/admin/agents/{planner}/evaluation/export?variant={variant}&from={Uri.EscapeDataString(from.ToString("O"))}");
            Assert.DoesNotContain("OnHold", export, StringComparison.Ordinal);
            var records = export
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<NhAssistantEvaluationExportRecord>(line, Json)!)
                .Where(record => record.SubjectId == projectText && record.TriggerId == SampleProjectPlannerComposition.DeadlineCheckTriggerId)
                .ToArray();
            var mirrored = Assert.Single(records, record => !record.Replayed);
            Assert.Single(records, record => record.Replayed);
            Assert.Equal(NhAssistantApprovalStatuses.Rejected, Assert.Single(mirrored.Live.Proposals).Status);
            Assert.Empty(mirrored.Variant.Proposals);
            Assert.True(mirrored.Live.ModelCalls > mirrored.Variant.ModelCalls);
            var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>().Events;
            Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentReplayStarted && evt.AgentId == variant);
            Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentEvaluationExported && evt.ObjectId == variant);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    [Fact]
    public async Task A_participant_starts_the_status_advisor_which_uses_their_rights_and_waits_inline_for_their_approval()
    {
        // SPM-272 and SPM-273
        var project = await host.CreateProjectAsync("ADVISOR", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        await host.AddMemberAsync(project, host.ReaderId, ProjectMemberRole.Reader);
        using var reader = host.CreateClient(host.ReaderId.ToString());
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        var start = new NhAssistantStartAgentRequest(
            SampleStatusAdvisorComposition.AgentId,
            ProjectSubjects.Type,
            project.ToString("D"),
            $"Set project {project:D} to onhold.");

        var readerStart = await reader.PostAsJsonAsync("/api/assistant/threads/start", start, Json);
        Assert.Equal(HttpStatusCode.Forbidden, readerStart.StatusCode);
        var started = await participant.PostAsJsonAsync("/api/assistant/threads/start", start, Json);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var receipt = (await started.Content.ReadFromJsonAsync<NhAssistantTriggerReceiptDto>(Json))!;
        Assert.Equal(NhAssistantTriggerIds.Manual, receipt.TriggerId);
        var conversationId = receipt.ConversationId!.Value;

        // The advisor has no grants: it proposes with the participant's own rights, checked again
        // without a request, and its run waits as a suspended background operation.
        var proposal = await WaitForProposalAsync(participant, conversationId, NhAssistantApprovalStatuses.Pending);
        Assert.Equal(NhAssistantApprovalModes.Inline, proposal.Mode);
        Assert.Equal("delegated", proposal.GrantSource);
        Assert.Equal(host.ParticipantId.ToString(), proposal.InitiatorActorId);
        var waiting = await WaitForRunAsync(participant, conversationId, NhAssistantRunStatuses.WaitingForApproval);
        // The operation releases its worker while it waits for the decision.
        await host.WaitForOperationAsync(waiting.BackgroundOperationId!.Value, NhBackgroundOperationStatus.WaitingForSignal);
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == project);

        var decision = await DecideAsync(participant, proposal, "approve");
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);

        var completed = await WaitForRunAsync(participant, conversationId, NhAssistantRunStatuses.Completed);
        Assert.Equal(waiting.Id, completed.Id);
        Assert.Contains(host.Projects.StatusChanges, change => change.ProjectId == project && change.Status == ProjectStatus.OnHold);
    }

    [Fact]
    public async Task An_approved_request_that_fails_without_a_result_waits_for_a_person_instead_of_running_again()
    {
        // SPM-274
        var project = await host.CreateProjectAsync("REVIEW", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        host.Reviews.FailFor(project);
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        using var colleague = host.CreateClient(host.ColleagueId.ToString());

        var started = await participant.PostAsJsonAsync(
            "/api/assistant/threads/start",
            new NhAssistantStartAgentRequest(
                SampleIntakeReviewerComposition.AgentId,
                ProjectSubjects.Type,
                project.ToString("D"),
                $"Please request a review of project {project:D}."),
            Json);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var conversationId = (await started.Content.ReadFromJsonAsync<NhAssistantTriggerReceiptDto>(Json))!.ConversationId!.Value;
        var proposal = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Pending);
        Assert.Equal(ProjectAiReviewTools.RequestReviewToolId, proposal.ToolId);

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(colleague, proposal, "approve")).StatusCode);

        // The review board may have received the request before the call failed, so the proposal
        // is neither failed nor retried: a person decides what happened.
        var unknown = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.ExecutionUnknown);
        Assert.Equal(1, host.Reviews.Attempts(project));
        var resolve = await colleague.PostAsJsonAsync(
            $"/api/assistant/approvals/{unknown.ApprovalId}/resolve",
            new NhAssistantResolveExecutionRequest("mark-resolved", "The review board confirmed the request."),
            Json);
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        var resolved = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Resolved);
        Assert.Equal(unknown.ApprovalId, resolved.ApprovalId);
        Assert.Equal(1, host.Reviews.Attempts(project));
    }

    [Fact]
    public async Task A_signed_webhook_a_weekly_schedule_and_the_kill_switch_control_the_intake_reviewer()
    {
        // SPM-275
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var anonymous = host.CreateAnonymousClient();

        // The review board answers through a signed webhook call; a repeated delivery starts no
        // second run and a wrong signature starts nothing.
        var boardProject = await host.CreateProjectAsync("BOARD", ownerId: host.OwnerId);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { projectId = boardProject, verdict = "approved" });
        var forged = await PostWebhookAsync(anonymous, body, "board-1", "a-forged-secret-that-is-long-enough-123");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        var accepted = await PostWebhookAsync(anonymous, body, "board-1", AssistantAgentRunSampleHost.ReviewBoardSecret);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var receipt = (await accepted.Content.ReadFromJsonAsync<NhAssistantTriggerReceiptDto>(Json))!;
        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
        var replayed = await PostWebhookAsync(anonymous, body, "board-1", AssistantAgentRunSampleHost.ReviewBoardSecret);
        var replay = (await replayed.Content.ReadFromJsonAsync<NhAssistantTriggerReceiptDto>(Json))!;
        Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, replay.Outcome);
        Assert.Equal(receipt.RunId, replay.RunId);
        var boardProposal = await WaitForProposalAsync(owner, receipt.ConversationId!.Value, NhAssistantApprovalStatuses.Pending);
        Assert.Equal(ProjectAiTools.ChangeStatusToolId, boardProposal.ToolId);

        // The weekly schedule is a Hangfire recurring job; triggering it reviews the active
        // project whose deadline falls within a week.
        var dueProject = await host.CreateProjectAsync(
            "DEADLINE",
            ownerId: host.OwnerId,
            status: ProjectStatus.Active,
            deadline: DateTimeOffset.UtcNow.AddDays(3));
        host.Services.GetRequiredService<IRecurringJobManager>()
            .Trigger("nh-assistant-trigger-" + SampleIntakeReviewerComposition.DeadlineReviewTriggerId);
        var dueThread = await WaitForThreadAsync(owner, dueProject);
        var dueProposal = await WaitForProposalAsync(owner, dueThread, NhAssistantApprovalStatuses.Pending);
        Assert.Equal(ProjectAiTools.ChangeStatusToolId, dueProposal.ToolId);

        // The kill switch stops the agent until an administrator releases it.
        using var admin = host.CreateClient(host.ColleagueId.ToString(), admin: true);
        var killSwitch = $"/api/assistant/admin/agents/{SampleIntakeReviewerComposition.AgentId}/kill-switch";
        try
        {
            var engaged = await admin.PostAsJsonAsync(killSwitch, new NhAssistantKillSwitchRequest(true), Json);
            Assert.Equal(HttpStatusCode.OK, engaged.StatusCode);
            Assert.False((await engaged.Content.ReadFromJsonAsync<NhAssistantKillSwitchDto>(Json))!.Enabled);
            var stopped = await host.CreateProjectAsync("STOPPED", ownerId: host.OwnerId);
            var rejected = Assert.Single(await host.PublishProjectCreatedAsync(stopped, createdBy: host.OwnerId));
            Assert.Equal(NhAssistantTriggerOutcomes.Rejected, rejected.Outcome);
            Assert.Equal(NhAssistantErrorCodes.AgentStopped, rejected.ReasonCode);
            using var participant = host.CreateClient(host.ParticipantId.ToString());
            var notAdmin = await participant.PostAsJsonAsync(killSwitch, new NhAssistantKillSwitchRequest(false), Json);
            Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        }
        finally
        {
            var released = await admin.PostAsJsonAsync(killSwitch, new NhAssistantKillSwitchRequest(false), Json);
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        }
    }

    [Fact]
    public async Task The_intake_reviewer_remembers_its_checks_saves_a_note_and_forgets_a_deleted_project()
    {
        // SPM-276
        var project = await host.CreateProjectAsync("MEMORY", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("memory_update", new { patch = new { checks = new[] { "owner-set" }, lastVerdict = "pending" } }, "call-memory")
            .RespondWithFunctionCall("projects_draft_intake_note_v1", new { input = new { projectId = project } }, "call-note")
            .RespondWithText("Noted the intake checks.")
            .RespondWithText("Nothing changed since the last review.");
        host.Model.Use(model);
        try
        {
            var first = Assert.Single(await host.PublishProjectCreatedAsync(project, createdBy: host.OwnerId));
            var conversationId = first.ConversationId!.Value;
            var firstRun = Assert.Single(await WaitForRunCountAsync(owner, conversationId, 1));
            Assert.Equal(NhAssistantRunStatuses.Completed, firstRun.Status);

            // The memory and the note survive the run; people on the project can see both.
            var memory = await owner.GetFromJsonAsync<NhAssistantThreadMemoryDto>($"/api/assistant/threads/{conversationId}/memory", Json);
            Assert.True(memory!.Configured);
            Assert.Equal("owner-set", memory.Memory!.Value.GetProperty("checks")[0].GetString());
            var artifacts = await owner.GetFromJsonAsync<NhAssistantArtifactDto[]>($"/api/assistant/threads/{conversationId}/artifacts", Json);
            var note = Assert.Single(artifacts!);
            Assert.Equal(1, note.Version);
            Assert.False(note.Restricted);
            var content = await owner.GetStringAsync($"/api/assistant/threads/{conversationId}/artifacts/{note.Id}/content");
            Assert.StartsWith("# Intake", content);

            // A later run starts from the memory, the note and the summary of the first run.
            var restart = await participant.PostAsJsonAsync(
                "/api/assistant/threads/start",
                new NhAssistantStartAgentRequest(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type, project.ToString("D"), "Check the project again."),
                Json);
            Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
            var runs = await WaitForRunCountAsync(owner, conversationId, 2);
            Assert.All(runs, run => Assert.Equal(NhAssistantRunStatuses.Completed, run.Status));
            var continuity = model.Requests[^1].Messages
                .Select(message => message.Text)
                .Single(text => text.Contains("<agent-continuity>", StringComparison.Ordinal));
            Assert.Contains("\"lastVerdict\":\"pending\"", continuity);
            Assert.Contains("Noted the intake checks.", continuity);
            Assert.Contains(note.Name + " v1", continuity);

            // Deleting the project deletes its agent thread with the memory and the note.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var deleted = await scope.ServiceProvider.GetRequiredService<ProjectService>().DeleteAsync(project);
                Assert.True(deleted.Success);
            }
            var gone = await owner.GetAsync($"/api/assistant/threads/{conversationId}/runs");
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    [Fact]
    public async Task The_project_assistant_consults_the_intake_reviewer_with_the_askers_rights()
    {
        // SPM-278
        const string consultTool = "consult_sample-project-intake-reviewer_project_v1";
        const string requestTool = "request_sample-project-intake-reviewer_project_v1";
        var project = await host.CreateProjectAsync("CONSULT", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        var reviewer = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("memory_update", new { patch = new { checks = new[] { "owner-set" }, lastVerdict = "pending" } }, "call-memory")
            .RespondWithText("Checked the owner; the review board has not answered.")
            .RespondWithText("The owner is set. The review board has not answered yet, so the verdict is pending.")
            .RespondWithText("Reviewed again; still waiting for the review board.");
        var assistant = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(consultTool, new { input = new { subjectId = project.ToString("D"), question = "What is still missing for the intake?" } }, "call-consult")
            .RespondWithText("The intake reviewer still waits for the review board.")
            .RespondWithFunctionCall(consultTool, new { input = new { subjectId = project.ToString("D"), question = "What is missing?" } }, "call-refused")
            .RespondWithText("I cannot ask about that project for you.")
            .RespondWithFunctionCall(requestTool, new { input = new { subjectId = project.ToString("D"), message = "Review the intake again." } }, "call-request")
            .RespondWithText("The intake reviewer answers in the project's thread.");
        var projectText = project.ToString("D");
        var assistantInstructions = ProjectAiAssets.ProjectAgentInstructions.Content.Split('\n')[0].Trim();
        host.Model.Use(new RoutingChatClient(
            new SampleAssistantChatClient(),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, assistantInstructions), assistant),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, projectText)
                || RoutingChatClient.Mentions(messages, options, "You are being consulted"), reviewer)));
        try
        {
            var created = Assert.Single(await host.PublishProjectCreatedAsync(project, createdBy: host.OwnerId));
            var conversationId = created.ConversationId!.Value;
            await WaitForRunAsync(owner, conversationId, NhAssistantRunStatuses.Completed);
            var messagesBefore = (await ReadConversationAsync(owner, conversationId)).Messages.Count;

            // A project participant asks the project assistant, which consults the reviewer.
            var chat = await host.CreateConversationAsync(participant);
            var events = await host.SendAsync(participant, chat, "What is still missing for the intake of my project?");
            var consulted = events.Single(evt => evt.Name == "tool.completed").Data;
            Assert.Equal("succeeded", consulted.GetProperty("status").GetString());

            // The reviewer answered from its working memory, with the participant's rights.
            var consultation = Assert.Single(reviewer.Requests, request => request.Messages.Any(message =>
                message.Role == ChatRole.System && message.Text.Contains("You are being consulted", StringComparison.Ordinal)));
            var instructions = string.Join("\n", consultation.Messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
            Assert.Contains("\"lastVerdict\":\"pending\"", instructions);
            Assert.Contains("Name the checks you did", instructions);
            Assert.Equal("What is still missing for the intake?", consultation.Messages[^1].Text);

            // People on the project see who consulted the reviewer, never the question; the
            // thread itself gets no message.
            var consultations = await owner.GetFromJsonAsync<NhAssistantConsultationListDto>(
                $"/api/assistant/threads/{conversationId}/consultations",
                Json);
            var record = Assert.Single(consultations!.Items);
            Assert.Equal(host.ParticipantId.ToString(), record.AskerActorId);
            Assert.Equal(SampleAssistantComposition.AgentId, record.AskingAgentId);
            Assert.Equal(NhAssistantConsultationStatuses.Completed, record.Status);
            Assert.Equal(messagesBefore, (await ReadConversationAsync(owner, conversationId)).Messages.Count);

            // A colleague outside the project gets the answer an unknown project would get.
            var refused = await host.SendAsync(colleague, await host.CreateConversationAsync(colleague), "What is missing for that project?");
            var refusedCall = refused.Single(evt => evt.Name == "tool.completed").Data;
            Assert.Equal("failed", refusedCall.GetProperty("status").GetString());
            Assert.Equal(NhAssistantErrorCodes.ConsultSubjectUnavailable, refusedCall.GetProperty("resultCode").GetString());

            // A participant may also request a new review: a run of the reviewer with them as initiator.
            await host.SendAsync(participant, chat, "Ask the reviewer to look at it again.");
            var runs = await WaitForRunCountAsync(owner, conversationId, 2);
            var requested = Assert.Single(runs, run => run.TriggerId == NhAssistantTriggerIds.AgentRequest);
            Assert.Equal(NhAssistantRunStatuses.Completed, requested.Status);
            Assert.Equal(host.ParticipantId.ToString(), requested.InitiatorActorId);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    [Fact]
    public async Task The_project_documents_source_matches_the_words_of_a_question_in_the_active_division_only()
    {
        // SPM-291: the project assistant retrieves with the person's whole question, so the sample's
        // source matches any of its words, regardless of case, and never reads another division.
        var roadmap = await host.CreateProjectAsync("ROADMAP", ownerId: host.OwnerId);
        var budget = await host.CreateProjectAsync("BUDGET", ownerId: host.OwnerId);
        var elsewhere = await host.CreateProjectAsync("ROADMAP", ownerId: host.OutsiderId, divisionId: host.OtherDivisionId);

        await using var scope = host.Services.CreateAsyncScope();
        var documents = await scope.ServiceProvider.GetRequiredService<ProjectService>().SearchContextForAiAsync(
            host.DivisionId,
            "What does the Roadmap say about May?",
            25,
            TestContext.Current.CancellationToken);

        var found = documents.Select(document => document.ProjectId).ToArray();
        Assert.Contains(roadmap, found);
        Assert.DoesNotContain(budget, found);
        Assert.DoesNotContain(elsewhere, found);
    }

    [Fact]
    public async Task The_project_planner_reads_a_projects_tasks_through_the_gateway_with_the_rights_of_whom_it_reads_for()
    {
        // SPM-290
        const string planner = SampleProjectPlannerComposition.AgentId;
        const string listAction = "sample-api.project-task.get";
        const string getAction = "sample-api.project-task.get-by-id";
        var owner = await host.CreatePersonAsync("app.project.view");
        var approver = await host.CreatePersonAsync();
        var project = await host.CreateProjectAsync("GATEWAY", ownerId: owner, status: ProjectStatus.Active, deadline: DateTimeOffset.UtcNow.AddDays(2));
        await host.AddMemberAsync(project, approver, ProjectMemberRole.Approver);
        var migrate = await host.CreateTaskAsync(project, "Migrate the customer data");
        await host.CreateTaskAsync(project, "Rehearse the cutover");
        using var ownerClient = host.CreateClient(owner.ToString());
        using var approverClient = host.CreateClient(approver.ToString());
        var projectText = project.ToString("D");
        var openTasks = new
        {
            input = new
            {
                resource = "project-task",
                filter = new[] { new { key = "projectId", @operator = "==", value = projectText } }
            }
        };
        var triage = new NhAiScriptedChatClient()
            .RespondWithText("""{"skill":"deadline-risk","reason":"Two tasks are open two days before the deadline."}""")
            .RespondWithText("""{"skill":"deadline-risk","reason":"The approver asks for the deadline again."}""");
        var deadline = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("sample-api-gateway_query_v1", openTasks, "call-owner")
            .RespondWithText("Both open tasks fit in the two days left, so the project holds.")
            .RespondWithFunctionCall("sample-api-gateway_query_v1", openTasks, "call-approver")
            .RespondWithText("I cannot read the tasks of this project for you.");
        host.Model.Use(new RoutingChatClient(
            new SampleAssistantChatClient(),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, "You route the work of an assistant agent")
                && RoutingChatClient.Mentions(messages, options, projectText)
                && !RoutingChatClient.Mentions(messages, options, VariantDeadlineSkill), triage),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, "## Skill: deadline-risk")
                && RoutingChatClient.Mentions(messages, options, projectText), deadline)));
        try
        {
            // The daily deadline check starts the planner without a person: it reads for the owner.
            host.Services.GetRequiredService<IRecurringJobManager>()
                .Trigger("nh-assistant-trigger-" + SampleProjectPlannerComposition.DeadlineCheckTriggerId);
            var conversationId = await WaitForThreadAsync(approverClient, project, planner);
            var check = await WaitForRunAsync(approverClient, conversationId, NhAssistantRunStatuses.Completed);
            Assert.Null(check.InitiatorActorId);
            Assert.Equal(SampleProjectPlannerComposition.DeadlineRiskSkill, check.Skill);
            // The skill's model is offered the gateway's read tools and never the API's own actions.
            var tools = deadline.Requests[0].Options!.Tools!.Select(tool => tool.Name).ToArray();
            Assert.Contains("sample-api-gateway_query_v1", tools);
            Assert.Contains("projects_change_status_v1", tools);
            Assert.DoesNotContain(tools, name => name.StartsWith("sample-api_", StringComparison.Ordinal));
            // The query ran through the task controller with the owner's credential.
            var ownerRead = deadline.FunctionResults.First(result => result.CallId == "call-owner").Result?.ToString();
            Assert.Contains("Migrate the customer data", ownerRead);
            Assert.Contains("Rehearse the cutover", ownerRead);

            // Only the owner sees what was read with their rights; the approver sees that it happened,
            // also with the permission to read projects.
            var shown = ToolCall(await ReadConversationAsync(ownerClient, conversationId), "call-owner");
            Assert.Equal("delegated-read", shown.GrantSource);
            Assert.False(shown.Restricted);
            Assert.Contains("Migrate the customer data", shown.ResultPreview);
            using var approverWithRight = host.CreateClient(approver.ToString(), false, "app.project.view");
            var hidden = ToolCall(await ReadConversationAsync(approverWithRight, conversationId), "call-owner");
            Assert.True(hidden.Restricted);
            Assert.Null(hidden.ResultPreview);

            // When the approver asks for a check, the run reads with the approver's own rights, which
            // do not include tasks: asking the planner never lends the owner's rights.
            var asked = await approverClient.PostAsJsonAsync(
                "/api/assistant/threads/start",
                new NhAssistantStartAgentRequest(planner, ProjectSubjects.Type, projectText, $"Check the deadline of project {projectText} again."),
                Json);
            Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
            await WaitForInitiatedRunAsync(approverClient, conversationId, approver.ToString());
            var approverRead = deadline.FunctionResults.First(result => result.CallId == "call-approver").Result?.ToString();
            Assert.DoesNotContain("Migrate the customer data", approverRead);
            Assert.Contains(NhAiBridgeFailureCodes.ResourceNotFound, approverRead);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }

        // The owner's credential from the built-in issuer reaches only the gateway action it was
        // issued for, with its invocation header: another gateway action, a write, an endpoint outside
        // the gateway and a call without the header fail.
        var invocation = Guid.NewGuid();
        var token = await IssueReadCredentialAsync(owner, invocation, listAction);
        using var api = host.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.OK, await SendWithTokenAsync(api, HttpMethod.Get, $"/project-tasks?projectId={projectText}", token, invocation));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendWithTokenAsync(api, HttpMethod.Get, $"/project-tasks/{migrate:D}", token, invocation));
        Assert.Equal(HttpStatusCode.OK, await SendWithTokenAsync(api, HttpMethod.Get, $"/project-tasks/{migrate:D}", await IssueReadCredentialAsync(owner, invocation, getAction), invocation));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendWithTokenAsync(api, HttpMethod.Post, "/project-tasks", token, invocation));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendWithTokenAsync(api, HttpMethod.Get, "/project-members", token, invocation));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendWithTokenAsync(api, HttpMethod.Get, $"/project-tasks?projectId={projectText}", token, null));

        // A locked-out owner gets no credential, while their earlier sessions are still valid.
        await host.LockOutAsync(owner);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var refused = await scope.ServiceProvider.GetRequiredService<INhAiDelegatedReadCredentialIssuer>().IssueAsync(
                ReadRequest(owner, Guid.NewGuid(), listAction),
                CancellationToken.None);
            Assert.False(refused.Success);
        }
    }

    private static NhAiDelegatedReadRequest ReadRequest(Guid person, Guid invocation, string action)
    {
        return new NhAiDelegatedReadRequest(
            person.ToString(),
            null,
            invocation,
            NhAiDelegatedReadDefaults.DefaultLifetime,
            [
                new Claim(NhAiDelegatedReadDefaults.ClaimType, invocation.ToString("D")),
                new Claim(NhAiDelegatedReadDefaults.ActionClaimType, action)
            ]);
    }

    private async Task<string> IssueReadCredentialAsync(Guid person, Guid invocation, string action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var issued = await scope.ServiceProvider.GetRequiredService<INhAiDelegatedReadCredentialIssuer>()
            .IssueAsync(ReadRequest(person, invocation, action), CancellationToken.None);
        Assert.True(issued.Success);
        return issued.Data!.BearerToken;
    }

    private static async Task WaitForInitiatedRunAsync(HttpClient client, Guid conversationId, string initiator)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var runs = await client.GetFromJsonAsync<NhAssistantRunListDto>($"/api/assistant/threads/{conversationId}/runs", Json);
            if (runs!.Items.Any(run => run.InitiatorActorId == initiator && run.Status == NhAssistantRunStatuses.Completed))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        throw new TimeoutException($"No completed run started by {initiator}.");
    }

    /// <summary>The tool call of the thread that answered the model's call <paramref name="callId"/>.</summary>
    private static NhAssistantToolCallPartDto ToolCall(NhAssistantConversationDto conversation, string callId)
    {
        var calls = conversation.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartDto>()
            .Where(part => part.ToolId == "sample-api-gateway.query")
            .ToArray();
        return callId == "call-owner" ? calls[0] : calls[^1];
    }

    private static async Task<HttpStatusCode> SendWithTokenAsync(HttpClient client, HttpMethod method, string path, string token, Guid? invocation)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (invocation is { } id)
        {
            request.Headers.Add(NhAiDelegatedReadDefaults.InvocationHeaderName, id.ToString("D"));
        }
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { title = "Written with a read credential" });
        }
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task The_project_planner_hands_a_late_task_to_its_follow_up_skills_model_and_follows_up_the_decision_in_that_skill()
    {
        // SPM-292
        const string planner = SampleProjectPlannerComposition.AgentId;
        const string variant = SampleProjectPlannerVariantComposition.AgentId;
        const string skill = SampleProjectPlannerComposition.TaskFollowUpSkill;
        var project = await host.CreateProjectAsync("LATE", ownerId: host.OwnerId, status: ProjectStatus.Active, deadline: DateTimeOffset.UtcNow.AddDays(-2));
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        var foundation = await host.CreateTaskAsync(project, "Pour the foundation");
        await host.CreateTaskAsync(project, "Build the walls");
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        var projectText = project.ToString("D");
        var late = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = project, status = "OnHold" } }, "call-hold")
            .RespondWithText("The walls cannot follow soon after the deadline; I proposed to put the project on hold.");
        host.Model.Use(new RoutingChatClient(
            new SampleAssistantChatClient(),
            ((messages, options) => RoutingChatClient.Mentions(messages, options, "## Skill: task-follow-up")
                && RoutingChatClient.Mentions(messages, options, projectText), late)));
        // The shadow variant's copy of the skill would take the same task; it stays out of this case.
        Assert.True((await ChangeSkillAsync(variant, skill, stop: true)).Success);
        try
        {
            // A task completed after the deadline starts the follow-up skill; its rules hand the case
            // to the skill's own model, which proposes to put the project on hold.
            await host.DeliverAsync(await host.CompleteTaskByHandAsync(foundation, host.ParticipantId));
            var conversationId = await WaitForThreadAsync(colleague, project, planner);
            var proposal = await WaitForProposalAsync(colleague, conversationId, NhAssistantApprovalStatuses.Pending);
            Assert.Equal(ProjectAiTools.ChangeStatusToolId, proposal.ToolId);
            var handedOver = Assert.Single(await RunsAsync(colleague, conversationId), run => run.Id == proposal.RunId);
            Assert.Equal(skill, handedOver.Skill);
            Assert.Equal(NhAssistantSkillChoosers.Trigger, handedOver.SkillChosenBy);
            Assert.Equal(NhAssistantRunKinds.RuleThenModel, handedOver.Kind);
            // The model worked in the skill: its instructions, only its status tool, the rules' reason.
            var request = late.Requests[0];
            Assert.Contains("The rules handed you a task that was completed after the project's deadline.", request.Options!.Instructions);
            var tools = request.Options.Tools!.Select(tool => tool.Name).ToArray();
            Assert.Contains("projects_change_status_v1", tools);
            Assert.DoesNotContain(tools, name => name.StartsWith("sample-api-gateway_", StringComparison.Ordinal));
            Assert.Contains(request.Messages, message => message.Text.Contains("completed after the project's deadline of", StringComparison.Ordinal));

            // The approver approves; once it executed, the follow-up run continues in the same skill
            // and its rules take the decision as the answer, without a model.
            var decision = await DecideAsync(colleague, proposal, "approve");
            Assert.True(decision.IsSuccessStatusCode);
            var followUp = await WaitForRunWithTriggerAsync(colleague, conversationId, NhAssistantTriggerIds.ProposalExecuted);
            Assert.Equal(NhAssistantRunStatuses.Completed, followUp.Status);
            Assert.Equal(skill, followUp.Skill);
            Assert.Equal(NhAssistantSkillChoosers.FollowUp, followUp.SkillChosenBy);
            Assert.Equal(NhAssistantRunKinds.Rule, followUp.Kind);
            Assert.Equal(2, late.Requests.Count);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
            await ChangeSkillAsync(variant, skill, stop: false);
        }
    }

    /// <summary>Stops or resumes a skill of any agent, the shadow variant included.</summary>
    private async Task<TaskResult<NhAssistantSkillState>> ChangeSkillAsync(string agentId, string skillId, bool stop)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var control = scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>();
        var operatorId = host.OwnerId.ToString("D");
        return stop
            ? await control.StopSkillAsync(agentId, skillId, operatorId, "Outside this case", CancellationToken.None)
            : await control.ResumeSkillAsync(agentId, skillId, operatorId, null, CancellationToken.None);
    }

    [Fact]
    public async Task An_approver_explains_a_rejection_that_the_next_run_and_the_administrators_report_see()
    {
        // SPM-279
        var project = await host.CreateProjectAsync("FEEDBACK", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        var created = Assert.Single(await host.PublishProjectCreatedAsync(project, createdBy: host.OwnerId));
        var conversationId = created.ConversationId!.Value;
        var proposal = await WaitForProposalAsync(owner, conversationId, NhAssistantApprovalStatuses.Pending);
        await WaitForRunCountAsync(owner, conversationId, 1);

        // Rejecting stays one decision; afterwards the approver may say what was wrong.
        var rejection = await DecideAsync(colleague, proposal, "reject");
        Assert.Equal(HttpStatusCode.OK, rejection.StatusCode);
        var rejected = (await rejection.Content.ReadFromJsonAsync<NhAssistantProposalDto>(Json))!;
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, rejected.Status);
        Assert.True(rejected.CanGiveFeedback);
        // People read who decided by name, and the proposal in their own language, whichever
        // culture the headless run worked in.
        Assert.Equal("colleague@sample.localhost", rejected.DecidedByDisplayName);
        var resources = new ResourceManager(typeof(SampleAssistantToolPresenter));
        Assert.Equal(
            resources.GetString("ToolProjectStatusChange", CultureInfo.GetCultureInfo("en-US")),
            rejected.Presentation!.ToolDisplayName);
        // The Angular client names only the language; the assistant picks the supported culture of
        // that language instead of the application's default.
        using (var englishColleague = host.CreateClient(host.ColleagueId.ToString()))
        {
            englishColleague.DefaultRequestHeaders.AcceptLanguage.Clear();
            englishColleague.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
            var english = Assert.Single(
                (await englishColleague.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json))!,
                item => item.ApprovalId == proposal.ApprovalId);
            Assert.Equal(
                resources.GetString("ToolProjectStatusChange", CultureInfo.GetCultureInfo("en-US")),
                english.Presentation!.ToolDisplayName);
        }
        using (var dutchColleague = host.CreateClient(host.ColleagueId.ToString()))
        {
            dutchColleague.DefaultRequestHeaders.AcceptLanguage.Clear();
            dutchColleague.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");
            var dutch = Assert.Single(
                (await dutchColleague.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json))!,
                item => item.ApprovalId == proposal.ApprovalId);
            Assert.Equal(
                resources.GetString("ToolProjectStatusChange", CultureInfo.GetCultureInfo("nl-NL")),
                dutch.Presentation!.ToolDisplayName);
        }

        var feedbackPath = $"/api/assistant/approvals/{proposal.ApprovalId}/feedback";
        var request = new NhAssistantProposalFeedbackRequest(
            NhAssistantProposalFeedbackCategories.NotNow,
            "Wait for the budget review before activating.");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync(feedbackPath, request, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.PutAsJsonAsync(feedbackPath, request, Json)).StatusCode);
        var given = await colleague.PutAsJsonAsync(feedbackPath, request, Json);
        Assert.Equal(HttpStatusCode.OK, given.StatusCode);
        var feedback = (await given.Content.ReadFromJsonAsync<NhAssistantProposalFeedbackDto>(Json))!;
        Assert.Equal(NhAssistantProposalFeedbackGroups.RightButNotNow, feedback.Group);

        // Each person sees their own feedback on the proposal; others see none.
        var colleagueView = Assert.Single(
            (await colleague.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json))!,
            item => item.ApprovalId == proposal.ApprovalId);
        Assert.Equal(NhAssistantProposalFeedbackCategories.NotNow, colleagueView.Feedback!.Category);
        var participantView = Assert.Single(
            (await participant.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json))!,
            item => item.ApprovalId == proposal.ApprovalId);
        Assert.Null(participantView.Feedback);
        Assert.False(participantView.CanGiveFeedback);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.Contains(
                scope.ServiceProvider.GetServices<INhAssistantProposalFeedbackSink>(),
                sink => sink is SampleProposalFeedbackLog);
        }

        // The next run on the project starts from the rejection and the approver's words.
        var model = new NhAiScriptedChatClient().RespondWithText("The budget review is still open, so I propose nothing yet.");
        host.Model.Use(model);
        try
        {
            var restart = await participant.PostAsJsonAsync(
                "/api/assistant/threads/start",
                new NhAssistantStartAgentRequest(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type, project.ToString("D"), "Check the project again."),
                Json);
            Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
            await WaitForRunCountAsync(owner, conversationId, 2);
            var continuity = model.Requests[^1].Messages
                .Select(message => message.Text)
                .Single(text => text.Contains("<agent-continuity>", StringComparison.Ordinal));
            Assert.Contains("<rejected-proposals>", continuity);
            Assert.Contains("not-now (right-but-not-now): \"Wait for the budget review before activating.\"", continuity);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }

        // Administrators read the rejection rate and the feedback per tool and agent version.
        var reportPath = $"/api/assistant/admin/agents/{SampleIntakeReviewerComposition.AgentId}/feedback";
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.GetAsync(reportPath + "/summary")).StatusCode);
        var summary = (await administrator.GetFromJsonAsync<NhAssistantFeedbackSummaryDto>(reportPath + "/summary", Json))!;
        Assert.Contains(summary.Rows, row => row.ToolId == ProjectAiTools.ChangeStatusToolId
            && row.Rejected > 0
            && row.RejectionRate is > 0
            && row.RejectedWithFeedback >= 1
            && row.Categories.Any(category => category.Category == NhAssistantProposalFeedbackCategories.NotNow));
        var list = (await administrator.GetFromJsonAsync<NhAssistantFeedbackListDto>(reportPath + "?category=not-now", Json))!;
        var listed = Assert.Single(list.Items, entry => entry.ApprovalId == proposal.ApprovalId);
        Assert.Equal("Wait for the budget review before activating.", listed.Text);
        Assert.Equal(host.ColleagueId.ToString(), listed.AuthorActorId);
        Assert.Equal("colleague@sample.localhost", listed.AuthorDisplayName);
        Assert.NotNull(listed.Presentation);

        using var export = await administrator.GetAsync(reportPath + "/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/x-ndjson", export.Content.Headers.ContentType?.MediaType);
        var exported = (await export.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<NhAssistantFeedbackExportRecord>(line, Json)!)
            .Single(record => record.Feedback.ApprovalId == proposal.ApprovalId);
        Assert.Contains(project.ToString("D"), exported.Arguments.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_project_shows_its_agent_work_and_a_project_list_counts_it_in_one_request()
    {
        // SPM-280
        var busy = await host.CreateProjectAsync("SUBJECT-BUSY", ownerId: host.OwnerId);
        var quiet = await host.CreateProjectAsync("SUBJECT-QUIET", ownerId: host.OwnerId);
        await host.AddMemberAsync(busy, host.ColleagueId, ProjectMemberRole.Approver);
        await host.AddMemberAsync(quiet, host.ParticipantId, ProjectMemberRole.Participant);
        await host.AddMemberAsync(quiet, host.ReaderId, ProjectMemberRole.Reader);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        using var reader = host.CreateClient(host.ReaderId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());
        var created = Assert.Single(await host.PublishProjectCreatedAsync(busy, createdBy: host.OwnerId));
        var proposal = await WaitForProposalAsync(owner, created.ConversationId!.Value, NhAssistantApprovalStatuses.Pending);

        // The project page reads all agent work on the project in one request, with live rights:
        // the approver decides, the creator is kept out by four eyes.
        var work = (await colleague.GetFromJsonAsync<NhAssistantSubjectWorkDto>($"/api/assistant/subjects/project/{busy:D}/work", Json))!;
        Assert.Equal("approver", work.Role);
        var reviewer = Assert.Single(work.Agents);
        Assert.Equal(SampleIntakeReviewerComposition.AgentId, reviewer.AgentId);
        Assert.Equal(created.ConversationId, reviewer.Thread!.ConversationId);
        var open = Assert.Single(work.OpenProposals);
        Assert.Equal(proposal.ApprovalId, open.Proposal.ApprovalId);
        Assert.True(open.CanDecide);
        var ownerWork = (await owner.GetFromJsonAsync<NhAssistantSubjectWorkDto>($"/api/assistant/subjects/project/{busy:D}/work", Json))!;
        Assert.Equal(NhAssistantErrorCodes.FourEyesRequired, Assert.Single(ownerWork.OpenProposals).ReasonCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/assistant/subjects/project/{busy:D}/work")).StatusCode);

        // A project list asks for every visible project at once.
        var summary = await colleague.PostAsJsonAsync(
            "/api/assistant/subjects/project/summary",
            new NhAssistantSubjectSummaryRequest([quiet.ToString("D"), busy.ToString("D")]),
            Json);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var counts = (await summary.Content.ReadFromJsonAsync<NhAssistantSubjectSummaryListDto>(Json))!.Items;
        Assert.Equal(0, counts[0].OpenProposals);
        Assert.Equal(1, counts[1].OpenProposals);
        Assert.Equal(1, counts[1].ProposalsToDecide);
        var outsiderCounts = (await (await outsider.PostAsJsonAsync(
                "/api/assistant/subjects/project/summary",
                new NhAssistantSubjectSummaryRequest([busy.ToString("D")]),
                Json))
            .Content.ReadFromJsonAsync<NhAssistantSubjectSummaryListDto>(Json))!.Items;
        Assert.Equal(0, Assert.Single(outsiderCounts).OpenProposals);

        // Opening the conversation about a project the reviewer never worked on creates its thread
        // without a run; a reader may not create it.
        var openRequest = new NhAssistantOpenThreadRequest(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type, quiet.ToString("D"));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/assistant/threads/open", openRequest, Json)).StatusCode);
        var opened = await participant.PostAsJsonAsync("/api/assistant/threads/open", openRequest, Json);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var lookup = (await opened.Content.ReadFromJsonAsync<NhAssistantThreadLookupDto>(Json))!;
        Assert.NotNull(lookup.Thread);
        Assert.Null(lookup.Thread!.ActiveRun);
        var runs = (await participant.GetFromJsonAsync<NhAssistantRunListDto>($"/api/assistant/threads/{lookup.Thread.ConversationId}/runs", Json))!;
        Assert.Empty(runs.Items);
        var again = (await (await reader.PostAsJsonAsync("/api/assistant/threads/open", openRequest, Json))
            .Content.ReadFromJsonAsync<NhAssistantThreadLookupDto>(Json))!;
        Assert.Equal(lookup.Thread.ConversationId, again.Thread!.ConversationId);
    }

    [Fact]
    public async Task The_owner_who_starts_the_intake_reviewer_by_hand_sees_its_run_start_and_end_live()
    {
        // SPM-280: the project page refreshes its suggestions and run timeline on AgentWorkChanged.
        // Nobody changed the members of a new project, so the assistant has no member snapshot of
        // it yet, and the owner never opened the reviewer's thread.
        var project = await host.CreateProjectAsync("LIVE-START", ownerId: host.OwnerId);
        var updates = Channel.CreateUnbounded<NhAssistantLiveAgentWorkDto>();
        await using var hub = await host.ConnectAssistantHubAsync(host.OwnerId.ToString());
        hub.On<NhAssistantLiveAgentWorkDto>(nameof(INhAssistantHubClient.AgentWorkChanged), update => updates.Writer.TryWrite(update));
        using var owner = host.CreateClient(host.OwnerId.ToString());

        // The reviewer looks and proposes nothing, so no notification reaches anyone.
        host.Model.Use(new NhAiScriptedChatClient().RespondWithText("The project needs nothing from me yet."));
        try
        {
            var started = await owner.PostAsJsonAsync(
                "/api/assistant/threads/start",
                new NhAssistantStartAgentRequest(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type, project.ToString("D"), "Have a look at this project."),
                Json);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var receipt = (await started.Content.ReadFromJsonAsync<NhAssistantTriggerReceiptDto>(Json))!;

            // The owner still hears that the run started and that it ended, without reloading.
            var seen = await ReadRunUpdatesAsync(updates.Reader, receipt.RunId!.Value, count: 2);
            Assert.All(seen, update =>
            {
                Assert.Equal(receipt.ConversationId, update.ConversationId);
                Assert.Equal(ProjectSubjects.Type, update.SubjectType);
                Assert.Equal(project.ToString("D"), update.SubjectId);
            });

            // Reloading on the update shows the ended run.
            var completed = await WaitForRunAsync(owner, receipt.ConversationId!.Value, NhAssistantRunStatuses.Completed);
            Assert.Equal(receipt.RunId, completed.Id);
        }
        finally
        {
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    [Fact]
    public async Task People_find_their_own_projects_by_name_and_read_the_project_type_named_and_linked()
    {
        // SPM-301: the project subject type has English and Dutch names, stands for the Project
        // entity, links to the portal's page of a project's agent work and is searched by its
        // resolver, which looks among the caller's own projects.
        var owned = await host.CreateProjectAsync("FINDABLE", ownerId: host.OwnerId);
        var shared = await host.CreateProjectAsync("FINDABLE", ownerId: host.ColleagueId);
        await host.AddMemberAsync(shared, host.OwnerId, ProjectMemberRole.Reader);
        await host.CreateProjectAsync("FINDABLE", ownerId: host.ColleagueId);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());

        // Any case of the name finds the projects the owner owns or is a member of, never a
        // colleague's own project; each result links to the project's page.
        var found = (await owner.GetFromJsonAsync<NhAssistantSubjectSearchDto>(
            "/api/assistant/subjects/project/search?query=findable%20PROJ&limit=10",
            Json))!;
        Assert.Equal(ProjectSubjects.Type, found.SubjectType);
        Assert.Equal(
            new[] { owned, shared }.Select(id => id.ToString("D")).Order(StringComparer.Ordinal),
            found.Items.Select(item => item.Id).Order(StringComparer.Ordinal));
        var ownedResult = Assert.Single(found.Items, item => item.Id == owned.ToString("D"));
        Assert.EndsWith(" — FINDABLE project", ownedResult.DisplayName);
        Assert.Equal(host.DivisionId, ownedResult.DivisionId);
        Assert.Equal(NhAiSubjectInfo.CreateScopeKey(null, host.DivisionId), ownedResult.ScopeKey);
        Assert.Equal($"/management/intake-reviews?project={owned:D}", ownedResult.Link);
        Assert.Empty((await outsider.GetFromJsonAsync<NhAssistantSubjectSearchDto>(
            "/api/assistant/subjects/project/search?query=findable",
            Json))!.Items);
        using (var tooShort = await owner.GetAsync("/api/assistant/subjects/project/search?query=f"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        }

        // The thread and the work on a project name the type in the reader's language and link to it.
        using var opened = await owner.PostAsJsonAsync(
            "/api/assistant/threads/open",
            new NhAssistantOpenThreadRequest(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type, owned.ToString("D")),
            Json);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var thread = (await opened.Content.ReadFromJsonAsync<NhAssistantThreadLookupDto>(Json))!.Thread!;
        Assert.Equal("Project", thread.SubjectTypeName);
        Assert.Equal(ownedResult.Link, thread.SubjectLink);
        var work = (await owner.GetFromJsonAsync<NhAssistantSubjectWorkDto>($"/api/assistant/subjects/project/{owned:D}/work", Json))!;
        Assert.Equal("Project", work.SubjectTypeName);
        Assert.Equal(ownedResult.Link, work.SubjectLink);

        // The run catalog describes the type for the administration, in Dutch for a Dutch reader.
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        administrator.DefaultRequestHeaders.AcceptLanguage.Clear();
        administrator.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");
        var catalog = (await administrator.GetFromJsonAsync<NhAssistantRunCatalogDto>("/api/assistant/admin/run-catalog", Json))!;
        // The names come from the application's resources, as its other assistant texts do.
        var dutch = CultureInfo.GetCultureInfo("nl-NL");
        var resources = new ResourceManager(typeof(SampleAssistantToolPresenter));
        Assert.Equal(
            new NhAssistantSubjectTypeDto(
                ProjectSubjects.Type,
                resources.GetString("SubjectTypeProject", dutch)!,
                resources.GetString("SubjectTypeProjects", dutch)!,
                nameof(Project),
                typeof(Project).FullName,
                ProjectSubjects.LinkTemplate,
                Searchable: true,
                IsSystem: false),
            Assert.Single(catalog.SubjectTypes, type => type.Id == ProjectSubjects.Type));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_project_search_stays_one_case_insensitive_server_side_query_on_both_providers(bool sqlServer)
    {
        // SPM-301: the search above runs on PostgreSQL; SQL Server translates the same query.
        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>();
        if (sqlServer)
        {
            options.UseSqlServer("Server=localhost;Database=TranslationOnly;Integrated Security=true");
        }
        else
        {
            options.UseNpgsql("Host=localhost;Database=translation_only");
        }

        using var dbContext = new SampleProjectManagementDbContext(options.Options);
        var sql = ProjectSubjectAccessResolver.Find(dbContext.Projects, Guid.NewGuid(), "Find", 20).ToQueryString();

        Assert.Contains("lower(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sqlServer ? "TOP" : "LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads live updates until <paramref name="count"/> of them named the run.
    /// </summary>
    private static async Task<NhAssistantLiveAgentWorkDto[]> ReadRunUpdatesAsync(
        ChannelReader<NhAssistantLiveAgentWorkDto> updates,
        Guid runId,
        int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var seen = new List<NhAssistantLiveAgentWorkDto>();
        try
        {
            while (seen.Count < count)
            {
                var update = await updates.ReadAsync(timeout.Token);
                if (update.Reason == "run" && update.RunId == runId)
                {
                    seen.Add(update);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"{seen.Count} of {count} live updates about run {runId} arrived.");
        }

        return seen.ToArray();
    }

    /// <summary>
    /// Waits until the thread has at least <paramref name="count"/> runs and every run has ended.
    /// </summary>
    private static async Task<NhAssistantRunDto[]> WaitForRunCountAsync(HttpClient client, Guid conversationId, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        NhAssistantRunDto[] runs = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            runs = (await client.GetFromJsonAsync<NhAssistantRunListDto>($"/api/assistant/threads/{conversationId}/runs", Json))?.Items.ToArray() ?? [];
            if (runs.Length >= count && runs.All(HasEnded))
            {
                return runs;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"The thread has {runs.Length} runs: {string.Join(", ", runs.Select(run => run.Status + (run.Summary is null ? " without summary" : string.Empty)))}.");
    }

    /// <summary>
    /// A run writes its summary after its status became terminal, and the next run of the thread
    /// starts from that summary; a run has ended once both are there.
    /// </summary>
    private static bool HasEnded(NhAssistantRunDto run)
    {
        return NhAssistantRunStatuses.IsTerminal(run.Status) && run.Summary is not null;
    }

    private static Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, byte[] body, string deliveryId, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var prefix = System.Text.Encoding.UTF8.GetBytes(timestamp + "." + deliveryId + ".");
        var signature = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret),
            (byte[])[.. prefix, .. body]));
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/assistant/triggers/" + SampleIntakeReviewerComposition.ReviewBoardTriggerId)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-NH-Timestamp", timestamp);
        request.Headers.Add("X-NH-Delivery", deliveryId);
        request.Headers.Add("X-NH-Signature", "sha256=" + signature);
        return client.SendAsync(request);
    }

    private static async Task<Guid> WaitForThreadAsync(
        HttpClient client,
        Guid projectId,
        string agentId = SampleIntakeReviewerComposition.AgentId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var lookup = await client.GetFromJsonAsync<NhAssistantThreadLookupDto>(
                $"/api/assistant/threads?agentId={agentId}&subjectType={ProjectSubjects.Type}&subjectId={projectId:D}",
                Json);
            if (lookup?.Thread is { } thread)
            {
                return thread.ConversationId;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"No thread of agent {agentId} was created for project {projectId}.");
    }

    /// <summary>
    /// Waits until the replay ended.
    /// </summary>
    private static async Task<NhAssistantReplayDto> WaitForReplayAsync(HttpClient client, string variantId, Guid replayId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        NhAssistantReplayDto? replay = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var list = await client.GetFromJsonAsync<NhAssistantReplayListDto>($"/api/assistant/admin/agents/{variantId}/replays", Json);
            replay = list?.Items.FirstOrDefault(item => item.Id == replayId);
            if (replay is not null && replay.Status != NhAssistantReplayStatuses.Running)
            {
                return replay;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException($"Replay {replayId} is still {replay?.Status ?? "missing"} after {replay?.RunsReplayed ?? 0} runs.");
    }

    /// <summary>
    /// Waits until the thread has an ended run of <paramref name="triggerId"/>.
    /// </summary>
    private static async Task<NhAssistantRunDto> WaitForRunWithTriggerAsync(HttpClient client, Guid conversationId, string triggerId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        NhAssistantRunDto[] runs = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            runs = (await client.GetFromJsonAsync<NhAssistantRunListDto>($"/api/assistant/threads/{conversationId}/runs", Json))?.Items.ToArray() ?? [];
            if (runs.FirstOrDefault(run => run.TriggerId == triggerId && HasEnded(run)) is { } found)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"The thread has no ended run of trigger {triggerId}: {string.Join(", ", runs.Select(run => run.TriggerId + " " + run.Status))}.");
    }

    private static async Task<NhAssistantRunDto> WaitForRunAsync(HttpClient client, Guid conversationId, string status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        NhAssistantRunDto[] runs = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/assistant/threads/{conversationId}/runs");
            if (response.IsSuccessStatusCode)
            {
                runs = (await response.Content.ReadFromJsonAsync<NhAssistantRunListDto>(Json))?.Items.ToArray() ?? [];
                if (runs.FirstOrDefault(item => item.Status == status) is { } match)
                {
                    return match;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"No run reached '{status}'; seen: {string.Join(", ", runs.Select(item => item.Status + "/" + item.FailureCode))}.");
    }

    private static async Task<NhAssistantConversationDto> ReadConversationAsync(HttpClient client, Guid conversationId)
    {
        using var response = await client.GetAsync($"/api/assistant/conversations/{conversationId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(NhAssistantJsonSerializerContext.Default.NhAssistantConversationDto))!;
    }

    private static string TextOf(NhAssistantMessageDto message)
    {
        return string.Concat(message.Parts.OfType<NhAssistantTextPartDto>().Select(part => part.Text));
    }

    private async Task<NhAssistantAgentState> ChangeShadowAsync(string agentId, SampleShadowChange change, string? reason)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var changed = await scope.ServiceProvider.GetRequiredService<SampleAgentMaintenanceService>()
            .ChangeShadowAsync(agentId, change, host.OwnerId, reason, CancellationToken.None);
        Assert.NotNull(changed);
        Assert.True(changed.Success);
        return changed.Data!;
    }

    private async Task<NhAssistantSkillState> ChangeSkillShadowAsync(string agentId, string skillId, SampleShadowChange change, string? reason)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var changed = await scope.ServiceProvider.GetRequiredService<SampleAgentMaintenanceService>()
            .ChangeSkillShadowAsync(agentId, skillId, change, host.OwnerId, reason, CancellationToken.None);
        Assert.NotNull(changed);
        Assert.True(changed.Success);
        return changed.Data!;
    }

    private async Task<TaskResult> ConfigureAgentsAsync(Func<SampleAgentConfigurationService, Task<TaskResult>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleAgentConfigurationService>());
    }

    private async Task<TaskResult<T>> ConfigureAgentsAsync<T>(Func<SampleAgentConfigurationService, Task<TaskResult<T>>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleAgentConfigurationService>());
    }

    private async Task<TaskResult<NhAssistantAgentState>> ChangeServiceAccountAsync(string agentId, Guid? accountId, string? reason)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var changed = await scope.ServiceProvider.GetRequiredService<SampleAgentMaintenanceService>()
            .ChangeServiceAccountAsync(agentId, accountId, host.OwnerId, reason, CancellationToken.None);
        Assert.NotNull(changed);
        return changed;
    }

    /// <summary>
    /// SPM-293: waits until an administrator sees the agent's shadow thread of the project.
    /// </summary>
    private static async Task<Guid> WaitForShadowThreadAsync(HttpClient administrator, Guid projectId, string agentId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var work = await administrator.GetFromJsonAsync<NhAssistantSubjectWorkDto>($"/api/assistant/subjects/project/{projectId:D}/work", Json);
            if (work?.Agents.FirstOrDefault(agent => agent.AgentId == agentId)?.ShadowThread is { } thread)
            {
                Assert.True(thread.Shadow);
                return thread.ConversationId;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"No shadow thread of agent {agentId} was created for project {projectId}.");
    }

    private async Task<NhAssistantAgentState> ChangeAgentAsync(string agentId, SampleAgentStateChange change, string? reason)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var changed = await scope.ServiceProvider.GetRequiredService<SampleAgentMaintenanceService>()
            .ChangeAsync(agentId, change, host.OwnerId, reason, CancellationToken.None);
        Assert.NotNull(changed);
        Assert.True(changed.Success);
        return changed.Data!;
    }

    private async Task<NhAssistantAgentState> AgentStateAsync(string agentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var states = await scope.ServiceProvider.GetRequiredService<SampleAgentMaintenanceService>().ListAsync(CancellationToken.None);
        return states.Single(state => state.AgentId == agentId);
    }

    private static async Task<NhAssistantRunDto[]> RunsAsync(HttpClient client, Guid conversationId)
    {
        return (await client.GetFromJsonAsync<NhAssistantRunListDto>($"/api/assistant/threads/{conversationId}/runs", Json))?.Items.ToArray() ?? [];
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, NhAssistantProposalDto proposal, string decision)
    {
        return client.PostAsJsonAsync(
            $"/api/assistant/approvals/{proposal.ApprovalId}/decisions",
            new NhAssistantProposalDecisionRequest(decision, proposal.ProposalHash, "Sample decision"),
            Json);
    }

    private static async Task<NhAssistantProposalDto> WaitForProposalAsync(
        HttpClient client,
        Guid conversationId,
        string status,
        string? toolId = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        NhAssistantProposalDto[] proposals = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/assistant/threads/{conversationId}/proposals");
            if (response.IsSuccessStatusCode)
            {
                proposals = await response.Content.ReadFromJsonAsync<NhAssistantProposalDto[]>(Json) ?? [];
                if (proposals.FirstOrDefault(item => item.Status == status && (toolId is null || item.ToolId == toolId)) is { } match)
                {
                    return match;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"No proposal reached '{status}'; seen: {string.Join(", ", proposals.Select(item => item.Status))}.");
    }
}

/// <summary>
/// The assistant sample host with the platform's background operations on Hangfire in memory and
/// the intake reviewer of Program.cs. The sample database is migrated before the host starts so
/// the background-operation dispatcher finds its tables from its first poll.
/// </summary>
public sealed class AssistantAgentRunSampleHost : AssistantCollaborationSampleHost
{
    private const string JwtIssuer = "sample-project-management";
    private const string JwtKey = "sample-project-management-signing-key-2026";
    private readonly InMemoryStorage _hangfireStorage = new();

    public Guid ReaderId { get; } = Guid.NewGuid();

    public Guid ParticipantId { get; } = Guid.NewGuid();

    /// <summary>
    /// SPM-294: the technical account the closer answers to by configuration, and the operations
    /// account an operator lets take over.
    /// </summary>
    public Guid TechnicalAccountId { get; } = Guid.NewGuid();

    public Guid OperationsAccountId { get; } = Guid.NewGuid();

    public FakeReviewRequests Reviews { get; } = new();

    public const string ReviewBoardSecret = "sample-review-board-secret-for-tests-only";

    public override async ValueTask InitializeAsync()
    {
        Model.Use(new SampleAssistantChatClient());
        await base.InitializeAsync();
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        AddUser(dbContext, ReaderId, "reader@sample.localhost");
        AddUser(dbContext, ParticipantId, "participant@sample.localhost");
        AddUser(dbContext, TechnicalAccountId, "closer-account@sample.localhost");
        AddUser(dbContext, OperationsAccountId, "operations-account@sample.localhost");
        await dbContext.SaveChangesAsync();
        // The technical accounts may sign in, so the closer's runs can answer to them.
        var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
        foreach (var account in new[] { TechnicalAccountId, OperationsAccountId })
        {
            await users.UpdateSecurityStampAsync((await users.FindByIdAsync(account.ToString()))!);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _hangfireStorage.Dispose();
    }

    protected override async Task OnDatabaseStartedAsync()
    {
        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using var dbContext = new SampleProjectManagementDbContext(options);
        await dbContext.Database.MigrateAsync();
    }

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        settings["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = JwtIssuer;
        settings["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = JwtKey;
        // As the sample's secrets.json provides it: NewHeap configuration loads that file under "Secrets".
        settings[$"{NhConfigurationSecrets.Prefix}:{SampleIntakeReviewerComposition.ReviewBoardSecretKey}"] = ReviewBoardSecret;
        // SPM-290: the bridge calls this host itself; the handler below sends it to the real port.
        settings[SampleAiBridgeComposition.SelfBaseUrlKey] = "http://sample.test/";
    }

    protected override void ConfigureAuthorization(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        base.ConfigureAuthorization(options);
        // The API's controller policies follow the permission claims, as in Program.cs. Tokens of the
        // NewHeap identity carry them as NhPlatformClaimTypes.Permission, the test sign-in as "permission".
        options.AddPolicy("app.project.view", policy => policy.RequireAssertion(context => HasPermission(context.User, "app.project.view")));
        options.AddPolicy("app.project.manage", policy => policy.RequireAssertion(context => HasPermission(context.User, "app.project.manage")));
    }

    private static bool HasPermission(ClaimsPrincipal user, string permission)
    {
        return user.HasClaim(NhPlatformClaimTypes.Permission, permission) || user.HasClaim("permission", permission);
    }

    protected override void MapEndpoints(WebApplication app)
    {
        app.MapControllers();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        // As in the sample's appsettings, the application defaults to Dutch, so headless runs make
        // their proposals in Dutch and English readers depend on the per-culture presentations.
        services.PostConfigure<RequestLocalizationOptions>(options =>
            options.DefaultRequestCulture = new RequestCulture(CultureInfo.GetCultureInfo("nl-NL")));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = JwtIssuer,
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:ValidAudience"] = JwtIssuer,
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = JwtKey
            })
            .Build();
        var platformOptions = NewHeapAspNetCommonOptions
            .Builder(configuration)
            .ConfigureAutoMapper(options => options.AddMaps(typeof(AutomapperProfileConfiguration).Assembly))
            .Build();
        services
            .AddNewHeapPlatformAspNetCommon<
                NhUser,
                NhUserRole,
                NhDivision,
                NhDivisionUser,
                NhDivisionRole,
                NhDivisionUserRole,
                NhDivisionRoleClaim,
                NhLog,
                NhLogMessageArgument,
                NhLogFile,
                NhLogMessageTranslated,
                NhDbLogService,
                SampleProjectManagementDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(platformOptions)
            // SPM-290: the built-in delegated read issuer creates tokens with the authentication service.
            .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(_ => { })
            .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql(ConnectionString))
            .WithIdentity()
            .WithDbLogService(_ => { })
            .WithHangfire(
                options => options.UseStorage(_hangfireStorage),
                backgroundJobServerOptions: server =>
                {
                    server.ServerName = "sample-agent-runs-" + Guid.NewGuid().ToString("N");
                    server.WorkerCount = 2;
                    server.SchedulePollingInterval = TimeSpan.FromMilliseconds(100);
                })
            .WithBackgroundOperations(operations =>
            {
                operations.Options.DispatchInterval = TimeSpan.FromMilliseconds(200);
                operations.Options.LiveUpdatesEnabled = false;
                operations.Options.UserNotificationProjectionEnabled = false;
                operations.Options.CleanupEnabled = false;
                operations.AddAssistantAgentRuns();
            });
        // The platform defaults to JWT bearer and Identity cookies; the sample tests sign in with
        // a header.
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = SampleTestAuthenticationHandler.Scheme;
            options.DefaultAuthenticateScheme = SampleTestAuthenticationHandler.Scheme;
            options.DefaultChallengeScheme = SampleTestAuthenticationHandler.Scheme;
            options.DefaultForbidScheme = SampleTestAuthenticationHandler.Scheme;
            options.DefaultSignInScheme = SampleTestAuthenticationHandler.Scheme;
        });
        services.AddScopedNhDbRepository<ProjectMember>();
        services.AddScoped<ProjectMemberService>();
        // ProjectService deletes projects for SPM-276; its events go nowhere in this host.
        services.AddScopedNhDbRepository<Project>();
        services.AddScoped<ProjectService>();
        // SPM-282: people tick tasks off by hand through the task service.
        services.AddScopedNhDbRepository<ProjectTask>();
        services.AddScoped<ProjectTaskService>();
        // SPM-284: the operator pauses, stops and resumes the closer through the maintenance page.
        services.AddScoped<SampleAgentMaintenanceService>();
        services.AddScoped<SampleAgentConfigurationService>();
        services.AddSingleton(Substitute.For<INhEventPublisher>());
        services.AddSingleton<SampleEventLog>();
        // SPM-290: the planner reads the project's tasks through the gateway over this host's own
        // HTTP pipeline, with a delegated read credential from the built-in issuer.
        // Only the API's project controllers: the bridge's resources and one controller outside it.
        services.AddControllers()
            .AddApplicationPart(typeof(ProjectController).Assembly)
            .ConfigureApplicationPartManager(manager =>
            {
                manager.FeatureProviders.Remove(manager.FeatureProviders.OfType<ControllerFeatureProvider>().Single());
                manager.FeatureProviders.Add(new SelectedControllers(
                    typeof(ProjectController),
                    typeof(ProjectTaskController),
                    typeof(ProjectMemberController)));
            });
        services.AddSampleAiBridge();
        services.AddHttpClient(NhAiMvcBridgeDefaults.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SelfHttpHandler(this));
        services.AddNewHeapPlatformAIDelegatedReads();
    }

    private sealed class SelectedControllers(params Type[] controllers) : ControllerFeatureProvider
    {
        protected override bool IsController(System.Reflection.TypeInfo typeInfo)
        {
            return controllers.Contains(typeInfo.AsType());
        }
    }

    /// <summary>Sends the bridge's self-HTTP calls to the port this host listens on.</summary>
    private sealed class SelfHttpHandler(AssistantAgentRunSampleHost host) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(host.BaseAddress, request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        services.AddSingleton<IProjectReviewRequestService>(Reviews);
        services.AddSampleIntakeReviewer();
        services.AddSampleStatusAdvisor();
        // SPM-294: the closer answers to the technical account in configuration.
        services.AddSampleProjectCloser(TechnicalAccountId.ToString("D"));
        services.AddSampleProjectPlanner();
        services.AddSampleProjectPlannerVariant();
        services.AddSampleProjectStarterBlocks();
    }

    public async Task WaitForOperationAsync(Guid operationId, NhBackgroundOperationStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var current = NhBackgroundOperationStatus.PendingDispatch;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using (var scope = Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
                current = await dbContext.Set<NhBackgroundOperation>()
                    .AsNoTracking()
                    .Where(item => item.Id == operationId)
                    .Select(item => item.Status)
                    .SingleAsync();
            }

            if (current == status)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException($"Operation {operationId} stayed {current} instead of {status}.");
    }

    /// <summary>
    /// SPM-280: connects to the assistant hub as the person, as the management portal does, and
    /// returns once the hub put the connection in that person's group.
    /// </summary>
    public async Task<HubConnection> ConnectAssistantHubAsync(string user)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(BaseAddress, "/hub/assistant"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[SampleTestAuthenticationHandler.UserHeader] = user;
            })
            .Build();
        await connection.StartAsync();

        // The hub takes no calls. It answers one only after it handled the connection, so the
        // refusal shows that live updates for the person reach this connection from now on.
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Ready"));
        return connection;
    }

    /// <summary>
    /// SPM-290: an active person of the division with the given permissions and a security stamp, so
    /// the NewHeap authentication service can issue their tokens.
    /// </summary>
    public async Task<Guid> CreatePersonAsync(params string[] permissions)
    {
        var id = Guid.NewGuid();
        await using (var scope = Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
            AddUser(dbContext, id, $"person-{id:N}@sample.localhost");
            await dbContext.SaveChangesAsync();
        }
        await using (var scope = Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
            var user = (await users.FindByIdAsync(id.ToString()))!;
            await users.UpdateSecurityStampAsync(user);
            foreach (var permission in permissions)
            {
                await users.AddClaimAsync(user, new Claim(NhPlatformClaimTypes.Permission, permission));
            }
        }
        return id;
    }

    /// <summary>Locks the person out for an hour, as an administrator blocks a user.</summary>
    public async Task LockOutAsync(Guid personId)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
        var user = (await users.FindByIdAsync(personId.ToString()))!;
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));
    }

    public async Task<Guid> CreateProjectAsync(
        string key,
        Guid ownerId,
        ProjectStatus status = ProjectStatus.Draft,
        DateTimeOffset? deadline = null,
        Guid? divisionId = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            DivisionId = divisionId ?? DivisionId,
            OwnerUserId = ownerId,
            Key = key + "-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Name = key + " project",
            Status = status,
            Deadline = deadline
        };
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project.Id;
    }

    public async Task<Guid> AddMemberAsync(Guid projectId, Guid userId, ProjectMemberRole role)
    {
        await using var scope = Services.CreateAsyncScope();
        var members = scope.ServiceProvider.GetRequiredService<ProjectMemberService>();
        var added = await members.CreateAsync(new ProjectMemberMutateModel
        {
            ProjectId = projectId,
            UserId = userId,
            Role = role
        });
        Assert.True(added.Success, string.Join("; ", added.AllErrorMessages.Select(error => error.ToString())));
        return added.Data!.Id;
    }

    public async Task RemoveMemberAsync(Guid membershipId)
    {
        await using var scope = Services.CreateAsyncScope();
        var members = scope.ServiceProvider.GetRequiredService<ProjectMemberService>();
        var removed = await members.DeleteAsync(membershipId);
        Assert.True(removed.Success);
    }

    /// <summary>
    /// Edits the project the way any update does: the modification time, and so the subject
    /// version, changes.
    /// </summary>
    public async Task TouchProjectAsync(Guid projectId)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        var project = await dbContext.Projects.SingleAsync(item => item.Id == projectId);
        project.Description = "Edited after the intake review.";
        project.LastModifiedDateTime = DateTimeOffset.UtcNow.AddSeconds(1);
        await dbContext.SaveChangesAsync();
    }

    public async Task<Guid> CreateTaskAsync(Guid projectId, string title)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        var task = new ProjectTask { Id = Guid.NewGuid(), ProjectId = projectId, Title = title };
        dbContext.ProjectTasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task.Id;
    }

    /// <summary>
    /// Completes a task the way the task page does, and returns the event the task service handed to
    /// the outbox for CAP to deliver after the commit.
    /// </summary>
    public async Task<ProjectTaskUpdatedEvent> CompleteTaskByHandAsync(Guid taskId, Guid userId)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        var task = await dbContext.ProjectTasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        var updated = await scope.ServiceProvider.GetRequiredService<ProjectTaskService>().UpdateAsync(
            taskId,
            new ProjectTaskMutateModel { ProjectId = task.ProjectId, Title = task.Title, IsCompleted = true },
            committedByUserId: userId);
        Assert.True(updated.Success);
        return Published<ProjectTaskUpdatedEvent>(evt => evt.ProjectTaskId == taskId);
    }

    /// <summary>
    /// Sets a project's status the way the project page does, and returns the event for CAP.
    /// </summary>
    public async Task<ProjectStatusChangedEvent> SetStatusByHandAsync(Guid projectId, ProjectStatus status, Guid userId)
    {
        await using var scope = Services.CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<ProjectService>().UpdateStatusAsync(
            projectId,
            new ProjectStatusMutateModel { Status = status },
            committedByUserId: userId);
        Assert.True(updated.Success);
        return Published<ProjectStatusChangedEvent>(evt => evt.ProjectId == projectId && evt.Status == status);
    }

    /// <summary>
    /// Delivers a task update through the sample's consumer, as CAP does after the commit.
    /// </summary>
    public async Task DeliverAsync(ProjectTaskUpdatedEvent evt)
    {
        await using var scope = Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<ProjectTaskEventConsumer>(scope.ServiceProvider);
        await consumer.HandleAsync(evt, CancellationToken.None);
    }

    /// <summary>
    /// Delivers a status change through the sample's consumer, as CAP does after the commit.
    /// </summary>
    public async Task DeliverAsync(ProjectStatusChangedEvent evt)
    {
        await using var scope = Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<ProjectStatusEventConsumer>(scope.ServiceProvider);
        await consumer.HandleAsync(evt, CancellationToken.None);
    }

    private TEvent Published<TEvent>(Func<TEvent, bool> matches)
        where TEvent : class, INhEvent
    {
        return Services.GetRequiredService<INhEventPublisher>()
            .ReceivedCalls()
            .SelectMany(call => call.GetArguments())
            .OfType<TEvent>()
            .Last(matches);
    }

    public Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishProjectCreatedAsync(Guid projectId, Guid? createdBy)
    {
        return PublishAsync(new ProjectCreatedEvent
        {
            ProjectId = projectId,
            ProjectKey = "PRJ",
            CreatedByUserId = createdBy
        });
    }

    /// <summary>
    /// Fires the triggers of the event and returns their receipts, as the sample's CAP consumer
    /// does without looking at them.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(ProjectCreatedEvent evt)
    {
        await using var scope = Services.CreateAsyncScope();
        var triggers = scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>();
        return await triggers.PublishAsync(evt);
    }

    /// <summary>
    /// Delivers the event the way CAP does after the project transaction committed: through the
    /// sample's own consumer, in a scope of its own.
    /// </summary>
    public async Task DeliverAsync(ProjectCreatedEvent evt)
    {
        await using var scope = Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<ProjectEventConsumer>(scope.ServiceProvider);
        await consumer.HandleAsync(evt, CancellationToken.None);
    }

    private void AddUser(SampleProjectManagementDbContext dbContext, Guid id, string email)
    {
        dbContext.Users.Add(new NhUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            // An active account: the identity requires a confirmed e-mail address to sign in, and
            // agents use the rights of people who may sign in only.
            EmailConfirmed = true,
            ActiveDivisionId = DivisionId
        });
        dbContext.Set<NhDivisionUser>().Add(new NhDivisionUser { Id = Guid.NewGuid(), UserId = id, DivisionId = DivisionId });
    }
}

/// <summary>
/// SPM-285: plays a script and reports a fixed number of tokens for every model call, as a model
/// with a large prompt would.
/// </summary>
public sealed class FixedTokenChatClient(NhAiScriptedChatClient script, int tokensPerCall) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return script.GetResponseAsync(messages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in script.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var usage in update.Contents.OfType<UsageContent>())
            {
                usage.Details.InputTokenCount = tokensPerCall;
                usage.Details.OutputTokenCount = 0;
                usage.Details.TotalTokenCount = tokensPerCall;
            }

            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return script.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        script.Dispose();
    }
}

/// <summary>
/// Plays scripted models on the shared host by request: the first route whose predicate matches
/// answers, everything else goes to the fallback. A consultation interrupts the asking turn, so one
/// script for both agents would interleave, and runs that earlier tests left behind must not take
/// a scripted answer.
/// </summary>
public sealed class RoutingChatClient(
    IChatClient fallback,
    params (Func<IReadOnlyList<ChatMessage>, ChatOptions?, bool> Matches, IChatClient Client)[] routes) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        return Route(list, options).GetResponseAsync(list, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        return Route(list, options).GetStreamingResponseAsync(list, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Whether the instructions or any message of the request contain <paramref name="text"/>.
    /// </summary>
    public static bool Mentions(IReadOnlyList<ChatMessage> messages, ChatOptions? options, string text)
    {
        return (options?.Instructions?.Contains(text, StringComparison.Ordinal) ?? false)
            || messages.Any(message => message.Text.Contains(text, StringComparison.Ordinal));
    }

    private IChatClient Route(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        foreach (var (matches, client) in routes)
        {
            if (matches(messages, options))
            {
                return client;
            }
        }

        return fallback;
    }
}

/// <summary>
/// Records review requests and fails them for chosen projects the way an unreachable review
/// board would: the call throws, so nobody knows whether the request arrived.
/// </summary>
public sealed class FakeReviewRequests : IProjectReviewRequestService
{
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private readonly ConcurrentDictionary<Guid, byte> _failing = new();

    public void FailFor(Guid projectId)
    {
        _failing[projectId] = 0;
    }

    public int Attempts(Guid projectId)
    {
        return _attempts.GetValueOrDefault(projectId);
    }

    public Task<TaskResult<ProjectAiReviewRequestReceipt>> RequestReviewAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        _attempts.AddOrUpdate(projectId, 1, (_, count) => count + 1);
        if (_failing.ContainsKey(projectId))
        {
            throw new HttpRequestException("The review board did not answer.");
        }

        return Task.FromResult(TaskResult<ProjectAiReviewRequestReceipt>.Succeeded(
            new ProjectAiReviewRequestReceipt(projectId, Guid.NewGuid())));
    }
}
