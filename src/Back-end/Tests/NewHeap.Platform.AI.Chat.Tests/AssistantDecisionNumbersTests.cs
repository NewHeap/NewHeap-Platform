using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// FB82, the numbers of how people decide agent proposals: the median and the 90th percentile of the
/// time from a proposal to a person's decision in the feedback summary, and the administrators' view
/// per kind of action over all agents with its candidates for running without approval, on both
/// relational providers, and only for administrators.
/// </summary>
public sealed class AssistantDecisionNumbersTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Assistant = "project-assistant";
    private const string Reviewer = "project-reviewer";
    private const string ChangeStatus = "projects.change-status";
    private const string AddTask = "projects.add-task";
    private const string Approver = "approver-1";
    private const string KindsPath = "/api/assistant/admin/proposal-kinds";

    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_view_per_kind_of_action_counts_decision_times_and_marks_candidates(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        await SeedAsync(app);
        using var admin = app.CreateClient(Approver, admin: true);
        var period = $"?from={Uri.EscapeDataString(Start.ToString("O"))}&to={Uri.EscapeDataString(End.ToString("O"))}";

        var view = (await admin.GetFromJsonAsync<NhAssistantProposalKindsDto>(KindsPath + period, Json))!;

        Assert.Equal((Start, End), (view.From, view.To));
        // The host lowers the fewest decisions; the other thresholds keep their defaults.
        Assert.Equal(new NhAssistantRoutineCandidateThresholdsDto(3, 0.98, 7, 0), view.Thresholds);
        Assert.Equal([ChangeStatus, AddTask], view.Kinds.Select(kind => kind.ToolId));

        var changeStatus = view.Kinds[0];
        // The name the cards show comes from the newest proposal's presentation.
        Assert.Equal("Change project status", changeStatus.DisplayName);
        Assert.Equal((8, 1, 9), (changeStatus.Numbers.Approved, changeStatus.Numbers.Rejected, changeStatus.Numbers.Decisions));
        // Over all agents: 30, 60, 90, 120, 150, 180, 240, 300 and 600 seconds.
        Assert.Equal(new NhAssistantDecisionTimeDto(9, 150, 360, false), changeStatus.Numbers.DecisionTime);
        Assert.Equal([Assistant, Reviewer], changeStatus.Agents.Select(agent => agent.AgentId));

        var assistant = changeStatus.Agents[0];
        Assert.Equal(
            new NhAssistantProposalKindNumbersDto(
                Pending: 1,
                Approved: 5,
                Rejected: 1,
                Decisions: 6,
                Expired: 1,
                Withdrawn: 1,
                Invalidated: 1,
                StaleAfterApproval: 1,
                ExecutionFailed: 0,
                ApprovalRate: 5d / 6,
                RejectionRate: 1d / 6,
                // 60, 120, 180, 240 (stale after its approval), 300 (the rejection) and 600 seconds;
                // the agent's withdrawal, the platform's invalidation and the expiry do not count.
                DecisionTime: new NhAssistantDecisionTimeDto(6, 210, 450, false),
                LastRejectedAt: Start.AddDays(28).AddSeconds(300)),
            assistant.Numbers);
        Assert.False(assistant.Candidate);
        Assert.Equal(
            [
                NhAssistantRoutineCandidateReasons.ApprovalRateTooLow,
                NhAssistantRoutineCandidateReasons.RecentRejection,
                NhAssistantRoutineCandidateReasons.StaleOrFailedAfterApproval
            ],
            assistant.UnmetThresholds);
        Assert.Equal(("nh-assistant.agents.project-assistant.name", "code"), (assistant.AgentDisplayName, assistant.AgentSource));

        var reviewer = changeStatus.Agents[1];
        Assert.True(reviewer.Candidate);
        Assert.Empty(reviewer.UnmetThresholds);
        Assert.Equal((Reviewer, ChangeStatus, 3, 1.0), (reviewer.AgentId, reviewer.ToolId, reviewer.Numbers.Decisions, reviewer.Numbers.ApprovalRate));
        Assert.Equal(new NhAssistantDecisionTimeDto(3, 90, 138, false), reviewer.Numbers.DecisionTime);

        var addTask = view.Kinds[1];
        // Without a stored presentation the kind is named by its tool id.
        Assert.Equal(AddTask, addTask.DisplayName);
        var tasks = Assert.Single(addTask.Agents);
        Assert.False(tasks.Candidate);
        Assert.Equal([NhAssistantRoutineCandidateReasons.TooFewDecisions], tasks.UnmetThresholds);
        Assert.Equal(new NhAssistantDecisionTimeDto(2, 60, 72, false), tasks.Numbers.DecisionTime);

        // The filters narrow the view to one agent or one kind.
        var ofReviewer = (await admin.GetFromJsonAsync<NhAssistantProposalKindsDto>($"{KindsPath}{period}&agentId={Reviewer}", Json))!;
        Assert.Equal([ChangeStatus, AddTask], ofReviewer.Kinds.Select(kind => kind.ToolId));
        Assert.All(ofReviewer.Kinds, kind => Assert.Equal(Reviewer, Assert.Single(kind.Agents).AgentId));
        Assert.Equal(3, ofReviewer.Kinds[0].Numbers.Decisions);
        var ofTasks = (await admin.GetFromJsonAsync<NhAssistantProposalKindsDto>($"{KindsPath}{period}&toolId={AddTask}", Json))!;
        Assert.Equal(AddTask, Assert.Single(ofTasks.Kinds).ToolId);
        // A period before the proposals lists nothing.
        var before = (await admin.GetFromJsonAsync<NhAssistantProposalKindsDto>(
            $"{KindsPath}?from={Uri.EscapeDataString(Start.AddDays(-30).ToString("O"))}&to={Uri.EscapeDataString(Start.AddDays(-2).ToString("O"))}",
            Json))!;
        Assert.Empty(before.Kinds);

        // The agent's feedback summary carries the same decision times per tool and agent version.
        var summary = (await admin.GetFromJsonAsync<NhAssistantFeedbackSummaryDto>(
            $"/api/assistant/admin/agents/{Assistant}/feedback/summary{period}",
            Json))!;
        var row = Assert.Single(summary.Rows, item => item.ToolId == ChangeStatus);
        Assert.Equal(new NhAssistantDecisionTimeDto(6, 210, 450, false), row.DecisionTime);
        Assert.Equal((5, 1), (row.Approved, row.Rejected));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_administrators_see_the_view_per_kind_of_action(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        await SeedAsync(app);
        using var anonymous = app.CreateClient(user: null, access: false);
        using var member = app.CreateClient(Approver);
        using var admin = app.CreateClient(Approver, admin: true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(KindsPath)).StatusCode);
        // An approver of every project is no administrator.
        using var forbidden = await member.GetAsync(KindsPath);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var refusal = await forbidden.Content.ReadAsStringAsync();
        Assert.Equal("assistant-forbidden", JsonDocument.Parse(refusal).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(ChangeStatus, refusal);

        // The period ends after it starts and spans at most 366 days; the filters are bounded.
        var invalid = new[]
        {
            $"?from={Uri.EscapeDataString(End.ToString("O"))}&to={Uri.EscapeDataString(Start.ToString("O"))}",
            $"?from={Uri.EscapeDataString(Start.AddDays(-400).ToString("O"))}&to={Uri.EscapeDataString(End.ToString("O"))}",
            "?agentId=" + new string('a', 129),
            "?toolId=" + new string('t', 257)
        };
        foreach (var query in invalid)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(KindsPath + query)).StatusCode);
        }

        // The default period is the last 30 days, which holds none of the seeded proposals.
        var current = (await admin.GetFromJsonAsync<NhAssistantProposalKindsDto>(KindsPath, Json))!;
        Assert.Equal(TimeSpan.FromDays(30), current.To - current.From);
        Assert.Empty(current.Kinds);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Decision_times_follow_the_application_clock_also_when_it_runs_ahead_of_the_system_clock(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var reviewer = await ThreadAsync(app, Reviewer);
        var day1 = Start.AddDays(1);
        // The application's clock runs two hours ahead: a proposal and its decision follow it, while
        // when it was recorded stays on the system clock for the proposal guards.
        var ahead = TimeSpan.FromHours(2);
        await using (var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            context.Approvals.AddRange(
                Approval(reviewer, AddTask, NhAssistantApprovalStatuses.Executed, day1, 60, executed: true, clockAhead: ahead),
                Approval(reviewer, AddTask, NhAssistantApprovalStatuses.Executed, day1, 90, executed: true, clockAhead: ahead),
                Approval(reviewer, AddTask, NhAssistantApprovalStatuses.Executed, day1, 120, executed: true, clockAhead: ahead),
                // A proposal made before 30.1 has its recorded time only.
                Approval(reviewer, AddTask, NhAssistantApprovalStatuses.Executed, day1, 30, executed: true));
            await context.SaveChangesAsync();
        }
        using var admin = app.CreateClient(Approver, admin: true);
        var period = $"?from={Uri.EscapeDataString(Start.ToString("O"))}&to={Uri.EscapeDataString(End.ToString("O"))}";

        var view = (await admin.GetFromJsonAsync<NhAssistantProposalKindsDto>(KindsPath + period, Json))!;

        // 30, 60, 90 and 120 seconds, not two hours more for the three on the application's clock.
        var tasks = Assert.Single(Assert.Single(view.Kinds).Agents);
        Assert.Equal(new NhAssistantDecisionTimeDto(4, 75, 111, false), tasks.Numbers.DecisionTime);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TestSubjects>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddAgent(AssistantTestData.Agent(Reviewer))
                .ConfigureRoutineCandidates(thresholds => thresholds.MinimumDecisions = 3),
            provider: provider);
    }

    /// <summary>
    /// Stores the proposals of the period with known times: those of the assistant miss three
    /// thresholds, the reviewer's status changes are a candidate and its two tasks are too few.
    /// </summary>
    private static async Task SeedAsync(AssistantWebApplication app)
    {
        var assistant = await ThreadAsync(app, Assistant);
        var reviewer = await ThreadAsync(app, Reviewer);
        var day1 = Start.AddDays(1);
        var approvals = new List<AssistantApproval>
        {
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Executed, day1, 60, executed: true, presentation: "Old name"),
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Executed, day1, 120, executed: true),
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Executed, day1, 180, executed: true),
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Executed, day1, 600, executed: true),
            // Approved, then closed right before it ran because the project changed.
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Stale, day1, 240, executed: true),
            // Rejected two days before the period's end, within the quiet period.
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Rejected, Start.AddDays(28), 300, presentation: "Change project status"),
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Expired, day1, null),
            // Closed by the platform before anyone decided, and withdrawn by the agent itself.
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Stale, day1, 30, decidedBy: NhAssistantProposalInvalidation.SystemActorId),
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Withdrawn, day1, 10, decidedBy: NhAssistantAgentEvents.AgentActorPrefix + Assistant),
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Pending, day1, null),
            // Shadow proposals and proposals made before the period do not count.
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Shadow, day1, null),
            Approval(assistant, ChangeStatus, NhAssistantApprovalStatuses.Executed, Start.AddDays(-1), 5, executed: true),
            Approval(reviewer, ChangeStatus, NhAssistantApprovalStatuses.Executed, day1, 30, executed: true),
            Approval(reviewer, ChangeStatus, NhAssistantApprovalStatuses.Executed, day1, 90, executed: true),
            Approval(reviewer, ChangeStatus, NhAssistantApprovalStatuses.Approved, day1, 150),
            Approval(reviewer, AddTask, NhAssistantApprovalStatuses.Executed, day1, 45, executed: true),
            Approval(reviewer, AddTask, NhAssistantApprovalStatuses.Executed, day1, 75, executed: true)
        };

        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Approvals.AddRange(approvals);
        await context.SaveChangesAsync();
    }

    private static async Task<AssistantThread> ThreadAsync(AssistantWebApplication app, string agentId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var subject = new NhAiSubjectRef("project", Guid.NewGuid().ToString());
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
    }

    /// <param name="decidedAfterSeconds">When it was decided after it was made; <see langword="null"/> for an open or expired proposal.</param>
    /// <param name="clockAhead">How far the application's clock runs ahead of the system clock when the
    /// proposal stores its own time; <see langword="null"/> for a proposal made before 30.1, which has none.</param>
    private static AssistantApproval Approval(
        AssistantThread thread,
        string toolId,
        string status,
        DateTimeOffset createdAt,
        int? decidedAfterSeconds,
        bool executed = false,
        string decidedBy = Approver,
        string? presentation = null,
        TimeSpan? clockAhead = null)
    {
        var proposedAt = clockAhead is { } shift ? createdAt + shift : (DateTimeOffset?)null;
        var decidedAt = decidedAfterSeconds is { } seconds ? (proposedAt ?? createdAt).AddSeconds(seconds) : (DateTimeOffset?)null;
        return new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = Guid.NewGuid().ToString("N"),
            ToolId = toolId,
            Summary = "Proposed " + toolId,
            PresentationJson = presentation is null
                ? null
                : NhAssistantToolPresentationResolver.Serialize(new NhAssistantApprovalPresentation(presentation, "Proposed " + toolId, [])),
            ArgumentsPreview = "{}",
            Status = status,
            CreatedAt = createdAt,
            ProposedAt = proposedAt,
            ExpiresAt = createdAt.AddDays(3),
            DecidedByActorId = decidedAt is null ? null : decidedBy,
            DecidedAt = decidedAt,
            ExecutedAt = executed ? decidedAt!.Value.AddSeconds(1) : null,
            Mode = NhAssistantApprovalModes.Deferred,
            ConcurrencyStamp = Guid.NewGuid()
        };
    }
}

