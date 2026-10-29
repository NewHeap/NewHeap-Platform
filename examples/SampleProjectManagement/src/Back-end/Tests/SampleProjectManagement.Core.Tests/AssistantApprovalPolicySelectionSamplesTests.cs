using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Identity.Claims;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-356: the API's approval policy selector keeps four eyes on the intake review only while the
/// approver directory finds a second approver; on a project whose creator is its only approver the review
/// waits under the same policy without four eyes, and the decision checks the policy the proposal keeps.
/// SPM-357: the approval reply channel asks everyone the approver directory lists under the proposal's
/// policy copy, and says whether that is everyone. SPM-379: a guard reads the policy a review recorded and
/// closes a review without four eyes once a second approver joined. Runs and approved proposals execute as
/// background operations through Hangfire, as in Program.cs.
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
        var aloneReview = await ReviewAsync(host, owner, alone);
        Assert.Equal(SampleApprovalPolicySelectionComposition.SoleApproverPolicyId, aloneReview.PolicyId);

        // A second approver keeps four eyes.
        var shared = await host.CreateProjectAsync("SHARED", ownerId: host.OwnerId);
        await host.AddMemberAsync(shared, host.ColleagueId, ProjectMemberRole.Approver);
        var sharedReview = await ReviewAsync(host, owner, shared);
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
        var review = await ReviewAsync(host, owner, project);
        var alone = await host.CreateProjectAsync("ASK-SOLE", ownerId: host.OwnerId);
        var soleReview = await ReviewAsync(host, owner, alone);

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

    [Fact]
    public async Task SPM_379_a_review_without_four_eyes_closes_at_its_decision_once_a_second_approver_joined()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var project = await host.CreateProjectAsync("JOINED", ownerId: host.OwnerId);
        var review = await ReviewAsync(host, owner, project);
        Assert.Equal(SampleApprovalPolicySelectionComposition.SoleApproverPolicyId, review.PolicyId);

        // A colleague joins as approver while the review waits. The guard reads the policy the review
        // recorded, finds that someone besides its creator may approve now and closes it at the decision, so
        // the creator never approves alone what a colleague can check.
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        using var refused = await DecideAsync(owner, review);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(NhAssistantErrorCodes.ProposalStale, await refused.Content.ReadAsStringAsync(cancellation));
        var closed = (await ProposalsAsync(owner, review.ConversationId)).Single(item => item.ApprovalId == review.ApprovalId);
        Assert.Equal(
            (NhAssistantApprovalStatuses.Stale, NhAssistantStaleKinds.NotApplicable, SampleSoleApproverProposalGuard.SecondApproverCode),
            (closed.Status, closed.StaleKind, closed.ExecutionResultCode));
    }

    /// <summary>
    /// Creates the project's intake review as the creator starts it and waits for its proposal.
    /// </summary>
    internal static async Task<NhAssistantProposalDto> ReviewAsync(AssistantApprovalPolicySelectionSampleHost host, HttpClient owner, Guid projectId)
    {
        var receipt = Assert.Single(await host.PublishProjectCreatedAsync(projectId, createdBy: host.OwnerId));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
        return await WaitForPendingProposalAsync(owner, receipt.ConversationId!.Value);
    }

    internal static Task<HttpResponseMessage> DecideAsync(HttpClient client, NhAssistantProposalDto proposal)
    {
        return client.PostAsJsonAsync(
            $"/api/assistant/approvals/{proposal.ApprovalId}/decisions",
            new NhAssistantProposalDecisionRequest("approve", proposal.ProposalHash, "Sample decision"),
            Json);
    }

    internal static async Task<NhAssistantProposalDto[]> ProposalsAsync(HttpClient client, Guid conversationId)
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
/// SPM-378: the application's project managers decide the intake review of a project its creator approves
/// alone, four-eyes, because the application names them (<see cref="SampleApproverCandidates"/>) and the
/// approver directory can therefore count the portfolio policy's rule completely. A host of its own: the
/// project manager this test adds would change the reviews of SPM-356.
/// </summary>
public sealed class AssistantApproverCandidatesSamplesTests(AssistantApprovalPolicySelectionSampleHost host)
    : IClassFixture<AssistantApprovalPolicySelectionSampleHost>
{
    [Fact]
    public async Task SPM_378_the_applications_project_managers_decide_a_review_that_the_creator_would_approve_alone()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var managerId = await AddProjectManagerAsync();
        var project = await host.CreateProjectAsync("PORTFOLIO", ownerId: host.OwnerId);

        // Before any proposal the application counts who else may approve under the portfolio policy: its
        // project manager, and the list is complete because the application named everyone its policy rule
        // admits.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>();
            var others = await directory.CountAsync(
                SampleApprovalPolicySelectionComposition.PortfolioPolicyId,
                ProjectSubjects.For(project),
                tenantId: null,
                excludeActorId: host.OwnerId.ToString(),
                cancellationToken: cancellation);
            Assert.Equal(new NhAssistantApproverCount(1, true), others.Data);
            // The candidates grant nothing: under the intake policy, without the rule, the creator is alone.
            var intake = await directory.CountAsync(
                SampleIntakeReviewerComposition.ApprovalPolicyId,
                ProjectSubjects.For(project),
                tenantId: null,
                excludeActorId: host.OwnerId.ToString(),
                cancellationToken: cancellation);
            Assert.Equal(new NhAssistantApproverCount(0, true), intake.Data);
        }

        // The creator approves the project alone, so the review waits for the project manager, four-eyes.
        var review = await AssistantApprovalPolicySelectionSamplesTests.ReviewAsync(host, owner, project);
        Assert.Equal(SampleApprovalPolicySelectionComposition.PortfolioPolicyId, review.PolicyId);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var listed = (await scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>()
                .ListForProposalAsync(review.ApprovalId, tenantId: null, cancellationToken: cancellation)).Data!;
            Assert.True(listed.Complete);
            var approver = Assert.Single(listed.Approvers);
            Assert.Equal((managerId.ToString("D"), NhAiSubjectRole.None, false), (approver.ActorId, approver.Role, approver.Initiator));
            Assert.Equal(ManagerEmail, approver.DisplayName);
        }

        // The creator is refused; the project manager approves.
        using var refused = await AssistantApprovalPolicySelectionSamplesTests.DecideAsync(owner, review);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(NhAssistantErrorCodes.FourEyesRequired, await refused.Content.ReadAsStringAsync(cancellation));
        using var manager = host.CreateClient(managerId.ToString(), admin: true);
        using var decided = await AssistantApprovalPolicySelectionSamplesTests.DecideAsync(manager, review);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
    }

    private const string ManagerEmail = "portfolio-office@sample.localhost";

    /// <summary>
    /// A project manager of the application: a person whose role grants <c>app.project.manage</c>, who
    /// may sign in and works in the host's division, but takes part in no project.
    /// </summary>
    private async Task<Guid> AddProjectManagerAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var roles = services.GetRequiredService<RoleManager<NhUserRole>>();
        var users = services.GetRequiredService<UserManager<NhUser>>();
        var role = new NhUserRole("portfolio-office");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        Assert.True((await roles.AddClaimAsync(role, new Claim(NhPlatformClaimTypes.Permission, SampleProjectStarterComposition.ProjectManagePolicy))).Succeeded);
        var manager = new NhUser
        {
            Email = ManagerEmail,
            UserName = ManagerEmail,
            EmailConfirmed = true,
            ActiveDivisionId = host.DivisionId
        };
        Assert.True((await users.CreateAsync(manager)).Succeeded);
        Assert.True((await users.AddToRoleAsync(manager, role.Name!)).Succeeded);
        var dbContext = services.GetRequiredService<SampleProjectManagementDbContext>();
        dbContext.Set<NhDivisionUser>().Add(new NhDivisionUser { Id = Guid.NewGuid(), UserId = manager.Id, DivisionId = host.DivisionId });
        await dbContext.SaveChangesAsync();
        return manager.Id;
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
