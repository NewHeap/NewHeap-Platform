using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
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
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Agent runs on a subject thread without an HTTP request: grants, deferred and inline approval,
/// execution of an approved proposal, approver policies, delegation and the inbox, on both
/// relational providers.
/// </summary>
public sealed class AssistantAgentRunTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string AgentActorId = "assistant-agent:project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Member = "member-1";
    private const string Approver = "approver-1";
    private const string SecondApprover = "approver-2";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_trigger_run_records_a_deferred_proposal_that_an_approver_executes_later(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.");
        await using var host = await CreateHostAsync(provider, model, NhAiApprovalMode.Deferred);
        var run = await StartRunAsync(host, projectId);

        var result = await RunTurnAsync(host, run, "Project PRJ-1 was created.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        Assert.Equal(1, result.ProposalCount);
        Assert.Empty(host.Tools.StatusChanges);
        var recorded = Assert.Single(model.FunctionResults);
        Assert.Contains(NhAssistantToolCallInterceptor.ProposalRecordedCode, JsonSerializer.Serialize(recorded.Result));

        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, approval.Status);
        Assert.Equal(NhAssistantApprovalModes.Deferred, approval.Mode);
        Assert.Equal("project-review", approval.PolicyId);
        Assert.Equal(NhAiGrantSource.Agent, approval.GrantSource);
        Assert.Equal("7", approval.SubjectVersion);
        var proposal = Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
        Assert.Equal(AgentActorId, proposal.ActorId);
        Assert.Equal(AgentActorId, proposal.AccountableOwnerId);
        Assert.Null(proposal.PromptHash);

        // The member snapshot lists the proposal for the approver only.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<INhAiSubjectAccess>();
            Assert.True((await access.RefreshAsync(Subject(projectId))).Success);
        }
        var (approverInbox, _) = await RunStore(host).ListInboxAsync(Approver, new NhAssistantInboxFilter(null, null, null, null), 1, 20, CancellationToken.None);
        var (memberInbox, _) = await RunStore(host).ListInboxAsync(Member, new NhAssistantInboxFilter(null, null, null, null), 1, 20, CancellationToken.None);
        Assert.Equal(approval.Id, Assert.Single(approverInbox).Approval.Id);
        Assert.Empty(memberInbox);

        Assert.False((await CheckApproverAsync(host, approval, run.Thread, Member)).Success);
        Assert.True((await CheckApproverAsync(host, approval, run.Thread, Approver)).Success);
        await ApproveAsync(host, approval, Approver);

        var execution = await ExecuteAsync(host, run, approval.Id);

        Assert.True(execution.Succeeded);
        Assert.Equal(new ProjectStatusInput(projectId, "Active"), Assert.Single(host.Tools.StatusChanges));
        var context = Assert.Single(host.Tools.Contexts);
        Assert.Equal(NhAiActorKind.Agent, context.ActorKind);
        Assert.Equal(AgentActorId, context.AccountableOwnerId);
        Assert.Equal(NhAiGrantSource.Agent, context.GrantSource);
        Assert.Equal(approval.ProposalId.ToString(), context.ProposalId);
        Assert.Equal(projectId.ToString(), Assert.Single(context.ExecutionScopes, scope => scope.Type == "project").Id);
        // The approved proposal executes for the run that proposed it.
        Assert.Equal(run.Run.Id, context.AgentRunId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_tool_in_an_agent_run_reads_the_runs_id_while_run_id_stays_the_turn(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-search")
            .RespondWithText("The roadmap project exists.");
        await using var host = await CreateHostAsync(provider, model, NhAiApprovalMode.Deferred);
        var run = await StartRunAsync(host, projectId);

        var result = await RunTurnAsync(host, run, "Project PRJ-1 was created.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        var context = Assert.Single(host.Tools.Contexts);
        var scope = Assert.Single(host.Tools.Scopes);
        Assert.NotNull(scope);
        // The tool reads the agent run on its invocation context and on the ambient scope.
        Assert.Equal(run.Run.Id, context.AgentRunId);
        Assert.Equal(run.Run.Id, scope.AgentRunId);
        // RunId stays the turn and CorrelationId the run in N format, as proposals and usage use them.
        Assert.Equal(scope.RunId, context.RunId);
        Assert.NotEqual(run.Run.Id.ToString(), context.RunId);
        Assert.Equal(run.Run.Id.ToString("N"), context.CorrelationId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_records_the_parent_its_subject_belongs_to_when_it_is_made(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.");
        await using var host = await CreateHostAsync(provider, model, NhAiApprovalMode.Deferred);
        var subjects = host.Services.GetRequiredService<TestSubjects>();
        subjects.Parent = new NhAiSubjectParent(new NhAiSubjectRef("portfolio", "north"), "Portfolio " + new string('n', 250));
        var run = await StartRunAsync(host, projectId);

        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        // The parent changes later; the proposal keeps what it recorded, so the inbox asks no resolver.
        subjects.Parent = null;

        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal(("portfolio", "north"), (approval.ParentSubjectType, approval.ParentSubjectId));
        Assert.Equal(NhAiSubjectParent.MaxDisplayNameLength, approval.ParentDisplayName!.Length);
        Assert.StartsWith("Portfolio nnn", approval.ParentDisplayName);
        Assert.Equal(
            new NhAssistantProposalParentDto("portfolio", "north", approval.ParentDisplayName),
            NhAssistantAgentRunDtoMapper.ToDto(approval, run.Thread).Parent);

        // A subject never belongs to itself: such a description is refused.
        subjects.Parent = new NhAiSubjectParent(Subject(projectId), "Itself");
        await using var scope = host.Services.CreateAsyncScope();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scope.ServiceProvider.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(Subject(projectId), CancellationToken.None));
        Assert.Contains("invalid parent", refused.Message);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_inline_approval_suspends_a_headless_turn_that_resumes_after_the_decision(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Closed" } }, "call-close")
            .RespondWithText("The project is closed.");
        await using var host = await CreateHostAsync(provider, model, NhAiApprovalMode.Inline);
        var run = await StartRunAsync(host, projectId);

        var waiting = await RunTurnAsync(host, run, "Close project PRJ-1.");

        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, waiting.Outcome);
        Assert.NotNull(waiting.PendingApprovalId);
        Assert.Empty(host.Tools.StatusChanges);
        var conversation = await host.ReloadAsync(run.Thread.ConversationId, AgentActorId);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, conversation.Status);

        var approval = (await host.Services.GetRequiredService<INhAssistantStore>()
            .FindApprovalAsync(waiting.PendingApprovalId!.Value, CancellationToken.None))!;
        Assert.Equal(NhAssistantApprovalModes.Inline, approval.Mode);
        await ApproveAsync(host, approval, Approver);

        NhAssistantAgentTurnResult resumed;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
            resumed = await runner.ResumeAgentTurnAsync(
                new NhAssistantAgentResumeRequest(run.AgentTurn, approval.Id),
                CancellationToken.None);
        }

        Assert.Equal(NhAssistantTurnStatuses.Completed, resumed.Outcome);
        Assert.Equal(waiting.TurnId, resumed.TurnId);
        Assert.Equal(new ProjectStatusInput(projectId, "Closed"), Assert.Single(host.Tools.StatusChanges));
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(run.Thread.ConversationId, AgentActorId)).Status);
    }

    /// <summary>
    /// An agent's inline proposal lives by the application's clock: decades behind the system
    /// clock, where a check on the system clock would have expired it at once, it stays open to its
    /// approvers and to the maintenance job for its lifetime of three days, is approved at the end of
    /// it and executes when the waiting run continues.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_inline_proposal_lives_by_the_applications_clock_and_executes_within_its_lifetime(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2001, 2, 3, 4, 5, 0, TimeSpan.Zero));
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Closed" } }, "call-close")
            .RespondWithText("The project is closed.");
        await using var host = await CreateHostAsync(provider, model, NhAiApprovalMode.Inline, clock: clock);
        var run = await StartRunAsync(host, projectId);

        var waiting = await RunTurnAsync(host, run, "Close project PRJ-1.");

        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, waiting.Outcome);
        var approval = await FindApprovalAsync(host, waiting.PendingApprovalId!.Value);
        Assert.Equal(clock.GetUtcNow().AddDays(3), approval.ExpiresAt);
        Assert.Equal(clock.GetUtcNow(), Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson).GeneratedAt);

        // A minute before the end of its lifetime it is still open, and the maintenance job leaves it.
        clock.Advance(TimeSpan.FromDays(3) - TimeSpan.FromMinutes(1));
        await RunMaintenanceAsync(host);
        Assert.Equal(approval.Id, Assert.Single(await RunStore(host).GetOpenThreadApprovalsAsync(run.Thread.ConversationId, 10, CancellationToken.None)).Id);
        Assert.True((await DecideAsync(host, approval.Id, approve: true)).Success);
        var approved = await FindApprovalAsync(host, approval.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Approved, approved.Status);
        Assert.Equal(clock.GetUtcNow(), approved.DecidedAt);

        // The run continues and executes the proposal: the invoker validates the approval at the
        // application's clock as well.
        var resumed = await ResumeAsync(host, run, approval.Id);

        Assert.Equal(NhAssistantTurnStatuses.Completed, resumed.Outcome);
        Assert.Equal(new ProjectStatusInput(projectId, "Closed"), Assert.Single(host.Tools.StatusChanges));
    }

    /// <summary>
    /// An agent's inline proposal expires when the application's clock passes its lifetime, years
    /// before the system clock would: it is no longer open, an approver's decision comes too late,
    /// and the waiting run continues without executing it.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_inline_proposal_expires_on_the_applications_clock_and_its_run_continues_without_it(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Closed" } }, "call-close");
        await using var host = await CreateHostAsync(provider, model, NhAiApprovalMode.Inline, clock: clock);
        var run = await StartRunAsync(host, projectId);
        var waiting = await RunTurnAsync(host, run, "Close project PRJ-1.");
        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, waiting.Outcome);
        var approvalId = waiting.PendingApprovalId!.Value;

        clock.Advance(TimeSpan.FromDays(3));

        Assert.Empty(await RunStore(host).GetOpenThreadApprovalsAsync(run.Thread.ConversationId, 10, CancellationToken.None));
        var late = await DecideAsync(host, approvalId, approve: true);
        Assert.False(late.Success);
        Assert.Contains(late.GetResultItems(), item => item.Name == NhAssistantErrorCodes.ApprovalExpired);
        Assert.Equal(NhAssistantApprovalStatuses.Expired, (await FindApprovalAsync(host, approvalId)).Status);

        var resumed = await ResumeAsync(host, run, approvalId);

        Assert.Equal(NhAssistantErrorCodes.ApprovalExpired, resumed.ErrorCode);
        Assert.Empty(host.Tools.StatusChanges);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(run.Thread.ConversationId, AgentActorId)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Delegation_uses_the_initiators_rights_and_four_eyes_keeps_the_initiator_out(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("Proposed.");
        await using var host = await CreateHostAsync(
            provider,
            model,
            NhAiApprovalMode.Deferred,
            fourEyes: true,
            grants: false,
            delegation: true);
        var run = await StartRunAsync(host, projectId, initiator: Approver);

        var result = await RunTurnAsync(host, run, "Please activate the project.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal(NhAiGrantSource.Delegated, approval.GrantSource);
        Assert.Equal(Approver, approval.InitiatorActorId);
        var denied = await CheckApproverAsync(host, approval, run.Thread, Approver);
        Assert.False(denied.Success);
        Assert.Contains(denied.GetResultItems(), item => item.Name == NhAssistantErrorCodes.FourEyesRequired);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Thread_access_follows_the_subject_resolver(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), NhAiApprovalMode.Deferred);
        var run = await StartRunAsync(host, projectId);
        var subjects = host.Services.GetRequiredService<TestSubjects>();

        await using var scope = host.Services.CreateAsyncScope();
        var threads = scope.ServiceProvider.GetRequiredService<NhAssistantThreadService>();
        var member = await threads.ResolveAccessAsync(run.Thread.ConversationId, Member, "Member", CancellationToken.None);
        var stranger = await threads.ResolveAccessAsync(run.Thread.ConversationId, "stranger-1", null, CancellationToken.None);
        Assert.Equal(NhAiSubjectRole.Participant, member!.Role);
        Assert.Null(stranger);
        Assert.Contains(
            await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().GetParticipantsAsync(run.Thread.ConversationId, CancellationToken.None),
            participant => participant.ActorId == Member && participant.JoinedVia == NhAssistantParticipantSources.Subject);

        // Taking the member off the subject removes their access at once.
        subjects.Roles.TryRemove(Member, out _);
        Assert.Null(await threads.ResolveAccessAsync(run.Thread.ConversationId, Member, null, CancellationToken.None));
        Assert.DoesNotContain(
            await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().GetParticipantsAsync(run.Thread.ConversationId, CancellationToken.None),
            participant => participant.ActorId == Member);

        // A second lookup of the same subject reuses the thread.
        var again = await scope.ServiceProvider.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(
            run.Agent,
            run.AgentTurn.Subject,
            CancellationToken.None);
        Assert.Equal(run.Thread.ConversationId, again.Data!.ConversationId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_thread_conversation_tells_each_reader_who_decides_its_open_proposals(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.");
        await using var host = await CreateHostAsync(provider, model, NhAiApprovalMode.Deferred, fourEyes: true);
        host.Services.GetRequiredService<TestSubjects>().Roles[SecondApprover] = NhAiSubjectRole.Approver;
        var run = await StartRunAsync(host, projectId, initiator: Approver);

        await RunTurnAsync(host, run, "Project PRJ-1 was created.");

        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        var approver = await ReadThreadConversationAsync(host, run, SecondApprover);
        Assert.Equal(NhAssistantConversationKinds.Thread, approver.Kind);
        Assert.Equal("approver", approver.Thread!.Role);
        Assert.Equal("project", approver.Thread.SubjectType);
        Assert.Equal(projectId.ToString(), approver.Thread.SubjectId);
        var decision = Assert.Single(approver.Thread.Decisions);
        Assert.Equal(approval.Id, decision.ApprovalId);
        Assert.Equal(run.Run.Id, decision.RunId);
        Assert.True(decision.CanDecide);
        Assert.Null(decision.ReasonCode);

        // Four-eyes keeps the person who started the work out, and a participant is no approver.
        var initiator = await ReadThreadConversationAsync(host, run, Approver);
        var own = Assert.Single(initiator.Thread!.Decisions);
        Assert.False(own.CanDecide);
        Assert.Equal(NhAssistantErrorCodes.FourEyesRequired, own.ReasonCode);
        var member = await ReadThreadConversationAsync(host, run, Member);
        Assert.Equal("participant", member.Thread!.Role);
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyDenied, Assert.Single(member.Thread.Decisions).ReasonCode);

        // The message of the trigger reads as a trigger, not as a person.
        var trigger = Assert.Single(approver.Messages, message => message.Role == NhAssistantMessageRoles.User);
        Assert.Equal(NhAssistantMessageAuthorKinds.Trigger, trigger.AuthorKind);
        Assert.Equal("trigger:project-created", trigger.AuthorActorId);
        Assert.All(
            approver.Messages.Where(message => message.Role != NhAssistantMessageRoles.User),
            message => Assert.Null(message.AuthorKind));

        // A decided proposal is no longer open.
        await ApproveAsync(host, approval, SecondApprover);
        Assert.Empty((await ReadThreadConversationAsync(host, run, SecondApprover)).Thread!.Decisions);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        NhAiApprovalMode mode,
        bool fourEyes = false,
        bool grants = true,
        bool delegation = false,
        TimeProvider? clock = null)
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
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddScoped<NhAssistantApproverCheck>();
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(mode)
                    .FourEyes(fourEyes)
                    .BindTo(NhAiApprovalBinding.ToolContract)
                    .RequireUnchangedSubject())
                .ConfigureAgent(AgentId, agent =>
                {
                    if (grants)
                    {
                        agent.Grant("projects.*");
                    }
                    agent
                        .DelegateFromInitiator(delegation)
                        .UseApprovalPolicy("project-review");
                }));
    }

    private sealed record StartedRun(
        AssistantThread Thread,
        AssistantRun Run,
        NhAssistantAgent Agent,
        NhAssistantAgentTurnContext AgentTurn);

    private static async Task<StartedRun> StartRunAsync(AssistantTestHost host, Guid projectId, string? initiator = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var subjects = services.GetRequiredService<NhAssistantSubjectAccessService>();
        var info = (await subjects.DescribeAsync(Subject(projectId), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "project-created",
            InitiatorActorId = initiator,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var agentTurn = new NhAssistantAgentTurnContext(
            thread,
            info,
            options,
            run.Id,
            run.TriggerId,
            initiator,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        return new StartedRun(thread, run, agent, agentTurn);
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, StartedRun run, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
        return await runner.RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                run.AgentTurn,
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message)]),
            CancellationToken.None);
    }

    private static async Task<NhAssistantProposalExecutionResult> ExecuteAsync(AssistantTestHost host, StartedRun run, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
        return await runner.ExecuteProposalAsync(
            new NhAssistantProposalExecutionRequest(run.AgentTurn, approvalId),
            CancellationToken.None);
    }

    private static async Task<Common.Models.TaskResult> CheckApproverAsync(
        AssistantTestHost host,
        AssistantApproval approval,
        AssistantThread thread,
        string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<NhAssistantApproverCheck>();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"));
        return await check.CheckAsync(approval, thread, actorId, user, CancellationToken.None);
    }

    /// <summary>
    /// The conversation as <c>GET conversations/{id}</c> returns it to <paramref name="actorId"/>.
    /// </summary>
    private static async Task<NhAssistantConversationDto> ReadThreadConversationAsync(
        AssistantTestHost host,
        StartedRun run,
        string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var conversationId = run.Thread.ConversationId;
        var threadAccess = await services.GetRequiredService<NhAssistantThreadService>()
            .ResolveAccessAsync(conversationId, actorId, actorId, CancellationToken.None);
        var access = await services.GetRequiredService<INhAssistantStore>()
            .FindAccessAsync(conversationId, actorId, null, CancellationToken.None);
        var view = await services.GetRequiredService<NhAssistantConversationReader>()
            .CreateViewAsync(access!, CancellationToken.None);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"));
        var thread = await NhAssistantThreadStanding.CreateAsync(services, user, threadAccess!, actorId, CancellationToken.None);
        return NhAssistantDtoMapper.ToDto(view, actorId, null, thread);
    }

    private static async Task ApproveAsync(AssistantTestHost host, AssistantApproval approval, string actorId)
    {
        var approved = await RunStore(host).TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Approved,
            new NhAssistantApprovalTransition
            {
                DecidedByActorId = actorId,
                DecidedAt = DateTimeOffset.UtcNow,
                ApprovalExpiresAt = approval.ExpiresAt
            },
            CancellationToken.None);
        Assert.True(approved);
    }

    private static async Task<AssistantApproval> FindApprovalAsync(AssistantTestHost host, Guid approvalId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
    }

    /// <summary>
    /// Decides the proposal as the subject's approver does through <c>POST approvals/{id}/decisions</c>.
    /// </summary>
    private static async Task<Common.Models.TaskResult<AssistantApproval>> DecideAsync(AssistantTestHost host, Guid approvalId, bool approve)
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

    /// <summary>
    /// Continues the run that waits for <paramref name="approvalId"/>, as its background operation
    /// does once the wait ends.
    /// </summary>
    private static async Task<NhAssistantAgentTurnResult> ResumeAsync(AssistantTestHost host, StartedRun run, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().ResumeAgentTurnAsync(
            new NhAssistantAgentResumeRequest(run.AgentTurn, approvalId),
            CancellationToken.None);
    }

    private static async Task RunMaintenanceAsync(AssistantTestHost host)
    {
        await host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<NhAssistantRunMaintenance>()
            .Single()
            .RunOnceAsync(CancellationToken.None);
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

/// <summary>
/// The people on every test project: one participant and one approver.
/// </summary>
public sealed class TestSubjects
{
    public ConcurrentDictionary<string, NhAiSubjectRole> Roles { get; } = new(new Dictionary<string, NhAiSubjectRole>
    {
        ["member-1"] = NhAiSubjectRole.Participant,
        ["approver-1"] = NhAiSubjectRole.Approver
    });

    public string Version { get; set; } = "7";

    /// <summary>
    /// The subject every test project belongs to, when a test names one.
    /// </summary>
    public NhAiSubjectParent? Parent { get; set; }
}

public sealed class TestSubjectResolver(TestSubjects subjects) : INhAiSubjectAccessResolver
{
    public string SubjectType => "project";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, null))
        {
            Version = subjects.Version,
            DisplayName = "Project " + subject.Id[..8],
            Parent = subjects.Parent
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(subjects.Roles.TryGetValue(actorId, out var role)
            ? new NhAiSubjectAccess(role)
            : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (actorId, role) in subjects.Roles)
        {
            await Task.Yield();
            yield return new NhAiSubjectMember(actorId, role) { DisplayName = actorId };
        }
    }
}

public sealed class TestPrincipalFactory : INhAiPrincipalFactory
{
    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId)],
            "test")));
    }
}
