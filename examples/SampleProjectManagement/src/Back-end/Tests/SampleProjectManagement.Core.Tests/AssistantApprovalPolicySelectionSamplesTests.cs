using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-356: the API's approval policy selector keeps four eyes on the intake review only while the
/// approver directory finds a second approver; on a project whose creator is its only approver the review
/// waits under the same policy without four eyes, and the decision checks the policy the proposal keeps.
/// SPM-357: the approval reply channel asks everyone the approver directory lists under the proposal's
/// policy copy, and says whether that is everyone. Runs and approved proposals execute as background
/// operations through Hangfire, as in Program.cs.
/// </summary>
public sealed class AssistantApprovalPolicySelectionSamplesTests(AssistantApprovalPolicySelectionSampleHost host)
    : IClassFixture<AssistantApprovalPolicySelectionSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_356_the_intake_review_needs_four_eyes_only_while_a_second_approver_may_decide()
    {
        using var owner = host.CreateClient(host.OwnerId.ToString());

        // The creator is the project's only approver: four eyes would keep the review waiting for nobody.
        var alone = await host.CreateProjectAsync("SOLE", ownerId: host.OwnerId);
        var aloneReview = await ReviewAsync(owner, alone);
        Assert.Equal(SampleApprovalPolicySelectionComposition.SoleApproverPolicyId, aloneReview.PolicyId);

        // A second approver keeps four eyes.
        var shared = await host.CreateProjectAsync("SHARED", ownerId: host.OwnerId);
        await host.AddMemberAsync(shared, host.ColleagueId, ProjectMemberRole.Approver);
        var sharedReview = await ReviewAsync(owner, shared);
        Assert.Equal(SampleIntakeReviewerComposition.ApprovalPolicyId, sharedReview.PolicyId);

        // The decision checks the policy each proposal keeps: the creator decides the review of the project
        // they approve alone, and not the one a colleague approves too.
        using var refused = await DecideAsync(owner, sharedReview);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(NhAssistantErrorCodes.FourEyesRequired, await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var decided = await DecideAsync(owner, aloneReview);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        // A colleague who joins later changes nothing for the proposal: it keeps the policy it was recorded with.
        await host.AddMemberAsync(alone, host.ColleagueId, ProjectMemberRole.Approver);
        Assert.Equal(
            SampleApprovalPolicySelectionComposition.SoleApproverPolicyId,
            (await ProposalsAsync(owner, aloneReview.ConversationId)).Single(item => item.ApprovalId == aloneReview.ApprovalId).PolicyId);
    }

    [Fact]
    public async Task SPM_357_the_reply_channel_asks_everyone_the_approver_directory_lists_under_the_proposals_policy()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var project = await host.CreateProjectAsync("ASK", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        var review = await ReviewAsync(owner, project);
        var alone = await host.CreateProjectAsync("ASK-SOLE", ownerId: host.OwnerId);
        var soleReview = await ReviewAsync(owner, alone);

        await using var scope = host.Services.CreateAsyncScope();
        var channel = scope.ServiceProvider.GetRequiredService<SampleApprovalReplyChannel>();
        var directory = scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>();

        // Four eyes leave the creator out and a participant is no approver: only the colleague is asked,
        // and the directory says that is everyone.
        var offers = await channel.OfferToApproversAsync(review.ApprovalId, CultureInfo.GetCultureInfo("en-US"), cancellation);
        Assert.True(offers.Success);
        Assert.True(offers.Data!.Complete);
        var offer = Assert.Single(offers.Data.Offers);
        Assert.Equal(host.ColleagueId, offer.UserId);
        Assert.NotNull(offer.DisplayName);
        // The project the creator approves alone asks the creator, under the policy the selector chose.
        var soleOffer = Assert.Single((await channel.OfferToApproversAsync(soleReview.ApprovalId, CultureInfo.GetCultureInfo("en-US"), cancellation)).Data!.Offers);
        Assert.Equal(host.OwnerId, soleOffer.UserId);

        // The application counts the approvers of a policy before any proposal, as the selector does.
        var others = await directory.CountAsync(
            SampleIntakeReviewerComposition.ApprovalPolicyId,
            ProjectSubjects.For(project),
            tenantId: null,
            excludeActorId: host.OwnerId.ToString(),
            cancellationToken: cancellation);
        Assert.Equal(new NhAssistantApproverCount(1, true), others.Data);

        // The colleague answers the message with the token the channel issued.
        var answered = await channel.ReceiveAsync(new SampleApprovalReply("delivery-357", offer.Offer.Token, "yes"), cancellation);
        Assert.True(answered.Success);
        Assert.Equal(NhAssistantProposalDecision.Approve, answered.Data!.Decision);
    }

    /// <summary>
    /// Creates the project's intake review as the creator starts it and waits for its proposal.
    /// </summary>
    private async Task<NhAssistantProposalDto> ReviewAsync(HttpClient owner, Guid projectId)
    {
        var receipt = Assert.Single(await host.PublishProjectCreatedAsync(projectId, createdBy: host.OwnerId));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
        return await WaitForPendingProposalAsync(owner, receipt.ConversationId!.Value);
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, NhAssistantProposalDto proposal)
    {
        return client.PostAsJsonAsync(
            $"/api/assistant/approvals/{proposal.ApprovalId}/decisions",
            new NhAssistantProposalDecisionRequest("approve", proposal.ProposalHash, "Sample decision"),
            Json);
    }

    private static async Task<NhAssistantProposalDto[]> ProposalsAsync(HttpClient client, Guid conversationId)
    {
        return await client.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json) ?? [];
    }

    /// <summary>
    /// Waits until the thread has a pending proposal; the host's background operations run the review.
    /// </summary>
    private static async Task<NhAssistantProposalDto> WaitForPendingProposalAsync(HttpClient client, Guid conversationId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/assistant/threads/{conversationId}/proposals");
            if (response.IsSuccessStatusCode
                && (await response.Content.ReadFromJsonAsync<NhAssistantProposalDto[]>(Json) ?? [])
                    .FirstOrDefault(item => item.Status == NhAssistantApprovalStatuses.Pending) is { } pending)
            {
                return pending;
            }

            await Task.Delay(AssistantAgentRunSampleHost.PollInterval);
        }

        throw new TimeoutException($"Thread {conversationId} has no pending proposal after 90 seconds.");
    }
}

/// <summary>
/// SPM-356 and SPM-357: the agent run host with the approval policy selection of Program.cs.
/// </summary>
public sealed class AssistantApprovalPolicySelectionSampleHost(SamplePostgreSqlServer postgreSql) : AssistantAgentRunSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSampleApprovalPolicySelection();
    }
}