/// <summary>
/// FB82: the median and the 90th percentile interpolate between the nearest decisions as
/// <c>percentile_cont</c> does, a group keeps its newest 1,000 decisions, and the candidate thresholds
/// stay within their ranges; no database involved.
/// </summary>
public sealed class AssistantDecisionTimeMathTests
{
    [Fact]
    public void Percentiles_interpolate_between_the_nearest_decisions()
    {
        Assert.Equal(42, NhAssistantDecisionTimes.Percentile([42], 0.9));
        Assert.Equal(15, NhAssistantDecisionTimes.Percentile([10, 20], 0.5));
        Assert.Equal(19, NhAssistantDecisionTimes.Percentile([10, 20], 0.9), 6);
        // The 90th percentile of five decisions lies 60% of the way from the fourth to the fifth.
        Assert.Equal(76, NhAssistantDecisionTimes.Percentile([10, 20, 30, 40, 100], 0.9), 6);
    }

    [Fact]
    public void A_group_with_more_decisions_than_the_sample_keeps_its_newest()
    {
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var loaded = Enumerable.Range(0, NhAssistantDecisionTimes.SamplePerGroup + 1)
            .Select(index => new NhAssistantDecisionTimeRow(
                "agent",
                "tool",
                null,
                start.AddMinutes(index),
                // The oldest decision took a day; every newer one a minute.
                start.AddMinutes(index).AddSeconds(index == 0 ? 86_400 : 60)))
            .ToList();

        var (sample, sampled) = NhAssistantDecisionTimes.SampleOf(loaded);
        var time = NhAssistantDecisionTimes.Of(sample, sampled)!;

        Assert.True(sampled);
        Assert.Equal(new NhAssistantDecisionTimeDto(NhAssistantDecisionTimes.SamplePerGroup, 60, 60, true), time);
        Assert.Null(NhAssistantDecisionTimes.Of([], sampled: false));
    }

