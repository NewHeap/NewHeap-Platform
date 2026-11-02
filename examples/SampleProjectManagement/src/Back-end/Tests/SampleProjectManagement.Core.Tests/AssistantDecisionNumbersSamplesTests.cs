using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Resources;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-279 (FB82): how people decide the intake reviewer's proposals, in numbers. Participants ask on the
/// project page for another status (SPM-364), which the reviewer records as proposals; the project owner
/// approves three of them after 10, 20 and 40 minutes on the host's clock. The agent's feedback summary
/// then reports the median and the 90th percentile of the time to a decision, and the administration's view
/// per kind of action over all agents names the kind as the cards do and marks it a candidate for running
/// without approval: with the development thresholds of the sample (three decisions, as
/// <c>appsettings.Development.json</c> sets them) the three approvals are enough, until a rejection makes the
/// kind approved too rarely and rejected too recently. Only administrators see the view, and nothing about
/// deciding changes: the next proposal still waits for its approver. The host's clock moves instead of
/// waiting, so the times are known.
/// </summary>
public sealed class AssistantDecisionNumbersSamplesTests(AssistantDecisionNumbersSampleHost host)
    : IClassFixture<AssistantDecisionNumbersSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_279_decision_times_and_the_view_per_kind_of_action_mark_a_routine_kind_as_a_candidate()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        administrator.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        // Three participants' requests wait together; the owner decides them one after the other.
        var waiting = new List<NhAssistantProposalDto>();
        foreach (var key in new[] { "ROUTINE-1", "ROUTINE-2", "ROUTINE-3" })
        {
            waiting.Add(await RequestOnHoldAsync(key, participant, owner, cancellation));
        }
        foreach (var (proposal, minutes) in waiting.Zip([10, 10, 20]))
        {
            host.Clock.Advance(TimeSpan.FromMinutes(minutes));
            await DecideAsync(owner, proposal, "approve", cancellation);
            await WaitForStatusAsync(owner, proposal, NhAssistantApprovalStatuses.Executed);
        }

        var to = host.Clock.GetUtcNow().AddMinutes(1);
        var period = $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var kindsPath = "/api/assistant/admin/proposal-kinds" + period + "&toolId=" + ProjectAiTools.ChangeStatusToolId;

        // The agent's summary: decided after about 10, 20 and 40 minutes, so a median of 20 minutes.
        var summary = (await administrator.GetFromJsonAsync<NhAssistantFeedbackSummaryDto>(
            $"/api/assistant/admin/agents/{SampleIntakeReviewerComposition.AgentId}/feedback/summary{period}",
            Json,
            cancellation))!;
        var row = Assert.Single(summary.Rows, item => item.ToolId == ProjectAiTools.ChangeStatusToolId);
        var time = Assert.IsType<NhAssistantDecisionTimeDto>(row.DecisionTime);
        Assert.Equal((3, false), (time.SampleSize, time.Sampled));
        Assert.InRange(time.MedianSeconds, 1_200, 1_260);
        Assert.InRange(time.Percentile90Seconds, 2_160, 2_220);

        // The view per kind of action over all agents, with the name the cards show.
        var view = (await administrator.GetFromJsonAsync<NhAssistantProposalKindsDto>(kindsPath, Json, cancellation))!;
        Assert.Equal(3, view.Thresholds.MinimumDecisions);
        Assert.Equal(NhAssistantRoutineCandidateOptions.DefaultMinimumApprovalRate, view.Thresholds.MinimumApprovalRate);
        var kind = Assert.Single(view.Kinds);
        var texts = new ResourceManager(typeof(SampleProjectStatusRequestTexts));
        Assert.Equal(texts.GetString("ToolName", CultureInfo.GetCultureInfo("en-US")), kind.DisplayName);
        var reviewer = Assert.Single(kind.Agents);
        Assert.Equal(
            (SampleIntakeReviewerComposition.AgentId, ProjectAiTools.ChangeStatusToolId, 3, 1.0, true),
            (reviewer.AgentId, reviewer.ToolId, reviewer.Numbers.Decisions, reviewer.Numbers.ApprovalRate, reviewer.Candidate));
        Assert.Empty(reviewer.UnmetThresholds);

        // Other people do not see the view, not even the project's approver.
        using var refused = await owner.GetAsync(kindsPath, cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // A candidate is a suggestion: the next request still waits for the owner, who rejects it.
        var next = await RequestOnHoldAsync("ROUTINE-4", participant, owner, cancellation);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, next.Status);
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await DecideAsync(owner, next, "reject", cancellation);

        to = host.Clock.GetUtcNow().AddMinutes(1);
        var after = (await administrator.GetFromJsonAsync<NhAssistantProposalKindsDto>(
            $"/api/assistant/admin/proposal-kinds?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&toolId={ProjectAiTools.ChangeStatusToolId}",
            Json,
            cancellation))!;
        var rejected = Assert.Single(Assert.Single(after.Kinds).Agents);
        Assert.False(rejected.Candidate);
        Assert.Equal(
            [NhAssistantRoutineCandidateReasons.ApprovalRateTooLow, NhAssistantRoutineCandidateReasons.RecentRejection],
            rejected.UnmetThresholds);
        Assert.Equal((3, 1), (rejected.Numbers.Approved, rejected.Numbers.Rejected));
    }

    /// <summary>
    /// A participant of a new project asks for the status on hold; the reviewer's proposal waits for the owner.
    /// </summary>
    private async Task<NhAssistantProposalDto> RequestOnHoldAsync(string key, HttpClient participant, HttpClient owner, CancellationToken cancellation)
    {
        var project = await host.CreateProjectAsync(key, ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        using var asked = await participant.PostAsJsonAsync(
            $"/projects/{project}/status-requests",
            new ProjectStatusRequestMutateModel
            {
                Status = ProjectStatus.OnHold,
                Reason = "The supplier delivers later.",
                RequestId = "routine-" + project.ToString("N")
            },
            Json,
            cancellation);
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        var recorded = (await asked.Content.ReadFromJsonAsync<ProjectStatusRequestViewModel>(Json, cancellation))!;
        return Assert.Single(await ProposalsAsync(owner, recorded.ConversationId!.Value), item => item.ApprovalId == recorded.ApprovalId);
    }

    private static async Task DecideAsync(HttpClient owner, NhAssistantProposalDto proposal, string decision, CancellationToken cancellation)
    {
        using var decided = await owner.PostAsJsonAsync(
            $"/api/assistant/approvals/{proposal.ApprovalId}/decisions",
            new NhAssistantProposalDecisionRequest(decision, proposal.ProposalHash, null),
            Json,
            cancellation);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
    }

    private static async Task<NhAssistantProposalDto[]> ProposalsAsync(HttpClient client, Guid conversationId)
    {
        return await client.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json) ?? [];
    }

    /// <summary>
    /// Waits until the proposal has the status; the host's background operations execute it.
    /// </summary>
    private static async Task WaitForStatusAsync(HttpClient client, NhAssistantProposalDto proposal, string status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await ProposalsAsync(client, proposal.ConversationId)).Any(item => item.ApprovalId == proposal.ApprovalId && item.Status == status))
            {
                return;
            }

            await Task.Delay(AssistantAgentRunSampleHost.PollInterval);
        }

        throw new TimeoutException($"Proposal {proposal.ApprovalId} is not {status} after 90 seconds.");
    }
}

/// <summary>
/// SPM-279 (FB82): the host of SPM-364 with the development thresholds of the view per kind of action, as
/// <c>appsettings.Development.json</c> sets them: three decisions are enough for a candidate.
/// </summary>
public sealed class AssistantDecisionNumbersSampleHost(SamplePostgreSqlServer postgreSql) : AssistantProposalForPersonSampleHost(postgreSql)
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddNewHeapAssistant(assistant => assistant.ConfigureRoutineCandidates(thresholds => thresholds.MinimumDecisions = 3));
    }
}
