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
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// DA1 and DA2: <see cref="INhAssistantProposalDecisionService"/> decides proposals of agent threads
/// for the application's own channels through the decision of <c>POST approvals/{id}/decisions</c>:
/// every refusal of the endpoint is the service's, the channel and the idempotency key reach the
/// content-free audit, and a repeat by the same person with the same decision and hash returns the
/// earlier decision, on both relational providers.
/// </summary>
public sealed class AssistantProposalDecisionServiceTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string AgentActorId = "assistant-agent:project-assistant";
    private const string ResultsAgent = "results-follow-up";
    private const string ViewPolicy = "app.results.view";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Channel = "messaging-reply";
    private const string Initiator = "approver-1";
    private const string SecondApprover = "approver-2";
    private const string ThirdApprover = "approver-3";
    private const string Member = "member-1";
    private const string Outsider = "outsider-1";
    private const string Viewer = "viewer-1";
    private const string ApproverWithoutView = "admin-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Every_refusal_of_the_decision_endpoint_is_the_refusal_of_the_service_and_of_its_facts(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, projectId);
        var (approval, thread) = await ProposeAsync(host, projectId);

        // The second approver may decide; the facts carry no proposal content.
        var facts = await FactsAsync(host, SecondApprover, approval.Id);
        Assert.True(facts.Success);
        Assert.Equal(
            new NhAssistantProposalDecisionFacts(
                approval.Id,
                NhAssistantApprovalStatuses.Pending,
                approval.ProposalHash,
                approval.ExpiresAt,
                FourEyes: true,
                ApprovalsNeeded: 1,
                CanDecide: true,
                ReasonCode: null)
            {
                // The project's version is the one the proposal recorded.
                SubjectChanged = false
            },
            facts.Data);

        // Four eyes keep the initiator out, a participant and an outsider are no approvers and an
        // agent never decides: the endpoint, the service and the facts say so alike.
        foreach (var (actorId, code) in new[]
        {
            (Initiator, NhAssistantErrorCodes.FourEyesRequired),
            (Member, NhAssistantErrorCodes.ApprovalPolicyDenied),
            (Outsider, NhAssistantErrorCodes.ApprovalPolicyDenied),
            (AgentActorId, NhAssistantErrorCodes.ApprovalDecisionInvalid)
        })
        {
            var refused = (await FactsAsync(host, actorId, approval.Id)).Data!;
            Assert.False(refused.CanDecide);
            Assert.Equal(code, refused.ReasonCode);
            Assert.Equal(code, CodeOf(await DecideAtEndpointAsync(host, actorId, approval.Id, approve: true, approval.ProposalHash)));
            Assert.Equal(code, CodeOf(await DecideAsync(host, actorId, approval.Id, approval.ProposalHash)));
        }

        // A proposal that changed since the person saw it.
        Assert.Equal(
            NhAssistantErrorCodes.ProposalHashMismatch,
            CodeOf(await DecideAtEndpointAsync(host, SecondApprover, approval.Id, approve: true, "sha256:another")));
        Assert.Equal(
            NhAssistantErrorCodes.ProposalHashMismatch,
            CodeOf(await DecideAsync(host, SecondApprover, approval.Id, "sha256:another")));

        // An unknown proposal.
        var unknown = Guid.NewGuid();
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await FactsAsync(host, SecondApprover, unknown)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await DecideAtEndpointAsync(host, SecondApprover, unknown, approve: true, approval.ProposalHash)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await DecideAsync(host, SecondApprover, unknown, approval.ProposalHash)));

        // DA3: a person's own chat proposal and a proposal the asker of a consultation decides are
        // decided in their conversation, through the service as through the endpoint.
        var ownTurn = await SeedProposalAsync(host, thread.ConversationId, NhAssistantApprovalModes.Inline);
        var askerDecides = await SeedProposalAsync(host, thread.ConversationId, NhAssistantApprovalModes.Inline, consultationId: Guid.NewGuid());
        foreach (var (seeded, code) in new[]
        {
            (ownTurn, NhAssistantErrorCodes.ApprovalDecisionInvalid),
            (askerDecides, NhAssistantErrorCodes.ConsultAskerDecides)
        })
        {
            Assert.Equal(code, (await FactsAsync(host, SecondApprover, seeded.Id)).Data!.ReasonCode);
            Assert.Equal(code, CodeOf(await DecideAtEndpointAsync(host, SecondApprover, seeded.Id, approve: true, seeded.ProposalHash)));
            Assert.Equal(code, CodeOf(await DecideAsync(host, SecondApprover, seeded.Id, seeded.ProposalHash)));
        }

        // The channel is a dash-case code the application names in code; the key comes from the
        // channel's delivery and is refused like any other unusable input.
        Assert.Equal(
            NhAssistantErrorCodes.ApprovalDecisionInvalid,
            CodeOf(await DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash, idempotencyKey: "a reply with spaces")));
        Assert.Equal(
            NhAssistantErrorCodes.ApprovalDecisionInvalid,
            CodeOf(await DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash, idempotencyKey: new string('k', NhAssistantDecisionChannels.MaxIdempotencyKeyLength + 1))));
        await Assert.ThrowsAsync<ArgumentException>(() => DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash, channel: "Messaging Reply"));
        await Assert.ThrowsAsync<ArgumentException>(() => DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash, channel: "web-" + new string('x', NhAssistantDecisionChannels.MaxLength)));

        // Nothing was decided or audited.
        Assert.Equal(NhAssistantApprovalStatuses.Pending, (await FindApprovalAsync(host, approval.Id)).Status);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind is NhAssistantAuditEventKind.ApprovalApproved or NhAssistantAuditEventKind.ApprovalRejected);
        Assert.Empty(host.Services.GetRequiredService<RecordingOperations>().Executions);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_channel_decides_once_and_a_repeated_delivery_returns_the_earlier_decision(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, projectId);
        var (approval, _) = await ProposeAsync(host, projectId);

        var decided = await DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash, idempotencyKey: "reply-1");

        Assert.True(decided.Success);
        Assert.Equal(NhAssistantApprovalStatuses.Approved, decided.Data!.Status);
        Assert.Equal(NhAssistantProposalDecision.Approve, decided.Data.Decision);
        Assert.False(decided.Data.Duplicate);
        Assert.NotNull(decided.Data.DecidedAt);
        var approved = await FindApprovalAsync(host, approval.Id);
        Assert.Equal(SecondApprover, approved.DecidedByActorId);
        // The approved proposal executes as one decided at the endpoint does.
        Assert.Equal(approval.Id, Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Executions));
        // The audit names the channel and the key, and nothing of the proposal's content.
        var recorded = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved);
        Assert.Equal(
            (approval.Id, SecondApprover, Channel, "reply-1"),
            (recorded.ApprovalId!.Value, recorded.ActorId, recorded.Channel, recorded.IdempotencyKey));

        // The channel delivers the reply again, with its key or another one: the earlier decision,
        // nothing decided, executed or audited twice.
        foreach (var key in new[] { "reply-1", "reply-2", null })
        {
            var repeated = await DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash, idempotencyKey: key);
            Assert.True(repeated.Success);
            Assert.True(repeated.Data!.Duplicate);
            Assert.Equal(
                (approval.Id, NhAssistantApprovalStatuses.Approved, NhAssistantProposalDecision.Approve, approved.DecidedAt),
                (repeated.Data.ApprovalId, repeated.Data.Status, repeated.Data.Decision, repeated.Data.DecidedAt));
        }
        Assert.Single(host.Business.Events, evt => evt.Kind is NhAssistantAuditEventKind.ApprovalApproved or NhAssistantAuditEventKind.ApprovalRejected);
        Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Executions);

        // Anything else is the ordinary refusal: another decision of the same person, another
        // approver, another hash, and a repeat at the endpoint, which keeps answering as before.
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotPending, CodeOf(await DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash, NhAssistantProposalDecision.Reject)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotPending, CodeOf(await DecideAsync(host, ThirdApprover, approval.Id, approval.ProposalHash)));
        Assert.Equal(NhAssistantErrorCodes.ProposalHashMismatch, CodeOf(await DecideAsync(host, SecondApprover, approval.Id, "sha256:another")));
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotPending, CodeOf(await DecideAtEndpointAsync(host, SecondApprover, approval.Id, approve: true, approval.ProposalHash)));

        var facts = (await FactsAsync(host, SecondApprover, approval.Id)).Data!;
        Assert.Equal(
            (NhAssistantApprovalStatuses.Approved, 0, false, NhAssistantErrorCodes.ApprovalNotPending),
            (facts.Status, facts.ApprovalsNeeded, facts.CanDecide, facts.ReasonCode));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_decision_endpoints_record_the_web_channel_and_the_service_repeats_their_decision(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, projectId);
        var (approval, _) = await ProposeAsync(host, projectId);

        Assert.True((await DecideAtEndpointAsync(host, SecondApprover, approval.Id, approve: false, approval.ProposalHash)).Success);

        var recorded = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRejected);
        Assert.Equal((NhAssistantDecisionChannels.Web, (string?)null), (recorded.Channel, recorded.IdempotencyKey));
        // The decision is a state, so the person's reply through a channel repeats it.
        var repeated = await DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash, NhAssistantProposalDecision.Reject, idempotencyKey: "reply-1");
        Assert.True(repeated.Data!.Duplicate);
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, repeated.Data.Status);
        Assert.Single(host.Business.Events, evt => evt.Kind is NhAssistantAuditEventKind.ApprovalApproved or NhAssistantAuditEventKind.ApprovalRejected);
    }

    /// <summary>
    /// The proposal expires on the application's clock: its facts say so before any decision, and
    /// the service refuses and closes it as the endpoint does.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_expired_proposal_is_refused_through_the_service(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, projectId, clock);
        var (approval, _) = await ProposeAsync(host, projectId);

        clock.Advance(TimeSpan.FromDays(3));

        var facts = (await FactsAsync(host, SecondApprover, approval.Id)).Data!;
        Assert.Equal(
            (NhAssistantApprovalStatuses.Pending, 0, false, NhAssistantErrorCodes.ApprovalExpired),
            (facts.Status, facts.ApprovalsNeeded, facts.CanDecide, facts.ReasonCode));
        Assert.Equal(NhAssistantErrorCodes.ApprovalExpired, CodeOf(await DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash)));
        Assert.Equal(NhAssistantApprovalStatuses.Expired, (await FindApprovalAsync(host, approval.Id)).Status);
        Assert.Empty(host.Services.GetRequiredService<RecordingOperations>().Executions);
    }

    /// <summary>
    /// A proposal of the whole application is for people who pass its agent's view policy: to an
    /// approver without it the service, its facts and the endpoint answer as for an unknown one.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_of_the_whole_application_is_unknown_to_an_approver_without_the_agents_view_policy(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, Guid.NewGuid());
        AssistantThread thread;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(NhAiSystemSubjects.Global, CancellationToken.None))!;
            var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(ResultsAgent, includeDisabled: false, CancellationToken.None))!;
            thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        }
        var approval = await SeedProposalAsync(host, thread.ConversationId, NhAssistantApprovalModes.Deferred);

        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await FactsAsync(host, ApproverWithoutView, approval.Id)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await DecideAtEndpointAsync(host, ApproverWithoutView, approval.Id, approve: false, approval.ProposalHash)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await DecideAsync(host, ApproverWithoutView, approval.Id, approval.ProposalHash, NhAssistantProposalDecision.Reject)));

        Assert.True((await FactsAsync(host, Viewer, approval.Id)).Data!.CanDecide);
        var rejected = await DecideAsync(host, Viewer, approval.Id, approval.ProposalHash, NhAssistantProposalDecision.Reject, idempotencyKey: "reply-9");
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, rejected.Data!.Status);
        Assert.Equal(Viewer, (await FindApprovalAsync(host, approval.Id)).DecidedByActorId);
    }

    /// <summary>
    /// LS16: the facts say whether the object changed since the proposal, by the precondition the
    /// execution runs, and an approval of a changed object is refused as stale and closes the proposal
    /// as the execution would, through the service and the endpoint alike, when the policy requires an
    /// unchanged subject. A rejection is still recorded.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_changed_object_shows_in_the_facts_and_refuses_an_approval_that_requires_an_unchanged_subject(AssistantTestProvider provider)
    {
        Guid[] projects = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await using var host = await CreateHostAsync(provider, projects[0], alsoProposing: projects[1..]);
        var viaService = (await ProposeAsync(host, projects[0])).Approval;
        var viaEndpoint = (await ProposeAsync(host, projects[1])).Approval;
        var rejected = (await ProposeAsync(host, projects[2])).Approval;

        // The projects changed after the run proposed.
        host.Services.GetRequiredService<TestSubjects>().Version = "8";

        var facts = (await FactsAsync(host, SecondApprover, viaService.Id)).Data!;
        Assert.Equal((true, true, (string?)null), (facts.SubjectChanged, facts.CanDecide, facts.ReasonCode));
        Assert.Equal(NhAssistantErrorCodes.ProposalStale, CodeOf(await DecideAsync(host, SecondApprover, viaService.Id, viaService.ProposalHash)));
        Assert.Equal(
            NhAssistantErrorCodes.ProposalStale,
            CodeOf(await DecideAtEndpointAsync(host, SecondApprover, viaEndpoint.Id, approve: true, viaEndpoint.ProposalHash)));
        var rejection = await DecideAsync(host, SecondApprover, rejected.Id, rejected.ProposalHash, NhAssistantProposalDecision.Reject);

        foreach (var closed in new[] { viaService, viaEndpoint })
        {
            var stored = await FindApprovalAsync(host, closed.Id);
            Assert.Equal(
                (NhAssistantApprovalStatuses.Stale, "subject-changed", (string?)null),
                (stored.Status, stored.ExecutionResultCode, stored.DecidedByActorId));
            Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalInvalidated && evt.ApprovalId == closed.Id);
        }
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, rejection.Data!.Status);
        Assert.Empty(host.Services.GetRequiredService<RecordingOperations>().Executions);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved);
        // A proposal that is no longer pending is not asked.
        Assert.Null((await FactsAsync(host, SecondApprover, viaService.Id)).Data!.SubjectChanged);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_an_unchanged_subject_required_a_changed_object_shows_in_the_facts_and_the_approval_is_recorded(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider, projectId, requireUnchangedSubject: false);
        var (approval, _) = await ProposeAsync(host, projectId);
        host.Services.GetRequiredService<TestSubjects>().Version = "8";

        var facts = (await FactsAsync(host, SecondApprover, approval.Id)).Data!;
        var decided = await DecideAsync(host, SecondApprover, approval.Id, approval.ProposalHash);

        Assert.True(facts.SubjectChanged);
        Assert.Equal(NhAssistantApprovalStatuses.Approved, decided.Data!.Status);
        Assert.Equal(approval.Id, Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Executions));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_replaced_precondition_decides_whether_the_object_changed_and_without_versions_the_default_cannot_tell(AssistantTestProvider provider)
    {
        Guid[] projects = [Guid.NewGuid(), Guid.NewGuid()];
        var archived = new ArchivedProjects();
        await using var host = await CreateHostAsync(
            provider,
            projects[0],
            configure: services =>
            {
                services.AddSingleton(archived);
                services.Replace(ServiceDescriptor.Scoped<INhAiProposalPrecondition, ArchivedPrecondition>());
            },
            alsoProposing: projects[1..]);
        var first = (await ProposeAsync(host, projects[0])).Approval;
        var second = (await ProposeAsync(host, projects[1])).Approval;

        // The version stays; the application's precondition alone finds the first project archived.
        var before = (await FactsAsync(host, SecondApprover, first.Id)).Data!;
        archived.Ids.Add(projects[0].ToString());
        var after = (await FactsAsync(host, SecondApprover, first.Id)).Data!;
        var refused = await DecideAsync(host, SecondApprover, first.Id, first.ProposalHash);

        Assert.Equal((false, true), (before.SubjectChanged, after.SubjectChanged));
        Assert.Equal(NhAssistantErrorCodes.ProposalStale, CodeOf(refused));
        var stored = await FindApprovalAsync(host, first.Id);
        Assert.Equal((NhAssistantApprovalStatuses.Stale, "project-archived"), (stored.Status, stored.ExecutionResultCode));
        Assert.True((await DecideAsync(host, SecondApprover, second.Id, second.ProposalHash)).Success);

        // The default precondition compares versions; without the current one it cannot tell.
        await using var unversioned = await CreateHostAsync(provider, projects[0]);
        var proposal = (await ProposeAsync(unversioned, projects[0])).Approval;
        unversioned.Services.GetRequiredService<TestSubjects>().Version = null!;
        Assert.Null((await FactsAsync(unversioned, SecondApprover, proposal.Id)).Data!.SubjectChanged);
        Assert.True((await DecideAsync(unversioned, SecondApprover, proposal.Id, proposal.ProposalHash)).Success);
    }

    /// <summary>
    /// A host whose model proposes once to activate <paramref name="projectId"/>, and then once for
    /// each project in <paramref name="alsoProposing"/>.
    /// </summary>
    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        Guid projectId,
        TimeProvider? clock = null,
        bool requireUnchangedSubject = true,
        Action<IServiceCollection>? configure = null,
        IReadOnlyList<Guid>? alsoProposing = null)
    {
        var model = new NhAiScriptedChatClient();
        foreach (var project in new[] { projectId }.Concat(alsoProposing ?? []))
        {
            model
                .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = project, status = "Active" } }, "call-status-" + project.ToString("N"))
                .RespondWithText("I proposed to activate the project.");
        }
        var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }
                var subjects = new TestSubjects();
                subjects.Roles[SecondApprover] = NhAiSubjectRole.Approver;
                subjects.Roles[ThirdApprover] = NhAiSubjectRole.Approver;
                services.AddSingleton(subjects);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, DecisionPrincipals>());
                services.AddAuthorization(options => options.AddPolicy(ViewPolicy, policy => policy.RequireClaim("permission", ViewPolicy)));
                // Approved proposals are queued for execution; this host records the queueing.
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                configure?.Invoke(services);
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("channel-review", policy =>
                {
                    policy
                        .Lifetime(TimeSpan.FromDays(3))
                        .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                        .Mode(NhAiApprovalMode.Deferred)
                        .FourEyes()
                        .BindTo(NhAiApprovalBinding.ToolContract);
                    if (requireUnchangedSubject)
                    {
                        policy.RequireUnchangedSubject();
                    }
                })
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("channel-review"))
                .AddAgent(AssistantTestData.Agent(ResultsAgent))
                .ConfigureAgent(ResultsAgent, run => run.WorksOnApplication().ViewPolicy(ViewPolicy)));
        return host;
    }

    /// <summary>
    /// A trigger run started by <see cref="Initiator"/> whose model proposes one status change, which
    /// waits deferred in the project's thread.
    /// </summary>
    private static async Task<(AssistantApproval Approval, AssistantThread Thread)> ProposeAsync(AssistantTestHost host, Guid projectId)
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
            TriggerId = "project-created",
            InitiatorActorId = Initiator,
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
            Initiator,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        var result = await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Project PRJ-1 was created.")]),
            CancellationToken.None);
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);

        var approval = Assert.Single(await runs.GetRunApprovalsAsync(run.Id, CancellationToken.None));
        Assert.Equal((NhAssistantApprovalStatuses.Pending, Initiator), (approval.Status, approval.InitiatorActorId));
        return (approval, thread);
    }

    /// <summary>
    /// Stores a pending proposal in the thread, as a chat turn, a consultation or a run of the whole
    /// application leaves one.
    /// </summary>
    private static async Task<AssistantApproval> SeedProposalAsync(
        AssistantTestHost host,
        Guid conversationId,
        string mode,
        Guid? consultationId = null)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = "{}",
            ToolId = "projects.complete",
            Summary = "Complete the project.",
            ArgumentsPreview = "{}",
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(1),
            ConsultationId = consultationId,
            Mode = mode,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var scope = host.Services.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval;
    }

    private static async Task<TaskResult<NhAssistantProposalDecisionFacts>> FactsAsync(AssistantTestHost host, string actorId, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantProposalDecisionService>()
            .GetDecisionFactsAsync(Person(actorId), approvalId, CancellationToken.None);
    }

    private static async Task<TaskResult<NhAssistantProposalDecisionResult>> DecideAsync(
        AssistantTestHost host,
        string actorId,
        Guid approvalId,
        string expectedProposalHash,
        NhAssistantProposalDecision decision = NhAssistantProposalDecision.Approve,
        string channel = Channel,
        string? idempotencyKey = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantProposalDecisionService>().DecideAsync(
            Person(actorId),
            approvalId,
            expectedProposalHash,
            decision,
            channel,
            idempotencyKey,
            CancellationToken.None);
    }

    /// <summary>
    /// Decides as <c>POST approvals/{id}/decisions</c> does for the signed-in person.
    /// </summary>
    private static async Task<TaskResult<AssistantApproval>> DecideAtEndpointAsync(
        AssistantTestHost host,
        string actorId,
        Guid approvalId,
        bool approve,
        string expectedProposalHash)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approvalId,
            actorId,
            Principal(actorId),
            approve,
            expectedProposalHash,
            reason: null,
            CancellationToken.None);
    }

    private static async Task<AssistantApproval> FindApprovalAsync(AssistantTestHost host, Guid approvalId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
    }

    private static NhAssistantDecidingPerson Person(string actorId)
    {
        return new NhAssistantDecidingPerson(Principal(actorId), actorId);
    }

    private static ClaimsPrincipal Principal(string actorId)
    {
        return DecisionPrincipals.For(actorId);
    }

    private static string? CodeOf(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The projects the application archived since a proposal, which its own precondition finds stale.
    /// </summary>
    private sealed class ArchivedProjects
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Ids { get; } = [];
    }

    private sealed class ArchivedPrecondition(ArchivedProjects archived) : INhAiProposalPrecondition
    {
        public ValueTask<NhAiPreconditionResult> CheckAsync(
            NhAiProposalPreconditionContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(archived.Ids.Contains(context.Subject.Id)
                ? NhAiPreconditionResult.Stale("project-archived")
                : NhAiPreconditionResult.Satisfied);
        }
    }

    /// <summary>
    /// The people as the application authenticates them and as headless runs rebuild them: the viewer
    /// passes the results' view policy, everyone else only signs in.
    /// </summary>
    private sealed class DecisionPrincipals : INhAiPrincipalFactory
    {
        public static ClaimsPrincipal For(string actorId)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actorId) };
            if (actorId == Viewer)
            {
                claims.Add(new Claim("permission", ViewPolicy));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<ClaimsPrincipal?>(For(actorId));
        }
    }
}