    [Fact]
    public void The_view_per_kind_of_action_keeps_its_json_names()
    {
        var numbers = new NhAssistantProposalKindNumbersDto(1, 2, 0, 2, 0, 0, 0, 0, 0, 1, 0, new NhAssistantDecisionTimeDto(2, 60, 90, false), null);
        var candidate = new NhAssistantRoutineCandidateDto("intake", "Intake", "admin", "projects.change-status", numbers, false, [NhAssistantRoutineCandidateReasons.TooFewDecisions]);
        var view = JsonSerializer.SerializeToElement(
            new NhAssistantProposalKindsDto(
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddDays(30),
                new NhAssistantRoutineCandidateThresholdsDto(50, 0.98, 7, 0),
                [new NhAssistantProposalKindDto("projects.change-status", "Change project status", numbers, [candidate])]),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalKindsDto);

        AssertNames(view, "from", "to", "thresholds", "kinds");
        AssertNames(view.GetProperty("thresholds"), "minimumDecisions", "minimumApprovalRate", "quietPeriodDays", "maximumStaleOrFailedAfterApproval");
        var kind = view.GetProperty("kinds")[0];
        AssertNames(kind, "toolId", "displayName", "numbers", "agents");
        AssertNames(
            kind.GetProperty("numbers"),
            "pending", "approved", "rejected", "decisions", "expired", "withdrawn", "invalidated", "staleAfterApproval",
            "executionFailed", "approvalRate", "rejectionRate", "decisionTime", "lastRejectedAt");
        AssertNames(kind.GetProperty("numbers").GetProperty("decisionTime"), "sampleSize", "medianSeconds", "percentile90Seconds", "sampled");
        AssertNames(kind.GetProperty("agents")[0], "agentId", "agentDisplayName", "agentSource", "toolId", "numbers", "candidate", "unmetThresholds");
        Assert.Equal("too-few-decisions", kind.GetProperty("agents")[0].GetProperty("unmetThresholds")[0].GetString());
    }

