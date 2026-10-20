using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Proposals placed at their object (W5 of the plan for agents working on application objects, K11 and
/// K12): a run's proposal for another object waits in the agent's thread at that object after the
/// platform checked the object, the object's approvers decide it, it executes in the object's scope,
/// and the proposals of the whole application reach its approvers, on both relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantPlacedProposalTests(AssistantDatabaseFixture database)
{
    private const string FollowUp = "project-assistant";
    private const string ApplicationOnly = "application-statistics";
    private const string DivisionAgent = "division-assistant";
    private const string ResponsibleAgent = "responsible-assistant";
    private const string Admin = "admin-1";
    private const string Approver = "approver-1";
    private const string Member = "member-1";
    private const string Runner = "runner-1";
    private const string PolicyId = "placed-review";
    private const string ChangeStatusTool = "projects.change-status";

    private static readonly Guid North = Guid.Parse("c3000000-0000-0000-0000-0000000000c3");
    private static readonly Guid South = Guid.Parse("d4000000-0000-0000-0000-0000000000d4");

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_for_the_whole_application_places_its_proposals_at_the_objects_they_concern(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var first = AddProject(app);
        var second = AddProject(app);
        var run = await StartRunAsync(app, FollowUp, NhAiSystemSubjects.Global, initiator: Runner);
        Script(app).Run = async (context, cancellationToken) =>
        {
            foreach (var project in new[] { first, second })
            {
                Script(app).Proposals.Enqueue(await context.ProposeAsync(TestPlacementToolCatalog.CloseToolId, new ProjectCloseInput(project), _ => "The project has no open work.", cancellationToken));
            }
            return NhAssistantRuleResult.Done("Proposed to close two projects.");
        };

        var result = await RunTurnAsync(app, run);

        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        Assert.Equal(2, result.ProposalCount);
        var proposals = Script(app).Proposals.ToArray();
        Assert.All(proposals, proposal => Assert.Equal(NhAssistantProposeOutcome.Created, proposal.Outcome));
        Assert.Equal([Project(first), Project(second)], proposals.Select(proposal => proposal.PlacedAt).ToArray());

        // Each proposal waits in the agent's thread at its project, created for it, with the run that
        // made it; deferred, with the project's version and execution scope.
        var approvals = await WithRunsAsync(app, runs => runs.GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal(2, approvals.Count);
        foreach (var (approval, project) in approvals.Zip([first, second]))
        {
            var thread = (await WithRunsAsync(app, runs => runs.FindThreadByConversationAsync(approval.ConversationId, CancellationToken.None)))!;
            Assert.Equal((FollowUp, "project", project.ToString(), false), (thread.AgentId, thread.SubjectType, thread.SubjectId, thread.Shadow));
            Assert.Equal(run.Thread.ConversationId, approval.PlacedFromConversationId);
            Assert.Equal(run.Run.Id, approval.RunId);
            Assert.Equal(Runner, approval.InitiatorActorId);
            Assert.Equal((NhAssistantApprovalStatuses.Pending, NhAssistantApprovalModes.Deferred, PolicyId), (approval.Status, approval.Mode, approval.PolicyId));
            Assert.Equal("v-" + project.ToString("N")[..4], approval.SubjectVersion);
            Assert.Contains($"project:{project}", approval.TargetsJson, StringComparison.Ordinal);
            var waiting = (await WithStoreAsync(app, store => store.FindToolInvocationAsync(approval.ToolInvocationId, CancellationToken.None)))!;
            Assert.Equal((approval.ConversationId, NhAssistantToolCallStatuses.AwaitingApproval, approval.Id), (waiting.ConversationId, waiting.Status, waiting.ApprovalId));
        }

        // The run's own thread keeps its calls as placed; nothing waits there.
        await using (var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var calls = await context.ToolInvocations.Where(call => call.ConversationId == run.Thread.ConversationId).ToListAsync();
            Assert.Equal(2, calls.Count);
            Assert.All(calls, call => Assert.Equal((NhAssistantToolCallStatuses.NotExecuted, NhAssistantErrorCodes.ProposalPlaced), (call.Status, call.ResultCode)));
            Assert.Empty(await context.Approvals.Where(approval => approval.ConversationId == run.Thread.ConversationId).ToListAsync());
        }

        var placedAudit = app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events
            .Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalPlaced)
            .ToArray();
        Assert.Equal([first.ToString(), second.ToString()], placedAudit.Select(evt => evt.SubjectId).ToArray());
        Assert.All(placedAudit, evt => Assert.Equal((run.Thread.ConversationId.ToString("D"), run.Run.Id), (evt.ObjectId, evt.RunId)));

        // The project's approver finds them in the inbox with the run that placed them; the run's
        // initiator, who does not approve the projects, and a member do not.
        using var approver = app.CreateClient(Approver);
        approver.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");
        var inbox = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox"));
        Assert.Equal(2, inbox.GetProperty("total").GetInt32());
        var item = inbox.GetProperty("items").EnumerateArray().First();
        var placedBy = item.GetProperty("placedBy");
        Assert.Equal(run.Thread.ConversationId, placedBy.GetProperty("conversationId").GetGuid());
        Assert.Equal(run.Run.Id, placedBy.GetProperty("runId").GetGuid());
        Assert.Equal((NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId), (placedBy.GetProperty("subjectType").GetString(), placedBy.GetProperty("subjectId").GetString()));
        Assert.Equal("De hele applicatie", placedBy.GetProperty("subjectDisplayName").GetString());
        using var runner = app.CreateClient(Runner);
        Assert.Equal(0, (await ReadJsonAsync(await runner.GetAsync("/api/assistant/approvals/inbox"))).GetProperty("total").GetInt32());
        using var member = app.CreateClient(Member);
        Assert.Equal(0, (await ReadJsonAsync(await member.GetAsync("/api/assistant/approvals/inbox"))).GetProperty("total").GetInt32());

        // The project's thread lists the proposal with its origin for everyone who reads the project.
        var threadProposals = await ReadJsonAsync(await member.GetAsync($"/api/assistant/threads/{approvals[0].ConversationId}/proposals"));
        var listed = Assert.Single(threadProposals.EnumerateArray());
        Assert.Equal(approvals[0].Id, listed.GetProperty("approvalId").GetGuid());
        Assert.Equal(NhAiSubjectRef.SystemType, listed.GetProperty("placedBy").GetProperty("subjectType").GetString());

        // The same proposal is not placed twice: a later run is told it already waits there.
        var again = await StartRunAsync(app, FollowUp, NhAiSystemSubjects.Global);
        Script(app).Proposals.Clear();
        Script(app).Run = async (context, cancellationToken) =>
        {
            Script(app).Proposals.Enqueue(await context.ProposeAsync(TestPlacementToolCatalog.CloseToolId, new ProjectCloseInput(first), null, cancellationToken));
            return NhAssistantRuleResult.Done("Checked again.");
        };
        await RunTurnAsync(app, again);
        var duplicate = Assert.Single(Script(app).Proposals);
        Assert.Equal((NhAssistantProposeOutcome.AlreadyOpen, approvals[0].Id), (duplicate.Outcome, duplicate.ApprovalId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_placement_that_a_later_check_refuses_leaves_no_empty_thread_at_the_object(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var project = AddProject(app);
        var first = await RunAndProposeAsync(app, FollowUp, NhAiSystemSubjects.Global, project);
        Assert.Equal(NhAssistantProposeOutcome.Created, first.Outcome);
        var approval = Assert.Single(await WithRunsAsync(app, runs => runs.GetRunApprovalsAsync(Script(app).Contexts.Last().RunId, CancellationToken.None)));
        var placedThread = (await WithRunsAsync(app, runs => runs.FindThreadByConversationAsync(approval.ConversationId, CancellationToken.None)))!;
        var systemThread = (await WithRunsAsync(app, runs => runs.FindThreadAsync("global", FollowUp, NhAiSystemSubjects.Global, CancellationToken.None)))!;

        // The same proposal waits in another thread of the agent in the object's scope, and the agent
        // has no thread at the object (yet).
        await using (var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.Approvals
                .Where(item => item.Id == approval.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ConversationId, systemThread.ConversationId)
                    .SetProperty(item => item.PlacedFromConversationId, (Guid?)null));
        }
        await WithRunsAsync(app, async runs =>
        {
            await runs.DeleteThreadAsync(placedThread, CancellationToken.None);
            return true;
        });

        // The duplicate check refuses the next one; no thread was created at the object for it.
        var again = await RunAndProposeAsync(app, FollowUp, NhAiSystemSubjects.Global, project);
        Assert.Equal((NhAssistantProposeOutcome.AlreadyOpen, approval.Id), (again.Outcome, again.ApprovalId));
        Assert.Null(await WithRunsAsync(app, runs => runs.FindThreadAsync("global", FollowUp, Project(project), CancellationToken.None)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Placing_checks_what_the_agent_works_on_the_object_its_scope_the_access_and_the_limit(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, maxPlaced: 2);
        var open = AddProject(app);
        var hidden = AddProject(app, agentReads: false);
        var missing = Guid.NewGuid();
        var otherTenant = AddProject(app, tenant: "tenant-b");
        var inNorth = AddProject(app, division: North);
        var alsoNorth = AddProject(app, division: North);
        var inSouth = AddProject(app, division: South);

        // An agent that does not work on projects places nothing at a project.
        var statistics = await RunAndProposeAsync(app, ApplicationOnly, NhAiSystemSubjects.Global, open);
        AssertNotPlaced(statistics, NhAssistantPlacementReasons.DoesNotWorkOn);

        // The object must exist, in the run's tenant, and the account the run answers to must read it.
        AssertNotPlaced(await RunAndProposeAsync(app, FollowUp, NhAiSystemSubjects.Global, missing), NhAssistantPlacementReasons.NotFound);
        AssertNotPlaced(await RunAndProposeAsync(app, FollowUp, NhAiSystemSubjects.Global, hidden), NhAssistantPlacementReasons.NoAccess);
        var tenantRun = await RunAndProposeAsync(app, FollowUp, Project(AddProject(app, tenant: "tenant-a")), otherTenant);
        AssertNotPlaced(tenantRun, NhAssistantPlacementReasons.NotFound);

        // A per-division agent places in its own division only; an app-wide agent in any.
        Assert.Equal(Project(alsoNorth), (await RunAndProposeAsync(app, DivisionAgent, Project(inNorth), alsoNorth)).PlacedAt);
        AssertNotPlaced(await RunAndProposeAsync(app, DivisionAgent, Project(inNorth), inSouth), NhAssistantPlacementReasons.DoesNotWorkOn);
        Assert.Equal(Project(inSouth), (await RunAndProposeAsync(app, FollowUp, Project(inNorth), inSouth)).PlacedAt);

        // A refusal leaves the run working: the call ends as not placed and the audit names why.
        var refusals = app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events
            .Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalNotPlaced)
            .Select(evt => evt.ResultCode)
            .ToArray();
        Assert.Equal(
            [NhAssistantPlacementReasons.DoesNotWorkOn, NhAssistantPlacementReasons.NotFound, NhAssistantPlacementReasons.NoAccess, NhAssistantPlacementReasons.NotFound, NhAssistantPlacementReasons.DoesNotWorkOn],
            refusals);
        await using (var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var refused = await context.ToolInvocations.Where(call => call.ResultCode == NhAssistantErrorCodes.ProposalNotPlaced).ToListAsync();
            Assert.Equal(5, refused.Count);
            Assert.All(refused, call => Assert.Equal(NhAssistantToolCallStatuses.NotExecuted, call.Status));
            Assert.Contains("\"reason\":\"no-access\"", refused.Select(call => call.ResultJson).Single(json => json!.Contains("no-access", StringComparison.Ordinal)), StringComparison.Ordinal);
        }

        // One run places at most the configured number of proposals.
        var many = new[] { AddProject(app), AddProject(app), AddProject(app) };
        var limited = await StartRunAsync(app, FollowUp, NhAiSystemSubjects.Global);
        Script(app).Proposals.Clear();
        Script(app).Run = async (context, cancellationToken) =>
        {
            foreach (var project in many)
            {
                Script(app).Proposals.Enqueue(await context.ProposeAsync(TestPlacementToolCatalog.CloseToolId, new ProjectCloseInput(project), null, cancellationToken));
            }
            return NhAssistantRuleResult.Done("Proposed.");
        };
        await RunTurnAsync(app, limited);
        var outcomes = Script(app).Proposals.ToArray();
        Assert.Equal([NhAssistantProposeOutcome.Created, NhAssistantProposeOutcome.Created, NhAssistantProposeOutcome.Refused], outcomes.Select(item => item.Outcome).ToArray());
        Assert.Equal(NhAssistantPlacementReasons.Limit, outcomes[2].PlacementReason);
        Assert.Equal(2, await WithRunsAsync(app, runs => runs.CountPlacedProposalsAsync(limited.Run.Id, CancellationToken.None)));

        // A call about the run's own object, and a tool that declares no subject, stay in the run's
        // own thread, as MCP tools always do.
        var own = await RunAndProposeAsync(app, FollowUp, Project(open), open);
        Assert.Equal((NhAssistantProposeOutcome.Created, (NhAiSubjectRef?)null), (own.Outcome, own.PlacedAt));
        var undeclared = await StartRunAsync(app, FollowUp, NhAiSystemSubjects.Global);
        Script(app).Proposals.Clear();
        Script(app).Run = async (context, cancellationToken) =>
        {
            Script(app).Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(open, "Closed"), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed.");
        };
        await RunTurnAsync(app, undeclared);
        var kept = Assert.Single(Script(app).Proposals);
        Assert.Null(kept.PlacedAt);
        var keptApproval = (await WithStoreAsync(app, store => store.FindApprovalAsync(kept.ApprovalId!.Value, CancellationToken.None)))!;
        Assert.Equal((undeclared.Thread.ConversationId, (Guid?)null), (keptApproval.ConversationId, keptApproval.PlacedFromConversationId));

        // A run in shadow places only shadow proposals, in the agent's shadow thread at the object.
        var shadowRun = await StartRunAsync(app, FollowUp, NhAiSystemSubjects.Global, shadow: true);
        Script(app).Proposals.Clear();
        Script(app).Run = async (context, cancellationToken) =>
        {
            Script(app).Proposals.Enqueue(await context.ProposeAsync(TestPlacementToolCatalog.CloseToolId, new ProjectCloseInput(open), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed in shadow.");
        };
        await RunTurnAsync(app, shadowRun);
        var shadowApproval = Assert.Single(await WithRunsAsync(app, runs => runs.GetRunApprovalsAsync(shadowRun.Run.Id, CancellationToken.None)));
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, shadowApproval.Status);
        var shadowThread = (await WithRunsAsync(app, runs => runs.FindThreadByConversationAsync(shadowApproval.ConversationId, CancellationToken.None)))!;
        Assert.Equal((open.ToString(), true), (shadowThread.SubjectId, shadowThread.Shadow));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_placed_proposal_is_decided_by_the_objects_approvers_and_executes_in_the_objects_scope(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var project = AddProject(app, division: North);
        var run = await StartRunAsync(app, FollowUp, NhAiSystemSubjects.Global, initiator: Runner);
        Script(app).Run = async (context, cancellationToken) =>
        {
            Script(app).Proposals.Enqueue(await context.ProposeAsync(TestPlacementToolCatalog.CloseToolId, new ProjectCloseInput(project), null, cancellationToken));
            Script(app).Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(project, "Closed"), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed.");
        };
        await RunTurnAsync(app, run);
        var proposals = Script(app).Proposals.ToArray();
        var placed = proposals[0].ApprovalId!.Value;
        var kept = proposals[1].ApprovalId!.Value;

        // The run's initiator decides a proposal in the run's own thread under the initiator rule,
        // but not one placed at the project: that one waits for the project's approvers.
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyDenied, FirstCode(await DecideAsync(app, placed, Runner)));
        Assert.True((await DecideAsync(app, kept, Runner)).Success);
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyDenied, FirstCode(await DecideAsync(app, placed, Member)));
        Assert.True((await DecideAsync(app, placed, Approver)).Success);
        Assert.Contains(placed, app.Services.GetRequiredService<RecordingOperations>().Executions);

        // It executes in the project's scope, with the agent's account, from its copy of the call.
        await ExecuteAsync(app, placed);
        var approval = (await WithStoreAsync(app, store => store.FindApprovalAsync(placed, CancellationToken.None)))!;
        Assert.Equal(NhAssistantApprovalStatuses.Executed, approval.Status);
        var (input, context) = Assert.Single(app.Services.GetRequiredService<TestPlacementRecorder>().Closed);
        Assert.Equal(project, input.ProjectId);
        Assert.Contains(context.ExecutionScopes, scope => scope.Type == "project" && scope.Id == project.ToString());
        Assert.Equal(("assistant-agent:" + FollowUp, NhAiActorKind.Agent), (context.ActorId, context.ActorKind));
        var waiting = (await WithStoreAsync(app, store => store.FindToolInvocationAsync(approval.ToolInvocationId, CancellationToken.None)))!;
        Assert.Equal((approval.ConversationId, NhAssistantToolCallStatuses.Succeeded), (waiting.ConversationId, waiting.Status));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Proposals_of_the_whole_application_reach_its_approvers(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var project = AddProject(app);
        var run = await StartRunAsync(app, FollowUp, NhAiSystemSubjects.Global);
        Script(app).Run = async (context, cancellationToken) =>
        {
            // A tool without a declared subject proposes in the run's own thread of the whole application.
            Script(app).Proposals.Enqueue(await context.ProposeAsync(ChangeStatusTool, new ProjectStatusInput(project, "Closed"), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed.");
        };
        await RunTurnAsync(app, run);
        var approvalId = Assert.Single(Script(app).Proposals).ApprovalId!.Value;

        // The whole application lists no members: its approvers, who pass the approve policy now, find
        // the proposal in the inbox; a reader of the whole application does not.
        using var admin = app.CreateClient(Admin, admin: true);
        var inbox = await ReadJsonAsync(await admin.GetAsync("/api/assistant/approvals/inbox"));
        Assert.Equal(approvalId, Assert.Single(inbox.GetProperty("items").EnumerateArray()).GetProperty("approvalId").GetGuid());
        Assert.Equal(1, (await ReadJsonAsync(await admin.GetAsync("/api/assistant/approvals/inbox?subjectType=nh-system"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await ReadJsonAsync(await admin.GetAsync("/api/assistant/approvals/inbox?subjectType=project"))).GetProperty("total").GetInt32());
        using var member = app.CreateClient(Member);
        Assert.Equal(0, (await ReadJsonAsync(await member.GetAsync("/api/assistant/approvals/inbox"))).GetProperty("total").GetInt32());

        // The summary of the whole application counts it with the caller's live role.
        var adminSummary = await SummarizeAsync(admin);
        Assert.Equal((1, 1), (adminSummary.GetProperty("openProposals").GetInt32(), adminSummary.GetProperty("proposalsToDecide").GetInt32()));
        var memberSummary = await SummarizeAsync(member);
        Assert.Equal((1, 0), (memberSummary.GetProperty("openProposals").GetInt32(), memberSummary.GetProperty("proposalsToDecide").GetInt32()));

        // Notifications reach the approvers among the people of the thread, checked now: the
        // administrator who opened it is told, the member who opened it only reads.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var threads = scope.ServiceProvider.GetRequiredService<NhAssistantThreadService>();
            await threads.ResolveAccessAsync(run.Thread.ConversationId, Admin, "Admin", CancellationToken.None);
            await threads.ResolveAccessAsync(run.Thread.ConversationId, Member, "Member", CancellationToken.None);
            var approval = (await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
            await scope.ServiceProvider.GetRequiredService<NhAssistantAgentNotifier>().NotifyAsync(
                NhAssistantAgentNotificationKind.RunEndedWithProposals,
                run.Thread,
                run.Run.Id,
                approval,
                null,
                shadow: false,
                CancellationToken.None,
                proposalCount: 1);
        }
        var delivery = Assert.Single(app.Services.GetRequiredService<RecordingChannel>().Deliveries);
        Assert.Equal([Admin], delivery.Recipients);
        Assert.Equal(NhAiSystemSubjects.Global, delivery.Context.Subject);
    }

    private static async Task<JsonElement> SummarizeAsync(HttpClient client)
    {
        using var response = await client.PostAsync(
            "/api/assistant/subjects/nh-system/summary",
            ServerSentEventReader.Json(new { subjectIds = new[] { NhAiSystemSubjects.GlobalId } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await ReadJsonAsync(response)).GetProperty("items").EnumerateArray());
    }

    private static void AssertNotPlaced(NhAssistantRuleProposal proposal, string reason)
    {
        Assert.Equal(NhAssistantProposeOutcome.Refused, proposal.Outcome);
        Assert.Equal(NhAssistantErrorCodes.ProposalNotPlaced, proposal.Code);
        Assert.Equal(reason, proposal.PlacementReason);
        Assert.Null(proposal.ApprovalId);
    }

    private static async Task<NhAssistantRuleProposal> RunAndProposeAsync(
        AssistantWebApplication app,
        string agentId,
        NhAiSubjectRef subject,
        Guid project)
    {
        var run = await StartRunAsync(app, agentId, subject);
        Script(app).Proposals.Clear();
        Script(app).Run = async (context, cancellationToken) =>
        {
            Script(app).Proposals.Enqueue(await context.ProposeAsync(TestPlacementToolCatalog.CloseToolId, new ProjectCloseInput(project), null, cancellationToken));
            return NhAssistantRuleResult.Done("Proposed.");
        };
        var result = await RunTurnAsync(app, run);
        // A refused placement never fails the run.
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        return Assert.Single(Script(app).Proposals);
    }

    [Fact]
    public async Task A_proposal_for_an_object_where_nobody_answers_for_the_agent_is_not_placed_there_with_its_own_reason()
    {
        await using var app = await StartAsync(AssistantTestProvider.PostgreSql);
        var project = AddProject(app);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(ResponsibleAgent, includeDisabled: false, CancellationToken.None))!;
        var target = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(Project(project), CancellationToken.None))!;

        // The agent answers to the object's responsible person, and this project names none.
        var prepared = await services.GetRequiredService<NhAssistantProposalPlacement>().PrepareThreadAtAsync(agent, target, shadow: false, CancellationToken.None);

        Assert.False(prepared.Success);
        Assert.Equal(NhAssistantErrorCodes.NoAccountableOwner, NhAssistantResults.CodeOf(prepared));
        Assert.Equal(NhAssistantPlacementReasons.NoAccountableOwner, NhAssistantProposalPlacement.ReasonOf(prepared));
        Assert.Equal(NhAssistantPlacementReasons.NoAccess, NhAssistantProposalPlacement.ReasonOf(TaskResult.Failed("other-code", "Another failure.")));
        Assert.Null(await WithRunsAsync(app, runs => runs.FindThreadAsync(target.ScopeKey, ResponsibleAgent, target.Subject, CancellationToken.None, false)));
    }

    [Fact]
    public async Task The_tool_catalog_names_the_object_type_a_tool_places_its_proposals_at()
    {
        await using var app = await StartAsync(AssistantTestProvider.PostgreSql);
        using var admin = app.CreateClient(Admin, admin: true);

        // The administration's flow view shows where a tool's proposals of a run go.
        var catalog = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/tools"))).EnumerateArray().ToArray();

        var close = Assert.Single(catalog, entry => entry.GetProperty("id").GetString() == TestPlacementToolCatalog.CloseToolId);
        Assert.Equal("project", close.GetProperty("subjectType").GetString());
        var status = Assert.Single(catalog, entry => entry.GetProperty("id").GetString() == ChangeStatusTool);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("subjectType").ValueKind);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, int maxPlaced = 20)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            limits: limits => limits.MaxPlacedProposalsPerRun = maxPlaced,
            configure: services =>
            {
                services.AddSingleton<PlacementProjects>();
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<TestPlacementRecorder>();
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddSingleton<RecordingChannel>();
                services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiToolCatalog, TestPlacementToolCatalog>());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, PlacementPrincipals>());
                // Runs are driven here, not executed by background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<PlacementProjectResolver>(type => type.Named(new() { ["en"] = "Project", ["nl"] = "Project" }, fallbackLanguage: "en"))
                .AddBusinessAuditSink<CapturedBusinessAuditSinkAdapter>()
                .AddAgentNotificationChannel<RecordingChannelAdapter>()
                .AddApprovalPolicy(PolicyId, policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver), NhAiApprovers.Initiator)
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .AddAgent(AssistantTestData.Agent(ApplicationOnly))
                .AddAgent(AssistantTestData.Agent(DivisionAgent))
                .AddAgent(AssistantTestData.Agent(ResponsibleAgent))
                .ConfigureAgent(ResponsibleAgent, run => run
                    .WorksOn("project")
                    .Accountability(NhAiAgentAccountability.SubjectResponsible)
                    .UseApprovalPolicy(PolicyId)
                    .UseRuleHandler<TestRules>())
                .ConfigureAgent(FollowUp, run => run
                    .WorksOn("project")
                    .WorksOnApplication()
                    .Grant("projects.*")
                    .UseApprovalPolicy(PolicyId)
                    .UseRuleHandler<TestRules>())
                .ConfigureAgent(ApplicationOnly, run => run
                    .WorksOnApplication()
                    .Grant("projects.*")
                    .UseApprovalPolicy(PolicyId)
                    .UseRuleHandler<TestRules>())
                .ConfigureAgent(DivisionAgent, run => run
                    .WorksOn("project")
                    .PerDivision()
                    .Grant("projects.*")
                    .UseApprovalPolicy(PolicyId)
                    .UseRuleHandler<TestRules>()),
            provider: provider);
    }

    private static Guid AddProject(
        AssistantWebApplication app,
        bool agentReads = true,
        Guid? division = null,
        string? tenant = null)
    {
        var id = Guid.NewGuid();
        app.Services.GetRequiredService<PlacementProjects>().Projects[id.ToString()] = new PlacementProject(division, tenant, agentReads);
        return id;
    }

    private sealed record StartedRun(AssistantThread Thread, AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    private static async Task<StartedRun> StartRunAsync(
        AssistantWebApplication app,
        string agentId,
        NhAiSubjectRef subject,
        string? initiator = null,
        bool shadow = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadow)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = NhAssistantTriggerIds.Manual,
            InitiatorActorId = initiator,
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
            run.Id,
            NhAssistantTriggerIds.Manual,
            initiator,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        return new StartedRun(thread, run, agentTurn);
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantWebApplication app, StartedRun run)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        return await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                run.AgentTurn,
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, "Weekly round.")]),
            CancellationToken.None);
    }

    private static async Task<TaskResult<AssistantApproval>> DecideAsync(AssistantWebApplication app, Guid approvalId, string actorId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approvalId,
            actorId,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test")),
            approve: true,
            expectedProposalHash: null,
            reason: null,
            CancellationToken.None);
    }

    private static async Task ExecuteAsync(AssistantWebApplication app, Guid approvalId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var result = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalExecutor>().ExecuteAsync(
            approvalId,
            Substitute.For<INhBackgroundOperationContext>(),
            fenced: false,
            CancellationToken.None);
        Assert.True(result.Success);
    }

    private static string? FirstCode(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static TestRuleScript Script(AssistantWebApplication app)
    {
        return app.Services.GetRequiredService<TestRuleScript>();
    }

    private static async Task<T> WithRunsAsync<T>(AssistantWebApplication app, Func<NhAssistantRunStore, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>());
    }

    private static async Task<T> WithStoreAsync<T>(AssistantWebApplication app, Func<INhAssistantStore, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantStore>());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static NhAiSubjectRef Project(Guid id)
    {
        return new NhAiSubjectRef("project", id.ToString());
    }

    /// <summary>
    /// One project of the test: its division and tenant, and whether the agents' own accounts read it.
    /// </summary>
    private sealed record PlacementProject(Guid? DivisionId, string? TenantId, bool AgentReads);

    private sealed class PlacementProjects
    {
        public ConcurrentDictionary<string, PlacementProject> Projects { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Projects that exist only when the test added them. The approver approves every project, the
    /// member and the runner take part, and the agents' accounts read the projects they may.
    /// </summary>
    private sealed class PlacementProjectResolver(PlacementProjects projects) : INhAiSubjectAccessResolver
    {
        public string SubjectType => "project";

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            if (!projects.Projects.TryGetValue(subject.Id, out var project))
            {
                return ValueTask.FromResult<NhAiSubjectInfo?>(null);
            }

            return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(project.TenantId, project.DivisionId))
            {
                DivisionId = project.DivisionId,
                TenantId = project.TenantId,
                Version = "v-" + subject.Id.Replace("-", string.Empty, StringComparison.Ordinal)[..4],
                DisplayName = "Project " + subject.Id[..8]
            });
        }

        public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
        {
            if (!projects.Projects.TryGetValue(subject.Id, out var project))
            {
                return ValueTask.FromResult(NhAiSubjectAccess.None);
            }

            var role = actorId switch
            {
                Approver => NhAiSubjectRole.Approver,
                Member or Runner => NhAiSubjectRole.Participant,
                _ when actorId.StartsWith("assistant-agent:", StringComparison.Ordinal) && project.AgentReads => NhAiSubjectRole.Reader,
                _ => NhAiSubjectRole.None
            };
            return ValueTask.FromResult(role == NhAiSubjectRole.None ? NhAiSubjectAccess.None : new NhAiSubjectAccess(role));
        }

        public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
            NhAiSubjectRef subject,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new NhAiSubjectMember(Approver, NhAiSubjectRole.Approver) { DisplayName = Approver };
            yield return new NhAiSubjectMember(Member, NhAiSubjectRole.Participant) { DisplayName = Member };
            yield return new NhAiSubjectMember(Runner, NhAiSubjectRole.Participant) { DisplayName = Runner };
        }
    }

    /// <summary>
    /// The people as headless runs and the whole application see them: the administrator passes the
    /// administration policy, everyone else uses the assistant.
    /// </summary>
    private sealed class PlacementPrincipals : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            var permissions = actorId == Admin ? new[] { "assistant", "assistant-admin" } : new[] { "assistant" };
            return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(value => new Claim("permission", value)).Append(new Claim(ClaimTypes.NameIdentifier, actorId)),
                "test")));
        }
    }
}
