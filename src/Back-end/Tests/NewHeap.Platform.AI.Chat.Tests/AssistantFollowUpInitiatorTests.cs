using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
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
/// A follow-up run continues the work of the run that made the proposal: it keeps that work's
/// initiator, or none for trigger work, after a rejection, an executed approval and a refused approval
/// of a changed subject alike. The person who decided stays in the approval and its audit only, so
/// four eyes keeps the requester out of the follow-up's proposals and the decider lends it no rights.
/// </summary>
public sealed class AssistantFollowUpInitiatorTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Requester = "requester-1";
    private const string Decider = "approver-2";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_follow_up_run_keeps_the_initiator_of_the_proposal_and_never_takes_the_decider(AssistantTestProvider provider)
    {
        Guid[] projects = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await using var host = await CreateHostAsync(provider, projects);
        var subjects = host.Services.GetRequiredService<TestSubjects>();

        // A rejection follows up at once, for the person who asked.
        var rejected = await ProposeAsync(host, projects[0], Requester);
        Assert.True((await DecideAsync(host, Decider, rejected.Approval.Id, approve: false)).Success);
        var afterRejection = await LatestRunAsync(host, rejected.Thread.ConversationId);
        Assert.Equal(
            (NhAssistantTriggerIds.ProposalDecided, (string?)Requester),
            (afterRejection.TriggerId, afterRejection.InitiatorActorId));

        // An approval follows up once its execution ended, for the person who asked as well.
        var approved = await ProposeAsync(host, projects[1], Requester);
        Assert.True((await DecideAsync(host, Decider, approved.Approval.Id, approve: true)).Success);
        await ExecuteProposalAsync(host, approved.Approval.Id);
        var afterExecution = await LatestRunAsync(host, approved.Thread.ConversationId);
        Assert.Equal(
            (NhAssistantTriggerIds.ProposalExecuted, (string?)Requester),
            (afterExecution.TriggerId, afterExecution.InitiatorActorId));

        // An approval refused because the project changed closes the proposal as stale and follows up
        // for the person who asked, not for the approver whose decision found the change.
        var changed = await ProposeAsync(host, projects[2], Requester);
        subjects.Version = "8";
        Assert.False((await DecideAsync(host, Decider, changed.Approval.Id, approve: true)).Success);
        subjects.Version = "7";
        var afterStale = await LatestRunAsync(host, changed.Thread.ConversationId);
        Assert.Equal(
            (NhAssistantTriggerIds.ProposalExecuted, (string?)Requester),
            (afterStale.TriggerId, afterStale.InitiatorActorId));

        // Trigger work is nobody's request: its follow-up has no initiator either.
        var triggered = await ProposeAsync(host, projects[3], initiator: null);
        Assert.True((await DecideAsync(host, Decider, triggered.Approval.Id, approve: false)).Success);
        var afterTrigger = await LatestRunAsync(host, triggered.Thread.ConversationId);
        Assert.Equal(
            (NhAssistantTriggerIds.ProposalDecided, (string?)null),
            (afterTrigger.TriggerId, afterTrigger.InitiatorActorId));

        // The decider stays in the approval and its audit.
        Assert.Equal(Decider, (await FindApprovalAsync(host, rejected.Approval.Id)).DecidedByActorId);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRejected
            && evt.ApprovalId == rejected.Approval.Id
            && evt.ActorId == Decider);

        // Four eyes keeps the requester out of what the follow-up proposes, and the decider may decide it.
        var followUpProposal = await SeedFollowUpProposalAsync(host, afterRejection);
        Assert.Equal(Requester, followUpProposal.InitiatorActorId);
        await using var scope = host.Services.CreateAsyncScope();
        var approvers = scope.ServiceProvider.GetRequiredService<NhAssistantApproverCheck>();
        var requesterCheck = await approvers.CheckAsync(followUpProposal, rejected.Thread, Requester, Principal(Requester), CancellationToken.None);
        var deciderCheck = await approvers.CheckAsync(followUpProposal, rejected.Thread, Decider, Principal(Decider), CancellationToken.None);
        Assert.Contains(requesterCheck.GetResultItems(), item => item.Name == NhAssistantErrorCodes.FourEyesRequired);
        Assert.True(deciderCheck.Success);
    }

    [Fact]
    public void A_proposal_placed_at_another_object_follows_up_for_nobody()
    {
        var own = new AssistantApproval { InitiatorActorId = Requester };
        var placed = new AssistantApproval { InitiatorActorId = Requester, PlacedFromConversationId = Guid.NewGuid() };
        var triggered = new AssistantApproval { InitiatorActorId = null };

        Assert.Equal(Requester, NhAssistantAgentTriggers.FollowUpInitiatorOf(own));
        Assert.Null(NhAssistantAgentTriggers.FollowUpInitiatorOf(placed));
        Assert.Null(NhAssistantAgentTriggers.FollowUpInitiatorOf(triggered));
    }

    /// <summary>
    /// A host whose model proposes once to activate each project in <paramref name="projects"/>, in
    /// that order, under a deferred four-eyes policy that asks for a follow-up run.
    /// </summary>
    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, IReadOnlyList<Guid> projects)
    {
        var model = new NhAiScriptedChatClient();
        foreach (var project in projects)
        {
            model
                .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = project, status = "Active" } }, "call-status-" + project.ToString("N"))
                .RespondWithText("I proposed to activate the project.");
        }

        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                var subjects = new TestSubjects();
                subjects.Roles[Requester] = NhAiSubjectRole.Approver;
                subjects.Roles[Decider] = NhAiSubjectRole.Approver;
                services.AddSingleton(subjects);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("follow-up-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .FourEyes()
                    .RequireUnchangedSubject()
                    .StartFollowUpRun()
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .DelegateFromInitiator()
                    .UseApprovalPolicy("follow-up-review")));
    }

    /// <summary>
    /// A run of <paramref name="initiator"/>, or trigger work without one, whose model proposes one
    /// status change that waits deferred in the project's thread.
    /// </summary>
    private static async Task<(AssistantApproval Approval, AssistantThread Thread)> ProposeAsync(
        AssistantTestHost host,
        Guid projectId,
        string? initiator)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId.ToString()), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var triggerId = initiator is null ? "project-created" : NhAssistantTriggerIds.Manual;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = triggerId,
            InitiatorActorId = initiator,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        var runs = services.GetRequiredService<NhAssistantRunStore>();
        await runs.AddRunAsync(run, CancellationToken.None);
        var agentTurn = new NhAssistantAgentTurnContext(
            thread,
            info,
            services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId],
            run.Id,
            run.TriggerId,
            initiator,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        var result = await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), initiator, run.TriggerId, "Activate the project.")]),
            CancellationToken.None);
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        // The run ended, so a follow-up starts a run of its own instead of joining this one.
        await runs.TrySetRunStatusAsync(run.Id, [NhAssistantRunStatuses.Queued], NhAssistantRunStatuses.Completed, CancellationToken.None);

        var approval = Assert.Single(await runs.GetRunApprovalsAsync(run.Id, CancellationToken.None));
        Assert.Equal((NhAssistantApprovalStatuses.Pending, initiator), (approval.Status, approval.InitiatorActorId));
        return (approval, thread);
    }

    /// <summary>
    /// Records a pending proposal of the follow-up run as its turn would: with the run's initiator and
    /// the snapshot of the policy that applied.
    /// </summary>
    private static async Task<AssistantApproval> SeedFollowUpProposalAsync(AssistantTestHost host, AssistantRun followUp)
    {
        var template = await LatestApprovalAsync(host, followUp.ConversationId);
        var createdAt = DateTimeOffset.UtcNow;
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = followUp.ConversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = template.ProposalJson,
            ToolId = template.ToolId,
            Summary = "Activate the project.",
            ArgumentsPreview = "{}",
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(1),
            RunId = followUp.Id,
            Mode = NhAssistantApprovalModes.Deferred,
            PolicyId = template.PolicyId,
            PolicyJson = template.PolicyJson,
            InitiatorActorId = followUp.InitiatorActorId,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval;
    }

    private static async Task<TaskResult<AssistantApproval>> DecideAsync(AssistantTestHost host, string actorId, Guid approvalId, bool approve)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approvalId,
            actorId,
            Principal(actorId),
            approve,
            expectedProposalHash: null,
            reason: null,
            CancellationToken.None);
    }

    private static async Task ExecuteProposalAsync(AssistantTestHost host, Guid approvalId)
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

    private static async Task<AssistantRun> LatestRunAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs.Where(run => run.ConversationId == conversationId).OrderByDescending(run => run.CreatedAt).FirstAsync();
    }

    private static async Task<AssistantApproval> LatestApprovalAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Approvals.Where(approval => approval.ConversationId == conversationId).OrderByDescending(approval => approval.CreatedAt).FirstAsync();
    }

    private static async Task<AssistantApproval> FindApprovalAsync(AssistantTestHost host, Guid approvalId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
    }

    private static ClaimsPrincipal Principal(string actorId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"));
    }
}
