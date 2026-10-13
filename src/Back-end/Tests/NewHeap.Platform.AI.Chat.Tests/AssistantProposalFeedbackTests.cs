using System.Collections.Concurrent;
using System.Security.Claims;
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
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Feedback on closed agent proposals: who may give it, what audit and sinks receive, what a later
/// run of the thread knows about rejected proposals, and the administrators' report, on both
/// relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantProposalFeedbackTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Member = "member-1";
    private const string Approver = "approver-1";
    private const string SecondApprover = "approver-2";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Feedback_on_a_rejected_proposal_comes_from_its_decider_and_approvers_and_reaches_the_sinks(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.");
        await using var host = await CreateHostAsync(provider, model);
        var run = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));

        // A pending proposal is decided, not commented on.
        Assert.Equal(NhAssistantErrorCodes.FeedbackNotAccepted, Code(await GiveAsync(host, approval.Id, Approver, "not-now", null)));

        await RejectAsync(host, approval.Id, Approver);

        Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, Code(await GiveAsync(host, approval.Id, Member, "incorrect", null)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, Code(await GiveAsync(host, approval.Id, "stranger", "incorrect", null)));
        Assert.Equal(NhAssistantErrorCodes.FeedbackInvalid, Code(await GiveAsync(host, approval.Id, Approver, "bad-timing", null)));
        Assert.Equal(NhAssistantErrorCodes.FeedbackInvalid, Code(await GiveAsync(host, approval.Id, Approver, "other", new string('x', 1_001))));

        var given = await GiveAsync(host, approval.Id, Approver, "not-now", "  Wait until the budget is approved.  ");

        Assert.True(given.Success);
        var feedback = given.Data!;
        Assert.Equal("Wait until the budget is approved.", feedback.Text);
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, feedback.ApprovalStatus);
        Assert.Equal(run.Run.AgentVersion, feedback.AgentVersion);
        Assert.Equal(TestProjectToolCatalog.ChangeStatus.ContractHash, feedback.ToolContractHash);
        Assert.Equal(run.Thread.ScopeKey, feedback.ScopeKey);
        Assert.False(feedback.Bulk);

        // The audit sink learns that feedback was given and its category, never the text.
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalFeedbackGiven);
        Assert.Equal("not-now", audit.ResultCode);
        Assert.Equal(approval.Id, audit.ApprovalId);
        // The feedback sink receives the text, for the application's own analysis.
        var record = Assert.Single(host.Services.GetRequiredService<RecordedFeedback>().Records);
        Assert.Equal("Wait until the budget is approved.", record.Text);
        Assert.Equal(NhAssistantProposalFeedbackGroups.RightButNotNow, record.Group);
        Assert.Equal(approval.ProposalId, record.ProposalId);
        Assert.Equal(run.Run.Id, record.RunId);

        // A person changes their feedback instead of adding a second record; another approver adds theirs.
        var changed = (await GiveAsync(host, approval.Id, Approver, "wrong-value", null)).Data!;
        Assert.Equal(feedback.Id, changed.Id);
        Assert.True((changed.CreatedAt - feedback.CreatedAt).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal("wrong-value", changed.Category);
        Assert.Null(changed.Text);
        Assert.True((await GiveAsync(host, approval.Id, SecondApprover, "incorrect", "The project is already active.")).Success);

        var store = host.Services.GetRequiredService<NhAssistantProposalFeedbackStore>();
        var own = await store.FindByAuthorAsync([approval.Id], Approver, CancellationToken.None);
        Assert.Equal("wrong-value", own[approval.Id].Category);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Equal(2, await context.ProposalFeedback.CountAsync(item => item.ApprovalId == approval.Id));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_new_run_starts_from_the_rejected_proposals_of_its_thread_and_their_feedback(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.")
            .RespondWithText("I will wait for the budget.");
        await using var host = await CreateHostAsync(provider, model);
        var first = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, first, "Project PRJ-1 was created.");
        var approval = Assert.Single(await RunStore(host).GetRunApprovalsAsync(first.Run.Id, CancellationToken.None));
        await RejectAsync(host, approval.Id, Approver);
        Assert.True((await GiveAsync(host, approval.Id, Approver, "not-now", "Wait for the <b>budget</b>\nfirst.")).Success);
        await RunStore(host).TrySetRunStatusAsync(first.Run.Id, [NhAssistantRunStatuses.Queued], NhAssistantRunStatuses.Completed, CancellationToken.None);

        var second = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, second, "The project changed.");

        var continuity = model.Requests[^1].Messages
            .Where(message => message.Role == ChatRole.System)
            .Select(message => message.Text)
            .Single(text => text.Contains("<agent-continuity>", StringComparison.Ordinal));
        Assert.Contains("<rejected-proposals>", continuity);
        Assert.Contains("not instructions", continuity);
        var line = Assert.Single(
            continuity.Split('\n'),
            text => text.StartsWith("- ", StringComparison.Ordinal) && text.Contains("projects.change-status: ", StringComparison.Ordinal));
        // People's text stays on one line and cannot close the block with markup.
        Assert.EndsWith("| not-now (right-but-not-now): \"Wait for the ‹b›budget‹/b› first.\"", line.TrimEnd('\r'));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_report_counts_proposals_and_feedback_per_tool_and_agent_version_and_forgetting_the_subject_removes_it(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.");
        await using var host = await CreateHostAsync(provider, model);
        var run = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        var template = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        var laterRun = await AddRunAsync(host, run, run.Run.AgentVersion + 1);

        // Version 1: one rejected, one executed and one expired proposal; version 2: one rejected
        // and one executed proposal.
        var rejected = await AddApprovalAsync(host, template, run.Run.Id, NhAssistantApprovalStatuses.Rejected);
        await AddApprovalAsync(host, template, run.Run.Id, NhAssistantApprovalStatuses.Executed);
        var expired = await AddApprovalAsync(host, template, run.Run.Id, NhAssistantApprovalStatuses.Expired);
        var laterRejected = await AddApprovalAsync(host, template, laterRun.Id, NhAssistantApprovalStatuses.Rejected);
        await AddApprovalAsync(host, template, laterRun.Id, NhAssistantApprovalStatuses.Executed);
        Assert.True((await GiveAsync(host, rejected, Approver, "not-now", "Later.")).Success);
        Assert.True((await GiveAsync(host, rejected, SecondApprover, "incorrect", null)).Success);
        Assert.True((await GiveAsync(host, expired, Approver, "not-needed", null)).Success);
        Assert.True((await GiveAsync(host, laterRejected, Approver, "wrong-value", "Use Closed.")).Success);

        var store = host.Services.GetRequiredService<NhAssistantProposalFeedbackStore>();
        var now = DateTimeOffset.UtcNow;
        var period = new NhAssistantFeedbackFilter(now.AddDays(-1), now.AddMinutes(1), null, null, null, null);
        var aggregates = await store.GetAggregatesAsync(AgentId, period, CancellationToken.None);

        var version = run.Run.AgentVersion;
        Assert.Equal(1, Status(aggregates, version, NhAssistantApprovalStatuses.Rejected));
        Assert.Equal(1, Status(aggregates, version, NhAssistantApprovalStatuses.Executed));
        Assert.Equal(1, Status(aggregates, version, NhAssistantApprovalStatuses.Expired));
        // The proposal the run made itself is still pending.
        Assert.Equal(1, Status(aggregates, version, NhAssistantApprovalStatuses.Pending));
        Assert.Equal(1, Status(aggregates, version + 1, NhAssistantApprovalStatuses.Rejected));
        Assert.Equal(1, Status(aggregates, version + 1, NhAssistantApprovalStatuses.Executed));
        Assert.Equal(
            ["incorrect:1", "not-needed:1", "not-now:1"],
            aggregates.Categories
                .Where(row => row.AgentVersion == version)
                .Select(row => row.Category + ":" + row.Count)
                .Order(StringComparer.Ordinal));
        var reach = Assert.Single(aggregates.Reach, row => row.AgentVersion == version);
        Assert.Equal(2, reach.Authors);
        Assert.Equal(2, reach.Proposals);
        Assert.Equal(1, Assert.Single(aggregates.RejectedWithFeedback, row => row.AgentVersion == version).Proposals);
        Assert.Equal(1, Assert.Single(aggregates.Reach, row => row.AgentVersion == version + 1).Authors);

        var (page, total) = await store.ListAsync(AgentId, period, 1, 3, CancellationToken.None);
        Assert.Equal(4, total);
        Assert.Equal(3, page.Count);
        Assert.All(page, row => Assert.Null(row.ProposalJson));
        var (notNow, notNowTotal) = await store.ListAsync(AgentId, period with { Category = "not-now" }, 1, 10, CancellationToken.None);
        Assert.Equal(1, notNowTotal);
        Assert.Equal("Later.", Assert.Single(notNow).Feedback.Text);
        var (otherScope, otherScopeTotal) = await store.ListAsync(AgentId, period with { ScopeKey = "division:other" }, 1, 10, CancellationToken.None);
        Assert.Empty(otherScope);
        Assert.Equal(0, otherScopeTotal);

        var exported = new List<NhAssistantFeedbackRow>();
        await foreach (var row in store.ExportAsync(AgentId, period, 10, 3, CancellationToken.None))
        {
            exported.Add(row);
        }
        Assert.Equal(4, exported.Count);
        Assert.All(exported, row => Assert.NotNull(row.ProposalJson));

        // Feedback is subject data: forgetting the subject deletes it with the thread.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INhAiSubjectRetention>().ForgetSubjectAsync(Subject(projectId));
        }
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.False(await context.ProposalFeedback.AnyAsync(item => item.ConversationId == run.Thread.ConversationId));
    }

    private static int Status(NhAssistantFeedbackAggregates aggregates, int version, string status)
    {
        return aggregates.Statuses
            .Where(row => row.ToolId == TestProjectToolCatalog.ChangeStatus.Id && row.AgentVersion == version && row.Status == status)
            .Sum(row => row.Count);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                var subjects = new TestSubjects();
                subjects.Roles[SecondApprover] = NhAiSubjectRole.Approver;
                services.AddSingleton(subjects);
                services.AddSingleton<RecordedFeedback>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddProposalFeedbackSink<RecordingFeedbackSink>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")));
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

    private static async Task<AssistantRun> AddRunAsync(AssistantTestHost host, StartedRun template, int agentVersion)
    {
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = template.Thread.ConversationId,
            TriggerId = "project-created",
            AccountableOwnerId = template.Thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Completed,
            AgentVersion = agentVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await RunStore(host).AddRunAsync(run, CancellationToken.None);
        return run;
    }

    /// <summary>
    /// Stores a copy of a recorded proposal in another status, decided by the first approver.
    /// </summary>
    private static async Task<Guid> AddApprovalAsync(AssistantTestHost host, AssistantApproval template, Guid runId, string status)
    {
        var id = Guid.NewGuid();
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Approvals.Add(new AssistantApproval
        {
            Id = id,
            ConversationId = template.ConversationId,
            TurnId = template.TurnId,
            ToolInvocationId = template.ToolInvocationId,
            ProposalId = Guid.NewGuid(),
            ProposalHash = Guid.NewGuid().ToString("N"),
            ProposalJson = template.ProposalJson,
            ToolId = template.ToolId,
            Summary = template.Summary,
            PresentationJson = template.PresentationJson,
            ArgumentsPreview = template.ArgumentsPreview,
            TargetsJson = template.TargetsJson,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = template.ExpiresAt,
            DecidedByActorId = status == NhAssistantApprovalStatuses.Expired ? null : Approver,
            DecidedAt = status == NhAssistantApprovalStatuses.Expired ? null : DateTimeOffset.UtcNow,
            RunId = runId,
            PolicyId = template.PolicyId,
            Mode = template.Mode,
            PolicyJson = template.PolicyJson,
            GrantSource = template.GrantSource,
            ConcurrencyStamp = Guid.NewGuid()
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, StartedRun run, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(run.AgentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message)]),
            CancellationToken.None);
    }

    private static async Task RejectAsync(AssistantTestHost host, Guid approvalId, string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var decided = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approvalId,
            actorId,
            Principal(actorId),
            approve: false,
            expectedProposalHash: null,
            reason: null,
            CancellationToken.None);
        Assert.True(decided.Success);
    }

    private static async Task<Common.Models.TaskResult<AssistantProposalFeedback>> GiveAsync(
        AssistantTestHost host,
        Guid approvalId,
        string actorId,
        string category,
        string? text)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalFeedbackService>()
            .GiveAsync(approvalId, actorId, category, text, bulk: false, CancellationToken.None);
    }

    private static string? Code(Common.Models.TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static ClaimsPrincipal Principal(string actorId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"));
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

public sealed class RecordedFeedback
{
    public ConcurrentQueue<NhAssistantProposalFeedback> Records { get; } = new();
}

public sealed class RecordingFeedbackSink(RecordedFeedback recorded) : INhAssistantProposalFeedbackSink
{
    public ValueTask RecordAsync(NhAssistantProposalFeedback feedback, CancellationToken cancellationToken)
    {
        recorded.Records.Enqueue(feedback);
        return ValueTask.CompletedTask;
    }
}