    private static void AssertNames(JsonElement element, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Thresholds_outside_their_ranges_count_as_their_nearest_bound()
    {
        var bounded = new NhAssistantRoutineCandidateOptions
        {
            MinimumDecisions = 0,
            MinimumApprovalRate = 1.5,
            QuietPeriod = TimeSpan.FromDays(-1),
            MaximumStaleOrFailedAfterApproval = -3
        }.Bounded();

        Assert.Equal((1, 1.0, TimeSpan.Zero, 0), (bounded.MinimumDecisions, bounded.MinimumApprovalRate, bounded.QuietPeriod, bounded.MaximumStaleOrFailedAfterApproval));
        var defaults = new NhAssistantRoutineCandidateOptions();
        Assert.Equal((50, 0.98, TimeSpan.FromDays(7), 0), (defaults.MinimumDecisions, defaults.MinimumApprovalRate, defaults.QuietPeriod, defaults.MaximumStaleOrFailedAfterApproval));
    }

    [Fact]
    public void A_rejection_just_outside_the_quiet_period_keeps_a_candidate()
    {
        var end = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var thresholds = new NhAssistantRoutineCandidateOptions();
        NhAssistantProposalKindNumbersDto Numbers(int approved, int rejected, DateTimeOffset? lastRejectedAt, int failed = 0)
        {
            var decisions = approved + rejected;
            return new NhAssistantProposalKindNumbersDto(0, approved, rejected, decisions, 0, 0, 0, 0, failed,
                (double)approved / decisions, (double)rejected / decisions, null, lastRejectedAt);
        }

        // 49 of 50 approved is exactly 98%.
        Assert.Empty(NhAssistantProposalKindReport.UnmetThresholds(Numbers(49, 1, end.AddDays(-7).AddTicks(-1)), thresholds, end));
        Assert.Equal(
            [NhAssistantRoutineCandidateReasons.RecentRejection],
            NhAssistantProposalKindReport.UnmetThresholds(Numbers(49, 1, end.AddDays(-7)), thresholds, end));
        Assert.Equal(
            [NhAssistantRoutineCandidateReasons.TooFewDecisions, NhAssistantRoutineCandidateReasons.StaleOrFailedAfterApproval],
            NhAssistantProposalKindReport.UnmetThresholds(Numbers(49, 0, null, failed: 1), thresholds, end));
        Assert.Equal(
            [NhAssistantRoutineCandidateReasons.ApprovalRateTooLow],
            NhAssistantProposalKindReport.UnmetThresholds(Numbers(48, 2, end.AddDays(-20)), thresholds, end));
    }
}
