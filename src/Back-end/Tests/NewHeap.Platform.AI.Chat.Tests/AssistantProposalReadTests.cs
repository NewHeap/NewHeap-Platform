using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Reading agent proposals by id: one proposal in any status, or the visible ones among many ids,
/// with the caller's live decision right and without joining the thread, on both relational
/// providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantProposalReadTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Approver = "approver-1";
    private const string Initiator = "approver-2";
    private const string Member = "member-1";
    private const string Reader = "reader-1";

    private static readonly string[] ClosedStatuses =
    [
        NhAssistantApprovalStatuses.Approved,
        NhAssistantApprovalStatuses.Executing,
        NhAssistantApprovalStatuses.Executed,
        NhAssistantApprovalStatuses.ExecutionFailed,
        NhAssistantApprovalStatuses.ExecutionUnknown,
        NhAssistantApprovalStatuses.Resolved,
        NhAssistantApprovalStatuses.Rejected,
        NhAssistantApprovalStatuses.Expired,
        NhAssistantApprovalStatuses.Withdrawn,
        NhAssistantApprovalStatuses.Stale
    ];

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_reads_by_id_in_every_status_with_the_callers_decision_right(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient();
        foreach (var index in Enumerable.Range(0, ClosedStatuses.Length + 1))
        {
            model.RespondWithFunctionCall(
                ChangeStatusFunction,
                new { input = new { projectId = Guid.NewGuid(), status = "Active" } },
                "call-" + index);
        }
        model.RespondWithText("I proposed the changes.");
        await using var host = await CreateHostAsync(provider, model);
        var thread = await RunAsync(host, projectId);
        var approvals = (await RunStore(host).GetThreadApprovalsAsync(thread.ConversationId, 50, CancellationToken.None))
            .OrderBy(approval => approval.CreatedAt)
            .ToArray();
        Assert.Equal(ClosedStatuses.Length + 1, approvals.Length);
        var pending = approvals[0];
        for (var index = 0; index < ClosedStatuses.Length; index++)
        {
            Assert.True(await RunStore(host).TryTransitionApprovalAsync(
                approvals[index + 1].Id,
                [NhAssistantApprovalStatuses.Pending],
                ClosedStatuses[index],
                Transition(ClosedStatuses[index], pending.Id),
                CancellationToken.None));
        }

        // Every status reads by id, with the fields the thread's list returns.
        foreach (var approval in approvals)
        {
            var read = Assert.Single(await ReadAsync(host, [approval.Id], Approver));
            Assert.Equal(approval.Id, read.Proposal.ApprovalId);
            Assert.Equal(thread.SubjectId, read.Proposal.SubjectId);
        }
        var statuses = (await ReadAsync(host, approvals.Select(approval => approval.Id).ToArray(), Approver))
            .Select(read => read.Proposal.Status)
            .ToArray();
        Assert.Equal(ClosedStatuses.Prepend(NhAssistantApprovalStatuses.Pending), statuses);
        var stale = Assert.Single(await ReadAsync(host, [approvals[10].Id], Approver)).Proposal;
        Assert.Equal(NhAssistantStaleKinds.AlreadyDone, stale.StaleKind);
        Assert.Equal("Completed by hand.", stale.StaleExplanation);
        Assert.Equal(Member, stale.StaleByActorId);
        Assert.Equal(Member, stale.StaleByDisplayName);
        Assert.Equal(pending.Id, Assert.Single(await ReadAsync(host, [approvals[9].Id], Approver)).Proposal.SupersededByApprovalId);
        var rejected = Assert.Single(await ReadAsync(host, [approvals[7].Id], Approver));
        Assert.True(rejected.Proposal.CanGiveFeedback);
        Assert.False(Assert.Single(await ReadAsync(host, [approvals[7].Id], Member)).Proposal.CanGiveFeedback);

        // Only a pending proposal can be decided, and the right is the live one of each caller.
        var forApprover = Assert.Single(await ReadAsync(host, [pending.Id], Approver));
        Assert.True(forApprover.CanDecide);
        Assert.Null(forApprover.ReasonCode);
        var forInitiator = Assert.Single(await ReadAsync(host, [pending.Id], Initiator));
        Assert.False(forInitiator.CanDecide);
        Assert.Equal(NhAssistantErrorCodes.FourEyesRequired, forInitiator.ReasonCode);
        var forReader = Assert.Single(await ReadAsync(host, [pending.Id], Reader));
        Assert.False(forReader.CanDecide);
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyDenied, forReader.ReasonCode);
        var decided = Assert.Single(await ReadAsync(host, [approvals[1].Id], Approver));
        Assert.False(decided.CanDecide);
        Assert.Null(decided.ReasonCode);

        // Nobody else sees it: no role, another tenant, a hidden agent or an unknown id.
        Assert.Empty(await ReadAsync(host, [pending.Id], "stranger"));
        Assert.Empty(await ReadAsync(host, [pending.Id], Approver, tenantId: "other-tenant"));
        Assert.Empty(await ReadAsync(host, [pending.Id], Approver, hidden: true));
        Assert.Empty(await ReadAsync(host, [Guid.NewGuid()], Approver));

        // Reading never joins the thread's conversation.
        var participants = await host.Services.GetRequiredService<INhAssistantStore>()
            .GetParticipantsAsync(thread.ConversationId, CancellationToken.None);
        Assert.DoesNotContain(participants, participant => participant.ActorId is Approver or Initiator or Reader);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_lookup_returns_the_visible_proposals_in_request_order(AssistantTestProvider provider)
    {
        var shared = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "Active" } }, "call-a")
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "OnHold" } }, "call-b")
            .RespondWithText("I proposed two changes.");
        await using var host = await CreateHostAsync(provider, model);
        var thread = await RunAsync(host, shared);
        var ids = (await RunStore(host).GetThreadApprovalsAsync(thread.ConversationId, 10, CancellationToken.None))
            .Select(approval => approval.Id)
            .ToArray();
        Assert.Equal(2, ids.Length);

        var unknown = Guid.NewGuid();
        var read = await ReadAsync(host, [ids[1], unknown, ids[0], ids[1]], Member);
        Assert.Equal([ids[1], ids[0]], read.Select(item => item.Proposal.ApprovalId));
        Assert.All(read, item => Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyDenied, item.ReasonCode));

        // Someone invited into the thread's conversation reads its proposals without a role.
        await host.Services.GetRequiredService<INhAssistantStore>().TryAddParticipantAsync(
            new AssistantConversationParticipant
            {
                ConversationId = thread.ConversationId,
                ActorId = "guest-1",
                DisplayName = "Guest",
                JoinedVia = NhAssistantParticipantSources.Invitation,
                JoinedAt = DateTimeOffset.UtcNow
            },
            int.MaxValue,
            CancellationToken.None);
        Assert.Equal(2, (await ReadAsync(host, ids, "guest-1")).Count);
        Assert.Empty(await ReadAsync(host, ids, "stranger"));
    }

    [Fact]
    public async Task The_endpoints_answer_404_for_invisible_proposals_and_bound_the_lookup()
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient());
        using var client = app.CreateClient();

        var unknown = await client.GetAsync($"/api/assistant/approvals/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains(NhAssistantErrorCodes.ApprovalNotFound, await unknown.Content.ReadAsStringAsync());

        var none = await client.PostAsJsonAsync("/api/assistant/approvals/lookup", new { approvalIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        var tooMany = await client.PostAsJsonAsync(
            "/api/assistant/approvals/lookup",
            new { approvalIds = Enumerable.Range(0, NhAssistantProposalLookupRequest.MaxApprovalIds + 1).Select(_ => Guid.NewGuid()).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains(NhAssistantErrorCodes.MessageInvalid, await tooMany.Content.ReadAsStringAsync());

        // Duplicates count once, and ids nobody may see leave an empty answer.
        var id = Guid.NewGuid();
        var duplicates = await client.PostAsJsonAsync(
            "/api/assistant/approvals/lookup",
            new { approvalIds = Enumerable.Repeat(id, NhAssistantProposalLookupRequest.MaxApprovalIds + 1).ToArray() });
        Assert.Equal(HttpStatusCode.OK, duplicates.StatusCode);
        Assert.Equal("{\"items\":[]}", await duplicates.Content.ReadAsStringAsync());
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            limits: limits => limits.MaxToolCallsPerTurn = 16,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .FourEyes()
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")));
        var roles = host.Services.GetRequiredService<TestSubjects>().Roles;
        roles[Initiator] = NhAiSubjectRole.Approver;
        roles[Reader] = NhAiSubjectRole.Reader;
        return host;
    }

    /// <summary>
    /// Runs the agent once on the project with <see cref="Initiator"/> as the person who started it,
    /// and refreshes the member snapshot for the display names.
    /// </summary>
    private static async Task<AssistantThread> RunAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var subject = new NhAiSubjectRef("project", projectId.ToString());
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "manual",
            InitiatorActorId = Initiator,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                new NhAssistantAgentTurnContext(thread, info, options, run.Id, run.TriggerId, Initiator, thread.AccountableOwnerId, IsRequestTurn: false),
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), Initiator, run.TriggerId, "Review project PRJ-1.")]),
            CancellationToken.None);
        Assert.True((await services.GetRequiredService<INhAiSubjectAccess>().RefreshAsync(subject)).Success);
        return thread;
    }

    private static async Task<IReadOnlyList<NhAssistantSubjectProposalDto>> ReadAsync(
        AssistantTestHost host,
        IReadOnlyList<Guid> approvalIds,
        string actorId,
        string? tenantId = null,
        bool hidden = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalReads>().ReadAsync(
            approvalIds,
            actorId,
            tenantId,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test")),
            _ => Task.FromResult(hidden),
            CancellationToken.None);
    }

    private static NhAssistantApprovalTransition Transition(string status, Guid successor)
    {
        var now = DateTimeOffset.UtcNow;
        return status switch
        {
            NhAssistantApprovalStatuses.Rejected => new NhAssistantApprovalTransition { DecidedByActorId = Approver, DecidedAt = now },
            NhAssistantApprovalStatuses.Withdrawn => new NhAssistantApprovalTransition
            {
                DecidedAt = now,
                ExecutionResultCode = NhAssistantErrorCodes.ProposalSuperseded,
                SupersededByApprovalId = successor
            },
            NhAssistantApprovalStatuses.Stale => new NhAssistantApprovalTransition
            {
                DecidedAt = now,
                ExecutionResultCode = "task-completed",
                StaleKind = NhAssistantStaleKinds.AlreadyDone,
                StaleExplanationsJson = "{\"en-US\":\"Completed by hand.\"}",
                StaleByActorId = Member
            },
            _ => NhAssistantApprovalTransition.None
        };
    }

    private static NhAssistantRunStore RunStore(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }
}
